# Architecture Decision Index

Decisions reflect the actual implementation refinement supplied on 2026-10-11, not an invented archival approval. Technical selection does not authorize chargeable deployment. The approved full plan remains referenced only at `/memories/session/plan.md`; [original source status](../product/initial-specification.md).

| ADR | Status | Decision / owner and evidence |
| --- | --- | --- |
| [005](005-finite-database-polling.md) | Selected implementation; activation gated | SQL pending work + shared finite every-minute scheduled worker; user refinement/main actual store, local claims tests; cloud latency/cost unmeasured |
| [006](006-sql-serverless.md) | Selected production backend; activation gated | One Azure SQL General Purpose serverless database; actual SQL adapter/LocalDB transaction tests, regional forecast still missing |

Other confirmed invariants retained without fabricating full historical ADR files: advisory-only/manual execution, private single-user Entra workspace, EUR/economic FIFO with unknown quality, every-day local08 schedule, six logical roles in one runtime with deterministic authority, unconfigured numerical policy, private/rights-limited history and total after-tax USD100 ceiling. Runtime .NET10/Consumption/Blob/ACR/WIF choices are documented in [overview](../architecture/overview.md) and [deployment](../operations/deployment.md).

Review decisions on evidence, not milestone approval: material changes in cost/rights/security/scope require explicit authority; routine source corrections use focused tests. No second database/Queue/forever dispatcher is introduced to bypass a blocker.