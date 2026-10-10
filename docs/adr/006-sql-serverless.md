# ADR-006: One SQL Serverless Database

Date: 2026-10-11. Status: **selected production implementation; deployment activation blocked** pending forecast, identity/schema/network and source acceptance. Scope: one operational database, no dual-backend architecture.

## Context And Selection

Cosmos eligible provisioned free tier, Cosmos serverless and SQL serverless were shortlisted in the approved plan. This implementation refinement explicitly selects Azure SQL; the existing application uses EF Core SQL Server, serializable native transactions, rowversion and conditional owner/generation checkpoints. LocalDB tests use real SQL transactions/queries rather than mocked durability. This is not a live Azure cost/RU comparison, Cosmos entitlement proof or invented benchmark.

## Decision

One General Purpose `GP_S_Gen5` serverless database, default min0.5/max1 vCore, max2GB, pause60 minutes, LRS backup and seven-day short-term retention. Bounded parameters permit reviewed scaling, not free-tier assumptions. Collation `Latin1_General_100_BIN2`; domain financial values remain decimal and serialized payloads, SQL reservation amounts decimal(19,8). [Actual tables/atomicity](../architecture/data-model.md).

Entra-only supplied tenant/object/name administrator. Runtime web/worker are separate UAMIs with verified client-ID contained users and narrow seven-table SELECT/INSERT/UPDATE/DELETE grants. Schema creation/migration rights belong to separate reviewed bootstrap identity; no application db_owner. Initial complete schema must precede EnsureCreated runtime initialization. Public endpoint/firewall is explicit, no automatically purchased private network or always-broad Azure-services exemption.

## Alternatives And Tradeoffs

Cosmos would require partition/batch/staged-publication and actual entitlement/RU/index/query evaluation; its free tier is not available for serverless or guaranteed to this subscription. SQL simplifies native confirmation plus work intent and history/claim constraints for this code. Blob-only would add manual indexed history/commit machinery; local SQLite/ephemeral files are not production sources of truth. No alternative backend is deployed as a convenience.

Minute polling means pause savings cannot be presumed: active minimum/memory plus bursts may dominate retained personal-scale infrastructure. SQL resume/cold start/storage/backup/network/tax/restore pricing is regional and unverified. LocalDB success does not establish production RBAC, actual pause behavior, Linux MI auth or restoration.

Acceptance: reviewed generated schema metadata and DML-only initialization, stale-confirm/atomic-rollback visibility, unique identities/idempotency, claims/fencing/expiry/deadlines/cancel/budget persistence, permitted immutable report publication and restore/maintenance. Local actual-SQL main tests reported separately from IaC; Azure checks NotRun. Revisit on actual invoices, sustained backlog/latency, growth past bounded payload/history/storage limits, rights/backup needs or USD100 envelope failure. [Deployment](../operations/deployment.md), [cost gate](../operations/cost-model.md).