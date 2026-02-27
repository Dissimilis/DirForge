using System.Diagnostics;
using System.Text;
using System.Xml;
using DirForge.Models;

namespace DirForge.Services;

public static class WebDavEndpoints
{
    private static readonly ContentTypeResolver ContentType = new();

    public static IApplicationBuilder MapWebDavEndpoints(this IApplicationBuilder app, DirForgeOptions options)
    {
        if (!options.EnableWebDav)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.Equals("/webdav", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/webdav/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            var subPath = path.Length > 7 ? path[7..] : "/";
            if (!subPath.StartsWith('/'))
            {
                subPath = "/" + subPath;
            }

            await HandleWebDavRequestAsync(context, subPath, options);
        });

        return app;
    }

    private static async Task HandleWebDavRequestAsync(HttpContext context, string subPath, DirForgeOptions options)
    {
        var metrics = context.RequestServices.GetService<DashboardMetricsService>();
        metrics?.RecordWebDavRequest();

        var method = context.Request.Method.ToUpperInvariant();
        switch (method)
        {
            case "OPTIONS":
                HandleOptions(context);
                return;
            case "PROPFIND":
                await HandlePropFindAsync(context, subPath, options);
                return;
            case "GET":
            case "HEAD":
                await HandleGetAsync(context, subPath, options, method == "HEAD");
                return;
            default:
                HandleMethodNotAllowed(context);
                return;
        }
    }

    private static void HandleOptions(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["DAV"] = "1";
        context.Response.Headers.Allow = "OPTIONS, PROPFIND, GET, HEAD";
        context.Response.Headers["MS-Author-Via"] = "DAV";
    }

    private static async Task HandlePropFindAsync(HttpContext context, string subPath, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();

        var depthHeader = context.Request.Headers["Depth"].FirstOrDefault();
        int depth;
        if (string.IsNullOrEmpty(depthHeader))
        {
            depth = 1;
        }
        else if (depthHeader == "0")
        {
            depth = 0;
        }
        else if (depthHeader == "1")
        {
            depth = 1;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, Directory.Exists(physicalPath)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var isDirectory = Directory.Exists(physicalPath);
        var isFile = !isDirectory && File.Exists(physicalPath);

        if (!isDirectory && !isFile)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (isFile && listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.StatusCode = 207;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("D", "multistatus", "DAV:");

            if (isFile)
            {
                var fileInfo = new FileInfo(physicalPath);
                WriteFileResponse(writer, relativePath, fileInfo);
            }
            else
            {
                var dirInfo = new DirectoryInfo(physicalPath);
                WriteDirectoryResponse(writer, relativePath, dirInfo);

                if (depth == 1)
                {
                    var opBudgetMs = options.OperationTimeBudgetMs;
                    Stopwatch? opTimer = opBudgetMs > 0 ? Stopwatch.StartNew() : null;
                    var entries = listingService.ReadEntries(physicalPath);
                    foreach (var entry in entries)
                    {
                        if (opTimer is not null && opTimer.ElapsedMilliseconds >= opBudgetMs)
                        {
                            break;
                        }

                        var childRelative = string.IsNullOrEmpty(relativePath)
                            ? entry.Name
                            : relativePath + "/" + entry.Name;
                        var childPhysical = Path.Combine(physicalPath, entry.Name);

                        if (entry.IsDirectory)
                        {
                            var childDirInfo = new DirectoryInfo(childPhysical);
                            WriteDirectoryResponse(writer, childRelative, childDirInfo);
                        }
                        else
                        {
                            if (listingService.IsFileDownloadBlocked(childRelative))
                            {
                                continue;
                            }

                            var childFileInfo = new FileInfo(childPhysical);
                            WriteFileResponse(writer, childRelative, childFileInfo);
                        }
                    }
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        ms.Position = 0;
        context.Response.ContentLength = ms.Length;
        await ms.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task HandleGetAsync(HttpContext context, string subPath, DirForgeOptions options, bool headOnly)
    {
        if (!options.AllowFileDownload)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var relativePath = DirectoryListingService.NormalizeRelativePath(subPath);

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var isDir = Directory.Exists(physicalPath);

        if (listingService.IsPathHiddenByPolicy(relativePath, isDir))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (isDir)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "PROPFIND, OPTIONS";
            return;
        }

        if (!File.Exists(physicalPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            listingService.LogBlockedExtension(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.Request.Path.Value ?? "/");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var fileInfo = new FileInfo(physicalPath);
        var contentType = ContentType.GetContentType(fileInfo.Name);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;
        context.Response.Headers.LastModified = fileInfo.LastWriteTimeUtc.ToString("R");

        if (headOnly)
        {
            return;
        }

        var metrics = context.RequestServices.GetService<DashboardMetricsService>();
        metrics?.RecordWebDavFileDownload(fileInfo.Length);

        await using var stream = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void HandleMethodNotAllowed(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "OPTIONS, PROPFIND, GET, HEAD";
    }

    private static void WriteDirectoryResponse(XmlWriter writer, string relativePath, DirectoryInfo dirInfo)
    {
        writer.WriteStartElement("D", "response", "DAV:");
        writer.WriteElementString("D", "href", "DAV:", BuildWebDavHref(relativePath, isDirectory: true));

        writer.WriteStartElement("D", "propstat", "DAV:");
        writer.WriteStartElement("D", "prop", "DAV:");

        writer.WriteStartElement("D", "resourcetype", "DAV:");
        writer.WriteStartElement("D", "collection", "DAV:");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteElementString("D", "getlastmodified", "DAV:", FormatRfc1123(dirInfo.LastWriteTimeUtc));

        writer.WriteEndElement(); // prop
        writer.WriteElementString("D", "status", "DAV:", "HTTP/1.1 200 OK");
        writer.WriteEndElement(); // propstat

        writer.WriteEndElement(); // response
    }

    private static void WriteFileResponse(XmlWriter writer, string relativePath, FileInfo fileInfo)
    {
        writer.WriteStartElement("D", "response", "DAV:");
        writer.WriteElementString("D", "href", "DAV:", BuildWebDavHref(relativePath, isDirectory: false));

        writer.WriteStartElement("D", "propstat", "DAV:");
        writer.WriteStartElement("D", "prop", "DAV:");

        writer.WriteStartElement("D", "resourcetype", "DAV:");
        writer.WriteEndElement();

        writer.WriteElementString("D", "getcontentlength", "DAV:", fileInfo.Length.ToString());
        writer.WriteElementString("D", "getcontenttype", "DAV:", ContentType.GetContentType(fileInfo.Name));
        writer.WriteElementString("D", "getlastmodified", "DAV:", FormatRfc1123(fileInfo.LastWriteTimeUtc));

        writer.WriteEndElement(); // prop
        writer.WriteElementString("D", "status", "DAV:", "HTTP/1.1 200 OK");
        writer.WriteEndElement(); // propstat

        writer.WriteEndElement(); // response
    }

    private static string BuildWebDavHref(string relativePath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return "/webdav/";
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var encoded = string.Join("/", segments.Select(Uri.EscapeDataString));
        return isDirectory ? $"/webdav/{encoded}/" : $"/webdav/{encoded}";
    }

    private static string FormatRfc1123(DateTime dateTime)
    {
        return dateTime.ToUniversalTime().ToString("R");
    }
}
