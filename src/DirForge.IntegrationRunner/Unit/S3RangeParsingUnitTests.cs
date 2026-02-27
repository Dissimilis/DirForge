using System.Reflection;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class S3RangeParsingUnitTests
{
    private static bool InvokeTryParseRange(string rangeHeader, long fileLength, out long start, out long end)
    {
        var method = typeof(S3Endpoints).GetMethod("TryParseRange",
            BindingFlags.NonPublic | BindingFlags.Static);
        var args = new object[] { rangeHeader, fileLength, 0L, 0L };
        var result = (bool)method!.Invoke(null, args)!;
        start = (long)args[2];
        end = (long)args[3];
        return result;
    }

    [TestMethod]
    public void TryParseRange_FullRange_ReturnsCorrectBounds()
    {
        var success = InvokeTryParseRange("bytes=0-499", 1000, out var start, out var end);

        Assert.IsTrue(success);
        Assert.AreEqual(0L, start);
        Assert.AreEqual(499L, end);
    }

    [TestMethod]
    public void TryParseRange_OpenEnded_ReturnsToEndOfFile()
    {
        var success = InvokeTryParseRange("bytes=500-", 1000, out var start, out var end);

        Assert.IsTrue(success);
        Assert.AreEqual(500L, start);
        Assert.AreEqual(999L, end);
    }

    [TestMethod]
    public void TryParseRange_Suffix_ReturnsLastNBytes()
    {
        var success = InvokeTryParseRange("bytes=-200", 1000, out var start, out var end);

        Assert.IsTrue(success);
        Assert.AreEqual(800L, start);
        Assert.AreEqual(999L, end);
    }

    [TestMethod]
    public void TryParseRange_SingleByte_ReturnsCorrectBounds()
    {
        var success = InvokeTryParseRange("bytes=0-0", 1000, out var start, out var end);

        Assert.IsTrue(success);
        Assert.AreEqual(0L, start);
        Assert.AreEqual(0L, end);
    }

    [TestMethod]
    public void TryParseRange_LastByte_ReturnsCorrectBounds()
    {
        var success = InvokeTryParseRange("bytes=999-999", 1000, out var start, out var end);

        Assert.IsTrue(success);
        Assert.AreEqual(999L, start);
        Assert.AreEqual(999L, end);
    }

    [TestMethod]
    public void TryParseRange_SuffixLargerThanFile_ClampsToZero()
    {
        var success = InvokeTryParseRange("bytes=-2000", 1000, out var start, out var end);

        Assert.IsTrue(success);
        Assert.AreEqual(0L, start);
        Assert.AreEqual(999L, end);
    }

    [TestMethod]
    public void TryParseRange_MultiRange_ReturnsFalse()
    {
        var success = InvokeTryParseRange("bytes=0-100,200-300", 1000, out _, out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryParseRange_MissingPrefix_ReturnsFalse()
    {
        var success = InvokeTryParseRange("0-100", 1000, out _, out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryParseRange_MissingDash_ReturnsFalse()
    {
        var success = InvokeTryParseRange("bytes=100", 1000, out _, out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryParseRange_StartGreaterThanEnd_ReturnsFalse()
    {
        var success = InvokeTryParseRange("bytes=500-100", 1000, out _, out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryParseRange_StartBeyondFileLength_ReturnsFalse()
    {
        var success = InvokeTryParseRange("bytes=1000-1500", 1000, out _, out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryParseRange_SuffixZero_ReturnsFalse()
    {
        var success = InvokeTryParseRange("bytes=-0", 1000, out _, out _);

        Assert.IsFalse(success);
    }

    [TestMethod]
    public void TryParseRange_EmptySpec_ReturnsFalse()
    {
        var success = InvokeTryParseRange("bytes=", 1000, out _, out _);

        Assert.IsFalse(success);
    }
}
