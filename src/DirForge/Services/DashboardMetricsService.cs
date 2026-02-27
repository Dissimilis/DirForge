using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DirForge.Models;

namespace DirForge.Services;

public sealed class DashboardMetricsService
{
    private const int UsageCapacity = 200;
    private const int DurationCapacity = 1000;
    private const int TrafficWindowSeconds = 300;

    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly ConcurrentDictionary<int, long> _statusCounts = new();
    private readonly ConcurrentDictionary<string, long> _endpointCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DashboardUsageEvent> _latestUsage = new();
    private readonly Queue<double> _recentDurationsMs = new();
    private readonly long[] _trafficCountsBySecond = new long[TrafficWindowSeconds];
    private readonly long[] _trafficBucketUnixSeconds = new long[TrafficWindowSeconds];
    private readonly object _usageSync = new();
    private readonly object _durationSync = new();
    private readonly object _trafficSync = new();

    private long _totalRequests;
    private long _inFlightRequests;
    private long _totalDurationMs;
    private long _durationCount;
    private long _searchRequests;
    private long _searchTotalDurationMs;
    private long _fileDownloadCount;
    private long _fileDownloadBytes;
    private long _zipDownloadCount;
    private long _zipDownloadBytes;
    private long _archiveBrowseCount;
    private long _archiveInnerDownloadCount;
    private long _archiveInnerDownloadBytes;
    private long _shareLinkCreatedCount;
    private long _shareOneTimeConsumedCount;
    private long _shareTokenRejectedCount;
    private long _shareUnauthorizedCount;
    private long _authFailureCount;
    private long _dashboardAuthFailureCount;
    private long _webDavRequestCount;
    private long _webDavFileDownloadCount;
    private long _webDavFileDownloadBytes;
    private long _s3RequestCount;
    private long _s3FileDownloadCount;
    private long _s3FileDownloadBytes;
    private long _apiRequestCount;
    private long _apiFileDownloadCount;
    private long _apiFileDownloadBytes;
    private long _mcpRequestCount;
    private long _mcpToolCallCount;
    private long _rateLimitRejectionCount;
    private long _healthProbeCount;
    private long _readyProbeCount;
    private DateTimeOffset? _lastReadyProbeUtc;
    private DateTimeOffset? _lastHealthProbeUtc;

