using DirForge.Models;
using DirForge.Pages;
using DirForge.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirForge.IntegrationRunner.Unit;

internal static class TestServiceFactory
{
    public static DirectoryListingService CreateDirectoryListingService(DirForgeOptions options)
    {
        var webRoot = System.IO.Path.Combine(options.RootPath, "wwwroot");
        Directory.CreateDirectory(webRoot);
        var iconResolver = new IconResolver(new TestWebHostEnvironment
        {
            WebRootPath = webRoot,
            ContentRootPath = options.RootPath
        });

        return new DirectoryListingService(
            options,
            iconResolver,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DirectoryListingService>.Instance);
    }

    public static DirectoryRequestGuards CreateRequestGuards(DirectoryListingService listingService, DirForgeOptions options)
    {
        return new DirectoryRequestGuards(
            listingService,
            options,
            NullLogger<DirectoryListingModel>.Instance);
    }
}
