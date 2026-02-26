using System.IO.Compression;
using System.Text;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ArchiveBrowseServiceUnitTests
{
    [TestMethod]
    public void TryNormalizeVirtualPath_RejectsTraversalSegments()
    {
        var service = new ArchiveBrowseService();

        Assert.IsFalse(service.TryNormalizeVirtualPath("..", out _));
        Assert.IsFalse(service.TryNormalizeVirtualPath("folder/../secret.txt", out _));
        Assert.IsTrue(service.TryNormalizeVirtualPath("folder/data.txt", out var normalized));
        Assert.AreEqual("folder/data.txt", normalized);
    }

    [TestMethod]
    public void BuildListing_ListsRootAndSubfolderEntries()
    {
        using var tempDir = new TestTempDirectory("ArchiveBrowse-Listing");
        var archivePath = Path.Combine(tempDir.Path, "test.zip");
        CreateZip(archivePath);
        var service = new ArchiveBrowseService();

        var rootListing = service.BuildListing(archivePath, string.Empty);
        var folderListing = service.BuildListing(archivePath, "folder");

        Assert.IsTrue(rootListing.Entries.Any(e => e.Name == "readme.txt" && !e.IsDirectory));
        Assert.IsTrue(rootListing.Entries.Any(e => e.Name == "folder" && e.IsDirectory));
        Assert.IsTrue(folderListing.Entries.Any(e => e.Name == "data.txt" && !e.IsDirectory));
    }

    [TestMethod]
    public async Task EntryDownloadInfoAndCopy_WorkForExistingEntry()
    {
        using var tempDir = new TestTempDirectory("ArchiveBrowse-Download");
        var archivePath = Path.Combine(tempDir.Path, "test.zip");
        CreateZip(archivePath);
        var service = new ArchiveBrowseService();

        var found = service.TryGetEntryDownloadInfo(archivePath, "readme.txt", out var info);
        var missing = service.TryGetEntryDownloadInfo(archivePath, "missing.txt", out _);

        Assert.IsTrue(found);
        Assert.IsNotNull(info);
        Assert.AreEqual("readme.txt", info.FileName);
        Assert.IsFalse(missing);

        await using var output = new MemoryStream();
        var copied = await service.CopyEntryToAsync(archivePath, "readme.txt", output, CancellationToken.None);
        var text = Encoding.UTF8.GetString(output.ToArray());

        Assert.IsTrue(copied > 0);
        Assert.AreEqual("readme inside archive\n", text);
    }

    private static void CreateZip(string archivePath)
    {
        using var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        var readme = archive.CreateEntry("readme.txt");
        using (var writer = new StreamWriter(readme.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("readme inside archive\n");
        }

        var nested = archive.CreateEntry("folder/data.txt");
        using (var writer = new StreamWriter(nested.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write("nested data\n");
        }
    }
}
