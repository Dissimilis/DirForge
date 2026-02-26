using DirForge.Pages;
using DirForge.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class DirectoryFileActionHandlersUnitTests
{
    [TestMethod]
    public async Task HandleGetDownloadZipAsync_WhenFolderDownloadDisabled_ReturnsForbidden()
    {
        using var tempDir = new TestTempDirectory("FileHandlers-DownloadDisabled");
        var options = TestOptionsFactory.Create(tempDir.Path);
        options.AllowFolderDownload = false;

        var listing = TestServiceFactory.CreateDirectoryListingService(options);
        var guards = TestServiceFactory.CreateRequestGuards(listing, options);
        var share = new ShareLinkService(options);
        var archive = new ArchiveBrowseService();
        var icon = new IconResolver(new TestWebHostEnvironment
        {
            WebRootPath = Path.Combine(tempDir.Path, "wwwroot"),
            ContentRootPath = tempDir.Path
        });
        var metrics = new DashboardMetricsService();
        var handlers = new DirectoryFileActionHandlers(
            listing,
            share,
            archive,
            icon,
            options,
            metrics,
            NullLogger<DirectoryListingModel>.Instance,
            guards);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/folder";

        var result = await handlers.HandleGetDownloadZipAsync(
            context,
            requestPath: "folder",
            shareContext: null,
            cancellationToken: CancellationToken.None);

        var status = result as StatusCodeResult;
        Assert.IsNotNull(status);
        Assert.AreEqual(StatusCodes.Status403Forbidden, status.StatusCode);
    }
}
