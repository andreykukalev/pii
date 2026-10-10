# Documentation

Personal Investment Intelligence is a private, single-user advisory workspace. Trades remain manual in Revolut. The executable research platform is required, but fixtures, local tests, actual statement formats, live research and Azure operation are separate evidence claims.

Start with [requirements](product/requirements.md), [architecture](architecture/overview.md), and [acceptance](product/roadmap-and-acceptance.md). The [original-source notice](product/initial-specification.md) records why no faithful original-specification archive is available.

Local infrastructure checks, without Azure login or model calls:

```powershell
./infra/validation/Test-IaC.ps1
./infra/validation/Test-ParameterGates.ps1
./infra/validation/Test-TrackedSecrets.ps1
./infra/validation/Test-Documentation.ps1
./infra/validation/Test-SchemaBootstrap.ps1
```

Infrastructure entry point: [main Bicep](../infra/main.bicep). [Parameter example](../infra/parameters.example.json) is deliberately invalid until real authorized identity/location inputs replace placeholders; all activation flags default off. Compiling a template is not deployment authority or production readiness.

## Design

- [Agent platform](architecture/agent-platform.md): actual roles, communication/tools, limits, model binding and modes.
- [Portfolio ingestion](architecture/portfolio-ingestion.md) and [data model](architecture/data-model.md): sample-only format, atomic SQL publication and quality gaps.
- [Data sources](architecture/data-sources.md), [recommendations/risk](architecture/recommendations-and-risk.md), [history/evaluation](architecture/history-and-evaluation.md): rights, bounded coverage, gates and replay limits.
- [Daily brief](product/daily-brief.md), [ADR index](adr/README.md): report/schedule contract and selected SQL/minute-poll decisions.

## Operations

- [Deployment](operations/deployment.md): isolated local launch, secure parameter/identity/schema bootstrap and WIF release.
- [Security/retention](operations/security-and-retention.md) and [cost model](operations/cost-model.md): permissions, manual deletion/restore and active-minimum SQL forecast.
- [Manual testing](operations/manual-testing.md) and [readiness](operations/readiness.md): executed checks, source blockers and NotRun integrations.

The schema check creates/removes a uniquely named disposable **local** SQL database; it does not connect to Azure or execute Entra user creation. Dockerfile checks are static until actual Docker/CI execution; local Linux-target publish is separate evidence.

## File Inventory

This ownership slice contains four root deployment files (two Dockerfiles, Docker ignore and Azure DevOps YAML), thirteen infrastructure files (main, three modules, one placeholder parameter example, two SQL bootstrap files and six validation scripts), and the twenty documents linked here. Source/tests/root README/SDK/build props/gitignore/solution remain owned by the main implementation; no manual edits to those paths were made by this task.