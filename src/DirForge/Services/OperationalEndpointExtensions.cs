using System.Text.Json;
using DirForge.Models;
using DirForge.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
        static Task WriteHealthResponse(HttpContext context, string body)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "text/plain; charset=utf-8";
            return HttpMethods.IsHead(context.Request.Method)
                ? Task.CompletedTask
                : context.Response.WriteAsync(body);
        }

        app.MapMethods(OperationalRouteHelper.HealthPath, [HttpMethods.Get, HttpMethods.Head], async context =>
            {
                var dashboardMetrics = context.RequestServices.GetRequiredService<DashboardMetricsService>();
                dashboardMetrics.RecordHealthProbe();
                await WriteHealthResponse(context, "ok");
            })
            .WithName("HealthLive");

        app.MapMethods(OperationalRouteHelper.HealthzPath, [HttpMethods.Get, HttpMethods.Head], async context =>
            {
                var dashboardMetrics = context.RequestServices.GetRequiredService<DashboardMetricsService>();
                dashboardMetrics.RecordHealthProbe();
                await WriteHealthResponse(context, "ok");
            })
            .WithName("HealthLiveAlt");

        app.MapMethods(OperationalRouteHelper.ReadyzPath, [HttpMethods.Get, HttpMethods.Head], async context =>
            {
                var dashboardMetrics = context.RequestServices.GetRequiredService<DashboardMetricsService>();
                dashboardMetrics.RecordReadyProbe();
                await WriteHealthResponse(context, "ready");
            })
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
            Ready = true,
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
