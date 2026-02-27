using DirForge.Models;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DirForgeOptionsValidatorUnitTests
{
    private readonly DirForgeOptionsValidator _validator = new();

    [TestMethod]
    public void Validate_ValidConfig_ReturnsSuccess()
    {
        using var tempDir = new TestTempDirectory("validator-valid");
        var options = TestOptionsFactory.Create(tempDir.Path);

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Validate_PortZero_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-port-zero");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.Port = 0;

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_PortBoundaryValues_ReturnsExpected()
    {
        using var tempDir = new TestTempDirectory("validator-port-boundary");
        var options = TestOptionsFactory.Create(tempDir.Path);

        options.Port = 1;
        Assert.IsTrue(_validator.Validate(null, options).Succeeded);

        options.Port = 65535;
        Assert.IsTrue(_validator.Validate(null, options).Succeeded);

        options.Port = 65536;
        Assert.IsTrue(_validator.Validate(null, options).Failed);
    }

    [TestMethod]
    public void Validate_RootPathEmpty_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-root-empty");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.RootPath = "";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_RootPathNonExistent_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-root-missing");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.RootPath = Path.Combine(tempDir.Path, "nonexistent-dir-12345");

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_ListenIpInvalid_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-ip-invalid");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.ListenIp = "not-an-ip";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_ListenIpEmpty_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-ip-empty");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.ListenIp = "";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_DefaultThemeInvalid_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-theme-invalid");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DefaultTheme = "blue";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_DefaultThemeCaseInsensitive_ReturnsSuccess()
    {
        using var tempDir = new TestTempDirectory("validator-theme-case");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DefaultTheme = "Dark";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Validate_OperationTimeBudget_ZeroIsValid()
    {
        using var tempDir = new TestTempDirectory("validator-budget-zero");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.OperationTimeBudgetMs = 0;

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Validate_OperationTimeBudget_BelowMinimumFails()
    {
        using var tempDir = new TestTempDirectory("validator-budget-low");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.OperationTimeBudgetMs = 50;

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_OperationTimeBudget_AtMinimumIsValid()
    {
        using var tempDir = new TestTempDirectory("validator-budget-min");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.OperationTimeBudgetMs = 100;

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Validate_S3EnabledWithoutCredentials_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-s3-no-creds");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.EnableS3Endpoint = true;
        options.S3AccessKeyId = null;
        options.S3SecretAccessKey = null;
        options.BasicAuthUser = null;
        options.BasicAuthPass = null;

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_BearerTokenWithEmptyHeaderName_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-bearer-empty-header");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.BearerToken = "my-secret-token";
        options.BearerTokenHeaderName = "";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_KnownProxiesInvalidIp_ReturnsFailure()
    {
        using var tempDir = new TestTempDirectory("validator-proxy-invalid");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.ForwardedHeadersKnownProxies = ["not-an-ip"];

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
    }

    [TestMethod]
    public void Validate_MultipleFailures_ReportsAll()
    {
        using var tempDir = new TestTempDirectory("validator-multi-fail");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.Port = 0;
        options.ListenIp = "not-an-ip";
        options.DefaultTheme = "blue";

        var result = _validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
        var failures = result.Failures.ToList();
        Assert.IsTrue(failures.Count >= 3, $"Expected at least 3 failures, got {failures.Count}");
    }
}
