namespace DirForge.Models;

public sealed record DashboardUsageEvent(
    DateTimeOffset TimestampUtc,
    string Method,
    string Path,
    string Endpoint,
    string Handler,
    int StatusCode,
    double DurationMs,
    string ClientIp,
    string UserAgent);
