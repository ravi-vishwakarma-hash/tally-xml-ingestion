using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using System.Data;
using Dapper;

var builder = WebApplication.CreateBuilder(args);
var maxUploadBytes = builder.Configuration.GetValue<long>("MaxUploadBytes", 10 * 1024 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Tally");
var storageProvider = builder.Configuration["Storage:Provider"] ?? (string.IsNullOrWhiteSpace(connectionString) ? "InMemory" : "SqlServer");
if (storageProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configure ConnectionStrings:Tally when Storage:Provider is SqlServer.");
builder.Services.AddSingleton(new ImportStore(storageProvider, connectionString));
builder.Services.AddSingleton<XmlImporter>();
builder.Services.AddSingleton(new UploadOptions(maxUploadBytes));
var app = builder.Build();
await app.Services.GetRequiredService<ImportStore>().InitializeAsync();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapPost("/api/tally/imports", async (HttpRequest request, UploadOptions options, XmlImporter importer, ImportStore store, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return ApiError(400, "invalid_content_type", "Expected multipart/form-data with a file field.");
    IFormCollection form;
    try { form = await request.ReadFormAsync(ct); }
    catch (InvalidDataException) { return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit."); }
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return ApiError(400, "file_required", "A non-empty multipart file field named 'file' is required.");
    if (file.Length > options.MaxBytes) return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.");
    await using var input = file.OpenReadStream();
    using var buffer = new MemoryStream((int)Math.Min(file.Length, int.MaxValue));
    await input.CopyToAsync(buffer, ct);
    if (buffer.Length > options.MaxBytes) return ApiError(413, "upload_too_large", "Upload exceeds the configured size limit.");
    var bytes = buffer.ToArray();
    try
    {
        var parsed = importer.Parse(bytes);
        var result = await store.SaveAsync(parsed, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), ct);
        return Results.Json(result.Body, statusCode: result.Duplicate ? 200 : 201);
    }
    catch (InvalidDataException ex) { return ApiError(400, "invalid_xml", ex.Message); }
});
app.MapGet("/api/tally/imports/{id}", async (string id, ImportStore store, CancellationToken ct) =>
    await store.GetDetailsAsync(id, ct) is { } value ? Results.Json(value) : ApiError(404, "import_not_found", "The requested import was not found."));
app.MapGet("/api/tally/imports/{id}/vouchers", async (string id, ImportStore store, CancellationToken ct) =>
    await store.GetVouchersAsync(id, ct) is { } value ? Results.Json(value) : ApiError(404, "import_not_found", "The requested import was not found."));
app.Run();

static IResult ApiError(int status, string code, string message) => Results.Json(new { error = new { code, message } }, statusCode: status);

/// <summary>Configuration for bounded HTTP uploads.</summary>
public sealed record UploadOptions(long MaxBytes);
/// <summary>Result of parsing a supported Tally XML document.</summary>
public sealed record ParsedImport(string DocumentType, string Encoding, List<string> Warnings, List<string> Errors, List<Voucher> Vouchers, TallyResponse? TallyResponse);
/// <summary>Parsed Tally response counters and business result.</summary>
public sealed record TallyResponse(string BusinessStatus, Dictionary<string, string> Counters, string? LineError);
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

/// <summary>Decodes and normalizes the contract-supported Tally XML structures.</summary>
public sealed class XmlImporter
{
    private static readonly Regex InvalidControlReference = new("&#(?:x0*[1-8bcef]|0*[1-8]|x0*1[0-9a-f]|0*(?:1[1-9]|2[0-9]|3[01]));", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> VoucherChildren = new(StringComparer.OrdinalIgnoreCase) { "DATE", "VOUCHERTYPENAME", "VOUCHERNUMBER", "PARTYLEDGERNAME", "NARRATION", "ALLLEDGERENTRIES.LIST", "LEDGERENTRIES.LIST", "ALLINVENTORYENTRIES.LIST" };
    /// <summary>Parses exact bytes, applying only safe recovery for illegal numeric XML control references.</summary>
    /// <exception cref="InvalidDataException">The bytes are not supported XML or use an unsupported root.</exception>
    public ParsedImport Parse(byte[] bytes)
    {
        if (bytes.Length == 0) throw new InvalidDataException("The uploaded file is empty.");
        var encoding = DetectEncoding(bytes, out var offset);
        string xml;
        try { xml = encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The uploaded file uses an invalid character sequence."); }
        var sanitized = InvalidControlReference.IsMatch(xml);
        if (sanitized) xml = InvalidControlReference.Replace(xml, "");
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50_000_000, IgnoreComments = true });
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("The XML document has no root element.");
            var warnings = new List<string>();
            if (sanitized) warnings.Add("Illegal XML 1.0 numeric control references were removed during parsing; original bytes are retained.");
            if (root.Name.LocalName.Equals("RESPONSE", StringComparison.OrdinalIgnoreCase))
            {
                var names = new[] { "CREATED", "ALTERED", "DELETED", "IGNORED", "ERRORS" };
                var counters = names.Where(n => Child(root, n) is not null).ToDictionary(n => n, n => Child(root, n)!, StringComparer.Ordinal);
                var lineError = Child(root, "LINEERROR");
                var tally = new TallyResponse((int.TryParse(counters.GetValueOrDefault("ERRORS"), out var errors) && errors > 0) || !string.IsNullOrWhiteSpace(lineError) ? "failed" : "succeeded", counters, lineError);
                return new("tallyResponse", EncodingName(encoding), warnings, [], [], tally);
            }
            if (!root.Name.LocalName.Equals("ENVELOPE", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported XML root. Expected ENVELOPE or RESPONSE.");
            var collection = root.Elements().FirstOrDefault(e => e.Name.LocalName == "BODY")?.Elements().FirstOrDefault(e => e.Name.LocalName == "DATA")?.Elements().FirstOrDefault(e => e.Name.LocalName == "COLLECTION");
            var vouchers = new List<Voucher>();
            if (collection is not null)
            {
                foreach (var node in collection.Elements().Where(e => e.Name.LocalName == "VOUCHER"))
                {
                    var index = vouchers.Count;
                    var unknown = node.Elements().Any(e => !VoucherChildren.Contains(e.Name.LocalName));
                    var vw = unknown ? new List<string> { "Unknown voucher child nodes were retained in the raw source but not normalized." } : [];
                    vouchers.Add(new(new { voucherIndex = index }, new { VCHTYPE = Attr(node, "VCHTYPE"), ACTION = Attr(node, "ACTION") }, Child(node,"DATE"), Child(node,"VOUCHERTYPENAME"), Child(node,"VOUCHERNUMBER"), Child(node,"PARTYLEDGERNAME"), Child(node,"NARRATION"),
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
    private static LedgerEntry ParseLedger(XElement e) => new(e.Name.LocalName, Child(e,"LEDGERNAME"), Child(e,"ISDEEMEDPOSITIVE"), Child(e,"AMOUNT"), e.Elements().Where(x=>x.Name.LocalName=="BILLALLOCATIONS.LIST").Select(x=>new BillAllocation(Child(x,"NAME"),Child(x,"BILLTYPE"),Child(x,"AMOUNT"))).ToList(), e.Elements().Where(x=>x.Name.LocalName=="BANKALLOCATIONS.LIST").Select(x=>new BankAllocation(Child(x,"DATE"),Child(x,"NAME"),Child(x,"TRANSACTIONTYPE"),Child(x,"AMOUNT"))).ToList(), Rates(e));
    private static InventoryEntry ParseInventory(XElement e) => new(e.Name.LocalName,Child(e,"STOCKITEMNAME"),Child(e,"ISDEEMEDPOSITIVE"),Child(e,"ACTUALQTY"),Child(e,"BILLEDQTY"),Child(e,"RATE"),Child(e,"AMOUNT"),e.Elements().Where(x=>x.Name.LocalName=="BATCHALLOCATIONS.LIST").Select(x=>new BatchAllocation(Child(x,"GODOWNNAME"),Child(x,"BATCHNAME"),Child(x,"ACTUALQTY"),Child(x,"BILLEDQTY"),Child(x,"AMOUNT"))).ToList(),e.Elements().Where(x=>x.Name.LocalName=="ACCOUNTINGALLOCATIONS.LIST").Select(x=>new AccountingAllocation(Child(x,"LEDGERNAME"),Child(x,"ISDEEMEDPOSITIVE"),Child(x,"AMOUNT"),Rates(x))).ToList(),Rates(e));
    private static List<RateDetail> Rates(XElement e) => e.Elements().Where(x=>x.Name.LocalName=="RATEDETAILS.LIST").Select(x=>new RateDetail(Child(x,"GSTRATEDUTYHEAD"),Child(x,"GSTRATE"))).ToList();
    private static string? Child(XElement e,string name) => e.Elements().FirstOrDefault(x=>x.Name.LocalName==name)?.Value;
    private static string? Attr(XElement e,string name) => e.Attributes().FirstOrDefault(x=>x.Name.LocalName==name)?.Value;
    private static Encoding DetectEncoding(byte[] b,out int offset) { offset=0; if(b.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf})){offset=3;return new UTF8Encoding(false,true);} if(b.AsSpan().StartsWith(new byte[]{0xff,0xfe})){offset=2;return new UnicodeEncoding(false,false,true);} if(b.AsSpan().StartsWith(new byte[]{0xfe,0xff})){offset=2;return new UnicodeEncoding(true,false,true);} return new UTF8Encoding(false,true); }
    private static string EncodingName(Encoding e) => e.CodePage switch { 65001=>"UTF-8",1200=>"UTF-16LE",1201=>"UTF-16BE",_=>e.WebName };
}

/// <summary>Persists imports, exact raw bytes, and normalized voucher trees in SQLite.</summary>
public sealed class ImportStore(string provider, string? connectionString)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, MemoryImport> memoryByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoryImport> memoryById = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim memoryLock = new(1, 1);
    private bool IsInMemory => provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase);
    private SqlConnection Open() { var c=new SqlConnection(connectionString); c.Open(); return c; }
    /// <summary>Creates tables and the unique exact-content hash constraint.</summary>
    public async Task InitializeAsync() { if (IsInMemory) return; if (!provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Unsupported storage provider '{provider}'."); await using var c=Open(); await c.ExecuteAsync("IF OBJECT_ID(N'dbo.Imports',N'U') IS NULL BEGIN CREATE TABLE dbo.Imports(Id nvarchar(32) NOT NULL PRIMARY KEY, Hash char(64) NOT NULL UNIQUE, DocumentType nvarchar(32) NOT NULL, Status nvarchar(16) NOT NULL, Encoding nvarchar(32) NOT NULL, Summary nvarchar(max) NOT NULL, Warnings nvarchar(max) NOT NULL, Errors nvarchar(max) NOT NULL, TallyResponse nvarchar(max) NULL, Raw varbinary(max) NOT NULL); CREATE TABLE dbo.Vouchers(ImportId nvarchar(32) NOT NULL REFERENCES dbo.Imports(Id) ON DELETE CASCADE, SourceIndex int NOT NULL, Json nvarchar(max) NOT NULL, CONSTRAINT PK_Vouchers PRIMARY KEY(ImportId,SourceIndex)); END"); }
    /// <summary>Saves a parsed import atomically, returning the prior record for identical bytes.</summary>
    public async Task<SaveResult> SaveAsync(ParsedImport p,byte[] raw,string hash,CancellationToken ct)
    {
        if (IsInMemory) return await SaveInMemoryAsync(p, raw, hash, ct);
        await using var c=Open(); await using var tx=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var existing=await c.QuerySingleOrDefaultAsync<ExistingImport>(new CommandDefinition("SELECT Id,DocumentType,Status,Summary FROM dbo.Imports WITH (UPDLOCK,HOLDLOCK) WHERE Hash=@Hash",new { Hash=hash },tx,cancellationToken:ct));
        if(existing is not null){var body=JsonDocument.Parse(existing.Summary).RootElement.Clone();var result=new { importId=existing.Id,documentType=existing.DocumentType,status=existing.Status,duplicate=true,summary=body };await tx.CommitAsync(ct);return new(result,true);}
        var id=Guid.NewGuid().ToString("N"); var summary=new { vouchers=p.Vouchers.Count,ledgerEntries=p.Vouchers.Sum(v=>v.LedgerEntries.Count),inventoryEntries=p.Vouchers.Sum(v=>v.InventoryEntries.Count),warnings=p.Warnings.Count+p.Vouchers.Sum(v=>v.Warnings.Count),errors=p.Errors.Count };
        await c.ExecuteAsync(new CommandDefinition("INSERT INTO dbo.Imports(Id,Hash,DocumentType,Status,Encoding,Summary,Warnings,Errors,TallyResponse,Raw) VALUES(@Id,@Hash,@Type,'completed',@Encoding,@Summary,@Warnings,@Errors,@Tally,@Raw)",new { Id=id,Hash=hash,Type=p.DocumentType,Encoding=p.Encoding,Summary=JsonSerializer.Serialize(summary),Warnings=JsonSerializer.Serialize(p.Warnings),Errors=JsonSerializer.Serialize(p.Errors),Tally=p.TallyResponse is null?(object?)null:JsonSerializer.Serialize(p.TallyResponse),Raw=raw },tx,cancellationToken:ct));
        var voucherRows=p.Vouchers.Select((v,i)=>{var normalized=v with { Source=new { importId=id,contentHash=hash,voucherIndex=i } };return new { ImportId=id,SourceIndex=i,Json=JsonSerializer.Serialize(normalized,JsonOptions) };}).ToArray();
        if(voucherRows.Length>0) await c.ExecuteAsync(new CommandDefinition("INSERT INTO dbo.Vouchers(ImportId,SourceIndex,Json) VALUES(@ImportId,@SourceIndex,@Json)",voucherRows,tx,cancellationToken:ct));
        await tx.CommitAsync(ct);return new(new { importId=id,documentType=p.DocumentType,status="completed",duplicate=false,summary },false);
    }
    private async Task<SaveResult> SaveInMemoryAsync(ParsedImport p, byte[] raw, string hash, CancellationToken ct)
    {
        await memoryLock.WaitAsync(ct);
        try
        {
            if (memoryByHash.TryGetValue(hash, out var existing)) return new(new { importId=existing.Id,documentType=existing.Parsed.DocumentType,status="completed",duplicate=true,summary=existing.Summary },true);
            var id=Guid.NewGuid().ToString("N"); var summary=new { vouchers=p.Vouchers.Count,ledgerEntries=p.Vouchers.Sum(v=>v.LedgerEntries.Count),inventoryEntries=p.Vouchers.Sum(v=>v.InventoryEntries.Count),warnings=p.Warnings.Count+p.Vouchers.Sum(v=>v.Warnings.Count),errors=p.Errors.Count };
            var json=p.Vouchers.Select((v,i)=>JsonSerializer.Serialize(v with { Source=new { importId=id,contentHash=hash,voucherIndex=i } },JsonOptions)).ToList();
            var record=new MemoryImport(id,hash,p,raw,summary,json); memoryByHash.Add(hash,record); memoryById.Add(id,record);
            return new(new { importId=id,documentType=p.DocumentType,status="completed",duplicate=false,summary },false);
        }
        finally { memoryLock.Release(); }
    }
    /// <summary>Reads import metadata without exposing the retained raw XML.</summary>
    public async Task<object?> GetDetailsAsync(string id,CancellationToken ct){if(IsInMemory){await memoryLock.WaitAsync(ct);try{if(!memoryById.TryGetValue(id,out var m))return null;return new {importId=m.Id,contentHash=m.Hash,documentType=m.Parsed.DocumentType,status="completed",detectedEncoding=m.Parsed.Encoding,summary=m.Summary,warnings=m.Parsed.Warnings,errors=m.Parsed.Errors,tallyResponse=m.Parsed.TallyResponse};}finally{memoryLock.Release();}} await using var c=Open();var r=await c.QuerySingleOrDefaultAsync<ImportDetailRow>(new CommandDefinition("SELECT Id,Hash,DocumentType,Status,Encoding,Summary,Warnings,Errors,TallyResponse FROM dbo.Imports WHERE Id=@Id",new { Id=id },cancellationToken:ct));if(r is null)return null;object? tally=r.TallyResponse is null?null:JsonDocument.Parse(r.TallyResponse).RootElement.Clone();return new {importId=r.Id,contentHash=r.Hash,documentType=r.DocumentType,status=r.Status,detectedEncoding=r.Encoding,summary=JsonDocument.Parse(r.Summary).RootElement.Clone(),warnings=JsonDocument.Parse(r.Warnings).RootElement.Clone(),errors=JsonDocument.Parse(r.Errors).RootElement.Clone(),tallyResponse=tally};}
    /// <summary>Returns normalized vouchers in source order, including an empty result for response imports.</summary>
    public async Task<object?> GetVouchersAsync(string id,CancellationToken ct){if(IsInMemory){await memoryLock.WaitAsync(ct);try{if(!memoryById.TryGetValue(id,out var m))return null;return new {items=m.VoucherJson.Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToList(),count=m.VoucherJson.Count};}finally{memoryLock.Release();}} await using var c=Open();if(await c.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT 1 FROM dbo.Imports WHERE Id=@Id",new { Id=id },cancellationToken:ct)) is null)return null;var rows=await c.QueryAsync<string>(new CommandDefinition("SELECT Json FROM dbo.Vouchers WHERE ImportId=@Id ORDER BY SourceIndex",new { Id=id },cancellationToken:ct));var items=rows.Select(s=>JsonDocument.Parse(s).RootElement.Clone()).ToList();return new {items,count=items.Count};}
    private sealed record MemoryImport(string Id,string Hash,ParsedImport Parsed,byte[] Raw,object Summary,List<string> VoucherJson);
    private sealed class ExistingImport { public string Id {get;set;}=""; public string DocumentType {get;set;}=""; public string Status {get;set;}=""; public string Summary {get;set;}=""; }
    private sealed class ImportDetailRow { public string Id {get;set;}="";public string Hash {get;set;}="";public string DocumentType {get;set;}="";public string Status {get;set;}="";public string Encoding {get;set;}="";public string Summary {get;set;}="";public string Warnings {get;set;}="";public string Errors {get;set;}="";public string? TallyResponse {get;set;} }
}
/// <summary>Upload persistence result and API representation.</summary>
public sealed record SaveResult(object Body,bool Duplicate);
