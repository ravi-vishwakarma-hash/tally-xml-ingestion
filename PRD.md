# Product Requirements Document: Reliable Tally XML Ingestion

**Status:** Draft derived from the supplied take-home requirements  
**Audience:** Backend engineering, QA, reviewers  
**Product:** A local backend service that accepts Tally XML documents, stores imports and normalized vouchers, and exposes them through a small HTTP API.

## 1. Summary

Build a reliable ingestion service for Tally XML. The service must decode supported encodings, parse voucher exports without losing nested parent-child relationships or source order, persist source documents and normalized data, and report both processing status and Tally business outcome. Identical uploaded bytes must resolve to the same import, including concurrent requests.

The primary success measure is faithful, inspectable ingestion of the required document shapes. Broad Tally compatibility, streaming, and optional partial-voucher processing are outside the required core.

## 2. Problem and user need

Tally exports contain repeated, nested XML structures where common names such as `AMOUNT`, `NAME`, and `DATE` occur under different owners. Flattening by tag name can silently overwrite financial data or detach allocations from their ledger, inventory, or accounting-allocation parent. XML may also use BOM-marked encodings, contain recoverable illegal numeric control references, or represent a failed business operation despite a successful HTTP exchange.

Backend consumers and operators need a dependable import record, normalized voucher API, duplicate protection, and enough diagnostics to investigate data handling without exposing raw accounting XML in API responses.

## 3. Goals

- Accept and persist voucher exports and Tally operation responses.
- Preserve ownership, repeated rows, and source order for all contract-supported structures.
- Preserve money, quantities, rates, and source boolean-like text losslessly.
- Identify content by SHA-256 of exact uploaded bytes and enforce atomic idempotency.
- Distinguish HTTP/document acceptance from Tally business success or failure.
- Handle the specified encodings, empty collections, unknown children, and recoverable control references safely.
- Provide a reproducible local service, database setup, API examples, and automated behavioral tests.

## 4. Non-goals

- Full Tally XML or report-format compatibility beyond the specified paths and roots.
- Authentication, authorization, deployment infrastructure, or unrelated UI.
- Streaming 500 MB files in the core implementation (a production design note is required).
- Optional per-voucher partial failure, alternate response envelopes, or richer unknown-subtree normalization unless explicitly implemented as extensions.
- Interpreting accounting semantics or recalculating/normalizing financial values.

## 5. Users and primary workflows

1. **Integrating client:** uploads a file using multipart field `file`, receives an import ID and summary, then requests details and normalized vouchers.
2. **Operator/reviewer:** inspects persisted import status, warnings/errors, content hash, encoding, summary, and Tally business result.
3. **Service maintainer:** runs the service and tests locally with documented setup and one-command instructions; reviews the raw source only through secure persistence access, never through the details API.

## 6. Supported inputs

### 6.1 Voucher export

Root is `ENVELOPE`; voucher nodes occur under `BODY/DATA/COLLECTION/VOUCHER`. A valid empty collection is accepted and completes with zero counts.

### 6.2 Tally operation response

Root is `RESPONSE`. Parse direct children `CREATED`, `ALTERED`, `DELETED`, `IGNORED`, `ERRORS`, and `LINEERROR` when present. Business status is `failed` if `ERRORS` is greater than zero or `LINEERROR` is non-empty; otherwise it is `succeeded`.

### 6.3 Encoding and recovery

Support UTF-8, UTF-8 with BOM, and UTF-16 with BOM. Recover otherwise parseable XML containing illegal XML 1.0 numeric control references such as `&#4;`; record a warning when sanitization is applied and retain original bytes (or a secure raw-source reference). Report detected encoding in import details.

Unsupported roots or syntactically unreadable input may return a documented structured 4xx response. Never expose parser stack traces or local filesystem paths.

## 7. Functional requirements

### FR-1: Health check

`GET /health` returns HTTP 200 and a JSON object indicating the service is running.

### FR-2: Import upload

`POST /api/tally/imports` accepts `multipart/form-data` with required field `file`.

