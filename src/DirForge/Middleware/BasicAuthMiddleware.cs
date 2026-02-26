using System.Net;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using DirForge.Models;
using DirForge.Security;
using DirForge.Services;
using Microsoft.Net.Http.Headers;

namespace DirForge.Middleware;

public sealed class BasicAuthMiddleware
{

    private const int MaxFailedAuthAttempts = 5;
    private const string OneTimeShareSessionCookieName = "df_share_once";
    public const string OriginalRemoteIpItemKey = "DirForge.OriginalRemoteIp";
    private static readonly string ChallengeHeaderValue = "Basic realm=\"Directory Listing\", charset=\"UTF-8\"";

    private readonly RequestDelegate _next;
    private readonly ILogger<BasicAuthMiddleware> _logger;
    private readonly ShareLinkService _shareLinkService;
    private readonly OneTimeShareStore _oneTimeShareStore;
    private readonly DashboardMetricsService _dashboardMetrics;
    private readonly bool _enabled;
    private readonly bool _sharingEnabled;
    private readonly bool _dashboardEnabled;
    private readonly bool _metricsEndpointEnabled;
    private readonly bool _dashboardAuthEnabled;
    private readonly bool _externalAuthEnabled;
    private readonly string _externalAuthIdentityHeader;
    private readonly bool _enableWebDav;
    private readonly bool _enableS3Endpoint;
    private readonly bool _bearerTokenEnabled;
    private readonly string _bearerTokenHeaderName = string.Empty;
    private readonly byte[] _expectedBearerTokenBytes = [];
    private readonly IPAddress[] _trustedProxyAddresses;
    private readonly byte[] _expectedUserBytes = [];
    private readonly byte[] _expectedPassBytes = [];

