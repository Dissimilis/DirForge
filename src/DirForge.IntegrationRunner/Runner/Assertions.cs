using System.Net;

namespace DirForge.IntegrationRunner.Runner;

internal static class Assertions
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new TestFailureException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestFailureException($"{message} Expected: '{expected}', Actual: '{actual}'.");
        }
    }

    public static void Contains(string value, string expectedSubstring, string message)
    {
        if (!value.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new TestFailureException($"{message} Missing substring: '{expectedSubstring}'.");
        }
    }

    public static void DoesNotContain(string value, string forbiddenSubstring, string message)
    {
        if (value.Contains(forbiddenSubstring, StringComparison.Ordinal))
        {
            throw new TestFailureException($"{message} Found forbidden substring: '{forbiddenSubstring}'.");
        }
    }

    public static void StatusIs(HttpResponseMessage response, HttpStatusCode expected, string message)
    {
        if (response.StatusCode != expected)
        {
            throw new TestFailureException(
                $"{message} Expected: {(int)expected} ({expected}), Actual: {(int)response.StatusCode} ({response.StatusCode}).");
        }
    }

    public static void StatusIsOneOf(HttpResponseMessage response, string message, params HttpStatusCode[] expected)
    {
        if (expected.Contains(response.StatusCode))
        {
            return;
        }

        var expectedText = string.Join(", ", expected.Select(code => $"{(int)code} ({code})"));
        throw new TestFailureException(
            $"{message} Expected one of: {expectedText}; Actual: {(int)response.StatusCode} ({response.StatusCode}).");
    }

    public static void HeaderContains(HttpResponseMessage response, string headerName, string expectedSubstring, string message)
    {
        if (!TryGetHeader(response, headerName, out var values))
        {
            throw new TestFailureException($"{message} Header '{headerName}' was missing.");
        }

        var joined = string.Join(", ", values);
        if (!joined.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
        {
            throw new TestFailureException(
                $"{message} Header '{headerName}' did not contain '{expectedSubstring}'. Actual: '{joined}'.");
        }
    }

    private static bool TryGetHeader(HttpResponseMessage response, string headerName, out IEnumerable<string> values)
    {
        if (response.Headers.TryGetValues(headerName, out values!))
        {
            return true;
        }

        if (response.Content.Headers.TryGetValues(headerName, out values!))
        {
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }
}
