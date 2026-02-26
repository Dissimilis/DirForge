using System.Security.Cryptography;
using System.Text;
using DirForge.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class S3SigV4AuthUnitTests
{
    private const string TestAccessKeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string TestSecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string TestRegion = "us-east-1";
    private const string TestHost = "s3.us-east-1.amazonaws.com";

    [TestMethod]
    public void ValidSignature_IsAccepted()
    {
        var now = DateTime.UtcNow;
        var timestamp = now.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.ToString("yyyyMMdd");

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/test-bucket/my-file.txt";
        context.Request.Host = new HostString(TestHost);
        context.Request.Headers["x-amz-date"] = timestamp;
        context.Request.Headers["x-amz-content-sha256"] = "UNSIGNED-PAYLOAD";

        var signedHeaderNames = "host;x-amz-content-sha256;x-amz-date";
        var authHeader = BuildSigV4AuthHeader(
            context.Request, TestAccessKeyId, TestSecretAccessKey, TestRegion,
            dateStamp, timestamp, signedHeaderNames);

        context.Request.Headers.Authorization = authHeader;

        var result = S3SigV4Auth.Validate(context.Request, TestAccessKeyId, TestSecretAccessKey, TestRegion);

        Assert.IsTrue(result.IsValid, $"Expected valid result but got error: {result.ErrorCode} - {result.ErrorMessage}");
        Assert.IsNull(result.ErrorCode);
        Assert.IsNull(result.ErrorMessage);
    }

    [TestMethod]
    public void WrongSecretKey_ReturnsSignatureDoesNotMatch()
    {
        var now = DateTime.UtcNow;
        var timestamp = now.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.ToString("yyyyMMdd");

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/";
        context.Request.Host = new HostString(TestHost);
        context.Request.Headers["x-amz-date"] = timestamp;
        context.Request.Headers["x-amz-content-sha256"] = "UNSIGNED-PAYLOAD";

        var wrongSecret = "WRONG_SECRET_KEY_XXXXXXXXXXXXXXXXXXXXXXXX";
        var signedHeaderNames = "host;x-amz-content-sha256;x-amz-date";
        var authHeader = BuildSigV4AuthHeader(
            context.Request, TestAccessKeyId, wrongSecret, TestRegion,
            dateStamp, timestamp, signedHeaderNames);

        context.Request.Headers.Authorization = authHeader;

        var result = S3SigV4Auth.Validate(context.Request, TestAccessKeyId, TestSecretAccessKey, TestRegion);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("SignatureDoesNotMatch", result.ErrorCode);
    }

    [TestMethod]
    public void MissingAuthorizationHeader_ReturnsMissingSecurityHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/";
        context.Request.Host = new HostString(TestHost);
        context.Request.Headers["x-amz-date"] = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");

        var result = S3SigV4Auth.Validate(context.Request, TestAccessKeyId, TestSecretAccessKey, TestRegion);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("MissingSecurityHeader", result.ErrorCode);
        StringAssert.Contains(result.ErrorMessage, "Missing Authorization header");
    }

    // ---- Helper: Compute a real SigV4 signature for test requests ----

    private static string BuildSigV4AuthHeader(
        HttpRequest request,
        string accessKeyId,
        string secretAccessKey,
        string region,
        string dateStamp,
        string timestamp,
        string signedHeaderNames)
    {
        var method = request.Method.ToUpperInvariant();
        var canonicalUri = BuildCanonicalUri(request.Path.Value ?? "/");
        var canonicalQueryString = BuildCanonicalQueryString(request.QueryString.Value);

        var signedHeaders = signedHeaderNames.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var canonicalHeaders = BuildCanonicalHeaders(request, signedHeaders);

        var payloadHash = request.Headers["x-amz-content-sha256"].FirstOrDefault() ?? "UNSIGNED-PAYLOAD";

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

        var kSecret = Encoding.UTF8.GetBytes("AWS4" + secretAccessKey);
        var kDate = HMACSHA256.HashData(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        var signingKey = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));

        var signature = HexEncode(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        var credential = $"{accessKeyId}/{dateStamp}/{region}/s3/aws4_request";
        return $"AWS4-HMAC-SHA256 Credential={credential}, SignedHeaders={signedHeaderNames}, Signature={signature}";
    }

    private static string BuildCanonicalUri(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
            return "/";

        var segments = path.Split('/', StringSplitOptions.None);
        for (var i = 0; i < segments.Length; i++)
            segments[i] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));

        return string.Join('/', segments);
    }

    private static string BuildCanonicalQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString) || queryString == "?")
            return string.Empty;

        var raw = queryString.StartsWith('?') ? queryString[1..] : queryString;
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

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

    private static string HexEncode(byte[] data)
    {
        return Convert.ToHexStringLower(data);
    }
}
