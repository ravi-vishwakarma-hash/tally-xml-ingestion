using System.IO.Compression;
using System.Text;
using TallyIngestion.Api;
using Xunit;

/// <summary>Verifies that common binary document uploads are identified from content signatures.</summary>
public sealed class UnsupportedFileTypeDetectorTests
{
    [Fact]
    public void DetectsPdfHeaderEvenWhenItDoesNotStartAtByteZero()
    {
        // A short prefix is permitted before the PDF marker, so detection checks the initial 1 KiB only.
        var content = Encoding.ASCII.GetBytes("%!prefix%PDF-1.7\n");

        Assert.Equal("PDF", UnsupportedFileTypeDetector.Detect(content));
    }

    [Fact]
    public void DetectsLegacyWordCompoundFileSignature()
    {
        // Binary .doc files use the OLE Compound File signature rather than a readable XML document.
        byte[] content = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00];

        Assert.Equal("Microsoft Word (.doc)", UnsupportedFileTypeDetector.Detect(content));
    }

    [Fact]
    public void DetectsDocxFromOpenXmlPackageEntries()
    {
        // DOCX is a ZIP package; require both the package content-types index and Word document entry.
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry("word/document.xml");
        }

        Assert.Equal("Microsoft Word Open XML (.docx)", UnsupportedFileTypeDetector.Detect(stream.ToArray()));
    }

    [Fact]
    public void DoesNotClassifyXmlOrAnUnrelatedZipAsWordDocuments()
    {
        var xml = Encoding.UTF8.GetBytes("<RESPONSE><CREATED>1</CREATED></RESPONSE>");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("[Content_Types].xml");
            archive.CreateEntry("xl/workbook.xml");
        }

        Assert.Null(UnsupportedFileTypeDetector.Detect(xml));
        Assert.Null(UnsupportedFileTypeDetector.Detect(stream.ToArray()));
    }
}
