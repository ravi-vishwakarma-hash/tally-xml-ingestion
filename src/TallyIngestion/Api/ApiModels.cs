using TallyIngestion.Models;
using TallyIngestion.Models.Vouchers;

namespace TallyIngestion.Api;

/// <summary>Counts the normalized records and diagnostics produced by an import.</summary>
public sealed record ImportSummary(int Vouchers, int LedgerEntries, int InventoryEntries, int Warnings, int Errors);

/// <summary>Response returned after a new or duplicate XML upload.</summary>
public sealed record UploadImportResponse(string ImportId, string DocumentType, string Status, bool Duplicate, ImportSummary Summary);

/// <summary>Safe import metadata. The original XML bytes are never included.</summary>
public sealed record ImportDetailsResponse(
    string ImportId, string ContentHash, string DocumentType, string Status, string DetectedEncoding,
    ImportSummary Summary, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, TallyResponse? TallyResponse);

/// <summary>Ordered normalized vouchers belonging to an import.</summary>
public sealed record VoucherListResponse(IReadOnlyList<Voucher> Items, int Count);
