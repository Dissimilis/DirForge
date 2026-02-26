using System.Reflection;
using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DirForge.Pages;

public sealed class DirectoryListingModel : PageModel
{
    private static readonly ContentTypeResolver MimeTypeResolver = new();

    public static string? AppVersion { get; } =
        typeof(DirectoryListingModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] is { } v && v != "1.0.0" ? v : null;

    public static bool IsImageExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) && DirectoryFileActionHandlers.ImageExtensions.Contains(extension);
    private readonly DirForgeOptions _options;
    private readonly DirectoryRequestGuards _guards;
    private readonly DirectoryActionHandlers _actions;
    private readonly DirectoryFileActionHandlers _fileActions;

    public DirectoryListingModel(
        DirectoryListingService directoryListingService,
        ShareLinkService shareLinkService,
        ArchiveBrowseService archiveBrowseService,
        IconResolver iconResolver,
        DirForgeOptions options,
        DashboardMetricsService dashboardMetrics,
        ILogger<DirectoryListingModel> logger)
    {
        _options = options;
        _guards = new DirectoryRequestGuards(directoryListingService, _options, logger);
        _actions = new DirectoryActionHandlers(directoryListingService, _options, dashboardMetrics, _guards);
        _fileActions = new DirectoryFileActionHandlers(directoryListingService, shareLinkService, archiveBrowseService, iconResolver, _options, dashboardMetrics, logger, _guards);
    }

    public string PageTitle { get; private set; } = string.Empty;
    public string CurrentRelativePath { get; private set; } = string.Empty;
    public string CurrentRequestPath { get; private set; } = "/";
    public string OrderSuffix { get; private set; } = string.Empty;
    public IEnumerable<DirectoryEntry> Entries { get; private set; } = [];
    public string DefaultTheme { get; private set; } = "dark";
    public bool CalculateDirectorySizes { get; private set; }
    public bool AllowFileDownload { get; private set; }
    public bool AllowFolderDownload { get; private set; }
    public bool OpenArchivesInline { get; private set; }
    public bool SearchEnabled { get; private set; }
    public bool SearchActive { get; private set; }
    public bool SearchTruncated { get; private set; }
    public string SearchQuery { get; private set; } = string.Empty;
    public string? SiteTitle { get; private set; }
    public bool DashboardEnabled { get; private set; }
    public string CurrentSortKey { get; private set; } = "type";
    public string CurrentSortDirectionKey { get; private set; } = "asc";
    public string TypeSortSuffix { get; private set; } = "?type";
    public string NameSortSuffix { get; private set; } = "?name";
    public string DateSortSuffix { get; private set; } = "?date";
    public string SizeSortSuffix { get; private set; } = "?size";
    public string ClearSearchSuffix { get; private set; } = "?type";
    public bool ShareAccessActive { get; private set; }
    public bool ShareFolderAccessActive { get; private set; }
    public bool ShareControlsEnabled { get; private set; }
    public string ShareToken { get; private set; } = string.Empty;
    public string ShareHiddenInputToken { get; private set; } = string.Empty;
    public string ShareQuerySuffix { get; private set; } = string.Empty;
    public string ShareActionSuffix { get; private set; } = string.Empty;
    public string ShareWarning => _options.ShareSecretWarning
        ?? "Changing ShareSecret invalidates all existing share links.";

    public IActionResult OnGet(string? requestPath)
    {
        var result = _actions.HandleGet(HttpContext, requestPath, _guards.GetShareContext(HttpContext));
        if (result.Result is not null)
        {
            return result.Result;
        }

        ApplyState(result.State!);
        return Page();
    }

    private void ApplyState(DirectoryListingPageState state)
    {
        PageTitle = state.PageTitle;
        CurrentRelativePath = state.CurrentRelativePath;
        CurrentRequestPath = state.CurrentRequestPath;
        OrderSuffix = state.OrderSuffix;
        Entries = state.Entries;
        DefaultTheme = state.DefaultTheme;
        CalculateDirectorySizes = state.CalculateDirectorySizes;
        AllowFileDownload = state.AllowFileDownload;
        AllowFolderDownload = state.AllowFolderDownload;
        OpenArchivesInline = state.OpenArchivesInline;
        SearchEnabled = state.SearchEnabled;
        SearchActive = state.SearchActive;
        SearchTruncated = state.SearchTruncated;
        SearchQuery = state.SearchQuery;
        SiteTitle = state.SiteTitle;
        DashboardEnabled = state.DashboardEnabled;
        CurrentSortKey = state.CurrentSortKey;
        CurrentSortDirectionKey = state.CurrentSortDirectionKey;
        TypeSortSuffix = state.TypeSortSuffix;
        NameSortSuffix = state.NameSortSuffix;
        DateSortSuffix = state.DateSortSuffix;
        SizeSortSuffix = state.SizeSortSuffix;
        ClearSearchSuffix = state.ClearSearchSuffix;
        ShareAccessActive = state.ShareAccessActive;
        ShareFolderAccessActive = state.ShareFolderAccessActive;
        ShareControlsEnabled = state.ShareControlsEnabled;
        ShareToken = state.ShareToken;
        ShareHiddenInputToken = state.ShareHiddenInputToken;
        ShareQuerySuffix = state.ShareQuerySuffix;
        ShareActionSuffix = state.ShareActionSuffix;
    }

    public IActionResult OnGetDirectorySizes(string? requestPath)
    {
        return _fileActions.HandleGetDirectorySizes(HttpContext, requestPath, _guards.GetShareContext(HttpContext));
    }

    public IActionResult OnGetView(string? requestPath)
    {
        return _fileActions.HandleGetView(HttpContext, requestPath, _guards.GetShareContext(HttpContext));
    }

    public IActionResult OnGetArchive(string? requestPath, string? ap)
    {
        var relativePath = DirectoryListingService.NormalizeRelativePath(requestPath);
        var encodedPath = DirectoryFileActionHandlers.EncodePathSegments(relativePath);
        var targetPath = "/archive" + (string.IsNullOrEmpty(encodedPath) ? string.Empty : "/" + encodedPath);

        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ap))
        {
            queryParts.Add("ap=" + Uri.EscapeDataString(ap));
        }

        ShareLinkService.AppendTokenQuery(queryParts, _guards.GetShareContext(HttpContext)?.Token);

        if (queryParts.Count == 0)
        {
            return Redirect(targetPath);
        }

        return Redirect(targetPath + "?" + string.Join('&', queryParts));
    }

    public async Task<IActionResult> OnGetArchiveEntry(string? requestPath, string? entryPath, CancellationToken cancellationToken)
    {
        return await _fileActions.HandleGetArchiveEntryAsync(
            HttpContext,
            requestPath,
            entryPath,
            _guards.GetShareContext(HttpContext),
            cancellationToken);
    }

    public async Task<IActionResult> OnGetDownloadZip(string? requestPath, CancellationToken cancellationToken)
    {
        return await _fileActions.HandleGetDownloadZipAsync(
            HttpContext,
            requestPath,
            _guards.GetShareContext(HttpContext),
            cancellationToken);
    }

    public IActionResult OnGetPreviewInfo(string? requestPath, bool forceText = false)
    {
        return _fileActions.HandleGetPreviewInfo(HttpContext, requestPath, _guards.GetShareContext(HttpContext), forceText);
    }

    public async Task<IActionResult> OnGetFileHashes(string? requestPath, CancellationToken cancellationToken)
    {
        return await _fileActions.HandleGetFileHashesAsync(HttpContext, requestPath, _guards.GetShareContext(HttpContext), cancellationToken);
    }

    public IActionResult OnGetShareLink()
    {
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    public IActionResult OnPostShareLink(string? targetPath, string? targetType, string? ttl, string? oneTime)
    {
        return _fileActions.HandlePostShareLink(
            HttpContext,
            targetPath,
            targetType,
            ttl,
            oneTime,
            _guards.GetShareContext(HttpContext),
            ShareWarning);
    }

    internal static DateTimeOffset GetListingLastModifiedUtc(IEnumerable<DirectoryEntry> entries, string physicalPath)
    {
        var latestUtc = new DateTimeOffset(Directory.GetLastWriteTimeUtc(physicalPath), TimeSpan.Zero);
        foreach (var entry in entries)
        {
            var entryUtc = new DateTimeOffset(entry.Modified.ToUniversalTime(), TimeSpan.Zero);
            if (entryUtc > latestUtc)
            {
                latestUtc = entryUtc;
            }
        }

        return latestUtc;
    }

    internal static DateTimeOffset TruncateToWholeSeconds(DateTimeOffset value)
    {
        return DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
    }

    internal static string GetContentType(string fileName)
    {
        return MimeTypeResolver.GetContentType(fileName);
    }

    internal static string BuildContentDisposition(string dispositionType, string fileName)
    {
        var safeFileName = SanitizeHeaderValue(fileName);
        var asciiFallback = BuildAsciiFallbackFileName(safeFileName);

        var header = new ContentDispositionHeaderValue(dispositionType)
        {
            FileName = asciiFallback,
            FileNameStar = safeFileName
        };

        return header.ToString();
    }

    private static string SanitizeHeaderValue(string value)
    {
        var sanitized = value.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized;
    }

    private static string BuildAsciiFallbackFileName(string value)
    {
        var fallback = new string(value
            .Select(ch => ch is >= ' ' and <= '~' && ch is not '"' and not '\\' ? ch : '_')
            .ToArray())
            .Trim();

        return string.IsNullOrWhiteSpace(fallback) ? "download" : fallback;
    }

}
