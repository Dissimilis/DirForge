using System.Diagnostics;
using DirForge.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace DirForge.Services;

public sealed class DirectoryListingService
{
    internal const int SearchResultLimit = 200;

    private static readonly MemoryCacheEntryOptions ListingCacheEntryOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(2),
        SlidingExpiration = TimeSpan.FromSeconds(2)
    };

    private static readonly EnumerationOptions NonRecursiveOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    private readonly DirForgeOptions _options;
    private readonly IconResolver _iconResolver;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger _logger;
    private readonly string _rootWithSeparator;
    private readonly StringComparison _pathComparison;
    private readonly bool _ignorePathCase;
    private readonly string[][] _hidePathPatternSegments;
    private readonly HashSet<string> _deniedDownloadExtensions;

    public DirectoryListingService(
        DirForgeOptions options,
        IconResolver iconResolver,
        IMemoryCache memoryCache,
        ILogger<DirectoryListingService> logger)
    {
        _options = options;
        _iconResolver = iconResolver;
        _memoryCache = memoryCache;
        _logger = logger;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _ignorePathCase = _pathComparison == StringComparison.OrdinalIgnoreCase;
        _rootWithSeparator = _options.RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _options.RootPath
            : _options.RootPath + Path.DirectorySeparatorChar;
        _hidePathPatternSegments = _options.HidePathPatterns
            .Select(SplitPathSegments)
            .Where(static segments => segments.Length > 0)
            .ToArray();
        _deniedDownloadExtensions = new HashSet<string>(_options.DenyDownloadExtensions, StringComparer.OrdinalIgnoreCase);
    }

    // --- Path normalization and building ---

    public static string NormalizeRelativePath(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            return string.Empty;
        }

        return requestPath.Replace('\\', '/').Trim().Trim('/');
    }

    public static string BuildRequestPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return "/";
        }

        return "/" + relativePath + "/";
    }

    // --- Path resolution ---

    public string? ResolvePhysicalPath(string relativePath)
    {
        var segments = relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var combinedPath = segments.Length == 0
            ? _options.RootPath
            : Path.Combine(_options.RootPath, Path.Combine(segments));

        var fullPath = Path.GetFullPath(combinedPath);
        if (IsUnderRoot(fullPath) && ValidateSymlinkContainment(fullPath))
        {
            return fullPath;
        }

        return null;
    }

    public string? ResolveCanonicalPath(string fullPath)
    {
        var relative = Path.GetRelativePath(_options.RootPath, fullPath);
        if (relative == ".")
        {
            return _options.RootPath;
        }

        var current = _options.RootPath;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                var entry = new FileInfo(current);
                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var target = entry.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                {
                    continue;
                }

                var resolved = Path.GetFullPath(target.FullName);
                if (!IsUnderRoot(resolved))
                {
                    return null;
                }

                current = resolved;
            }
            catch (IOException)
            {
                break;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return current;
    }

    public bool ValidateSymlinkContainment(string fullPath)
    {
        return ResolveCanonicalPath(fullPath) is not null;
    }

    public bool IsCanonicallyWithinScope(string physicalPath, string scopeRelativePath)
    {
        var scopePhysicalPath = ResolvePhysicalPath(scopeRelativePath);
        if (scopePhysicalPath is null)
        {
            return false;
        }

        var canonicalPath = ResolveCanonicalPath(physicalPath);
        var canonicalScope = ResolveCanonicalPath(scopePhysicalPath);
        if (canonicalPath is null || canonicalScope is null)
        {
            return false;
        }

        if (canonicalPath.Equals(canonicalScope, _pathComparison))
        {
            return true;
        }

        var scopePrefix = canonicalScope.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalScope
            : canonicalScope + Path.DirectorySeparatorChar;
        return canonicalPath.StartsWith(scopePrefix, _pathComparison);
    }

    // --- Policy checks ---

    public bool IsPathHiddenByPolicy(string relativePath, bool isDirectory)
    {
        if (_hidePathPatternSegments.Length == 0)
        {
            return false;
        }

        var normalizedPath = NormalizePolicyPath(relativePath);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return false;
        }

        if (MatchesAnyHidePathPattern(normalizedPath))
        {
            return true;
        }

        return isDirectory && MatchesAnyHidePathPattern(normalizedPath + "/");
    }

    public bool IsPathHiddenByPolicyForFullPath(string fullPath, bool isDirectory)
    {
        var rootRelativePath = GetRootRelativePath(fullPath);
        return IsPathHiddenByPolicy(rootRelativePath, isDirectory);
    }

    public bool IsFileDownloadBlocked(string relativePath)
    {
        var normalizedPath = NormalizePolicyPath(relativePath);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var extension = IconResolver.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && _deniedDownloadExtensions.Contains(extension);
    }

    public string GetRootRelativePath(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_options.RootPath, fullPath).Replace('\\', '/');
        return NormalizePolicyPath(relativePath);
    }

    // --- Listing and enumeration ---

    public IEnumerable<string> EnumerateFilePathsRecursive(string directoryPath)
    {
        foreach (var info in EnumerateRecursive(new DirectoryInfo(directoryPath), includeDirectories: false))
        {
            yield return info.FullName;
        }
    }

    public IEnumerable<string> EnumerateFilePathsRecursive(string directoryPath, Stopwatch? timeBudget, int timeBudgetMs)
    {
        foreach (var info in EnumerateRecursive(new DirectoryInfo(directoryPath), includeDirectories: false, timeBudget, timeBudgetMs))
        {
            yield return info.FullName;
        }
    }

    public List<DirectoryEntry> ReadEntries(string directoryPath)
    {
        var dirInfo = new DirectoryInfo(directoryPath);
        var entries = new List<DirectoryEntry>();

        foreach (var info in dirInfo.EnumerateFileSystemInfos())
        {
            if (info.Name is "." or "..")
            {
                continue;
            }

            if (!ValidateSymlinkContainment(info.FullName))
            {
                continue;
            }

            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            if (IsPathHiddenByPolicyForFullPath(info.FullName, isDirectory))
            {
                continue;
            }

            if (_options.HideDotfiles && info.Name.StartsWith('.'))
            {
                continue;
            }

            entries.Add(CreateDirectoryEntry(info, info.Name, info.Name));
        }

        return entries;
    }

    public Dictionary<string, object> ComputeDirectorySizes(string physicalPath)
    {
        var result = new Dictionary<string, object>();
        var dirInfo = new DirectoryInfo(physicalPath);
        var budgetMs = _options.OperationTimeBudgetMs;
        Stopwatch? timer = budgetMs > 0 ? Stopwatch.StartNew() : null;

        foreach (var subDir in dirInfo.EnumerateDirectories())
        {
            if (timer is not null && timer.ElapsedMilliseconds >= budgetMs)
            {
                break;
            }

            if (!ValidateSymlinkContainment(subDir.FullName))
            {
                continue;
            }

            if (IsPathHiddenByPolicyForFullPath(subDir.FullName, isDirectory: true))
            {
                continue;
            }

            if (_options.HideDotfiles && subDir.Name.StartsWith('.'))
            {
                continue;
            }

            var size = CalculateDirectorySize(subDir, timer, budgetMs);
            result[subDir.Name] = new { size, humanSize = HumanizeSize(size), tooltip = $"{size:N0} bytes" };
        }

        return result;
    }

    public void EnrichWithDirectorySizes(List<DirectoryEntry> entries, string physicalPath)
    {
        var budgetMs = _options.OperationTimeBudgetMs;
        Stopwatch? timer = budgetMs > 0 ? Stopwatch.StartNew() : null;

        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                continue;
            }

            if (timer is not null && timer.ElapsedMilliseconds >= budgetMs)
            {
                break;
            }

            var dirPath = Path.Combine(physicalPath, entry.RelativePath);
            var size = CalculateDirectorySize(new DirectoryInfo(dirPath), timer, budgetMs);
            entry.Size = size;
            entry.HumanSize = HumanizeSize(size);
            entry.SizeTooltip = $"{size:N0} bytes";
        }
    }

    // --- Sorting and search ---

    public List<DirectoryEntry> GetSortedEntries(
        string physicalPath,
        string relativePath,
        SortMode sortMode,
        SortDirection sortDirection)
    {
        var directoryMtimeTicks = Directory.GetLastWriteTimeUtc(physicalPath).Ticks;
        var cacheKey = $"listing:{relativePath}:{(int)sortMode}:{(int)sortDirection}:{directoryMtimeTicks}";
        if (_memoryCache.TryGetValue<List<DirectoryEntry>>(cacheKey, out var cachedEntries))
        {
            return cachedEntries!;
        }

        var entries = ReadEntries(physicalPath);
        SortEntries(entries, sortMode, sortDirection);
        _memoryCache.Set(cacheKey, entries, ListingCacheEntryOptions);
        return entries;
    }

    public List<DirectoryEntry> GetSortedSearchEntries(
        string physicalPath,
        string relativePath,
        string searchQuery,
        SortMode sortMode,
        SortDirection sortDirection,
        int maxResults,
        out bool truncated)
    {
        var normalizedQuery = searchQuery.Trim().ToLowerInvariant();
        var directoryMtimeTicks = Directory.GetLastWriteTimeUtc(physicalPath).Ticks;
        var cacheKey = $"search:{relativePath}:{normalizedQuery}:{(int)sortMode}:{(int)sortDirection}:{maxResults}:{directoryMtimeTicks}";
        if (_memoryCache.TryGetValue<(List<DirectoryEntry> Entries, bool Truncated)>(cacheKey, out var cached))
        {
            truncated = cached.Truncated;
            return cached.Entries;
        }

        var entries = SearchEntriesRecursive(physicalPath, searchQuery.Trim(), maxResults, out truncated);
        SortEntries(entries, sortMode, sortDirection);
        _memoryCache.Set(cacheKey, (entries, truncated), ListingCacheEntryOptions);
        return entries;
    }

    public void SortEntries(List<DirectoryEntry> entries, SortMode sortMode, SortDirection sortDirection)
    {
        entries.Sort((left, right) => CompareEntries(left, right, sortMode, sortDirection));
    }

    public static SortMode GetSortMode(IQueryCollection query)
    {
        if (query.ContainsKey("name"))
        {
            return SortMode.Name;
        }

        if (query.ContainsKey("date"))
        {
            return SortMode.Date;
        }

        if (query.ContainsKey("size"))
        {
            return SortMode.Size;
        }

        return SortMode.Type;
    }

    public static SortDirection GetSortDirection(IQueryCollection query, SortMode sortMode)
    {
        var rawValue = query["dir"].ToString();
        if (rawValue.Equals("asc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Asc;
        }

        if (rawValue.Equals("desc", StringComparison.OrdinalIgnoreCase))
        {
            return SortDirection.Desc;
        }

        return GetDefaultSortDirection(sortMode);
    }

    public static SortDirection GetDefaultSortDirection(SortMode sortMode)
    {
        return sortMode is SortMode.Date or SortMode.Size
            ? SortDirection.Desc
            : SortDirection.Asc;
    }

    public static string ComputeETag(
        List<DirectoryEntry> entries,
        SortMode sortMode,
        SortDirection sortDirection,
        string relativePath,
        string? searchQuery = null)
    {
        var hash = new HashCode();
        hash.Add(relativePath, StringComparer.Ordinal);
        hash.Add((int)sortMode);
        hash.Add((int)sortDirection);
        hash.Add(searchQuery ?? string.Empty, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            hash.Add(entry.Name, StringComparer.Ordinal);
            hash.Add(entry.Size);
            hash.Add(entry.Modified.Ticks);
        }

        return $"\"{hash.ToHashCode():x8}\"";
    }

    internal static string HumanizeSize(long size)
    {
        if (size < 1024L)
        {
            return $"{size} B";
        }

        ReadOnlySpan<string> units = ["KB", "MB", "GB", "TB"];
        var unitIndex = 0;
        var value = size / 1024d;

        while (unitIndex < units.Length - 1 && value >= 1024d)
        {
            value /= 1024d;
            unitIndex++;
        }

        return value >= 100d
            ? $"{value:F0} {units[unitIndex]}"
            : value >= 10d
                ? $"{value:F1} {units[unitIndex]}"
                : $"{value:F2} {units[unitIndex]}";
    }

    internal static bool ContainsDotPathSegment(string relativePath)
    {
        var normalized = NormalizePolicyPath(relativePath);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('.'))
            {
                return true;
            }
        }

        return false;
    }

    // --- Private helpers ---

    private bool IsUnderRoot(string path)
    {
        return path.Equals(_options.RootPath, _pathComparison) ||
               path.StartsWith(_rootWithSeparator, _pathComparison);
    }

    private bool MatchesAnyHidePathPattern(string relativePath)
    {
        var pathSegments = SplitPathSegments(relativePath);
        foreach (var patternSegments in _hidePathPatternSegments)
        {
            if (GlobSegmentsMatch(patternSegments, pathSegments, _ignorePathCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool GlobSegmentsMatch(string[] patternSegments, string[] pathSegments, bool ignoreCase)
    {
        var memo = new Dictionary<(int PatternIndex, int PathIndex), bool>();
        return MatchRecursive(0, 0);

        bool MatchRecursive(int patternIndex, int pathIndex)
        {
            var key = (patternIndex, pathIndex);
            if (memo.TryGetValue(key, out var cached))
            {
                return cached;
            }

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
            else if (GlobMatcher.IsSimpleWildcardMatch(pathSegments[pathIndex], patternSegments[patternIndex], ignoreCase))
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

    private static string[] SplitPathSegments(string relativePath)
    {
        return NormalizePolicyPath(relativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizePolicyPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var normalized = relativePath.Replace('\\', '/').Trim();
        if (normalized == ".")
        {
            return string.Empty;
        }

        normalized = normalized.Trim('/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private long CalculateDirectorySize(DirectoryInfo directory, Stopwatch? timeBudget = null, int timeBudgetMs = 0)
    {
        long size = 0;
        try
        {
            foreach (var info in EnumerateRecursive(directory, includeDirectories: false, timeBudget, timeBudgetMs))
            {
                var file = (FileInfo)info;
                if (!ValidateSymlinkContainment(file.FullName))
                {
                    continue;
                }

                var rootRelativePath = GetRootRelativePath(file.FullName);
                if (_options.HideDotfiles && ContainsDotPathSegment(rootRelativePath))
                {
                    continue;
                }

                if (IsPathHiddenByPolicy(rootRelativePath, isDirectory: false))
                {
                    continue;
                }

                try
                {
                    size += file.Length;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogInformation(ex,
                        "Skipping file size read due to access denied: {FilePath}",
                        file.FullName);
                }
                catch (FileNotFoundException ex)
                {
                    _logger.LogInformation(ex,
                        "Skipping file size read because file no longer exists: {FilePath}",
                        file.FullName);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogInformation(ex,
                "Skipping directory size calculation due to access denied: {DirectoryPath}",
                directory.FullName);
        }

        return size;
    }

    private List<DirectoryEntry> SearchEntriesRecursive(
        string basePhysicalPath,
        string searchQuery,
        int maxResults,
        out bool truncated)
    {
        var entries = new List<DirectoryEntry>();
        truncated = false;
        var useWildcard = ContainsWildcard(searchQuery);
        var searchTimer = Stopwatch.StartNew();
        var visited = new HashSet<string>(
            _ignorePathCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var stack = new Stack<(DirectoryInfo Directory, int Depth)>();
        var rootDirectory = new DirectoryInfo(basePhysicalPath);
        if (TryMarkDirectoryVisited(rootDirectory, visited))
        {
            stack.Push((rootDirectory, 0));
        }

        while (stack.Count > 0)
        {
            if (searchTimer.ElapsedMilliseconds >= _options.OperationTimeBudgetMs)
            {
                truncated = true;
                break;
            }

            var (currentDirectory, currentDepth) = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = currentDirectory.EnumerateFileSystemInfos("*", NonRecursiveOptions);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                _logger.LogInformation(ex,
                    "Skipping directory during recursive search: {DirectoryPath}",
                    currentDirectory.FullName);
                continue;
            }

            foreach (var info in children)
            {
                if (searchTimer.ElapsedMilliseconds >= _options.OperationTimeBudgetMs)
                {
                    truncated = true;
                    return entries;
                }

                if (!ValidateSymlinkContainment(info.FullName))
                {
                    continue;
                }

                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                var rootRelativePath = GetRootRelativePath(info.FullName);

                if (IsPathHiddenByPolicy(rootRelativePath, isDirectory))
                {
                    continue;
                }

                if (_options.HideDotfiles && ContainsDotPathSegment(rootRelativePath))
                {
                    continue;
                }

                if (MatchesSearch(info.Name, searchQuery, useWildcard))
                {
                    var relativePath = Path.GetRelativePath(basePhysicalPath, info.FullName)
                        .Replace('\\', '/');
                    entries.Add(CreateDirectoryEntry(info, relativePath, relativePath));

                    if (entries.Count >= maxResults)
                    {
                        truncated = true;
                        return entries;
                    }
                }

                if (!isDirectory || info is not DirectoryInfo subDirectory)
                {
                    continue;
                }

                if (currentDepth >= _options.SearchMaxDepth)
                {
                    truncated = true;
                    continue;
                }

                if (TryMarkDirectoryVisited(subDirectory, visited))
                {
                    stack.Push((subDirectory, currentDepth + 1));
                }
            }
        }

        return entries;
    }

    private IEnumerable<FileSystemInfo> EnumerateRecursive(DirectoryInfo root, bool includeDirectories)
    {
        return EnumerateRecursive(root, includeDirectories, null, 0);
    }

    private IEnumerable<FileSystemInfo> EnumerateRecursive(
        DirectoryInfo root, bool includeDirectories, Stopwatch? timeBudget, int timeBudgetMs)
    {
        var visited = new HashSet<string>(
            _ignorePathCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var stack = new Stack<DirectoryInfo>();

        if (TryMarkDirectoryVisited(root, visited))
        {
            stack.Push(root);
        }

        while (stack.Count > 0)
        {
            if (timeBudget is not null && timeBudget.ElapsedMilliseconds >= timeBudgetMs)
            {
                yield break;
            }

            var current = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = current.EnumerateFileSystemInfos("*", NonRecursiveOptions);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                _logger.LogInformation(ex,
                    "Skipping directory during recursive enumeration: {DirectoryPath}",
                    current.FullName);
                continue;
            }

            foreach (var child in children)
            {
                if (timeBudget is not null && timeBudget.ElapsedMilliseconds >= timeBudgetMs)
                {
                    yield break;
                }

                if ((child.Attributes & FileAttributes.Directory) != 0)
                {
                    if (includeDirectories)
                    {
                        yield return child;
                    }

                    if (child is DirectoryInfo subDir && TryMarkDirectoryVisited(subDir, visited))
                    {
                        stack.Push(subDir);
                    }
                }
                else
                {
                    yield return child;
                }
            }
        }
    }

    private bool TryMarkDirectoryVisited(DirectoryInfo dir, HashSet<string> visited)
    {
        var canonicalPath = ResolveCanonicalPath(dir.FullName);
        return canonicalPath is not null && visited.Add(canonicalPath);
    }

    private static bool MatchesSearch(string value, string searchQuery, bool useWildcard)
    {
        if (useWildcard)
        {
            return GlobMatcher.IsSimpleWildcardMatch(value, searchQuery, ignoreCase: true);
        }

        return value.Contains(searchQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWildcard(string query)
    {
        return query.Contains('*') || query.Contains('?');
    }

    private static int CompareEntries(DirectoryEntry left, DirectoryEntry right, SortMode sortMode, SortDirection sortDirection)
    {
        var comparison = CompareDirectoriesFirst(left, right);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = sortMode switch
        {
            SortMode.Name => CompareByName(left, right),
            SortMode.Date => CompareByDate(left, right),
            SortMode.Size => CompareBySize(left, right),
            _ => CompareByType(left, right)
        };
        if (sortDirection != GetDefaultSortDirection(sortMode))
        {
            comparison *= -1;
        }

        if (comparison != 0)
        {
            return comparison;
        }

        return CompareByName(left, right);
    }

    private static int CompareDirectoriesFirst(DirectoryEntry left, DirectoryEntry right)
    {
        if (left.IsDirectory == right.IsDirectory)
        {
            return 0;
        }

        return left.IsDirectory ? -1 : 1;
    }

    private static int CompareByName(DirectoryEntry left, DirectoryEntry right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Name, right.Name);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Extension, right.Extension);
        if (comparison != 0)
        {
            return comparison;
        }

        return right.Size.CompareTo(left.Size);
    }

    private static int CompareByDate(DirectoryEntry left, DirectoryEntry right)
    {
        var comparison = right.Modified.CompareTo(left.Modified);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Type, right.Type);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Extension, right.Extension);
        if (comparison != 0)
        {
            return comparison;
        }

        return right.Size.CompareTo(left.Size);
    }

    private static int CompareBySize(DirectoryEntry left, DirectoryEntry right)
    {
        var comparison = right.Size.CompareTo(left.Size);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Type, right.Type);
        if (comparison != 0)
        {
            return comparison;
        }

        return StringComparer.Ordinal.Compare(left.Extension, right.Extension);
    }

    private static int CompareByType(DirectoryEntry left, DirectoryEntry right)
    {
        var comparison = StringComparer.Ordinal.Compare(left.Type, right.Type);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Extension, right.Extension);
        if (comparison != 0)
        {
            return comparison;
        }

        return right.Size.CompareTo(left.Size);
    }

    private DirectoryEntry CreateDirectoryEntry(FileSystemInfo info, string displayName, string relativePath)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        long size;
        string humanSize;
        string sizeTooltip;

        if (isDirectory)
        {
            size = 0;
            humanSize = "-";
            sizeTooltip = "Directory";
        }
        else
        {
            size = ((FileInfo)info).Length;
            humanSize = HumanizeSize(size);
            sizeTooltip = $"{size:N0} bytes";
        }

        var type = _iconResolver.ResolveType(info.FullName, isDirectory, size);
        return new DirectoryEntry
        {
            Name = displayName,
            RelativePath = relativePath,
            Extension = IconResolver.GetExtension(info.Name),
            Type = type,
            IconPath = _iconResolver.ResolveIconPath(info.Name, type),
            IsDirectory = isDirectory,
            Size = size,
            HumanSize = humanSize,
            SizeTooltip = sizeTooltip,
            Modified = info.LastWriteTime,
            ModifiedString = info.LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss")
        };
    }
}
