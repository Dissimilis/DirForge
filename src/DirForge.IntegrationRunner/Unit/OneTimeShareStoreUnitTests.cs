using DirForge.Models;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class OneTimeShareStoreUnitTests
{
    [TestMethod]
    public void TryConsumeNonce_AcceptsFirstUse_RejectsReplay()
    {
        var store = new OneTimeShareStore();
        var nowUtc = DateTimeOffset.UtcNow;
        var expiresAt = nowUtc.AddHours(1).ToUnixTimeSeconds();

        var first = store.TryConsumeNonce("nonce-1", expiresAt, nowUtc);
        var second = store.TryConsumeNonce("nonce-1", expiresAt, nowUtc);

        Assert.IsTrue(first);
        Assert.IsFalse(second);
    }

    [TestMethod]
    public void SessionContext_ValidBeforeExpiry_InvalidAfterExpiry()
    {
        var store = new OneTimeShareStore();
        var nowUtc = DateTimeOffset.UtcNow;
        var context = new ShareAccessContext(
            ShareMode.Directory,
            "shared-folder",
            nowUtc.AddMinutes(1).ToUnixTimeSeconds(),
            Token: "token",
            IsOneTime: true,
            Nonce: "nonce-2");
        var sessionId = store.CreateSession(context, nowUtc);

        var valid = store.TryGetSessionContext(sessionId, nowUtc, out var activeContext);
        var expired = store.TryGetSessionContext(
            sessionId,
            nowUtc.AddSeconds(ShareLinkService.ExpirySkewSeconds + 120),
            out var expiredContext);

        Assert.IsTrue(valid);
        Assert.IsNotNull(activeContext);
        Assert.AreEqual(string.Empty, activeContext.Token);
        Assert.IsFalse(expired);
        Assert.IsNull(expiredContext);
    }
}
