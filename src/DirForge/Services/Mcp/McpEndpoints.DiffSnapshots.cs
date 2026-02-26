using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DirForge.Models;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    // Snapshot token format: base64url(gzip(json manifest))
    // Manifest: { "path": "...", "entries": { "relative/path": "size:mtime_ticks", ... } }
    // This is fully self-contained — no server state needed.

    private const int MaxSnapshotEntries = 50_000;

    private static async Task HandleToolDiffSnapshots(
        HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);
        var previousToken = arguments?.TryGetProperty("previousToken", out var tokenProp) == true ? tokenProp.GetString() : null;

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
                maxDepth = requested;
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

        // Build current snapshot
        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var currentEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;

        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((physicalPath, 0));

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

                var entryRelPath = Path.GetRelativePath(physicalPath, child.PhysicalPath).Replace('\\', '/');

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                        stack.Push((child.PhysicalPath, depth + 1));
                }
                else
                {
                    currentEntries[entryRelPath] = $"{child.Size}:{child.Modified.Ticks}";
                    if (currentEntries.Count >= MaxSnapshotEntries)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            if (currentEntries.Count >= MaxSnapshotEntries)
                break;
        }

        // Encode current snapshot as token
        var currentToken = EncodeSnapshotToken(relativePath, currentEntries);

        if (string.IsNullOrWhiteSpace(previousToken))
        {
            // First call — return the token only
            var sb = new StringBuilder();
            var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
            sb.AppendLine($"Snapshot captured for {displayRoot}");
            sb.AppendLine($"Files indexed: {currentEntries.Count:N0}");
            sb.AppendLine();
            sb.AppendLine("Pass the token below as 'previousToken' on the next call to detect changes.");
            sb.AppendLine();
            sb.AppendLine($"token: {currentToken}");

            if (truncated)
            {
                sb.AppendLine();
                sb.AppendLine("(snapshot may be incomplete — time or entry limit exceeded)");
            }

            await WriteToolResult(context, id, sb.ToString());
            return;
        }

        // Decode previous snapshot
        Dictionary<string, string>? previousEntries;
        string? previousPath;
        try
        {
            (previousPath, previousEntries) = DecodeSnapshotToken(previousToken);
        }
        catch
        {
            await WriteToolError(context, id, "Invalid or corrupted previousToken");
            return;
        }

        if (!string.Equals(previousPath, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            await WriteToolError(context, id, $"Token was created for '/{previousPath}' but current path is '/{relativePath}'. Tokens are path-specific.");
            return;
        }

        // Compute diff
        var added = new List<(string Path, long Size, DateTime Modified)>();
        var removed = new List<(string Path, long Size, DateTime Modified)>();
        var modified = new List<(string Path, long OldSize, long NewSize, DateTime OldModified, DateTime NewModified)>();
        var unchangedCount = 0;

        foreach (var kvp in currentEntries)
        {
            if (previousEntries.TryGetValue(kvp.Key, out var oldValue))
            {
                if (kvp.Value != oldValue)
                {
                    ParseEntryValue(kvp.Value, out var newSize, out var newMod);
                    ParseEntryValue(oldValue, out var oldSize, out var oldMod);
                    modified.Add((kvp.Key, oldSize, newSize, oldMod, newMod));
                }
                else
                {
                    unchangedCount++;
                }
            }
            else
            {
                ParseEntryValue(kvp.Value, out var size, out var mod);
                added.Add((kvp.Key, size, mod));
            }
        }

        foreach (var kvp in previousEntries)
        {
            if (!currentEntries.ContainsKey(kvp.Key))
            {
                ParseEntryValue(kvp.Value, out var size, out var mod);
                removed.Add((kvp.Key, size, mod));
            }
        }

        added.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        removed.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        modified.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        // Format output
        var output = new StringBuilder();
        var display = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        output.AppendLine($"Changes detected in {display}");
        output.AppendLine($"Added: {added.Count} | Removed: {removed.Count} | Modified: {modified.Count} | Unchanged: {unchangedCount}");

        if (added.Count == 0 && removed.Count == 0 && modified.Count == 0)
        {
            output.AppendLine();
            output.AppendLine("No changes detected.");
        }

        if (added.Count > 0)
        {
            output.AppendLine();
            output.AppendLine($"Added ({added.Count}):");
            foreach (var (entryPath, size, mod) in added)
                output.AppendLine($"  + {entryPath}  ({DirectoryListingService.HumanizeSize(size)}, {mod:yyyy-MM-dd HH:mm})");
        }

        if (removed.Count > 0)
        {
            output.AppendLine();
            output.AppendLine($"Removed ({removed.Count}):");
            foreach (var (entryPath, size, mod) in removed)
                output.AppendLine($"  - {entryPath}  ({DirectoryListingService.HumanizeSize(size)}, {mod:yyyy-MM-dd HH:mm})");
        }

        if (modified.Count > 0)
        {
            output.AppendLine();
            output.AppendLine($"Modified ({modified.Count}):");
            foreach (var (entryPath, oldSize, newSize, oldMod, newMod) in modified)
            {
                var sizeChange = oldSize != newSize
                    ? $"{DirectoryListingService.HumanizeSize(oldSize)} → {DirectoryListingService.HumanizeSize(newSize)}"
                    : DirectoryListingService.HumanizeSize(newSize);
                output.AppendLine($"  ~ {entryPath}  ({sizeChange}, {oldMod:yyyy-MM-dd HH:mm} → {newMod:yyyy-MM-dd HH:mm})");
            }
        }

        output.AppendLine();
        output.AppendLine("Updated token (use as 'previousToken' for next diff):");
        output.AppendLine($"token: {currentToken}");

        if (truncated)
        {
            output.AppendLine();
            output.AppendLine("(snapshot may be incomplete — time or entry limit exceeded)");
        }

        await WriteToolResult(context, id, output.ToString());
    }

    private static string EncodeSnapshotToken(string path, Dictionary<string, string> entries)
    {
        var manifest = new Dictionary<string, object>
        {
            ["p"] = path,
            ["e"] = entries
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);

        using var compressedStream = new MemoryStream();
        using (var gzip = new GZipStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return Base64UrlEncode(compressedStream.ToArray());
    }

    private static (string Path, Dictionary<string, string> Entries) DecodeSnapshotToken(string token)
    {
        var compressed = Base64UrlDecode(token);

        using var compressedStream = new MemoryStream(compressed);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var decompressedStream = new MemoryStream();
        gzip.CopyTo(decompressedStream);

        var json = decompressedStream.ToArray();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var path = root.GetProperty("p").GetString() ?? "";
        var entriesElement = root.GetProperty("e");
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in entriesElement.EnumerateObject())
        {
            entries[prop.Name] = prop.Value.GetString() ?? "";
        }

        return (path, entries);
    }

    private static void ParseEntryValue(string value, out long size, out DateTime modified)
    {
        var colonIndex = value.IndexOf(':');
        size = long.Parse(value.AsSpan(0, colonIndex));
        var ticks = long.Parse(value.AsSpan(colonIndex + 1));
        modified = new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string encoded)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
