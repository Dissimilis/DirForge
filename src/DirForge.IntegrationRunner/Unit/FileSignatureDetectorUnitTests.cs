using System.Text;
using DirForge.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DirForge.IntegrationRunner.Unit;

[TestClass]
[TestCategory("Unit")]
public sealed class FileSignatureDetectorUnitTests
{
    [TestMethod]
    public void Detect_PngSignature_ReturnsPngImage()
    {
        using var temp = new TestTempDirectory("Sig-PNG");
        var path = Path.Combine(temp.Path, "test.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00]);

        Assert.AreEqual("PNG Image", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_PdfSignature_ReturnsPdfDocument()
    {
        using var temp = new TestTempDirectory("Sig-PDF");
        var path = Path.Combine(temp.Path, "test.pdf");
        File.WriteAllBytes(path, "%PDF-1.4 fake"u8.ToArray());

        Assert.AreEqual("PDF Document", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_ZipSignature_ReturnsZipArchive()
    {
        using var temp = new TestTempDirectory("Sig-ZIP");
        var path = Path.Combine(temp.Path, "test.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);

        Assert.AreEqual("ZIP Archive", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_ElfSignature_ReturnsElfExecutable()
    {
        using var temp = new TestTempDirectory("Sig-ELF");
        var path = Path.Combine(temp.Path, "test.elf");
        File.WriteAllBytes(path, [0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01]);

        Assert.AreEqual("ELF Executable", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_SvgMarkup_ReturnsSvgImage()
    {
        using var temp = new TestTempDirectory("Sig-SVG");
        var path = Path.Combine(temp.Path, "test.svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");

        Assert.AreEqual("SVG Image", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_UnknownBytes_ReturnsNull()
    {
        using var temp = new TestTempDirectory("Sig-Unknown");
        var path = Path.Combine(temp.Path, "test.bin");
        File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        Assert.IsNull(FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_EmptyFile_ReturnsNull()
    {
        using var temp = new TestTempDirectory("Sig-Empty");
        var path = Path.Combine(temp.Path, "empty.bin");
        File.WriteAllBytes(path, []);

        Assert.IsNull(FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_NonExistentPath_ReturnsNull()
    {
        Assert.IsNull(FileSignatureDetector.Detect(@"C:\nonexistent\fake\file.bin"));
    }

    [TestMethod]
    public void Detect_JsonObject_ReturnsJsonData()
    {
        using var temp = new TestTempDirectory("Sig-JSON-Obj");
        var path = Path.Combine(temp.Path, "test.json");
        File.WriteAllText(path, "{\"key\": \"value\", \"num\": 42}");

        Assert.AreEqual("JSON Data", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_JsonArray_ReturnsJsonData()
    {
        using var temp = new TestTempDirectory("Sig-JSON-Arr");
        var path = Path.Combine(temp.Path, "test.json");
        File.WriteAllText(path, "[1, 2, 3]");

        Assert.AreEqual("JSON Data", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_YamlDocument_ReturnsYamlDocument()
    {
        using var temp = new TestTempDirectory("Sig-YAML");
        var path = Path.Combine(temp.Path, "test.yaml");
        File.WriteAllText(path, "---\nkey: value\nlist:\n  - item1\n  - item2\n");

        Assert.AreEqual("YAML Document", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_HtmlDocument_ReturnsHtmlDocument()
    {
        using var temp = new TestTempDirectory("Sig-HTML");
        var path = Path.Combine(temp.Path, "test.html");
        File.WriteAllText(path, "<!DOCTYPE html>\n<html><head><title>Test</title></head><body></body></html>");

        Assert.AreEqual("HTML Document", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_XmlDocument_ReturnsXmlDocument()
    {
        using var temp = new TestTempDirectory("Sig-XML");
        var path = Path.Combine(temp.Path, "test.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root><item>data</item></root>");

        Assert.AreEqual("XML Document", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_CssStylesheet_ReturnsCssStylesheet()
    {
        using var temp = new TestTempDirectory("Sig-CSS");
        var path = Path.Combine(temp.Path, "test.css");
        File.WriteAllText(path, "@charset \"UTF-8\";\nbody { margin: 0; padding: 0; }");

        Assert.AreEqual("CSS Stylesheet", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_CsvData_ReturnsCsvData()
    {
        using var temp = new TestTempDirectory("Sig-CSV");
        var path = Path.Combine(temp.Path, "test.csv");
        File.WriteAllText(path, "name,age,city\nAlice,30,NYC\nBob,25,LA\nCarol,28,Chicago\n");

        Assert.AreEqual("CSV Data", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_ShellScript_ReturnsShellScript()
    {
        using var temp = new TestTempDirectory("Sig-Shell");
        var path = Path.Combine(temp.Path, "test.sh");
        File.WriteAllText(path, "#!/bin/bash\necho \"hello world\"\n");

        Assert.AreEqual("Shell Script", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_PythonScript_ReturnsPythonScript()
    {
        using var temp = new TestTempDirectory("Sig-Python");
        var path = Path.Combine(temp.Path, "test.py");
        File.WriteAllText(path, "#!/usr/bin/env python3\nprint('hello')\n");

        Assert.AreEqual("Python Script", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_Dockerfile_ReturnsDockerfile()
    {
        using var temp = new TestTempDirectory("Sig-Docker");
        var path = Path.Combine(temp.Path, "Dockerfile");
        File.WriteAllText(path, "FROM ubuntu:22.04\nRUN apt-get update\n");

        Assert.AreEqual("Dockerfile", FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_NullBytesSkipsTextDetection()
    {
        using var temp = new TestTempDirectory("Sig-NullByte");
        var path = Path.Combine(temp.Path, "test.bin");
        // Starts with '{' but contains null bytes — should NOT detect as JSON
        File.WriteAllBytes(path, [0x7B, 0x22, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04]);

        Assert.IsNull(FileSignatureDetector.Detect(path));
    }

    [TestMethod]
    public void Detect_BomStripped_StillDetects()
    {
        using var temp = new TestTempDirectory("Sig-BOM");
        var path = Path.Combine(temp.Path, "test.json");
        // UTF-8 BOM (EF BB BF) followed by JSON
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var json = Encoding.UTF8.GetBytes("{\"key\": \"value\"}");
        var content = new byte[bom.Length + json.Length];
        bom.CopyTo(content, 0);
        json.CopyTo(content, bom.Length);
        File.WriteAllBytes(path, content);

        Assert.AreEqual("JSON Data", FileSignatureDetector.Detect(path));
    }
}
