using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DirForge.Pages;

public sealed class DirectoryFileActionHandlers
{
    private static readonly HashSet<string> PreviewTextTypes = new(StringComparer.Ordinal)
    {
        "text", "source", "info", "ht", "php"
    };
    private static readonly HashSet<string> PreviewTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "csproj", "vbproj", "fsproj", "dbproj", "sqlproj", "vcxproj", "proj",
        "sln", "props", "targets", "nuspec", "resx", "ruleset", "runsettings",
        "razor", "cshtml", "vbhtml", "editorconfig",
        "plist", "config", "manifest", "svg",
        "jsonc", "json5", "ndjson", "geojson", "har", "webmanifest",
        "csv", "tsv", "tex", "latex", "sty", "cls", "bib", "ipynb",
        "liquid", "ejs", "pug", "jade", "slim", "haml", "mustache",
        "diff", "patch",
        "srt", "sub", "ass", "ssa", "vtt",
        "m3u", "m3u8", "pls", "xspf",
        "dockerignore",
        "meson",
        "pom", "ivy",
        "graphql", "gql", "proto",
        "tfvars",
        "reg", "inf",
        "htaccess", "nginx",
        "pem", "pub", "crt", "cer", "asc"
    };
    internal static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "webp", "svg", "bmp", "ico", "avif"
    };
    private static readonly HashSet<string> PreviewVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "ogv", "mov"
    };
    private static readonly HashSet<string> PreviewAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "ogg", "opus", "flac", "aac", "m4a"
    };

    private readonly DirectoryListingService _directoryListingService;
    private readonly ShareLinkService _shareLinkService;
    private readonly ArchiveBrowseService _archiveBrowseService;
    private readonly IconResolver _iconResolver;
    private readonly DirForgeOptions _options;
    private readonly DashboardMetricsService _dashboardMetrics;
    private readonly ILogger<DirectoryListingModel> _logger;
    private readonly DirectoryRequestGuards _guards;

    public DirectoryFileActionHandlers(
        DirectoryListingService directoryListingService,
        ShareLinkService shareLinkService,
        ArchiveBrowseService archiveBrowseService,
        IconResolver iconResolver,
        DirForgeOptions options,
        DashboardMetricsService dashboardMetrics,
        ILogger<DirectoryListingModel> logger,
        DirectoryRequestGuards guards)
    {
        _directoryListingService = directoryListingService;
        _shareLinkService = shareLinkService;
        _archiveBrowseService = archiveBrowseService;
        _iconResolver = iconResolver;
        _options = options;
        _dashboardMetrics = dashboardMetrics;
        _logger = logger;
        _guards = guards;
    }

    public IActionResult HandleGetDirectorySizes(HttpContext httpContext, string? requestPath, ShareAccessContext? shareContext)
    {
        if (!_guards.TryResolvePhysicalPath(httpContext, requestPath, out var relativePath, out var physicalPath))
        {
            return new JsonResult(new { }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        if (!Directory.Exists(physicalPath))
        {
            return new JsonResult(new { }) { StatusCode = StatusCodes.Status404NotFound };
        }

        if (_guards.IsDirectoryShareScopeViolation(shareContext, physicalPath))
        {
            return new JsonResult(new { }) { StatusCode = StatusCodes.Status403Forbidden };
        }

        if (_directoryListingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            return new JsonResult(new { }) { StatusCode = StatusCodes.Status404NotFound };
        }

        var sizes = _directoryListingService.ComputeDirectorySizes(physicalPath);
        return new JsonResult(sizes);
    }

    public IActionResult HandleGetView(HttpContext httpContext, string? requestPath, ShareAccessContext? shareContext)
    {
        var guard = GuardFileAccess(httpContext, requestPath, shareContext, out _, out var physicalPath, out var fileInfo);
        if (guard is not null) return guard;

        var contentType = DirectoryListingModel.GetContentType(fileInfo.Name);
        httpContext.Response.Headers[HeaderNames.ContentDisposition] = DirectoryListingModel.BuildContentDisposition("inline", fileInfo.Name);
        return new PhysicalFileResult(physicalPath, contentType)
        {
            EnableRangeProcessing = true
        };
    }

    public IActionResult HandleGetArchive(
        HttpContext httpContext,
        string? requestPath,
        string? archivePath,
        ShareAccessContext? shareContext)
    {
        if (!_options.OpenArchivesInline)
        {
            return new NotFoundResult();
        }

        var guard = GuardFileAccess(httpContext, requestPath, shareContext, out var relativePath, out var physicalPath, out var fileInfo);
        if (guard is not null) return guard;

        if (!_archiveBrowseService.IsSupportedArchiveName(fileInfo.Name))
        {
            return new NotFoundResult();
        }

        if (!_archiveBrowseService.TryNormalizeVirtualPath(archivePath, out var normalizedArchivePath))
        {
            return new BadRequestObjectResult("Invalid archive path.");
        }

        ArchiveBrowseListing listing;
        try
        {
            listing = _archiveBrowseService.BuildListing(physicalPath, normalizedArchivePath, _options.OperationTimeBudgetMs);
        }
        catch (InvalidDataException)
        {
            return new BadRequestObjectResult("Archive is invalid or unreadable.");
        }
        catch (NotSupportedException)
        {
            return new NotFoundResult();
        }

        _dashboardMetrics.RecordArchiveBrowse();
        var html = BuildArchiveBrowseHtml(
            requestPath: httpContext.Request.Path.Value ?? "/",
            archiveFileName: fileInfo.Name,
            archiveRelativePath: relativePath,
            listing,
            shareToken: shareContext?.Token ?? string.Empty,
            siteTitle: _options.SiteTitle);

        return new ContentResult
        {
            ContentType = "text/html; charset=utf-8",
            Content = html,
            StatusCode = StatusCodes.Status200OK
        };
    }

    public async Task<IActionResult> HandleGetArchiveEntryAsync(
        HttpContext httpContext,
        string? requestPath,
        string? entryPath,
        ShareAccessContext? shareContext,
        CancellationToken cancellationToken)
    {
        if (!_options.OpenArchivesInline)
        {
            return new NotFoundResult();
        }

        var guard = GuardFileAccess(httpContext, requestPath, shareContext, out _, out var physicalPath, out var fileInfo);
        if (guard is not null) return guard;

        if (!_archiveBrowseService.IsSupportedArchiveName(fileInfo.Name))
        {
            return new NotFoundResult();
        }

        if (!_archiveBrowseService.TryNormalizeVirtualPath(entryPath, out var normalizedEntryPath) ||
            string.IsNullOrEmpty(normalizedEntryPath))
        {
            return new BadRequestObjectResult("Invalid archive entry path.");
        }

        if (!_options.AllowFileDownload || _directoryListingService.IsFileDownloadBlocked(normalizedEntryPath))
        {
            return _guards.CreateBlockedExtensionNotFound(httpContext);
        }

        if (!_archiveBrowseService.TryGetEntryDownloadInfo(physicalPath, normalizedEntryPath, out var entryInfo) ||
            entryInfo is null)
        {
            return new NotFoundResult();
        }

        var response = httpContext.Response;
        response.ContentType = DirectoryListingModel.GetContentType(entryInfo.FileName);
        response.Headers[HeaderNames.ContentDisposition] = DirectoryListingModel.BuildContentDisposition("attachment", entryInfo.FileName);
        if (entryInfo.Size.HasValue && entryInfo.Size.Value >= 0)
        {
            response.ContentLength = entryInfo.Size.Value;
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
        catch (FileNotFoundException ex)
        {
            if (httpContext.Response.HasStarted)
            {
                _logger.LogWarning(ex, "Archive entry not found after response started for {Path}", requestPath);
                return new EmptyResult();
            }

            return new NotFoundResult();
        }
        catch (InvalidDataException ex)
        {
            if (httpContext.Response.HasStarted)
            {
                _logger.LogWarning(ex, "Invalid archive entry after response started for {Path}", requestPath);
                return new EmptyResult();
            }

            return new BadRequestObjectResult("Archive entry is invalid.");
        }

        return new EmptyResult();
    }

    public async Task<IActionResult> HandleGetDownloadZipAsync(
        HttpContext httpContext,
        string? requestPath,
        ShareAccessContext? shareContext,
        CancellationToken cancellationToken)
    {
        var request = httpContext.Request;
        var response = httpContext.Response;

        if (!_options.AllowFolderDownload)
        {
            _logger.LogWarning("[SECURITY] FOLDER_DOWNLOAD_DISABLED ClientIP={ClientIp} Path={RequestPath} Folder download not allowed",
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", request.Path.Value ?? "/");
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        if (!_guards.TryResolvePhysicalPath(httpContext, requestPath, out var relativePath, out var physicalPath))
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        if (!Directory.Exists(physicalPath))
        {
            return new NotFoundResult();
        }

        if (_guards.IsDirectoryShareScopeViolation(shareContext, physicalPath))
        {
            return _guards.CreateShareScopeForbidden(httpContext);
        }

        if (_directoryListingService.IsPathHiddenByPolicy(relativePath, isDirectory: true))
        {
            return new NotFoundResult();
        }

        var sortMode = DirectoryListingService.GetSortMode(request.Query);
        var sortDirection = DirectoryListingService.GetSortDirection(request.Query, sortMode);
        var searchQuery = _options.EnableSearch ? request.Query["q"].ToString().Trim() : string.Empty;
        var searchActive = !string.IsNullOrWhiteSpace(searchQuery);

        List<DirectoryEntry>? filteredEntries = null;
        if (searchActive)
        {
            try
            {
                filteredEntries = _directoryListingService.GetSortedSearchEntries(
                    physicalPath,
                    relativePath,
                    searchQuery,
                    sortMode,
                    sortDirection,
                    DirectoryListingService.SearchResultLimit,
                    out _);
            }
            catch (UnauthorizedAccessException)
            {
                return new StatusCodeResult(StatusCodes.Status403Forbidden);
            }
        }

        var folderName = Path.GetFileName(physicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName))
        {
            folderName = "download";
        }

        response.ContentType = "application/zip";
        response.Headers[HeaderNames.ContentDisposition] = DirectoryListingModel.BuildContentDisposition("attachment", folderName + ".zip");

        long totalBytes = 0;
        try
        {
            await using (var archive = new ZipArchive(response.Body, ZipArchiveMode.Create, leaveOpen: true))
            {
                var opBudgetMs = _options.OperationTimeBudgetMs;
                Stopwatch? opTimer = opBudgetMs > 0 ? Stopwatch.StartNew() : null;
                IEnumerable<string> sourceFilePaths = searchActive
                    ? GetFilteredFilePaths(physicalPath, filteredEntries!)
                    : _directoryListingService.EnumerateFilePathsRecursive(physicalPath, opTimer, opBudgetMs);

                var maxZip = _options.MaxZipSize;

                foreach (var filePath in sourceFilePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!_directoryListingService.ValidateSymlinkContainment(filePath))
                    {
                        continue;
                    }

                    if (_guards.IsDirectoryShareScopeViolation(shareContext, filePath))
                    {
                        continue;
                    }

                    var entryName = Path.GetRelativePath(physicalPath, filePath).Replace('\\', '/');
                    var rootRelativePath = _directoryListingService.GetRootRelativePath(filePath);

                    if (_directoryListingService.IsPathHiddenByPolicy(rootRelativePath, isDirectory: false))
                    {
                        continue;
                    }

                    if (!_guards.IsFileDownloadAllowed(rootRelativePath))
                    {
                        continue;
                    }

                    if (_options.HideDotfiles && entryName.Split('/').Any(s => s.StartsWith('.')))
                    {
                        continue;
                    }

                    var fileLength = new FileInfo(filePath).Length;
                    if (maxZip > 0 && totalBytes + fileLength > maxZip)
                    {
                        _dashboardMetrics.RecordZipSizeLimitHit();
                        _logger.LogWarning("ZIP size limit ({MaxZipSize} bytes) reached for '{Path}', stopping", maxZip, relativePath);
                        break;
                    }

                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    entry.LastWriteTime = File.GetLastWriteTime(filePath);

                    try
                    {
                        await using var entryStream = entry.Open();
                        await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1048576, useAsync: true);
                        await fileStream.CopyToAsync(entryStream, cancellationToken);
                        totalBytes += fileLength;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogInformation(ex, "Skipping ZIP entry due to access denied: {FilePath}", filePath);
                    }
                    catch (FileNotFoundException ex)
                    {
                        _logger.LogInformation(ex, "Skipping ZIP entry because file no longer exists: {FilePath}", filePath);
                    }
                }
            }

            await response.Body.FlushAsync(cancellationToken);
            await response.CompleteAsync();
            _dashboardMetrics.RecordZipDownload(totalBytes);
        }
        catch (OperationCanceledException)
        {
            _dashboardMetrics.RecordZipCancelled();
            _logger.LogDebug("ZIP download cancelled for '{RelativePath}'", relativePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZIP download failed for '{RelativePath}'", relativePath);
        }

        return new EmptyResult();
    }

    public IActionResult HandleGetPreviewInfo(HttpContext httpContext, string? requestPath, ShareAccessContext? shareContext, bool forceText = false)
    {
        var guard = GuardFileAccess(httpContext, requestPath, shareContext, out var relativePath, out var physicalPath, out var fileInfo);
        if (guard is not null) return guard;

        var extension = IconResolver.GetExtension(fileInfo.Name);
        var type = _iconResolver.ResolveType(fileInfo.FullName, isDirectory: false, fileInfo.Length);
        var mimeType = DirectoryListingModel.GetContentType(fileInfo.Name);
        var previewMode = ResolvePreviewMode(type, extension, mimeType, fileInfo.Length, _options.MaxPreviewFileSize);

        if (forceText)
            previewMode = "text";

        var shareToken = shareContext?.Token;
        var viewSuffix = string.IsNullOrEmpty(shareToken)
            ? string.Empty
            : $"&{ShareLinkService.TokenQueryParameter}={Uri.EscapeDataString(shareToken)}";
        var encodedPath = EncodePathSegments(relativePath);
        var viewUrl = $"/{encodedPath}?handler=View{viewSuffix}";
        var downloadUrl = string.IsNullOrEmpty(shareToken)
            ? $"/{encodedPath}"
            : $"/{encodedPath}?{ShareLinkService.TokenQueryParameter}={Uri.EscapeDataString(shareToken)}";

        string? textContent = null;
        var textTruncated = false;

        if (previewMode == "text")
        {
            try
            {
                var bytes = new byte[Math.Min(fileInfo.Length, TextPreviewDefaults.MaxBytes)];
                using var fs = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytesRead = fs.Read(bytes, 0, bytes.Length);
                textContent = Encoding.UTF8.GetString(bytes, 0, bytesRead);
                textTruncated = fileInfo.Length > TextPreviewDefaults.MaxBytes;
            }
            catch (Exception)
            {
                textContent = null;
                previewMode = "none";
            }
        }

        var iconPath = _iconResolver.ResolveIconPath(fileInfo.Name, type);
        var detectedFileType = FileSignatureDetector.Detect(physicalPath);

        return new JsonResult(new
        {
            name = fileInfo.Name,
            extension,
            type,
            mimeType,
            detectedFileType,
            size = fileInfo.Length,
            humanSize = DirectoryListingService.HumanizeSize(fileInfo.Length),
            modified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss"),
            previewMode,
            viewUrl,
            downloadUrl,
            iconPath,
            textContent,
            textTruncated,
            maxFileSizeForHashing = _options.MaxFileSizeForHashing
        });
    }

    public async Task<IActionResult> HandleGetFileHashesAsync(HttpContext httpContext, string? requestPath, ShareAccessContext? shareContext, CancellationToken cancellationToken = default)
    {
        var guard = GuardFileAccess(httpContext, requestPath, shareContext, out _, out var physicalPath, out var fileInfo);
        if (guard is not null) return guard;

        if (fileInfo.Length > _options.MaxFileSizeForHashing)
        {
            return new BadRequestObjectResult("File exceeds the maximum size for hashing.");
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];

        await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken)) > 0)
        {
            md5.AppendData(buffer.AsSpan(0, bytesRead));
            sha1.AppendData(buffer.AsSpan(0, bytesRead));
            sha256.AppendData(buffer.AsSpan(0, bytesRead));
            sha512.AppendData(buffer.AsSpan(0, bytesRead));
        }

        return new JsonResult(new
        {
            md5 = Convert.ToHexString(md5.GetCurrentHash()).ToLowerInvariant(),
            sha1 = Convert.ToHexString(sha1.GetCurrentHash()).ToLowerInvariant(),
            sha256 = Convert.ToHexString(sha256.GetCurrentHash()).ToLowerInvariant(),
            sha512 = Convert.ToHexString(sha512.GetCurrentHash()).ToLowerInvariant()
        });
    }

    public IActionResult HandlePostShareLink(
        HttpContext httpContext,
        string? targetPath,
        string? targetType,
        string? ttl,
        string? oneTime,
        ShareAccessContext? shareContext,
        string shareWarning)
    {
        if (!_options.EnableSharing || shareContext is not null)
        {
            return new StatusCodeResult(StatusCodes.Status401Unauthorized);
        }

        if (!_guards.IsTrustedShareLinkRequest(httpContext.Request))
        {
            _logger.LogWarning("[SECURITY] SHARE_LINK_REJECTED ClientIP={ClientIp} Path={RequestPath} Untrusted share link request",
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                httpContext.Request.Path.Value ?? "/");
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(ttl))
        {
            return new BadRequestObjectResult(new { error = "Missing targetType or ttl." });
        }

        var mode = targetType.Trim().ToLowerInvariant() switch
        {
            "file" => ShareMode.File,
            "folder" => ShareMode.Directory,
            _ => (ShareMode?)null
        };

        if (mode is null)
        {
            return new BadRequestObjectResult(new { error = "Invalid targetType." });
        }

        if (!TryResolveTtl(ttl.Trim(), out var duration))
        {
            return new BadRequestObjectResult(new { error = "Invalid ttl." });
        }

        if (!TryResolveOneTime(oneTime, out var isOneTime))
        {
            return new BadRequestObjectResult(new { error = "Invalid oneTime." });
        }

        var relativePath = ShareLinkService.NormalizeRelativePath(targetPath);
        if (mode == ShareMode.File && string.IsNullOrEmpty(relativePath))
        {
            return new BadRequestObjectResult(new { error = "File share path is required." });
        }

        var physicalPath = _directoryListingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            return new NotFoundResult();
        }

        var isDirectory = mode == ShareMode.Directory;
        if (_directoryListingService.IsPathHiddenByPolicy(relativePath, isDirectory))
        {
            return new NotFoundResult();
        }

        if (isDirectory)
        {
            if (!Directory.Exists(physicalPath))
            {
                return new NotFoundResult();
            }
        }
        else
        {
            if (!File.Exists(physicalPath))
            {
                return new NotFoundResult();
            }
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(duration);
        var token = _shareLinkService.CreateToken(mode.Value, relativePath, expiresAt.ToUnixTimeSeconds(), isOneTime);

        var sharePath = mode == ShareMode.Directory
            ? DirectoryListingService.BuildRequestPath(relativePath)
            : "/" + EncodePathSegments(relativePath);
        var shareUrl = $"{sharePath}?{ShareLinkService.TokenQueryParameter}={Uri.EscapeDataString(token)}";
        _dashboardMetrics.RecordShareLinkCreated();

        return new JsonResult(new
        {
            url = shareUrl,
            expiresAtUtc = expiresAt.ToString("O"),
            oneTime = isOneTime,
            warning = shareWarning
        });
    }

    private IActionResult? GuardFileAccess(
        HttpContext httpContext, string? requestPath, ShareAccessContext? shareContext,
        out string relativePath, out string physicalPath, out FileInfo fileInfo)
    {
        relativePath = string.Empty;
        physicalPath = string.Empty;
        fileInfo = null!;

        if (!_guards.TryResolvePhysicalPath(httpContext, requestPath, out relativePath, out physicalPath))
        {
            return new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        if (_guards.IsDirectoryShareScopeViolation(shareContext, physicalPath))
        {
            return _guards.CreateShareScopeForbidden(httpContext);
        }

        if (_directoryListingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            return new NotFoundResult();
        }

        if (!_guards.IsFileDownloadAllowed(relativePath))
        {
            return _guards.CreateBlockedExtensionNotFound(httpContext);
        }

        fileInfo = new FileInfo(physicalPath);
        if (!fileInfo.Exists)
        {
            return new NotFoundResult();
        }

        return null;
    }

    private static IEnumerable<string> GetFilteredFilePaths(string basePhysicalPath, IEnumerable<DirectoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            var relativePath = entry.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(basePhysicalPath, relativePath);
            if (File.Exists(filePath))
            {
                yield return filePath;
            }
        }
    }

    private static bool TryResolveTtl(string ttl, out TimeSpan duration)
    {
        duration = ttl switch
        {
            "1h" => TimeSpan.FromHours(1),
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "week" => TimeSpan.FromDays(7),
            "month" => TimeSpan.FromDays(30),
            "year" => TimeSpan.FromDays(365),
            "10years" => TimeSpan.FromDays(3650),
            _ => TimeSpan.Zero
        };

        return duration > TimeSpan.Zero;
    }

    private static bool TryResolveOneTime(string? oneTime, out bool isOneTime)
    {
        if (string.IsNullOrWhiteSpace(oneTime))
        {
            isOneTime = false;
            return true;
        }

        switch (oneTime.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
                isOneTime = true;
                return true;
            case "0":
            case "false":
            case "off":
                isOneTime = false;
                return true;
            default:
                isOneTime = false;
                return false;
        }
    }

    internal static string EncodePathSegments(string relativePath)
    {
        return string.Join("/", relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    private static string ResolvePreviewMode(string type, string extension, string mimeType, long fileSize, long maxPreviewSize)
    {
        if (maxPreviewSize > 0 && fileSize > maxPreviewSize)
        {
            return "none";
        }

        if (PreviewTextTypes.Contains(type))
        {
            return "text";
        }

        if (PreviewTextExtensions.Contains(extension))
        {
            return "text";
        }

        if (ImageExtensions.Contains(extension))
        {
            return "image";
        }

        if (PreviewVideoExtensions.Contains(extension))
        {
            return "video";
        }

        if (PreviewAudioExtensions.Contains(extension))
        {
            return "audio";
        }

        if (extension.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "pdf";
        }

        return TextPreviewDefaults.IsTextLikeMimeType(mimeType) ? "text" : "none";
    }

    private static string BuildArchiveBrowseHtml(
        string requestPath,
        string archiveFileName,
        string archiveRelativePath,
        ArchiveBrowseListing listing,
        string shareToken,
        string? siteTitle)
    {
        var sb = new StringBuilder(4096);
        var safeSiteTitle = WebUtility.HtmlEncode(siteTitle ?? "DirForge");
        var safeArchiveFileName = WebUtility.HtmlEncode(archiveFileName);
        var safeCurrentPath = string.IsNullOrEmpty(listing.CurrentPath)
            ? "/"
            : "/" + WebUtility.HtmlEncode(listing.CurrentPath);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"  <title>{safeSiteTitle} :: {safeArchiveFileName}{safeCurrentPath}</title>");
        sb.AppendLine($"  <link rel=\"stylesheet\" href=\"{StaticAssetRouteHelper.AssetPath("css/style.css")}\">");
        sb.AppendLine("  <style>body{padding:1rem} .archive-actions{display:flex;gap:.5rem;align-items:center;margin:0 0 1rem 0}.archive-note{opacity:.8;font-size:.95rem;margin:.5rem 0 1rem}.listing td,.listing th{text-align:left}.archive-download{text-decoration:none}</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine("    <h1 class=\"header-title\">Archive Browser</h1>");
        sb.AppendLine("    <div class=\"archive-actions\">");
        sb.AppendLine($"      <a class=\"header-btn\" href=\"{WebUtility.HtmlEncode(BuildParentDirectoryHref(archiveRelativePath, shareToken))}\" title=\"Back to folder\">Back to Folder</a>");
        sb.AppendLine($"      <a class=\"header-btn\" href=\"{WebUtility.HtmlEncode(BuildArchiveBrowseHref(requestPath, string.Empty, shareToken))}\" title=\"Archive root\">Archive Root</a>");
        sb.AppendLine("    </div>");
        sb.AppendLine($"    <div class=\"archive-note\"><strong>{safeArchiveFileName}</strong> &nbsp; Path: <code>{safeCurrentPath}</code></div>");
        sb.AppendLine("    <table class=\"listing\">");
        sb.AppendLine("      <thead><tr><th>Type</th><th>Name</th><th>Modified</th><th>Size</th><th>Actions</th></tr></thead>");
        sb.AppendLine("      <tbody>");

        if (!string.IsNullOrEmpty(listing.CurrentPath))
        {
            sb.AppendLine("        <tr>");
            sb.AppendLine("          <td>dir</td>");
            sb.AppendLine($"          <td><a href=\"{WebUtility.HtmlEncode(BuildArchiveBrowseHref(requestPath, listing.ParentPath, shareToken))}\">..</a></td>");
            sb.AppendLine("          <td></td><td></td><td></td>");
            sb.AppendLine("        </tr>");
        }

        foreach (var entry in listing.Entries)
        {
            var typeLabel = entry.IsDirectory ? "dir" : "file";
            var safeName = WebUtility.HtmlEncode(entry.Name);
            var modified = entry.ModifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd  HH:mm:ss") ?? "-";
            var size = entry.IsDirectory ? "-" : DirectoryListingService.HumanizeSize(entry.Size);
            var href = entry.IsDirectory
                ? BuildArchiveBrowseHref(requestPath, entry.Path, shareToken)
                : BuildArchiveEntryHref(requestPath, entry.Path, shareToken);
            var action = entry.IsDirectory
                ? string.Empty
                : $"<a class=\"archive-download\" href=\"{WebUtility.HtmlEncode(href)}\">Download</a>";

            sb.AppendLine("        <tr>");
            sb.AppendLine($"          <td>{typeLabel}</td>");
            sb.AppendLine($"          <td><a href=\"{WebUtility.HtmlEncode(href)}\">{safeName}</a></td>");
            sb.AppendLine($"          <td>{WebUtility.HtmlEncode(modified)}</td>");
            sb.AppendLine($"          <td>{WebUtility.HtmlEncode(size)}</td>");
            sb.AppendLine($"          <td>{action}</td>");
            sb.AppendLine("        </tr>");
        }

        if (listing.Entries.Count == 0)
        {
            sb.AppendLine("        <tr><td colspan=\"5\" class=\"empty-state\">No archive entries found at this path.</td></tr>");
        }

        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string BuildArchiveBrowseHref(string requestPath, string archivePath, string shareToken)
    {
        var query = new List<string> { "handler=Archive" };
        if (!string.IsNullOrEmpty(archivePath))
        {
            query.Add("ap=" + Uri.EscapeDataString(archivePath));
        }

        ShareLinkService.AppendTokenQuery(query, shareToken);
        return requestPath + "?" + string.Join("&", query);
    }

    private static string BuildArchiveEntryHref(string requestPath, string entryPath, string shareToken)
    {
        var query = new List<string>
        {
            "handler=ArchiveEntry",
            "entryPath=" + Uri.EscapeDataString(entryPath)
        };

        ShareLinkService.AppendTokenQuery(query, shareToken);
        return requestPath + "?" + string.Join("&", query);
    }

    private static string BuildParentDirectoryHref(string archiveRelativePath, string shareToken)
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
}
