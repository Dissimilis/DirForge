using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DirForge.Models;
using Microsoft.Net.Http.Headers;

namespace DirForge.Services;

public static class JsonApiEndpoints
{
    private static readonly ContentTypeResolver ContentType = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static IApplicationBuilder MapJsonApiEndpoints(this IApplicationBuilder app, DirForgeOptions options)
    {
        if (!options.EnableJsonApi)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.Equals("/api", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // /api/stats is owned by operational endpoints and must bypass JSON API routing.
            if (path.Equals(DashboardRouteHelper.ApiStatsPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals($"{DashboardRouteHelper.ApiStatsPath}/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            await HandleApiRequestAsync(context, path, options);
        });

        return app;
    }

    private static async Task HandleApiRequestAsync(HttpContext context, string path, DirForgeOptions options)
    {
        var method = context.Request.Method;
        if (method != "GET" && method != "POST")
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var metrics = context.RequestServices.GetService<DashboardMetricsService>();
        metrics?.RecordApiRequest();

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var shareLinkService = context.RequestServices.GetRequiredService<ShareLinkService>();

        ShareAccessContext? shareContext = null;
        var tokenQuery = context.Request.Query[ShareLinkService.TokenQueryParameter].ToString();
        if (!string.IsNullOrWhiteSpace(tokenQuery))
        {
            if (!shareLinkService.TryValidateToken(tokenQuery, DateTimeOffset.UtcNow, out shareContext))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Invalid or expired share token.");
                return;
            }
        }

        var subPath = path.Length > 4 ? path[4..] : string.Empty;

        if (string.IsNullOrEmpty(subPath) || subPath == "/")
        {
            await HandleApiRootAsync(context, options);
            return;
        }

        if (subPath.StartsWith("/browse", StringComparison.OrdinalIgnoreCase))
        {
            var browsePath = subPath.Length > 7 ? subPath[7..] : string.Empty;

            if (browsePath.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
            {
                var dirPath = browsePath[..^"/download".Length];
                await HandleDirectoryDownloadAsync(context, dirPath, options, listingService, shareContext, metrics);
            }
            else
            {
                await HandleBrowseAsync(context, browsePath, options, listingService, shareContext);
            }
        }
        else if (subPath.StartsWith("/files", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = subPath.Length > 6 ? subPath[6..] : string.Empty;

            if (filePath.EndsWith("/hashes", StringComparison.OrdinalIgnoreCase))
            {
                var actualPath = filePath[..^"/hashes".Length];
                await HandleFileHashesAsync(context, actualPath, options, listingService, shareContext);
            }
            else if (filePath.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
            {
                var actualPath = filePath[..^"/download".Length];
                await HandleFileDownloadAsync(context, actualPath, options, listingService, shareContext, metrics);
            }
            else
            {
                await HandleFileMetadataAsync(context, filePath, options, listingService, shareContext);
            }
        }
        else if (subPath.StartsWith("/search", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSearchAsync(context, options, listingService, shareContext);
        }
        else if (subPath.StartsWith("/share", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            await HandleCreateShareAsync(context, options, listingService, shareLinkService, shareContext, metrics);
        }
        else if (subPath.StartsWith("/archive", StringComparison.OrdinalIgnoreCase))
        {
            var archivePath = subPath.Length > 8 ? subPath[8..] : string.Empty;
            await HandleArchiveBrowseAsync(context, archivePath, options, listingService, shareContext);
        }
        else
        {
            await WriteErrorAsync(context, 404, "NotFound", "Unknown API endpoint.");
        }
    }


    private static async Task HandleApiRootAsync(HttpContext context, DirForgeOptions options)
    {
        var links = new Dictionary<string, object>
        {
            ["self"] = new { href = "/api" },
            ["browse"] = new { href = "/api/browse" }
        };

        if (options.EnableSearch)
        {
            links["search"] = new { href = "/api/search?q={query}" };
        }

        if (options.EnableSharing)
        {
            links["share"] = new { href = "/api/share" };
        }

        var result = new Dictionary<string, object>
        {
            ["_links"] = links,
            ["server"] = "DirForge",
            ["apiVersion"] = "1.0"
        };

        await WriteJsonAsync(context, 200, result);
    }

    private static async Task HandleBrowseAsync(
        HttpContext context,
        string subPath,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext)
    {
        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (!Directory.Exists(physicalPath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (shareContext is not null)
        {
            var shareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!shareSvc.IsPathWithinScope(relativePath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        var query = context.Request.Query;
        var sortMode = ParseSortMode(query["sort"].ToString());
        var sortDirection = ParseSortDirection(query["dir"].ToString(), sortMode);
        var searchQuery = options.EnableSearch ? query["q"].ToString().Trim() : string.Empty;
        var searchActive = !string.IsNullOrWhiteSpace(searchQuery);

        List<DirectoryEntry> entries;
        bool truncated = false;

        try
        {
            if (searchActive)
            {
                entries = listingService.GetSortedSearchEntries(
                    physicalPath, relativePath, searchQuery,
                    sortMode, sortDirection,
                    DirectoryListingService.SearchResultLimit, out truncated);
            }
            else
            {
                entries = listingService.GetSortedEntries(physicalPath, relativePath, sortMode, sortDirection);
            }
        }
        catch (UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "Access denied.");
            return;
        }

        var name = string.IsNullOrEmpty(relativePath) ? "" : Path.GetFileName(relativePath);
        var parentPath = string.IsNullOrEmpty(relativePath) ? null : GetParentBrowseHref(relativePath);
        var selfHref = "/api/browse" + (string.IsNullOrEmpty(relativePath) ? "" : "/" + relativePath);

        var links = new Dictionary<string, object>
        {
            ["self"] = new { href = selfHref }
        };
        if (parentPath is not null)
        {
            links["parent"] = new { href = parentPath };
        }
        if (options.AllowFolderDownload)
        {
            links["download"] = new { href = selfHref + "/download" };
        }
        if (options.EnableSearch)
        {
            links["search"] = new { href = "/api/search" + (string.IsNullOrEmpty(relativePath) ? "" : "?path=" + Uri.EscapeDataString(relativePath)) };
        }

        var items = new List<object>(entries.Count);
        foreach (var entry in entries)
        {
            if (options.HideDotfiles && entry.Name.StartsWith('.'))
            {
                continue;
            }

            items.Add(BuildItemObject(entry, relativePath));
        }

        var result = new Dictionary<string, object>
        {
            ["_links"] = links,
            ["path"] = relativePath,
            ["name"] = name,
            ["isDirectory"] = true,
            ["sort"] = new { by = sortMode.ToString().ToLowerInvariant(), direction = sortDirection.ToString().ToLowerInvariant() },
            ["totalItems"] = items.Count
        };

        if (searchActive)
        {
            result["searchQuery"] = searchQuery;
            result["searchTruncated"] = truncated;
        }

        result["items"] = items;

        await WriteJsonAsync(context, 200, result);
    }

    private static async Task HandleFileMetadataAsync(
        HttpContext context,
        string subPath,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext)
    {
        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "File path is required.");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (shareContext is not null)
        {
            var shareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!shareSvc.IsPathWithinScope(relativePath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        var extension = Path.GetExtension(fileInfo.Name);
        var mimeType = ContentType.GetContentType(fileInfo.Name);
        var detectedType = FileSignatureDetector.Detect(physicalPath);
        var parentRelative = GetParentRelativePath(relativePath);

        var links = new Dictionary<string, object>
        {
            ["self"] = new { href = "/api/files/" + relativePath }
        };
        if (options.AllowFileDownload && !listingService.IsFileDownloadBlocked(relativePath))
        {
            links["download"] = new { href = "/api/files/" + relativePath + "/download" };
            links["hashes"] = new { href = "/api/files/" + relativePath + "/hashes" };
        }
        links["parent"] = new { href = "/api/browse" + (string.IsNullOrEmpty(parentRelative) ? "" : "/" + parentRelative) };

        var result = new Dictionary<string, object>
        {
            ["_links"] = links,
            ["path"] = relativePath,
            ["name"] = fileInfo.Name,
            ["isDirectory"] = false,
            ["size"] = fileInfo.Length,
            ["humanSize"] = DirectoryListingService.HumanizeSize(fileInfo.Length),
            ["modified"] = fileInfo.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["extension"] = extension,
            ["mimeType"] = mimeType
        };

        if (detectedType is not null)
            result["detectedFileType"] = detectedType;

        await WriteJsonAsync(context, 200, result);
    }

    private static async Task HandleFileDownloadAsync(
        HttpContext context,
        string subPath,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext,
        DashboardMetricsService? metrics)
    {
        if (!options.AllowFileDownload)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "File downloads are disabled.");
            return;
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "File path is required.");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteErrorAsync(context, 403, "Forbidden", "File download blocked by policy.");
            return;
        }

        if (shareContext is not null)
        {
            var shareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!shareSvc.IsPathWithinScope(relativePath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        var contentType = ContentType.GetContentType(fileInfo.Name);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;
        context.Response.Headers[HeaderNames.ContentDisposition] =
            $"attachment; filename=\"{Uri.EscapeDataString(fileInfo.Name)}\"";

        metrics?.RecordFileDownload(fileInfo.Length);
        metrics?.RecordApiFileDownload(fileInfo.Length);

        await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task HandleFileHashesAsync(
        HttpContext context,
        string subPath,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext)
    {
        if (!options.AllowFileDownload)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "File downloads are disabled.");
            return;
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "File path is required.");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteErrorAsync(context, 403, "Forbidden", "File download blocked by policy.");
            return;
        }

        if (shareContext is not null)
        {
            var shareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!shareSvc.IsPathWithinScope(relativePath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteErrorAsync(context, 404, "NotFound", "File not found.");
            return;
        }

        if (fileInfo.Length > options.MaxFileSizeForHashing)
        {
            await WriteErrorAsync(context, 400, "BadRequest", "File exceeds the maximum size for hashing.");
            return;
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];

        await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), context.RequestAborted)) > 0)
        {
            md5.AppendData(buffer.AsSpan(0, bytesRead));
            sha1.AppendData(buffer.AsSpan(0, bytesRead));
            sha256.AppendData(buffer.AsSpan(0, bytesRead));
            sha512.AppendData(buffer.AsSpan(0, bytesRead));
        }

        var result = new Dictionary<string, object>
        {
            ["_links"] = new Dictionary<string, object>
            {
                ["self"] = new { href = "/api/files/" + relativePath + "/hashes" },
                ["file"] = new { href = "/api/files/" + relativePath }
            },
            ["path"] = relativePath,
            ["md5"] = Convert.ToHexString(md5.GetCurrentHash()).ToLowerInvariant(),
            ["sha1"] = Convert.ToHexString(sha1.GetCurrentHash()).ToLowerInvariant(),
            ["sha256"] = Convert.ToHexString(sha256.GetCurrentHash()).ToLowerInvariant(),
            ["sha512"] = Convert.ToHexString(sha512.GetCurrentHash()).ToLowerInvariant()
        };

        await WriteJsonAsync(context, 200, result);
    }

    private static async Task HandleDirectoryDownloadAsync(
        HttpContext context,
        string subPath,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext,
        DashboardMetricsService? metrics)
    {
        if (!options.AllowFolderDownload)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "Folder downloads are disabled.");
            return;
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (!Directory.Exists(physicalPath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        ShareLinkService? zipShareSvc = null;
        if (shareContext is not null)
        {
            zipShareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!zipShareSvc.IsPathWithinScope(relativePath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        var folderName = Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName))
        {
            folderName = "download";
        }

        context.Response.ContentType = "application/zip";
        context.Response.Headers[HeaderNames.ContentDisposition] =
            $"attachment; filename=\"{Uri.EscapeDataString(folderName)}.zip\"";

        long totalBytes = 0;
        try
        {
            await using (var archive = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                var opBudgetMs = options.OperationTimeBudgetMs;
                Stopwatch? opTimer = opBudgetMs > 0 ? Stopwatch.StartNew() : null;
                var sourceFilePaths = listingService.EnumerateFilePathsRecursive(physicalPath, opTimer, opBudgetMs);
                var maxZip = options.MaxZipSize;

                foreach (var filePath in sourceFilePaths)
                {
                    context.RequestAborted.ThrowIfCancellationRequested();

                    if (!listingService.ValidateSymlinkContainment(filePath))
                        continue;

                    var entryName = Path.GetRelativePath(physicalPath, filePath).Replace('\\', '/');
                    var rootRelativePath = listingService.GetRootRelativePath(filePath);

                    if (listingService.IsPathHiddenByPolicy(rootRelativePath, isDirectory: false))
                        continue;

                    if (listingService.IsFileDownloadBlocked(rootRelativePath))
                        continue;

                    if (options.HideDotfiles && entryName.Split('/').Any(s => s.StartsWith('.')))
                        continue;

                    if (shareContext is not null && !zipShareSvc!.IsPathWithinScope(rootRelativePath, shareContext.ScopePath))
                        continue;

                    var fileLength = new FileInfo(filePath).Length;
                    if (maxZip > 0 && totalBytes + fileLength > maxZip)
                        break;

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(filePath);

                    try
                    {
                        await using var entryStream = entry.Open();
                        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, useAsync: true);
                        await fileStream.CopyToAsync(entryStream, context.RequestAborted);
                        totalBytes += fileLength;
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (FileNotFoundException) { }
                }
            }

            await context.Response.Body.FlushAsync(context.RequestAborted);
            await context.Response.CompleteAsync();
            metrics?.RecordZipDownload(totalBytes);
        }
        catch (OperationCanceledException) { }
    }

    private static async Task HandleSearchAsync(
        HttpContext context,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext)
    {
        if (!options.EnableSearch)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "Search is disabled.");
            return;
        }

        var query = context.Request.Query;
        var searchQuery = query["q"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Search query 'q' is required.");
            return;
        }

        var searchPath = DirectoryListingService.NormalizeRelativePath(query["path"].ToString());

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(searchPath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(searchPath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Search path not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(searchPath, isDirectory: true))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (shareContext is not null)
        {
            var shareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!shareSvc.IsPathWithinScope(searchPath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        var sortMode = ParseSortMode(query["sort"].ToString());
        var sortDirection = ParseSortDirection(query["dir"].ToString(), sortMode);

        List<DirectoryEntry> entries;
        bool truncated;
        try
        {
            entries = listingService.GetSortedSearchEntries(
                physicalPath, searchPath, searchQuery,
                sortMode, sortDirection,
                DirectoryListingService.SearchResultLimit, out truncated);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "Access denied.");
            return;
        }

        var items = new List<object>(entries.Count);
        foreach (var entry in entries)
        {
            if (options.HideDotfiles && entry.Name.StartsWith('.'))
                continue;

            items.Add(BuildItemObject(entry, searchPath));
        }

        var result = new Dictionary<string, object>
        {
            ["_links"] = new Dictionary<string, object>
            {
                ["self"] = new { href = "/api/search?q=" + Uri.EscapeDataString(searchQuery) + (string.IsNullOrEmpty(searchPath) ? "" : "&path=" + Uri.EscapeDataString(searchPath)) }
            },
            ["searchQuery"] = searchQuery,
            ["searchPath"] = searchPath,
            ["searchTruncated"] = truncated,
            ["sort"] = new { by = sortMode.ToString().ToLowerInvariant(), direction = sortDirection.ToString().ToLowerInvariant() },
            ["totalItems"] = items.Count,
            ["items"] = items
        };

        await WriteJsonAsync(context, 200, result);
    }

    private static async Task HandleCreateShareAsync(
        HttpContext context,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareLinkService shareLinkService,
        ShareAccessContext? shareContext,
        DashboardMetricsService? metrics)
    {
        if (!options.EnableSharing || shareContext is not null)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "Sharing is disabled or not allowed from share context.");
            return;
        }

        JsonElement body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Invalid JSON body.");
            return;
        }

        var targetPath = body.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
        var targetType = body.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        var ttlSeconds = body.TryGetProperty("ttlSeconds", out var ttlProp) && ttlProp.ValueKind == JsonValueKind.Number
            ? ttlProp.GetInt64()
            : 0L;
        var oneTime = body.TryGetProperty("oneTime", out var otProp) && otProp.ValueKind == JsonValueKind.True;

        if (string.IsNullOrWhiteSpace(targetType))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Missing 'type' (file or directory).");
            return;
        }

        var mode = targetType.Trim().ToLowerInvariant() switch
        {
            "file" => ShareMode.File,
            "directory" => ShareMode.Directory,
            _ => (ShareMode?)null
        };

        if (mode is null)
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Invalid 'type'. Must be 'file' or 'directory'.");
            return;
        }

        if (ttlSeconds <= 0)
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Missing or invalid 'ttlSeconds'.");
            return;
        }

