using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DirForge.Models;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    private static async Task HandleToolListDirectory(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        List<DirectoryEntry> entries;
        try
        {
            entries = listingService.GetSortedEntries(physicalPath, relativePath, SortMode.Type, SortDirection.Asc);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteToolError(context, id, "Access denied");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Directory: /{relativePath}");
        sb.AppendLine($"Items: {entries.Count}");
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
                sb.AppendLine($"  [FILE] {entry.Name}  ({entry.HumanSize}, modified {entry.Modified:yyyy-MM-dd HH:mm})");
            }
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolGetFileInfo(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var contentTypeResolver = new ContentTypeResolver();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteToolError(context, id, "File path is required");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var mimeType = contentTypeResolver.GetContentType(fileInfo.Name);
        var detectedType = FileSignatureDetector.Detect(physicalPath);
        var sb = new StringBuilder();
        sb.AppendLine($"File: {fileInfo.Name}");
        sb.AppendLine($"Path: {relativePath}");
        sb.AppendLine($"Size: {DirectoryListingService.HumanizeSize(fileInfo.Length)} ({fileInfo.Length:N0} bytes)");
        sb.AppendLine($"Modified: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"MIME type: {mimeType}");
        sb.AppendLine($"Extension: {fileInfo.Extension}");
        if (detectedType is not null)
            sb.AppendLine($"Detected type: {detectedType}");

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolReadFile(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.AllowFileDownload)
        {
            await WriteToolError(context, id, "File downloads are disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxBytes = options.McpReadFileSizeLimit;
        if (arguments?.TryGetProperty("maxBytes", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt64();
            if (requested > 0 && requested < maxBytes)
            {
                maxBytes = requested;
            }
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteToolError(context, id, "File path is required");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteToolError(context, id, "File download blocked by policy");
            return;
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (fileInfo.Length > maxBytes)
        {
            await WriteToolError(context, id, $"File too large ({DirectoryListingService.HumanizeSize(fileInfo.Length)}). Maximum: {DirectoryListingService.HumanizeSize(maxBytes)}");
            return;
        }

        // Binary detection: read first 8KB and check for null bytes
        const int probeSize = 8192;
        var probeLength = (int)Math.Min(fileInfo.Length, probeSize);
        var probeBuffer = new byte[probeLength];
        await using (var probeStream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = await probeStream.ReadAsync(probeBuffer.AsMemory(0, probeLength), context.RequestAborted);
        }
        if (Array.IndexOf(probeBuffer, (byte)0, 0, probeLength) >= 0)
        {
            await WriteToolError(context, id, "File appears to be binary. Only text files can be read.");
            return;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(physicalPath, Encoding.UTF8, context.RequestAborted);
        }
        catch (Exception)
        {
            await WriteToolError(context, id, "Failed to read file (may be binary or inaccessible)");
            return;
        }

        var startLine = arguments?.TryGetProperty("startLine", out var startProp) == true && startProp.ValueKind == JsonValueKind.Number
            ? startProp.GetInt32() : 0;
        var endLine = arguments?.TryGetProperty("endLine", out var endProp) == true && endProp.ValueKind == JsonValueKind.Number
            ? endProp.GetInt32() : 0;

        if (startLine > 0 || endLine > 0)
        {
            var lines = content.Split('\n');
            var from = startLine > 0 ? Math.Min(startLine, lines.Length) : 1;
            var to = endLine > 0 ? Math.Min(endLine, lines.Length) : lines.Length;

            if (from > to)
            {
                await WriteToolError(context, id, "startLine must be less than or equal to endLine");
                return;
            }

            var sb = new StringBuilder();
            for (var i = from; i <= to; i++)
            {
                sb.AppendLine($"{i,6}\t{lines[i - 1]}");
            }

            await WriteToolResult(context, id, sb.ToString());
            return;
        }

        await WriteToolResult(context, id, content);
    }

    private static async Task HandleToolSearch(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.EnableSearch)
        {
            await WriteToolError(context, id, "Search is disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var query = arguments?.TryGetProperty("query", out var queryProp) == true ? queryProp.GetString() : "";
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var maxResults = DirectoryListingService.SearchResultLimit;

        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested < maxResults)
            {
                maxResults = requested;
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            await WriteToolError(context, id, "Search query is required");
            return;
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Search path not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        List<DirectoryEntry> entries;
        bool truncated;
        try
        {
            entries = listingService.GetSortedSearchEntries(
                physicalPath, relativePath, query,
                SortMode.Type, SortDirection.Asc,
                maxResults, out truncated);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteToolError(context, id, "Access denied");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Search results for \"{query}\" in /{relativePath}");
        sb.AppendLine($"Found: {entries.Count}{(truncated ? " (truncated)" : "")}");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            if (options.HideDotfiles && entry.Name.StartsWith('.'))
                continue;

            if (entry.IsDirectory)
            {
                sb.AppendLine($"  [DIR]  {entry.RelativePath}/");
            }
            else
            {
                sb.AppendLine($"  [FILE] {entry.RelativePath}  ({entry.HumanSize})");
            }
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolSearchContent(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.EnableSearch)
        {
            await WriteToolError(context, id, "Search is disabled");
            return;
        }

        if (!options.AllowFileDownload)
        {
            await WriteToolError(context, id, "File downloads are disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var query = arguments?.TryGetProperty("query", out var queryProp) == true ? queryProp.GetString() : "";
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";

        if (string.IsNullOrWhiteSpace(query))
        {
            await WriteToolError(context, id, "Search query is required");
            return;
        }

        var maxResults = 50;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxContentSearchResults)
            {
                maxResults = requested;
            }
        }

        var maxFileSize = DefaultContentSearchFileSize;
        if (arguments?.TryGetProperty("maxFileSize", out var fsProp) == true && fsProp.ValueKind == JsonValueKind.Number)
        {
            var requested = fsProp.GetInt64();
            if (requested > 0 && requested <= MaxContentSearchFileSize)
            {
                maxFileSize = requested;
            }
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Search path not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var maxDepth = options.SearchMaxDepth;
        var matchCount = 0;
        var matchFileCount = 0;
        var truncated = false;

        // Collect matches grouped by file: (displayPath, list of (lineNumber, lineText))
        var fileMatches = new List<(string DisplayPath, List<(int LineNumber, string LineText)> Lines)>();

        // Stack-based DFS: (physicalPath, depth)
        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((physicalPath, 0));

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs || matchCount >= maxResults)
            {
                truncated = true;
                break;
            }

            var (currentPath, depth) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs || matchCount >= maxResults)
                {
                    truncated = true;
                    break;
                }

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else
                {
                    if (child.Size > maxFileSize) continue;

                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (listingService.IsFileDownloadBlocked(fileRelPath)) continue;

                    // Binary detection: probe first 8KB for null bytes
                    const int probeSize = 8192;
                    var probeLength = (int)Math.Min(child.Size, probeSize);
                    byte[] probeBuffer;
                    try
                    {
                        probeBuffer = new byte[probeLength];
                        await using var probeStream = new FileStream(child.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        _ = await probeStream.ReadAsync(probeBuffer.AsMemory(0, probeLength), context.RequestAborted);
                    }
                    catch
                    {
                        continue;
                    }
                    if (Array.IndexOf(probeBuffer, (byte)0, 0, probeLength) >= 0) continue;

                    // Read and search the file
                    string content;
                    try
                    {
                        content = await File.ReadAllTextAsync(child.PhysicalPath, Encoding.UTF8, context.RequestAborted);
                    }
                    catch
                    {
                        continue;
                    }

                    var lines = content.Split('\n');
                    var thisFileMatches = new List<(int LineNumber, string LineText)>();

                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (matchCount >= maxResults)
                        {
                            truncated = true;
                            break;
                        }

                        if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            var lineText = lines[i].Trim();
                            if (lineText.Length > 200)
                                lineText = lineText[..200] + "...";
                            thisFileMatches.Add((i + 1, lineText));
                            matchCount++;
                        }
                    }

                    if (thisFileMatches.Count > 0)
                    {
                        fileMatches.Add((fileRelPath, thisFileMatches));
                        matchFileCount++;
                    }
                }
            }
        }

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"Content search for \"{query}\" in {displayRoot}");
        sb.AppendLine($"Found: {matchCount} matches in {matchFileCount} files");
        sb.AppendLine();

        foreach (var (displayPath, lineMatches) in fileMatches)
        {
            sb.AppendLine(displayPath);
            foreach (var (lineNumber, lineText) in lineMatches)
            {
                sb.AppendLine($"  {lineNumber,6}: {lineText}");
            }
            sb.AppendLine();
        }

        if (truncated)
        {
            sb.AppendLine("(results may be incomplete — result limit or time budget reached)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolGetFileHashes(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.AllowFileDownload)
        {
            await WriteToolError(context, id, "File downloads are disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteToolError(context, id, "File path is required");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteToolError(context, id, "File download blocked by policy");
            return;
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (fileInfo.Length > options.MaxFileSizeForHashing)
        {
            await WriteToolError(context, id, $"File exceeds maximum size for hashing ({DirectoryListingService.HumanizeSize(options.MaxFileSizeForHashing)})");
            return;
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        uint crc = Crc32.Init();

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
            crc = Crc32.Update(crc, buffer.AsSpan(0, bytesRead));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"File: {relativePath}");
        sb.AppendLine($"Size: {DirectoryListingService.HumanizeSize(fileInfo.Length)} ({fileInfo.Length:N0} bytes)");
        sb.AppendLine();
        sb.AppendLine($"CRC32:  {Crc32.Finalize(crc)}");
        sb.AppendLine($"MD5:    {Convert.ToHexString(md5.GetCurrentHash()).ToLowerInvariant()}");
        sb.AppendLine($"SHA1:   {Convert.ToHexString(sha1.GetCurrentHash()).ToLowerInvariant()}");
        sb.AppendLine($"SHA256: {Convert.ToHexString(sha256.GetCurrentHash()).ToLowerInvariant()}");
        sb.AppendLine($"SHA512: {Convert.ToHexString(sha512.GetCurrentHash()).ToLowerInvariant()}");

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolVerifySidecar(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.AllowFileDownload)
        {
            await WriteToolError(context, id, "File downloads are disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteToolError(context, id, "File path is required");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteToolError(context, id, "File download blocked by policy");
            return;
        }

        var fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        (string ext, string algo)[] sidecarExts =
        [
            (".sha512", "sha512"), (".sha512sum", "sha512"),
            (".sha256", "sha256"), (".sha256sum", "sha256"),
            (".sha1", "sha1"), (".sha1sum", "sha1"),
            (".md5", "md5"), (".md5sum", "md5"),
            (".sfv", "crc32"),
        ];

        string? sidecarPath = null;
        string? algorithm = null;
        foreach (var (ext, algo) in sidecarExts)
        {
            var candidate = physicalPath + ext;
            if (File.Exists(candidate))
            {
                sidecarPath = candidate;
                algorithm = algo;
                break;
            }
        }

        if (sidecarPath is null || algorithm is null)
        {
            await WriteToolError(context, id, "No sidecar checksum file found");
            return;
        }

        string sidecarContent;
        try
        {
            var sidecarInfo = new FileInfo(sidecarPath);
            if (sidecarInfo.Length > 4096)
            {
                await WriteToolError(context, id, "Sidecar file is too large");
                return;
            }
            sidecarContent = await File.ReadAllTextAsync(sidecarPath, context.RequestAborted);
        }
        catch (Exception)
        {
            await WriteToolError(context, id, "Could not read sidecar file");
            return;
        }

        var expectedHash = McpParseSidecarHash(sidecarContent, fileInfo.Name);
        if (expectedHash is null)
        {
            await WriteToolError(context, id, "Could not parse expected hash from sidecar file");
            return;
        }

        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        var timer = Stopwatch.StartNew();
        var budgetMs = options.OperationTimeBudgetMs;
        string computedHash;

        await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        int bytesRead;

        if (algorithm == "crc32")
        {
            uint crc = Crc32.Init();
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), context.RequestAborted)) > 0)
            {
                crc = Crc32.Update(crc, buffer.AsSpan(0, bytesRead));
                if (budgetMs > 0 && timer.ElapsedMilliseconds >= budgetMs)
                {
                    await WriteToolError(context, id, "Operation timed out");
                    return;
                }
            }
            computedHash = Crc32.Finalize(crc);
        }
        else
        {
            var algoName = algorithm switch
            {
                "md5" => HashAlgorithmName.MD5,
                "sha1" => HashAlgorithmName.SHA1,
                "sha256" => HashAlgorithmName.SHA256,
                "sha512" => HashAlgorithmName.SHA512,
                _ => throw new InvalidOperationException()
            };
            using var hasher = IncrementalHash.CreateHash(algoName);
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), context.RequestAborted)) > 0)
            {
                hasher.AppendData(buffer.AsSpan(0, bytesRead));
                if (budgetMs > 0 && timer.ElapsedMilliseconds >= budgetMs)
                {
                    await WriteToolError(context, id, "Operation timed out");
                    return;
                }
            }
            computedHash = Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
        }

        var verified = string.Equals(computedHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        var sidecarFileName = Path.GetFileName(sidecarPath);

        var sb = new StringBuilder();
        sb.AppendLine($"File: {relativePath}");
        sb.AppendLine($"Sidecar: {sidecarFileName}");
        sb.AppendLine($"Algorithm: {algorithm}");
        sb.AppendLine($"Expected: {expectedHash}");
        sb.AppendLine($"Computed: {computedHash}");
        sb.AppendLine($"Verified: {verified}");

        await WriteToolResult(context, id, sb.ToString());
    }

    private static string? McpParseSidecarHash(string content, string fileName)
    {
        var lines = content.Split('\n', StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            var spaceIdx = line.IndexOfAny([' ', '\t']);
            if (spaceIdx > 0)
            {
                var hashPart = line[..spaceIdx];
                var filePart = line[(spaceIdx + 1)..].TrimStart(' ', '\t', '*');
                if (McpIsHexString(hashPart) &&
                    (filePart.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(filePart).Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    return hashPart;
                }
            }
        }

        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;

            var lastSpace = line.LastIndexOfAny([' ', '\t']);
            if (lastSpace > 0)
            {
                var hashPart = line[(lastSpace + 1)..].Trim();
                var filePart = line[..lastSpace].Trim();
                if (hashPart.Length == 8 && McpIsHexString(hashPart) &&
                    (filePart.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(filePart).Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    return hashPart;
                }
            }
        }

        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                continue;
            var trimmed = line.Trim();
            if (McpIsHexString(trimmed) && trimmed.Length >= 8)
                return trimmed;
        }

        return null;
    }

    private static bool McpIsHexString(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    private static async Task HandleToolGetDirectoryTree(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxDepth = DefaultTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
            {
                maxDepth = requested;
            }
        }

        var includeSizes = arguments?.TryGetProperty("includeSizes", out var sizesProp) == true
            && sizesProp.ValueKind == JsonValueKind.True;

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine(displayRoot);

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var entryCount = 0;
        var truncated = false;

        // Stack items: (physicalPath, depth, prefix, isLastChild)
        var stack = new Stack<(string PhysicalPath, int Depth, string Prefix, bool IsLast)>();

        // Seed with root children
        var rootChildren = GetFilteredChildren(new DirectoryInfo(physicalPath), listingService, options);
        for (var i = rootChildren.Count - 1; i >= 0; i--)
        {
            stack.Push((rootChildren[i].PhysicalPath, 1, "", i == rootChildren.Count - 1));
        }

        while (stack.Count > 0)
        {
            if (entryCount >= MaxTreeEntries || sw.ElapsedMilliseconds > timeBudgetMs)
            {
                truncated = true;
                break;
            }

            var (itemPath, depth, prefix, isLast) = stack.Pop();
            entryCount++;

            var connector = isLast ? "└── " : "├── ";
            var childPrefix = prefix + (isLast ? "    " : "│   ");

            var isDir = Directory.Exists(itemPath);
            var name = Path.GetFileName(itemPath);

            if (isDir)
            {
                if (includeSizes)
                {
                    // Calculate directory size inline by summing visible files
                    long dirSize = 0;
                    try
                    {
                        var dirInfo = new DirectoryInfo(itemPath);
                        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            try { dirSize += file.Length; } catch { /* skip inaccessible */ }
                        }
                    }
                    catch { /* skip inaccessible */ }
                    var sizeText = DirectoryListingService.HumanizeSize(dirSize);
                    sb.AppendLine($"{prefix}{connector}{name}/  ({sizeText})");
                }
                else
                {
                    sb.AppendLine($"{prefix}{connector}{name}/");
                }

                if (depth < maxDepth)
                {
                    var children = GetFilteredChildren(new DirectoryInfo(itemPath), listingService, options);
                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((children[i].PhysicalPath, depth + 1, childPrefix, i == children.Count - 1));
                    }
                }
            }
            else
            {
                if (includeSizes)
                {
                    try
                    {
                        var fi = new FileInfo(itemPath);
                        var sizeText = DirectoryListingService.HumanizeSize(fi.Length);
                        sb.AppendLine($"{prefix}{connector}{name}  ({sizeText})");
                    }
                    catch
                    {
                        sb.AppendLine($"{prefix}{connector}{name}");
                    }
                }
                else
                {
                    sb.AppendLine($"{prefix}{connector}{name}");
                }
            }
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine($"(truncated at {entryCount} entries)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolGroupTreeByType(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
            {
                maxDepth = requested;
            }
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var groups = new Dictionary<string, (int Count, long TotalSize)>(StringComparer.OrdinalIgnoreCase);
        long grandTotal = 0;
        var totalFiles = 0;

        // Stack-based traversal: (physicalPath, depth)
        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((physicalPath, 0));

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs) break;

            var (currentPath, depth) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs) break;

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else
                {
                    var ext = Path.GetExtension(child.Name);
                    var key = string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();

                    if (groups.TryGetValue(key, out var existing))
                    {
                        groups[key] = (existing.Count + 1, existing.TotalSize + child.Size);
                    }
                    else
                    {
                        groups[key] = (1, child.Size);
                    }

                    grandTotal += child.Size;
                    totalFiles++;
                }
            }
        }

        var sorted = groups.OrderByDescending(g => g.Value.TotalSize).ToList();

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"File type summary for {displayRoot}");
        sb.AppendLine($"Total: {totalFiles:N0} files, {DirectoryListingService.HumanizeSize(grandTotal)}");
        sb.AppendLine();

        foreach (var group in sorted)
        {
            var pct = grandTotal > 0 ? (double)group.Value.TotalSize / grandTotal * 100 : 0;
            sb.AppendLine($"  {group.Key,-20} {group.Value.Count,8:N0} files  {DirectoryListingService.HumanizeSize(group.Value.TotalSize),10}  ({pct:F1}%)");
        }

        if (sw.ElapsedMilliseconds > timeBudgetMs)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolGroupTreeByDate(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteToolError(context, id, "Path not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;

        var now = DateTime.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var daysSinceSunday = (int)today.DayOfWeek; // Sunday=0
        var weekStart = today.AddDays(-daysSinceSunday);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);

        var buckets = new (string Label, int Count, long Size)[]
        {
            ("Today", 0, 0),
            ("Yesterday", 0, 0),
            ("This week", 0, 0),
            ("This month", 0, 0),
            ("This year", 0, 0),
            ("Older", 0, 0)
        };

        var totalFiles = 0;
        long grandTotal = 0;

        // Stack-based traversal with unbounded depth
        var stack = new Stack<string>();
        stack.Push(physicalPath);

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs) break;

            var currentPath = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs) break;

                if (child.IsDirectory)
                {
                    stack.Push(child.PhysicalPath);
                }
                else
                {
                    var modDate = child.Modified.Date;
                    int bucket;
                    if (modDate >= today) bucket = 0;          // Today
                    else if (modDate >= yesterday) bucket = 1;  // Yesterday
                    else if (modDate >= weekStart) bucket = 2;  // This week
                    else if (modDate >= monthStart) bucket = 3; // This month
                    else if (modDate >= yearStart) bucket = 4;  // This year
                    else bucket = 5;                             // Older

                    buckets[bucket] = (buckets[bucket].Label, buckets[bucket].Count + 1, buckets[bucket].Size + child.Size);
                    grandTotal += child.Size;
                    totalFiles++;
                }
            }
        }

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"File age summary for {displayRoot}");
        sb.AppendLine($"Total: {totalFiles:N0} files, {DirectoryListingService.HumanizeSize(grandTotal)}");
        sb.AppendLine();

        foreach (var (label, count, size) in buckets)
        {
            if (count == 0) continue;
            var pct = grandTotal > 0 ? (double)size / grandTotal * 100 : 0;
            sb.AppendLine($"  {label,-15} {count,8:N0} files  {DirectoryListingService.HumanizeSize(size),10}  ({pct:F1}%)");
        }

        if (sw.ElapsedMilliseconds > timeBudgetMs)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolReadArchiveEntry(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.OpenArchivesInline)
        {
            await WriteToolError(context, id, "Archive browsing is disabled");
            return;
        }

        if (!options.AllowFileDownload)
        {
            await WriteToolError(context, id, "File downloads are disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var archiveBrowseService = context.RequestServices.GetRequiredService<ArchiveBrowseService>();

        var archivePath = arguments?.TryGetProperty("archivePath", out var archProp) == true ? archProp.GetString() : "";
        var entryPath = arguments?.TryGetProperty("entryPath", out var entryProp) == true ? entryProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(archivePath);

        var maxBytes = (int)options.McpReadFileSizeLimit;
        if (arguments?.TryGetProperty("maxBytes", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested < maxBytes)
            {
                maxBytes = requested;
            }
        }

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteToolError(context, id, "Archive path is required");
            return;
        }

        if (string.IsNullOrEmpty(entryPath))
        {
            await WriteToolError(context, id, "Entry path is required");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteToolError(context, id, "Archive not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteToolError(context, id, "File download blocked by policy");
            return;
        }

        if (!File.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Archive not found");
            return;
        }

        if (!archiveBrowseService.TryNormalizeVirtualPath(entryPath, out var normalizedEntry) || string.IsNullOrEmpty(normalizedEntry))
        {
            await WriteToolError(context, id, "Invalid entry path");
            return;
        }

        byte[] entryBytes;
        try
        {
            entryBytes = await archiveBrowseService.ReadEntryHeadBytesAsync(physicalPath, normalizedEntry, maxBytes, context.RequestAborted);
        }
        catch (FileNotFoundException)
        {
            await WriteToolError(context, id, "Entry not found in archive");
            return;
        }
        catch (NotSupportedException)
        {
            await WriteToolError(context, id, "Unsupported archive format");
            return;
        }

        string content;
        try
        {
            content = Encoding.UTF8.GetString(entryBytes);
        }
        catch
        {
            await WriteToolError(context, id, "Entry content is not valid UTF-8 text");
            return;
        }

        await WriteToolResult(context, id, content);
    }

    private static async Task HandleToolListArchiveEntries(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        if (!options.OpenArchivesInline)
        {
            await WriteToolError(context, id, "Archive browsing is disabled");
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var archiveBrowseService = context.RequestServices.GetRequiredService<ArchiveBrowseService>();

        var archivePath = arguments?.TryGetProperty("archivePath", out var archProp) == true ? archProp.GetString() : "";
        var virtualPath = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(archivePath);

        if (string.IsNullOrEmpty(relativePath))
        {
            await WriteToolError(context, id, "Archive path is required");
            return;
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            await WriteToolError(context, id, "Archive not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            await WriteToolError(context, id, "File not found");
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            await WriteToolError(context, id, "File download blocked by policy");
            return;
        }

        if (!File.Exists(physicalPath))
        {
            await WriteToolError(context, id, "Archive not found");
            return;
        }

        if (!archiveBrowseService.TryNormalizeVirtualPath(virtualPath, out var normalizedVirtualPath))
        {
            await WriteToolError(context, id, "Invalid path within archive");
            return;
        }

        ArchiveBrowseListing listing;
        try
        {
            listing = archiveBrowseService.BuildListing(physicalPath, normalizedVirtualPath, options.OperationTimeBudgetMs);
        }
        catch (InvalidDataException)
        {
            await WriteToolError(context, id, "Archive is invalid or unreadable");
            return;
        }
        catch (NotSupportedException)
        {
            await WriteToolError(context, id, "Unsupported archive format");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Archive: {relativePath}");
        sb.AppendLine($"Path: /{normalizedVirtualPath}");
        sb.AppendLine($"Items: {listing.Entries.Count}");
        sb.AppendLine();

        foreach (var entry in listing.Entries)
        {
            if (entry.IsDirectory)
            {
                sb.AppendLine($"  [DIR]  {entry.Name}/");
            }
            else
            {
                var size = DirectoryListingService.HumanizeSize(entry.Size);
                var modified = entry.ModifiedUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
                sb.AppendLine($"  [FILE] {entry.Name}  ({size}{(modified.Length > 0 ? ", " + modified : "")})");
            }
        }

        await WriteToolResult(context, id, sb.ToString());
    }
}
