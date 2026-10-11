# Incremental Infrastructure

This is the independent-stage workflow. [main.bicep](main.bicep) and [parameters.example.json](parameters.example.json) remain the **legacy combined deployment contract** for the unchanged pipeline; they are not invoked by these wrappers. Do not alternate legacy and staged owners on the same resources without reviewing names, role IDs and settings. This change performs no Azure login, resource operation, provider registration, inference, image build or push.

## Inventory And Ownership

| Stage / entrypoint | Created resources in create mode | Existing mode / outputs |
| --- | --- | --- |
| [foundation](stages/foundation.bicep) | Subscription-scoped resource group only | `createResourceGroup=false` validates binding; RG ID/name/region |
| [foundry](stages/foundry.bicep) | S0 **AIServices**, `allowProjectManagement=true`, child project; optional consumption model deployment OFF | Existing OpenAI or AIServices account, optional existing project; account ID/kind, **resource OpenAI root**, project ID, deployment/version |
| [storage](stages/storage.bicep) | GPv2 Standard_LRS Hot account, private documents and keys containers; versioning and 7-day soft-delete | Existing account/containers are read-only bindings; account ID, Blob root, container names/keyring URI |
| [data](stages/data.bicep) | SQL server, one native SQL DB, reviewed firewall rules and backup retention | Existing server/DB read-only binding; server/database IDs, FQDN/database name; never credentials |
| [monitoring](stages/monitoring.bicep) | Optional workspace + workspace-based Application Insights | Independent workspace/Insights create-or-existing modes; IDs only, telemetry enabled flag |
| [compute](stages/compute.bicep) | Consumption environment, optional ACR Basic, two UAMIs, scoped pull/Blob/optional worker inference grants | Existing environment/registry/identities supported; environment/registry IDs, domain, identity principal/client IDs |
| [application](stages/application.bicep) | Web Container App and finite worker Job **only when explicitly enabled** | Binds existing environment, registry and identities; host/job names; scheduled trigger separately OFF |
| [access](stages/access.bicep) | Explicit optional local User/Group container RBAC; optional inference/keyring grant | Not included in all; does not require a compute identity |

No Hub, hosted agents, AI Search/vector store, Cosmos, Redis, Queue, Key Vault, private endpoint, NAT, paid availability tests, budgets, application registration or permanent dispatcher is created. Project identity receives no data-plane grants. Storage remains authenticated/private, not network-private by default.

## Manifest And Names

Keep the actual manifest and all protected parameter/configuration files **outside the repository**, for example under `$HOME\pii-private`. Edit an operator-owned instance based on [manifest.example.json](parameters/manifest.example.json). The example has placeholders, not actual identity/region/entitlement/forecast evidence. Fields: `schemaVersion=1`, explicit subscription/RG/location/environment/prefix/tags and `stages.<stage>.enabled/parameters`. An optional stage `resourceGroupName` supports an existing Foundry/storage/data RG in the **same subscription and selected region**. Compute's `storageResourceGroup`, `registryResourceGroup`, `foundryResourceGroup`, `workspaceResourceGroup` allow scoped same-subscription dependencies. Cross-subscription/sovereign-cloud runtime bindings are not implemented.

Use explicit existing resource names and scope fields, derived from verified full ARM IDs. Foundry/storage/data sections optionally accept `existingResourceId` for the account or SQL **server** ARM ID; it must match explicit `mode=existing`, account/server name and subscription, and supplies the stage RG. A conflicting explicit stage RG fails. Database/project IDs are not accepted as account/server IDs. No discovery picks a subscription/name for you. Stable fallback names use `uniqueString(resourceGroup().id,prefix,environmentName)`; no time/random component. Pin generated names into the manifest before moving ownership. The example pins replaceable names so all later stages bind the same resources.

**Legacy migration:** old suffix is `uniqueString(resourceGroup().id,prefix)` (different from the new fallback). Inventory and retain its exact `st<suffix>`, `<prefix>-sql-<suffix>`, database, `acr<suffix>`, `<prefix>-logs`, `<prefix>-environment`, `<prefix>-web-mi`, `<prefix>-worker-mi`, `<prefix>-web`, `<prefix>-worker`. Set the corresponding stage name parameters explicitly. Existing OpenAI accounts must use Foundry `mode=existing, existingAccountKind=OpenAI, createProject=false, bindProject=false`. **Never convert OpenAI to AIServices.** New AIServices belongs to a separately authorized name if needed, not an accidental duplicate. Role assignment IDs differ in some legacy container grants: inventory them and review duplicate-effective access/ownership before enabling compute grants. These scripts never delete/reconcile old grants or omitted resources.

