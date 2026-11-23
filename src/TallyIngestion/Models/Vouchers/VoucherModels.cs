namespace TallyIngestion.Models.Vouchers;

/// <summary>Source-ordered normalized voucher and its nested entries.</summary>
public sealed record Voucher(object Source, object Attributes, string? Date, string? VoucherType, string? VoucherNumber, string? PartyLedgerName, string? Narration,
    List<LedgerEntry> LedgerEntries, List<InventoryEntry> InventoryEntries, List<string> Warnings);
/// <summary>Normalized ledger entry with its own allocations and rates.</summary>
public sealed record LedgerEntry(string SourceTag, string? LedgerName, string? IsDeemedPositive, string? Amount, List<BillAllocation> BillAllocations, List<BankAllocation> BankAllocations, List<RateDetail> RateDetails);
/// <summary>Normalized bill allocation.</summary>
public sealed record BillAllocation(string? Name, string? BillType, string? Amount);
/// <summary>Normalized bank allocation.</summary>
public sealed record BankAllocation(string? Date, string? Name, string? TransactionType, string? Amount);
/// <summary>Normalized inventory entry.</summary>
public sealed record InventoryEntry(string SourceTag, string? StockItemName, string? IsDeemedPositive, string? ActualQty, string? BilledQty, string? Rate, string? Amount, List<BatchAllocation> BatchAllocations, List<AccountingAllocation> AccountingAllocations, List<RateDetail> RateDetails);
/// <summary>Normalized inventory batch allocation.</summary>
public sealed record BatchAllocation(string? GodownName, string? BatchName, string? ActualQty, string? BilledQty, string? Amount);
/// <summary>Normalized accounting allocation.</summary>
public sealed record AccountingAllocation(string? LedgerName, string? IsDeemedPositive, string? Amount, List<RateDetail> RateDetails);
/// <summary>GST rate information attached to its source owner.</summary>
public sealed record RateDetail(string? DutyHead, string? Rate);

