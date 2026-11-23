using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TallyIngestion.Models;
using TallyIngestion.Models.Vouchers;

namespace TallyIngestion.Application;

/// <summary>Decodes and normalizes the contract-supported Tally XML structures.</summary>
public sealed class XmlImporter
{
    private static readonly Regex InvalidControlReference = new("&#(?:x0*[1-8bcef]|0*[1-8]|x0*1[0-9a-f]|0*(?:1[1-9]|2[0-9]|3[01]));", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> VoucherChildren = new(StringComparer.OrdinalIgnoreCase) { "DATE", "VOUCHERTYPENAME", "VOUCHERNUMBER", "PARTYLEDGERNAME", "NARRATION", "ALLLEDGERENTRIES.LIST", "LEDGERENTRIES.LIST", "ALLINVENTORYENTRIES.LIST" };
    /// <summary>Parses exact bytes, applying only safe recovery for illegal numeric XML control references.</summary>
    /// <exception cref="InvalidDataException">The bytes are not supported XML or use an unsupported root.</exception>
    public ParsedImport Parse(byte[] bytes)
    {
        // Empty byte input cannot represent a supported XML document.
        if (bytes.Length == 0) throw new InvalidDataException("The uploaded file is empty.");

        // Detect BOMs before decoding so UTF-16 files are not mistaken for malformed UTF-8.
        var encoding = DetectEncoding(bytes, out var offset);
        string xml;
        try { xml = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The uploaded file uses an invalid character sequence."); }

        // XML 1.0 forbids some numeric control references; remove only those references and report the recovery.
        var sanitized = InvalidControlReference.IsMatch(xml);
        if (sanitized) xml = InvalidControlReference.Replace(xml, "");
        try
        {
            // Disable DTDs and external entities before loading any document content.
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50_000_000, IgnoreComments = true });
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("The XML document has no root element.");
            var warnings = new List<string>();
            if (sanitized) warnings.Add("Illegal XML 1.0 numeric control references were removed during parsing; original bytes are retained.");

            // A RESPONSE describes Tally's operation outcome, which is separate from successful document parsing.
            if (root.Name.LocalName.Equals("RESPONSE", StringComparison.OrdinalIgnoreCase))
            {
                var names = new[] { "CREATED", "ALTERED", "DELETED", "IGNORED", "ERRORS" };
                var counters = names.Where(n => Child(root, n) is not null).ToDictionary(n => n, n => Child(root, n)!, StringComparer.Ordinal);
                var lineError = Child(root, "LINEERROR");
                var tally = new TallyResponse((int.TryParse(counters.GetValueOrDefault("ERRORS"), out var errors) && errors > 0) || !string.IsNullOrWhiteSpace(lineError) ? "failed" : "succeeded", counters, lineError);
                return new("tallyResponse", EncodingName(encoding), warnings, [], [], tally);
            }
            if (!root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported XML root. Expected ENVELOPE or RESPONSE.");

            // Follow the contract path explicitly so similarly named elements elsewhere cannot be mistaken for vouchers.
            var collection = root.Elements().FirstOrDefault(e => e.Name.LocalName == "BODY")?.Elements().FirstOrDefault(e => e.Name.LocalName == "DATA")?.Elements().FirstOrDefault(e => e.Name.LocalName == "COLLECTION");
            var vouchers = new List<Voucher>();
            if (collection is not null)
            {
                foreach (var node in collection.Elements().Where(e => e.Name.LocalName == "VOUCHER"))
                {
                    var index = vouchers.Count;
                    var unknown = node.Elements().Any(e => !VoucherChildren.Contains(e.Name.LocalName));
                    var vw = unknown ? new List<string> { "Unknown voucher child nodes were retained in the raw source but not normalized." } : [];
                    // Select direct voucher children only; nested AMOUNT/DATE nodes belong to their own entries.
                    vouchers.Add(new(new { voucherIndex = index }, new { VCHTYPE = Attr(node, "VCHTYPE"), ACTION = Attr(node, "ACTION") }, Child(node, "DATE"), Child(node, "VOUCHERTYPENAME"), Child(node, "VOUCHERNUMBER"), Child(node, "PARTYLEDGERNAME"), Child(node, "NARRATION"),
                        node.Elements().Where(e => e.Name.LocalName is "ALLLEDGERENTRIES.LIST" or "LEDGERENTRIES.LIST").Select(ParseLedger).ToList(),
                        node.Elements().Where(e => e.Name.LocalName == "ALLINVENTORYENTRIES.LIST").Select(ParseInventory).ToList(), vw));
                    if (unknown) warnings.Add($"Voucher {index} contains unknown child nodes retained in the raw source but not normalized.");
                }
            }
            return new("voucherExport", EncodingName(encoding), warnings, [], vouchers, null);
        }
        catch (InvalidDataException) { throw; }
        catch (XmlException) { throw new InvalidDataException("The uploaded file is not well-formed supported XML."); }
    }
    private static LedgerEntry ParseLedger(XElement element)
    {
        // Read each allocation from this ledger node so its AMOUNT cannot overwrite the ledger AMOUNT.
        var billAllocations = element.Elements()
            .Where(child => child.Name.LocalName == "BILLALLOCATIONS.LIST")
            .Select(child => new BillAllocation(Child(child, "NAME"), Child(child, "BILLTYPE"), Child(child, "AMOUNT")))
            .ToList();
        var bankAllocations = element.Elements()
            .Where(child => child.Name.LocalName == "BANKALLOCATIONS.LIST")
            .Select(child => new BankAllocation(Child(child, "DATE"), Child(child, "NAME"), Child(child, "TRANSACTIONTYPE"), Child(child, "AMOUNT")))
            .ToList();

        return new LedgerEntry(
            element.Name.LocalName,
            Child(element, "LEDGERNAME"),
            Child(element, "ISDEEMEDPOSITIVE"),
            Child(element, "AMOUNT"),
            billAllocations,
            bankAllocations,
            Rates(element));
    }

    private static InventoryEntry ParseInventory(XElement element)
    {
        // Keep batch and accounting allocations attached to this inventory entry in their source order.
        var batches = element.Elements()
            .Where(child => child.Name.LocalName == "BATCHALLOCATIONS.LIST")
            .Select(child => new BatchAllocation(
                Child(child, "GODOWNNAME"), Child(child, "BATCHNAME"),
                Child(child, "ACTUALQTY"), Child(child, "BILLEDQTY"), Child(child, "AMOUNT")))
            .ToList();
        var accounting = element.Elements()
            .Where(child => child.Name.LocalName == "ACCOUNTINGALLOCATIONS.LIST")
            .Select(child => new AccountingAllocation(
                Child(child, "LEDGERNAME"), Child(child, "ISDEEMEDPOSITIVE"),
                Child(child, "AMOUNT"), Rates(child)))
            .ToList();

        return new InventoryEntry(
            element.Name.LocalName,
            Child(element, "STOCKITEMNAME"),
            Child(element, "ISDEEMEDPOSITIVE"),
            Child(element, "ACTUALQTY"),
            Child(element, "BILLEDQTY"),
            Child(element, "RATE"),
            Child(element, "AMOUNT"),
            batches,
            accounting,
            Rates(element));
    }

    private static List<RateDetail> Rates(XElement element) => element.Elements()
        .Where(child => child.Name.LocalName == "RATEDETAILS.LIST")
        .Select(child => new RateDetail(Child(child, "GSTRATEDUTYHEAD"), Child(child, "GSTRATE")))
        .ToList();

    // XML owners use direct children intentionally; descendant searches can return a nested parent's field.
    private static string? Child(XElement element, string name) => element.Elements()
        .FirstOrDefault(child => child.Name.LocalName == name)?.Value;

    private static string? Attr(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static Encoding DetectEncoding(byte[] bytes, out int offset)
    {
        offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
        {
            offset = 3;
            return new UTF8Encoding(false, true);
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
        {
            offset = 2;
            return new UnicodeEncoding(false, false, true);
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            offset = 2;
            return new UnicodeEncoding(true, false, true);
        }

        // UTF-8 without a byte-order mark is the documented fallback encoding.
        return new UTF8Encoding(false, true);
    }

    private static string EncodingName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "UTF-8",
        1200 => "UTF-16LE",
        1201 => "UTF-16BE",
        _ => encoding.WebName
    };
}

