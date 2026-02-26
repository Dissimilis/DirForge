using DirForge.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DirForge.Services;

public sealed class RootPathReadableHealthCheck : IHealthCheck
{
    private readonly DirForgeOptions _options;

    public RootPathReadableHealthCheck(DirForgeOptions options)
    {
        _options = options;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            DirectoryReadinessHelper.IsDirectoryReadable(_options.RootPath)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Root path '{_options.RootPath}' is not readable."));
    }
}
