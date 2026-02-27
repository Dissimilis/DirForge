using System.Text;
using DirForge.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class BasicAuthParserUnitTests
{
    private static DefaultHttpContext CreateContextWithAuth(string headerValue)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = headerValue;
        return context;
    }

    private static string BuildBasicAuth(string user, string pass)
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
    }

    [TestMethod]
    public void TryReadCredentials_ValidBasicAuth_ReturnsTrueWithCorrectBytes()
    {
        var context = CreateContextWithAuth(BuildBasicAuth("user", "pass"));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out var userBytes, out var passBytes);

        Assert.IsTrue(result);
        Assert.AreEqual("user", Encoding.UTF8.GetString(userBytes));
        Assert.AreEqual("pass", Encoding.UTF8.GetString(passBytes));
    }

    [TestMethod]
    public void TryReadCredentials_MissingHeader_ReturnsFalse()
    {
        var context = new DefaultHttpContext();

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_BearerScheme_ReturnsFalse()
    {
        var context = CreateContextWithAuth("Bearer some-token-value");

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_DigestScheme_ReturnsFalse()
    {
        var context = CreateContextWithAuth("Digest username=\"admin\"");

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_InvalidBase64_ReturnsFalse()
    {
        var context = CreateContextWithAuth("Basic !!!bad!!!");

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_NoColonInDecoded_ReturnsFalse()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("nocolon"));
        var context = CreateContextWithAuth("Basic " + encoded);

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_EmptyUsername_ReturnsTrueWithEmptyBytes()
    {
        var context = CreateContextWithAuth(BuildBasicAuth("", "password"));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out var userBytes, out var passBytes);

        Assert.IsTrue(result);
        Assert.AreEqual(0, userBytes.Length);
        Assert.AreEqual("password", Encoding.UTF8.GetString(passBytes));
    }

    [TestMethod]
    public void TryReadCredentials_EmptyPassword_ReturnsTrueWithEmptyBytes()
    {
        var context = CreateContextWithAuth(BuildBasicAuth("user", ""));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out var userBytes, out var passBytes);

        Assert.IsTrue(result);
        Assert.AreEqual("user", Encoding.UTF8.GetString(userBytes));
        Assert.AreEqual(0, passBytes.Length);
    }

    [TestMethod]
    public void TryReadCredentials_HeaderExceedsMaxLength_ReturnsFalse()
    {
        var longUser = new string('a', 8200);
        var context = CreateContextWithAuth(BuildBasicAuth(longUser, "pass"));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_MultipleAuthHeaders_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Append("Authorization", BuildBasicAuth("user1", "pass1"));
        context.Request.Headers.Append("Authorization", BuildBasicAuth("user2", "pass2"));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_EmptyHeaderValue_ReturnsFalse()
    {
        var context = CreateContextWithAuth("");

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_WhitespaceOnlyHeader_ReturnsFalse()
    {
        var context = CreateContextWithAuth("   ");

        var result = BasicAuthParser.TryReadCredentials(context.Request, out _, out _);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryReadCredentials_Utf8MultibyteCredentials_RoundTrips()
    {
        var context = CreateContextWithAuth(BuildBasicAuth("\u00e9mile", "p\u00e4ssw\u00f6rd"));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out var userBytes, out var passBytes);

        Assert.IsTrue(result);
        Assert.AreEqual("\u00e9mile", Encoding.UTF8.GetString(userBytes));
        Assert.AreEqual("p\u00e4ssw\u00f6rd", Encoding.UTF8.GetString(passBytes));
    }

    [TestMethod]
    public void TryReadCredentials_PasswordContainsColons_ReturnsFullPassword()
    {
        var context = CreateContextWithAuth(BuildBasicAuth("user", "pass:word:extra"));

        var result = BasicAuthParser.TryReadCredentials(context.Request, out var userBytes, out var passBytes);

        Assert.IsTrue(result);
        Assert.AreEqual("user", Encoding.UTF8.GetString(userBytes));
        Assert.AreEqual("pass:word:extra", Encoding.UTF8.GetString(passBytes));
    }

    [TestMethod]
    public void TryReadCredentials_LowercaseScheme_ReturnsTrue()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));
        var context = CreateContextWithAuth("basic " + encoded);

        var result = BasicAuthParser.TryReadCredentials(context.Request, out var userBytes, out var passBytes);

        Assert.IsTrue(result);
        Assert.AreEqual("user", Encoding.UTF8.GetString(userBytes));
        Assert.AreEqual("pass", Encoding.UTF8.GetString(passBytes));
    }
}
