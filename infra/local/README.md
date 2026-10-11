# Local Hosts With Azure Dependencies

This guide is operator-only integration, **not executed cloud verification**. Dependencies are independent of compute. Use one dedicated Azure SQL **fixture acceptance** DB and private Blob account/containers, never a real ledger, for no-inference checks. No model deployment/purchases are needed for FixtureReplay. Retain existing bundled appsettings; apply explicit configuration instead of replacing them.

## Prerequisites And Identity

Require SDK **10.0.401**, an approved subscription/tenant/region, SQL/Blob endpoints, reviewed IP/firewall route and Entra admin/bootstrap authority. Main-owner host changes are now implemented: explicit Development `Pii:Blob:ServiceUri` uses DefaultAzureCredential, conflicting URI/connection-string settings fail, Azure failures never fall back to emulator, and ordinary runtime does not create Azure containers. Provision the intended containers first. Both hosts load their own user-secrets in Development with environment precedence; telemetry OFF performs no exporter network I/O. Main owner reports **275 full application tests passing without Azure**; this infra-only repair did not rerun them, and counts may increase. That local evidence is not actual Azure integration acceptance.

The main agent owns source/test changes. Implemented optional telemetry keys are **`Pii:Telemetry:Enabled`** and protected **`APPLICATIONINSIGHTS_CONNECTION_STRING`**. Instrumentation emits health events only, not automatic request/dependency tracing; finite worker flush is bounded to **two seconds**. Container logs/Log Analytics operational capture is distinct from these application health events. No prompts, statements, SQL text, private routes, provider URLs/keys or payloads are exported. Host unit tests and monitoring ARM resources do not prove live telemetry capture/redaction; actual network capture remains NotRun.

Login is deliberate and manual; wrappers never do it:

```powershell
$manifest = './infra/parameters/manifest.json'
$subscription = (Get-Content $manifest -Raw | ConvertFrom-Json).subscriptionId
$tenant = (Get-Content $manifest -Raw | ConvertFrom-Json).shared.auth.tenantId
az login --tenant $tenant
az account show --subscription $subscription --query '{id:id,tenantId:tenantId}' --output json
az ad signed-in-user show --query id --output tsv
```

Verify the intended user, object ID and subscription. DefaultAzureCredential reads the developer credential context; because its Azure CLI credential can use the CLI's active tenant, independently verify the active account and intended data resource tenant. Every documented ARM command explicitly supplies subscription; do not assume that makes SDK credential selection automatic. No automatic subscription mutation is supplied. Unset inherited `AZURE_CLIENT_ID` locally unless intentionally choosing a supported credential. Web loopback identity authorizes app access only; it is **not** Azure data-plane auth. Future web/worker UAMI principal IDs are ARM RBAC IDs and their client IDs are SQL/SDK MI selection IDs, not developer user object IDs.

Review the selected providers manually (`Microsoft.CognitiveServices`, `Microsoft.Storage`, `Microsoft.Sql`; later `Microsoft.Insights`, `Microsoft.OperationalInsights`, `Microsoft.ManagedIdentity`, `Microsoft.App`, `Microsoft.ContainerRegistry`). Wrappers do not register them or accept terms. Compile locally, then obtain separate authority for ARM WhatIf/Validate/Apply using [stage commands](../README.md).

## Grants And Schema

Provision approved storage/data stages first. A SQL Entra admin applies [schema-bootstrap.sql](../sql/schema-bootstrap.sql) to the intended **empty** dedicated DB; for an existing compatible workspace use the reviewed [source-credit-upgrade.sql](../sql/source-credit-upgrade.sql), not EnsureCreated/drop. There are nine tables including shared-credit totals/entries. Then apply [developer-users.sql](../sql/developer-users.sql) in SQLCMD mode through the separate admin connection with verified safe variables `DatabaseName`, `DeveloperObjectId`, `DeveloperPrincipalType=E` for User or `X` for Group. The graph-free SID uses the actual User/Group **object ID**. Existing principal is never silently rebound. Developer receives table-scoped DML, narrow source-credit access, no DDL/db_owner. SQL admin and developer identities must differ; an admin token still bypasses the intended least-privilege test.