- New supported content returns HTTP 201.
- Exact bytes previously imported return HTTP 200 with the original import record and `duplicate: true`.
- Required response includes `importId`, `documentType`, `status`, `duplicate`, and summary counts for vouchers, ledger entries, inventory entries, warnings, and errors.
- `documentType` is `voucherExport` or `tallyResponse` according to the root.
- `completed` indicates successful decode, parse, and persistence; it does not imply Tally business success.
- `partial` is reserved for an implemented optional per-voucher partial-failure extension. A persisted unsafe import may be marked failed; alternatively return a structured 4xx for unreadable/unsupported content.

### FR-3: Import details

`GET /api/tally/imports/{importId}` returns at least import ID, document type, processing status, lowercase 64-character SHA-256 content hash, detected encoding, five-field integer summary, warnings, and errors. For a Tally response it also returns `tallyResponse.businessStatus`, counters, and optional line error. Counter representation must be consistent (source strings or lossless integers).

Details must not include raw XML, secrets, stack traces, or local paths. Unknown IDs return a structured not-found response.

### FR-4: Normalized vouchers

`GET /api/tally/imports/{importId}/vouchers` returns `{ "items": [], "count": 0 }` for no vouchers and for Tally response imports. Each voucher follows the JSON contract in Section 8. Unknown IDs return a structured not-found response.

### FR-5: Idempotency and persistence

Persist data in SQLite, PostgreSQL, or another documented database; in-memory-only storage is insufficient. Compute SHA-256 over exact uploaded bytes. Enforce a unique content-hash constraint or equivalent atomic persistence guarantee so concurrent identical uploads create only one import and one set of voucher rows. Re-uploaded bytes with a different filename return the original import.

Retain the raw upload or secure raw-source reference. Document the relational/JSON/hybrid model and how source order is represented.

### FR-6: Diagnostics and unknown nodes

Persist import warnings and errors. Unknown/custom nodes must not crash import. Do not claim unknown data was normalized; record a warning when source data is retained but not normalized. Import-wide warnings are returned in details; voucher-specific warnings belong on the voucher item.

## 8. Normalized voucher contract

All fields are sourced from direct children of their owning node, never descendant-wide tag searches. Missing source values may be consistently null, empty, or omitted. Every listed repeated structure is always represented as an array, including zero or one items. Preserve XML source order and do not merge equal rows.

Each voucher contains:

- `source`: import ID, exact-byte content hash, zero-based source voucher index.
- `attributes`: source `VCHTYPE` and `ACTION` when present.
- Direct voucher fields: `date`, `voucherType`, `voucherNumber`, `partyLedgerName`, `narration`.
- Ordered `ledgerEntries`, accepting both `ALLLEDGERENTRIES.LIST` and `LEDGERENTRIES.LIST`, with exact `sourceTag`; direct ledger fields; ordered `billAllocations`, `bankAllocations`, and `rateDetails`.
- Ordered `inventoryEntries` for `ALLINVENTORYENTRIES.LIST`, with direct item/quantity/rate/amount fields; ordered `batchAllocations`, `accountingAllocations`, and `rateDetails`.
- Voucher `warnings` array.

Allocation and rate detail fields are as specified in `NORMALIZED_DATA_CONTRACT.md`. In particular, each `AMOUNT` stays with its direct owner: ledger, bill allocation, bank allocation, inventory entry, batch allocation, or accounting allocation. `RATEDETAILS.LIST` belongs to its direct ledger, inventory, or accounting-allocation parent.

Money, quantity, rate, date, and boolean-like source values remain lossless source strings. Never use binary floating point for money, change a sign based on `ISDEEMEDPOSITIVE`, or remove units such as `PCS` from quantities/rates.

## 9. Persistence model requirements

The chosen storage model must make these relationships inspectable:

- Import → vouchers.
- Voucher → ledger entries.
- Ledger entry → bill allocations and bank allocations.
- Voucher → inventory entries.
- Inventory entry → batch allocations and accounting allocations.
- Owning ledger, inventory, or accounting-allocation node → rate details.

A relational, JSON, or hybrid implementation is acceptable when it preserves these ownership relationships and source ordering. The design note must explain the choice. Keep original uploaded bytes or a secure raw-source reference for traceability.

## 10. Security and operational requirements

