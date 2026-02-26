using System.Text;
using DirForge.Models;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DirectoryListingServiceUnitTests
{
    [TestMethod]
    public void ReadEntries_ReturnsFilesAndDirectories()
    {
        using var tempDir = new TestTempDirectory("Listing-ReadEntries");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(
            Path.Combine(dataDir, "readme.txt"),
            "hello",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(dataDir, "data.csv"),
            "a,b,c",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Directory.CreateDirectory(Path.Combine(dataDir, "subdir"));

        var options = TestOptionsFactory.Create(root);
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        var entries = service.ReadEntries(dataDir);

        Assert.AreEqual(3, entries.Count);
        Assert.IsTrue(entries.Any(e => e.Name == "readme.txt" && !e.IsDirectory));
        Assert.IsTrue(entries.Any(e => e.Name == "data.csv" && !e.IsDirectory));
        Assert.IsTrue(entries.Any(e => e.Name == "subdir" && e.IsDirectory));
    }

    [TestMethod]
    public void ReadEntries_HidesDotfilesWhenEnabled()
    {
        using var tempDir = new TestTempDirectory("Listing-HideDotfiles");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(
            Path.Combine(dataDir, "visible.txt"),
            "ok",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(dataDir, ".hidden"),
            "secret",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var options = TestOptionsFactory.Create(root);
        options.HideDotfiles = true;
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        var entries = service.ReadEntries(dataDir);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("visible.txt", entries[0].Name);
    }

    [TestMethod]
    public void GetSortedEntries_SortsByNameWithDirectoriesFirst()
    {
        using var tempDir = new TestTempDirectory("Listing-Sorted");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(
            Path.Combine(dataDir, "zebra.txt"),
            "z",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(dataDir, "alpha.txt"),
            "a",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Directory.CreateDirectory(Path.Combine(dataDir, "middle-folder"));

        var options = TestOptionsFactory.Create(root);
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        var entries = service.GetSortedEntries(
            dataDir,
            relativePath: "data",
            SortMode.Name,
            SortDirection.Asc);

        Assert.AreEqual(3, entries.Count);
        Assert.IsTrue(entries[0].IsDirectory, "Directories should come first.");
        Assert.AreEqual("middle-folder", entries[0].Name);
        Assert.AreEqual("alpha.txt", entries[1].Name);
        Assert.AreEqual("zebra.txt", entries[2].Name);
    }

    [TestMethod]
    public void SearchEntriesRecursive_FindsMatchingFiles()
    {
        using var tempDir = new TestTempDirectory("Listing-Search");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(
            Path.Combine(dataDir, "report.txt"),
            "data",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(dataDir, "notes.md"),
            "notes",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var subDir = Path.Combine(dataDir, "docs");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(
            Path.Combine(subDir, "report-v2.txt"),
            "more data",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var options = TestOptionsFactory.Create(root);
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        var results = service.GetSortedSearchEntries(
            dataDir,
            relativePath: "data",
            searchQuery: "report",
            SortMode.Name,
            SortDirection.Asc,
            maxResults: 100,
            out var truncated);

        Assert.IsFalse(truncated);
        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(e => e.Name.Contains("report", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ReadEntries_PopulatesFileSizeAndHumanSize()
    {
        using var tempDir = new TestTempDirectory("Listing-FileSize");
        var root = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(root);
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        var content = new string('x', 2048);
        File.WriteAllText(
            Path.Combine(dataDir, "sized.txt"),
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var options = TestOptionsFactory.Create(root);
        var service = TestServiceFactory.CreateDirectoryListingService(options);

        var entries = service.ReadEntries(dataDir);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(2048, entries[0].Size);
        StringAssert.EndsWith(entries[0].HumanSize, "KB");
        StringAssert.StartsWith(entries[0].HumanSize, "2");
    }
}
