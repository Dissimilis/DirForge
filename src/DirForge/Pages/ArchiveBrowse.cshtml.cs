using System.Text;
using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Net.Http.Headers;

namespace DirForge.Pages;

[IgnoreAntiforgeryToken]
public sealed class ArchiveBrowseModel : PageModel
{
    private readonly DirectoryListingService _directoryListingService;
    private readonly ArchiveBrowseService _archiveBrowseService;
    private readonly IconResolver _iconResolver;
    private readonly DirForgeOptions _options;
    private readonly DashboardMetricsService _dashboardMetrics;
    private readonly ILogger<ArchiveBrowseModel> _logger;

    private string _shareToken = string.Empty;

    public ArchiveBrowseModel(
        DirectoryListingService directoryListingService,
        ArchiveBrowseService archiveBrowseService,
        IconResolver iconResolver,
        DirForgeOptions options,
        DashboardMetricsService dashboardMetrics,
        ILogger<ArchiveBrowseModel> logger)
    {
        _directoryListingService = directoryListingService;
        _archiveBrowseService = archiveBrowseService;
        _iconResolver = iconResolver;
        _options = options;
        _dashboardMetrics = dashboardMetrics;
        _logger = logger;
    }

    public string ArchiveFileName { get; private set; } = string.Empty;
    public string ArchiveRelativePath { get; private set; } = string.Empty;
    public string CurrentArchivePath { get; private set; } = string.Empty;
    public string ParentArchivePath { get; private set; } = string.Empty;
    public string ArchiveBasePath { get; private set; } = "/archive";
    public string BackToFolderHref { get; private set; } = "/";
    public IReadOnlyList<ArchiveEntryViewModel> Entries { get; private set; } = [];
    public string DefaultTheme { get; private set; } = "dark";
    public string? SiteTitle { get; private set; }
    public string PageTitle { get; private set; } = "DirForge :: archive";
    public bool ShareAccessActive { get; private set; }
    public bool DashboardEnabled { get; private set; }
    public bool IsArchiveRoot => string.IsNullOrEmpty(CurrentArchivePath);

    public IActionResult OnGet(string? requestPath, string? ap)
    {
        if (!_options.OpenArchivesInline)
        {
            return NotFound();
        }

        if (!TryResolveArchiveFile(requestPath, out var shareContext, out var relativePath, out var physicalPath, out var fileInfo, out var failure))
        {
            return failure!;
        }

        if (!_archiveBrowseService.TryNormalizeVirtualPath(ap, out var normalizedArchivePath))
        {
            return BadRequest("Invalid archive path.");
        }

        ArchiveBrowseListing listing;
        try
        {
            listing = _archiveBrowseService.BuildListing(physicalPath, normalizedArchivePath);
        }
        catch (InvalidDataException)
        {
            return BadRequest("Archive is invalid or unreadable.");
        }
        catch (NotSupportedException)
        {
            return NotFound();
        }

        _dashboardMetrics.RecordArchiveBrowse();
        ApplyState(relativePath, fileInfo.Name, listing, shareContext);
        return Page();
    }

    public async Task<IActionResult> OnGetDownloadEntry(string? requestPath, string? entryPath, CancellationToken cancellationToken)
    {
        if (!_options.OpenArchivesInline)
        {
            return NotFound();
        }

        if (!TryResolveArchiveFile(requestPath, out _, out _, out var physicalPath, out _, out var failure))
        {
            return failure!;
        }

        if (!_archiveBrowseService.TryNormalizeVirtualPath(entryPath, out var normalizedEntryPath) ||
            string.IsNullOrEmpty(normalizedEntryPath))
        {
            return BadRequest("Invalid archive entry path.");
        }

        if (!_options.AllowFileDownload || _directoryListingService.IsFileDownloadBlocked(normalizedEntryPath))
        {
            return NotFound();
        }

        if (!_archiveBrowseService.TryGetEntryDownloadInfo(physicalPath, normalizedEntryPath, out var entryInfo) ||
            entryInfo is null)
        {
            return NotFound();
        }

        var response = HttpContext.Response;
        response.ContentType = DirectoryListingModel.GetContentType(entryInfo.FileName);
        response.Headers[HeaderNames.ContentDisposition] = DirectoryListingModel.BuildContentDisposition("attachment", entryInfo.FileName);
        if (entryInfo.Size is long size and >= 0)
        {
            response.ContentLength = size;
        }

        try
        {
            var copiedBytes = await _archiveBrowseService.CopyEntryToAsync(
                physicalPath,
                normalizedEntryPath,
                response.Body,
                cancellationToken);
            _dashboardMetrics.RecordArchiveInnerDownload(copiedBytes);
            await response.Body.FlushAsync(cancellationToken);
            await response.CompleteAsync();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidDataException)
        {
            return BadRequest("Archive entry is invalid.");
        }

        return new EmptyResult();
    }

