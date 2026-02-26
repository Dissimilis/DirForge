using System.Text;
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
public sealed class McpEndpointsUnitTests
{
    private static async Task<(int StatusCode, string Body)> InvokeMcpAsync(
        DirForgeOptions options, string? jsonBody = null, string method = "POST")
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestServiceFactory.CreateDirectoryListingService(options));
        services.AddSingleton(new ShareLinkService(options));
        var serviceProvider = services.BuildServiceProvider();

        var appBuilder = new ApplicationBuilder(serviceProvider);
        appBuilder.MapMcpEndpoints(options);
        appBuilder.Run(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        var pipeline = appBuilder.Build();

        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Method = method;
        context.RequestServices = serviceProvider;
        context.Response.Body = new MemoryStream();

        if (jsonBody != null)
        {
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
        }

        await pipeline(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [TestMethod]
    public async Task Initialize_ReturnsServerInfo()
    {
        using var tempDir = new TestTempDirectory("Mcp-Init");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options, """{"method":"initialize","id":1}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString());
        var result = root.GetProperty("result");
        Assert.IsTrue(result.TryGetProperty("protocolVersion", out _));
        Assert.AreEqual("dirforge", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task Ping_ReturnsEmptyResult()
    {
        using var tempDir = new TestTempDirectory("Mcp-Ping");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options, """{"method":"ping","id":2}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.AreEqual(JsonValueKind.Object, root.GetProperty("result").ValueKind);
    }

    [TestMethod]
    public async Task NonPostMethod_Returns405()
    {
        using var tempDir = new TestTempDirectory("Mcp-405");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, _) = await InvokeMcpAsync(options, method: "GET");

        Assert.AreEqual(405, status);
    }

    [TestMethod]
    public async Task Initialize_AdvertisesPromptsCapability()
    {
        using var tempDir = new TestTempDirectory("Mcp-InitPrompts");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options, """{"method":"initialize","id":1}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var capabilities = doc.RootElement.GetProperty("result").GetProperty("capabilities");
        Assert.IsTrue(capabilities.TryGetProperty("prompts", out _), "capabilities should include prompts");
        Assert.IsTrue(capabilities.TryGetProperty("tools", out _), "capabilities should include tools");
        Assert.IsTrue(capabilities.TryGetProperty("resources", out _), "capabilities should include resources");
    }

    [TestMethod]
    public async Task PromptsList_ReturnsThreePrompts()
    {
        using var tempDir = new TestTempDirectory("Mcp-PromptsList");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options, """{"method":"prompts/list","id":10}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var prompts = doc.RootElement.GetProperty("result").GetProperty("prompts");
        Assert.AreEqual(JsonValueKind.Array, prompts.ValueKind);
        Assert.AreEqual(3, prompts.GetArrayLength());

        var names = new HashSet<string>();
        foreach (var p in prompts.EnumerateArray())
            names.Add(p.GetProperty("name").GetString()!);

        Assert.IsTrue(names.Contains("storage_report"));
        Assert.IsTrue(names.Contains("cleanup_candidates"));
        Assert.IsTrue(names.Contains("organize_suggestions"));
    }

    [TestMethod]
    public async Task PromptsGet_StorageReport_ReturnsMessages()
    {
        using var tempDir = new TestTempDirectory("Mcp-PromptStorageReport");
        File.WriteAllText(Path.Combine(tempDir.Path, "data.csv"), "col1,col2\n1,2\n");
        File.WriteAllText(Path.Combine(tempDir.Path, "notes.txt"), "hello world");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options,
            """{"method":"prompts/get","id":11,"params":{"name":"storage_report","arguments":{}}}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var result = doc.RootElement.GetProperty("result");
        var messages = result.GetProperty("messages");
        Assert.AreEqual(1, messages.GetArrayLength());
        var text = messages[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.IsTrue(text.Contains("Storage Report"), "Should contain report header");
        Assert.IsTrue(text.Contains("Total files"), "Should contain file count");
    }

    [TestMethod]
    public async Task PromptsGet_CleanupCandidates_ReturnsMessages()
    {
        using var tempDir = new TestTempDirectory("Mcp-PromptCleanup");
        File.WriteAllText(Path.Combine(tempDir.Path, "keep.txt"), "important data");
        File.WriteAllText(Path.Combine(tempDir.Path, "junk.tmp"), "temp data");
        File.WriteAllText(Path.Combine(tempDir.Path, "empty.txt"), ""); // 0 byte file
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options,
            """{"method":"prompts/get","id":12,"params":{"name":"cleanup_candidates","arguments":{}}}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("result")
            .GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.IsTrue(text.Contains("Cleanup Candidates"), "Should contain cleanup header");
        Assert.IsTrue(text.Contains("empty.txt"), "Should identify empty file");
        Assert.IsTrue(text.Contains("junk.tmp"), "Should identify temp file");
    }

    [TestMethod]
    public async Task PromptsGet_OrganizeSuggestions_ReturnsMessages()
    {
        using var tempDir = new TestTempDirectory("Mcp-PromptOrganize");
        File.WriteAllText(Path.Combine(tempDir.Path, "photo_001.jpg"), "fake");
        File.WriteAllText(Path.Combine(tempDir.Path, "photo_002.jpg"), "fake");
        File.WriteAllText(Path.Combine(tempDir.Path, "photo_003.jpg"), "fake");
        File.WriteAllText(Path.Combine(tempDir.Path, "report.pdf"), "fake");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options,
            """{"method":"prompts/get","id":13,"params":{"name":"organize_suggestions","arguments":{}}}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("result")
            .GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.IsTrue(text.Contains("Directory Structure Analysis"), "Should contain analysis header");
        Assert.IsTrue(text.Contains("File Types"), "Should contain type breakdown");
    }

    [TestMethod]
    public async Task PromptsGet_UnknownPrompt_ReturnsError()
    {
        using var tempDir = new TestTempDirectory("Mcp-PromptUnknown");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options,
            """{"method":"prompts/get","id":14,"params":{"name":"nonexistent"}}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out var error));
        Assert.AreEqual(-32602, error.GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task DiffSnapshots_FirstCall_ReturnsToken()
    {
        using var tempDir = new TestTempDirectory("Mcp-DiffFirst");
        File.WriteAllText(Path.Combine(tempDir.Path, "file1.txt"), "hello");
        File.WriteAllText(Path.Combine(tempDir.Path, "file2.txt"), "world");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options,
            """{"method":"tools/call","id":20,"params":{"name":"diff_snapshots","arguments":{}}}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.IsTrue(text.Contains("Snapshot captured"), "Should confirm snapshot capture");
        Assert.IsTrue(text.Contains("Files indexed: 2"), "Should count 2 files");
        Assert.IsTrue(text.Contains("token:"), "Should include token");
    }

    [TestMethod]
    public async Task DiffSnapshots_NoChanges_ReportsNoChanges()
    {
        using var tempDir = new TestTempDirectory("Mcp-DiffNoChange");
        File.WriteAllText(Path.Combine(tempDir.Path, "stable.txt"), "content");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        // First call to get token
        var (_, body1) = await InvokeMcpAsync(options,
            """{"method":"tools/call","id":21,"params":{"name":"diff_snapshots","arguments":{}}}""");
        var text1 = JsonDocument.Parse(body1).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var tokenLine = text1.Split('\n').First(l => l.StartsWith("token:"));
        var token = tokenLine["token:".Length..].Trim();

        // Second call with same state
        var requestBody = JsonSerializer.Serialize(new
        {
            method = "tools/call",
            id = 22,
            @params = new
            {
                name = "diff_snapshots",
                arguments = new { previousToken = token }
            }
        });

        var (status2, body2) = await InvokeMcpAsync(options, requestBody);

        Assert.AreEqual(200, status2);
        var text2 = JsonDocument.Parse(body2).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.IsTrue(text2.Contains("No changes detected"), "Should report no changes");
        Assert.IsTrue(text2.Contains("Unchanged: 1"), "Should show 1 unchanged file");
    }

    [TestMethod]
    public async Task DiffSnapshots_DetectsAddedAndRemovedFiles()
    {
        using var tempDir = new TestTempDirectory("Mcp-DiffChanges");
        var file1 = Path.Combine(tempDir.Path, "original.txt");
        File.WriteAllText(file1, "original content");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        // First snapshot
        var (_, body1) = await InvokeMcpAsync(options,
            """{"method":"tools/call","id":30,"params":{"name":"diff_snapshots","arguments":{}}}""");
        var text1 = JsonDocument.Parse(body1).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var token = text1.Split('\n').First(l => l.StartsWith("token:"))["token:".Length..].Trim();

        // Make changes: delete original, add new
        File.Delete(file1);
        File.WriteAllText(Path.Combine(tempDir.Path, "newfile.txt"), "new content");

        // Second snapshot with previous token
        var requestBody = JsonSerializer.Serialize(new
        {
            method = "tools/call",
            id = 31,
            @params = new
            {
                name = "diff_snapshots",
                arguments = new { previousToken = token }
            }
        });

        var (status2, body2) = await InvokeMcpAsync(options, requestBody);

        Assert.AreEqual(200, status2);
        var text2 = JsonDocument.Parse(body2).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.IsTrue(text2.Contains("Added (1)"), "Should detect 1 added file");
        Assert.IsTrue(text2.Contains("Removed (1)"), "Should detect 1 removed file");
        Assert.IsTrue(text2.Contains("newfile.txt"), "Should list the new file");
        Assert.IsTrue(text2.Contains("original.txt"), "Should list the removed file");
    }

    [TestMethod]
    public async Task DiffSnapshots_InvalidToken_ReturnsError()
    {
        using var tempDir = new TestTempDirectory("Mcp-DiffBadToken");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var requestBody = JsonSerializer.Serialize(new
        {
            method = "tools/call",
            id = 40,
            @params = new
            {
                name = "diff_snapshots",
                arguments = new { previousToken = "not-a-valid-token" }
            }
        });

        var (status, body) = await InvokeMcpAsync(options, requestBody);

        Assert.AreEqual(200, status);
        var text = JsonDocument.Parse(body).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.IsTrue(text.Contains("Invalid or corrupted"), "Should report invalid token");
    }

    [TestMethod]
    public async Task DiffSnapshots_WrongPath_ReturnsError()
    {
        using var tempDir = new TestTempDirectory("Mcp-DiffWrongPath");
        var subDir = Path.Combine(tempDir.Path, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "data");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        // Get token for "subdir"
        var (_, body1) = await InvokeMcpAsync(options,
            """{"method":"tools/call","id":50,"params":{"name":"diff_snapshots","arguments":{"path":"subdir"}}}""");
        var text1 = JsonDocument.Parse(body1).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var token = text1.Split('\n').First(l => l.StartsWith("token:"))["token:".Length..].Trim();

        // Use token against root path (mismatch)
        var requestBody = JsonSerializer.Serialize(new
        {
            method = "tools/call",
            id = 51,
            @params = new
            {
                name = "diff_snapshots",
                arguments = new { previousToken = token }
            }
        });

        var (status2, body2) = await InvokeMcpAsync(options, requestBody);

        Assert.AreEqual(200, status2);
        var text2 = JsonDocument.Parse(body2).RootElement
            .GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.IsTrue(text2.Contains("path-specific"), "Should warn about path mismatch");
    }

    [TestMethod]
    public async Task ToolsList_IncludesDiffSnapshots()
    {
        using var tempDir = new TestTempDirectory("Mcp-ToolsListDiff");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableMcpEndpoint = true;

        var (status, body) = await InvokeMcpAsync(options,
            """{"method":"tools/list","id":60}""");

        Assert.AreEqual(200, status);
        using var doc = JsonDocument.Parse(body);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        var found = false;
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.GetProperty("name").GetString() == "diff_snapshots")
            {
                found = true;
                break;
            }
        }
        Assert.IsTrue(found, "tools/list should include diff_snapshots");
    }
}
