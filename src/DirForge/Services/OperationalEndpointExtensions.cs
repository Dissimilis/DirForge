using System.Text.Json;
using DirForge.Models;
using DirForge.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DirForge.Services;

public static class OperationalEndpointExtensions
{
    private const string MetricsContentType = "text/plain; version=0.0.4; charset=utf-8";
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public static IEndpointRouteBuilder MapOperationalEndpoints(this IEndpointRouteBuilder app, DirForgeOptions options)
    {
        MapHealthEndpoints(app);
        MapMetricsEndpoint(app, options);
        MapDashboardStatsEndpoint(app, options);

        return app;
    }

    private static void MapHealthEndpoints(IEndpointRouteBuilder app)
    {
        var livenessHealthOptions = new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = async (context, report) =>
            {
                var dashboardMetrics = context.RequestServices.GetRequiredService<DashboardMetricsService>();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/plain; charset=utf-8";
                dashboardMetrics.RecordHealthProbe();

                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    var responseText = report.Status == HealthStatus.Unhealthy ? "not healthy" : "ok";
                    await context.Response.WriteAsync(responseText);
                }
            }
        };

        var readinessHealthOptions = new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            AllowCachingResponses = false,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = async (context, report) =>
            {
                var dashboardMetrics = context.RequestServices.GetRequiredService<DashboardMetricsService>();
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "text/plain; charset=utf-8";
                var ready = report.Status == HealthStatus.Healthy;
                dashboardMetrics.RecordReadyProbe();

                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.WriteAsync(ready ? "ready" : "not ready");
                }
            }
        };

        app.MapHealthChecks(OperationalRouteHelper.HealthPath, livenessHealthOptions)
            .WithName("HealthLive");
        app.MapHealthChecks(OperationalRouteHelper.HealthzPath, livenessHealthOptions)
            .WithName("HealthLiveAlt");
        app.MapHealthChecks(OperationalRouteHelper.ReadyzPath, readinessHealthOptions)
            .WithName("HealthReady");
    }

    private static void MapMetricsEndpoint(
        IEndpointRouteBuilder app,
        DirForgeOptions options)
    {
        var metricsEndpoint = app.MapGet(DashboardRouteHelper.MetricsPath, async (HttpContext context, DashboardMetricsService dashboardMetrics) =>
            {
                if (!options.DashboardEnabled || !options.EnableMetricsEndpoint)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = MetricsContentType;

                if (HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;
                }

                await context.Response.WriteAsync(dashboardMetrics.RenderPrometheus(options));
            })
            .WithName("Metrics");

        if (options.DashboardAuthEnabled && options.DashboardEnabled && options.EnableMetricsEndpoint)
        {
            metricsEndpoint.RequireAuthorization(DashboardBasicAuthenticationHandler.PolicyName);
        }
    }

    private static void MapDashboardStatsEndpoint(
        IEndpointRouteBuilder app,
        DirForgeOptions options)
    {
        var dashboardStatsEndpoint = app.MapGet(DashboardRouteHelper.DashboardStatsPath, async (HttpContext context, DashboardMetricsService dashboardMetrics) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.ContentType = "application/json; charset=utf-8";
                if (HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return;
                }

                var response = CreateApiStatsResponse(options, dashboardMetrics.CreateSnapshot());
                await context.Response.WriteAsJsonAsync(response, IndentedJsonOptions);
            })
            .WithName("DashboardStats");

        if (options.DashboardAuthEnabled)
        {
            dashboardStatsEndpoint.RequireAuthorization(DashboardBasicAuthenticationHandler.PolicyName);
        }
    }

    private static ApiStatsResponse CreateApiStatsResponse(DirForgeOptions options, DashboardMetricsSnapshot snapshot)
    {
        return new ApiStatsResponse
        {
            Version = AppVersionInfo.AppVersion,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Ready = DirectoryReadinessHelper.IsDirectoryReadable(options.RootPath),
            UptimeSeconds = (long)snapshot.Uptime.TotalSeconds,
            TotalRequests = snapshot.TotalRequests,
            InFlightRequests = snapshot.InFlightRequests,
            RequestsPerMinute = snapshot.RequestsPerMinute,
            AverageLatencyMs = snapshot.AverageLatencyMs,
            TotalDownloadTrafficBytes = snapshot.FileDownloadBytes + snapshot.ZipDownloadBytes,
            TotalDownloadCount = snapshot.FileDownloadCount + snapshot.ZipDownloadCount
        };
    }

}
