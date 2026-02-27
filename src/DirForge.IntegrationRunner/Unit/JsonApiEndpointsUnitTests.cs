using System.Text.Json;
using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class JsonApiEndpointsUnitTests
{
    private static async Task<(int StatusCode, string Body)> InvokeApiAsync(
        DirForgeOptions options,
        string path,
        string method = "GET",
        int nextStatusCode = StatusCodes.Status404NotFound,
        string nextBody = "")
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestServiceFactory.CreateDirectoryListingService(options));
        services.AddSingleton(new ShareLinkService(options));
        var serviceProvider = services.BuildServiceProvider();

        var appBuilder = new ApplicationBuilder(serviceProvider);
        appBuilder.MapJsonApiEndpoints(options);
        appBuilder.Run(async ctx =>
        {
            ctx.Response.StatusCode = nextStatusCode;
            if (!string.IsNullOrEmpty(nextBody))
            {
                await ctx.Response.WriteAsync(nextBody);
            }
        });
        var pipeline = appBuilder.Build();

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.RequestServices = serviceProvider;
        context.Response.Body = new MemoryStream();

        await pipeline(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [TestMethod]
    public async Task ApiRoot_ReturnsDiscoveryDocument()
    {
        using var tempDir = new TestTempDirectory("JsonApi-Root");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableJsonApi = true;

        var (status, body) = await InvokeApiAsync(options, "/api");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.IsTrue(root.TryGetProperty("_links", out var links));
        Assert.IsTrue(links.TryGetProperty("self", out _));
        Assert.IsTrue(links.TryGetProperty("browse", out _));
        Assert.AreEqual("DirForge", root.GetProperty("server").GetString());
        Assert.AreEqual("1.0", root.GetProperty("apiVersion").GetString());
    }

    [TestMethod]
    public async Task UnknownSubPath_Returns404()
    {
        using var tempDir = new TestTempDirectory("JsonApi-404");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableJsonApi = true;

        var (status, body) = await InvokeApiAsync(options, "/api/nonexistent");

        Assert.AreEqual(404, status);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        Assert.AreEqual("NotFound", error.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task NonGetPostMethod_Returns405()
    {
        using var tempDir = new TestTempDirectory("JsonApi-405");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableJsonApi = true;

        var (status, _) = await InvokeApiAsync(options, "/api/browse", method: "PUT");

        Assert.AreEqual(405, status);
    }

    [TestMethod]
    public async Task ApiStatsPath_BypassesJsonApiMiddleware()
    {
        using var tempDir = new TestTempDirectory("JsonApi-StatsBypass");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableJsonApi = true;

        var (status, body) = await InvokeApiAsync(
            options,
            "/api/stats",
            nextStatusCode: StatusCodes.Status418ImATeapot,
            nextBody: "passthrough");

        Assert.AreEqual(StatusCodes.Status418ImATeapot, status);
        Assert.AreEqual("passthrough", body);
    }
}
