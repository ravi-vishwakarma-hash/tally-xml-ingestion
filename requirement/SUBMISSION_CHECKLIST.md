# Submission Checklist

Before submitting, please confirm:

- [x] I stopped at or before 6 hours and recorded my approximate active time (candidate estimate: under 3 hours).
- [x] `GET /health` works.
- [x] `POST /api/tally/imports` accepts multipart field `file`.
- [x] `GET /api/tally/imports/{importId}` returns import details.
- [x] `GET /api/tally/imports/{importId}/vouchers` returns the normalized contract.
- [x] Both `ENVELOPE` and `RESPONSE` roots are handled as documented.
- [x] Repeated `.LIST` nodes remain arrays under their direct parent.
- [x] Signed amounts are lossless and not stored as binary floating-point.
- [x] UTF-8, BOM, and UTF-16 behavior is tested.
- [x] Duplicate content is idempotent regardless of filename.
- [x] Exact-byte SHA-256 uniqueness is enforced atomically in persistence.
- [x] Import warnings and errors are persisted.
- [x] Raw source or a secure raw-source reference is retained.
- [x] Tally `ERRORS` and `LINEERROR` produce `businessStatus: failed` without confusing upload acceptance.
- [x] Unknown children do not crash ingestion and produce a warning.
- [x] An empty collection completes with zero vouchers.
- [x] Tests run with one documented command.
- [x] Database setup/migrations are documented.
- [x] The 500 MB ingestion design is explained.
- [x] Security assumptions are explained.
- [x] Incomplete work and next steps are stated honestly.
- [x] AI tool usage is disclosed.

Verification notes: the .NET 10 build succeeded and all 27 tests passed. The configured SQL Server database name is `TallyIngestion`, while the application defaults to in-memory storage. The SQL Server implementation was not exercised against a live database. HTTPS redirection is configured, but an end-to-end HTTPS/HSTS check still requires a valid local development certificate (`dotnet dev-certs https --trust`). SQL Server and HTTPS runtime verification should be completed in the target environment before submission.

Optional extensions:

- [ ] If I claim per-voucher partial failure, I tested valid and invalid vouchers in one document. (Not claimed; extension not attempted.)
- [x] I listed every optional extension I attempted and did not present unimplemented work as complete.
