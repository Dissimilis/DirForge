using System.Net;
using System.Text;
using DirForge.Middleware;
using DirForge.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class BasicAuthMiddlewareUnitTests
{
    [TestMethod]
    public async Task MissingAuthorizationHeader_Returns401WithChallenge()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-MissingHeader");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "password-1";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.IsTrue(context.Response.Headers.TryGetValue("WWW-Authenticate", out var challenge));
        StringAssert.Contains(challenge.ToString(), "Basic realm=\"Directory Listing\"");
    }

    [TestMethod]
    public async Task InvalidCredentials_TriggersRateLimitOnSixthAttempt()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-RateLimit");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "correct-password";

        var middleware = CreateMiddleware(options, _ => Task.CompletedTask);

        for (var i = 1; i <= 5; i++)
        {
            var unauthorized = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
            unauthorized.Request.Headers.Authorization = BuildBasicAuth("alice", "wrong-password");

            await middleware.InvokeAsync(unauthorized);

            Assert.AreEqual(
                StatusCodes.Status401Unauthorized,
                unauthorized.Response.StatusCode,
                $"Attempt {i} should return 401.");
        }

        var throttled = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        throttled.Request.Headers.Authorization = BuildBasicAuth("alice", "wrong-password");
        await middleware.InvokeAsync(throttled);

        Assert.AreEqual(StatusCodes.Status429TooManyRequests, throttled.Response.StatusCode);
        Assert.IsTrue(throttled.Response.Headers.TryGetValue("Retry-After", out var retryAfter));
        Assert.AreEqual("60", retryAfter.ToString());
    }

    [TestMethod]
    public async Task ValidCredentials_InvokeNextDelegate()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-Valid");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "correct-password";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });

        var request = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        request.Request.Headers.Authorization = BuildBasicAuth("alice", "correct-password");

        await middleware.InvokeAsync(request);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(StatusCodes.Status204NoContent, request.Response.StatusCode);
    }

    [TestMethod]
    public async Task HealthEndpoint_BypassesGlobalAuth()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-HealthBypass");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "correct-password";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });

        var request = CreateContext(path: "/health", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(request);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(StatusCodes.Status200OK, request.Response.StatusCode);
    }

    [TestMethod]
    public async Task BearerToken_AuthorizationHeaderWithPrefix_GrantsAccess()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BearerPrefix");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Authorization = "Bearer my-secret-token";

        await middleware.InvokeAsync(context);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task BearerToken_AuthorizationHeaderWithoutPrefix_GrantsAccess()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BearerNoPrefix");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Authorization = "my-secret-token";

        await middleware.InvokeAsync(context);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task BearerToken_CustomHeader_GrantsAccess()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BearerCustomHeader");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";
        options.BearerTokenHeaderName = "X-API-Key";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        context.Request.Headers["X-API-Key"] = "my-secret-token";

        await middleware.InvokeAsync(context);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task BearerToken_InvalidToken_Returns401()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BearerInvalid");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Authorization = "Bearer wrong-token";

        await middleware.InvokeAsync(context);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.IsFalse(context.Response.Headers.ContainsKey("WWW-Authenticate"));
    }

    [TestMethod]
    public async Task BearerToken_RateLimitOnRepeatedFailures()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BearerRateLimit");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";

        var middleware = CreateMiddleware(options, _ => Task.CompletedTask);

        for (var i = 1; i <= 5; i++)
        {
            var unauthorized = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
            unauthorized.Request.Headers.Authorization = "Bearer wrong-token";

            await middleware.InvokeAsync(unauthorized);

            Assert.AreEqual(
                StatusCodes.Status401Unauthorized,
                unauthorized.Response.StatusCode,
                $"Attempt {i} should return 401.");
        }

        var throttled = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        throttled.Request.Headers.Authorization = "Bearer wrong-token";
        await middleware.InvokeAsync(throttled);

        Assert.AreEqual(StatusCodes.Status429TooManyRequests, throttled.Response.StatusCode);
        Assert.IsTrue(throttled.Response.Headers.TryGetValue("Retry-After", out var retryAfter));
        Assert.AreEqual("60", retryAfter.ToString());
    }

    [TestMethod]
    public async Task BasicAuth_StillWorksWhenBothConfigured()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BothBasicAuth");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "password-1";
        options.BearerToken = "my-secret-token";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Authorization = BuildBasicAuth("alice", "password-1");

        await middleware.InvokeAsync(context);

        Assert.IsTrue(nextCalled);
        Assert.AreEqual(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task MissingAllCredentials_Returns401WhenBothConfigured()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BothNoCredentials");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "password-1";
        options.BearerToken = "my-secret-token";

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task BearerToken_IgnoredWhenExternalAuthEnabled()
    {
        using var tempDir = new TestTempDirectory("AuthMiddleware-BearerExternal");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";
        options.ExternalAuthEnabled = true;
        options.ExternalAuthIdentityHeader = "X-Forwarded-User";
        options.ForwardedHeadersKnownProxies = [];

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        // Send bearer token but external auth has no identity header — should be rejected
        var context = CreateContext(path: "/", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Authorization = "Bearer my-secret-token";

        await middleware.InvokeAsync(context);

        Assert.IsFalse(nextCalled);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private static BasicAuthMiddleware CreateMiddleware(DirForge.Models.DirForgeOptions options, RequestDelegate next)
    {
        var shareLinkService = new ShareLinkService(options);
        var oneTimeShareStore = new OneTimeShareStore();
        var dashboardMetrics = new DashboardMetricsService();
        return new BasicAuthMiddleware(
            next,
            options,
            shareLinkService,
            oneTimeShareStore,
            dashboardMetrics,
            NullLogger<BasicAuthMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(string path, IPAddress remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = HttpMethods.Get;
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }

    private static string BuildBasicAuth(string user, string pass)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        return "Basic " + token;
    }
}
