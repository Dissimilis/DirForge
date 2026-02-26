using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DirForge.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace DirForge.Services;

public sealed class ShareLinkService
{
    public const string TokenQueryParameter = "s";
    public const string HttpContextItemKey = "DirForge.ShareContext";
    public const int ExpirySkewSeconds = 60;

    private readonly byte[] _secretKey;
    private readonly StringComparison _pathComparison;

    public ShareLinkService(DirForgeOptions options)
    {
        _secretKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(options.ShareSecret),
            outputLength: 32,
            salt: null,
            info: Encoding.UTF8.GetBytes("dirforge-share-v1"));
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    public string CreateToken(ShareMode mode, string relativePath, long expiresAtUnix, bool isOneTime = false)
    {
        var normalizedPath = NormalizeRelativePath(relativePath);
        var nonce = isOneTime
            ? WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))
            : string.Empty;
        var payload = new TokenPayload
        {
            V = 1,
            M = mode == ShareMode.File ? "f" : "d",
            P = normalizedPath,
            E = expiresAtUnix,
            O = isOneTime ? 1 : 0,
            N = nonce
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signatureBytes = ComputeSignature(payloadBytes);
        return $"{WebEncoders.Base64UrlEncode(payloadBytes)}.{WebEncoders.Base64UrlEncode(signatureBytes)}";
    }

    public bool TryValidateToken(string token, DateTimeOffset nowUtc, out ShareAccessContext? context)
    {
        return TryValidateToken(token, nowUtc, out context, out _);
    }

    public bool TryValidateToken(string token, DateTimeOffset nowUtc, out ShareAccessContext? context, out bool expired)
    {
        context = null;
        expired = false;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var separatorIndex = token.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= token.Length - 1)
        {
            return false;
        }

        var payloadPart = token[..separatorIndex];
        var signaturePart = token[(separatorIndex + 1)..];

        byte[] payloadBytes;
        byte[] signatureBytes;
        try
        {
            payloadBytes = WebEncoders.Base64UrlDecode(payloadPart);
            signatureBytes = WebEncoders.Base64UrlDecode(signaturePart);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = ComputeSignature(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSignature))
        {
            return false;
        }

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null || payload.V != 1 || string.IsNullOrWhiteSpace(payload.M) || payload.E <= 0)
        {
            return false;
        }

        var mode = payload.M switch
        {
            "f" => ShareMode.File,
            "d" => ShareMode.Directory,
            _ => (ShareMode?)null
        };

        if (mode is null)
        {
            return false;
        }

        if (payload.O is < 0 or > 1)
        {
            return false;
        }

        var isOneTime = payload.O == 1;
        var nonce = isOneTime
            ? payload.N?.Trim() ?? string.Empty
            : string.Empty;
        if (isOneTime && string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        var normalizedPath = NormalizeRelativePath(payload.P);
        if (mode == ShareMode.File && string.IsNullOrEmpty(normalizedPath))
        {
            return false;
        }

        var nowUnix = nowUtc.ToUnixTimeSeconds();
        if (nowUnix > payload.E + ExpirySkewSeconds)
        {
            expired = true;
            return false;
        }

        context = new ShareAccessContext(mode.Value, normalizedPath, payload.E, token, isOneTime, nonce);
        return true;
    }

    public bool IsRequestAllowed(HttpRequest request, ShareAccessContext context)
    {
        if (StaticAssetRouteHelper.IsStaticRequest(request.Path))
        {
            return true;
        }

        var requestPath = NormalizeRelativePath(request.Path.Value);
        var handler = request.Query["handler"].ToString();
        var isArchiveRoute = TryResolveArchiveRoutePath(requestPath, out var scopeRequestPath);

        if (context.Mode == ShareMode.File)
        {
            if (!scopeRequestPath.Equals(context.ScopePath, _pathComparison))
            {
                return false;
            }

            if (isArchiveRoute)
            {
                return string.IsNullOrEmpty(handler) ||
                       handler.Equals("DownloadEntry", StringComparison.OrdinalIgnoreCase);
            }

            return string.IsNullOrEmpty(handler) ||
                   handler.Equals("View", StringComparison.OrdinalIgnoreCase) ||
                   handler.Equals("Archive", StringComparison.OrdinalIgnoreCase) ||
                   handler.Equals("ArchiveEntry", StringComparison.OrdinalIgnoreCase);
        }

        if (!IsPathWithinScope(scopeRequestPath, context.ScopePath))
        {
            return false;
        }

        if (isArchiveRoute)
        {
            return string.IsNullOrEmpty(handler) ||
                   handler.Equals("DownloadEntry", StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrEmpty(handler) ||
               handler.Equals("View", StringComparison.OrdinalIgnoreCase) ||
               handler.Equals("DirectorySizes", StringComparison.OrdinalIgnoreCase) ||
               handler.Equals("DownloadZip", StringComparison.OrdinalIgnoreCase) ||
               handler.Equals("Archive", StringComparison.OrdinalIgnoreCase) ||
               handler.Equals("ArchiveEntry", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsPathWithinScope(string requestPath, string scopePath)
    {
        var normalizedRequest = NormalizeRelativePath(requestPath);
        var normalizedScope = NormalizeRelativePath(scopePath);

        if (string.IsNullOrEmpty(normalizedScope))
        {
            return true;
        }

        return normalizedRequest.Equals(normalizedScope, _pathComparison) ||
               normalizedRequest.StartsWith(normalizedScope + "/", _pathComparison);
    }

    public static void AppendTokenQuery(ICollection<string> queryParts, string? shareToken)
    {
        if (!string.IsNullOrEmpty(shareToken))
        {
            queryParts.Add($"{TokenQueryParameter}={Uri.EscapeDataString(shareToken)}");
        }
    }

    public static string NormalizeRelativePath(string? path)
    {
        return DirectoryListingService.NormalizeRelativePath(path);
    }

    private byte[] ComputeSignature(byte[] payloadBytes)
    {
        using var hmac = new HMACSHA256(_secretKey);
        return hmac.ComputeHash(payloadBytes);
    }

    private bool TryResolveArchiveRoutePath(string requestPath, out string scopeRequestPath)
    {
        scopeRequestPath = requestPath;

        if (requestPath.Equals("archive", _pathComparison))
        {
            scopeRequestPath = string.Empty;
            return true;
        }

        if (requestPath.StartsWith("archive/", _pathComparison))
        {
            scopeRequestPath = requestPath["archive/".Length..];
            return true;
        }

        return false;
    }

    private sealed class TokenPayload
    {
        public int V { get; set; }
        public string M { get; set; } = string.Empty;
        public string P { get; set; } = string.Empty;
        public long E { get; set; }
        public int O { get; set; }
        public string N { get; set; } = string.Empty;
    }
}
