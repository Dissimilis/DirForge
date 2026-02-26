namespace DirForge.Models;

public readonly record struct DashboardTrafficPoint(
    long UnixSecond,
    long RequestsPerSecond);
