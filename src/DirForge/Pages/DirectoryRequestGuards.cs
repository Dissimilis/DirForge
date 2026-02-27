using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DirForge.Pages;

public sealed class DirectoryRequestGuards
{
    private readonly DirectoryListingService _directoryListingService;
    private readonly DirForgeOptions _options;
    private readonly ILogger<DirectoryListingModel> _logger;

    public DirectoryRequestGuards(
        DirectoryListingService directoryListingService,
        DirForgeOptions options,
        ILogger<DirectoryListingModel> logger)
    {
        _directoryListingService = directoryListingService;
        _options = options;
        _logger = logger;
    }

    public ShareAccessContext? GetShareContext(HttpContext httpContext)
    {
        return httpContext.Items.TryGetValue(ShareLinkService.HttpContextItemKey, out var value)
            ? value as ShareAccessContext
            : null;
    }

    public bool TryResolvePhysicalPath(
        HttpContext httpContext,
        string? requestPath,
        out string relativePath,
        out string physicalPath)
    {
        relativePath = DirectoryListingService.NormalizeRelativePath(requestPath);
        physicalPath = _directoryListingService.ResolvePhysicalPath(relativePath) ?? string.Empty;

        if (!string.IsNullOrEmpty(physicalPath))
        {
            return true;
        }

        _logger.LogWarning("[SECURITY] PATH_TRAVERSAL ClientIP={ClientIp} Path={RequestPath} Resolved path is null",
            GetClientIp(httpContext), httpContext.Request.Path.Value ?? "/");
        return false;
    }

    public bool IsDirectoryShareScopeViolation(ShareAccessContext? shareContext, string physicalPath)
    {
        return shareContext?.Mode == ShareMode.Directory &&
               !_directoryListingService.IsCanonicallyWithinScope(physicalPath, shareContext.ScopePath);
    }

    public bool IsFileDownloadAllowed(string relativePath)
    {
        return _options.AllowFileDownload &&
               !_directoryListingService.IsFileDownloadBlocked(relativePath);
    }

    public IActionResult CreateShareScopeForbidden(HttpContext httpContext)
    {
        _logger.LogWarning("[SECURITY] SHARE_SCOPE_VIOLATION ClientIP={ClientIp} Path={RequestPath} Outside share scope",
            GetClientIp(httpContext), httpContext.Request.Path.Value ?? "/");
        return new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    public IActionResult CreateBlockedExtensionNotFound(HttpContext httpContext)
    {
        _directoryListingService.LogBlockedExtension(
            GetClientIp(httpContext), httpContext.Request.Path.Value ?? "/");
        return new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    public bool IsTrustedShareLinkRequest(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("X-DirForge-Share", out var shareHeader) ||
            shareHeader.Count != 1 ||
            !shareHeader[0]!.Equals("1", StringComparison.Ordinal))
        {
            return false;
        }

        if (request.Headers.TryGetValue("Sec-Fetch-Site", out var secFetchSiteValues) &&
            secFetchSiteValues.Count > 0)
        {
            var secFetchSite = secFetchSiteValues[0] ?? string.Empty;
            if (secFetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return IsSameOriginIfPresent(request, HeaderNames.Origin) &&
               IsSameOriginIfPresent(request, HeaderNames.Referer);
    }

    private string GetClientIp(HttpContext httpContext)
    {
        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static bool IsSameOriginIfPresent(HttpRequest request, string headerName)
    {
        if (!request.Headers.TryGetValue(headerName, out var values) ||
            values.Count == 0 ||
            string.IsNullOrWhiteSpace(values[0]))
        {
            return true;
        }

        if (!Uri.TryCreate(values[0], UriKind.Absolute, out var uri))
        {
            return false;
        }

        var expectedPort = request.Host.Port ??
                           (request.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var actualPort = uri.IsDefaultPort
            ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
            : uri.Port;

        return uri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
               actualPort == expectedPort;
    }
}