**Registry role GUID migration:** runtime pull assignments now use `guid(registry.id, principalId, role)`, not the UAMI resource ID. Recreating a UAMI at the same resource ID gives it a new principal and therefore a new assignment name, avoiding an attempt to mutate the old assignment's principal. Existing identity-ID-based grants have different GUIDs. Review current assignments, duplicate-effective access and stale principals before applying this change; any cleanup requires separate explicit review/authority. No automatic deletion or migration of old grants is performed.

`mode=create` owns and reapplies settings on repeats; `mode=existing` does not mutate account/storage/SQL security/SKU/admin. Foundry can explicitly create a child model/project on an existing compatible account. Foundation refuses location/tag replacement on an existing RG; select `createResourceGroup=false` to preserve its tags. RG and existing binding type/kind/name/region are checked by authenticated preflight. Newly created resources cannot prove endpoint/data-plane availability until deployed. Incremental mode never promises reconciliation/deletion of omitted child resources. Monitoring false, schedule false or workload false does **not** delete previously deployed resources; explicitly change/review existing owned settings for suspension, and do not assume that omitting an existing job stops it.

## Commands

Run from repository root in **Windows PowerShell 5.1** with Azure CLI and its local Bicep compiler already available. No wrapper logs in, changes active subscription, registers providers or obtains secrets. Provider availability/registration is a separately reviewed operator action, not an automatic helper. Every cloud CLI command includes `--subscription`.

```powershell
$subscription = '<actual-subscription-guid>'
$manifest = "$HOME\pii-private\manifest.json"
$secureApplication = "$HOME\pii-private\application.secure.json"

# LOCAL ONLY: compile + manifest type/enum/range/combination checks, no authentication.
./infra/scripts/deploy-foundation.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
./infra/scripts/deploy-foundry.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
./infra/scripts/deploy-storage.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
./infra/scripts/deploy-data.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
./infra/scripts/deploy-monitoring.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
./infra/scripts/deploy-compute.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
./infra/scripts/deploy-application.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline -SecureParametersPath $secureApplication
./infra/scripts/deploy-all.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate -Offline
```

Enable the selected stage in the manifest before invoking its wrapper. Default all only processes enabled foundation, Foundry, storage, data and monitoring, **never compute/application**, even if their sections are enabled. Monitoring is disabled in the example. Dependencies are separate ARM deployments, not a cloud transaction; earlier successful stages remain if a later stage fails. All cannot implicitly obtain/use SQL passwords; run opted-in password data separately with its protected file.

The following are **operator-only cloud commands**, NOT executed by local validation. Azure `Validate` uses ARM and existing-resource read-only preflight; `WhatIf` previews changes and may require elevated read/what-if rights. Apply can incur charges. First authenticate deliberately outside the wrappers, verify subscription/tenant/provider registration and approve the dated forecast. WhatIf writes a per-stage hash-only receipt under `$HOME\pii-private\receipts`; a single-stage wrapper can override `-WhatIfReceiptPath`. Apply requires that reviewed receipt plus both explicit switches and repeats the selected stage preview immediately before create.

```powershell
./infra/scripts/deploy-foundation.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf
./infra/scripts/deploy-foundry.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf
./infra/scripts/deploy-storage.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf
./infra/scripts/deploy-data.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf
./infra/scripts/deploy-monitoring.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf
./infra/scripts/deploy-compute.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf
./infra/scripts/deploy-application.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf -SecureParametersPath $secureApplication
./infra/scripts/deploy-all.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action WhatIf

# Use any corresponding stage wrapper with Action Validate for authenticated ARM validation.
./infra/scripts/deploy-data.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Validate
# Only after that exact stage, parameters, scope, immutable images and forecast are reviewed:
./infra/scripts/deploy-data.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Apply -AuthorizeApply -AcknowledgeCost
./infra/scripts/deploy-application.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Apply -AuthorizeApply -AcknowledgeCost -SecureParametersPath $secureApplication
./infra/scripts/deploy-all.ps1 -SubscriptionId $subscription -ManifestPath $manifest -Action Apply -AuthorizeApply -AcknowledgeCost
```

