using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DirForge.Models;

namespace DirForge.Services;

public static class S3Endpoints
{
    private static readonly ContentTypeResolver ContentType = new();

    public static IApplicationBuilder MapS3Endpoints(this IApplicationBuilder app, DirForgeOptions options)
    {
        if (!options.EnableS3Endpoint)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.Equals("/s3", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/s3/", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            await HandleS3RequestAsync(context, path, options);
        });

        return app;
    }

    private static async Task HandleS3RequestAsync(HttpContext context, string path, DirForgeOptions options)
    {
        var metrics = context.RequestServices.GetService<DashboardMetricsService>();
        metrics?.RecordS3Request();

        // Auth check via SigV4
        var authResult = S3SigV4Auth.Validate(context.Request,
            options.ResolvedS3AccessKeyId, options.ResolvedS3SecretAccessKey, options.S3Region);
        if (!authResult.IsValid)
        {
            var statusCode = authResult.ErrorCode switch
            {
                "RequestTimeTooSkewed" => 403,
                "InvalidAccessKeyId" => 403,
                "SignatureDoesNotMatch" => 403,
                "MissingSecurityHeader" => 403,
                "AuthorizationHeaderMalformed" => 400,
                _ => 403
            };
            await WriteS3ErrorAsync(context, statusCode, authResult.ErrorCode!, authResult.ErrorMessage!);
            return;
        }

        var method = context.Request.Method.ToUpperInvariant();
        if (method != "GET" && method != "HEAD")
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DirForge.S3");
            logger.LogWarning("[S3] Rejected {Method} {Path} from {ClientIp} — DirForge S3 endpoint is read-only.",
                method, path, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            await WriteS3ErrorAsync(context, 405, "MethodNotAllowed",
                "DirForge S3 endpoint is read-only. PUT, POST, and DELETE operations are not supported.");
            return;
        }

        // Parse path: /s3, /s3/, /s3/{bucket}, /s3/{bucket}/{key...}
        var subPath = path.Length > 3 ? path[3..] : string.Empty;
        if (subPath.StartsWith('/'))
        {
            subPath = subPath[1..];
        }

        // Root level: ListBuckets
        if (string.IsNullOrEmpty(subPath))
        {
            await HandleListBucketsAsync(context, options);
            return;
        }

        // Split into bucket and key
        var slashIndex = subPath.IndexOf('/');
        string bucketName;
        string objectKey;
        if (slashIndex < 0)
        {
            bucketName = Uri.UnescapeDataString(subPath);
            objectKey = string.Empty;
        }
        else
        {
            bucketName = Uri.UnescapeDataString(subPath[..slashIndex]);
            objectKey = Uri.UnescapeDataString(subPath[(slashIndex + 1)..]);
        }

        // Validate bucket name
        if (!string.Equals(bucketName, options.S3BucketName, StringComparison.Ordinal))
        {
            await WriteS3ErrorAsync(context, 404, "NoSuchBucket", $"The specified bucket does not exist.");
            return;
        }

        // Bucket-level queries
        if (string.IsNullOrEmpty(objectKey))
        {
            if (context.Request.Query.ContainsKey("location"))
            {
                await HandleGetBucketLocationAsync(context, options);
                return;
            }

            await HandleListObjectsV2Async(context, options);
            return;
        }

        // Object-level
        if (method == "HEAD")
        {
            await HandleHeadObjectAsync(context, objectKey, options);
        }
        else
        {
            await HandleGetObjectAsync(context, objectKey, options);
        }
    }

    private static async Task HandleListBucketsAsync(HttpContext context, DirForgeOptions options)
    {
        DateTime creationDate;
        try
        {
            creationDate = new DirectoryInfo(options.RootPath).LastWriteTimeUtc;
        }
        catch
        {
            creationDate = DateTime.UtcNow;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = CreateXmlWriter(ms))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ListAllMyBucketsResult", "http://s3.amazonaws.com/doc/2006-03-01/");

            writer.WriteStartElement("Owner");
            writer.WriteElementString("ID", "dirforge");
            writer.WriteElementString("DisplayName", "dirforge");
            writer.WriteEndElement();

            writer.WriteStartElement("Buckets");
            writer.WriteStartElement("Bucket");
            writer.WriteElementString("Name", options.S3BucketName);
            writer.WriteElementString("CreationDate", creationDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            writer.WriteEndElement(); // Bucket
            writer.WriteEndElement(); // Buckets

            writer.WriteEndElement(); // ListAllMyBucketsResult
            writer.WriteEndDocument();
        }

        await WriteXmlResponseAsync(context, ms);
    }

    private static async Task HandleGetBucketLocationAsync(HttpContext context, DirForgeOptions options)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = CreateXmlWriter(ms))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("LocationConstraint", "http://s3.amazonaws.com/doc/2006-03-01/");
            writer.WriteString(options.S3Region);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        await WriteXmlResponseAsync(context, ms);
    }

    private static async Task HandleListObjectsV2Async(HttpContext context, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();

        var prefix = context.Request.Query["prefix"].FirstOrDefault() ?? string.Empty;
        var delimiter = context.Request.Query["delimiter"].FirstOrDefault() ?? string.Empty;
        var maxKeysStr = context.Request.Query["max-keys"].FirstOrDefault();
        var continuationToken = context.Request.Query["continuation-token"].FirstOrDefault();
        var startAfter = context.Request.Query["start-after"].FirstOrDefault() ?? string.Empty;

        int maxKeys = 1000;
        if (!string.IsNullOrEmpty(maxKeysStr) && int.TryParse(maxKeysStr, out var parsedMaxKeys))
        {
            maxKeys = Math.Clamp(parsedMaxKeys, 0, 1000);
        }

        // Decode continuation token (base64url-encoded last key)
        var marker = string.Empty;
        if (!string.IsNullOrEmpty(continuationToken))
        {
            try
            {
                marker = Encoding.UTF8.GetString(Convert.FromBase64String(
                    continuationToken.Replace('-', '+').Replace('_', '/')));
            }
            catch
            {
                await WriteS3ErrorAsync(context, 400, "InvalidArgument", "Invalid continuation token.");
                return;
            }
        }

        if (string.IsNullOrEmpty(marker) && !string.IsNullOrEmpty(startAfter))
        {
            marker = startAfter;
        }

        // Enumerate entries
        var useDelimiter = delimiter == "/";
        var allEntries = new List<S3ObjectEntry>();
        var commonPrefixes = new SortedSet<string>(StringComparer.Ordinal);

        // Determine the directory to list based on prefix
        var prefixDir = string.Empty;
        var prefixFilter = prefix;

        if (useDelimiter && prefix.Length > 0)
        {
            var lastSlash = prefix.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                prefixDir = prefix[..lastSlash];
                prefixFilter = prefix[(lastSlash + 1)..];
            }
        }
        else if (!useDelimiter && prefix.Length > 0)
        {
            // For recursive listing, start from the prefix directory portion
            var lastSlash = prefix.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                prefixDir = prefix[..lastSlash];
                prefixFilter = prefix[(lastSlash + 1)..];
            }
        }

        var relativePath = DirectoryListingService.NormalizeRelativePath(prefixDir);
        var physicalPath = listingService.ResolvePhysicalPath(relativePath);

        if (physicalPath is null || !Directory.Exists(physicalPath))
        {
            // Empty result — prefix doesn't match any directory
            await WriteListObjectsV2ResultAsync(context, options, prefix, delimiter, maxKeys,
                continuationToken, startAfter, [], [], false, null);
            return;
        }

        if (options.HideDotfiles && !string.IsNullOrEmpty(relativePath) &&
            DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            await WriteListObjectsV2ResultAsync(context, options, prefix, delimiter, maxKeys,
                continuationToken, startAfter, [], [], false, null);
            return;
        }

        if (useDelimiter)
        {
            // Single-level listing
            var entries = listingService.ReadEntries(physicalPath);
            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(prefixFilter) &&
                    !entry.Name.StartsWith(prefixFilter, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    var cpKey = string.IsNullOrEmpty(prefixDir)
                        ? entry.Name + "/"
                        : prefixDir + "/" + entry.Name + "/";
                    commonPrefixes.Add(cpKey);
                }
                else
                {
                    if (listingService.IsFileDownloadBlocked(
                            string.IsNullOrEmpty(relativePath) ? entry.Name : relativePath + "/" + entry.Name))
                    {
                        continue;
                    }

                    var childPhysical = Path.Combine(physicalPath, entry.Name);
                    var fileInfo = new FileInfo(childPhysical);
                    var key = string.IsNullOrEmpty(prefixDir)
                        ? entry.Name
                        : prefixDir + "/" + entry.Name;

                    allEntries.Add(new S3ObjectEntry(
                        key,
                        fileInfo.LastWriteTimeUtc,
                        fileInfo.Length,
                        ComputeETag(fileInfo)));
                }
            }
        }
        else
        {
            // Recursive listing
            var opBudgetMs = options.OperationTimeBudgetMs;
            Stopwatch? opTimer = opBudgetMs > 0 ? Stopwatch.StartNew() : null;
            EnumerateRecursive(listingService, physicalPath, relativePath, prefix, allEntries, options, opTimer, opBudgetMs);
        }

        // Sort lexicographically
        allEntries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        // Apply marker
        if (!string.IsNullOrEmpty(marker))
        {
            var startIndex = 0;
            for (var i = 0; i < allEntries.Count; i++)
            {
                if (string.Compare(allEntries[i].Key, marker, StringComparison.Ordinal) > 0)
                {
                    startIndex = i;
                    break;
                }

                startIndex = allEntries.Count;
            }

            allEntries = allEntries.GetRange(startIndex, allEntries.Count - startIndex);

            // Also filter common prefixes
            commonPrefixes.RemoveWhere(cp => string.Compare(cp, marker, StringComparison.Ordinal) <= 0);
        }

        // Truncate
        var totalCount = allEntries.Count + commonPrefixes.Count;
        var isTruncated = totalCount > maxKeys;
        string? nextContinuationToken = null;

        if (isTruncated)
        {
            // Combine entries and common prefixes, take maxKeys items
            var combined = new List<string>();
            var entryIdx = 0;
            using var cpEnum = commonPrefixes.GetEnumerator();
            var hasCp = cpEnum.MoveNext();

            while (combined.Count < maxKeys)
            {
                var hasEntry = entryIdx < allEntries.Count;
                if (!hasEntry && !hasCp)
                {
                    break;
                }

                if (hasEntry && (!hasCp || string.Compare(allEntries[entryIdx].Key, cpEnum.Current, StringComparison.Ordinal) <= 0))
                {
                    combined.Add(allEntries[entryIdx].Key);
                    entryIdx++;
                }
                else if (hasCp)
                {
                    combined.Add(cpEnum.Current);
                    hasCp = cpEnum.MoveNext();
                }
            }

            if (combined.Count > 0)
            {
                var lastKey = combined[^1];
                nextContinuationToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(lastKey))
                    .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            }

            // Trim allEntries to only include keys that made it
            allEntries = allEntries.GetRange(0, Math.Min(entryIdx, allEntries.Count));
            var retainedPrefixes = new SortedSet<string>(StringComparer.Ordinal);
            using var cpEnum2 = commonPrefixes.GetEnumerator();
            var count = allEntries.Count;
            while (count < maxKeys && cpEnum2.MoveNext())
            {
                retainedPrefixes.Add(cpEnum2.Current);
                count++;
            }

            commonPrefixes = retainedPrefixes;
        }

        await WriteListObjectsV2ResultAsync(context, options, prefix, delimiter, maxKeys,
            continuationToken, startAfter, allEntries, commonPrefixes, isTruncated, nextContinuationToken);
    }

    private static void EnumerateRecursive(
        DirectoryListingService listingService,
        string physicalPath,
        string relativePath,
        string prefix,
        List<S3ObjectEntry> results,
        DirForgeOptions options,
        Stopwatch? timeBudget = null,
        int timeBudgetMs = 0)
    {
        if (timeBudget is not null && timeBudget.ElapsedMilliseconds >= timeBudgetMs)
        {
            return;
        }

        List<DirectoryEntry> entries;
        try
        {
            entries = listingService.ReadEntries(physicalPath);
        }
        catch
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (timeBudget is not null && timeBudget.ElapsedMilliseconds >= timeBudgetMs)
            {
                return;
            }

            var childRelative = string.IsNullOrEmpty(relativePath)
                ? entry.Name
                : relativePath + "/" + entry.Name;

            if (entry.IsDirectory)
            {
                var childPhysical = Path.Combine(physicalPath, entry.Name);
                EnumerateRecursive(listingService, childPhysical, childRelative, prefix, results, options, timeBudget, timeBudgetMs);
            }
            else
            {
                if (listingService.IsFileDownloadBlocked(childRelative))
                {
                    continue;
                }

                // Apply prefix filter
                if (!string.IsNullOrEmpty(prefix) &&
                    !childRelative.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var childPhysical = Path.Combine(physicalPath, entry.Name);
                var fileInfo = new FileInfo(childPhysical);
                results.Add(new S3ObjectEntry(
                    childRelative,
                    fileInfo.LastWriteTimeUtc,
                    fileInfo.Length,
                    ComputeETag(fileInfo)));
            }
        }
    }

    private static async Task WriteListObjectsV2ResultAsync(
        HttpContext context,
        DirForgeOptions options,
        string prefix,
        string delimiter,
        int maxKeys,
        string? continuationToken,
        string startAfter,
        List<S3ObjectEntry> entries,
        IEnumerable<string> commonPrefixes,
        bool isTruncated,
        string? nextContinuationToken)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = CreateXmlWriter(ms))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("ListBucketResult", "http://s3.amazonaws.com/doc/2006-03-01/");

            writer.WriteElementString("Name", options.S3BucketName);
            writer.WriteElementString("Prefix", prefix);
            writer.WriteElementString("KeyCount", entries.Count.ToString());
            writer.WriteElementString("MaxKeys", maxKeys.ToString());
            writer.WriteElementString("IsTruncated", isTruncated ? "true" : "false");

            if (!string.IsNullOrEmpty(delimiter))
            {
                writer.WriteElementString("Delimiter", delimiter);
            }

            if (!string.IsNullOrEmpty(startAfter))
            {
                writer.WriteElementString("StartAfter", startAfter);
            }

            if (!string.IsNullOrEmpty(continuationToken))
            {
                writer.WriteElementString("ContinuationToken", continuationToken);
            }

            if (!string.IsNullOrEmpty(nextContinuationToken))
            {
                writer.WriteElementString("NextContinuationToken", nextContinuationToken);
            }

            writer.WriteElementString("EncodingType", "url");

            foreach (var entry in entries)
            {
                writer.WriteStartElement("Contents");
                writer.WriteElementString("Key", entry.Key);
                writer.WriteElementString("LastModified", entry.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                writer.WriteElementString("ETag", entry.ETag);
                writer.WriteElementString("Size", entry.Size.ToString());
                writer.WriteElementString("StorageClass", "STANDARD");
                writer.WriteEndElement(); // Contents
            }

            foreach (var cp in commonPrefixes)
            {
                writer.WriteStartElement("CommonPrefixes");
                writer.WriteElementString("Prefix", cp);
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // ListBucketResult
            writer.WriteEndDocument();
        }

        await WriteXmlResponseAsync(context, ms);
    }

    private static async Task HandleGetObjectAsync(HttpContext context, string objectKey, DirForgeOptions options)
    {
        var (fileInfo, statusCode, errorCode, errorMessage) = ResolveObjectFile(context, objectKey, options);
        if (fileInfo is null)
        {
            await WriteS3ErrorAsync(context, statusCode, errorCode!, errorMessage!);
            return;
        }

        var contentType = ContentType.GetContentType(fileInfo.Name);
        var etag = ComputeETag(fileInfo);

        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers.LastModified = fileInfo.LastWriteTimeUtc.ToString("R");
        context.Response.Headers.ETag = etag;
        context.Response.ContentType = contentType;

        // Range support
        var rangeHeader = context.Request.Headers.Range.FirstOrDefault();
        if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, fileInfo.Length, out var rangeStart, out var rangeEnd))
        {
            if (rangeStart < 0 || rangeEnd >= fileInfo.Length || rangeStart > rangeEnd)
            {
                context.Response.StatusCode = 416;
                context.Response.Headers.ContentRange = $"bytes */{fileInfo.Length}";
                return;
            }

            var rangeLength = rangeEnd - rangeStart + 1;
            context.Response.StatusCode = 206;
            context.Response.ContentLength = rangeLength;
            context.Response.Headers.ContentRange = $"bytes {rangeStart}-{rangeEnd}/{fileInfo.Length}";

            var metrics = context.RequestServices.GetService<DashboardMetricsService>();
            metrics?.RecordS3FileDownload(rangeLength);

            await using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(rangeStart, SeekOrigin.Begin);
            await CopyBytesAsync(stream, context.Response.Body, rangeLength, context.RequestAborted);
        }
        else if (!string.IsNullOrEmpty(rangeHeader))
        {
            // Unparseable range header — return full content (per HTTP spec, ignore invalid ranges)
            context.Response.StatusCode = 200;
            context.Response.ContentLength = fileInfo.Length;

            var metrics = context.RequestServices.GetService<DashboardMetricsService>();
            metrics?.RecordS3FileDownload(fileInfo.Length);

            await using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        else
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength = fileInfo.Length;

            var metrics = context.RequestServices.GetService<DashboardMetricsService>();
            metrics?.RecordS3FileDownload(fileInfo.Length);

            await using var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    private static async Task HandleHeadObjectAsync(HttpContext context, string objectKey, DirForgeOptions options)
    {
        var (fileInfo, statusCode, errorCode, errorMessage) = ResolveObjectFile(context, objectKey, options);
        if (fileInfo is null)
        {
            await WriteS3ErrorAsync(context, statusCode, errorCode!, errorMessage!);
            return;
        }

        var contentType = ContentType.GetContentType(fileInfo.Name);
        var etag = ComputeETag(fileInfo);

        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;
        context.Response.Headers["Accept-Ranges"] = "bytes";
        context.Response.Headers.LastModified = fileInfo.LastWriteTimeUtc.ToString("R");
        context.Response.Headers.ETag = etag;
    }

    private static (FileInfo? FileInfo, int StatusCode, string? ErrorCode, string? ErrorMessage) ResolveObjectFile(
        HttpContext context, string objectKey, DirForgeOptions options)
    {
        var listingService = context.RequestServices.GetRequiredService<DirectoryListingService>();
        var relativePath = DirectoryListingService.NormalizeRelativePath(objectKey);

        if (string.IsNullOrEmpty(relativePath))
        {
            return (null, 404, "NoSuchKey", "The specified key does not exist.");
        }

        if (options.HideDotfiles && DirectoryListingService.ContainsDotPathSegment(relativePath))
        {
            return (null, 404, "NoSuchKey", "The specified key does not exist.");
        }

        var physicalPath = listingService.ResolvePhysicalPath(relativePath);
        if (physicalPath is null)
        {
            return (null, 404, "NoSuchKey", "The specified key does not exist.");
        }

        if (Directory.Exists(physicalPath))
        {
            return (null, 404, "NoSuchKey", "The specified key does not exist.");
        }

        if (!File.Exists(physicalPath))
        {
            return (null, 404, "NoSuchKey", "The specified key does not exist.");
        }

        if (listingService.IsPathHiddenByPolicy(relativePath, isDirectory: false))
        {
            return (null, 404, "NoSuchKey", "The specified key does not exist.");
        }

        if (listingService.IsFileDownloadBlocked(relativePath))
        {
            return (null, 403, "AccessDenied", "Access Denied.");
        }

        return (new FileInfo(physicalPath), 200, null, null);
    }

    private static bool TryParseRange(string rangeHeader, long fileLength, out long start, out long end)
    {
        start = 0;
        end = fileLength - 1;

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rangeSpec = rangeHeader["bytes=".Length..];
        // Only support single range
        if (rangeSpec.Contains(','))
        {
            return false;
        }

        var dashIndex = rangeSpec.IndexOf('-');
        if (dashIndex < 0)
        {
            return false;
        }

        var startStr = rangeSpec[..dashIndex].Trim();
        var endStr = rangeSpec[(dashIndex + 1)..].Trim();

        if (string.IsNullOrEmpty(startStr))
        {
            // Suffix range: -N means last N bytes
            if (!long.TryParse(endStr, out var suffixLength) || suffixLength <= 0)
            {
                return false;
            }

            start = Math.Max(0, fileLength - suffixLength);
            end = fileLength - 1;
            return true;
        }

        if (!long.TryParse(startStr, out start))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(endStr))
        {
            if (!long.TryParse(endStr, out end))
            {
                return false;
            }
        }
        else
        {
            end = fileLength - 1;
        }

        return start >= 0 && end >= start && start < fileLength;
    }

    private static async Task CopyBytesAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static string ComputeETag(FileInfo fileInfo)
    {
        // Cheap, deterministic ETag: MD5 of "{ticks}-{size}"
        var input = $"{fileInfo.LastWriteTimeUtc.Ticks}-{fileInfo.Length}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return $"\"{Convert.ToHexStringLower(hash)}\"";
    }

    private static async Task WriteS3ErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml; charset=utf-8";

        using var ms = new MemoryStream();
        using (var writer = CreateXmlWriter(ms))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Error");
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            writer.WriteElementString("RequestId", Guid.NewGuid().ToString("N"));
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        await WriteXmlResponseAsync(context, ms);
    }

    private static XmlWriter CreateXmlWriter(MemoryStream ms)
    {
        return XmlWriter.Create(ms, new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        });
    }

    private static async Task WriteXmlResponseAsync(HttpContext context, MemoryStream ms)
    {
        ms.Position = 0;
        context.Response.ContentLength = ms.Length;
        await ms.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private sealed record S3ObjectEntry(string Key, DateTime LastModified, long Size, string ETag);
}
