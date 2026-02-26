using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using AuthenticationHeaderValue = System.Net.Http.Headers.AuthenticationHeaderValue;

namespace DirForge.Security;

public static class BasicAuthParser
{
    private const int MaxAuthorizationHeaderLength = 8192;
    internal static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryReadCredentials(HttpRequest request, out byte[] usernameBytes, out byte[] passwordBytes)
    {
        usernameBytes = [];
        passwordBytes = [];

        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out StringValues authHeaders) || authHeaders.Count != 1)
        {
            return false;
        }

        var raw = authHeaders[0];
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxAuthorizationHeaderLength)
        {
            return false;
        }

        if (!AuthenticationHeaderValue.TryParse(raw, out var authHeader) ||
            !authHeader.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authHeader.Parameter))
        {
            return false;
        }

        var encoded = authHeader.Parameter.AsSpan().Trim();
        if (encoded.Length == 0)
        {
            return false;
        }

        var decodedBuffer = new byte[(encoded.Length * 3) / 4];
        if (!Convert.TryFromBase64Chars(encoded, decodedBuffer, out var bytesWritten))
        {
            return false;
        }

        var decodedBytes = decodedBuffer.AsSpan(0, bytesWritten);
        var separatorIndex = decodedBytes.IndexOf((byte)':');
        if (separatorIndex < 0)
        {
            CryptographicOperations.ZeroMemory(decodedBytes);
            return false;
        }

        var usernameSpan = decodedBytes[..separatorIndex];
        var passwordSpan = decodedBytes[(separatorIndex + 1)..];

        // Enforce UTF-8 validity and normalize to UTF-8 bytes for deterministic comparisons.
        try
        {
            _ = Utf8Strict.GetString(usernameSpan);
            _ = Utf8Strict.GetString(passwordSpan);
        }
        catch (DecoderFallbackException)
        {
            CryptographicOperations.ZeroMemory(decodedBytes);
            return false;
        }

        usernameBytes = usernameSpan.ToArray();
        passwordBytes = passwordSpan.ToArray();
        CryptographicOperations.ZeroMemory(decodedBytes);
        return true;
    }

    internal static bool ValidateCredentials(
        ReadOnlySpan<byte> userBytes, ReadOnlySpan<byte> passBytes,
        ReadOnlySpan<byte> expectedUser, ReadOnlySpan<byte> expectedPass)
    {
        var userMatches = CryptographicOperations.FixedTimeEquals(userBytes, expectedUser);
        var passMatches = CryptographicOperations.FixedTimeEquals(passBytes, expectedPass);
        return userMatches & passMatches;
    }
}