SQLCMD/VS Code MSSQL or another approved SQL client must connect with Entra interactively/default credentials. Never pass a password/access token in CLI arguments or chat. Verify the target DB, all schema metadata, developer login and denied DDL. ARM deploy authority is not SQL user authority. Developer script was checked locally as text, **not executed against Azure SQL**. Future UAMIs use [runtime-users.sql](../sql/runtime-users.sql) after compute with verified **client IDs**; only the worker receives credit grants. Do not give project MI data access.

Developer Blob RBAC is a separate optional resource action. `access.bicep` defaults principalType User, documents-only; choose Group explicitly when appropriate. Grant keys only when testing persisted production cookies; local Development does not normally need that container. Inference is a further explicit opt-in:

```powershell
$subscription = (Get-Content $manifest -Raw | ConvertFrom-Json).subscriptionId
az deployment group what-if --subscription $subscription --resource-group <storage-rg> --name pii-dev-developer-access --template-file infra/stages/access.bicep --parameters storageAccountName=<actual-name> principalId=<verified-user-object-guid> principalType=User grantKeyContainer=false grantInference=false --mode Incremental --result-format ResourceIdOnly
# Only after separate role-assignment authority/review, replace what-if with create and omit result-format.
```

The access entrypoint scopes storage and optional Foundry in the same RG. For a different Foundry RG, use a separate reviewed role assignment there or the scoped runtime inference module with a deliberately supplied developer principalType extension; do not reuse the ServicePrincipal-only runtime module for a User. No developer grant is automatic. Allow time for RBAC propagation and refresh credentials manually; no helper sleeps/retries provisioning indefinitely. SQL operator IP rules and Blob allowed-IP/network policy must match the actual route. Azure-services SQL exemption is OFF; enabling it permits broad Azure-tenant network reach, not just this app.

## Shared Configuration