        var relativePath = ShareLinkService.NormalizeRelativePath(targetPath);
        if (mode == ShareMode.File && string.IsNullOrEmpty(relativePath))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "File share path is required.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        var isDirectory = mode == ShareMode.Directory;
        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (isDirectory ? !Directory.Exists(physicalPath) : !File.Exists(physicalPath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds);
        var token = shareLinkService.CreateToken(mode.Value, relativePath, expiresAt.ToUnixTimeSeconds(), oneTime);
        metrics?.RecordShareLinkCreated();

        var result = new Dictionary<string, object>
        {
            ["token"] = token,
            ["expiresAtUtc"] = expiresAt.ToString("O"),
            ["oneTime"] = oneTime
        };

        await WriteJsonAsync(context, 200, result);
    }

    private static async Task HandleArchiveBrowseAsync(
        HttpContext context,
        string subPath,
        DirForgeOptions options,
        DirectoryListingService listingService,
        ShareAccessContext? shareContext)
    {
        if (!options.OpenArchivesInline)
        {
            await WriteErrorAsync(context, 403, "Forbidden", "Archive browsing is disabled.");
            return;
        }

        var archiveBrowseService = context.RequestServices.GetRequiredService<ArchiveBrowseService>();
        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !File.Exists(physicalPath))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Archive not found.");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteErrorAsync(context, 404, "NotFound", "Path not found.");
            return;
        }

        if (shareContext is not null)
        {
            var shareSvc = context.RequestServices.GetRequiredService<ShareLinkService>();
            if (!shareSvc.IsPathWithinScope(relativePath, shareContext.ScopePath))
            {
                await WriteErrorAsync(context, 403, "Forbidden", "Path outside share scope.");
                return;
            }
        }

        if (!archiveBrowseService.IsSupportedArchiveName(physicalPath))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Not a supported archive format.");
            return;
        }

        var virtualPath = context.Request.Query["virtualPath"].ToString();
        if (!archiveBrowseService.TryNormalizeVirtualPath(virtualPath, out var normalizedVirtualPath))
        {
            await WriteErrorAsync(context, 400, "BadRequest", "Invalid virtual path.");
            return;
        }

        ArchiveBrowseListing listing;
        try
        {
            listing = archiveBrowseService.BuildListing(physicalPath, normalizedVirtualPath, options.OperationTimeBudgetMs);
        }
        catch (Exception)
        {
            await WriteErrorAsync(context, 500, "InternalError", "Failed to read archive.");
            return;
        }

        var items = new List<object>(listing.Entries.Count);
        foreach (var entry in listing.Entries)
        {
            items.Add(new Dictionary<string, object?>
            {
                ["name"] = entry.Name,
                ["path"] = entry.Path,
                ["isDirectory"] = entry.IsDirectory,
                ["size"] = entry.Size,
                ["humanSize"] = entry.IsDirectory ? "-" : DirectoryListingService.HumanizeSize(entry.Size),
                ["modified"] = entry.ModifiedUtc?.ToString("yyyy-MM-ddTHH:mm:ss")
            });
        }

        var result = new Dictionary<string, object>
        {
            ["_links"] = new Dictionary<string, object>
            {
                ["self"] = new { href = "/api/archive/" + relativePath + (string.IsNullOrEmpty(normalizedVirtualPath) ? "" : "?virtualPath=" + Uri.EscapeDataString(normalizedVirtualPath)) }
            },
            ["archivePath"] = relativePath,
            ["virtualPath"] = normalizedVirtualPath,
            ["parentPath"] = listing.ParentPath,
            ["totalItems"] = items.Count,
            ["items"] = items
        };

        await WriteJsonAsync(context, 200, result);
    }

    private static object BuildItemObject(DirectoryEntry entry, string currentRelativePath)
    {
        var item = new Dictionary<string, object>
        {
            ["name"] = entry.Name,
            ["relativePath"] = entry.RelativePath,
            ["isDirectory"] = entry.IsDirectory
        };

        var links = new Dictionary<string, object>();
        if (entry.IsDirectory)
        {
            links["self"] = new { href = "/api/browse/" + entry.RelativePath };
            var parentHref = string.IsNullOrEmpty(currentRelativePath)
                ? "/api/browse"
                : "/api/browse/" + currentRelativePath;
            links["parent"] = new { href = parentHref };
        }
        else
        {
            links["self"] = new { href = "/api/files/" + entry.RelativePath };
            links["download"] = new { href = "/api/files/" + entry.RelativePath + "/download" };
            links["hashes"] = new { href = "/api/files/" + entry.RelativePath + "/hashes" };
            var parentHref = string.IsNullOrEmpty(currentRelativePath)
                ? "/api/browse"
                : "/api/browse/" + currentRelativePath;
            links["parent"] = new { href = parentHref };

            item["size"] = entry.Size;
            item["humanSize"] = entry.HumanSize;
            item["modified"] = entry.Modified.ToString("yyyy-MM-ddTHH:mm:ss");
            item["type"] = entry.Type;
            item["extension"] = entry.Extension;
        }

        item["_links"] = links;
        return item;
    }

    private static SortMode ParseSortMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "name" => SortMode.Name,
            "date" => SortMode.Date,
            "size" => SortMode.Size,
            "type" => SortMode.Type,
            _ => SortMode.Type
        };
    }

    private static SortDirection ParseSortDirection(string? value, SortMode sortMode)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "asc" => SortDirection.Asc,
            "desc" => SortDirection.Desc,
            _ => DirectoryListingService.GetDefaultSortDirection(sortMode)
        };
    }

    private static string? GetParentBrowseHref(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return "/api/browse";
        }
        return "/api/browse/" + relativePath[..lastSlash];
    }

    private static string GetParentRelativePath(string relativePath)
    {
        var lastSlash = relativePath.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : relativePath[..lastSlash];
    }

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, object value)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, JsonOptions, context.RequestAborted);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var error = new { error = new { status = statusCode, code, message } };
        await JsonSerializer.SerializeAsync(context.Response.Body, error, JsonOptions, context.RequestAborted);
    }
}
