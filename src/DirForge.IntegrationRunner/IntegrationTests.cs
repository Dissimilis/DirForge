using DirForge.IntegrationRunner.Runner;
using DirForge.IntegrationRunner.Scenarios;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner;

[TestClass]
[TestCategory("Smoke")]
public sealed class SmokeIntegrationTests
{
    private static string _repositoryRoot = string.Empty;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        _repositoryRoot = RepositoryRootLocator.Resolve();
        await DirForgeBuild.EnsureBuiltAsync(_repositoryRoot, CancellationToken.None);
    }

    [TestMethod]
    public Task BrowseAndFileDownloadHappyPath()
    {
        return BrowseAndDownloadScenario.RunAsync(_repositoryRoot, CancellationToken.None);
    }

    [TestMethod]
    public Task BasicAuthGateCorrectness()
    {
        return AuthGateScenario.RunAsync(_repositoryRoot, CancellationToken.None);
    }

    [TestMethod]
    public Task HealthReadinessAuthBypass()
    {
        return OperationalBypassScenario.RunAsync(_repositoryRoot, CancellationToken.None);
    }
}
