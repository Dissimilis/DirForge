using System.Text;
using DirForge.Pages;
using DirForge.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DirectoryPolicyUnitTests
{
    [TestMethod]
    public void ResolvePhysicalPath_RejectsTraversalOutsideRoot()
    {
        using var tempDir = new TestTempDirectory("DirPolicy-Traversal");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "inside.txt"),
            "inside",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var options = TestOptionsFactory.Create(root);
        var listing = TestServiceFactory.CreateDirectoryListingService(options);

        var inside = listing.ResolvePhysicalPath("inside.txt");
        var outside = listing.ResolvePhysicalPath("../outside.txt");

        Assert.IsNotNull(inside);
        Assert.IsNull(outside);
    }

    [TestMethod]
    public void HiddenAndDeniedPolicy_EnforcesExpectedRules()
    {
        using var tempDir = new TestTempDirectory("DirPolicy-HiddenDenied");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.HidePathPatterns = ["secret-folder", "secret-folder/**"];
        options.DenyDownloadExtensions = ["exe", "dll"];

        var listing = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsTrue(listing.IsPathHiddenByPolicy("secret-folder", isDirectory: true));
        Assert.IsTrue(listing.IsPathHiddenByPolicy("secret-folder/private.txt", isDirectory: false));
        Assert.IsFalse(listing.IsPathHiddenByPolicy("visible.txt", isDirectory: false));

        Assert.IsTrue(listing.IsFileDownloadBlocked("app.exe"));
        Assert.IsTrue(listing.IsFileDownloadBlocked("lib.dll"));
        Assert.IsFalse(listing.IsFileDownloadBlocked("readme.txt"));
    }

    [TestMethod]
    public void RequestGuards_FileDownloadToggle_RespectsOptions()
    {
        using var tempDir = new TestTempDirectory("DirPolicy-Guards");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.AllowFileDownload = false;
        options.DenyDownloadExtensions = ["exe"];
        var listing = TestServiceFactory.CreateDirectoryListingService(options);
        var guards = TestServiceFactory.CreateRequestGuards(listing, options);

        Assert.IsFalse(guards.IsFileDownloadAllowed("file.txt"));

        options.AllowFileDownload = true;
        var guardsAllow = TestServiceFactory.CreateRequestGuards(listing, options);
        Assert.IsFalse(guardsAllow.IsFileDownloadAllowed("bad.exe"));
        Assert.IsTrue(guardsAllow.IsFileDownloadAllowed("good.txt"));
    }

    [TestMethod]
    public void TryResolvePhysicalPath_ReturnsFalseForTraversal()
    {
        using var tempDir = new TestTempDirectory("DirPolicy-ResolveGuard");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        var options = TestOptionsFactory.Create(root);
        var listing = TestServiceFactory.CreateDirectoryListingService(options);
        var guards = TestServiceFactory.CreateRequestGuards(listing, options);

        var context = new DefaultHttpContext();
        context.Request.Path = "/../outside.txt";

        var ok = guards.TryResolvePhysicalPath(
            context,
            requestPath: "../outside.txt",
            out _,
            out _);

        Assert.IsFalse(ok);
    }
}
