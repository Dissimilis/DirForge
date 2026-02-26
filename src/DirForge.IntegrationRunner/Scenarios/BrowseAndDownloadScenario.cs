using System.Net;
using System.Text;
using DirForge.IntegrationRunner.Runner;

namespace DirForge.IntegrationRunner.Scenarios;

internal static class BrowseAndDownloadScenario
{
    public static async Task RunAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        const string fileName = "hello.txt";
        const string content = "hello from dirforge\n";

        static void Seed(string rootPath, string _)
        {
            var path = Path.Combine(rootPath, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        await using var env = await TestEnvironment.StartAsync(
            repositoryRoot,
            "BrowseAndDownload",
            Seed,
            environmentOverrides: null,
            cancellationToken);

        using var listingResponse = await env.GetAsync("/", authorizationHeader: null, cancellationToken);
        Assertions.StatusIs(listingResponse, HttpStatusCode.OK, "Directory listing request failed.");
        var listingBody = await listingResponse.Content.ReadAsStringAsync(cancellationToken);
        Assertions.Contains(listingBody, fileName, "Directory listing did not include seeded file.");

        using var fileResponse = await env.GetAsync($"/{fileName}", authorizationHeader: null, cancellationToken);
        Assertions.StatusIs(fileResponse, HttpStatusCode.OK, "File download request failed.");
        var fileBody = await fileResponse.Content.ReadAsStringAsync(cancellationToken);
        Assertions.Equal(content, fileBody, "Downloaded file contents did not match seeded file.");
        Assertions.HeaderContains(fileResponse, "Content-Type", "text/plain", "Downloaded file content type was unexpected.");
        Assertions.HeaderContains(fileResponse, "Content-Disposition", "inline", "Downloaded file disposition was unexpected.");
    }
}
