using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DirForge.Security;

public sealed class DashboardBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DashboardBasic";
    public const string PolicyName = "DashboardPolicy";
    private const string ChallengeHeaderValue = "Basic realm=\"DirForge Dashboard\", charset=\"UTF-8\"";
    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private const int MaxFailedAuthAttempts = 5;

    private static readonly PartitionedRateLimiter<string> _authFailureLimiter =
        PartitionedRateLimiter.Create<string, string>(ip =>
            RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = MaxFailedAuthAttempts,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    public DashboardBasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var dirForgeOptions = Context.RequestServices.GetRequiredService<Models.DirForgeOptions>();
        if (!dirForgeOptions.DashboardAuthEnabled)
        {
            return Task.FromResult(AuthenticateResult.Success(CreateTicket("dashboard-open")));
        }

        if (!BasicAuthParser.TryReadCredentials(Request, out var usernameBytes, out var passwordBytes))
        {
            RecordDashboardAuthFailure();
            return Task.FromResult(AuthenticateResult.Fail("Missing or invalid dashboard credentials."));
        }

        byte[] expectedUserBytes = [];
        byte[] expectedPassBytes = [];
        try
        {
            expectedUserBytes = Utf8Strict.GetBytes(dirForgeOptions.DashboardAuthUser ?? string.Empty);
            expectedPassBytes = Utf8Strict.GetBytes(dirForgeOptions.DashboardAuthPass ?? string.Empty);

            var userMatches = CryptographicOperations.FixedTimeEquals(usernameBytes, expectedUserBytes);
            var passMatches = CryptographicOperations.FixedTimeEquals(passwordBytes, expectedPassBytes);
            if (!(userMatches & passMatches))
            {
                RecordDashboardAuthFailure();

                var clientIp = Context.Connection.RemoteIpAddress?.ToString();
                if (clientIp is not null)
                {
                    using var lease = _authFailureLimiter.AttemptAcquire(clientIp);
                    if (!lease.IsAcquired)
                    {
                        var dashboardMetrics = Context.RequestServices.GetService<Services.DashboardMetricsService>();
                        dashboardMetrics?.RecordRateLimitRejection();
                        Logger.LogWarning("[SECURITY] AUTH_LOCKOUT ClientIP={ClientIp} Path={RequestPath} Too many failed attempts",
                            clientIp, Request.Path.Value ?? "/");

                        Context.Items["DashboardAuthRateLimited"] = true;
                        return Task.FromResult(AuthenticateResult.Fail("Too many failed auth attempts."));
                    }
                }

                Logger.LogWarning("[SECURITY] AUTH_FAILURE ClientIP={ClientIp} Path={RequestPath} Invalid credentials",
                    clientIp ?? "unknown", Request.Path.Value ?? "/");

                return Task.FromResult(AuthenticateResult.Fail("Invalid dashboard credentials."));
            }

            var identityName = dirForgeOptions.DashboardAuthUser?.Trim();
            if (string.IsNullOrWhiteSpace(identityName))
            {
                identityName = "dashboard";
            }

            return Task.FromResult(AuthenticateResult.Success(CreateTicket(identityName)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(usernameBytes);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(expectedUserBytes);
            CryptographicOperations.ZeroMemory(expectedPassBytes);
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties? properties)
    {
        if (Context.Items.TryGetValue("DashboardAuthRateLimited", out var isRateLimited) && isRateLimited is true)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers[HeaderNames.RetryAfter] = "60";
            Response.Headers[HeaderNames.CacheControl] = "no-store";
            Response.Headers[HeaderNames.Pragma] = "no-cache";
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = ChallengeHeaderValue;
        Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Task.CompletedTask;
    }

    private AuthenticationTicket CreateTicket(string identityName)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, identityName),
            new Claim(ClaimTypes.NameIdentifier, identityName)
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: SchemeName));
        return new AuthenticationTicket(principal, SchemeName);
    }

    private void RecordDashboardAuthFailure()
    {
        var dashboardMetrics = Context.RequestServices.GetService<Services.DashboardMetricsService>();
        dashboardMetrics?.RecordDashboardAuthFailure();
    }
}