[appsettings.Development.json](../parameters/local/appsettings.Development.json) inventories **shared** keys; [worker.settings.json](../parameters/local/worker.settings.json) inventories **worker-only** model/source keys. These tracked placeholders moved under `infra/parameters/local`; they are manually imported templates, not auto-loaded application paths. Use approved user-secrets/nonsecret environment configuration for both hosts. Do not pipe whole worker configuration into web. User-secrets are Development convenience storage, not an encrypted vault; protect local profile/files and never publish them. Resource identifiers come from the SAME existing manifest selected for [each deployment stage](../README.md#manifest-and-names); local templates do not become another provisioning source. Runtime key names and application behavior are unchanged.

Development user-secrets are implemented: web ID **`pii-web-development`** uses the web host loader; worker ID **`pii-worker-development`** is explicitly loaded by the worker. Environment configuration overrides user-secrets in both hosts. The project-based commands below select those IDs automatically.

```powershell
# Nonsecret shared config: same values in the two terminal sessions.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Pii__Mode = 'FixtureReplay'
$env:Pii__AllowDevelopmentIdentity = 'true'
$env:Pii__Sql__ConnectionString = 'Server=tcp:<actual-server>.database.windows.net,1433;Database=pii_fixture_acceptance;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;Connect Timeout=60'
$env:Pii__Blob__ServiceUri = 'https://<actual-account>.blob.core.windows.net/'
$env:Pii__Blob__Container = 'documents'
$env:Pii__Worker__MaximumBatch = '1'
$env:Pii__Telemetry__Enabled = 'false'
Remove-Item Env:Pii__Blob__ConnectionString -ErrorAction SilentlyContinue
Remove-Item Env:AZURE_CLIENT_ID -ErrorAction SilentlyContinue

dotnet run --project src/Pii.Web -- --urls http://127.0.0.1:5080
# Separate terminal, identical shared configuration:
dotnet run --project src/Pii.Worker -- --once
```

Active Directory Interactive is an optional deliberate SQL connection-string choice, but it prompts and is not suitable for unattended worker runs. Azure paused SQL may resume slowly; use bounded connection timeout and diagnose the actual failure, never silently switch backend. No auth/key listing is performed by infra scripts.

For explicit development SQL-password mode, opt in on the **data stage** using a protected secure file with only `sqlAdministratorPassword`, then configure a separate least-privilege SQL user connection with secure user-secrets. Use `Authentication=Sql Password;User ID=<reviewed-dev-user>;Password=<secret>` plus Encrypt/verified certificate; do not run hosts as SQL administrator. Keep Entra default and future hosted MI auth unchanged. For explicit shared-key Blob mode, opt in dev-only at storage; supply a protected `Pii:Blob:ConnectionString` **instead of** ServiceUri. Obtain/rotate keys yourself using an approved tool; scripts never extract them. Protect files and remove conflicting environment values. No keys in outputs or tracked templates.

[web.secrets.json](../parameters/local/web.secrets.json) and [worker.secrets.json](../parameters/local/worker.secrets.json) show placeholders only; remove unused entries, do not load placeholder values. Real credentials remain outside the repository. A private JSON dictionary can be imported into user-secrets without placing literal secrets on a command line:

```powershell
Get-Content "$HOME\pii-private\web.secrets.json" -Raw | dotnet user-secrets set --project src/Pii.Web
Get-Content "$HOME\pii-private\worker.secrets.json" -Raw | dotnet user-secrets set --project src/Pii.Worker
```

Do not run `user-secrets list`/environment dumps in shared logs. Existing raw `PII_AZURE_OPENAI_API_KEY` and `PII_TWELVEDATA_API_KEY` names now resolve through `IConfiguration`, including worker user-secrets with environment precedence, and remain worker-only. Do not supply either to web. Azure model key auth is only an explicit Development choice: set worker `Pii:Model:Authentication=ApiKey` when deliberately authorized; never configure model keys in hosted production. Prefer DAC with inference RBAC.

## Verification Ladder

1. **No inference:** confirm config diagnostics, SQL developer connection and denied DDL, private Blob read/write/delete roundtrip in the isolated container, import preview/confirm and artifact persistence, web denied remote/nonloopback identity paths. Fixtures are never written to a real ledger. For an empty isolated workspace only, `dotnet run --project src/Pii.Worker -- --fixture-scenario` exercises supplied dated synthetic imports/research; UI must remain FixtureReplay. Use `--once` for queued work and `--daily` for due-local-08:00 plus pending processing. Workers exit; no perpetual dispatcher.
2. **ModelOnFixtures:** independently authorize the actual consumption deployment, supported model/version/API/tools/strict schema, geography/rights, positive dated input/output rates and bounded budgets. Configure shared `Pii:Mode=ModelOnFixtures` in both hosts and worker-only model settings from the inventory; turn `Pii:LiveAuthorized`/processing/capability flags on only with actual evidence. Use the **resource** `.openai.azure.com/` root for SDK2024-10-21, not project endpoint. This incurs paid inference and is a separate operator action, never part of deploy/validate. Actual model behavior/usage is NotRun until observed.
3. **Live:** new or deliberately reviewed real SQL workspace, independently reconciled actual baseline and canonical held/sector/outside candidate catalog, licensed sources/entitlements/weighted credits/indefinite-retention rights, approved processing and forecast. Switch both hosts to Live and configure worker-only provider settings/secure key. Do not seed synthetic IDs. Inspect critic/gates/trace and actual consumption. Missing evidence/rights is Unavailable/Partial, never fixture fallback or claimed real portfolio research. Main-owner claim-time ModelBinding stamping keeps model/provider config out of web; validate that regression before acceptance.
4. **Optional telemetry:** provision selected monitoring separately; protect manually obtained `APPLICATIONINSIGHTS_CONNECTION_STRING` and explicitly enable `Pii:Telemetry:Enabled` in both hosts. The implemented scope is health events only, not automatic dependency tracing. Verify actual delivery/privacy and the two-second bounded worker flush, with no SQL/routes/payloads. Disable and verify no exporter calls. Resource creation and local tests do not establish live telemetry privacy; capture remains NotRun.

Record each capability as Passed/Failed/NotRun with command, date, environment, actual-vs-fixture input and reason. Check Azure SQL auto-pause/wake, RBAC propagation, owner auth, endpoint/version/quota, model unavailable flags, secret precedence and actual Blob URI path separately. No live check, Docker runtime, telemetry network capture, SDK inference or repeat-deploy WhatIf was performed in this implementation. The [readiness register](../../docs/operations/readiness.md) remains the source of evidence, not a green aggregate claim.