    public async Task<IActionResult> OnGetPreviewInfoEntry(string? requestPath, string? entryPath, CancellationToken cancellationToken, bool forceText = false)
    {
        if (!_options.OpenArchivesInline)
        {
            return NotFound();
        }

        if (!TryResolveArchiveFile(requestPath, out var shareContext, out var relativePath, out var physicalPath, out _, out var failure))
        {
            return failure!;
        }

        if (!_archiveBrowseService.TryNormalizeVirtualPath(entryPath, out var normalizedEntryPath) ||
            string.IsNullOrEmpty(normalizedEntryPath))
        {
            return BadRequest("Invalid archive entry path.");
        }

        if (!_options.AllowFileDownload || _directoryListingService.IsFileDownloadBlocked(normalizedEntryPath))
        {
            return NotFound();
        }

        if (!_archiveBrowseService.TryGetEntryDownloadInfo(physicalPath, normalizedEntryPath, out var entryInfo) ||
            entryInfo is null)
        {
            return NotFound();
        }

        var extension = IconResolver.GetExtension(entryInfo.FileName);
        var effectiveSize = entryInfo.Size.GetValueOrDefault();
        var type = _iconResolver.ResolveType(entryInfo.FileName, isDirectory: false, effectiveSize);
        var iconPath = _iconResolver.ResolveIconPath(entryInfo.FileName, type);
        var mimeType = DirectoryListingModel.GetContentType(entryInfo.FileName);
        var previewMode = TextPreviewDefaults.IsTextLikeMimeType(mimeType) ? "text" : "none";

        if (forceText)
            previewMode = "text";
        if (previewMode == "text" &&
            _options.MaxPreviewFileSize > 0 &&
            entryInfo.Size is long knownSize &&
            knownSize > _options.MaxPreviewFileSize)
        {
            previewMode = "none";
        }

        string? textContent = null;
        var textTruncated = false;

        if (previewMode == "text")
        {
            try
            {
                var bytes = await _archiveBrowseService.ReadEntryHeadBytesAsync(
                    physicalPath,
                    normalizedEntryPath,
                    TextPreviewDefaults.MaxBytes,
                    cancellationToken);
                textContent = Encoding.UTF8.GetString(bytes);
                textTruncated = entryInfo.Size is long knownTextSize
                    ? knownTextSize > TextPreviewDefaults.MaxBytes
                    : bytes.Length >= TextPreviewDefaults.MaxBytes;
            }
            catch (Exception)
            {
                previewMode = "none";
                textContent = null;
                textTruncated = false;
            }
        }

        var size = entryInfo.Size ?? -1;
        var humanSize = entryInfo.Size is long knownEntrySize and >= 0
            ? DirectoryListingService.HumanizeSize(knownEntrySize)
            : "Unknown";
        var modified = entryInfo.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss") ?? string.Empty;

        var archiveBasePath = BuildArchiveBasePath(relativePath);
        var shareToken = shareContext?.Token ?? string.Empty;
        var downloadQuery = new List<string>(3)
        {
            "handler=DownloadEntry",
            "entryPath=" + Uri.EscapeDataString(normalizedEntryPath)
        };
        ShareLinkService.AppendTokenQuery(downloadQuery, shareToken);
        var downloadUrl = archiveBasePath + "?" + string.Join('&', downloadQuery);

        return new JsonResult(new
        {
            name = entryInfo.FileName,
            extension,
            type,
            mimeType,
            size,
            humanSize,
            modified,
            previewMode,
            viewUrl = string.Empty,
            downloadUrl,
            iconPath,
            textContent,
            textTruncated,
            maxFileSizeForHashing = _options.MaxFileSizeForHashing
        });
    }

    public string BuildBrowseHref(string archivePath)
    {
        var query = new List<string>(2);
        if (!string.IsNullOrEmpty(archivePath))
        {
            query.Add("ap=" + Uri.EscapeDataString(archivePath));
        }

        ShareLinkService.AppendTokenQuery(query, _shareToken);
        return query.Count == 0
            ? ArchiveBasePath
            : ArchiveBasePath + "?" + string.Join('&', query);
    }

    public string BuildDownloadHref(string entryPath)
    {
        var query = new List<string>(3)
        {
            "handler=DownloadEntry",
            "entryPath=" + Uri.EscapeDataString(entryPath)
        };

        ShareLinkService.AppendTokenQuery(query, _shareToken);
        return ArchiveBasePath + "?" + string.Join('&', query);
    }

    public string BuildPreviewInfoHref(string entryPath)
    {
        var query = new List<string>(3)
        {
            "handler=PreviewInfoEntry",
            "entryPath=" + Uri.EscapeDataString(entryPath)
        };

        ShareLinkService.AppendTokenQuery(query, _shareToken);
        return ArchiveBasePath + "?" + string.Join('&', query);
    }

