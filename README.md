# Tally XML Ingestion

A small ASP.NET Core service that accepts Tally voucher exports and operation responses, retains exact uploaded bytes, and exposes normalized voucher data. The parser reads only direct children for each field and preserves repeated entries, nested allocations, source order, and source financial strings.

## Code layout

```text
src/TallyIngestion/
├── appsettings.json                   Local defaults; contains no connection credentials
├── Api/ApiEndpoints.cs                 HTTP routes and request validation
├── Api/SupportedFileTypeDetector.cs    Checks XML content before document parsing
├── Application/IImportStore.cs         Persistence contract
├── Application/XmlImporter.cs          XML decoding and normalization
├── Infrastructure/ImportStore.cs       In-memory and SQL Server persistence
└── Models/
    ├── ImportModels.cs                 Import and Tally response models
    ├── SaveResult.cs                    Persistence result model
    └── Vouchers/VoucherModels.cs        Voucher and nested entry models
```

The startup file only configures dependencies and maps the API. XML parsing, endpoint handling, persistence, and response models live in their own files. `ApiResponseHandler` creates the shared success and Problem Details responses, while `GlobalExceptionHandler` logs uncaught server exceptions and returns a generic problem with a trace ID. Comments explain the important validation, ownership, security, and idempotency decisions; routine syntax is left readable without repetitive line-by-line narration.

## Package management

NuGet package versions are centralized in `Directory.Packages.props`; project files declare package names without repeating version numbers. Update package versions in that file so the service and tests use one consistent dependency set.

## Run locally

Requires the .NET 10 SDK. In-memory storage is the default for local development:

```powershell
dotnet build TallyIngestion.sln
dotnet run --project src/TallyIngestion
```

The default storage provider is `InMemory`; data is cleared when the process stops. The upload limit defaults to 10 MiB and is configurable using `MaxUploadBytes`.

### SQL Server with .NET User Secrets

Create the `TallyIngestion` database, then store the provider and connection string in .NET User Secrets. The project already has a `UserSecretsId`, and its launch profile sets `ASPNETCORE_ENVIRONMENT=Development`; ASP.NET Core loads these local secrets automatically when the app starts. No secret values need to be placed in `appsettings.json` or committed to Git.

```powershell
dotnet user-secrets set "Storage:Provider" "SqlServer" --project src/TallyIngestion
dotnet user-secrets set "ConnectionStrings:Tally" "Server=localhost;Database=TallyIngestion;Integrated Security=True;TrustServerCertificate=True" --project src/TallyIngestion
dotnet run --project src/TallyIngestion
```

For SQL Server authentication, set `ConnectionStrings:Tally` to your server, database, user ID, and password with the same `dotnet user-secrets set` command; the secret stays outside the repository. User Secrets are for local development only. Configure deployment credentials through the environment or a managed secret store. On startup the service creates its `dbo.Imports` and `dbo.Vouchers` tables if absent. SQL Server mode retains exact raw uploads and atomically enforces a unique SHA-256 hash; it is required when imports must survive restarts.

### SQL schema setup and upgrades

Create the `TallyIngestion` database first, then use the User Secrets commands above and start the service with `dotnet run --project src/TallyIngestion`. Startup performs an idempotent schema bootstrap: `dbo.Imports` stores import metadata, diagnostics, Tally response JSON, the unique SHA-256 hash, and the exact raw bytes; `dbo.Vouchers` stores ordered normalized voucher JSON with a foreign key to its import. Nested arrays stay inside the voucher JSON so their parent ownership and source order remain inspectable. There is no separate migration command in this take-home version. For a schema change after deployment, apply a versioned SQL migration before starting the updated service; the automatic bootstrap only creates missing tables and does not alter existing ones.

Open `TallyIngestion.sln` in Visual Studio or another .NET IDE to work with both projects. Run tests with `dotnet test TallyIngestion.sln`.

## OpenAPI and Scalar docs

After starting the service, open the Scalar API reference at `http://localhost:5000/docs`. The generated OpenAPI JSON is at `http://localhost:5000/openapi/v1.json`. The document describes the multipart `file` field, success and error status codes, import details, normalized voucher routes, and health check.

### Postman

Import [Tally-XML-Ingestion.postman_collection.json](postman/Tally-XML-Ingestion.postman_collection.json) into Postman. The collection is grouped by Health and Imports and includes saved examples for successful uploads, duplicate uploads, normalized results, and common errors. It does not contain credentials; this API currently has no authentication requirement.

