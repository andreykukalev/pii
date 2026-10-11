# Personal Investment Intelligence

Private, single-user investment research workspace on .NET 10. Financial calculations are deterministic EUR economic FIFO; investment research uses bounded logical agents through Azure OpenAI/Foundry. All trades remain manual. No brokerage credentials, order APIs, execution tools or tax-compliance claims.

## Run Locally

Prerequisites: SDK 10.0.401, SQL Server LocalDB `MSSQLLocalDB` (or a configured SQL Server), Node/npm for Azurite. Docker is not required for Windows LocalDB development.

```powershell
npx --yes --package azurite@3.35.0 azurite --silent --location .local/azurite --blobHost 127.0.0.1 --queueHost 127.0.0.1 --tableHost 127.0.0.1
```

In another terminal:

```powershell
dotnet test Pii.slnx -m:1
dotnet run --project src/Pii.Web
```

Open `http://127.0.0.1:5080`. Development uses an explicit loopback-only identity, isolated LocalDB `PiiDevelopment`, private Azurite storage and **FixtureReplay**, not live AI or market evidence. Download the sample in Imports, upload, inspect and confirm it. Then run the finite worker:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Pii.Worker -- --once
```

Reload Briefs and Runs to inspect holdings/outside research, long-term/tactical findings, critic follow-up, gates, evidence, task/tool/model traces and usage. To exercise an empty isolated workspace without browser import:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Pii.Worker -- --fixture-scenario
```

The fixture command refuses a nonempty workspace and cannot run in production. Normal workers exit after a finite batch; they do not continuously watch. Local on-demand requests remain durable until you run the worker. Azure definitions use finite scheduled executions for dispatch and daily due-time checks, not web background processing.

## Real Model And Sources

For Azure provisioning and local execution against Azure dependencies, start with [infra/README.md](infra/README.md) and [the local integration guide](infra/local/README.md). [The existing central configuration manifest](infra/parameters/manifest.json) exclusively owns all nonsecret deployment settings, including root `subscriptionId` and `shared.auth.tenantId`. Every wrapper requires explicit `-ManifestPath`; subscription CLI overrides and environment selectors are not supported. Fill the tracked placeholders in the selected file; do not create a competing environment file. Review and apply each stage against that same file. [Local templates](infra/parameters/local/appsettings.Development.json) are not automatically loaded. Independent Foundry, storage, SQL and optional monitoring stages do not create compute. Hosting infrastructure and actual image deployment are separate later stages; compute never starts workloads. [Azure DevOps](azure-pipelines.yml) uses the same staged wrappers and central secure-file manifest, verifies WIF scope against its subscription/tenant, and defaults deployment off with one explicitly selected stage per approved run. No combined deployment path remains.

Both hosts support Development user secrets. The worker loads secrets before environment overrides; Azure/source keys remain worker-only. Optional Application Insights emits health events only, not private request paths, SQL statements or model/dependency payloads. Monitoring remains disabled unless explicitly configured.

The actual SDK adapter executes sequential tool calls and strict structured results. `ModelOnFixtures` uses real configured inference on permitted synthetic evidence; `Live` requires a verified real portfolio, permitted source data and secure model configuration. Neither is enabled by default. Missing access is explicit Unavailable/ConfigurationError; live failures never switch to fixtures.

Follow [deployment](docs/operations/deployment.md), [source boundaries](docs/architecture/data-sources.md) and [readiness](docs/operations/readiness.md). Model/source credentials belong only to the worker. Supply secrets outside chat and source control. Existing suitable Foundry deployments can be bound; creating resources requires authority and a verified after-tax forecast within USD100 total/month. No Azure resources have been provisioned by this implementation.

## Structure

- [Domain](src/Pii.Domain/Portfolio.cs): FIFO, dated FX, fees, unknown history and deterministic eligibility gates.
- [Application](src/Pii.Application/InvestmentPipeline.cs): implemented orchestration, roles, tool dispatcher, evidence validation, budget/checkpoints and brief.
- [Infrastructure](src/Pii.Infrastructure/InfrastructureRegistration.cs): SQL transaction/claims, private Blob, actual Foundry and source adapters, explicit development transports.
- [Web](src/Pii.Web/README.md): authenticated Razor Pages investment workspace.
- [Worker](src/Pii.Worker/README.md): shared finite on-demand/daily pipeline.
- [Documentation](docs/README.md): architecture, accepted boundaries, decisions, operations and manual tests.

Current parser supports documented **PII-Sample-v1**, not verified Revolut CSV/PDF. Cash/performance, advanced corporate actions, live source coverage and production activation have explicit limitations in readiness. Unconfigured investment policy never authorizes sizing or suitability. Fixture success proves workflow/control behavior, not live investment analysis or profitability.# pii