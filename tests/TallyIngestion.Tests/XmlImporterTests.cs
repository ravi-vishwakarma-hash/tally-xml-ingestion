using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using TallyIngestion.Application;
using TallyIngestion.Infrastructure;
using Xunit;

/// <summary>Checks XML decoding, parent-child ownership, diagnostics, and duplicate import behavior.</summary>
public sealed class XmlImporterTests
{
    private readonly XmlImporter importer = new();

    // Resolve fixture files from the test output folder where the test project copies them during build.
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void RepeatedLedgersRemainOrderedAndDistinct()
    {
        // Two ledger rows have similar structure, so the parser must retain both and preserve their original order.
        var bytes = File.ReadAllBytes(Fixture("01_receipt_with_allocations.xml"));
        var parsed = importer.Parse(bytes);
        var ledgerEntries = parsed.Vouchers[0].LedgerEntries;

        Assert.Equal(2, ledgerEntries.Count);
        Assert.Equal("Demo Bank", ledgerEntries[0].LedgerName);
        Assert.Equal("Demo Customer", ledgerEntries[1].LedgerName);
        Assert.Single(ledgerEntries[1].BillAllocations);
        Assert.Single(ledgerEntries[0].BankAllocations);
    }

    [Fact]
    public void NestedAmountsRemainOwnedAndLossless()
    {
        // The fixture repeats AMOUNT at multiple levels; assertions guard against descendant-wide value overwrites.
        var bytes = File.ReadAllBytes(Fixture("02_sales_invoice_nested.xml"));
        var voucher = importer.Parse(bytes).Vouchers[0];
        var inventory = Assert.Single(voucher.InventoryEntries);

        Assert.Equal("-118.00", voucher.LedgerEntries[0].Amount);
        Assert.Equal("100.00", inventory.Amount);
        Assert.Equal("100.00", inventory.BatchAllocations[0].Amount);
        Assert.Equal("100.00", inventory.AccountingAllocations[0].Amount);
        Assert.Equal("50.00/PCS", inventory.Rate);
        Assert.Equal("2 PCS", inventory.ActualQty);
        Assert.Equal("CGST", inventory.AccountingAllocations[0].RateDetails[0].DutyHead);
    }

    [Fact]
    public void Utf16AndIllegalControlReferenceAreRecoveredWithWarning()
    {
        // The fixture combines BOM-marked UTF-16 with an illegal XML numeric control reference.
        var bytes = File.ReadAllBytes(Fixture("04_utf16_invalid_control_ref.xml"));
        var parsed = importer.Parse(bytes);

        Assert.Equal("UTF-16LE", parsed.Encoding);
        Assert.Single(parsed.Warnings);
        Assert.Contains("Recoverable control reference  in", parsed.Vouchers[0].Narration);
    }

    [Fact]
    public void TallyErrorsAreBusinessFailure()
    {
        // Parsing the RESPONSE document succeeds while its counters report a failed Tally business operation.
        var bytes = File.ReadAllBytes(Fixture("03_tally_error_response.xml"));
        var parsed = importer.Parse(bytes);

        Assert.Equal("tallyResponse", parsed.DocumentType);
        Assert.Equal("failed", parsed.TallyResponse!.BusinessStatus);
        Assert.Equal("1", parsed.TallyResponse.Counters["ERRORS"]);
    }

    [Fact]
    public void EmptyCollectionAndUnknownChildrenAreSafe()
    {
        // A valid empty export has no vouchers; an unknown child is warned about without inventing normalized data.
        var emptyBytes = File.ReadAllBytes(Fixture("05_empty_collection.xml"));
        Assert.Empty(importer.Parse(emptyBytes).Vouchers);

        var xml = Encoding.UTF8.GetBytes(
            "<ENVELOPE><BODY><DATA><COLLECTION><VOUCHER><CUSTOM>safe</CUSTOM></VOUCHER></COLLECTION></DATA></BODY></ENVELOPE>");
        var parsed = importer.Parse(xml);

        Assert.Single(parsed.Vouchers);
        Assert.Empty(parsed.Vouchers[0].LedgerEntries);
        Assert.Empty(parsed.Warnings);
        Assert.NotEmpty(parsed.Vouchers[0].Warnings);
    }