    public long BeginRequest()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _inFlightRequests);
        return Stopwatch.GetTimestamp();
    }

    public void EndRequest(HttpContext context, long startTimestamp, Exception? exception = null)
    {
        Interlocked.Decrement(ref _inFlightRequests);
        RecordTrafficPoint(DateTimeOffset.UtcNow);

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var durationIntegral = (long)Math.Round(elapsedMs, MidpointRounding.AwayFromZero);
        Interlocked.Add(ref _totalDurationMs, Math.Max(0L, durationIntegral));
        Interlocked.Increment(ref _durationCount);

        lock (_durationSync)
        {
            if (_recentDurationsMs.Count >= DurationCapacity)
            {
                _recentDurationsMs.Dequeue();
            }

            _recentDurationsMs.Enqueue(Math.Max(0d, elapsedMs));
        }

        var statusCode = exception is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
        _statusCounts.AddOrUpdate(statusCode, 1, static (_, existing) => existing + 1);
        var endpoint = ClassifyEndpoint(context.Request.Path, context.Request.Query["handler"], context.Request.Method);
        _endpointCounts.AddOrUpdate(endpoint, 1, static (_, existing) => existing + 1);

        if (endpoint.Equals("static", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var usageEvent = new DashboardUsageEvent(
            TimestampUtc: DateTimeOffset.UtcNow,
            Method: context.Request.Method,
            Path: context.Request.Path.Value ?? "/",
            Endpoint: endpoint,
            Handler: context.Request.Query["handler"].ToString(),
            StatusCode: statusCode,
            DurationMs: elapsedMs,
            ClientIp: MaskIp(context.Connection.RemoteIpAddress),
            UserAgent: Truncate(context.Request.Headers.UserAgent.ToString(), 140));

        lock (_usageSync)
        {
            if (_latestUsage.Count >= UsageCapacity)
            {
                _latestUsage.Dequeue();
            }

            _latestUsage.Enqueue(usageEvent);
        }
    }

    public void RecordSearch(long elapsedMs)
    {
        Interlocked.Increment(ref _searchRequests);
        Interlocked.Add(ref _searchTotalDurationMs, Math.Max(0L, elapsedMs));
    }

    public void RecordFileDownload(long fileSizeBytes)
    {
        Interlocked.Increment(ref _fileDownloadCount);
        Interlocked.Add(ref _fileDownloadBytes, Math.Max(0L, fileSizeBytes));
    }

    public void RecordZipDownload(long zipSourceBytes)
    {
        Interlocked.Increment(ref _zipDownloadCount);
        Interlocked.Add(ref _zipDownloadBytes, Math.Max(0L, zipSourceBytes));
    }

    public void RecordArchiveBrowse()
    {
        Interlocked.Increment(ref _archiveBrowseCount);
    }

    public void RecordArchiveInnerDownload(long bytes)
    {
        Interlocked.Increment(ref _archiveInnerDownloadCount);
        Interlocked.Add(ref _archiveInnerDownloadBytes, Math.Max(0L, bytes));
    }

    public void RecordShareLinkCreated()
    {
        Interlocked.Increment(ref _shareLinkCreatedCount);
    }

    public void RecordShareOneTimeConsumed()
    {
        Interlocked.Increment(ref _shareOneTimeConsumedCount);
    }

    public void RecordShareTokenRejected()
    {
        Interlocked.Increment(ref _shareTokenRejectedCount);
    }

    public void RecordShareUnauthorized()
    {
        Interlocked.Increment(ref _shareUnauthorizedCount);
    }

    public void RecordAuthFailure()
    {
        Interlocked.Increment(ref _authFailureCount);
    }

    public void RecordDashboardAuthFailure()
    {
        Interlocked.Increment(ref _dashboardAuthFailureCount);
    }

    public void RecordWebDavRequest()
    {
        Interlocked.Increment(ref _webDavRequestCount);
    }

    public void RecordWebDavFileDownload(long bytes)
    {
        Interlocked.Increment(ref _webDavFileDownloadCount);
        Interlocked.Add(ref _webDavFileDownloadBytes, Math.Max(0L, bytes));
    }

    public void RecordS3Request()
    {
        Interlocked.Increment(ref _s3RequestCount);
    }

    public void RecordS3FileDownload(long bytes)
    {
        Interlocked.Increment(ref _s3FileDownloadCount);
        Interlocked.Add(ref _s3FileDownloadBytes, Math.Max(0L, bytes));
    }

    public void RecordApiRequest()
    {
        Interlocked.Increment(ref _apiRequestCount);
    }

    public void RecordApiFileDownload(long bytes)
    {
        Interlocked.Increment(ref _apiFileDownloadCount);
        Interlocked.Add(ref _apiFileDownloadBytes, Math.Max(0L, bytes));
    }

    public void RecordMcpRequest()
    {
        Interlocked.Increment(ref _mcpRequestCount);
    }

    public void RecordMcpToolCall()
    {
        Interlocked.Increment(ref _mcpToolCallCount);
    }

    public void RecordRateLimitRejection()
    {
        Interlocked.Increment(ref _rateLimitRejectionCount);
    }

    public void RecordHealthProbe()
    {
        Interlocked.Increment(ref _healthProbeCount);
        _lastHealthProbeUtc = DateTimeOffset.UtcNow;
    }

    public void RecordReadyProbe()
    {
        Interlocked.Increment(ref _readyProbeCount);
        _lastReadyProbeUtc = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<DashboardTrafficPoint> GetTrafficSeries(DateTimeOffset nowUtc)
    {
        var nowUnix = nowUtc.ToUnixTimeSeconds();
        var points = new DashboardTrafficPoint[TrafficWindowSeconds];
        lock (_trafficSync)
        {
            for (var i = 0; i < TrafficWindowSeconds; i++)
            {
                var second = nowUnix - (TrafficWindowSeconds - 1 - i);
                var slot = GetTrafficSlot(second);
                var count = _trafficBucketUnixSeconds[slot] == second
                    ? _trafficCountsBySecond[slot]
                    : 0L;
                points[i] = new DashboardTrafficPoint(second, count);
            }
        }

        return points;
    }

    public void ResetDashboardViewState()
    {
        lock (_usageSync)
        {
            _latestUsage.Clear();
        }

        lock (_trafficSync)
        {
            Array.Clear(_trafficCountsBySecond);
            Array.Clear(_trafficBucketUnixSeconds);
        }
    }

    public DashboardMetricsSnapshot CreateSnapshot()
    {
        var uptime = DateTimeOffset.UtcNow - _startedAtUtc;
        var totalRequests = Interlocked.Read(ref _totalRequests);
        var durationCount = Interlocked.Read(ref _durationCount);
        var totalDurationMs = Interlocked.Read(ref _totalDurationMs);

        var averageLatencyMs = durationCount > 0
            ? totalDurationMs / (double)durationCount
            : 0d;

        double p50;
        double p95;
        lock (_durationSync)
        {
            var durations = _recentDurationsMs.ToArray();
            Array.Sort(durations);
            p50 = Percentile(durations, 0.50);
            p95 = Percentile(durations, 0.95);
        }

        DashboardUsageEvent[] usage;
        lock (_usageSync)
        {
            usage = _latestUsage.Reverse().ToArray();
        }

        var statusCounts = _statusCounts
            .OrderBy(kvp => kvp.Key)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var endpointCounts = _endpointCounts
            .OrderByDescending(kvp => kvp.Value)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var minutes = Math.Max(uptime.TotalMinutes, 1d / 60d);
        var searchRequests = Interlocked.Read(ref _searchRequests);
        var searchAverageMs = searchRequests > 0
            ? Interlocked.Read(ref _searchTotalDurationMs) / (double)searchRequests
            : 0d;

        return new DashboardMetricsSnapshot
        {
            StartedAtUtc = _startedAtUtc,
            Uptime = uptime,
            TotalRequests = totalRequests,
            InFlightRequests = Interlocked.Read(ref _inFlightRequests),
            RequestsPerMinute = totalRequests / minutes,
            AverageLatencyMs = averageLatencyMs,
            P50LatencyMs = p50,
            P95LatencyMs = p95,
            StatusCounts = statusCounts,
            EndpointCounts = endpointCounts,
            LatestUsage = usage,
            SearchRequests = searchRequests,
            AverageSearchMs = searchAverageMs,
            FileDownloadCount = Interlocked.Read(ref _fileDownloadCount),
            FileDownloadBytes = Interlocked.Read(ref _fileDownloadBytes),
            ZipDownloadCount = Interlocked.Read(ref _zipDownloadCount),
            ZipDownloadBytes = Interlocked.Read(ref _zipDownloadBytes),
            ArchiveBrowseCount = Interlocked.Read(ref _archiveBrowseCount),
            ArchiveInnerDownloadCount = Interlocked.Read(ref _archiveInnerDownloadCount),
            ArchiveInnerDownloadBytes = Interlocked.Read(ref _archiveInnerDownloadBytes),
            ShareLinkCreatedCount = Interlocked.Read(ref _shareLinkCreatedCount),
            ShareOneTimeConsumedCount = Interlocked.Read(ref _shareOneTimeConsumedCount),
            ShareTokenRejectedCount = Interlocked.Read(ref _shareTokenRejectedCount),
            ShareUnauthorizedCount = Interlocked.Read(ref _shareUnauthorizedCount),
            AuthFailureCount = Interlocked.Read(ref _authFailureCount),
            DashboardAuthFailureCount = Interlocked.Read(ref _dashboardAuthFailureCount),
            WebDavRequestCount = Interlocked.Read(ref _webDavRequestCount),
            WebDavFileDownloadCount = Interlocked.Read(ref _webDavFileDownloadCount),
            WebDavFileDownloadBytes = Interlocked.Read(ref _webDavFileDownloadBytes),
            S3RequestCount = Interlocked.Read(ref _s3RequestCount),
            S3FileDownloadCount = Interlocked.Read(ref _s3FileDownloadCount),
            S3FileDownloadBytes = Interlocked.Read(ref _s3FileDownloadBytes),
            ApiRequestCount = Interlocked.Read(ref _apiRequestCount),
            ApiFileDownloadCount = Interlocked.Read(ref _apiFileDownloadCount),
            ApiFileDownloadBytes = Interlocked.Read(ref _apiFileDownloadBytes),
            McpRequestCount = Interlocked.Read(ref _mcpRequestCount),
            McpToolCallCount = Interlocked.Read(ref _mcpToolCallCount),
            RateLimitRejectionCount = Interlocked.Read(ref _rateLimitRejectionCount),
            HealthProbeCount = Interlocked.Read(ref _healthProbeCount),
            ReadyProbeCount = Interlocked.Read(ref _readyProbeCount),
            LastReadyProbeUtc = _lastReadyProbeUtc,
            LastHealthProbeUtc = _lastHealthProbeUtc
        };
    }

    private static readonly (string Name, string Type, string Help, Func<DashboardMetricsSnapshot, DirForgeOptions, string> Value)[] MetricDescriptors =
    [
        ("dirforge_dashboard_enabled", "gauge", "Dashboard feature toggle state.", (s, o) => o.DashboardEnabled ? "1" : "0"),
        ("dirforge_metrics_endpoint_enabled", "gauge", "Metrics endpoint toggle state.", (s, o) => o.EnableMetricsEndpoint ? "1" : "0"),
        ("dirforge_requests_total", "counter", "Total HTTP requests observed by DirForge.", (s, _) => $"{s.TotalRequests}"),
        ("dirforge_requests_in_flight", "gauge", "Current in-flight HTTP requests.", (s, _) => $"{s.InFlightRequests}"),
        ("dirforge_requests_per_minute", "gauge", "Current request rate estimate.", (s, _) => $"{s.RequestsPerMinute:F4}"),
        ("dirforge_request_duration_ms_avg", "gauge", "Average request latency in milliseconds.", (s, _) => $"{s.AverageLatencyMs:F4}"),
        ("dirforge_request_duration_ms_p50", "gauge", "P50 request latency in milliseconds.", (s, _) => $"{s.P50LatencyMs:F4}"),
        ("dirforge_request_duration_ms_p95", "gauge", "P95 request latency in milliseconds.", (s, _) => $"{s.P95LatencyMs:F4}"),
        ("dirforge_search_requests_total", "counter", "Total search operations.", (s, _) => $"{s.SearchRequests}"),
        ("dirforge_search_duration_ms_avg", "gauge", "Average search duration in milliseconds.", (s, _) => $"{s.AverageSearchMs:F4}"),
        ("dirforge_file_downloads_total", "counter", "Total direct file downloads.", (s, _) => $"{s.FileDownloadCount}"),
        ("dirforge_file_download_bytes_total", "counter", "Total direct file download bytes.", (s, _) => $"{s.FileDownloadBytes}"),
        ("dirforge_zip_downloads_total", "counter", "Total ZIP folder downloads.", (s, _) => $"{s.ZipDownloadCount}"),
        ("dirforge_zip_download_bytes_total", "counter", "Total ZIP source bytes written.", (s, _) => $"{s.ZipDownloadBytes}"),
        ("dirforge_archive_browse_total", "counter", "Total archive browse page opens.", (s, _) => $"{s.ArchiveBrowseCount}"),
        ("dirforge_archive_inner_downloads_total", "counter", "Total downloads of files from inside archives.", (s, _) => $"{s.ArchiveInnerDownloadCount}"),
        ("dirforge_archive_inner_download_bytes_total", "counter", "Total bytes downloaded from archive inner files.", (s, _) => $"{s.ArchiveInnerDownloadBytes}"),
        ("dirforge_share_links_created_total", "counter", "Total share links created.", (s, _) => $"{s.ShareLinkCreatedCount}"),
        ("dirforge_share_one_time_consumed_total", "counter", "Total one-time share links consumed.", (s, _) => $"{s.ShareOneTimeConsumedCount}"),
        ("dirforge_share_token_rejected_total", "counter", "Total rejected share token requests.", (s, _) => $"{s.ShareTokenRejectedCount}"),
        ("dirforge_share_unauthorized_total", "counter", "Total unauthorized share requests.", (s, _) => $"{s.ShareUnauthorizedCount}"),
        ("dirforge_auth_failures_total", "counter", "Total global basic auth failures.", (s, _) => $"{s.AuthFailureCount}"),
        ("dirforge_dashboard_auth_failures_total", "counter", "Total dashboard auth failures.", (s, _) => $"{s.DashboardAuthFailureCount}"),
        ("dirforge_webdav_requests_total", "counter", "Total WebDAV requests.", (s, _) => $"{s.WebDavRequestCount}"),
        ("dirforge_webdav_file_downloads_total", "counter", "Total WebDAV file downloads.", (s, _) => $"{s.WebDavFileDownloadCount}"),
        ("dirforge_webdav_file_download_bytes_total", "counter", "Total WebDAV file download bytes.", (s, _) => $"{s.WebDavFileDownloadBytes}"),
        ("dirforge_s3_requests_total", "counter", "Total S3 API requests.", (s, _) => $"{s.S3RequestCount}"),
        ("dirforge_s3_file_downloads_total", "counter", "Total S3 file downloads.", (s, _) => $"{s.S3FileDownloadCount}"),
        ("dirforge_s3_file_download_bytes_total", "counter", "Total S3 file download bytes.", (s, _) => $"{s.S3FileDownloadBytes}"),
        ("dirforge_api_requests_total", "counter", "Total JSON API requests.", (s, _) => $"{s.ApiRequestCount}"),
        ("dirforge_api_file_downloads_total", "counter", "Total JSON API file downloads.", (s, _) => $"{s.ApiFileDownloadCount}"),
        ("dirforge_api_file_download_bytes_total", "counter", "Total JSON API file download bytes.", (s, _) => $"{s.ApiFileDownloadBytes}"),
        ("dirforge_mcp_requests_total", "counter", "Total MCP endpoint requests.", (s, _) => $"{s.McpRequestCount}"),
        ("dirforge_mcp_tool_calls_total", "counter", "Total MCP tools/call invocations.", (s, _) => $"{s.McpToolCallCount}"),
        ("dirforge_rate_limit_rejections_total", "counter", "Total 429 rejections.", (s, _) => $"{s.RateLimitRejectionCount}"),
        ("dirforge_healthz_hits_total", "counter", "Total liveness probe hits (/health and /healthz).", (s, _) => $"{s.HealthProbeCount}"),
        ("dirforge_readyz_hits_total", "counter", "Total /readyz probe hits.", (s, _) => $"{s.ReadyProbeCount}"),

    ];

    public string RenderPrometheus(DirForgeOptions options)
    {
        var snapshot = CreateSnapshot();
        var lines = new List<string>(128);

        foreach (var (name, type, help, valueFn) in MetricDescriptors)
        {
            lines.Add($"# HELP {name} {help}");
            lines.Add($"# TYPE {name} {type}");
            lines.Add($"{name} {valueFn(snapshot, options)}");
        }

        foreach (var statusCount in snapshot.StatusCounts)
        {
            lines.Add($"dirforge_response_status_total{{code=\"{statusCount.Key}\"}} {statusCount.Value}");
        }

        foreach (var endpointCount in snapshot.EndpointCounts)
        {
            lines.Add($"dirforge_endpoint_requests_total{{endpoint=\"{EscapeLabel(endpointCount.Key)}\"}} {endpointCount.Value}");
        }

        return string.Join('\n', lines) + "\n";
    }

    private static readonly Dictionary<string, string> PathEndpointMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [DashboardRouteHelper.DashboardPath] = "dashboard",
        [DashboardRouteHelper.MetricsPath] = "metrics",
        [DashboardRouteHelper.ApiStatsPath] = "api-stats",
        [OperationalRouteHelper.HealthPath] = "healthz",
        [OperationalRouteHelper.HealthzPath] = "healthz",
        [OperationalRouteHelper.ReadyzPath] = "readyz",
    };

    private static string ClassifyEndpoint(PathString path, string? handler, string method)
    {
        var pathValue = path.Value ?? string.Empty;
        if (PathEndpointMap.TryGetValue(pathValue, out var mapped))
        {
            return mapped;
        }

        if (StaticAssetRouteHelper.IsStaticRequest(path))
        {
            return "static";
        }

        if (path.StartsWithSegments("/s3", StringComparison.OrdinalIgnoreCase))
        {
            return method.ToUpperInvariant() switch
            {
                "GET" => "s3-get",
                "HEAD" => "s3-head",
                _ => "s3-blocked"
            };
        }

        if (path.StartsWithSegments("/webdav", StringComparison.OrdinalIgnoreCase))
        {
            return method.ToUpperInvariant() switch
            {
                "PROPFIND" => "webdav-propfind",
                "GET" => "webdav-get",
                "HEAD" => "webdav-get",
                "OPTIONS" => "webdav-options",
                _ => "webdav-blocked"
            };
        }

        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return "api";
        }

        if (path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp";
        }

        var normalizedHandler = handler?.ToLowerInvariant() ?? string.Empty;

        if (path.StartsWithSegments("/archive", StringComparison.OrdinalIgnoreCase))
        {
            return normalizedHandler switch
            {
                "downloadentry" => "archive-entry-download",
                "previewinfoentry" => "archive-preview-info",
                _ => "archive-browse"
            };
        }

        return normalizedHandler switch
        {
            "view" => "preview-view",
            "downloadzip" => "zip-download",
            "previewinfo" => "preview-info",
            "filehashes" => "file-hashes",
            "directorysizes" => "directory-sizes",
            "sharelink" => "share-link",
            "archive" => "archive-browse",
            "archiveentry" => "archive-entry-download",
            _ => "browse"
        };
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return 0d;
        }

        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        index = Math.Clamp(index, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static string MaskIp(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            bytes[3] = 0;
            return new IPAddress(bytes).ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            for (var i = 8; i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }

            return new IPAddress(bytes).ToString();
        }

        return "unknown";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static string EscapeLabel(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private void RecordTrafficPoint(DateTimeOffset nowUtc)
    {
        var second = nowUtc.ToUnixTimeSeconds();
        var slot = GetTrafficSlot(second);

        lock (_trafficSync)
        {
            if (_trafficBucketUnixSeconds[slot] != second)
            {
                _trafficBucketUnixSeconds[slot] = second;
                _trafficCountsBySecond[slot] = 0;
            }

            _trafficCountsBySecond[slot]++;
        }
    }

    private static int GetTrafficSlot(long unixSecond)
    {
        var mod = unixSecond % TrafficWindowSeconds;
        return (int)(mod >= 0 ? mod : mod + TrafficWindowSeconds);
    }
}
