namespace DirForge.Models;

public sealed class DashboardMetricsSnapshot
{
    public DateTimeOffset StartedAtUtc { get; init; }
    public TimeSpan Uptime { get; init; }
    public long TotalRequests { get; init; }
    public long InFlightRequests { get; init; }
    public double RequestsPerMinute { get; init; }
    public double AverageLatencyMs { get; init; }
    public double P50LatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public IReadOnlyDictionary<int, long> StatusCounts { get; init; } = new Dictionary<int, long>();
    public IReadOnlyDictionary<string, long> EndpointCounts { get; init; } = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<DashboardUsageEvent> LatestUsage { get; init; } = [];
    public long SearchRequests { get; init; }
    public long SearchTruncatedRequests { get; init; }
    public double AverageSearchMs { get; init; }
    public long FileDownloadCount { get; init; }
    public long FileDownloadBytes { get; init; }
    public long ZipDownloadCount { get; init; }
    public long ZipDownloadBytes { get; init; }
    public long ZipCancelledCount { get; init; }
    public long ZipSizeLimitHitCount { get; init; }
    public long ArchiveBrowseCount { get; init; }
    public long ArchiveInnerDownloadCount { get; init; }
    public long ArchiveInnerDownloadBytes { get; init; }
    public long ShareLinkCreatedCount { get; init; }
    public long ShareOneTimeConsumedCount { get; init; }
    public long ShareTokenRejectedCount { get; init; }
    public long ShareUnauthorizedCount { get; init; }
    public long AuthFailureCount { get; init; }
    public long DashboardAuthFailureCount { get; init; }
    public long WebDavRequestCount { get; init; }
    public long WebDavFileDownloadCount { get; init; }
    public long WebDavFileDownloadBytes { get; init; }
    public long S3RequestCount { get; init; }
    public long S3FileDownloadCount { get; init; }
    public long S3FileDownloadBytes { get; init; }
    public long ApiRequestCount { get; init; }
    public long McpRequestCount { get; init; }
    public long RateLimitRejectionCount { get; init; }
    public long HealthProbeCount { get; init; }
    public long ReadyProbeCount { get; init; }
    public bool LastReadyState { get; init; }
    public DateTimeOffset? LastReadyProbeUtc { get; init; }
    public DateTimeOffset? LastHealthProbeUtc { get; init; }
}
