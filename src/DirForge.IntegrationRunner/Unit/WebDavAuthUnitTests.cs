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
public sealed class WebDavAuthUnitTests
{
    [TestMethod]
    public async Task Options_WebDavPath_BypassesAuth_WhenWebDavEnabled()
    {
        using var tempDir = new TestTempDirectory("WebDav-OptionsBypass");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "password-1";
        options.EnableWebDav = true;

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/webdav/", method: HttpMethods.Options, remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.IsTrue(nextCalled, "OPTIONS on /webdav should bypass auth.");
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task Propfind_WebDavPath_RequiresAuth()
    {
        using var tempDir = new TestTempDirectory("WebDav-PropfindAuth");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "password-1";
        options.EnableWebDav = true;

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/webdav/", method: "PROPFIND", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.IsFalse(nextCalled, "PROPFIND without credentials should not pass through.");
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task ValidCredentials_WebDavPath_PassesThrough()
    {
        using var tempDir = new TestTempDirectory("WebDav-ValidCreds");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BasicAuthUser = "alice";
        options.BasicAuthPass = "correct-password";
        options.EnableWebDav = true;

        var nextCalled = false;
        var middleware = CreateMiddleware(
            options,
            context =>
            {
                nextCalled = true;
                context.Response.StatusCode = StatusCodes.Status207MultiStatus;
                return Task.CompletedTask;
            });

        var context = CreateContext(path: "/webdav/", method: "PROPFIND", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Authorization = BuildBasicAuth("alice", "correct-password");

        await middleware.InvokeAsync(context);

        Assert.IsTrue(nextCalled, "Valid credentials on /webdav should pass through.");
        Assert.AreEqual(StatusCodes.Status207MultiStatus, context.Response.StatusCode);
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

    private static DefaultHttpContext CreateContext(string path, string method, IPAddress remoteIp)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = remoteIp;
        return context;
    }

    private static string BuildBasicAuth(string user, string pass)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        return "Basic " + token;
    }
}
