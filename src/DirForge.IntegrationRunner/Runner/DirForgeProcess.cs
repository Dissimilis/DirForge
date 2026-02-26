using System.Diagnostics;
using System.Net;
using System.Text;

namespace DirForge.IntegrationRunner.Runner;

internal sealed record DirForgeProcessOptions(
    string RepositoryRoot,
    string RootPath,
    int Port,
    IReadOnlyDictionary<string, string> EnvironmentOverrides,
    TimeSpan StartupTimeout);

internal sealed class DirForgeProcess : IAsyncDisposable
{
    private const int MaxLogLines = 800;

    private readonly Process _process;
    private readonly List<string> _logs;
    private readonly object _logSync;
    private readonly HttpClient _client;
    private bool _disposed;

    private DirForgeProcess(
        Process process,
        List<string> logs,
        object logSync,
        HttpClient client,
        Uri baseUri)
    {
        _process = process;
        _logs = logs;
        _logSync = logSync;
        _client = client;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public HttpClient Client => _client;

    public static async Task<DirForgeProcess> StartAsync(DirForgeProcessOptions options, CancellationToken cancellationToken)
    {
        var projectPath = Path.Combine(
            options.RepositoryRoot,
            "src",
            "DirForge",
            "DirForge.csproj");

        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException($"DirForge project not found: {projectPath}");
        }

        var logs = new List<string>(MaxLogLines);
        var logSync = new object();
        static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");

        void AppendLog(string streamName, string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (logSync)
            {
                if (logs.Count >= MaxLogLines)
                {
                    logs.RemoveAt(0);
                }

                logs.Add($"{Timestamp()} [{streamName}] {line}");
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{projectPath}\" -c Release --no-build --no-launch-profile",
            WorkingDirectory = options.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["RootPath"] = options.RootPath;
        startInfo.Environment["Port"] = options.Port.ToString();
        foreach (var pair in options.EnvironmentOverrides)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => AppendLog("OUT", args.Data);
        process.ErrorDataReceived += (_, args) => AppendLog("ERR", args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start DirForge process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var baseUri = new Uri($"http://127.0.0.1:{options.Port}/", UriKind.Absolute);
        var client = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };

        var instance = new DirForgeProcess(process, logs, logSync, client, baseUri);
        await instance.WaitForHealthyAsync(options.StartupTimeout, cancellationToken);
        return instance;
    }

    public string GetRecentLogs(int maxLines = 120)
    {
        lock (_logSync)
        {
            if (_logs.Count == 0)
            {
                return "(no logs captured)";
            }

            var startIndex = Math.Max(0, _logs.Count - maxLines);
            var slice = _logs.GetRange(startIndex, _logs.Count - startIndex);
            return string.Join(Environment.NewLine, slice);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch
        {
            // best effort cleanup
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task WaitForHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        Exception? lastException = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"DirForge process exited early with code {_process.ExitCode}.{Environment.NewLine}" +
                    $"Recent logs:{Environment.NewLine}{GetRecentLogs()}");
            }

            try
            {
                using var response = await _client.GetAsync("/health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastException = ex;
            }

            await Task.Delay(200, cancellationToken);
        }

        var diagnostics = new StringBuilder();
        diagnostics.AppendLine("Timed out waiting for DirForge health endpoint.");
        if (lastException is not null)
        {
            diagnostics.AppendLine($"Last exception: {lastException.GetType().Name}: {lastException.Message}");
        }

        diagnostics.AppendLine("Recent logs:");
        diagnostics.AppendLine(GetRecentLogs());
        throw new InvalidOperationException(diagnostics.ToString());
    }
}
