using System.Diagnostics;
using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace DirForge.Pages;

public sealed class DirectoryActionHandlers
{
    private readonly DirectoryListingService _directoryListingService;
    private readonly DirForgeOptions _options;
    private readonly DashboardMetricsService _dashboardMetrics;
    private readonly DirectoryRequestGuards _guards;

    public DirectoryActionHandlers(
        DirectoryListingService directoryListingService,
        DirForgeOptions options,
        DashboardMetricsService dashboardMetrics,
        DirectoryRequestGuards guards)
    {
        _directoryListingService = directoryListingService;
        _options = options;
        _dashboardMetrics = dashboardMetrics;
        _guards = guards;
    }

    public DirectoryListingPageResult HandleGet(HttpContext httpContext, string? requestPath, ShareAccessContext? shareContext)
    {
        var request = httpContext.Request;
        var response = httpContext.Response;

        var shareAccessActive = shareContext is not null;
        var shareFolderAccessActive = shareContext?.Mode == ShareMode.Directory;
        var shareControlsEnabled = _options.EnableSharing && !shareAccessActive;
        var shareToken = shareContext?.Token ?? string.Empty;
        var shareHiddenInputToken = shareToken;
        var shareQuerySuffix = string.IsNullOrEmpty(shareToken)
            ? string.Empty
            : $"?{ShareLinkService.TokenQueryParameter}={Uri.EscapeDataString(shareToken)}";
        var shareActionSuffix = string.IsNullOrEmpty(shareToken)
            ? string.Empty
            : $"&{ShareLinkService.TokenQueryParameter}={Uri.EscapeDataString(shareToken)}";

        if (!_guards.TryResolvePhysicalPath(httpContext, requestPath, out var relativePath, out var physicalPath))
        {
            return DirectoryListingPageResult.FromResult(new StatusCodeResult(StatusCodes.Status403Forbidden));
        }

        var sortMode = DirectoryListingService.GetSortMode(request.Query);
        var sortDirection = DirectoryListingService.GetSortDirection(request.Query, sortMode);
        var currentSortKey = GetSortQueryKey(sortMode);
        var currentSortDirectionKey = GetSortDirectionQueryKey(sortDirection);
        var searchEnabled = _options.EnableSearch;
        var dashboardEnabled = _options.DashboardEnabled;
        var searchQuery = searchEnabled ? request.Query["q"].ToString().Trim() : string.Empty;
        var searchActive = searchEnabled && !string.IsNullOrWhiteSpace(searchQuery);

        if (_guards.IsDirectoryShareScopeViolation(shareContext, physicalPath))
        {
            return DirectoryListingPageResult.FromResult(_guards.CreateShareScopeForbidden(httpContext));
        }

        var physicalDirectoryExists = Directory.Exists(physicalPath);
        if (_directoryListingService.IsPathHiddenByPolicy(relativePath, isDirectory: physicalDirectoryExists))
        {
            return DirectoryListingPageResult.FromResult(new NotFoundResult());
        }

        var fileInfo = new FileInfo(physicalPath);
        if (fileInfo.Exists)
        {
            if (!_guards.IsFileDownloadAllowed(relativePath))
            {
                return DirectoryListingPageResult.FromResult(_guards.CreateBlockedExtensionNotFound(httpContext));
            }

            var contentType = DirectoryListingModel.GetContentType(fileInfo.Name);
            response.Headers[HeaderNames.ContentDisposition] = DirectoryListingModel.BuildContentDisposition("inline", fileInfo.Name);
            _dashboardMetrics.RecordFileDownload(fileInfo.Length);
            return DirectoryListingPageResult.FromResult(new PhysicalFileResult(physicalPath, contentType)
            {
                EnableRangeProcessing = true
            });
        }

        if (!Directory.Exists(physicalPath))
        {
            return DirectoryListingPageResult.FromResult(new NotFoundResult());
        }

        List<DirectoryEntry> entries;
        var searchTruncated = false;
        try
        {
            if (searchActive)
            {
                var searchStartTimestamp = Stopwatch.GetTimestamp();
                entries = _directoryListingService.GetSortedSearchEntries(
                    physicalPath,
                    relativePath,
                    searchQuery,
                    sortMode,
                    sortDirection,
                    DirectoryListingService.SearchResultLimit,
                    out searchTruncated);
                var searchElapsedMs = (long)Math.Round(
                    Stopwatch.GetElapsedTime(searchStartTimestamp).TotalMilliseconds,
                    MidpointRounding.AwayFromZero);
                _dashboardMetrics.RecordSearch(searchElapsedMs);
            }
            else
            {
                entries = _directoryListingService.GetSortedEntries(physicalPath, relativePath, sortMode, sortDirection);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return DirectoryListingPageResult.FromResult(new StatusCodeResult(StatusCodes.Status403Forbidden));
        }

        ApplyCachingHeaders(
            request,
            response,
            entries,
            sortMode,
            sortDirection,
            relativePath,
            searchQuery,
            searchActive,
            shareControlsEnabled,
            physicalPath,
            out var notModifiedResult);

        if (notModifiedResult is not null)
        {
            return DirectoryListingPageResult.FromResult(notModifiedResult);
        }

        if (_options.CalculateDirectorySizes && !searchActive)
        {
            _directoryListingService.EnrichWithDirectorySizes(entries, physicalPath);
        }

        var state = BuildPageState(
            request,
            entries,
            relativePath,
            sortMode,
            sortDirection,
            currentSortKey,
            currentSortDirectionKey,
            searchEnabled,
            dashboardEnabled,
            searchActive,
            searchQuery,
            searchTruncated,
            shareAccessActive,
            shareFolderAccessActive,
            shareControlsEnabled,
            shareToken,
            shareHiddenInputToken,
            shareQuerySuffix,
            shareActionSuffix);

        return DirectoryListingPageResult.FromState(state);
    }

    private DirectoryListingPageState BuildPageState(
        HttpRequest request,
        IEnumerable<DirectoryEntry> entries,
        string relativePath,
        SortMode sortMode,
        SortDirection sortDirection,
        string currentSortKey,
        string currentSortDirectionKey,
        bool searchEnabled,
        bool dashboardEnabled,
        bool searchActive,
        string searchQuery,
        bool searchTruncated,
        bool shareAccessActive,
        bool shareFolderAccessActive,
        bool shareControlsEnabled,
        string shareToken,
        string shareHiddenInputToken,
        string shareQuerySuffix,
        string shareActionSuffix)
    {
        return new DirectoryListingPageState
        {
            Entries = entries,
            CurrentRelativePath = relativePath,
            CurrentRequestPath = DirectoryListingService.BuildRequestPath(relativePath),
            CurrentSortKey = currentSortKey,
            CurrentSortDirectionKey = currentSortDirectionKey,
            OrderSuffix = BuildQuerySuffix(currentSortKey, currentSortDirectionKey, searchActive ? searchQuery : null, shareToken),
            TypeSortSuffix = BuildQuerySuffix(
                "type",
                GetSortDirectionQueryKey(GetNextSortDirection(sortMode, sortDirection, SortMode.Type)),
                searchActive ? searchQuery : null,
                shareToken),
            NameSortSuffix = BuildQuerySuffix(
                "name",
                GetSortDirectionQueryKey(GetNextSortDirection(sortMode, sortDirection, SortMode.Name)),
                searchActive ? searchQuery : null,
                shareToken),
            DateSortSuffix = BuildQuerySuffix(
                "date",
                GetSortDirectionQueryKey(GetNextSortDirection(sortMode, sortDirection, SortMode.Date)),
                searchActive ? searchQuery : null,
                shareToken),
            SizeSortSuffix = BuildQuerySuffix(
                "size",
                GetSortDirectionQueryKey(GetNextSortDirection(sortMode, sortDirection, SortMode.Size)),
                searchActive ? searchQuery : null,
                shareToken),
            ClearSearchSuffix = BuildQuerySuffix(currentSortKey, currentSortDirectionKey, null, shareToken),
            SearchEnabled = searchEnabled,
            DashboardEnabled = dashboardEnabled,
            SearchActive = searchActive,
            SearchTruncated = searchTruncated,
            SearchQuery = searchQuery,
            ShareAccessActive = shareAccessActive,
            ShareFolderAccessActive = shareFolderAccessActive,
            ShareControlsEnabled = shareControlsEnabled,
            ShareToken = shareToken,
            ShareHiddenInputToken = shareHiddenInputToken,
            ShareQuerySuffix = shareQuerySuffix,
            ShareActionSuffix = shareActionSuffix,
            DefaultTheme = _options.DefaultTheme,
            CalculateDirectorySizes = _options.CalculateDirectorySizes,
            AllowFileDownload = _options.AllowFileDownload,
            AllowFolderDownload = _options.AllowFolderDownload,
            OpenArchivesInline = _options.OpenArchivesInline,
            SiteTitle = _options.SiteTitle,
            PageTitle = (_options.SiteTitle ?? request.Host.ToString()) + " :: " + UriHelper.GetEncodedPathAndQuery(request)
        };
    }

    private static void ApplyCachingHeaders(
        HttpRequest request,
        HttpResponse response,
        List<DirectoryEntry> entries,
        SortMode sortMode,
        SortDirection sortDirection,
        string relativePath,
        string? searchQuery,
        bool searchActive,
        bool shareControlsEnabled,
        string physicalPath,
        out IActionResult? notModifiedResult)
    {
        notModifiedResult = null;
        var typedRequestHeaders = request.GetTypedHeaders();
        var typedResponseHeaders = response.GetTypedHeaders();
        var listingLastModified = DirectoryListingModel.TruncateToWholeSeconds(DirectoryListingModel.GetListingLastModifiedUtc(entries, physicalPath));

        if (shareControlsEnabled)
        {
            response.Headers.CacheControl = "no-cache, no-store";
            response.Headers.Pragma = "no-cache";
            return;
        }

        var etag = DirectoryListingService.ComputeETag(entries, sortMode, sortDirection, relativePath, searchActive ? searchQuery : null);
        var ifNoneMatch = typedRequestHeaders.IfNoneMatch;
        var hasIfNoneMatch = ifNoneMatch is { Count: > 0 };
        response.Headers.CacheControl = "no-cache";
        response.Headers.ETag = etag;
        typedResponseHeaders.LastModified = listingLastModified;

        if (hasIfNoneMatch && ifNoneMatch!.Any(tag => tag == EntityTagHeaderValue.Any || string.Equals(tag.Tag.Value, etag, StringComparison.Ordinal)))
        {
            notModifiedResult = new StatusCodeResult(StatusCodes.Status304NotModified);
            return;
        }

        if (!hasIfNoneMatch && typedRequestHeaders.IfModifiedSince is DateTimeOffset ifModifiedSince)
        {
            var ifModifiedSinceUtc = DirectoryListingModel.TruncateToWholeSeconds(ifModifiedSince.ToUniversalTime());
            if (listingLastModified <= ifModifiedSinceUtc)
            {
                notModifiedResult = new StatusCodeResult(StatusCodes.Status304NotModified);
            }
        }
    }

    private static string GetSortQueryKey(SortMode sortMode)
    {
        return sortMode switch
        {
            SortMode.Name => "name",
            SortMode.Date => "date",
            SortMode.Size => "size",
            _ => "type"
        };
    }

    private static string GetSortDirectionQueryKey(SortDirection sortDirection)
    {
        return sortDirection == SortDirection.Desc ? "desc" : "asc";
    }

    private static SortDirection GetNextSortDirection(SortMode currentSortMode, SortDirection currentSortDirection, SortMode targetSortMode)
    {
        if (currentSortMode == targetSortMode)
        {
            return currentSortDirection == SortDirection.Asc
                ? SortDirection.Desc
                : SortDirection.Asc;
        }

        return DirectoryListingService.GetDefaultSortDirection(targetSortMode);
    }

    private static string BuildQuerySuffix(string sortKey, string sortDirection, string? searchQuery, string? shareToken)
    {
        var queryParts = new List<string> { sortKey, $"dir={sortDirection}" };

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            queryParts.Add($"q={Uri.EscapeDataString(searchQuery)}");
        }

        ShareLinkService.AppendTokenQuery(queryParts, shareToken);
        return "?" + string.Join("&", queryParts);
    }
}