Set the collection variable `baseUrl` to the service URL if it differs from `http://localhost:5000`. In **Upload Tally XML**, choose the sanitized `requirement/fixtures/01_receipt_with_allocations.xml` file for the `file` form-data field (Postman may ask you to select the local file after import). The upload test stores the returned `importId` in a collection variable, which the two read requests use automatically. To try the Tally business failure case, upload `requirement/fixtures/03_tally_error_response.xml`; the HTTP import still completes successfully and its business result appears under `tallyResponse`.

The API uses JSON responses with `Accept: application/json`; errors use `application/problem+json` and include a stable `code` extension. Upload requests must be `multipart/form-data` with a non-empty `file` field. There are no query parameters or authentication headers. The only path parameter is `importId` on the two import read routes. No secrets or production data are included in the examples.

## API

```powershell
curl.exe -F "file=@requirement/fixtures/01_receipt_with_allocations.xml" http://localhost:5000/api/tally/imports
curl.exe http://localhost:5000/api/tally/imports/{importId}
curl.exe http://localhost:5000/api/tally/imports/{importId}/vouchers
curl.exe http://localhost:5000/health
```

Upload returns 201 for a new import and 200 with `duplicate: true` for exact bytes already stored. `ENVELOPE` voucher exports and `RESPONSE` operation results are supported. Business failure reported by Tally does not change successful document processing status. Invalid input produces a structured 4xx response.

Only files with a `.xml` filename extension and supported Tally XML content are imported. The extension is checked case-insensitively, and the content is validated independently; the filename alone and the MIME type are not trusted. PDF, legacy Word `.doc`, Word Open XML `.docx`, plain text, and other non-XML content return `415` with code `unsupported_file_type`. Malformed XML or XML that does not use a supported document shape returns `400` with code `invalid_xml`.

API errors use the standard `application/problem+json` Problem Details shape (`type`, `title`, `status`, `detail`, and `instance`). The `code` extension provides a stable machine-readable application error such as `invalid_xml`, `unsupported_file_type`, or `import_not_found`.

## Data and design

In-memory mode stores imports for the lifetime of the process and is intended only for quick local runs. SQL Server stores import metadata, exact uploaded bytes, and ordered normalized voucher JSON through Dapper and parameterized SQL. Voucher JSON captures each nested allocation under its source parent; the composite voucher key retains order. A unique SHA-256 constraint and serializable transaction with a key-range lock make duplicate detection and insert atomic. Keeping normalized nested objects as JSON avoids a large collection of mostly mechanical relational tables while preserving the inspectable relationships required by the contract. Raw XML is never returned from API endpoints.

The XML reader prohibits DTDs and external resolution, limits parsed document characters, and never executes source content. UTF-8, UTF-8 BOM, and UTF-16 BOM are supported. Illegal XML 1.0 numeric control references are removed before parsing and reported as an import warning; the raw upload is retained unchanged. Unknown voucher children are retained in the raw source and reported as warnings.

For production, raw bytes should move to encrypted object storage with narrow access controls and documented retention/cleanup. Add request rate limits, authentication, metrics, structured operational logging that excludes XML, and versioned SQL Server migrations.

## 500 MB production ingestion

Stream the request to a temporary encrypted object while incrementally hashing exact bytes. After upload completion and hash lookup, parse with a forward-only XML reader using DTD prohibition and strict byte/character limits. Buffer only one voucher subtree at a time, normalize it, and write voucher plus children in bounded database batches. Keep a unique import hash and a durable checkpoint containing source byte offset or voucher ordinal and parser-safe resume boundary; make each batch idempotent by import ID and source ordinal. Persist the raw object reference before processing, then mark the import complete only after all batches commit. Apply bounded queues, upload backpressure, cancellation, request/processing timeouts, temporary object expiry, and cleanup jobs. Restart from the last committed voucher boundary and recheck the unique hash before resuming.

## Scope and delivery notes

The required API, XML ownership, exact-byte idempotency in SQL Server mode, encoding recovery, Tally response result, unknown-node warnings, and core fixture tests are implemented. The requested in-memory default is intentionally ephemeral; select SQL Server to meet the assignment's durable persistence requirement. Optional extensions attempted: none. Per-voucher partial failure, streaming ingestion, a concurrent-upload stress test, richer unknown-subtree persistence, and alternate response-envelope normalization remain unimplemented. The main production risk is bounded-memory processing; this version buffers uploads in memory within its configurable size cap. SQL Server schema and transaction behavior are implemented but were not exercised against a live SQL Server during this verification.

Approximate active time: under 3 hours (candidate estimate). AI use: OpenAI Codex was used to read the requirements and code, identify and implement fixes, update documentation, and author behavioral tests. The requirements and fixtures were reread; parser and persistence code were reviewed against them; the .NET build and test suite were run. The candidate should review the final changes and be ready to explain the implementation and tradeoffs in the follow-up interview.
