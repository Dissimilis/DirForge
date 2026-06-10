using System.Reflection;

namespace DirForge.Services;

/// <summary>
/// Provides assembly version and build-date information.
/// </summary>
public static class AppVersionInfo
{
    /// <summary>
    /// Extracts a bare version (no "v" prefix, no build metadata) from an assembly
    /// informational version. Returns null for the default "1.0.0" dev placeholder.
    /// </summary>
    public static string? ParseBareVersion(string? informationalVersion)
    {
        var version = informationalVersion?.Split('+')[0];
        if (string.IsNullOrEmpty(version) || version == "1.0.0")
        {
            return null;
        }

        if (version.Length > 1 && (version[0] is 'v' or 'V') && char.IsDigit(version[1]))
        {
            version = version[1..];
        }

        return version;
    }

    public static string? BareVersion { get; } = ParseBareVersion(
        typeof(AppVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    private static readonly string? RawVersion =
        BareVersion is { } v
            ? (char.IsDigit(v[0]) ? "v" + v : v)
            : null;

    private static readonly string? BuildDate =
        typeof(AppVersionInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")
            ?.Value is { Length: >= 10 } d ? d[..10] : null;

    public static string? AppVersion { get; } =
        RawVersion is not null && BuildDate is not null ? $"{RawVersion}, {BuildDate}"
        : RawVersion is not null ? RawVersion
        : null;
}