    private void ApplyState(string relativePath, string archiveFileName, ArchiveBrowseListing listing, ShareAccessContext? shareContext)
    {
        ArchiveFileName = archiveFileName;
        ArchiveRelativePath = relativePath;
        CurrentArchivePath = listing.CurrentPath;
        ParentArchivePath = listing.ParentPath;
        _shareToken = shareContext?.Token ?? string.Empty;
        ShareAccessActive = shareContext is not null;
        DashboardEnabled = _options.DashboardEnabled;
        DefaultTheme = _options.DefaultTheme;
        SiteTitle = _options.SiteTitle;
        PageTitle = $"{(_options.SiteTitle ?? "DirForge")} :: {ArchiveFileName} {(string.IsNullOrEmpty(CurrentArchivePath) ? "/" : "/" + CurrentArchivePath)}";

        ArchiveBasePath = BuildArchiveBasePath(relativePath);
        BackToFolderHref = BuildFolderBackLink(relativePath, _shareToken);

        Entries = listing.Entries
            .Select(entry => new ArchiveEntryViewModel
            {
                Name = entry.Name,
                Path = entry.Path,
                IsDirectory = entry.IsDirectory,
                Type = entry.IsDirectory ? "folder" : "file",
                IsTextPreviewable = !entry.IsDirectory && TextPreviewDefaults.IsTextLikeMimeType(DirectoryListingModel.GetContentType(entry.Name)),
                IconPath = entry.IsDirectory
                    ? StaticAssetRouteHelper.AssetPath("file-icon-vectors/folder.svg")
                    : _iconResolver.ResolveIconPath(entry.Name, "blank"),
                ModifiedString = entry.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss") ?? string.Empty,
                HumanSize = entry.IsDirectory ? string.Empty : DirectoryListingService.HumanizeSize(entry.Size)
            })
            .ToArray();
    }

    private bool TryResolveArchiveFile(
        string? requestPath,
        out ShareAccessContext? shareContext,
        out string relativePath,
        out string physicalPath,
        out FileInfo fileInfo,
        out IActionResult? failure)
    {
        shareContext = GetShareContext();
        relativePath = DirectoryListingService.NormalizeRelativePath(requestPath);
        physicalPath = _directoryListingService.ResolvePhysicalPath(relativePath) ?? string.Empty;
        fileInfo = new FileInfo(Path.GetTempPath());
        failure = null;

        if (string.IsNullOrEmpty(physicalPath))
        {
            _logger.LogWarning("[SECURITY] PATH_TRAVERSAL ClientIP={ClientIp} Path={RequestPath} Resolved path is null",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                HttpContext.Request.Path.Value ?? "/");
            failure = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return false;
        }

        if (shareContext?.Mode == ShareMode.Directory &&
            !_directoryListingService.IsCanonicallyWithinScope(physicalPath, shareContext.ScopePath))
        {
            _logger.LogWarning("[SECURITY] SHARE_SCOPE_VIOLATION ClientIP={ClientIp} Path={RequestPath} Outside share scope",
                HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                HttpContext.Request.Path.Value ?? "/");
            failure = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return false;
        }

        if (_directoryListingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            failure = new NotFoundResult();
            return false;
        }

        if (!_options.AllowFileDownload || _directoryListingService.IsFileDownloadBlocked(relativePath))
        {
            failure = new NotFoundResult();
            return false;
        }

        fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            failure = new NotFoundResult();
            return false;
        }

        if (!_archiveBrowseService.IsSupportedArchiveName(fileInfo.Name))
        {
            failure = new NotFoundResult();
            return false;
        }

        return true;
    }

    private ShareAccessContext? GetShareContext()
    {
        return HttpContext.Items.TryGetValue(ShareLinkService.HttpContextItemKey, out var value)
            ? value as ShareAccessContext
            : null;
    }

    private static string BuildArchiveBasePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return "/archive";
        }

        return "/archive/" + DirectoryFileActionHandlers.EncodePathSegments(relativePath);
    }

    private static string BuildFolderBackLink(string archiveRelativePath, string shareToken)
    {
        var parentRelativePath = string.Empty;
        if (!string.IsNullOrEmpty(archiveRelativePath))
        {
            var trimmed = archiveRelativePath.Trim('/');
            var splitIndex = trimmed.LastIndexOf('/');
            parentRelativePath = splitIndex > 0 ? trimmed[..splitIndex] : string.Empty;
        }

        var path = DirectoryListingService.BuildRequestPath(parentRelativePath);
        if (string.IsNullOrEmpty(shareToken))
        {
            return path;
        }

        return path + "?" + ShareLinkService.TokenQueryParameter + "=" + Uri.EscapeDataString(shareToken);
    }

    public sealed class ArchiveEntryViewModel
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string Type { get; init; }
        public required string IconPath { get; init; }
        public required bool IsDirectory { get; init; }
        public required bool IsTextPreviewable { get; init; }
        public required string ModifiedString { get; init; }
        public required string HumanSize { get; init; }
    }
}
