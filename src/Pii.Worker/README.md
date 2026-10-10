# Finite Worker

.NET 10 console executable with Microsoft.AspNetCore.App framework reference. Registers Infrastructure with `worker:true`, then Application InvestmentPipeline and RunCoordinator. Sources/model credentials and IModelGateway are worker-only. Uses the same actual pipeline for import, on-demand and scheduled work.

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/Pii.Worker -- --once
dotnet run --project src/Pii.Worker -- --daily
```

`--once` (or no flag) consumes a finite batch, default 3, configured by `Pii:Worker:MaximumBatch` and hard-clamped to 1..10. It exits when no claim is available or the batch is exhausted. No watch/forever loop, web background execution or cloud job-start privilege. Configure the same isolated `Pii:Sql:ConnectionString` and `Pii:Blob:ConnectionString`/container as the web. Development defaults are LocalDB/Azurite and explicit FixtureReplay; no provider/model cloud calls in FixtureReplay.

`--daily` first checks actual current time in Europe/Vilnius. Before 08:00 it skips the daily submission but still consumes pending work. At/after 08:00 it submits durable `daily:vilnius:yyyy-MM-dd:v1` (`Pii:ScheduleVersion`, positive integer) once per local date using store idempotency, provided a confirmed revision exists. Both summer/winter UTC candidate invocations map to this same key. UTC cron `0 5,6 * * *` is a candidate daily schedule, not an exact completion SLA. Delayed starts retain actual cutoff/time; previous-day reports are not backdated with future information. On-demand work requires a finite scheduled invocation or manual `--once`; a once-daily schedule alone does not guarantee within-minutes start.

Explicit `--fixture-scenario` optionally seeds the documented two-row sample only in Development + FixtureReplay + an empty isolated workspace, through the real import coordinator and explicit store confirmation. It is forbidden in production and does not overwrite an existing ledger. Ordinary `--once`/`--daily` do not seed.

Store is initialized before operations. Claims use a bounded lifetime (run maximum + 1 minute, at most 15); persistence applies owner/generation fencing. Interruptions preserve existing run budgets/counters/results and attempt to persist a sanitized terminal status under current ownership. Lost claims are left for the store's bounded expiry recovery. Console/host shutdown is propagated through the same cancellation token. Exit 0 means the invocation completed, not necessarily successful AI; inspect separate run/AI/status results. Exit 1 means startup or caught execution failed, 130 means cancellation.

Production requires actual Live configuration, SQL/Blob credentials/managed identities, model/source rights/binding/rates and scheduled invocations as documented by Infrastructure. No cloud resources or paid subscriptions are provisioned by this executable.