For each other stage, the exact Apply invocation is the shown WhatIf command with `-Action Apply -AuthorizeApply -AcknowledgeCost`, after its WhatIf receipt is reviewed. The shared helper suppresses deployment outputs and displays only resource IDs/change types. Full ARM changes are captured in memory and canonicalized/hashed, **never printed or archived**; receipts store only stage/subscription/input/change hashes. Apply rejects changed manifest/secure-file/template inputs, changed property-level previews and Delete changes. Protect reviewed files/receipts, inspect intended settings privately and do not edit a receipt to bypass drift. Unknown/volatile ARM preview changes may require a fresh reviewed WhatIf; this cannot eliminate changes between preview and create or turn multiple stages into a transaction. Receipts hash the full manifest, so editing any section intentionally requires renewed previews. There is no automatic pipeline migration; [the existing pipeline](../azure-pipelines.yml) remains on main and retains its own approval/drift gates.

Raw CLI equivalents for a private stage parameter file (use actual names/scopes; these bypass wrapper safeguards):

```powershell
az deployment sub what-if --subscription $subscription --name pii-dev-foundation --location northeurope --template-file infra/stages/foundation.bicep --parameters "@$HOME\pii-private\foundation.parameters.json" --result-format ResourceIdOnly
az deployment group what-if --subscription $subscription --resource-group pii-dev-rg --name pii-dev-foundry --template-file infra/stages/foundry.bicep --parameters "@$HOME\pii-private\foundry.parameters.json" --mode Incremental --result-format ResourceIdOnly
az deployment group what-if --subscription $subscription --resource-group pii-dev-rg --name pii-dev-storage --template-file infra/stages/storage.bicep --parameters "@$HOME\pii-private\storage.parameters.json" --mode Incremental --result-format ResourceIdOnly
az deployment group what-if --subscription $subscription --resource-group pii-dev-rg --name pii-dev-data --template-file infra/stages/data.bicep --parameters "@$HOME\pii-private\data.parameters.json" --mode Incremental --result-format ResourceIdOnly
az deployment group what-if --subscription $subscription --resource-group pii-dev-rg --name pii-dev-monitoring --template-file infra/stages/monitoring.bicep --parameters "@$HOME\pii-private\monitoring.parameters.json" --mode Incremental --result-format ResourceIdOnly
az deployment group what-if --subscription $subscription --resource-group pii-dev-rg --name pii-dev-compute --template-file infra/stages/compute.bicep --parameters "@$HOME\pii-private\compute.parameters.json" --mode Incremental --result-format ResourceIdOnly
az deployment group what-if --subscription $subscription --resource-group pii-dev-rg --name pii-dev-application --template-file infra/stages/application.bicep --parameters "@$HOME\pii-private\application.parameters.json" "@$secureApplication" --mode Incremental --result-format ResourceIdOnly
```

For authenticated Validate replace `what-if` with `validate` and remove `--result-format`; for authorized Apply replace it with `create`, remove that flag and add `--output none`. Subscription scope is implicitly incremental. Reviewed [per-stage Bicep parameter examples](parameters/foundation.example.bicepparam) compile with `az bicep build-params`; they are independent alternatives to the shared manifest, not automatic source-of-truth merges.

### Safe Output Retrieval

After an operator-authorized deployment, retrieve **only** `properties.outputs`, never the whole deployment or its parameters. All staged outputs are nonsecret resource identifiers/endpoints and stage metadata, not connection strings, account keys, passwords, tokens or provider credentials. Monitoring outputs contain IDs/enabled state only; obtain ingestion configuration separately with an approved tool into protected local configuration, never public logs. These read-only commands are operator examples, not executed verification:

```powershell
$resourceGroup = '<actual-stage-resource-group>'
# Use actual prefix/environment deployment names and each stage's selected RG.
az deployment sub show --subscription $subscription --name pii-dev-foundation --query properties.outputs --output json
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-foundry --query properties.outputs --output json
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-storage --query properties.outputs --output json
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-data --query properties.outputs --output json
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-monitoring --query properties.outputs --output json
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-compute --query properties.outputs --output json
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-application --query properties.outputs --output json
# Optional access stage uses its explicitly selected deployment name.
az deployment group show --subscription $subscription --resource-group $resourceGroup --name pii-dev-developer-access --query properties.outputs --output json
```

