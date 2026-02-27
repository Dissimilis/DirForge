using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DirForge.Models;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private const string ServerName = "dirforge";
    private const string ServerVersion = "1.0.0";
    private const string ProtocolVersion = "2025-03-26";
    private const int MaxTreeEntries = 2000;
    private const int DefaultTreeDepth = 3;
    private const int MaxTreeDepth = 10;
    private const int MaxContentSearchResults = 200;
    private const long MaxContentSearchFileSize = 10_485_760; // 10 MB
    private const long DefaultContentSearchFileSize = 2_097_152; // 2 MB
    private const int MaxFindResults = 100;
    private const int DefaultFindResults = 20;
    private const int MaxDuplicateScanFiles = 50_000;


    public static IApplicationBuilder MapMcpEndpoints(this IApplicationBuilder app, DirForgeOptions options)
    {
        if (!options.EnableMcpEndpoint)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (context.Request.Method != "POST")
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            await HandleMcpRequestAsync(context, options);
        });

        return app;
    }

    private static async Task HandleMcpRequestAsync(HttpContext context, DirForgeOptions options)
    {
        var metrics = context.RequestServices.GetService<DashboardMetricsService>();
        metrics?.RecordMcpRequest();

        JsonElement request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteJsonRpcErrorAsync(context, null, -32700, "Parse error");
            return;
        }

        var method = request.TryGetProperty("method", out var methodProp) ? methodProp.GetString() : null;
        var id = request.TryGetProperty("id", out var idProp) ? idProp : (JsonElement?)null;
        var @params = request.TryGetProperty("params", out var paramsProp) ? paramsProp : (JsonElement?)null;

        if (string.IsNullOrWhiteSpace(method))
        {
            await WriteJsonRpcErrorAsync(context, id, -32600, "Invalid request: missing method");
            return;
        }

        switch (method)
        {
            case "initialize":
                await HandleInitializeAsync(context, id, options);
                break;
            case "notifications/initialized":
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                break;
            case "ping":
                await WriteJsonRpcResultAsync(context, id, new { });
                break;
            case "resources/list":
                await HandleResourcesListAsync(context, id, options);
                break;
            case "resources/read":
                await HandleResourcesReadAsync(context, id, @params, options);
                break;
            case "tools/list":
                await HandleToolsListAsync(context, id, options);
                break;
            case "tools/call":
                metrics?.RecordMcpToolCall();
                await HandleToolsCallAsync(context, id, @params, options);
                break;
            case "prompts/list":
                await HandlePromptsListAsync(context, id, options);
                break;
            case "prompts/get":
                await HandlePromptsGetAsync(context, id, @params, options);
                break;
            default:
                await WriteJsonRpcErrorAsync(context, id, -32601, $"Method not found: {method}");
                break;
        }
    }

    private static async Task HandleInitializeAsync(HttpContext context, JsonElement? id, DirForgeOptions options)
    {
        var result = new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new
            {
                resources = new { },
                tools = new { },
                prompts = new { }
            },
            serverInfo = new
            {
                name = ServerName,
                version = ServerVersion
            }
        };

        await WriteJsonRpcResultAsync(context, id, result);
    }

    private static async Task HandleResourcesListAsync(HttpContext context, JsonElement? id, DirForgeOptions options)
    {
        var resources = new[]
        {
            new
            {
                uri = "dirforge://browse",
                name = "Root directory listing",
                description = "Browse the root directory of the DirForge file server.",
                mimeType = "text/plain"
            }
        };

        await WriteJsonRpcResultAsync(context, id, new { resources });
    }

    private static async Task HandleResourcesReadAsync(HttpContext context, JsonElement? id, JsonElement? @params, DirForgeOptions options)
    {
        var uri = @params?.TryGetProperty("uri", out var uriProp) == true ? uriProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(uri))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Missing 'uri' parameter");
            return;
        }

        if (!uri.StartsWith("dirforge://browse", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, $"Unknown resource URI: {uri}");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = uri.Length > "dirforge://browse".Length ? uri["dirforge://browse".Length..].TrimStart('/') : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var entries = listingService.GetSortedEntries(physicalPath, relativePath, SortMode.Type, SortDirection.Asc);
        var sb = new StringBuilder();
        sb.AppendLine($"Directory: /{relativePath}");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            if (options.HideDotfiles && entry.Name.StartsWith('.'))
                continue;

            if (entry.IsDirectory)
            {
                sb.AppendLine($"  [DIR]  {entry.Name}/");
            }
            else
            {
                sb.AppendLine($"  [FILE] {entry.Name}  ({entry.HumanSize})");
            }
        }

        var result = new
        {
            contents = new[]
            {
                new
                {
                    uri,
                    mimeType = "text/plain",
                    text = sb.ToString()
                }
            }
        };

        await WriteJsonRpcResultAsync(context, id, result);
    }

    private static List<(string Name, bool IsDirectory, long Size, DateTime Modified, string PhysicalPath)> GetFilteredChildren(
        DirectoryInfo dirInfo,
        DirectoryListingService listingService,
        DirForgeOptions options)
    {
        var results = new List<(string Name, bool IsDirectory, long Size, DateTime Modified, string PhysicalPath)>();
        try
        {
            foreach (var entry in listingService.ReadEntries(dirInfo.FullName))
            {
                var entryPhysical = Path.GetFullPath(Path.Combine(dirInfo.FullName, entry.Name));
                results.Add((entry.Name, entry.IsDirectory, entry.Size, entry.Modified, entryPhysical));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip inaccessible directories
        }

        results.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return results;
    }

    private static Dictionary<string, (bool IsDir, long Size, DateTime Modified)> CollectDirectoryEntries(
        string rootPhysicalPath,
        int maxDepth,
        long timeBudgetMs,
        Stopwatch sw,
        DirectoryListingService listingService,
        DirForgeOptions options,
        out bool truncated)
    {
        truncated = false;
        var entries = new Dictionary<string, (bool IsDir, long Size, DateTime Modified)>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((rootPhysicalPath, 0));

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs)
            {
                truncated = true;
                break;
            }

            var (currentPath, depth) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs)
                {
                    truncated = true;
                    break;
                }

                var relPath = Path.GetRelativePath(rootPhysicalPath, child.PhysicalPath).Replace('\\', '/');

                if (child.IsDirectory)
                {
                    entries[relPath] = (true, 0, child.Modified);
                    if (depth < maxDepth)
                    {
                        stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else
                {
                    entries[relPath] = (false, child.Size, child.Modified);
                }
            }
        }

        return entries;
    }


    private static bool MatchesAnyPattern(string relativePath, string name, string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
            return false;

        foreach (var raw in patterns.Split(','))
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0) continue;

            if (pattern.Contains('/'))
            {
                if (PathGlobMatch(relativePath, pattern))
                    return true;
            }
            else
            {
                if (GlobMatcher.IsSimpleWildcardMatch(name, pattern, ignoreCase: true))
                    return true;
            }
        }

        return false;
    }

    private static bool PathGlobMatch(string relativePath, string pattern)
    {
        var pathSegments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var memo = new Dictionary<(int PatternIndex, int PathIndex), bool>();
        return MatchRecursive(0, 0);

        bool MatchRecursive(int patternIndex, int pathIndex)
        {
            var key = (patternIndex, pathIndex);
            if (memo.TryGetValue(key, out var cached))
                return cached;

            bool result;
            if (patternIndex == patternSegments.Length)
            {
                result = pathIndex == pathSegments.Length;
            }
            else if (patternSegments[patternIndex] == "**")
            {
                result = MatchRecursive(patternIndex + 1, pathIndex) ||
                         (pathIndex < pathSegments.Length && MatchRecursive(patternIndex, pathIndex + 1));
            }
            else if (pathIndex == pathSegments.Length)
            {
                result = false;
            }
            else if (GlobMatcher.IsSimpleWildcardMatch(pathSegments[pathIndex], patternSegments[patternIndex], ignoreCase: true))
            {
                result = MatchRecursive(patternIndex + 1, pathIndex + 1);
            }
            else
            {
                result = false;
            }

            memo[key] = result;
            return result;
        }
    }

    private static async Task WriteToolResult(HttpContext context, JsonElement? id, string text)
    {
        var result = new
        {
            content = new[]
            {
                new { type = "text", text }
            }
        };
        await WriteJsonRpcResultAsync(context, id, result);
    }

    private static async Task WriteToolError(HttpContext context, JsonElement? id, string message)
    {
        var result = new
        {
            content = new[]
            {
                new { type = "text", text = $"Error: {message}" }
            },
            isError = true
        };
        await WriteJsonRpcResultAsync(context, id, result);
    }

    private static async Task WriteJsonRpcResultAsync(HttpContext context, JsonElement? id, object result)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        var response = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.ValueKind == JsonValueKind.Number ? (object)id.Value.GetInt64() :
                     id?.ValueKind == JsonValueKind.String ? id.Value.GetString() : null,
            ["result"] = result
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, response, JsonOptions, context.RequestAborted);
    }

    private static async Task WriteJsonRpcErrorAsync(HttpContext context, JsonElement? id, int code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        var response = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.ValueKind == JsonValueKind.Number ? (object)id.Value.GetInt64() :
                     id?.ValueKind == JsonValueKind.String ? id.Value.GetString() : null,
            ["error"] = new { code, message }
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, response, JsonOptions, context.RequestAborted);
    }
}
