using System.Text;

namespace TallyIngestion.Api;

/// <summary>Checks whether uploaded bytes look like XML before the XML parser validates their document structure.</summary>
public static class SupportedFileTypeDetector
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LittleEndianBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];

    /// <summary>Returns true when the content decodes as supported text encoding and begins with XML markup.</summary>
    public static bool IsXmlContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var (encoding, offset) = DetectEncoding(content);
        try
        {
            var text = encoding.GetString(content, offset, content.Length - offset);
            var firstContentCharacter = 0;
            while (firstContentCharacter < text.Length && char.IsWhiteSpace(text[firstContentCharacter]))
                firstContentCharacter++;

            // XML starts with markup after any supported byte-order mark and leading whitespace.
            return firstContentCharacter < text.Length && text[firstContentCharacter] == '<';
        }
        catch (DecoderFallbackException)
        {
            // Binary formats and invalid text encodings are not accepted as XML uploads.
            return false;
        }
    }

    private static (Encoding Encoding, int Offset) DetectEncoding(byte[] content)
    {
        if (content.AsSpan().StartsWith(Utf8Bom)) return (new UTF8Encoding(false, true), 3);
        if (content.AsSpan().StartsWith(Utf16LittleEndianBom)) return (new UnicodeEncoding(false, false, true), 2);
        if (content.AsSpan().StartsWith(Utf16BigEndianBom)) return (new UnicodeEncoding(true, false, true), 2);

        // The importer uses strict UTF-8 when no byte-order mark is present.
        return (new UTF8Encoding(false, true), 0);
    }
}
