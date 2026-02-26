using System.Collections.Concurrent;
using System.Security.Cryptography;
using DirForge.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace DirForge.Services;

public sealed class OneTimeShareStore
{
    private readonly ConcurrentDictionary<string, long> _consumedNonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionGrant> _sessionGrants = new(StringComparer.Ordinal);

    public bool TryConsumeNonce(string nonce, long expiresAtUnix, DateTimeOffset nowUtc)
    {
        ReapExpired(nowUtc.ToUnixTimeSeconds());
        return _consumedNonces.TryAdd(nonce, expiresAtUnix);
    }

    public string CreateSession(ShareAccessContext context, DateTimeOffset nowUtc)
    {
        ReapExpired(nowUtc.ToUnixTimeSeconds());
        var sessionId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        _sessionGrants[sessionId] = new SessionGrant(context with { Token = string.Empty }, context.ExpiresAtUnix);
        return sessionId;
    }

    public bool TryGetSessionContext(string sessionId, DateTimeOffset nowUtc, out ShareAccessContext? context)
    {
        context = null;
        var nowUnix = nowUtc.ToUnixTimeSeconds();
        ReapExpired(nowUnix);

        if (!_sessionGrants.TryGetValue(sessionId, out var grant))
        {
            return false;
        }

        if (nowUnix > grant.ExpiresAtUnix + ShareLinkService.ExpirySkewSeconds)
        {
            _sessionGrants.TryRemove(sessionId, out _);
            return false;
        }

        context = grant.Context;
        return true;
    }

    private void ReapExpired(long nowUnix)
    {
        foreach (var item in _consumedNonces)
        {
            if (nowUnix > item.Value + ShareLinkService.ExpirySkewSeconds)
            {
                _consumedNonces.TryRemove(item.Key, out _);
            }
        }

        foreach (var item in _sessionGrants)
        {
            if (nowUnix > item.Value.ExpiresAtUnix + ShareLinkService.ExpirySkewSeconds)
            {
                _sessionGrants.TryRemove(item.Key, out _);
            }
        }
    }

    private sealed record SessionGrant(ShareAccessContext Context, long ExpiresAtUnix);
}
