using TallyIngestion.Models.Vouchers;

namespace TallyIngestion.Models;

/// <summary>Configuration for bounded HTTP uploads.</summary>
public sealed record UploadOptions(long MaxBytes);
/// <summary>Result of parsing a supported Tally XML document.</summary>
public sealed record ParsedImport(string DocumentType, string Encoding, List<string> Warnings, List<string> Errors, List<Voucher> Vouchers, TallyResponse? TallyResponse);
/// <summary>Parsed Tally response counters and business result.</summary>
public sealed record TallyResponse(string BusinessStatus, Dictionary<string, string> Counters, string? LineError);