public sealed class DirectoryListingPageResult
{
    public IActionResult? Result { get; private init; }
    public DirectoryListingPageState? State { get; private init; }

    public static DirectoryListingPageResult FromResult(IActionResult result)
    {
        return new DirectoryListingPageResult { Result = result };
    }

    public static DirectoryListingPageResult FromState(DirectoryListingPageState state)
    {
        return new DirectoryListingPageResult { State = state };
    }
}

public sealed class DirectoryListingPageState
{
    public string PageTitle { get; init; } = string.Empty;
    public string CurrentRelativePath { get; init; } = string.Empty;
    public string CurrentRequestPath { get; init; } = "/";
    public string OrderSuffix { get; init; } = string.Empty;
    public IEnumerable<DirectoryEntry> Entries { get; init; } = [];
    public string DefaultTheme { get; init; } = "dark";
    public bool CalculateDirectorySizes { get; init; }
    public bool AllowFileDownload { get; init; }
    public bool AllowFolderDownload { get; init; }
    public bool OpenArchivesInline { get; init; }
    public bool SearchEnabled { get; init; }
    public bool SearchActive { get; init; }
    public bool SearchTruncated { get; init; }
    public string SearchQuery { get; init; } = string.Empty;
    public string? SiteTitle { get; init; }
    public bool DashboardEnabled { get; init; }
    public string CurrentSortKey { get; init; } = "type";
    public string CurrentSortDirectionKey { get; init; } = "asc";
    public string TypeSortSuffix { get; init; } = "?type";
    public string NameSortSuffix { get; init; } = "?name";
    public string DateSortSuffix { get; init; } = "?date";
    public string SizeSortSuffix { get; init; } = "?size";
    public string ClearSearchSuffix { get; init; } = "?type";
    public bool ShareAccessActive { get; init; }
    public bool ShareFolderAccessActive { get; init; }
    public bool ShareControlsEnabled { get; init; }
    public string ShareToken { get; init; } = string.Empty;
    public string ShareHiddenInputToken { get; init; } = string.Empty;
    public string ShareQuerySuffix { get; init; } = string.Empty;
    public string ShareActionSuffix { get; init; } = string.Empty;
}
