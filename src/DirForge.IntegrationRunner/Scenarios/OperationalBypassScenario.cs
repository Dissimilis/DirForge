using System.Net;
using System.Text;
using DirForge.IntegrationRunner.Runner;

namespace DirForge.IntegrationRunner.Scenarios;

internal static class OperationalBypassScenario
{
    public static async Task RunAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        const string username = "alice";
        const string password = "operational-auth-pass";

        static void Seed(string rootPath, string _)
        {
            File.WriteAllText(
                Path.Combine(rootPath, "index.txt"),
                "health checks should bypass auth",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BasicAuthUser"] = username,
            ["BasicAuthPass"] = password
        };

        await using var env = await TestEnvironment.StartAsync(
            repositoryRoot,
            "OperationalBypass",
            Seed,
            envVars,
            cancellationToken);

        using var healthResponse = await env.GetAsync("/health", authorizationHeader: null, cancellationToken);
        Assertions.StatusIs(healthResponse, HttpStatusCode.OK, "Health endpoint should bypass global auth.");
        var healthBody = await healthResponse.Content.ReadAsStringAsync(cancellationToken);
        Assertions.Contains(healthBody, "ok", "Health endpoint body was unexpected.");

        using var readyResponse = await env.GetAsync("/readyz", authorizationHeader: null, cancellationToken);
        Assertions.StatusIs(readyResponse, HttpStatusCode.OK, "Ready endpoint should return healthy for readable root.");
        var readyBody = await readyResponse.Content.ReadAsStringAsync(cancellationToken);
        Assertions.Contains(readyBody, "ready", "Readiness endpoint body was unexpected.");

        using var listingResponse = await env.GetAsync("/", authorizationHeader: null, cancellationToken);
        Assertions.StatusIs(listingResponse, HttpStatusCode.Unauthorized, "Root listing should still require auth.");
    }
}
