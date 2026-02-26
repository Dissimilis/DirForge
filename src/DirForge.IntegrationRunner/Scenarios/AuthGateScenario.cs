using System.Net;
using System.Text;
using DirForge.IntegrationRunner.Runner;

namespace DirForge.IntegrationRunner.Scenarios;

internal static class AuthGateScenario
{
    public static async Task RunAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        const string username = "alice";
        const string password = "correct-horse-battery-staple";

        static void Seed(string rootPath, string _)
        {
            File.WriteAllText(
                Path.Combine(rootPath, "hello.txt"),
                "auth protected content",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BasicAuthUser"] = username,
            ["BasicAuthPass"] = password
        };

        await using var env = await TestEnvironment.StartAsync(
            repositoryRoot,
            "AuthGate",
            Seed,
            envVars,
            cancellationToken);

        using var noAuthResponse = await env.GetAsync("/", authorizationHeader: null, cancellationToken);
        Assertions.StatusIs(noAuthResponse, HttpStatusCode.Unauthorized, "Unauthenticated request should be rejected.");
        Assertions.HeaderContains(
            noAuthResponse,
            "WWW-Authenticate",
            "Basic realm=\"Directory Listing\"",
            "Unauthorized response did not include expected auth challenge.");

        var wrongAuthHeader = TestEnvironment.BuildBasicAuthHeader(username, "wrong-password");
        using var wrongAuthResponse = await env.GetAsync("/", wrongAuthHeader, cancellationToken);
        Assertions.StatusIs(wrongAuthResponse, HttpStatusCode.Unauthorized, "Invalid credentials should be rejected.");

        var validAuthHeader = TestEnvironment.BuildBasicAuthHeader(username, password);
        using var validAuthResponse = await env.GetAsync("/", validAuthHeader, cancellationToken);
        Assertions.StatusIs(validAuthResponse, HttpStatusCode.OK, "Valid credentials should grant access.");
        var body = await validAuthResponse.Content.ReadAsStringAsync(cancellationToken);
        Assertions.Contains(body, "hello.txt", "Authenticated listing did not include expected content.");
    }
}
