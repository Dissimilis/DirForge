using DirForge.Models;
using DirForge.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class ShareLinkServiceUnitTests
{
    [TestMethod]
    public void CreateAndValidate_FileToken_RoundTripSucceeds()
    {
        using var tempDir = new TestTempDirectory("ShareLink-RoundTrip");
        var options = TestOptionsFactory.Create(tempDir.Path);
        var service = new ShareLinkService(options);
        var nowUtc = DateTimeOffset.UtcNow;
        var expiresAt = nowUtc.AddHours(1).ToUnixTimeSeconds();

        var token = service.CreateToken(ShareMode.File, "shared-file.txt", expiresAt, isOneTime: false);

        var ok = service.TryValidateToken(token, nowUtc, out var context, out var expired);

        Assert.IsTrue(ok);
        Assert.IsFalse(expired);
        Assert.IsNotNull(context);
        Assert.AreEqual(ShareMode.File, context.Mode);
        Assert.AreEqual("shared-file.txt", context.ScopePath);
        Assert.IsFalse(context.IsOneTime);
    }

    [TestMethod]
    public void ValidateToken_TamperedSignature_Fails()
    {
        using var tempDir = new TestTempDirectory("ShareLink-Tamper");
        var options = TestOptionsFactory.Create(tempDir.Path);
        var service = new ShareLinkService(options);
        var nowUtc = DateTimeOffset.UtcNow;
        var expiresAt = nowUtc.AddHours(1).ToUnixTimeSeconds();

        var token = service.CreateToken(ShareMode.File, "shared-file.txt", expiresAt, isOneTime: false);
        var chars = token.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        var ok = service.TryValidateToken(tampered, nowUtc, out var context, out var expired);

        Assert.IsFalse(ok);
        Assert.IsFalse(expired);
        Assert.IsNull(context);
    }

    [TestMethod]
    public void ValidateToken_ExpiredBeyondSkew_FailsAndMarksExpired()
    {
        using var tempDir = new TestTempDirectory("ShareLink-Expired");
        var options = TestOptionsFactory.Create(tempDir.Path);
        var service = new ShareLinkService(options);
        var issuedAt = DateTimeOffset.UtcNow;
        var expiresAt = issuedAt.ToUnixTimeSeconds();
        var nowBeyondSkew = issuedAt.AddSeconds(ShareLinkService.ExpirySkewSeconds + 2);

        var token = service.CreateToken(ShareMode.File, "shared-file.txt", expiresAt, isOneTime: false);
        var ok = service.TryValidateToken(token, nowBeyondSkew, out var context, out var expired);

        Assert.IsFalse(ok);
        Assert.IsTrue(expired);
        Assert.IsNull(context);
    }

    [TestMethod]
    public void IsRequestAllowed_FileScope_AllowsOnlyExactPath()
    {
        using var tempDir = new TestTempDirectory("ShareLink-FileScope");
        var options = TestOptionsFactory.Create(tempDir.Path);
        var service = new ShareLinkService(options);
        var context = new ShareAccessContext(
            ShareMode.File,
            "shared-file.txt",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            "token");

        var allowedRequest = new DefaultHttpContext();
        allowedRequest.Request.Path = "/shared-file.txt";
        allowedRequest.Request.Method = HttpMethods.Get;

        var deniedRequest = new DefaultHttpContext();
        deniedRequest.Request.Path = "/private.txt";
        deniedRequest.Request.Method = HttpMethods.Get;

        Assert.IsTrue(service.IsRequestAllowed(allowedRequest.Request, context));
        Assert.IsFalse(service.IsRequestAllowed(deniedRequest.Request, context));
    }

    [TestMethod]
    public void IsRequestAllowed_DirectoryScope_AllowsNestedAndRejectsOutside()
    {
        using var tempDir = new TestTempDirectory("ShareLink-DirScope");
        var options = TestOptionsFactory.Create(tempDir.Path);
        var service = new ShareLinkService(options);
        var context = new ShareAccessContext(
            ShareMode.Directory,
            "shared-folder",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            "token");

        var nestedRequest = new DefaultHttpContext();
        nestedRequest.Request.Path = "/shared-folder/inner.txt";
        nestedRequest.Request.Method = HttpMethods.Get;

        var outsideRequest = new DefaultHttpContext();
        outsideRequest.Request.Path = "/private.txt";
        outsideRequest.Request.Method = HttpMethods.Get;

        Assert.IsTrue(service.IsRequestAllowed(nestedRequest.Request, context));
        Assert.IsFalse(service.IsRequestAllowed(outsideRequest.Request, context));
    }
}
