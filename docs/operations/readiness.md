# Readiness And Verification Register

Date: 2026-10-11. Overall readiness: **Partial / Blocked for production activation**. Infrastructure and operating documentation are implemented and locally checked; essential independent-source and real-environment acceptance remains unresolved. No resource provisioning, login, purchases, model/provider calls or actual pipeline run occurred.

## Incremental Infrastructure

Final main-owner verification for this incremental deployment task (2026-10-11): `dotnet test Pii.slnx --disable-build-servers -m:1` passed **276 tests**, zero failures/skips, including enabled health telemetry through an in-memory channel and explicit Azure Blob configuration. A prior parallel terminal run was interrupted during compilation; the isolated retry passed. Whole-solution top-level/transitive NuGet audit reported **zero known vulnerabilities**. The health-event SDK is Microsoft.ApplicationInsights **2.23.0**; tests verify bounded permitted fields and telemetry-off behavior, not actual Azure ingestion. CI now runs the three modular validation suites alongside the legacy checks; the actual hosted pipeline remains NotRun.

Final local infrastructure suites passed **159 modular**, **76 wrapper**, **16 local-template**, **120 legacy IaC**, and **46 legacy parameter** checks; documentation checked **141 links**. Pipeline checks used a PowerShell structural fallback because the Node YAML parser was unavailable, so this is not a new full YAML parse or Azure DevOps execution. No cloud deployments, authenticated validation, inference, source calls or purchases were performed.

Implemented within infra and the three owned operations documents: eight independent entrypoints (including optional developer access), three scoped runtime-access modules, eight PowerShell5.1 wrappers/shared manifest validation, seven compiled stage parameter examples, protected-secret/local exact-key templates and separate SQL developer contained-user grants. [Inventory and commands](../../infra/README.md) identify each resource owner. Legacy main/modules/examples/pipeline remain compatible and are explicitly not the incremental workflow. All excludes compute/application; monitoring/model deployment/shared keys/SQL password auth/scheduling/telemetry are opt-in.

Local results from this implementation: **156** Bicep/module/ARM contract checks and **16** parameter compilation/JSON/developer-grant checks. PowerShell syntax/offline/mocked deployment safety checks include protected parameters, subscription/type/kind/region/account conversion/PTU/deletion and hash-only per-stage WhatIf receipt/input/property-drift gates; see [the executable wrapper suite](../../infra/validation/Test-DeploymentWrappers.ps1) for its current count. Preserved legacy checks passed **120** IaC and **46** parameter gates. Compiler version0.48.1; every new entrypoint and module compiled cleanly. Mock cloud responses are not Azure validation. No Azure API/provider/authenticated ARM call occurred. These counts are independent of earlier application results below.

Main-owner host changes are now implemented: explicit Development `Pii:Blob:ServiceUri` uses DefaultAzureCredential, conflicting URI/connection-string settings fail, Azure failures do not fall back to emulator, and ordinary runtime does not create Azure containers. Web loads Development user-secrets ID `pii-web-development`; worker explicitly loads `pii-worker-development`; environment configuration takes precedence. Raw `PII_AZURE_OPENAI_API_KEY` and `PII_TWELVEDATA_API_KEY` resolve through `IConfiguration` and remain worker-only. Optional `Pii:Telemetry:Enabled` plus protected `APPLICATIONINSIGHTS_CONNECTION_STRING` emits health events only, not automatic request/dependency tracing; worker flush is bounded to two seconds, with no SQL text, private routes, URLs or payloads exported. Telemetry-off does not perform exporter network calls. These implementation/local-test results do not establish live telemetry capture/privacy.

Focused infra repair results: **76** PowerShell syntax/offline/mocked wrapper checks and **159** local Bicep/module/ARM contract checks passed. Both suites were run immediately after the first repair (then 59/156 checks) and rerun with focused negatives and updated modular assertions. Guards reject image hosts outside the selected registry offline and mismatches with actual `loginServer` through mocked preflight; Blob account-root and query/fragment-free XML key-blob contracts are distinct. Registry pull GUIDs now include current principal IDs; migration requires assignment review, with no automatic old-grant deletion. Earlier static results above and below are retained as historical evidence, not silently replaced or summed.

Actual existing-resource preflight, Foundry root/SDK model support, regional Basic/serverless/SKU acceptance, new/existing repeat what-if, Entra SQL/Blob/User-vs-MI identity roundtrip, live telemetry capture/redaction/bounded flush and Docker Linux runtime remain **NotRun**. Authenticated Validate/WhatIf/Apply require explicit operator authority; deployment remains blocked on forecast and evidence. No Azure calls were made in this repair.

## Executed Evidence

