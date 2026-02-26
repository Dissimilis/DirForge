namespace DirForge.Models;

public sealed record ShareAccessContext(
    ShareMode Mode,
    string ScopePath,
    long ExpiresAtUnix,
    string Token,
    bool IsOneTime = false,
    string Nonce = "");
