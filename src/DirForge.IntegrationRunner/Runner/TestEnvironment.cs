using System.Net.Sockets;
using System.Text;

namespace DirForge.IntegrationRunner.Runner;

internal sealed class TestEnvironment : IAsyncDisposable
{
    private readonly DirForgeProcess _process;
    private bool _disposed;

    private TestEnvironment(string repositoryRoot, string scenarioDirectory, string rootPath, DirForgeProcess process)
    {
        RepositoryRoot = repositoryRoot;
        ScenarioDirectory = scenarioDirectory;
        RootPath = rootPath;
        _process = process;
    }

    public string RepositoryRoot { get; }

    public string ScenarioDirectory { get; }

    public string RootPath { get; }

    public HttpClient Client => _process.Client;

    public Uri BaseUri => _process.BaseUri;

    public static async Task<TestEnvironment> StartAsync(
        string repositoryRoot,
        string scenarioName,
        Action<string, string>? seedData,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        CancellationToken cancellationToken)
    {
        var scenarioDirectory = Path.Combine(
            Path.GetTempPath(),
            "DirForgeIntegrationRunner",
            BuildSafeDirectoryName(scenarioName) + "-" + Guid.NewGuid().ToString("N"));
        var rootPath = Path.Combine(scenarioDirectory, "root");
        Directory.CreateDirectory(rootPath);

        seedData?.Invoke(rootPath, scenarioDirectory);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EnableDefaultRateLimiter"] = "false",
            ["EnableSharing"] = "false",
            ["DashboardEnabled"] = "false",
            ["EnableMetricsEndpoint"] = "false",
            ["ForwardedHeadersEnabled"] = "false",
            ["AllowFolderDownload"] = "true",
            ["AllowFileDownload"] = "true"
        };

        if (environmentOverrides is not null)
        {
            foreach (var pair in environmentOverrides)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        var port = GetFreeTcpPort();
        var process = await DirForgeProcess.StartAsync(
            new DirForgeProcessOptions(
                repositoryRoot,
                rootPath,
                port,
                environment,
                StartupTimeout: TimeSpan.FromSeconds(35)),
            cancellationToken);

        return new TestEnvironment(repositoryRoot, scenarioDirectory, rootPath, process);
    }

    public async Task<HttpResponseMessage> GetAsync(
        string pathAndQuery,
        string? authorizationHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (!string.IsNullOrWhiteSpace(authorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
        }

        return await Client.SendAsync(request, cancellationToken);
    }

    public static string BuildBasicAuthHeader(string username, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return $"Basic {token}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _process.DisposeAsync();

        try
        {
            if (Directory.Exists(ScenarioDirectory))
            {
                Directory.Delete(ScenarioDirectory, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string BuildSafeDirectoryName(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}