    private readonly PartitionedRateLimiter<string> _authFailureLimiter =
        PartitionedRateLimiter.Create<string, string>(ip =>
            RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = MaxFailedAuthAttempts,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    public BasicAuthMiddleware(
        RequestDelegate next,
        DirForgeOptions options,
        ShareLinkService shareLinkService,
        OneTimeShareStore oneTimeShareStore,
        DashboardMetricsService dashboardMetrics,
        ILogger<BasicAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _shareLinkService = shareLinkService;
        _oneTimeShareStore = oneTimeShareStore;
        _dashboardMetrics = dashboardMetrics;
        _sharingEnabled = options.EnableSharing;
        _dashboardEnabled = options.DashboardEnabled;
        _metricsEndpointEnabled = options.EnableMetricsEndpoint;
        _dashboardAuthEnabled = options.DashboardAuthEnabled;
        _externalAuthEnabled = options.ExternalAuthEnabled;
        _externalAuthIdentityHeader = options.ExternalAuthIdentityHeader;
        _enableWebDav = options.EnableWebDav;
        _enableS3Endpoint = options.EnableS3Endpoint;
        _trustedProxyAddresses = options.ForwardedHeadersKnownProxies
            .Select(IPAddress.Parse)
            .ToArray();

        var hasUser = !string.IsNullOrWhiteSpace(options.BasicAuthUser);
        var hasPass = !string.IsNullOrWhiteSpace(options.BasicAuthPass);

        var hasBearerToken = options.BearerTokenEnabled;

        if (_externalAuthEnabled)
        {
            _enabled = false;
            _bearerTokenEnabled = false;
            if (hasUser || hasPass)
            {
                _logger.LogInformation("External auth enabled; BasicAuthUser/BasicAuthPass are ignored.");
            }

            if (hasBearerToken)
            {
                _logger.LogInformation("External auth enabled; BearerToken is ignored.");
            }

            return;
        }

        if (hasBearerToken)
        {
            _bearerTokenEnabled = true;
            _bearerTokenHeaderName = options.BearerTokenHeaderName;
            _expectedBearerTokenBytes = BasicAuthParser.Utf8Strict.GetBytes(options.BearerToken!);
        }

        if (hasUser && hasPass)
        {
            _enabled = true;
            _expectedUserBytes = BasicAuthParser.Utf8Strict.GetBytes(options.BasicAuthUser!);
            _expectedPassBytes = BasicAuthParser.Utf8Strict.GetBytes(options.BasicAuthPass!);
            return;
        }

        _enabled = false;
        if (!hasUser && !hasPass)
        {
            if (!_bearerTokenEnabled)
            {
                _logger.LogInformation("Basic Auth disabled: BasicAuthUser and BasicAuthPass are not set.");
            }

            return;
        }

        if (!hasUser)
        {
            _logger.LogWarning("Basic Auth disabled: BasicAuthUser is not set while BasicAuthPass is set.");
            return;
        }

        _logger.LogWarning("Basic Auth disabled: BasicAuthPass is not set while BasicAuthUser is set.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (ShouldBypassGlobalAuth(path))
        {
            await _next(context);
            return;
        }

        if (_enableWebDav &&
            HttpMethods.IsOptions(context.Request.Method) &&
            IsWebDavPath(path))
        {
            await _next(context);
            return;
        }

        var isWebDavRequest = _enableWebDav && IsWebDavPath(path);

        var isDashboardRequest = path.Equals(DashboardRouteHelper.DashboardPath, StringComparison.OrdinalIgnoreCase) ||
                                 path.Equals(DashboardRouteHelper.MetricsPath, StringComparison.OrdinalIgnoreCase);
        if (isDashboardRequest && _dashboardAuthEnabled)
        {
            await _next(context);
            return;
        }

        if (!isDashboardRequest &&
            _sharingEnabled &&
            context.Request.Query.ContainsKey(ShareLinkService.TokenQueryParameter))
        {
            var nowUtc = DateTimeOffset.UtcNow;
            if (!TryReadShareToken(context.Request, out var shareToken) ||
                !_shareLinkService.TryValidateToken(shareToken, nowUtc, out var shareContext, out _) ||
                shareContext is null ||
                !_shareLinkService.IsRequestAllowed(context.Request, shareContext))
            {
                _dashboardMetrics.RecordShareTokenRejected();
                _dashboardMetrics.RecordShareUnauthorized();
                _logger.LogWarning("[SECURITY] SHARE_TOKEN_REJECTED ClientIP={ClientIp} Path={RequestPath}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    context.Request.Path.Value ?? "/");
                UnauthorizedWithoutChallenge(context.Response);
                return;
            }

            if (shareContext.IsOneTime)
            {
                if (string.IsNullOrWhiteSpace(shareContext.Nonce) ||
                    !_oneTimeShareStore.TryConsumeNonce(shareContext.Nonce, shareContext.ExpiresAtUnix, nowUtc))
                {
                    _dashboardMetrics.RecordShareTokenRejected();
                    _dashboardMetrics.RecordShareUnauthorized();
                    _logger.LogWarning("[SECURITY] SHARE_TOKEN_REPLAYED ClientIP={ClientIp} Path={RequestPath}",
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        context.Request.Path.Value ?? "/");
                    UnauthorizedWithoutChallenge(context.Response);
                    return;
                }

                _dashboardMetrics.RecordShareOneTimeConsumed();

                if (shareContext.Mode == ShareMode.Directory)
                {
                    var sessionId = _oneTimeShareStore.CreateSession(shareContext, nowUtc);
                    SetShareSessionCookie(context.Response, sessionId, shareContext.ExpiresAtUnix, context.Request.IsHttps);

                    if (ShouldRedirectToCleanShareUrl(context.Request))
                    {
                        context.Response.Redirect(BuildCleanShareUrl(context.Request), permanent: false);
                        return;
                    }

                    shareContext = shareContext with { Token = string.Empty };
                }
            }

            context.Items[ShareLinkService.HttpContextItemKey] = shareContext;
            await _next(context);
            return;
        }

        if (!isDashboardRequest &&
            _sharingEnabled &&
            TryReadShareSessionId(context.Request, out var shareSessionId))
        {
            if (_oneTimeShareStore.TryGetSessionContext(shareSessionId, DateTimeOffset.UtcNow, out var sessionContext) &&
                sessionContext is not null &&
                _shareLinkService.IsRequestAllowed(context.Request, sessionContext))
            {
                context.Items[ShareLinkService.HttpContextItemKey] = sessionContext;
                await _next(context);
                return;
            }

            ClearShareSessionCookie(context.Response, context.Request.IsHttps);
        }

        if (_externalAuthEnabled)
        {
            if (!TryReadExternalIdentity(context, out var externalIdentity))
            {
                _dashboardMetrics.RecordAuthFailure();
                _logger.LogWarning("[SECURITY] EXTERNAL_AUTH_REJECTED ClientIP={ClientIp} Path={RequestPath}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    context.Request.Path.Value ?? "/");
                UnauthorizedWithoutChallenge(context.Response);
                return;
            }

            context.User = BuildExternalPrincipal(externalIdentity);
            await _next(context);
            return;
        }

        if (!_enabled && !_bearerTokenEnabled)
        {
            await _next(context);
            return;
        }

        if (_bearerTokenEnabled && TryReadBearerToken(context.Request, out var bearerTokenBytes))
        {
            var clientIpBearer = context.Connection.RemoteIpAddress?.ToString();
            try
            {
                if (CryptographicOperations.FixedTimeEquals(bearerTokenBytes, _expectedBearerTokenBytes))
                {
                    await _next(context);
                    return;
                }

                _dashboardMetrics.RecordAuthFailure();
                if (clientIpBearer is not null)
                {
                    using var lease = _authFailureLimiter.AttemptAcquire(clientIpBearer);
                    if (!lease.IsAcquired)
                    {
                        _dashboardMetrics.RecordRateLimitRejection();
                        _logger.LogWarning("[SECURITY] AUTH_LOCKOUT ClientIP={ClientIp} Path={RequestPath} Too many failed attempts",
                            clientIpBearer, context.Request.Path.Value ?? "/");
                        TooManyRequests(context.Response);
                        return;
                    }
                }

                _logger.LogWarning("[SECURITY] AUTH_FAILURE ClientIP={ClientIp} Path={RequestPath} Invalid bearer token",
                    clientIpBearer ?? "unknown", context.Request.Path.Value ?? "/");
                UnauthorizedWithoutChallenge(context.Response);
                return;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bearerTokenBytes);
            }
        }

        if (_enabled)
        {
            if (!BasicAuthParser.TryReadCredentials(context.Request, out var usernameBytes, out var passwordBytes))
            {
                _dashboardMetrics.RecordAuthFailure();
                Challenge(context.Response, isWebDavRequest);
                return;
            }

            var clientIp = context.Connection.RemoteIpAddress?.ToString();

            try
            {
                if (!BasicAuthParser.ValidateCredentials(usernameBytes, passwordBytes, _expectedUserBytes, _expectedPassBytes))
                {
                    _dashboardMetrics.RecordAuthFailure();
                    if (clientIp is not null)
                    {
                        using var lease = _authFailureLimiter.AttemptAcquire(clientIp);
                        if (!lease.IsAcquired)
                        {
                            _dashboardMetrics.RecordRateLimitRejection();
                            _logger.LogWarning("[SECURITY] AUTH_LOCKOUT ClientIP={ClientIp} Path={RequestPath} Too many failed attempts",
                                clientIp, context.Request.Path.Value ?? "/");
                            TooManyRequests(context.Response);
                            return;
                        }
                    }

                    _logger.LogWarning("[SECURITY] AUTH_FAILURE ClientIP={ClientIp} Path={RequestPath} Invalid credentials",
                        clientIp ?? "unknown", context.Request.Path.Value ?? "/");
                    Challenge(context.Response, isWebDavRequest);
                    return;
                }

                await _next(context);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(usernameBytes);
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            return;
        }

        // Only bearer configured but no bearer token sent — reject without Basic challenge
        _dashboardMetrics.RecordAuthFailure();
        UnauthorizedWithoutChallenge(context.Response);
    }

    private bool ShouldBypassGlobalAuth(PathString path)
    {
        if (StaticAssetRouteHelper.IsStaticRequest(path))
        {
            return true;
        }

        // Health endpoints always bypass (for orchestrators)
        if (path.Equals(OperationalRouteHelper.HealthPath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(OperationalRouteHelper.HealthzPath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(OperationalRouteHelper.ReadyzPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // /api/stats bypasses global auth only when dashboard has its own auth
        if (path.Equals(DashboardRouteHelper.ApiStatsPath, StringComparison.OrdinalIgnoreCase) && _dashboardAuthEnabled)
        {
            return true;
        }

        if (path.Equals(DashboardRouteHelper.DashboardPath, StringComparison.OrdinalIgnoreCase) && !_dashboardEnabled)
        {
            return true;
        }

        if (path.Equals(DashboardRouteHelper.MetricsPath, StringComparison.OrdinalIgnoreCase) &&
            (!_dashboardEnabled || !_metricsEndpointEnabled))
        {
            return true;
        }

        // S3 handles its own auth via SigV4 — BasicAuth challenge must not interfere
        if (_enableS3Endpoint && path.StartsWithSegments("/s3", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsWebDavPath(PathString path)
    {
        return path.StartsWithSegments("/webdav", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryReadExternalIdentity(HttpContext context, out string identity)
    {
        identity = string.Empty;

        var sourceRemoteIp = context.Items.TryGetValue(OriginalRemoteIpItemKey, out var originalRemoteIpValue)
            ? originalRemoteIpValue as IPAddress
            : context.Connection.RemoteIpAddress;
        if (!IsTrustedProxy(sourceRemoteIp))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue(_externalAuthIdentityHeader, out var values) ||
            values.Count != 1)
        {
            return false;
        }

        identity = values[0]?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(identity);
    }

    private bool IsTrustedProxy(IPAddress? remoteIpAddress)
    {
        return TrustedProxyDefaults.IsTrusted(remoteIpAddress, _trustedProxyAddresses);
    }

    private static ClaimsPrincipal BuildExternalPrincipal(string identity)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, identity),
            new Claim(ClaimTypes.NameIdentifier, identity)
        };
        var claimsIdentity = new ClaimsIdentity(claims, authenticationType: "ReverseProxyHeader");
        return new ClaimsPrincipal(claimsIdentity);
    }



    private static void Challenge(HttpResponse response, bool includeDav = false)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers[HeaderNames.WWWAuthenticate] = ChallengeHeaderValue;
        response.Headers[HeaderNames.CacheControl] = "no-store";
        response.Headers[HeaderNames.Pragma] = "no-cache";
        if (includeDav)
        {
            response.Headers["DAV"] = "1";
        }
    }

    private static bool TryReadShareToken(HttpRequest request, out string token)
    {
        token = string.Empty;

        if (!request.Query.TryGetValue(ShareLinkService.TokenQueryParameter, out var values) || values.Count != 1)
        {
            return false;
        }

        token = values[0] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(token);
    }

    private static bool TryReadShareSessionId(HttpRequest request, out string sessionId)
    {
        sessionId = string.Empty;
        if (!request.Cookies.TryGetValue(OneTimeShareSessionCookieName, out var rawSessionId) ||
            string.IsNullOrWhiteSpace(rawSessionId))
        {
            return false;
        }

        sessionId = rawSessionId.Trim();
        return sessionId.Length > 0;
    }

    private bool TryReadBearerToken(HttpRequest request, out byte[] tokenBytes)
    {
        tokenBytes = [];

        if (!request.Headers.TryGetValue(_bearerTokenHeaderName, out var values) || values.Count != 1)
        {
            return false;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 8192)
        {
            return false;
        }

        string tokenValue;

        if (string.Equals(_bearerTokenHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = raw.AsSpan().Trim();

            // Let Basic Auth handle its own scheme
            if (trimmed.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Strip "Bearer " prefix if present
            tokenValue = trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? trimmed[7..].Trim().ToString()
                : trimmed.ToString();
        }
        else
        {
            tokenValue = raw.Trim();
        }

        if (string.IsNullOrWhiteSpace(tokenValue))
        {
            return false;
        }

        try
        {
            tokenBytes = BasicAuthParser.Utf8Strict.GetBytes(tokenValue);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static void SetShareSessionCookie(HttpResponse response, string sessionId, long expiresAtUnix, bool secure)
    {
        response.Cookies.Append(OneTimeShareSessionCookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix)
        });
    }

    private static void ClearShareSessionCookie(HttpResponse response, bool secure)
    {
        response.Cookies.Delete(OneTimeShareSessionCookieName, new CookieOptions
        {
            Secure = secure,
            SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
            Path = "/"
        });
    }

    private static bool ShouldRedirectToCleanShareUrl(HttpRequest request)
    {
        return HttpMethods.IsGet(request.Method) &&
               string.IsNullOrWhiteSpace(request.Query["handler"]);
    }

    private static string BuildCleanShareUrl(HttpRequest request)
    {
        var cleanedQuery = request.Query
            .Where(kvp => !kvp.Key.Equals(ShareLinkService.TokenQueryParameter, StringComparison.OrdinalIgnoreCase))
            .SelectMany(kvp =>
                kvp.Value.Select(value => new KeyValuePair<string, string?>(kvp.Key, value)));
        var queryString = QueryString.Create(cleanedQuery);
        var path = $"{request.PathBase}{request.Path}";
        return string.IsNullOrEmpty(path) ? "/" : path + queryString;
    }

    private static void TooManyRequests(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers[HeaderNames.RetryAfter] = "60";
        response.Headers[HeaderNames.CacheControl] = "no-store";
        response.Headers[HeaderNames.Pragma] = "no-cache";
    }

    private static void UnauthorizedWithoutChallenge(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.Remove(HeaderNames.WWWAuthenticate);
        response.Headers[HeaderNames.CacheControl] = "no-store";
        response.Headers[HeaderNames.Pragma] = "no-cache";
    }
}