## Configuration And Gates

- Foundry: stable `2025-06-01`, new AIServices/project, model deployment default OFF. Standard/GlobalStandard/DataZoneStandard capacity 1-100; no PTU. Verify region/model/version/quota/geography/tool/strict-schema support separately. Runtime Azure.AI.OpenAI SDK API is **2024-10-21** and requires `.openai.azure.com/` root, not `.services.ai.azure.com/api/projects/...` or `/openai/v1`. Project ID is informational for this custom logical-agent runtime.
- Storage: allowlisted Standard LRS/ZRS/GRS/RAGRS/GZRS/RAGZRS and Hot/Cool; no Premium/Archive. Shared-key access OFF, explicit `developmentSharedKeyOptIn` only with `environmentName=dev`; no automatic key extraction. Existing storage must be private HTTPS with appropriate auth; container existence/privacy checked before cloud actions. The main-owner Development fix is implemented: explicit `Pii:Blob:ServiceUri` uses DefaultAzureCredential, simultaneous URI/connection-string settings fail, Azure failures never fall back to Azurite, and ordinary runtime does not create Azure containers. Application `blobServiceUri` must be an HTTPS account root; `keyRingUri` must be an HTTPS `.xml` blob path; neither accepts query/fragment.
- SQL: EF/native SQL only. Serverless GP_S_Gen5 defaults min0.5/max1/autoPause60/2GiB/Local backups/7-day retention; max1/2/4, min0.5/1, pause allowlist, storage1-32. Optional `computeModel=Basic` uses **Basic tier, 5 DTU, max2GiB**, excludes serverless properties. DTU is fixed billed capacity, not auto-pause. Region/SKU/backup support still requires ARM validation. Entra admin default; `developmentPasswordOptIn` only dev plus `@secure sqlAdministratorPassword` from a separate protected data-stage file. No SQL connection/password output. No automatic database DDL/user/grant/firewall discovery.
- Monitoring: optional/off. Workspace retention30/60/90, daily quota0.1/0.25/0.5/1GiB, Insights sampling1-100 (default10). Main-owner optional host telemetry is implemented with `Pii:Telemetry:Enabled` and protected `APPLICATIONINSIGHTS_CONNECTION_STRING`: health events only, not automatic request/dependency tracing. Worker flush is bounded to two seconds; disabled telemetry performs no exporter network calls. No SQL text, private routes, URLs, prompts, provider keys or payloads are exported. Local tests are not a live telemetry capture/redaction claim. Connection configuration is NOT output. Obtain it manually into a protected local file using an approved operator tool. Connection-string ingestion compatibility is enabled; the instrumentation identifier is not a data-reading credential but must not be published. No synthetic tests/diagnostics/alerts by default. Compute optionally reuses the workspace internally; listKeys for environment log configuration stays inside ARM, never output. Disabling logging on a **new** environment selects `none`; changing existing logging requires explicit ownership/review, not environment recreation.
- Compute: Consumption only; registry Basic default with reviewed Standard/Premium options. Distinct UAMIs; registry pull, documents both, keys web-only, inference worker-only if explicitly granted. No image build/push, web app or worker job. Existing runtime SQL users are manually bootstrapped after UAMI creation. No automatic registry push grant is introduced.
- Application: existing environment/registry/UAMIs only. CPU0.25 through2 in0.25 increments maps to memory2GiB/vCPU, web min0/max1, capped min2/max5, HTTP concurrency1-100. Finite job batch/parallelism1/retry0, timeout900-3600 with startup allowance. `deployWorkloads=false`, `scheduleEnabled=false`; enabled workloads create a Manual job until scheduling is authorized. Minute UTC schedule retains SQL polling behavior and can keep SQL awake. No worker execution/start command is called. Both enabled image hosts must match `registryName.azurecr.io` offline and the selected registry's actual `loginServer` in cloud preflight, with immutable SHA256 digests. No model/provider keys in web; production inference uses DAC/MI only. Secrets use ACA secretRefs/vault references; optional health-event telemetry is implemented but live delivery/privacy remains NotRun.

