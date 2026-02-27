using DirForge.Models;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class GlobMatcherUnitTests
{
    [TestMethod]
    public void IsSimpleWildcardMatch_ExactMatch_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("hello", "hello", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_ExactMismatch_ReturnsFalse()
    {
        Assert.IsFalse(GlobMatcher.IsSimpleWildcardMatch("hello", "world", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_StarMatchesAny_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("hello.txt", "*.txt", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_StarMatchesEmpty_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("file", "file*", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_QuestionMatchesSingleChar_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("file1", "file?", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_QuestionDoesNotMatchEmpty_ReturnsFalse()
    {
        Assert.IsFalse(GlobMatcher.IsSimpleWildcardMatch("file", "file?", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_CaseSensitive_ReturnsFalse()
    {
        Assert.IsFalse(GlobMatcher.IsSimpleWildcardMatch("Hello", "hello", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_CaseInsensitive_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("Hello", "hello", ignoreCase: true));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_EmptyValueAndPattern_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("", "", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_StarMatchesEmptyString_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("", "*", ignoreCase: false));
    }

    [TestMethod]
    public void IsSimpleWildcardMatch_StarInMiddle_ReturnsTrue()
    {
        Assert.IsTrue(GlobMatcher.IsSimpleWildcardMatch("start-end", "start*end", ignoreCase: false));
    }

    [TestMethod]
    public void IsPathHiddenByPolicy_MatchesSinglePattern_ReturnsTrue()
    {
        using var tempDir = new TestTempDirectory("glob-hidden-single");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.HidePathPatterns = ["secret"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsTrue(service.IsPathHiddenByPolicy("secret", isDirectory: false));
    }

    [TestMethod]
    public void IsPathHiddenByPolicy_GlobstarMatchesNested_ReturnsTrue()
    {
        using var tempDir = new TestTempDirectory("glob-hidden-nested");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.HidePathPatterns = ["private", "private/**"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsTrue(service.IsPathHiddenByPolicy("private/docs/file.txt", isDirectory: false));
    }

    [TestMethod]
    public void IsPathHiddenByPolicy_UnmatchedPath_ReturnsFalse()
    {
        using var tempDir = new TestTempDirectory("glob-hidden-unmatched");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.HidePathPatterns = ["secret"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsFalse(service.IsPathHiddenByPolicy("public/file.txt", isDirectory: false));
    }

    [TestMethod]
    public void IsPathHiddenByPolicy_NoPatterns_ReturnsFalse()
    {
        using var tempDir = new TestTempDirectory("glob-hidden-none");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.HidePathPatterns = [];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsFalse(service.IsPathHiddenByPolicy("anything/file.txt", isDirectory: false));
    }

    [TestMethod]
    public void IsPathHiddenByPolicy_WildcardSegment_MatchesNames()
    {
        using var tempDir = new TestTempDirectory("glob-hidden-wildcard");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.HidePathPatterns = ["*.secret"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsTrue(service.IsPathHiddenByPolicy("data.secret", isDirectory: false));
        Assert.IsFalse(service.IsPathHiddenByPolicy("data.public", isDirectory: false));
    }

    [TestMethod]
    public void IsFileDownloadBlocked_BlockedExtension_ReturnsTrue()
    {
        using var tempDir = new TestTempDirectory("glob-blocked-ext");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DenyDownloadExtensions = ["exe"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsTrue(service.IsFileDownloadBlocked("app.exe"));
    }

    [TestMethod]
    public void IsFileDownloadBlocked_AllowedExtension_ReturnsFalse()
    {
        using var tempDir = new TestTempDirectory("glob-allowed-ext");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DenyDownloadExtensions = ["exe"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsFalse(service.IsFileDownloadBlocked("readme.txt"));
    }

    [TestMethod]
    public void IsFileDownloadBlocked_NoExtension_ReturnsFalse()
    {
        using var tempDir = new TestTempDirectory("glob-no-ext");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DenyDownloadExtensions = ["exe"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsFalse(service.IsFileDownloadBlocked("Makefile"));
    }

    [TestMethod]
    public void IsFileDownloadBlocked_EmptyDeniedList_ReturnsFalse()
    {
        using var tempDir = new TestTempDirectory("glob-empty-deny");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DenyDownloadExtensions = [];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsFalse(service.IsFileDownloadBlocked("app.exe"));
    }

    [TestMethod]
    public void IsFileDownloadBlocked_CaseInsensitive_ReturnsTrue()
    {
        using var tempDir = new TestTempDirectory("glob-case-ext");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.DenyDownloadExtensions = ["exe"];
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        Assert.IsTrue(service.IsFileDownloadBlocked("app.EXE"));
    }
}
