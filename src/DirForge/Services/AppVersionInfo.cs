using System.Reflection;

namespace DirForge.Services;

/// <summary>
/// Provides assembly version and build-date information.
/// </summary>
public static class AppVersionInfo
{
    private static readonly string? RawVersion =
        typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] is { } v && v != "1.0.0"
            ? (char.IsDigit(v[0]) ? "v" + v : v)
            : null;

    private static readonly string? BuildDate =
        typeof(AppVersionInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")
            ?.Value is { Length: >= 10 } d ? d[..10] : null;

    public static string? AppVersion { get; } =
        RawVersion is not null && BuildDate is not null ? $"({RawVersion}, {BuildDate})"
        : RawVersion is not null ? $"({RawVersion})"
        : null;
}
