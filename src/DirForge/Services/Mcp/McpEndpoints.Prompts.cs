using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DirForge.Models;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    private static async Task HandlePromptsListAsync(HttpContext context, JsonElement? id, DirForgeOptions options)
    {
        var prompts = new List<object>
        {
            new
            {
                name = "storage_report",
                description = "Generate a comprehensive storage health report for a directory, including file type breakdown, largest files, recent activity, and duplicate analysis.",
                arguments = new[]
                {
                    new { name = "path", description = "Directory path to analyze. Empty for root.", required = false }
                }
            },
            new
            {
                name = "cleanup_candidates",
                description = "Identify files that are likely safe to delete: empty files, temporary files, orphaned sidecars, and duplicates. Returns candidates ranked by confidence.",
                arguments = new[]
                {
                    new { name = "path", description = "Directory path to scan. Empty for root.", required = false }
                }
            },
            new
            {
                name = "organize_suggestions",
                description = "Analyze a directory's contents and suggest how to reorganize files into a cleaner structure based on file types, dates, and naming patterns.",
                arguments = new[]
                {
                    new { name = "path", description = "Directory path to analyze. Empty for root.", required = false }
                }
            }
        };

        await WriteJsonRpcResultAsync(context, id, new { prompts });
    }

    private static async Task HandlePromptsGetAsync(
        HttpContext context, JsonElement? id, JsonElement? @params, DirForgeOptions options)
    {
        var promptName = @params?.TryGetProperty("name", out var nameProp) == true ? nameProp.GetString() : null;
        var arguments = @params?.TryGetProperty("arguments", out var argsProp) == true ? argsProp : (JsonElement?)null;

        if (string.IsNullOrWhiteSpace(promptName))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Missing prompt 'name'");
            return;
        }

        switch (promptName)
        {
            case "storage_report":
                await HandlePromptStorageReport(context, id, arguments, options);
                break;
            case "cleanup_candidates":
                await HandlePromptCleanupCandidates(context, id, arguments, options);
                break;
            case "organize_suggestions":
                await HandlePromptOrganizeSuggestions(context, id, arguments, options);
                break;
            default:
                await WriteJsonRpcErrorAsync(context, id, -32602, $"Unknown prompt: {promptName}");
                break;
        }
    }

    private static async Task HandlePromptStorageReport(
        HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var truncated = false;

        // Collect all files
        var files = new List<(string RelativePath, long Size, DateTime Modified, string Extension)>();
        var dirCount = 0;
        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((physicalPath, 0));

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs) { truncated = true; break; }
            var (currentPath, depth) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs) { truncated = true; break; }

                if (child.IsDirectory)
                {
                    dirCount++;
                    if (depth < MaxTreeDepth) stack.Push((child.PhysicalPath, depth + 1));
                }
                else
                {
                    var ext = Path.GetExtension(child.Name).ToLowerInvariant();
                    var relPath = Path.GetRelativePath(physicalPath, child.PhysicalPath).Replace('\\', '/');
                    files.Add((relPath, child.Size, child.Modified, ext));
                }
            }
        }

        var totalSize = files.Sum(f => f.Size);
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";

        // Build type breakdown
        var typeGroups = files
            .GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(no extension)" : f.Extension)
            .Select(g => new { Extension = g.Key, Count = g.Count(), TotalSize = g.Sum(f => f.Size) })
            .OrderByDescending(g => g.TotalSize)
            .Take(15)
            .ToList();

        // Largest files
        var largest = files.OrderByDescending(f => f.Size).Take(10).ToList();

        // Recent activity
        var recent = files.OrderByDescending(f => f.Modified).Take(10).ToList();

        // Date buckets
        var now = DateTime.UtcNow;
        var today = now.Date;
        var thisWeek = today.AddDays(-7);
        var thisMonth = today.AddDays(-30);
        var thisYear = today.AddDays(-365);

        var todayCount = files.Count(f => f.Modified.Date == today);
        var weekCount = files.Count(f => f.Modified >= thisWeek && f.Modified.Date != today);
        var monthCount = files.Count(f => f.Modified >= thisMonth && f.Modified < thisWeek);
        var yearCount = files.Count(f => f.Modified >= thisYear && f.Modified < thisMonth);
        var olderCount = files.Count(f => f.Modified < thisYear);

        // Potential duplicates (by size)
        var sizeGroups = files.GroupBy(f => f.Size).Where(g => g.Count() >= 2 && g.Key > 0).ToList();
        var dupeCandidateCount = sizeGroups.Sum(g => g.Count());
        var dupeWastedEstimate = sizeGroups.Sum(g => g.Key * (g.Count() - 1));

        var data = new StringBuilder();
        data.AppendLine($"# Storage Report for {displayRoot}");
        data.AppendLine();
        data.AppendLine("## Overview");
        data.AppendLine($"- Total files: {files.Count:N0}");
        data.AppendLine($"- Total directories: {dirCount:N0}");
        data.AppendLine($"- Total size: {DirectoryListingService.HumanizeSize(totalSize)}");
        data.AppendLine();

        data.AppendLine("## File Types (top 15 by size)");
        foreach (var g in typeGroups)
            data.AppendLine($"- {g.Extension}: {g.Count:N0} files, {DirectoryListingService.HumanizeSize(g.TotalSize)}");
        data.AppendLine();

        data.AppendLine("## Largest Files (top 10)");
        foreach (var f in largest)
            data.AppendLine($"- {f.RelativePath} ({DirectoryListingService.HumanizeSize(f.Size)})");
        data.AppendLine();

        data.AppendLine("## Recent Activity (10 most recent)");
        foreach (var f in recent)
            data.AppendLine($"- {f.Modified:yyyy-MM-dd HH:mm} — {f.RelativePath} ({DirectoryListingService.HumanizeSize(f.Size)})");
        data.AppendLine();

        data.AppendLine("## Activity Timeline");
        data.AppendLine($"- Today: {todayCount:N0} files");
        data.AppendLine($"- This week: {weekCount:N0} files");
        data.AppendLine($"- This month: {monthCount:N0} files");
        data.AppendLine($"- This year: {yearCount:N0} files");
        data.AppendLine($"- Older: {olderCount:N0} files");
        data.AppendLine();

        data.AppendLine("## Duplicate Estimate");
        data.AppendLine($"- Files sharing identical sizes: {dupeCandidateCount:N0}");
        data.AppendLine($"- Estimated wasted space (upper bound): {DirectoryListingService.HumanizeSize(dupeWastedEstimate)}");
        data.AppendLine("- Run the `find_potential_duplicates` tool for hash-verified results.");

        if (truncated)
        {
            data.AppendLine();
            data.AppendLine("(report may be incomplete — time budget exceeded)");
        }

        var result = new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new
                    {
                        type = "text",
                        text = $"Here is the storage data for {displayRoot}. Please analyze it and provide a concise health assessment with actionable recommendations.\n\n{data}"
                    }
                }
            }
        };

        await WriteJsonRpcResultAsync(context, id, result);
    }

    private static async Task HandlePromptCleanupCandidates(
        HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var truncated = false;

        var tempExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".tmp", ".temp", ".bak", ".old", ".orig", ".swp", ".swo",
            ".partial", ".crdownload", ".part", ".download"
        };

        var emptyFiles = new List<string>();
        var tempFiles = new List<(string Path, long Size, string Extension)>();
        var allFiles = new List<(string Path, long Size)>();
        var thumbsDbFiles = new List<string>();

        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((physicalPath, 0));

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs) { truncated = true; break; }
            var (currentPath, depth) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs) { truncated = true; break; }

                if (child.IsDirectory)
                {
                    if (depth < MaxTreeDepth) stack.Push((child.PhysicalPath, depth + 1));
                    continue;
                }

                var relPath = Path.GetRelativePath(physicalPath, child.PhysicalPath).Replace('\\', '/');
                var ext = Path.GetExtension(child.Name).ToLowerInvariant();
                var nameLower = child.Name.ToLowerInvariant();

                allFiles.Add((relPath, child.Size));

                if (child.Size == 0)
                    emptyFiles.Add(relPath);

                if (tempExtensions.Contains(ext))
                    tempFiles.Add((relPath, child.Size, ext));

                if (nameLower is "thumbs.db" or "desktop.ini" or ".ds_store" or "._*")
                    thumbsDbFiles.Add(relPath);
            }
        }

        // Duplicate candidates by size
        var sizeGroups = allFiles
            .GroupBy(f => f.Size)
            .Where(g => g.Count() >= 2 && g.Key > 0)
            .OrderByDescending(g => g.Key * (g.Count() - 1))
            .Take(10)
            .ToList();

        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        var data = new StringBuilder();
        data.AppendLine($"# Cleanup Candidates for {displayRoot}");
        data.AppendLine();
        data.AppendLine($"Total files scanned: {allFiles.Count:N0}");
        data.AppendLine();

        if (emptyFiles.Count > 0)
        {
            data.AppendLine($"## Empty Files ({emptyFiles.Count}) — HIGH confidence");
            foreach (var f in emptyFiles.Take(50))
                data.AppendLine($"- {f}");
            if (emptyFiles.Count > 50)
                data.AppendLine($"  ... and {emptyFiles.Count - 50} more");
            data.AppendLine();
        }

        if (tempFiles.Count > 0)
        {
            var totalTempSize = tempFiles.Sum(f => f.Size);
            data.AppendLine($"## Temporary/Partial Files ({tempFiles.Count}, {DirectoryListingService.HumanizeSize(totalTempSize)}) — MEDIUM confidence");
            foreach (var (fPath, size, ext) in tempFiles.Take(50))
                data.AppendLine($"- {fPath} ({DirectoryListingService.HumanizeSize(size)})");
            if (tempFiles.Count > 50)
                data.AppendLine($"  ... and {tempFiles.Count - 50} more");
            data.AppendLine();
        }

        if (thumbsDbFiles.Count > 0)
        {
            data.AppendLine($"## OS Metadata Files ({thumbsDbFiles.Count}) — HIGH confidence");
            foreach (var f in thumbsDbFiles.Take(50))
                data.AppendLine($"- {f}");
            if (thumbsDbFiles.Count > 50)
                data.AppendLine($"  ... and {thumbsDbFiles.Count - 50} more");
            data.AppendLine();
        }

        if (sizeGroups.Count > 0)
        {
            data.AppendLine("## Potential Duplicate Groups (by size, top 10) — LOW confidence");
            data.AppendLine("(Use `find_potential_duplicates` tool to verify with hashing)");
            foreach (var g in sizeGroups)
            {
                var wasted = g.Key * (g.Count() - 1);
                data.AppendLine($"- {g.Count()} files x {DirectoryListingService.HumanizeSize(g.Key)} (potential waste: {DirectoryListingService.HumanizeSize(wasted)})");
                foreach (var (fPath, _) in g.Take(5))
                    data.AppendLine($"    {fPath}");
                if (g.Count() > 5)
                    data.AppendLine($"    ... and {g.Count() - 5} more");
            }
            data.AppendLine();
        }

        if (emptyFiles.Count == 0 && tempFiles.Count == 0 && thumbsDbFiles.Count == 0 && sizeGroups.Count == 0)
        {
            data.AppendLine("No obvious cleanup candidates found. The directory appears clean.");
        }

        if (truncated)
        {
            data.AppendLine();
            data.AppendLine("(scan may be incomplete — time budget exceeded)");
        }

        var result = new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new
                    {
                        type = "text",
                        text = $"Here are cleanup candidates for {displayRoot}. Please review them, rank by safety/confidence, and provide recommendations on what to delete.\n\n{data}"
                    }
                }
            }
        };

        await WriteJsonRpcResultAsync(context, id, result);
    }

    private static async Task HandlePromptOrganizeSuggestions(
        HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Directory not found");
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            await WriteJsonRpcErrorAsync(context, id, -32602, "Path not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var truncated = false;

        var files = new List<(string RelativePath, string Name, long Size, DateTime Modified, string Extension, bool IsDirectory)>();
        var stack = new Stack<(string PhysicalPath, int Depth)>();
        stack.Push((physicalPath, 0));

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs) { truncated = true; break; }
            var (currentPath, depth) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs) { truncated = true; break; }

                var relPath = Path.GetRelativePath(physicalPath, child.PhysicalPath).Replace('\\', '/');
                var ext = Path.GetExtension(child.Name).ToLowerInvariant();
                files.Add((relPath, child.Name, child.Size, child.Modified, ext, child.IsDirectory));

                if (child.IsDirectory && depth < MaxTreeDepth)
                    stack.Push((child.PhysicalPath, depth + 1));
            }
        }

        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";

        // Analyze the structure
        var fileEntries = files.Where(f => !f.IsDirectory).ToList();
        var dirEntries = files.Where(f => f.IsDirectory).ToList();

        // Type breakdown
        var typeGroups = fileEntries
            .GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(no extension)" : f.Extension)
            .Select(g => new { Extension = g.Key, Count = g.Count(), TotalSize = g.Sum(f => f.Size) })
            .OrderByDescending(g => g.Count)
            .Take(20)
            .ToList();

        // Files in root (not in subdirectories)
        var rootFiles = fileEntries.Where(f => !f.RelativePath.Contains('/')).ToList();

        // Depth analysis
        var depthGroups = fileEntries
            .GroupBy(f => f.RelativePath.Count(c => c == '/'))
            .OrderBy(g => g.Key)
            .ToList();

        // Date ranges
        var dateGroups = fileEntries
            .GroupBy(f => f.Modified.Year)
            .OrderBy(g => g.Key)
            .ToList();

        // Naming patterns (common prefixes in flat directories)
        var commonPrefixes = rootFiles
            .Where(f => f.Name.Contains('_') || f.Name.Contains('-'))
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f.Name);
                var sep = name.IndexOfAny(['_', '-']);
                return sep > 1 ? name[..sep].ToLowerInvariant() : null;
            })
            .Where(p => p != null)
            .GroupBy(p => p!)
            .Where(g => g.Count() >= 3)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToList();

        var data = new StringBuilder();
        data.AppendLine($"# Directory Structure Analysis for {displayRoot}");
        data.AppendLine();
        data.AppendLine("## Current Layout");
        data.AppendLine($"- Files: {fileEntries.Count:N0}");
        data.AppendLine($"- Subdirectories: {dirEntries.Count:N0}");
        data.AppendLine($"- Files in root level: {rootFiles.Count:N0}");
        data.AppendLine($"- Total size: {DirectoryListingService.HumanizeSize(fileEntries.Sum(f => f.Size))}");
        data.AppendLine();

        data.AppendLine("## File Types");
        foreach (var g in typeGroups)
            data.AppendLine($"- {g.Extension}: {g.Count} files ({DirectoryListingService.HumanizeSize(g.TotalSize)})");
        data.AppendLine();

        data.AppendLine("## Depth Distribution");
        foreach (var g in depthGroups)
            data.AppendLine($"- Depth {g.Key}: {g.Count()} files");
        data.AppendLine();

        if (dateGroups.Count > 1)
        {
            data.AppendLine("## Files by Year");
            foreach (var g in dateGroups)
                data.AppendLine($"- {g.Key}: {g.Count()} files");
            data.AppendLine();
        }

        if (commonPrefixes.Count > 0)
        {
            data.AppendLine("## Common Naming Prefixes (in root)");
            foreach (var g in commonPrefixes)
                data.AppendLine($"- \"{g.Key}\": {g.Count()} files");
            data.AppendLine();
        }

        // Existing subdirectories
        if (dirEntries.Count > 0)
        {
            data.AppendLine("## Existing Subdirectories");
            var topDirs = dirEntries.Where(d => !d.RelativePath.Contains('/')).Take(30).ToList();
            foreach (var d in topDirs)
            {
                var filesInDir = fileEntries.Count(f => f.RelativePath.StartsWith(d.RelativePath + "/", StringComparison.OrdinalIgnoreCase));
                data.AppendLine($"- {d.Name}/ ({filesInDir} files)");
            }
            data.AppendLine();
        }

        // Sample of root-level files
        if (rootFiles.Count > 0)
        {
            data.AppendLine($"## Root-level Files (sample, {Math.Min(rootFiles.Count, 30)} of {rootFiles.Count})");
            foreach (var f in rootFiles.Take(30))
                data.AppendLine($"- {f.Name} ({DirectoryListingService.HumanizeSize(f.Size)}, {f.Modified:yyyy-MM-dd})");
            data.AppendLine();
        }

        if (truncated)
        {
            data.AppendLine("(analysis may be incomplete — time budget exceeded)");
        }

        var result = new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new
                    {
                        type = "text",
                        text = $"Here is the structure of {displayRoot}. Please suggest how to reorganize these files into a cleaner directory layout. Consider grouping by type, date, naming patterns, and project. Provide concrete 'move from → to' suggestions.\n\n{data}"
                    }
                }
            }
        };

        await WriteJsonRpcResultAsync(context, id, result);
    }
}
