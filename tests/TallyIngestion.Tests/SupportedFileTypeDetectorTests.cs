using System.IO.Compression;
using System.Text;
using TallyIngestion.Api;
using Xunit;

/// <summary>Verifies that only XML-like byte content proceeds to the Tally XML parser.</summary>
public sealed class SupportedFileTypeDetectorTests
{
    [Fact]
    public void AllowsUtf8AndUtf8BomXmlContent()
    {
        // The detector identifies XML markup without relying on the upload filename or MIME type.
        var xml = Encoding.UTF8.GetBytes("<RESPONSE><CREATED>1</CREATED></RESPONSE>");
        var bomXml = Encoding.UTF8.GetPreamble().Concat(xml).ToArray();

        Assert.True(SupportedFileTypeDetector.IsXmlContent(xml));
        Assert.True(SupportedFileTypeDetector.IsXmlContent(bomXml));
    }

    [Fact]
    public void AllowsBomMarkedUtf16XmlContent()
    {
        // UTF-16 is one of the supported XML encodings, so its byte-order mark is recognized before decoding.
        var encoding = Encoding.Unicode;
        var xml = encoding.GetPreamble().Concat(encoding.GetBytes("<ENVELOPE />")).ToArray();

        Assert.True(SupportedFileTypeDetector.IsXmlContent(xml));
    }

    [Theory]
    [InlineData("This is plain text rather than XML.")]
    [InlineData("%PDF-1.7 sample PDF content")]
    public void RejectsTextAndPdfContent(string content)
    {
        // A misleading extension or multipart MIME type cannot turn non-XML bytes into an accepted upload.
        Assert.False(SupportedFileTypeDetector.IsXmlContent(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void RejectsLegacyWordBinaryContent()
    {
        // Legacy .doc files begin with the OLE Compound File signature and are not supported XML.
        byte[] content = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00];

        Assert.False(SupportedFileTypeDetector.IsXmlContent(content));
    }

    [Fact]
    public void RejectsDocxZipPackageContent()
    {
        // DOCX is a ZIP package whose bytes do not begin with XML markup.
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry("word/document.xml");
        }

        Assert.False(SupportedFileTypeDetector.IsXmlContent(stream.ToArray()));
    }
}