- Enforce a configurable maximum upload size.
- Disable/reject DTD and external entity processing; prevent entity expansion.
- Treat XML content as data and never execute it.
- Do not log complete accounting XML.
- Return structured errors without stack traces or local paths.
- Document additional production controls, including secure raw-source access/retention and cleanup.
- Document a production design for 500 MB exports: incremental parsing/object boundaries, database chunking and restart checkpoints, idempotency, raw-file storage and cleanup, backpressure, and request timeouts.

## 11. Acceptance criteria

The core is accepted when the implementation demonstrates:

1. `GET /health` returns 200.
2. Multipart upload accepts the required `file` field and handles both required roots.
3. Import details and voucher endpoints return the defined shapes.
4. Repeated ledger nodes remain distinct, ordered array entries; nested allocations remain under their direct parents and preserve distinct `AMOUNT` values.
5. All listed arrays are arrays even when empty or singular; values such as signed decimal amounts, `2 PCS`, and `50.00/PCS` are unchanged.
6. SHA-256 matches exact uploaded bytes; duplicate bytes under a different filename return the original import without duplicate vouchers; persistence enforces uniqueness atomically.
7. UTF-8, UTF-8 BOM, UTF-16 BOM, and recoverable illegal control references behave as specified; sanitization records a warning and source bytes remain available.
8. A Tally `RESPONSE` with `ERRORS=1` is accepted as a document while `businessStatus` is `failed`.
9. Empty collections complete with zero counts; unknown children do not crash and generate an appropriate warning.
10. Automated behavioral tests cover all scenarios above (including the six minimum test groups in the assignment) and run with one documented command.
11. The service has documented setup/startup/migration instructions, API examples, design tradeoffs, large-file note, incomplete work/next steps, active-time estimate, and AI-use disclosure.
12. XML parsing is hardened against DTD/external entity processing and upload size is configurable.

## 12. Required test scenarios

- Repeated ledger entries remain separate and source-ordered.
- Ledger, bill, inventory, batch, and accounting `AMOUNT` values do not overwrite one another.
- Same exact bytes with a different filename return the existing import without duplicate vouchers.
- UTF-16 and illegal-control-reference recovery records a warning.
- A response with `ERRORS=1` produces failed business status while upload processing is accepted.
- Empty collection and unknown child do not crash or invent vouchers.
- Include UTF-8 BOM and ordinary UTF-8 coverage, as required by the checklist.

Tests may be unit, integration, or a sensible mix; one documented command must run them.

## 13. Delivery requirements

- Source code, database schema/migrations, and automated tests.
- Project README with one-command local startup and setup/migration/test instructions.
- API usage examples using curl or equivalent.
- Design/tradeoff and security notes, plus the 500 MB ingestion design note.
- Approximate active time, complete/incomplete work, next steps, production risk of missing work, attempted optional extensions, and AI tool disclosure.

The assignment imposes a six-hour active-work limit. Prioritize the required core and record the approximate time honestly.

## 14. Optional extensions

These are explicitly optional and must be identified as attempted or not attempted:

1. Per-voucher partial failure for missing `DATE`, `VOUCHERTYPENAME`, or `VOUCHERNUMBER`, while persisting valid vouchers and no child rows for invalid vouchers.
2. Incremental streaming ingestion with bounded memory.
3. Concurrency test proving simultaneous identical uploads create one import.
4. Persisting unknown source paths/subtrees beyond raw-source retention.
5. Normalize counters from `ENVELOPE/BODY/DATA/IMPORTRESULT`.

Optional work must not weaken or obscure core requirements.

## 15. Assumptions and open implementation choices

- The supplied assignment, normalized contract, and fixtures are the complete required integration contract; no external Tally behavior is assumed.
- A voucher export has the documented `ENVELOPE/BODY/DATA/COLLECTION/VOUCHER` path. Alternate envelopes/reports are out of scope.
- The service may choose its framework, database, relational/JSON balance, and a structured 4xx strategy for unsupported or unreadable documents, provided behavior is documented.
- Document status describes decoding/parsing/persistence, while `businessStatus` describes the result reported by Tally.
- Unknown fields can be retained in the raw upload without normalization, with a warning.
