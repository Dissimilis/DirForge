using System.Diagnostics;
using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DirForge.Pages;

[IgnoreAntiforgeryToken]
public sealed class DashboardModel : PageModel
{
    private readonly DirForgeOptions _options;
    private readonly DashboardMetricsService _dashboardMetrics;
    private readonly InMemoryLogStore _logStore;

    public DashboardModel(
        DirForgeOptions options,
        DashboardMetricsService dashboardMetrics,
        InMemoryLogStore logStore)
    {
        _options = options;
        _dashboardMetrics = dashboardMetrics;
        _logStore = logStore;
    }

    public DashboardMetricsSnapshot Snapshot { get; private set; } = new();
    public IReadOnlyList<InMemoryLogEntry> Logs { get; private set; } = [];
    public DateTimeOffset GeneratedAtUtc { get; private set; }
    public string PageTitle { get; private set; } = "DirForge :: /dashboard";
    public string SiteTitle { get; private set; } = "DirForge";
    public string MachineName { get; private set; } = string.Empty;
    public int ProcessId { get; private set; }
    public long WorkingSetBytes { get; private set; }
    public long ManagedMemoryBytes { get; private set; }
    public IReadOnlyList<DashboardTrafficPoint> TrafficSeries { get; private set; } = [];
    public long TotalDownloadTrafficBytes => Snapshot.FileDownloadBytes + Snapshot.ZipDownloadBytes;
    public bool DashboardAuthEnabled => _options.DashboardAuthEnabled;
    public bool GlobalAuthEnabled => _options.AuthEnabled || _options.ExternalAuthEnabled;
    public bool SearchEnabled => _options.EnableSearch;
    public bool SharingEnabled => _options.EnableSharing;
    public bool OpenArchivesInlineEnabled => _options.OpenArchivesInline;
    public bool RateLimiterEnabled => _options.EnableDefaultRateLimiter;
    public bool MetricsEndpointEnabled => _options.EnableMetricsEndpoint;
    public bool WebDavEnabled => _options.EnableWebDav;
    public string ConfiguredProxyIps => _options.ForwardedHeadersKnownProxies.Length == 0
        ? "(none configured)"
        : string.Join(", ", _options.ForwardedHeadersKnownProxies);

    public IActionResult OnGet()
    {
        if (!_options.DashboardEnabled)
        {
            return NotFound();
        }
        Response.Headers.CacheControl = "no-store";

        Snapshot = _dashboardMetrics.CreateSnapshot();
        TrafficSeries = _dashboardMetrics.GetTrafficSeries(DateTimeOffset.UtcNow);
        Logs = _logStore.GetLatest(500);
        GeneratedAtUtc = DateTimeOffset.UtcNow;
        SiteTitle = _options.SiteTitle ?? "DirForge";
        PageTitle = $"{SiteTitle} :: /dashboard";
        MachineName = Environment.MachineName;

        using var process = Process.GetCurrentProcess();
        ProcessId = process.Id;
        WorkingSetBytes = process.WorkingSet64;
        ManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);

        return Page();
    }

    public IActionResult OnPostReset()
    {
        if (!_options.DashboardEnabled)
        {
            return NotFound();
        }

        _dashboardMetrics.ResetDashboardViewState();
        _logStore.Clear();
        Response.Headers.CacheControl = "no-store";
        return Redirect(DashboardRouteHelper.DashboardPath);
    }
}
