using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DirForge.Models;

namespace DirForge.Services;

public static partial class McpEndpoints
{
    private static async Task HandleToolFindLargest(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxResults = DefaultFindResults;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxFindResults)
            {
                maxResults = requested;
            }
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
            {
                maxDepth = requested;
            }
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;

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
        var files = new List<(string RelativePath, long Size, DateTime Modified)>();
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

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                        if (!MatchesAnyPattern(dirRelPath, child.Name, exclude))
                            stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else
                {
                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                    if (!string.IsNullOrWhiteSpace(include) && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;
                    files.Add((fileRelPath, child.Size, child.Modified));
                }
            }
        }

        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        var results = files.Count > maxResults ? files.GetRange(0, maxResults) : files;

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"Largest files in {displayRoot}");
        sb.AppendLine($"Found: {results.Count} of {files.Count:N0} files");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var (filePath, size, modified) = results[i];
            sb.AppendLine($"  {i + 1,3}.  {DirectoryListingService.HumanizeSize(size),10}  {modified:yyyy-MM-dd HH:mm}  {filePath}");
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolFindRecent(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxResults = DefaultFindResults;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxFindResults)
            {
                maxResults = requested;
            }
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
            {
                maxDepth = requested;
            }
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;

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
        var files = new List<(string RelativePath, long Size, DateTime Modified)>();
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

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                        if (!MatchesAnyPattern(dirRelPath, child.Name, exclude))
                            stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else
                {
                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                    if (!string.IsNullOrWhiteSpace(include) && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;
                    files.Add((fileRelPath, child.Size, child.Modified));
                }
            }
        }

        files.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        var results = files.Count > maxResults ? files.GetRange(0, maxResults) : files;

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"Most recently modified files in {displayRoot}");
        sb.AppendLine($"Found: {results.Count} of {files.Count:N0} files");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var (filePath, size, modified) = results[i];
            sb.AppendLine($"  {i + 1,3}.  {modified:yyyy-MM-dd HH:mm}  {DirectoryListingService.HumanizeSize(size),10}  {filePath}");
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolCompareDirectories(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path1 = arguments?.TryGetProperty("path1", out var p1Prop) == true ? p1Prop.GetString() : "";
        var path2 = arguments?.TryGetProperty("path2", out var p2Prop) == true ? p2Prop.GetString() : "";

        if (string.IsNullOrWhiteSpace(path1) || string.IsNullOrWhiteSpace(path2))
        {
            await WriteToolError(context, id, "Both path1 and path2 are required");
            return;
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
            {
                maxDepth = requested;
            }
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;

        // Validate path1
        var rel1 = DirectoryListingService.NormalizeRelativePath(path1);
        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(rel1))
        {
            await WriteToolError(context, id, "path1 not found");
            return;
        }
        var phys1 = listingService.ResolvePhysicalPath(rel1);
        if (phys1 is null || !Directory.Exists(phys1))
        {
            await WriteToolError(context, id, "path1 directory not found");
            return;
        }
        if (listingService.IsPathHiddenByPolicy(rel1, isDirectory: true))
        {
            await WriteToolError(context, id, "path1 not found");
            return;
        }

        // Validate path2
        var rel2 = DirectoryListingService.NormalizeRelativePath(path2);
        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(rel2))
        {
            await WriteToolError(context, id, "path2 not found");
            return;
        }
        var phys2 = listingService.ResolvePhysicalPath(rel2);
        if (phys2 is null || !Directory.Exists(phys2))
        {
            await WriteToolError(context, id, "path2 directory not found");
            return;
        }
        if (listingService.IsPathHiddenByPolicy(rel2, isDirectory: true))
        {
            await WriteToolError(context, id, "path2 not found");
            return;
        }

        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var halfBudget = timeBudgetMs / 2;
        var truncated = false;

        var entries1 = CollectDirectoryEntries(phys1, maxDepth, halfBudget, sw, listingService, options, out var trunc1);
        var entries2 = CollectDirectoryEntries(phys2, maxDepth, timeBudgetMs, sw, listingService, options, out var trunc2);
        truncated = trunc1 || trunc2;

        // Apply include/exclude filters to collected entries
        if (!string.IsNullOrWhiteSpace(include) || !string.IsNullOrWhiteSpace(exclude))
        {
            FilterEntries(entries1, include, exclude);
            FilterEntries(entries2, include, exclude);
        }

        var onlyLeft = new List<(string Path, bool IsDir, long Size)>();
        var onlyRight = new List<(string Path, bool IsDir, long Size)>();
        var different = new List<(string Path, bool IsDir, long Size1, long Size2, bool SizeDiffers, bool DateDiffers)>();
        var identicalCount = 0;

        foreach (var kvp in entries1)
        {
            if (entries2.TryGetValue(kvp.Key, out var other))
            {
                var sizeDiffers = kvp.Value.Size != other.Size;
                var dateDiffers = Math.Abs((kvp.Value.Modified - other.Modified).TotalSeconds) > 2;
                if (sizeDiffers || dateDiffers)
                {
                    different.Add((kvp.Key, kvp.Value.IsDir, kvp.Value.Size, other.Size, sizeDiffers, dateDiffers));
                }
                else
                {
                    identicalCount++;
                }
            }
            else
            {
                onlyLeft.Add((kvp.Key, kvp.Value.IsDir, kvp.Value.Size));
            }
        }

        foreach (var kvp in entries2)
        {
            if (!entries1.ContainsKey(kvp.Key))
            {
                onlyRight.Add((kvp.Key, kvp.Value.IsDir, kvp.Value.Size));
            }
        }

        onlyLeft.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        onlyRight.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        different.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        var display1 = string.IsNullOrEmpty(rel1) ? "/" : $"/{rel1}";
        var display2 = string.IsNullOrEmpty(rel2) ? "/" : $"/{rel2}";
        sb.AppendLine($"Comparing {display1} vs {display2}");
        sb.AppendLine();

        if (onlyLeft.Count > 0)
        {
            sb.AppendLine($"Only in {display1} ({onlyLeft.Count} items):");
            foreach (var (entryPath, isDir, size) in onlyLeft)
            {
                if (isDir)
                    sb.AppendLine($"  [DIR]  {entryPath}/");
                else
                    sb.AppendLine($"  [FILE] {entryPath}  ({DirectoryListingService.HumanizeSize(size)})");
            }
            sb.AppendLine();
        }

        if (onlyRight.Count > 0)
        {
            sb.AppendLine($"Only in {display2} ({onlyRight.Count} items):");
            foreach (var (entryPath, isDir, size) in onlyRight)
            {
                if (isDir)
                    sb.AppendLine($"  [DIR]  {entryPath}/");
                else
                    sb.AppendLine($"  [FILE] {entryPath}  ({DirectoryListingService.HumanizeSize(size)})");
            }
            sb.AppendLine();
        }

        if (different.Count > 0)
        {
            sb.AppendLine($"Different ({different.Count} items):");
            foreach (var (entryPath, isDir, size1, size2, sizeDiffers, dateDiffers) in different)
            {
                if (isDir)
                {
                    sb.AppendLine($"  [DIR]  {entryPath}/");
                }
                else if (sizeDiffers)
                {
                    sb.AppendLine($"  [FILE] {entryPath}  ({DirectoryListingService.HumanizeSize(size1)} vs {DirectoryListingService.HumanizeSize(size2)})");
                }
                else
                {
                    sb.AppendLine($"  [FILE] {entryPath}  (same size, different dates)");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Summary: {onlyLeft.Count} only-left, {onlyRight.Count} only-right, {different.Count} different, {identicalCount} identical");

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static void FilterEntries(Dictionary<string, (bool IsDir, long Size, DateTime Modified)> entries, string? include, string? exclude)
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in entries)
        {
            var name = kvp.Key.Contains('/') ? kvp.Key[(kvp.Key.LastIndexOf('/') + 1)..] : kvp.Key;
            if (kvp.Value.IsDir)
            {
                if (MatchesAnyPattern(kvp.Key, name, exclude))
                    keysToRemove.Add(kvp.Key);
            }
            else
            {
                if (MatchesAnyPattern(kvp.Key, name, exclude))
                    keysToRemove.Add(kvp.Key);
                else if (!string.IsNullOrWhiteSpace(include) && !MatchesAnyPattern(kvp.Key, name, include))
                    keysToRemove.Add(kvp.Key);
            }
        }
        foreach (var key in keysToRemove)
            entries.Remove(key);
    }

    private static async Task HandleToolFindPotentialDuplicates(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        long minSize = 1;
        if (arguments?.TryGetProperty("minSize", out var minProp) == true && minProp.ValueKind == JsonValueKind.Number)
        {
            var requested = minProp.GetInt64();
            if (requested > 0)
            {
                minSize = requested;
            }
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
            {
                maxDepth = requested;
            }
        }

        var maxResults = DefaultFindResults;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxFindResults)
            {
                maxResults = requested;
            }
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;

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

        // Phase 1: Enumerate files
        var sw = Stopwatch.StartNew();
        var timeBudgetMs = options.OperationTimeBudgetMs;
        var files = new List<(string RelativePath, long Size, string PhysicalPath)>();
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

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                        if (!MatchesAnyPattern(dirRelPath, child.Name, exclude))
                            stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else if (child.Size >= minSize)
                {
                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                    if (!string.IsNullOrWhiteSpace(include) && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;
                    files.Add((fileRelPath, child.Size, child.PhysicalPath));
                    if (files.Count >= MaxDuplicateScanFiles)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            if (files.Count >= MaxDuplicateScanFiles)
                break;
        }

        var totalScanned = files.Count;

        // Phase 2: Group by size, then hash candidates
        var sizeGroups = files
            .GroupBy(f => f.Size)
            .Where(g => g.Count() >= 2)
            .ToList();

        var candidateCount = sizeGroups.Sum(g => g.Count());
        var duplicateGroups = new List<(long Size, string Hash, List<string> Paths)>();

        foreach (var sizeGroup in sizeGroups)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs)
            {
                truncated = true;
                break;
            }

            var hashGroups = new Dictionary<string, List<string>>();

            foreach (var (relPath, size, physPath) in sizeGroup)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs)
                {
                    truncated = true;
                    break;
                }

                var hash = await ComputeSmartHashAsync(physPath, sizeGroup.Key, context.RequestAborted);
                if (hash is null)
                    continue;

                if (!hashGroups.TryGetValue(hash, out var group))
                {
                    group = new List<string>();
                    hashGroups[hash] = group;
                }
                group.Add(relPath);
            }

            foreach (var kvp in hashGroups)
            {
                if (kvp.Value.Count >= 2)
                {
                    duplicateGroups.Add((sizeGroup.Key, kvp.Key, kvp.Value));
                }
            }
        }

        // Phase 3: Format output
        duplicateGroups.Sort((a, b) =>
        {
            var wastedA = a.Size * (a.Paths.Count - 1);
            var wastedB = b.Size * (b.Paths.Count - 1);
            return wastedB.CompareTo(wastedA);
        });

        if (duplicateGroups.Count > maxResults)
            duplicateGroups = duplicateGroups.GetRange(0, maxResults);

        long totalWasted = 0;
        foreach (var g in duplicateGroups)
            totalWasted += g.Size * (g.Paths.Count - 1);

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"Potential duplicate files in {displayRoot}");
        sb.AppendLine($"Scanned: {totalScanned:N0} files | Candidates (shared sizes): {candidateCount:N0} | Duplicate groups: {duplicateGroups.Count:N0}");
        sb.AppendLine($"Total potentially wasted space: {DirectoryListingService.HumanizeSize(totalWasted)}");

        for (var i = 0; i < duplicateGroups.Count; i++)
        {
            var (size, hash, paths) = duplicateGroups[i];
            var wasted = size * (paths.Count - 1);
            sb.AppendLine();
            sb.AppendLine($"Group {i + 1} — {paths.Count} files x {DirectoryListingService.HumanizeSize(size)} (wasted: {DirectoryListingService.HumanizeSize(wasted)})");
            foreach (var p in paths)
            {
                sb.AppendLine($"  /{p}");
            }
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time or file limit exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }


    private static async Task HandleToolSearchBySize(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        long? minSize = null;
        long? maxSize = null;
        if (arguments?.TryGetProperty("minSize", out var minProp) == true && minProp.ValueKind == JsonValueKind.Number)
            minSize = minProp.GetInt64();
        if (arguments?.TryGetProperty("maxSize", out var maxProp2) == true && maxProp2.ValueKind == JsonValueKind.Number)
            maxSize = maxProp2.GetInt64();

        if (minSize is null && maxSize is null)
        {
            await WriteToolError(context, id, "At least one of 'minSize' or 'maxSize' is required.");
            return;
        }

        var maxResults = DefaultFindResults;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxFindResults)
                maxResults = requested;
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
                maxDepth = requested;
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;

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
        var files = new List<(string RelativePath, long Size, DateTime Modified)>();
        var totalMatching = 0;
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

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                        if (!MatchesAnyPattern(dirRelPath, child.Name, exclude))
                            stack.Push((child.PhysicalPath, depth + 1));
                    }
                }
                else
                {
                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                    if (!string.IsNullOrWhiteSpace(include) && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;
                    if (minSize.HasValue && child.Size < minSize.Value) continue;
                    if (maxSize.HasValue && child.Size > maxSize.Value) continue;

                    totalMatching++;
                    files.Add((fileRelPath, child.Size, child.Modified));
                }
            }
        }

        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        var results = files.Count > maxResults ? files.GetRange(0, maxResults) : files;

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        var rangeDesc = (minSize, maxSize) switch
        {
            (not null, not null) => $"between {DirectoryListingService.HumanizeSize(minSize.Value)} and {DirectoryListingService.HumanizeSize(maxSize.Value)}",
            (not null, null) => $"larger than {DirectoryListingService.HumanizeSize(minSize.Value)}",
            (null, not null) => $"smaller than {DirectoryListingService.HumanizeSize(maxSize.Value)}",
            _ => ""
        };
        sb.AppendLine($"Files {rangeDesc} in {displayRoot}");
        sb.AppendLine($"Found: {results.Count} of {totalMatching:N0} matching files");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var (filePath, size, modified) = results[i];
            sb.AppendLine($"  {i + 1,3}.  {DirectoryListingService.HumanizeSize(size),10}  {modified:yyyy-MM-dd HH:mm}  {filePath}");
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolDiskUsageSummary(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
                maxDepth = requested;
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;
        var hasFilter = !string.IsNullOrWhiteSpace(include);

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
        var truncated = false;

        long totalSize = 0;
        var totalFiles = 0;
        var totalDirs = 0;

        // Track per-immediate-subdirectory aggregates
        var subdirSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var subdirNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<(string PhysicalPath, int Depth, string? ImmediateSubdir)>();

        // Seed with immediate children to track which subdirectory each file belongs to
        var rootChildren = GetFilteredChildren(new DirectoryInfo(physicalPath), listingService, options);
        foreach (var child in rootChildren)
        {
            if (child.IsDirectory)
            {
                var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                if (MatchesAnyPattern(dirRelPath, child.Name, exclude)) continue;

                totalDirs++;
                subdirSizes[child.PhysicalPath] = 0;
                subdirNames[child.PhysicalPath] = child.Name;

                if (1 < maxDepth)
                    stack.Push((child.PhysicalPath, 1, child.PhysicalPath));
            }
            else
            {
                var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                if (hasFilter && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;

                totalFiles++;
                totalSize += child.Size;
            }
        }

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > timeBudgetMs)
            {
                truncated = true;
                break;
            }

            var (currentPath, depth, immediateSubdir) = stack.Pop();
            var children = GetFilteredChildren(new DirectoryInfo(currentPath), listingService, options);

            foreach (var child in children)
            {
                if (sw.ElapsedMilliseconds > timeBudgetMs)
                {
                    truncated = true;
                    break;
                }

                if (child.IsDirectory)
                {
                    var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(dirRelPath, child.Name, exclude)) continue;

                    totalDirs++;
                    if (depth < maxDepth)
                        stack.Push((child.PhysicalPath, depth + 1, immediateSubdir));
                }
                else
                {
                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                    if (hasFilter && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;

                    totalFiles++;
                    totalSize += child.Size;
                    if (immediateSubdir != null)
                        subdirSizes[immediateSubdir] = subdirSizes.GetValueOrDefault(immediateSubdir) + child.Size;
                }
            }
        }

        var topSubdirs = subdirSizes
            .Select(kvp => (Name: subdirNames[kvp.Key], Size: kvp.Value))
            .OrderByDescending(d => d.Size)
            .Take(10)
            .ToList();

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        if (hasFilter)
            sb.AppendLine($"Disk usage summary for {displayRoot} (filtered: {include})");
        else
            sb.AppendLine($"Disk usage summary for {displayRoot}");

        var sizeLabel = hasFilter ? "Matching size" : "Total size";
        var filesLabel = hasFilter ? "Matching files" : "Files";
        sb.AppendLine($"  {sizeLabel}:  {DirectoryListingService.HumanizeSize(totalSize),10}");
        sb.AppendLine($"  {filesLabel}:  {totalFiles,10:N0}");
        sb.AppendLine($"  Directories:  {totalDirs,10:N0}");
        sb.AppendLine();

        if (topSubdirs.Count > 0)
        {
            var subdirLabel = hasFilter ? "Top subdirectories (by matching files):" : "Top subdirectories:";
            sb.AppendLine(subdirLabel);
            for (var i = 0; i < topSubdirs.Count; i++)
            {
                var (name, size) = topSubdirs[i];
                sb.AppendLine($"  {i + 1,3}.  {DirectoryListingService.HumanizeSize(size),10}  {name}");
            }
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolFindLargestDirectories(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var maxResults = DefaultFindResults;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxFindResults)
                maxResults = requested;
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
                maxDepth = requested;
        }

        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;

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
        var truncated = false;

        // Two-pass: first collect all file sizes per directory, then aggregate
        // dirSizes maps physical directory path -> total recursive file size
        var dirSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var dirModified = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var dirRelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

                if (child.IsDirectory)
                {
                    var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(dirRelPath, child.Name, exclude)) continue;

                    dirSizes[child.PhysicalPath] = 0;
                    dirModified[child.PhysicalPath] = child.Modified;
                    dirRelPaths[child.PhysicalPath] = dirRelPath;

                    if (depth < maxDepth)
                        stack.Push((child.PhysicalPath, depth + 1));
                }
                else
                {
                    // Add file size to all ancestor directories we're tracking
                    var fileDir = Path.GetDirectoryName(child.PhysicalPath);
                    while (fileDir != null && dirSizes.ContainsKey(fileDir))
                    {
                        dirSizes[fileDir] += child.Size;
                        fileDir = Path.GetDirectoryName(fileDir);
                    }
                    // Also add to the search root if current file is a direct child
                    // (the root itself isn't tracked in dirSizes)
                }
            }
        }

        var dirs = dirSizes
            .Select(kvp => (RelativePath: dirRelPaths[kvp.Key], Size: kvp.Value, Modified: dirModified[kvp.Key]))
            .OrderByDescending(d => d.Size)
            .Take(maxResults)
            .ToList();

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
        sb.AppendLine($"Largest directories in {displayRoot}");
        sb.AppendLine($"Found: {dirs.Count} of {dirSizes.Count:N0} directories");
        sb.AppendLine();

        for (var i = 0; i < dirs.Count; i++)
        {
            var (dirPath, size, modified) = dirs[i];
            sb.AppendLine($"  {i + 1,3}.  {DirectoryListingService.HumanizeSize(size),10}  {modified:yyyy-MM-dd HH:mm}  {dirPath}");
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }

    private static async Task HandleToolSearchByDate(HttpContext context, JsonElement? id, JsonElement? arguments, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var path = arguments?.TryGetProperty("path", out var pathProp) == true ? pathProp.GetString() : "";
        var relativePath = DirectoryListingService.NormalizeRelativePath(path);

        var afterStr = arguments?.TryGetProperty("after", out var afterProp) == true ? afterProp.GetString() : null;
        var beforeStr = arguments?.TryGetProperty("before", out var beforeProp) == true ? beforeProp.GetString() : null;

        if (string.IsNullOrWhiteSpace(afterStr) && string.IsNullOrWhiteSpace(beforeStr))
        {
            await WriteToolError(context, id, "At least one of 'after' or 'before' is required");
            return;
        }

        DateTime? after = null;
        DateTime? before = null;

        if (!string.IsNullOrWhiteSpace(afterStr))
        {
            if (!DateTime.TryParse(afterStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                await WriteToolError(context, id, $"Invalid 'after' date: {afterStr}");
                return;
            }
            after = parsed;
        }

        if (!string.IsNullOrWhiteSpace(beforeStr))
        {
            if (!DateTime.TryParse(beforeStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                await WriteToolError(context, id, $"Invalid 'before' date: {beforeStr}");
                return;
            }
            before = parsed;
        }

        var maxResults = DefaultFindResults;
        if (arguments?.TryGetProperty("maxResults", out var maxProp) == true && maxProp.ValueKind == JsonValueKind.Number)
        {
            var requested = maxProp.GetInt32();
            if (requested > 0 && requested <= MaxFindResults)
                maxResults = requested;
        }

        var maxDepth = MaxTreeDepth;
        if (arguments?.TryGetProperty("maxDepth", out var depthProp) == true && depthProp.ValueKind == JsonValueKind.Number)
        {
            var requested = depthProp.GetInt32();
            if (requested >= 1 && requested <= MaxTreeDepth)
                maxDepth = requested;
        }

        var include = arguments?.TryGetProperty("include", out var inclProp) == true ? inclProp.GetString() : null;
        var exclude = arguments?.TryGetProperty("exclude", out var exclProp) == true ? exclProp.GetString() : null;
        var includeDirectories = arguments?.TryGetProperty("includeDirectories", out var inclDirProp) == true
            && inclDirProp.ValueKind == JsonValueKind.True;

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
        var entries = new List<(string RelativePath, long Size, DateTime Modified, bool IsDir)>();
        var totalScanned = 0;
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

                if (child.IsDirectory)
                {
                    if (depth < maxDepth)
                    {
                        var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                        if (!MatchesAnyPattern(dirRelPath, child.Name, exclude))
                            stack.Push((child.PhysicalPath, depth + 1));
                    }

                    if (includeDirectories)
                    {
                        var dirRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                        if (MatchesAnyPattern(dirRelPath, child.Name, exclude)) continue;
                        totalScanned++;
                        if ((after.HasValue && child.Modified < after.Value) ||
                            (before.HasValue && child.Modified >= before.Value))
                            continue;
                        entries.Add((dirRelPath, child.Size, child.Modified, true));
                    }
                }
                else
                {
                    var fileRelPath = listingService.GetRootRelativePath(child.PhysicalPath);
                    if (MatchesAnyPattern(fileRelPath, child.Name, exclude)) continue;
                    if (!string.IsNullOrWhiteSpace(include) && !MatchesAnyPattern(fileRelPath, child.Name, include)) continue;
                    totalScanned++;
                    if ((after.HasValue && child.Modified < after.Value) ||
                        (before.HasValue && child.Modified >= before.Value))
                        continue;
                    entries.Add((fileRelPath, child.Size, child.Modified, false));
                }
            }
        }

        entries.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        var results = entries.Count > maxResults ? entries.GetRange(0, maxResults) : entries;

        var sb = new StringBuilder();
        var displayRoot = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";

        var rangeDesc = (after, before) switch
        {
            (not null, not null) => $"between {after.Value:yyyy-MM-dd} and {before.Value:yyyy-MM-dd}",
            (not null, null) => $"after {after.Value:yyyy-MM-dd}",
            (null, not null) => $"before {before.Value:yyyy-MM-dd}",
            _ => ""
        };
        var entryType = includeDirectories ? "entries" : "files";

        sb.AppendLine($"Files modified {rangeDesc} in {displayRoot}");
        sb.AppendLine($"Found: {results.Count} of {entries.Count:N0} matching {entryType} (scanned {totalScanned:N0})");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var (entryPath, size, modified, isDir) = results[i];
            var sizeStr = isDir ? "     <DIR>" : DirectoryListingService.HumanizeSize(size).PadLeft(10);
            sb.AppendLine($"  {i + 1,3}.  {modified:yyyy-MM-dd HH:mm}  {sizeStr}  {entryPath}");
        }

        if (truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(results may be incomplete — time budget exceeded)");
        }

        await WriteToolResult(context, id, sb.ToString());
    }
}
