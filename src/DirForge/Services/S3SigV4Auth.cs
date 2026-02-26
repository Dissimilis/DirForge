using System.Security.Cryptography;
using System.Text;

namespace DirForge.Services;

public readonly record struct S3AuthResult(bool IsValid, string? ErrorCode, string? ErrorMessage);

public static class S3SigV4Auth
{
    private const int MaxClockSkewMinutes = 15;

    public static S3AuthResult Validate(HttpRequest request, string accessKeyId, string secretAccessKey, string region)
    {
        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return new S3AuthResult(false, "MissingSecurityHeader", "Missing Authorization header.");
        }

        if (!authHeader.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", "Unsupported authorization algorithm.");
        }

        if (!TryParseAuthHeader(authHeader, out var credential, out var signedHeaderNames, out var providedSignature))
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", "Could not parse Authorization header.");
        }

        // Credential = accessKey/date/region/s3/aws4_request
        var credentialParts = credential.Split('/');
        if (credentialParts.Length != 5)
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", "Invalid credential scope format.");
        }

        var requestAccessKey = credentialParts[0];
        var credentialDate = credentialParts[1];
        var credentialRegion = credentialParts[2];
        var credentialService = credentialParts[3];
        var credentialTerminator = credentialParts[4];

        if (credentialService != "s3" || credentialTerminator != "aws4_request")
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", "Invalid credential scope.");
        }

        if (!string.Equals(requestAccessKey, accessKeyId, StringComparison.Ordinal))
        {
            return new S3AuthResult(false, "InvalidAccessKeyId", "The AWS Access Key Id you provided does not exist in our records.");
        }

        if (!string.Equals(credentialRegion, region, StringComparison.Ordinal))
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", $"Credential region '{credentialRegion}' does not match expected region '{region}'.");
        }

        // Read timestamp
        var amzDate = request.Headers["x-amz-date"].FirstOrDefault();
        string timestamp;
        if (!string.IsNullOrEmpty(amzDate))
        {
            timestamp = amzDate;
        }
        else
        {
            var dateHeader = request.Headers.Date.FirstOrDefault();
            if (string.IsNullOrEmpty(dateHeader))
            {
                return new S3AuthResult(false, "MissingSecurityHeader", "Missing x-amz-date or Date header.");
            }

            if (!DateTimeOffset.TryParse(dateHeader, out var parsedDate))
            {
                return new S3AuthResult(false, "AccessDenied", "Cannot parse Date header.");
            }

            timestamp = parsedDate.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        }

        // Validate timestamp format and clock skew
        if (!DateTime.TryParseExact(timestamp, "yyyyMMddTHHmmssZ",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var requestTime))
        {
            return new S3AuthResult(false, "AccessDenied", "Invalid x-amz-date format.");
        }

        var skew = Math.Abs((DateTime.UtcNow - requestTime).TotalMinutes);
        if (skew > MaxClockSkewMinutes)
        {
            return new S3AuthResult(false, "RequestTimeTooSkewed",
                "The difference between the request time and the current time is too large.");
        }

        var dateStamp = timestamp[..8];
        if (dateStamp != credentialDate)
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", "Date in credential scope does not match x-amz-date.");
        }

        // Signed headers must include host and x-amz-date
        var signedHeaders = signedHeaderNames.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (!signedHeaders.Contains("host", StringComparer.Ordinal))
        {
            return new S3AuthResult(false, "AuthorizationHeaderMalformed", "host must be a signed header.");
        }

        // Build canonical request
        var method = request.Method.ToUpperInvariant();
        var canonicalUri = BuildCanonicalUri(request.Path.Value ?? "/");
        var canonicalQueryString = BuildCanonicalQueryString(request.QueryString.Value);
        var canonicalHeaders = BuildCanonicalHeaders(request, signedHeaders);

        var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault();
        if (string.IsNullOrEmpty(payloadHash))
        {
            payloadHash = "UNSIGNED-PAYLOAD";
        }

        var canonicalRequest = string.Join('\n',
            method,
            canonicalUri,
            canonicalQueryString,
            canonicalHeaders,
            signedHeaderNames,
            payloadHash);

        var scope = $"{dateStamp}/{region}/s3/aws4_request";
        var canonicalRequestHash = HexEncode(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{canonicalRequestHash}";

        // Derive signing key
        var signingKey = DeriveSigningKey(secretAccessKey, dateStamp, region);
        var computedSignature = HexEncode(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        // Constant-time comparison
        var computedBytes = Encoding.UTF8.GetBytes(computedSignature);
        var providedBytes = Encoding.UTF8.GetBytes(providedSignature);
        if (!CryptographicOperations.FixedTimeEquals(computedBytes, providedBytes))
        {
            return new S3AuthResult(false, "SignatureDoesNotMatch",
                "The request signature we calculated does not match the signature you provided.");
        }

        return new S3AuthResult(true, null, null);
    }

    private static bool TryParseAuthHeader(string header, out string credential, out string signedHeaders, out string signature)
    {
        credential = string.Empty;
        signedHeaders = string.Empty;
        signature = string.Empty;

        // AWS4-HMAC-SHA256 Credential=..., SignedHeaders=..., Signature=...
        var paramsPart = header["AWS4-HMAC-SHA256 ".Length..];
        var parts = paramsPart.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.StartsWith("Credential=", StringComparison.Ordinal))
            {
                credential = part["Credential=".Length..];
            }
            else if (part.StartsWith("SignedHeaders=", StringComparison.Ordinal))
            {
                signedHeaders = part["SignedHeaders=".Length..];
            }
            else if (part.StartsWith("Signature=", StringComparison.Ordinal))
            {
                signature = part["Signature=".Length..];
            }
        }

        return !string.IsNullOrEmpty(credential) &&
               !string.IsNullOrEmpty(signedHeaders) &&
               !string.IsNullOrEmpty(signature);
    }

    private static string BuildCanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return "/";
        }

        var segments = path.Split('/', StringSplitOptions.None);
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));
        }

        return string.Join('/', segments);
    }

    private static string BuildCanonicalQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString) || queryString == "?")
        {
            return string.Empty;
        }

        var raw = queryString.StartsWith('?') ? queryString[1..] : queryString;
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var pairs = new SortedList<string, string>(StringComparer.Ordinal);
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex >= 0)
            {
                var key = Uri.EscapeDataString(Uri.UnescapeDataString(part[..eqIndex]));
                var value = Uri.EscapeDataString(Uri.UnescapeDataString(part[(eqIndex + 1)..]));
                pairs[key] = value;
            }
            else
            {
                pairs[Uri.EscapeDataString(Uri.UnescapeDataString(part))] = string.Empty;
            }
        }

        return string.Join('&', pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    private static string BuildCanonicalHeaders(HttpRequest request, string[] signedHeaders)
    {
        var sorted = signedHeaders.OrderBy(h => h, StringComparer.Ordinal);
        var sb = new StringBuilder();

        foreach (var header in sorted)
        {
            var value = header == "host"
                ? request.Host.Value ?? string.Empty
                : request.Headers[header].FirstOrDefault() ?? string.Empty;

            value = value.Trim();
            sb.Append(header).Append(':').Append(value).Append('\n');
        }

        return sb.ToString();
    }

    private static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        var kDate = HMACSHA256.HashData(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string HexEncode(byte[] data)
    {
        return Convert.ToHexStringLower(data);
    }
}
