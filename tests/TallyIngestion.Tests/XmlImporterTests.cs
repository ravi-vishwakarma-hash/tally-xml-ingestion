using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using TallyIngestion.Application;
using TallyIngestion.Infrastructure;
using Xunit;

public sealed class XmlImporterTests
{
    private readonly XmlImporter importer = new();
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory,"fixtures",name);
    [Fact] public void RepeatedLedgersRemainOrderedAndDistinct() { var parsed=importer.Parse(File.ReadAllBytes(Fixture("01_receipt_with_allocations.xml"))); Assert.Equal(2,parsed.Vouchers[0].LedgerEntries.Count); Assert.Equal("Demo Bank",parsed.Vouchers[0].LedgerEntries[0].LedgerName); Assert.Equal("Demo Customer",parsed.Vouchers[0].LedgerEntries[1].LedgerName); Assert.Single(parsed.Vouchers[0].LedgerEntries[1].BillAllocations); Assert.Single(parsed.Vouchers[0].LedgerEntries[0].BankAllocations); }
    [Fact] public void NestedAmountsRemainOwnedAndLossless() { var v=importer.Parse(File.ReadAllBytes(Fixture("02_sales_invoice_nested.xml"))).Vouchers[0]; Assert.Equal("-118.00",v.LedgerEntries[0].Amount); var i=Assert.Single(v.InventoryEntries); Assert.Equal("100.00",i.Amount); Assert.Equal("100.00",i.BatchAllocations[0].Amount); Assert.Equal("100.00",i.AccountingAllocations[0].Amount); Assert.Equal("50.00/PCS",i.Rate); Assert.Equal("2 PCS",i.ActualQty); Assert.Equal("CGST",i.AccountingAllocations[0].RateDetails[0].DutyHead); }
    [Fact] public void Utf16AndIllegalControlReferenceAreRecoveredWithWarning() { var p=importer.Parse(File.ReadAllBytes(Fixture("04_utf16_invalid_control_ref.xml"))); Assert.Equal("UTF-16LE",p.Encoding); Assert.Single(p.Warnings); Assert.Contains("Recoverable control reference  in",p.Vouchers[0].Narration); }
    [Fact] public void TallyErrorsAreBusinessFailure() { var p=importer.Parse(File.ReadAllBytes(Fixture("03_tally_error_response.xml"))); Assert.Equal("failed",p.TallyResponse!.BusinessStatus); Assert.Equal("1",p.TallyResponse.Counters["ERRORS"]); }
    [Fact] public void EmptyCollectionAndUnknownChildrenAreSafe() { Assert.Empty(importer.Parse(File.ReadAllBytes(Fixture("05_empty_collection.xml"))).Vouchers); var xml=Encoding.UTF8.GetBytes("<ENVELOPE><BODY><DATA><COLLECTION><VOUCHER><CUSTOM>safe</CUSTOM></VOUCHER></COLLECTION></DATA></BODY></ENVELOPE>"); var p=importer.Parse(xml); Assert.Single(p.Vouchers); Assert.Empty(p.Vouchers[0].LedgerEntries); Assert.NotEmpty(p.Warnings); }
    [Fact] public void Utf8BomAndUtf8AreDetected() { Assert.Equal("UTF-8",importer.Parse(File.ReadAllBytes(Fixture("07_utf8_bom_payment.xml"))).Encoding); Assert.Equal("UTF-8",importer.Parse(File.ReadAllBytes(Fixture("01_receipt_with_allocations.xml"))).Encoding); }
    [Fact] public void DtdIsRejected() { var xml=Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><ENVELOPE>&e;</ENVELOPE>"); Assert.Throws<InvalidDataException>(()=>importer.Parse(xml)); }
    [Fact] public async Task InMemoryStorageIsIdempotentForExactBytes() { var raw=File.ReadAllBytes(Fixture("01_receipt_with_allocations.xml"));var store=new ImportStore("InMemory",null);var parsed=importer.Parse(raw);var hash=Convert.ToHexStringLower(SHA256.HashData(raw));var first=await store.SaveAsync(parsed,raw,hash,CancellationToken.None);var second=await store.SaveAsync(parsed,raw,hash,CancellationToken.None);Assert.False(first.Duplicate);Assert.True(second.Duplicate);var id=JsonDocument.Parse(JsonSerializer.Serialize(first.Body)).RootElement.GetProperty("importId").GetString()!;var vouchers=await store.GetVouchersAsync(id,CancellationToken.None);Assert.Equal(1,JsonDocument.Parse(JsonSerializer.Serialize(vouchers)).RootElement.GetProperty("count").GetInt32());}
}
