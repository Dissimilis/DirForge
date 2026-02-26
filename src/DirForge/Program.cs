using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using DirForge.Middleware;
using DirForge.Models;
using DirForge.Security;
using DirForge.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.WebHost.UseKestrelCore();
builder.WebHost.ConfigureKestrel(o => o.AllowSynchronousIO = true);
builder.WebHost.UseStaticWebAssets();

var startupOptions = DirForgeOptionsResolver.Resolve(builder.Configuration);
var startupValidation = new DirForgeOptionsValidator().Validate(Options.DefaultName, startupOptions);
if (startupValidation.Failed)
{
    throw new OptionsValidationException(nameof(DirForgeOptions), typeof(DirForgeOptions), startupValidation.Failures);
}

var dashboardAuthConfigured = startupOptions.DashboardAuthEnabled;
var dashboardEnabledConfigured = startupOptions.DashboardEnabled;
var parsedListenIp = IPAddress.Parse(startupOptions.ListenIp);
var formattedListenHost = parsedListenIp.AddressFamily == AddressFamily.InterNetworkV6
    ? $"[{parsedListenIp}]"
    : parsedListenIp.ToString();
builder.WebHost.UseUrls($"http://{formattedListenHost}:{startupOptions.Port}");

builder.Services.AddSingleton(startupOptions);
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, DashboardBasicAuthenticationHandler>(
        DashboardBasicAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        DashboardBasicAuthenticationHandler.PolicyName,
        policy =>
        {
            policy.AddAuthenticationSchemes(DashboardBasicAuthenticationHandler.SchemeName);
            policy.RequireAuthenticatedUser();
        });
});

builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<DirForgeOptions>((forwardedHeadersOptions, options) =>
    {
        forwardedHeadersOptions.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;
        forwardedHeadersOptions.ForwardLimit = options.ForwardedHeadersForwardLimit;

        if (options.ForwardedHeadersKnownProxies.Length > 0)
        {
            forwardedHeadersOptions.KnownIPNetworks.Clear();
            forwardedHeadersOptions.KnownProxies.Clear();

            foreach (var knownProxy in options.ForwardedHeadersKnownProxies)
            {
                forwardedHeadersOptions.KnownProxies.Add(IPAddress.Parse(knownProxy));
            }
        }
        else
        {
            TrustedProxyDefaults.AddKnownNetworks(forwardedHeadersOptions);
        }
    });

builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiterOptions.OnRejected = (ctx, _) =>
    {
        var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DirForge.RateLimiter");
        var dashboardMetrics = ctx.HttpContext.RequestServices.GetRequiredService<DashboardMetricsService>();
        dashboardMetrics.RecordRateLimitRejection();
        logger.LogWarning("[SECURITY] RATE_LIMIT ClientIP={ClientIp} Path={RequestPath} Rate limit exceeded",
            ctx.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            ctx.HttpContext.Request.Path.Value ?? "/");
        return ValueTask.CompletedTask;
    };
    static FixedWindowRateLimiterOptions CreateFixedWindowOptions(int permitLimit) => new()
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        AutoReplenishment = true
    };

    rateLimiterOptions.GlobalLimiter = PartitionedRateLimiter.CreateChained(
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var opts = context.RequestServices.GetRequiredService<DirForgeOptions>();
            var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey,
                _ => CreateFixedWindowOptions(opts.RateLimitPerIp));
        }),
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var opts = context.RequestServices.GetRequiredService<DirForgeOptions>();
            return RateLimitPartition.GetFixedWindowLimiter(
                "global",
                _ => CreateFixedWindowOptions(opts.RateLimitGlobal));
        }));
});

var inMemoryLogStore = new InMemoryLogStore(capacity: 500);
builder.Logging.AddProvider(new InMemoryLoggerProvider(inMemoryLogStore));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton(inMemoryLogStore);
builder.Services.AddSingleton<DashboardMetricsService>();
builder.Services.AddSingleton<IconResolver>();
builder.Services.AddSingleton<DirectoryListingService>();
builder.Services.AddSingleton<ArchiveBrowseService>();
builder.Services.AddSingleton<ShareLinkService>();
builder.Services.AddSingleton<OneTimeShareStore>();
builder.Services.AddRazorPages(options =>
{
    if (dashboardAuthConfigured && dashboardEnabledConfigured)
    {
        options.Conventions.AuthorizePage("/Dashboard", DashboardBasicAuthenticationHandler.PolicyName);
    }
});
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<RootPathReadableHealthCheck>("root_path_readable", tags: ["ready"]);

