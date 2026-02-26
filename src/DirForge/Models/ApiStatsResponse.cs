namespace DirForge.Models;

public sealed class ApiStatsResponse
{
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public bool Ready { get; init; }
    public long UptimeSeconds { get; init; }
    public long TotalRequests { get; init; }
    public long InFlightRequests { get; init; }
    public double RequestsPerMinute { get; init; }
    public double AverageLatencyMs { get; init; }
    public long TotalDownloadTrafficBytes { get; init; }
    public long FileCount { get; init; }
    public long ZipCount { get; init; }
}
