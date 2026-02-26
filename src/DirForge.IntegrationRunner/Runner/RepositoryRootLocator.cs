namespace DirForge.IntegrationRunner.Runner;

internal static class RepositoryRootLocator
{
    public static string Resolve()
    {
        var startingPoints = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var startingPoint in startingPoints)
        {
            var directory = new DirectoryInfo(startingPoint);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "src", "DirForge", "DirForge.csproj");
                if (File.Exists(candidate))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Unable to locate repository root containing src/DirForge/DirForge.csproj.");
    }
}
