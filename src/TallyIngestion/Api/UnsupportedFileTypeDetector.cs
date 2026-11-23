using System.IO.Compression;
using System.Text;

namespace TallyIngestion.Api;

/// <summary>Identifies common binary document types that the Tally XML importer cannot process.</summary>
public static class UnsupportedFileTypeDetector
{
    private static readonly byte[] PdfHeader = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] CompoundFileHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Returns a friendly type label for PDF, legacy Word, or DOCX content; otherwise returns null.</summary>
    public static string? Detect(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (ContainsPdfHeader(content)) return "PDF";
        if (content.AsSpan().StartsWith(CompoundFileHeader)) return "Microsoft Word (.doc)";
        if (IsWordOpenXmlPackage(content)) return "Microsoft Word Open XML (.docx)";

        return null;
    }

    // PDF readers allow a small prefix before the header, so inspect only the first 1 KiB.
    private static bool ContainsPdfHeader(byte[] content)
    {
        var lastPossibleOffset = Math.Min(content.Length - PdfHeader.Length, 1024);
        for (var offset = 0; offset <= lastPossibleOffset; offset++)
        {
            if (content.AsSpan(offset).StartsWith(PdfHeader)) return true;
        }

        return false;
    }

    // DOCX is an Open XML ZIP package; inspect its directory without extracting or decompressing document entries.
    private static bool IsWordOpenXmlPackage(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.GetEntry("[Content_Types].xml") is not null &&
                   archive.GetEntry("word/document.xml") is not null;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            // Non-ZIP or malformed ZIP data continues to the XML parser, which returns its normal invalid-XML error.
            return false;
        }
    }
}
