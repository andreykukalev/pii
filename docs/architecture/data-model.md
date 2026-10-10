# Data Model And Atomicity

Production persistence is one Azure SQL serverless database with the [actual EF model](../../src/Pii.Infrastructure/WorkspaceDbContext.cs), not parallel Cosmos/SQLite stores. LocalDB is the same SQL adapter on a local test/development instance, not a second production backend.

| Table | Actual storage responsibility |
| --- | --- |
| WorkspaceHeads | Single active/last revision pointer plus SQL rowversion |
| PortfolioRevisions | Immutable revision number and serialized PortfolioRevision payload |
| ImportPreviews | ID, baseline/hash uniqueness, confirmed revision and serialized proposal |
| LedgerIdentities | Unique revision + economic transaction ID |
| AnalysisRuns | ID/idempotency uniqueness; status/created/deadline/owner/generation/expiry/version/cancel fields; rowversion and serialized AnalysisRun |
| BudgetMonths | UTC month, decimal(19,8) reserved USD total and rowversion |
| BudgetReservations | Immutable reservation ID, month and decimal(19,8) amount |

Most ledger/instrument/fact/task/tool/evidence/brief data lives in versioned JSON payloads, not independently normalized relational tables. Do not claim a separate table for every conceptual entity. SQL database collation is `Latin1_General_100_BIN2`; monetary domain values use decimal, with large payload finance values serialized rather than queried as SQL floating-point values.

## Finance And Independent Observations

[Domain finance records](../../src/Pii.Domain/Portfolio.cs) retain decimal quantity/gross/fee, original currency, transaction effective time, optional historical EUR conversion, source identity and split ratio. `TransactionKind.OpeningCash` is appended to the enum, preserving existing numeric member identities. Optional `Transaction.CashLedgerComplete` defaults to false for legacy JSON and can declare a complete native cash ledger only on an explicit OpeningCash. Opening holdings remain known quantity with unknown acquisition basis. A snapshot is never converted into an opening event.

`PortfolioFacts` adds optional `CashBalances`, `KnownCashValueEur`, `CashValuationQuality` and `KnownFeesEur`. Missing fields deserialize as null; legacy facts remain readable. Each CashBalance distinguishes nullable native Balance from nullable EUR ValueEur, balance quality from valuation quality, explicit coverage start, sourced valuation FX date/evidence and issues. One declared native account per currency is the supported neutral sample boundary. Unknown opening/coverage yields null, never zero; known zero is possible only with explicit coverage. A dated quote FX can value cash in the same original currency without an instrument price; transaction FX does not establish cash valuation. Partial EUR known subtotals never imply complete account totals.

All ledger calculations use events `Time <= AsOf`. A quote/valuation FX must also have `AsOf <=` the calculation cutoff. Future sells/dividends/withholding/fees/deposits cannot alter earlier quantities, gains, income or cash. Decimal economic FIFO is independent of research/model output; no annual tax calculation or unsupported return is added.

[Application contracts](../../src/Pii.Application/Contracts.cs) append nullable Snapshot/Reconciliation to ImportPreview and PortfolioRevision:

| JSON record | Responsibility |
| --- | --- |
| StatementSnapshot | Independent effective AsOf (including explicit supplied offset), native holding and cash observations, source document hash |
| SnapshotHolding / SnapshotCash | Decimal observed quantity or signed native balance plus original currency and row provenance; not ledger mutations |
| ReconciliationResult | Compared ledger revision, snapshot cutoff, comparison items and derived Match/Mismatch/Unknown state |
| ReconciliationItem | Metric/instrument/native currency, nullable independent and ledger amounts, nullable difference, status, reason and source |

Unknown ledger/observation amounts remain null in comparisons; omissions are not zero. Quantity matches are independent of basis sufficiency; native cash matches are independent of valuation FX. Mismatches enter the existing blocking preview Issues gate. Unknown coverage requires the explicit Imports acknowledgement, not fabricated equality. Parser resource bounds and neutral format definitions are in [portfolio ingestion](portfolio-ingestion.md).

Preview payload serialization already persists these additive records. **The existing SQL ConfirmAsync PortfolioRevision constructor must still pass `Snapshot: preview.Snapshot, Reconciliation: preview.Reconciliation`.** No new table/schema is necessary. Until its owner integrates and tests this, previews retain the independent observation but confirmed revision/run payloads omit it. The Portfolio view consumes retained Snapshot and deterministically recalculates its dated comparison when available; otherwise it reports no retained independent snapshot. Frozen optional Reconciliation preserves the original preview/baseline comparison context. ConfirmedAt/CreatedAt are workflow timestamps and must never replace the independent snapshot AsOf. Do not copy historical snapshots onto later revisions as if they were fresh observations or convert them into synthetic ledger events.

Unique run idempotency index and `(Status, ClaimExpires, CreatedAt)` index support acceptance and pending/expired scans. Serializable transactions and conditional writes with server UTC time, owner/generation/version/rowversion and nonexpired deadline prevent stale publication. First claim starts the bounded run deadline. Recovery has at most three generations and preserves budgets/results; an expired deadline ends recovery rather than renewing it forever.

Store payloads are capped at 4 MiB and ledger size at 10,000 events. ListRuns returns the newest 100; complete-history pagination/export is not provided by that method. SQL/Blob do not share an ACID transaction: immutable artifacts may be orphaned after a failed DB publication, but unpublished artifacts are not a committed report.

[Blob store](../../src/Pii.Infrastructure/BlobArtifactStore.cs) uses `workspace/` plus validated canonical relative paths, immutable create-if-absent writes and SHA-256 metadata. Maximum artifact is 8 MiB. Same name/different bytes fails. Cookie-key XML is in a separate container and is not under the document cleanup prefix.

The [schema bootstrap](../../infra/sql/schema-bootstrap.sql) mirrors seven current tables/indexes and refuses nonempty/wrong-collation/system databases. Runtime initialization still invokes EnsureCreated: complete schema must exist **before** DML-only hosts start. This file is not an EF migration system; future schema changes require reviewed forward/rollback compatibility, not editing production via application db_owner.

Database DeletePortfolioAsync removes current runs/previews/ledger/revisions and resets the head, but preserves budget reservations/month totals and last-revision identity. Artifact DeleteAllAsync removes current document-prefix blobs with bounded iteration; Blob versions/soft-delete and SQL backups require separate maintenance. Neither is an implemented public export/delete UI. See [source requirements](../product/requirements.md).