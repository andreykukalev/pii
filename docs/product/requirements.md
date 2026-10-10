# Requirements And Evidence

Baseline: approved saved plan plus the implementation refinements supplied on 2026-10-11. Original full text remains [unavailable](initial-specification.md). This is a traceability baseline, not a promise that every essential capability is complete.

| ID | Requirement | Implementation anchor / acceptance evidence |
| --- | --- | --- |
| R01 | Advisory only; one authorized user; no broker credentials, orders or execution | [Security](../../src/Pii.Web/Security/WorkspaceSecurity.cs); dependency/endpoint review; manual trade boundary |
| R02 | EUR, decimal economic FIFO, dated FX, fees, dividends, unknown history/cash/basis explicit; no Lithuanian tax claim | [Domain](../../src/Pii.Domain/Portfolio.cs); independent financial tests; actual cash/snapshot coverage remains qualified |
| R03 | Private originals, preview, explicit confirmation, idempotent overlap, pinned revisions | [Import](../../src/Pii.Application/ImportCoordinator.cs); only exact PII-Sample-v1 CSV verified internally, not Revolut/PDF |
| R04 | Exactly one production DB: Azure SQL General Purpose serverless | [SQL store](../../src/Pii.Infrastructure/SqlWorkspaceStore.cs); actual LocalDB tests reported separately from Azure SQL |
| R05 | Durable pending work; finite minute polling; crash expiry/fencing; bounded recovery | [Worker](../../src/Pii.Worker/Program.cs), [Bicep](../../infra/main.bicep); no Queue, second DB or forever dispatcher |
| R06 | Daily 08:00 Europe/Vilnius, weekends/DST, due-key uniqueness; no exact completion SLA | [DueKey](../../src/Pii.Worker/DailySchedule.cs); every-minute UTC job with `--daily`; overdue current-date submission, not fabricated historical briefs |
| R07 | Connected six-role model/tool loop, held plus outside/sector research, long-term/tactical, critic and deterministic gates | [Pipeline](../../src/Pii.Application/InvestmentPipeline.cs); actual fixture transport execution, not disconnected prompts |
| R08 | Primary Azure OpenAI/Foundry path with strict tools/schema, MI, rights, region, rates and bounded calls | [Gateway](../../src/Pii.Infrastructure/AzureModelGateway.cs); API `2024-10-21`, Azure.AI.OpenAI `2.1.0`; live verification NotRun |
| R09 | SEC/ECB and one bounded commercial candidate path with lawful processing/retention/coverage | [Sources](../../src/Pii.Infrastructure/ResearchSources.cs); recorded HTTP tests; no purchased provider or complete-news claim |
| R10 | BUY/HOLD/REDUCE/SELL/REVIEW/WATCHLIST, no-action, explicit unconfigured policy, no invented sizing | [Policy](../../src/Pii.Domain/Portfolio.cs); numerical policy and complete suitability inputs remain required |
| R11 | Private brief, evidence/critic/task/tool/usage/history inspection; finite limits and cancellation | [Contracts](../../src/Pii.Application/Contracts.cs); UI/manual/browser evidence separate; no invented export/delete UI |
| R12 | Private containers, web/worker/deploy identities, least privilege, persisted cookie keys | [Infrastructure](../../infra/main.bicep), [runtime SQL grants](../../infra/sql/runtime-users.sql); actual Entra/RBAC NotRun |
| R13 | Total after-tax USD100 planning ceiling; forecasts and authority before chargeable deployment | SQL minute-poll active minimum, fixed charges, all agent calls, no-grant/retry/growth scenarios; no verified qualifying quote |
| R14 | .NET SDK 10.0.401, Linux non-root images, WIF CI, audit, local Bicep checks and approved what-if/apply | [Pipeline](../../azure-pipelines.yml); local publish/compilation evidence is not a hosted pipeline run |

Essential gaps are acceptance blockers, not reasons to invent facts: original/sample files, independent reconciliation/cash detail, stable web-to-worker model binding, production Entra/network/RBAC, model/source compatibility and licensing, recovery/retention testing, regional after-tax forecast and explicit authority.

The selected deployment is public HTTPS with authenticated user access and public Azure service endpoints with Entra/RBAC. It is not private networking, multi-region HA or a hard billing cap. Further analytical coverage, notification delivery, calibrated outcomes and advanced strategy work are later enhancements, not substitutes for the first-release research platform.