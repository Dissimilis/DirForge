using System.Security.Cryptography;
using DirForge.Models;

namespace DirForge.Services;

public static class DirForgeOptionsResolver
{
    public static DirForgeOptions Resolve(IConfiguration configuration)
    {
        var options = configuration.Get<DirForgeOptions>() ??
                      throw new InvalidOperationException("Failed to bind DirForgeOptions.");

        options.RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(options.RootPath) ? "." : options.RootPath);
        options.ListenIp = string.IsNullOrWhiteSpace(options.ListenIp) ? "0.0.0.0" : options.ListenIp.Trim();
        options.DefaultTheme = options.DefaultTheme.ToLowerInvariant();
        options.HidePathPatterns = ReadConfiguredStringList(configuration, nameof(DirForgeOptions.HidePathPatterns))
            .Select(static pattern => pattern.Replace('\\', '/').Trim().Trim('/'))
            .Where(static pattern => !string.IsNullOrEmpty(pattern))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        options.DenyDownloadExtensions = ReadConfiguredStringList(configuration, nameof(DirForgeOptions.DenyDownloadExtensions))
            .Select(static extension => extension.Trim().TrimStart('.').ToLowerInvariant())
            .Where(static extension => !string.IsNullOrEmpty(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        options.ForwardedHeadersKnownProxies = ReadConfiguredStringList(configuration, nameof(DirForgeOptions.ForwardedHeadersKnownProxies))
            .Select(static proxy => proxy.Trim())
            .Where(static proxy => !string.IsNullOrEmpty(proxy))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        options.ExternalAuthIdentityHeader = options.ExternalAuthIdentityHeader?.Trim() ?? string.Empty;
        options.BearerTokenHeaderName = options.BearerTokenHeaderName?.Trim() ?? string.Empty;

        var configuredShareSecret = options.ShareSecret;
        if (string.IsNullOrWhiteSpace(configuredShareSecret))
        {
            options.ShareSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            options.ShareSecretWarning = options.EnableSharing
                ? "ShareSecret is not set - share links will expire when the server restarts."
                : null;
            return options;
        }

        options.ShareSecret = configuredShareSecret.Trim();
        if (options.ShareSecret.Length < 16)
        {
            options.ShareSecretWarning = $"ShareSecret is only {options.ShareSecret.Length} characters - at least 16 recommended.";
            return options;
        }

        options.ShareSecretWarning = options.ShareSecret.Distinct().Count() < 6
            ? "ShareSecret has very low entropy - use a more random value."
            : null;

        return options;
    }


    private static string[] ReadConfiguredStringList(IConfiguration configuration, string key)
    {
        var scalarValue = configuration[key];
        if (!string.IsNullOrWhiteSpace(scalarValue))
        {
            return scalarValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        var section = configuration.GetSection(key);
        var values = section
            .GetChildren()
            .Select(child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .ToArray();

        return values.Length > 0 ? values : [];
    }
}
