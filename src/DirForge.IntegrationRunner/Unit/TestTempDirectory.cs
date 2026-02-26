namespace DirForge.IntegrationRunner.Unit;

internal sealed class TestTempDirectory : IDisposable
{
    public TestTempDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DirForgeIntegrationRunner",
            prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }
}