## Local Integration And Application Release

Follow [the exact local guide](local/README.md) before hosted compute. Schema bootstrap, additive upgrade and developer/runtime contained users are separate reviewed SQL actions, not ARM side effects. Entra developer User/Group object IDs differ from future UAMI client IDs. Use the [existing deployment guide](../docs/operations/deployment.md) for schema, rights, recovery and acceptance detail.

Main-owner host configuration changes are implemented. In Development, web loads user-secrets ID `pii-web-development`; worker explicitly loads `pii-worker-development`, with environment configuration taking precedence in both hosts. Raw `PII_AZURE_OPENAI_API_KEY` and `PII_TWELVEDATA_API_KEY` names resolve through `IConfiguration` and remain worker-only. Main owner reports **275 full application tests passing without Azure**; these were not rerun in this infra-only repair and may increase. Static infra checks and actual live NotRun results remain separate.

The actual SDK is **10.0.401**, not 8.x or an assumed installed version. Existing root Dockerfiles build .NET10 and use ASP.NET10 runtime (worker needs Microsoft.AspNetCore.App). Image build/push is a separate operator/CI release activity after compute. Never use `latest` or local image ID as ACR manifest digest:

```powershell
dotnet --version
docker build --file Dockerfile.web --build-arg SDK_VERSION=10.0.401 --tag <acr-login>/pii/web:<immutable-build-id> .
docker build --file Dockerfile.worker --build-arg SDK_VERSION=10.0.401 --tag <acr-login>/pii/worker:<immutable-build-id> .
# Operator-authorized registry authentication/push only, never called by wrappers:
az acr login --subscription $subscription --name <registry-name>
docker push <acr-login>/pii/web:<immutable-build-id>
docker push <acr-login>/pii/worker:<immutable-build-id>
az acr repository show --subscription $subscription --name <registry-name> --image pii/web:<immutable-build-id> --query digest --output tsv
az acr repository show --subscription $subscription --name <registry-name> --image pii/worker:<immutable-build-id> --query digest --output tsv
```

Set manifest `webImage`/`workerImage` to `<acr-login>/...@sha256:<verified-digest>`. Enable application only after SQL schema/DML users, Blob/registry access and owner auth are verified. First compute output gives the domain: configure the existing Entra registration redirect `https://<web-name>.<domain>/signin-oidc` and signed-out callback before exposing workloads. Persist web keyring and match tenant/object allowlist. Roll back image/config to reviewed compatible digests, never drop/restore a ledger over newer history. Stop scheduling via an explicitly applied Manual trigger, drain runs and disable discretionary calls before maintenance; false omission flags do not stop already deployed resources.

## Validation And Cost

Both hosts now have Development user-secrets support and explicit Azure Blob selection. The telemetry SDK is Microsoft.ApplicationInsights 2.23.0, with manual health events only and no automatic request/dependency instrumentation. Environment configuration overrides worker user secrets. Disabled monitoring creates no exporter; enabled telemetry strips private context and the finite worker allows at most two seconds for SDK flush. Offline recording-channel tests validate the health-field contract, not real Azure ingestion or end-to-end flush delivery.

```powershell
./infra/validation/Test-ModularStages.ps1
./infra/validation/Test-DeploymentWrappers.ps1
./infra/validation/Test-LocalIntegrationTemplates.ps1
./infra/validation/Test-IaC.ps1
./infra/validation/Test-ParameterGates.ps1
./infra/validation/Test-Documentation.ps1
```

Local compiler0.48.1 validates syntax/type/lint, not regional ARM acceptance, Foundry inference/root availability, actual Entra/RBAC, SQL/Blob network, Docker Linux runtime, telemetry sanitization or deployment idempotence. [Readiness](../docs/operations/readiness.md) distinguishes these NotRun checks. **USD100/month is a planning ceiling, not a guarantee**. Price dated regional SQL active hours/DTU, all agent-loop tokens, retained Blob versions/transactions/egress, optional logs, ACR/CI, worker startup/polling/overlap, tax and no-grant cases using [the cost worksheet](../docs/operations/cost-model.md). Budget alerts are not hard caps. No auto budget resource/contact/action is created.