| Check | Result / environment | Scope, not implied claims |
| --- | --- | --- |
| `dotnet --version` | Passed: Windows selected SDK 10.0.401 | Exact public SDK pin, not container SDK or hosted-agent installation |
| `az bicep install` then `az bicep build` | Passed: local CLI 0.48.1; main and modules clean after iterative fixes | Compiler/lint only, no ARM what-if/validation or service availability |
| `./infra/validation/Test-IaC.ps1` | Passed: 93 offline resource/env/container-text/schema-grant contracts | Static compiled ARM/config checks, not actual Azure RBAC/network/SQL execution |
| `./infra/validation/Test-ParameterGates.ps1` | Passed: 22 positive/negative tests; unconfigured example rejected | Synthetic random IDs/rates used only offline, not credentials or quote |
| `./infra/validation/Test-SchemaBootstrap.ps1` | Passed: 76 actual disposable LocalDB schema/index/precision/rowversion/precreated-schema visibility and table-DML/denied-DDL checks; test DB removed | Executes supplied schema DDL locally; simulated WITHOUT LOGIN users test grants, not Azure Entra SID creation or deployed MI authentication |
| `./infra/validation/Test-TrackedSecrets.ps1` | Passed: limited credential signatures | Not exhaustive secret/DLP/security audit |
| Standalone yaml@2.8.1 `yaml valid` and JSON structure inspection | Passed: YAML parse plus 12 CI gate checks for disabled defaults/SDK/WIF/authority/forecast/what-if/environment | Not Azure DevOps schema/expansion/service-connection/approval execution |
| `dotnet list src/Pii.Web/Pii.Web.csproj package --vulnerable --include-transitive --format json --output-version 1 --no-restore` | Passed: machine-readable JSON, zero known top-level/transitive vulnerability findings | Web dependency graph only, not whole-solution audit or proof of no unknown vulnerabilities |
| Web/worker `dotnet publish --configuration Release --runtime linux-x64 --self-contained false -p:UseAppHost=false -p:ContinuousIntegrationBuild=true` | Passed: SDK10.0.401, output outside repository | Linux-target compilation, not Docker build/Linux execution/browser/auth |
| `./infra/validation/Test-Documentation.ps1` | Passed: local file links and PowerShell parser | Not product-feature acceptance or live source truth |

SQL files intentionally use SQLCMD `:ON ERROR EXIT`; the ordinary editor T-SQL parser can flag that directive without SQLCMD mode. It is not standard SQL sent to the server. The schema test removes only the client directive and substitutes a safe generated database name before local execution. Azure Entra-specific user syntax still needs its actual environment test.

Main-owner supplied earlier application test results: 9 financial/pipeline, 75 infrastructure including actual SQL LocalDB and recorded HTTP model/source, 60 web/schedule/host. Not rerun in this docs/IaC scope, counts may increase; do not combine these with offline infrastructure counts as a single executed suite. Local durable restart/browser tests are still to be performed by the main owner.

Latest main-owner report: **275 full application tests passed without Azure** (count may increase), including the implemented host configuration/telemetry changes. This is supplied application evidence, not a test run performed by this infra-only repair. Keep the earlier results for provenance and actual live checks as NotRun.

## Source-Owner Blockers

| Impact | Gap observed in current source / required repair or evidence |
| --- | --- |
| Web-submitted research regression | Approved plan records claim-time worker ModelBinding stamping as fixed; retain worker-only model/provider configuration. Main owner must report regression and actual web-submitted worker acceptance, not treat old submitter-binding diagnosis as a remaining code blocker |
| Financial actual-data acceptance | Exact sample CSV only, not real Revolut CSV/PDF parser; no independently validated real snapshot/cash-reconciliation workflow. Detailed cash quality/account support remains limited |
| Suitability/sizing | Policy supports gates, but no completed scenario/sizing workflow; current supervisor never supplies proposed percentage. Do not claim action eligibility merely after setting policy |
| Licensed lifecycle / restore | Store deletion APIs exist; no UI export/delete or complete tested embedded-evidence/provider-withdrawal/Blob-version/backup cascade. Manual procedure alone is not implemented acceptance |
| Source coverage | 1MiB full-response bound may reject real SEC companyfacts; recent-submission/tag/window/ETF/news/timezone limits. Provider credit/cache/retention claims require implemented controls/evidence |
| Cost/provenance completeness | Current estimator has input/output rates only, no independently priced cache/reasoning classes; static version labels do not prove image/commit/config pinning into every run |
| Whole CI | Pii.slnx is present; Windows agent LocalDB availability, actual hosted entitlement and unchanged legacy pipeline execution remain unverified. Never omit actual SQL tests silently |

This is a limited configuration/operating audit, not the remaining source-logic audit. The main owner retains source/tests and final financial, orchestrator, retention, concurrency and browser review. No source/test fixes were made in this ownership slice.

## Activation And NotRun

| Capability | Status / exact missing action |
| --- | --- |
| Original full specification archive | Unavailable; preserve exact supplied text later, not reconstruct; [notice](../product/initial-specification.md) |
| Docker image build/runtime | NotRun: Docker absent; run supplied CI/local Docker builds and nonroot/shared-framework/runtime checks on actual Linux host |
| Azure resource/what-if/deploy | NotRun: no authority/resource action; select region/SKU/identity, complete forecast, then reviewed WIF what-if/apply |
| Production Entra/MI/SQL/Blob/ACR | NotRun: actual tenant/app/owner/admin/identity grants, schema bootstrap, firewall/private pull and denied-scope tests required |
| ModelOnFixtures / real-model capability | NotRun: access/deployment/model/version/API/token support, lawful inputs/geography/rate card/authority required |
| Real sources / real provider format | NotRun: actual covered IDs/dates, rights/endpoint credits/key/timezones, recorded live response contract validation required |
| Real Revolut / real snapshot/cash | NotRun: privately anonymized samples and independent totals plus verified parser/reconciliation code required |
| Live complete research | NotRun: claim-time binding regression acceptance, real baseline/candidates, permitted sources/model, critic/gates/trace and actual measured usage required |
| Local durable/browser | Pending main-owner testing; do not claim restart or browser workflow passed from unit/HTTP evidence |
| Schedule/backlog / SQL billing | Template cadence locally checked; live within-minutes timing, overlap/recovery and minimum-active SQL invoice unmeasured |
| Export/delete/restore | NotRun as complete operation; implement/approve maintenance, inspect versions/embedded evidence/backups and test restoration |
| Budget qualification | Blocked: no actual verified after-tax normal/peak/no-grant/full-loop forecast <=USD100 or deployment authority |

Use [manual checklist](manual-testing.md) for reruns and record Passed/Failed/NotRun by input mode/service independently. Fixtures are not real research, configuration is not access, compilation is not deployment, and an approved resource deployment alone is not full production readiness.