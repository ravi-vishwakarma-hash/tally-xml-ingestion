# Tally XML Ingestion

A small ASP.NET Core service that accepts Tally voucher exports and operation responses, retains exact uploaded bytes, and exposes normalized voucher data. The parser reads only direct children for each field and preserves repeated entries, nested allocations, source order, and source financial strings.

## Code layout

```text
src/TallyIngestion/
├── Api/ApiEndpoints.cs                 HTTP routes and request validation
├── Application/IImportStore.cs         Persistence contract
├── Application/XmlImporter.cs          XML decoding and normalization
├── Infrastructure/ImportStore.cs       In-memory and SQL Server persistence
└── Models/
    ├── ImportModels.cs                 Import and Tally response models
    ├── SaveResult.cs                    Persistence result model
    └── Vouchers/VoucherModels.cs        Voucher and nested entry models
```

The startup file only configures dependencies and maps the API. XML parsing, endpoint handling, persistence, and response models live in their own files. `ApiResponseHandler` creates the shared success and Problem Details responses, while `GlobalExceptionHandler` logs uncaught server exceptions and returns a generic problem with a trace ID. Comments explain the important validation, ownership, security, and idempotency decisions; routine syntax is left readable without repetitive line-by-line narration.

## Run locally

Requires the .NET 11 SDK. In-memory storage is the default for local development:

```powershell
dotnet run --project src/TallyIngestion
```

The default storage provider is `InMemory`; data is cleared when the process stops. The upload limit defaults to 10 MiB and is configurable using `MaxUploadBytes`.

To use SQL Server, create the `TallyIngestion` database, set the connection string, and select the provider:

```powershell
$env:Storage__Provider = "SqlServer"
$env:ConnectionStrings__Tally = "Server=localhost;Database=TallyIngestion;Integrated Security=True;TrustServerCertificate=True"
dotnet run --project src/TallyIngestion
```

On startup the service creates its `dbo.Imports` and `dbo.Vouchers` tables if absent. Use SQL Server authentication if Windows authentication does not fit your environment; keep credentials in user secrets or a secret manager. The SQL Server mode retains the exact raw uploads and atomically enforces a unique SHA-256 hash. It requires SQL Server and is the mode to use when imports must survive restarts.

Run tests with `dotnet test tests/TallyIngestion.Tests`.

## OpenAPI and Scalar docs

After starting the service, open the Scalar API reference at `http://localhost:5000/docs`. The generated OpenAPI JSON is at `http://localhost:5000/openapi/v1.json`. The document describes the multipart `file` field, success and error status codes, import details, normalized voucher routes, and health check.

## API

```powershell
curl.exe -F "file=@requirement/fixtures/01_receipt_with_allocations.xml" http://localhost:5000/api/tally/imports
curl.exe http://localhost:5000/api/tally/imports/{importId}
curl.exe http://localhost:5000/api/tally/imports/{importId}/vouchers
curl.exe http://localhost:5000/health
```

Upload returns 201 for a new import and 200 with `duplicate: true` for exact bytes already stored. `ENVELOPE` voucher exports and `RESPONSE` operation results are supported. Business failure reported by Tally does not change successful document processing status. Invalid input produces a structured 4xx response.

API errors use the standard `application/problem+json` Problem Details shape (`type`, `title`, `status`, `detail`, and `instance`). The `code` extension provides a stable machine-readable application error such as `invalid_xml` or `import_not_found`.

## Data and design

In-memory mode stores imports for the lifetime of the process and is intended only for quick local runs. SQL Server stores import metadata, exact uploaded bytes, and ordered normalized voucher JSON through Dapper and parameterized SQL. Voucher JSON captures each nested allocation under its source parent; the composite voucher key retains order. A unique SHA-256 constraint and serializable transaction with a key-range lock make duplicate detection and insert atomic. Keeping normalized nested objects as JSON avoids a large collection of mostly mechanical relational tables while preserving the inspectable relationships required by the contract. Raw XML is never returned from API endpoints.

The XML reader prohibits DTDs and external resolution, limits parsed document characters, and never executes source content. UTF-8, UTF-8 BOM, and UTF-16 BOM are supported. Illegal XML 1.0 numeric control references are removed before parsing and reported as an import warning; the raw upload is retained unchanged. Unknown voucher children are retained in the raw source and reported as warnings.

For production, raw bytes should move to encrypted object storage with narrow access controls and documented retention/cleanup. Add request rate limits, authentication, metrics, structured operational logging that excludes XML, and versioned SQL Server migrations.

## 500 MB production ingestion

Stream the request to a temporary encrypted object while incrementally hashing exact bytes. After upload completion and hash lookup, parse with a forward-only XML reader using DTD prohibition and strict byte/character limits. Buffer only one voucher subtree at a time, normalize it, and write voucher plus children in bounded database batches. Keep a unique import hash and a durable checkpoint containing source byte offset or voucher ordinal and parser-safe resume boundary; make each batch idempotent by import ID and source ordinal. Persist the raw object reference before processing, then mark the import complete only after all batches commit. Apply bounded queues, upload backpressure, cancellation, request/processing timeouts, temporary object expiry, and cleanup jobs. Restart from the last committed voucher boundary and recheck the unique hash before resuming.

## Scope and delivery notes

Required API, XML ownership, exact-byte idempotency in SQL Server mode, encoding recovery, Tally response result, and core parser fixture tests are implemented. The default in-memory mode is intentionally ephemeral and does not meet durable persistence requirements; select SQL Server for persistence. Optional per-voucher partial failure, streaming ingestion, concurrent-upload stress proof, rich unknown-subtree persistence, and alternate response envelope normalization are not implemented. The main production risk is bounded-memory processing; this version buffers uploads in memory within its configurable size cap.

Approximate active time: record actual elapsed implementation time before submission. AI tools used: OpenAI Codex assisted with repository reading, implementation, and test authoring; review and verification should be completed by the candidate.
