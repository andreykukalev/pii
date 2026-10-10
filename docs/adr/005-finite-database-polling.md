# ADR-005: Finite Database Polling

Date: 2026-10-11. Status: **selected implementation; chargeable activation blocked** pending source/environment/cost acceptance. Owner: approved implementation refinement and main application's durable-work owner; infrastructure/docs implement that choice.

## Context

Single-user imports and on-demand research should start within minutes, with durable backlog recovery and daily08 local generation. Web must not retain work in memory or receive ARM job-start permissions; template override can expose worker secrets/identity. Queue/outbox/repair and database polling were shortlisted, not both required. Existing SQL store atomically persists confirmation/run intent, conditional expiring claims and fenced publication.

## Decision

Use one shared finite Consumption Container Apps Job at UTC cron `* * * * *`, `--daily`. Each invocation checks Europe/Vilnius due local08 key/date/version and then consumes pending/expired SQL work even when before08 or daily key already exists. Production overrides finite batch to one, maximum run10 minutes, platform timeout900 seconds, replica completion/parallelism1 and platform retry0. Application recovery is bounded by existing deadlines/generations, not multiplied platform retries. No Queue, second DB, event job, permanent dispatcher or web ARM privilege.

Same-date due-key uniqueness handles repeated minute starts and DST. Full prior-day outage does not create fictitious historical briefs; existing pending runs are recovered/stopped under their actual deadlines, and current-date daily submission occurs after due time using actual evidence/cutoff. Changing scheduleVersion changes idempotency and must be intentional.

## Alternatives

Storage Queue with durable run intent could avoid SQL wake from idle queue-scaler checks, but introduces delivery/visibility/repair/poison boundaries and still needs prompt lost-dispatch repair. Once-daily SQL scan misses within-minutes work. In-process fire-and-forget and forever dispatcher are rejected. No claim of measured cloud superiority or cheapest cost is made.

## Consequences And Checks

Local main-owner SQL tests cover atomic acceptance, overlap/one-winner claims, stale checkpoints, expiry, bounded recovery/counters and cancellation. Offline Bicep contracts check cadence/args/batch/retry/timeouts. Overlapping scheduled executions remain possible; parallelism1 is not global serialization. Cold start/private pull/SQL wake and bursts determine actual latency/backlog drain; live schedule/timing remains NotRun.

43,200 invocations/30days (44,640/31days) plus startup/processing/logs/queries must be budgeted. One-minute SQL connections can keep 0.5-vCore minimum active all month: at least360/372 vCore-hours before memory/storage/bursts/tax. [Cost model](../operations/cost-model.md) is not a verified <=USD100 quote. Review cadence if actual latency/billing/entitlement fails the authorized envelope; do not silently add a second dispatch architecture or reduce cadence below required work latency without decision.