using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class AppVersionInfoUnitTests
{
    [TestMethod]
    public void ParseBareVersion_ReleaseVersionWithBuildMetadata_StripsMetadata()
    {
        Assert.AreEqual("1.2.1", AppVersionInfo.ParseBareVersion("1.2.1+a1b2c3d"));
    }

    [TestMethod]
    public void ParseBareVersion_PlainReleaseVersion_IsReturned()
    {
        Assert.AreEqual("1.2.1", AppVersionInfo.ParseBareVersion("1.2.1"));
    }

    [TestMethod]
    public void ParseBareVersion_VPrefixedVersion_StripsPrefix()
    {
        Assert.AreEqual("1.2.1", AppVersionInfo.ParseBareVersion("v1.2.1"));
    }

    [TestMethod]
    public void ParseBareVersion_DefaultDevVersion_ReturnsNull()
    {
        Assert.IsNull(AppVersionInfo.ParseBareVersion("1.0.0"));
    }

    [TestMethod]
    public void ParseBareVersion_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(AppVersionInfo.ParseBareVersion(null));
        Assert.IsNull(AppVersionInfo.ParseBareVersion(""));
    }

    [TestMethod]
    public void ParseBareVersion_PrereleaseVersion_IsReturned()
    {
        Assert.AreEqual("1.3.0-rc.1", AppVersionInfo.ParseBareVersion("1.3.0-rc.1+deadbeef"));
    }
}
