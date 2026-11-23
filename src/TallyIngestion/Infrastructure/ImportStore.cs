using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using TallyIngestion.Application;
using TallyIngestion.Models;
using TallyIngestion.Models.Vouchers;

namespace TallyIngestion.Infrastructure;

/// <summary>Persists imports, exact raw bytes, and normalized voucher trees in memory or SQL Server.</summary>
public sealed class ImportStore(string provider, string? connectionString) : IImportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, MemoryImport> memoryByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoryImport> memoryById = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim memoryLock = new(1, 1);
    // Provider selection keeps ephemeral local runs separate from durable SQL Server storage.
    private bool IsInMemory => provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase);
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        // Each operation uses its own connection; SQL Server connection pooling reuses the physical connection.
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
    /// <summary>Creates tables and the unique exact-content hash constraint.</summary>
    public async Task InitializeAsync()
    {
        if (IsInMemory) return;
        if (!provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported storage provider '{provider}'.");

        // The unique hash index is the final database-level guard against duplicate uploads.
        const string schema = "IF OBJECT_ID(N'dbo.Imports',N'U') IS NULL CREATE TABLE dbo.Imports(Id nvarchar(32) NOT NULL PRIMARY KEY, Hash char(64) NOT NULL UNIQUE, DocumentType nvarchar(32) NOT NULL, Status nvarchar(16) NOT NULL, Encoding nvarchar(32) NOT NULL, Summary nvarchar(max) NOT NULL, Warnings nvarchar(max) NOT NULL, Errors nvarchar(max) NOT NULL, TallyResponse nvarchar(max) NULL, Raw varbinary(max) NOT NULL); IF OBJECT_ID(N'dbo.Vouchers',N'U') IS NULL CREATE TABLE dbo.Vouchers(ImportId nvarchar(32) NOT NULL REFERENCES dbo.Imports(Id) ON DELETE CASCADE, SourceIndex int NOT NULL, Json nvarchar(max) NOT NULL, CONSTRAINT PK_Vouchers PRIMARY KEY(ImportId,SourceIndex));";
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(schema);
    }
    /// <summary>Saves a parsed import atomically, returning the prior record for identical bytes.</summary>
    public async Task<SaveResult> SaveAsync(ParsedImport p, byte[] raw, string hash, CancellationToken ct)
    {
        if (IsInMemory) return await SaveInMemoryAsync(p, raw, hash, ct);
        await using var c = await OpenAsync(ct);
        // Serializable isolation plus key-range locks makes the check-and-insert safe for simultaneous uploads.
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var existing = await c.QuerySingleOrDefaultAsync<ExistingImport>(new CommandDefinition("SELECT Id,DocumentType,Status,Summary FROM dbo.Imports WITH (UPDLOCK,HOLDLOCK) WHERE Hash=@Hash", new { Hash = hash }, tx, cancellationToken: ct));
        if (existing is not null)
        {
            var duplicateSummary = JsonDocument.Parse(existing.Summary).RootElement.Clone();
            var response = new { importId = existing.Id, documentType = existing.DocumentType, status = existing.Status, duplicate = true, summary = duplicateSummary };
            await tx.CommitAsync(ct);
            return new SaveResult(response, true);
        }
        // Summaries are stored separately so import details never need to read the private raw payload.
        var id = Guid.NewGuid().ToString("N"); var summary = new { vouchers = p.Vouchers.Count, ledgerEntries = p.Vouchers.Sum(v => v.LedgerEntries.Count), inventoryEntries = p.Vouchers.Sum(v => v.InventoryEntries.Count), warnings = p.Warnings.Count + p.Vouchers.Sum(v => v.Warnings.Count), errors = p.Errors.Count };
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO dbo.Imports(Id,Hash,DocumentType,Status,Encoding,Summary,Warnings,Errors,TallyResponse,Raw) VALUES(@Id,@Hash,@Type,'completed',@Encoding,@Summary,@Warnings,@Errors,@Tally,@Raw)", new { Id = id, Hash = hash, Type = p.DocumentType, Encoding = p.Encoding, Summary = JsonSerializer.Serialize(summary), Warnings = JsonSerializer.Serialize(p.Warnings), Errors = JsonSerializer.Serialize(p.Errors), Tally = p.TallyResponse is null ? (object?)null : JsonSerializer.Serialize(p.TallyResponse, JsonOptions), Raw = raw }, tx, cancellationToken: ct));
        // Replace the parser's placeholder source values with stable import identity before persisting each voucher.
        var voucherRows = p.Vouchers.Select((v, i) => { var normalized = v with { Source = new { importId = id, contentHash = hash, voucherIndex = i } }; return new { ImportId = id, SourceIndex = i, Json = JsonSerializer.Serialize(normalized, JsonOptions) }; }).ToArray();
        if (voucherRows.Length > 0) await c.ExecuteAsync(new CommandDefinition("INSERT INTO dbo.Vouchers(ImportId,SourceIndex,Json) VALUES(@ImportId,@SourceIndex,@Json)", voucherRows, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new SaveResult(new { importId = id, documentType = p.DocumentType, status = "completed", duplicate = false, summary }, false);
    }
    private async Task<SaveResult> SaveInMemoryAsync(ParsedImport p, byte[] raw, string hash, CancellationToken ct)
    {
        await memoryLock.WaitAsync(ct);
        try
        {
            // Serialize lookup and insertion under one lock so simultaneous requests cannot duplicate an in-memory import.
            if (memoryByHash.TryGetValue(hash, out var existing))
            {
                var response = new { importId = existing.Id, documentType = existing.Parsed.DocumentType, status = "completed", duplicate = true, summary = existing.Summary };
                return new SaveResult(response, true);
            }

            var id = Guid.NewGuid().ToString("N");
            var summary = new
            {
                vouchers = p.Vouchers.Count,
                ledgerEntries = p.Vouchers.Sum(voucher => voucher.LedgerEntries.Count),
                inventoryEntries = p.Vouchers.Sum(voucher => voucher.InventoryEntries.Count),
                warnings = p.Warnings.Count + p.Vouchers.Sum(voucher => voucher.Warnings.Count),
                errors = p.Errors.Count
            };

            // Store normalized vouchers in source order and replace the placeholder with this import's identity.
            var json = p.Vouchers.Select((v, i) => JsonSerializer.Serialize(v with { Source = new { importId = id, contentHash = hash, voucherIndex = i } }, JsonOptions)).ToList();
            var record = new MemoryImport(id, hash, p, raw, summary, json);
            memoryByHash.Add(hash, record);
            memoryById.Add(id, record);

            return new SaveResult(new { importId = id, documentType = p.DocumentType, status = "completed", duplicate = false, summary }, false);
        }
        finally { memoryLock.Release(); }
    }
    /// <summary>Reads import metadata without exposing the retained raw XML.</summary>
    public async Task<object?> GetDetailsAsync(string id, CancellationToken ct)
    {
        if (IsInMemory)
        {
            await memoryLock.WaitAsync(ct);
            try
            {
                if (!memoryById.TryGetValue(id, out var memoryRecord)) return null;

                // The raw XML is intentionally omitted from the public details response.
                return new
                {
                    importId = memoryRecord.Id,
                    contentHash = memoryRecord.Hash,
                    documentType = memoryRecord.Parsed.DocumentType,
                    status = "completed",
                    detectedEncoding = memoryRecord.Parsed.Encoding,
                    summary = memoryRecord.Summary,
                    warnings = memoryRecord.Parsed.Warnings,
                    errors = memoryRecord.Parsed.Errors,
                    tallyResponse = memoryRecord.Parsed.TallyResponse
                };
            }
            finally { memoryLock.Release(); }
        }

        await using var connection = await OpenAsync(ct);
        var record = await connection.QuerySingleOrDefaultAsync<ImportDetailRow>(new CommandDefinition(
            "SELECT Id,Hash,DocumentType,Status,Encoding,Summary,Warnings,Errors,TallyResponse FROM dbo.Imports WHERE Id=@Id",
            new { Id = id }, cancellationToken: ct));
        if (record is null) return null;

        // JSON columns are decoded as JSON values so clients receive objects and arrays, not escaped JSON text.
        object? tallyResponse = record.TallyResponse is null ? null : JsonDocument.Parse(record.TallyResponse).RootElement.Clone();
        return new
        {
            importId = record.Id,
            contentHash = record.Hash,
            documentType = record.DocumentType,
            status = record.Status,
            detectedEncoding = record.Encoding,
            summary = JsonDocument.Parse(record.Summary).RootElement.Clone(),
            warnings = JsonDocument.Parse(record.Warnings).RootElement.Clone(),
            errors = JsonDocument.Parse(record.Errors).RootElement.Clone(),
            tallyResponse
        };
    }
    /// <summary>Returns normalized vouchers in source order, including an empty result for response imports.</summary>
    public async Task<object?> GetVouchersAsync(string id, CancellationToken ct)
    {
        if (IsInMemory)
        {
            await memoryLock.WaitAsync(ct);
            try
            {
                if (!memoryById.TryGetValue(id, out var record)) return null;
                var memoryItems = record.VoucherJson.Select(json => JsonDocument.Parse(json).RootElement.Clone()).ToList();
                return new { items = memoryItems, count = memoryItems.Count };
            }
            finally { memoryLock.Release(); }
        }

        await using var connection = await OpenAsync(ct);
        var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM dbo.Imports WHERE Id=@Id", new { Id = id }, cancellationToken: ct));
        if (exists is null) return null;

        // Ordering by source index reproduces the order of the original XML voucher collection.
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT Json FROM dbo.Vouchers WHERE ImportId=@Id ORDER BY SourceIndex",
            new { Id = id }, cancellationToken: ct));
        var items = rows.Select(json => JsonDocument.Parse(json).RootElement.Clone()).ToList();
        return new { items, count = items.Count };
    }
    private sealed record MemoryImport(string Id, string Hash, ParsedImport Parsed, byte[] Raw, object Summary, List<string> VoucherJson);
    private sealed class ExistingImport { public string Id { get; set; } = ""; public string DocumentType { get; set; } = ""; public string Status { get; set; } = ""; public string Summary { get; set; } = ""; }
    private sealed class ImportDetailRow { public string Id { get; set; } = ""; public string Hash { get; set; } = ""; public string DocumentType { get; set; } = ""; public string Status { get; set; } = ""; public string Encoding { get; set; } = ""; public string Summary { get; set; } = ""; public string Warnings { get; set; } = ""; public string Errors { get; set; } = ""; public string? TallyResponse { get; set; } }
}
