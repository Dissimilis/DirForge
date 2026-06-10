using DirForge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DirForgeOptionsResolverUnitTests
{
    private static IConfiguration BuildConfiguration(string rootPath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RootPath"] = rootPath,
                ["DefaultTheme"] = "auto"
            })
            .Build();
    }

    [TestMethod]
    public void Resolve_RelativeRootPath_RunningAsService_AnchorsToBaseDirectory()
    {
        var configuration = BuildConfiguration("samples");

        var options = DirForgeOptionsResolver.Resolve(configuration, runningAsService: true);

        Assert.AreEqual(Path.GetFullPath("samples", AppContext.BaseDirectory), options.RootPath);
    }

    [TestMethod]
    public void Resolve_RelativeRootPath_NotRunningAsService_AnchorsToWorkingDirectory()
    {
        var configuration = BuildConfiguration("samples");

        var options = DirForgeOptionsResolver.Resolve(configuration, runningAsService: false);

        Assert.AreEqual(Path.GetFullPath("samples"), options.RootPath);
    }

    [TestMethod]
    public void Resolve_AbsoluteRootPath_RunningAsService_IsUnchanged()
    {
        using var tempDir = new TestTempDirectory("resolver-absolute-root");
        var configuration = BuildConfiguration(tempDir.Path);

        var options = DirForgeOptionsResolver.Resolve(configuration, runningAsService: true);

        Assert.AreEqual(Path.GetFullPath(tempDir.Path), options.RootPath);
    }
}