var app = builder.Build();
StaticAssetRouteHelper.Initialize(app.Logger);
var options = app.Services.GetRequiredService<DirForgeOptions>();
var dashboardMetrics = app.Services.GetRequiredService<DashboardMetricsService>();

if (!string.IsNullOrWhiteSpace(options.ShareSecretWarning))
{
    app.Logger.LogWarning("[SECURITY] {ShareSecretWarning}", options.ShareSecretWarning);
}

if (options.ExternalAuthEnabled && options.ForwardedHeadersKnownProxies.Length == 0)
{
    app.Logger.LogWarning(
        "[SECURITY] External auth is enabled but no trusted proxies are configured " +
        "(ForwardedHeadersKnownProxies is empty). Any device on the local network " +
        "can spoof the {Header} header. Configure explicit proxy IPs for production use.",
        options.ExternalAuthIdentityHeader);
}

var dashboardAuthState = options.DashboardAuthEnabled
    ? "dashboard auth enabled"
    : "no dashboard auth configured";

if (options.DashboardEnabled)
{
    app.Logger.LogInformation(
        "Dashboard endpoint enabled at {DashboardPath} ({DashboardAuthState}).",
        DashboardRouteHelper.DashboardPath,
        dashboardAuthState);

    if (options.EnableMetricsEndpoint)
    {
        app.Logger.LogInformation(
            "Metrics endpoint enabled at {MetricsPath} ({DashboardAuthState}).",
            DashboardRouteHelper.MetricsPath,
            dashboardAuthState);
    }
}

app.Logger.LogInformation(
    "API stats endpoint enabled at {ApiStatsPath} ({DashboardAuthState}).",
    DashboardRouteHelper.ApiStatsPath,
    dashboardAuthState);

if (options.EnableWebDav)
{
    app.Logger.LogInformation("WebDAV endpoint enabled at {WebDavPath}.", "/webdav/");
}

if (options.EnableS3Endpoint)
{
    app.Logger.LogInformation("S3-compatible endpoint enabled at /s3/ (bucket: {BucketName}, region: {Region}).",
        options.S3BucketName, options.S3Region);
}

if (options.EnableJsonApi)
{
    app.Logger.LogInformation("JSON API endpoint enabled at /api/.");
}

if (options.EnableMcpEndpoint)
{
    app.Logger.LogInformation("MCP endpoint enabled at /mcp.");
}

if (options.BearerTokenEnabled)
{
    app.Logger.LogInformation("Bearer token auth enabled (header: {HeaderName}).", options.BearerTokenHeaderName);
}

app.Use((context, next) =>
{
    context.Items[BasicAuthMiddleware.OriginalRemoteIpItemKey] = context.Connection.RemoteIpAddress;
    return next();
});

if (options.ForwardedHeadersEnabled)
{
    app.UseForwardedHeaders();
}

app.Use((context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    return next();
});

app.UseStatusCodePagesWithReExecute("/Error/{0}");
app.UseExceptionHandler("/Error");

app.Use(async (context, next) =>
{
    var startTimestamp = dashboardMetrics.BeginRequest();
    try
    {
        await next();
        dashboardMetrics.EndRequest(context, startTimestamp);
    }
    catch (Exception ex)
    {
        dashboardMetrics.EndRequest(context, startTimestamp, ex);
        throw;
    }
});

if (options.EnableDefaultRateLimiter)
{
    app.UseRateLimiter();
}

app.UseMiddleware<BasicAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapWebDavEndpoints(options);
app.MapS3Endpoints(options);
app.MapJsonApiEndpoints(options);
app.MapMcpEndpoints(options);

app.MapRazorPages();
app.MapOperationalEndpoints(options);

app.Run();
