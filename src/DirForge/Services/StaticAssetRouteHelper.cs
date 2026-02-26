using System.Collections.Frozen;
using System.Text.Json;

namespace DirForge.Services;

public static class StaticAssetRouteHelper
{
    private static FrozenSet<string> _knownRoutes = FrozenSet<string>.Empty;

    public static void Initialize(ILogger logger)
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Try manifest first: {AppName}.staticwebassets.endpoints.json
        var manifestPath = Path.Combine(AppContext.BaseDirectory,
            typeof(StaticAssetRouteHelper).Assembly.GetName().Name + ".staticwebassets.endpoints.json");

        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("Endpoints", out var endpoints))
                {
                    foreach (var endpoint in endpoints.EnumerateArray())
                    {
                        if (endpoint.TryGetProperty("Route", out var route))
                        {
                            var routeValue = route.GetString();
                            if (!string.IsNullOrEmpty(routeValue))
                            {
                                routes.Add("/" + routeValue.TrimStart('/'));
                            }
                        }
                    }
                }

                logger.LogInformation("Static asset route helper initialized from manifest with {Count} routes.", routes.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse static asset manifest at {Path}. Falling back to wwwroot scan.", manifestPath);
                routes.Clear();
            }
        }

        // Fallback: scan physical wwwroot directory
        if (routes.Count == 0)
        {
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(wwwroot))
            {
                foreach (var file in Directory.EnumerateFiles(wwwroot, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(wwwroot, file).Replace('\\', '/');
                    routes.Add("/" + relative);
                }

                logger.LogWarning("Static asset manifest not found. Initialized from wwwroot scan with {Count} routes.", routes.Count);
            }
            else
            {
                logger.LogWarning("No static asset manifest or wwwroot directory found. Static asset route detection disabled.");
            }
        }

        // Always include /favicon.ico
        routes.Add("/favicon.ico");

        _knownRoutes = routes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string AssetPath(string relativePath)
        => "/dirforge-assets/" + relativePath.TrimStart('/');

    public static bool IsStaticRequest(PathString path)
        => path.HasValue && _knownRoutes.Contains(path.Value!);
}
