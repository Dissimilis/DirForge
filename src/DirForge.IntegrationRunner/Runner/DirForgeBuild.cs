using System.Diagnostics;
using System.Text;

namespace DirForge.IntegrationRunner.Runner;

internal static class DirForgeBuild
{
    private static readonly SemaphoreSlim BuildSync = new(1, 1);
    private static bool _built;

    public static async Task EnsureBuiltAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        if (_built)
        {
            return;
        }

        await BuildSync.WaitAsync(cancellationToken);
        try
        {
            if (_built)
            {
                return;
            }

            var projectPath = Path.Combine(repositoryRoot, "src", "DirForge", "DirForge.csproj");
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" -c Release --nologo --verbosity minimal",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder(4096);
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    output.AppendLine(args.Data);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    output.AppendLine(args.Data);
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start dotnet build process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "DirForge build failed before integration tests." + Environment.NewLine + output);
            }

            _built = true;
        }
        finally
        {
            BuildSync.Release();
        }
    }
}