    [Fact]
    public void UnknownNestedAndResponseNodesAreReportedAtTheirOwningScope()
    {
        // Nested custom fields belong in the voucher warning, while response-level extensions are import-wide.
        var voucherXml = Encoding.UTF8.GetBytes(
            "<ENVELOPE><BODY><DATA><COLLECTION><VOUCHER><ALLLEDGERENTRIES.LIST><LEDGERNAME>Cash</LEDGERNAME><AMOUNT>1.00</AMOUNT><CUSTOMFIELD>x</CUSTOMFIELD></ALLLEDGERENTRIES.LIST></VOUCHER></COLLECTION></DATA></BODY></ENVELOPE>");
        var responseXml = Encoding.UTF8.GetBytes("<RESPONSE><CREATED>1</CREATED><CUSTOMRESULT>kept</CUSTOMRESULT></RESPONSE>");

        var voucherImport = importer.Parse(voucherXml);
        var responseImport = importer.Parse(responseXml);

        Assert.Single(voucherImport.Vouchers[0].Warnings);
        Assert.Empty(voucherImport.Warnings);
        Assert.Single(responseImport.Warnings);
        Assert.Empty(responseImport.Vouchers);
    }

    [Fact]
    public void InvalidXmlNumericReferencesAreRemovedAndLegalWhitespaceReferencesRemain()
    {
        // XML 1.0 excludes NUL, surrogate, and FFFE references, but permits tab and carriage return references.
        var xml = Encoding.UTF8.GetBytes(
            "<ENVELOPE><BODY><DATA><COLLECTION><VOUCHER><NARRATION>bad &#0; &#xD800; &#65534; good&#9;tab&#13;end</NARRATION></VOUCHER></COLLECTION></DATA></BODY></ENVELOPE>");

        var parsed = importer.Parse(xml);

        Assert.Single(parsed.Warnings);
        Assert.Equal("bad    good\ttab\rend", parsed.Vouchers[0].Narration);
    }

    [Fact]
    public void Utf8BomAndUtf8AreDetected()
    {
        // Verify both BOM-marked and ordinary UTF-8 paths, as the bytes look different to the decoder.
        var bomBytes = File.ReadAllBytes(Fixture("07_utf8_bom_payment.xml"));
        var plainBytes = File.ReadAllBytes(Fixture("01_receipt_with_allocations.xml"));

        Assert.Equal("UTF-8", importer.Parse(bomBytes).Encoding);
        Assert.Equal("UTF-8", importer.Parse(plainBytes).Encoding);
    }

    [Fact]
    public void DtdIsRejected()
    {
        // External entity declarations must be rejected so uploaded XML cannot read local resources.
        var xml = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><ENVELOPE>&e;</ENVELOPE>");

        Assert.Throws<InvalidDataException>(() => importer.Parse(xml));
    }

    [Fact]
    public async Task InMemoryStorageIsIdempotentForExactBytes()
    {
        // Reusing the exact content hash must return the first ID and leave its voucher collection unchanged.
        var raw = File.ReadAllBytes(Fixture("01_receipt_with_allocations.xml"));
        var parsed = importer.Parse(raw);
        var hash = Convert.ToHexStringLower(SHA256.HashData(raw));
        var store = new ImportStore("InMemory", null);

        var first = await store.SaveAsync(parsed, raw, hash, CancellationToken.None);
        var duplicate = await store.SaveAsync(parsed, raw, hash, CancellationToken.None);

        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        var importId = JsonDocument.Parse(JsonSerializer.Serialize(first.Body))
            .RootElement.GetProperty("importId").GetString()!;
        var vouchers = await store.GetVouchersAsync(importId, CancellationToken.None);
        var count = JsonDocument.Parse(JsonSerializer.Serialize(vouchers)).RootElement.GetProperty("count").GetInt32();
        Assert.Equal(1, count);
    }
}
