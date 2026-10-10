using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pii.Application;
using Pii.Infrastructure;
using Pii.Worker;

try
{
    var command = WorkerCommand.Parse(args);
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Services.AddPiiInfrastructure(builder.Configuration, builder.Environment.IsDevelopment(), worker: true);
    builder.Services.AddTransient<ImportCoordinator>();
    builder.Services.AddTransient<RunCoordinator>();
    builder.Services.AddTransient<InvestmentPipeline>();
    using var host = builder.Build();
    using var cancellation = new CancellationTokenSource();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    using var stopping = lifetime.ApplicationStopping.Register(cancellation.Cancel);
    await host.StartAsync(cancellation.Token);
    var store = host.Services.GetRequiredService<IWorkspaceStore>();
    var runtime = host.Services.GetRequiredService<RuntimeConfiguration>();
    var coordinator = host.Services.GetRequiredService<RunCoordinator>();
    var pipeline = host.Services.GetRequiredService<InvestmentPipeline>();
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pii.Worker");
    await store.InitializeAsync(cancellation.Token);
    if (command.FixtureScenario)
    {
        if (!builder.Environment.IsDevelopment() || runtime.Mode != ExecutionMode.FixtureReplay)
            throw new InvalidOperationException("Fixture scenario requires explicit Development and FixtureReplay; production cannot seed synthetic holdings.");
        var portfolio = await store.GetPortfolioAsync(cancellation.Token);
        if (portfolio.Revision != 0 || portfolio.Transactions.Count != 0)
            throw new InvalidOperationException("Fixture scenario requires an empty isolated workspace. Use explicit import review for an existing portfolio.");
        const string csv = """
            id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio
            synthetic-buy-1,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,10,1000,2,0.9,,
            synthetic-sell-1,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-02T10:00:00Z,Sell,2,240,1,0.92,,
            """;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv + "\n"));
        var preview = await host.Services.GetRequiredService<ImportCoordinator>().PreviewAsync(input, "synthetic-sample-v1.csv", cancellation.Token);
        if (preview.Issues.Count != 0) throw new InvalidDataException("Fixture import has blocking issues.");
        await store.ConfirmAsync(preview.Id, preview.BaselineRevision, cancellation.Token);
    }
    if (command.Daily)
    {
        var key = DailySchedule.DueKey(DateTimeOffset.UtcNow, builder.Configuration.GetValue("Pii:ScheduleVersion", 1));
        if (key is not null && (await store.GetPortfolioAsync(cancellation.Token)).Revision > 0)
            await coordinator.SubmitAsync(key, cancellation.Token);
        else logger.LogInformation("Daily submission skipped: not due or no confirmed revision. Pending work is still consumed.");
    }
    var maximumBatch = Math.Clamp(builder.Configuration.GetValue("Pii:Worker:MaximumBatch", 3), 1, 10);
    var owner = Guid.NewGuid().ToString("N");
    var failed = false;
    for (var index = 0; index < maximumBatch && !cancellation.IsCancellationRequested; index++)
    {
        var run = await store.ClaimAsync(owner, DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(Math.Clamp(runtime.Limits.MaximumRunMinutes + 1, 2, 15)), cancellation.Token);
        if (run is null) break;
        try { await pipeline.ExecuteAsync(run, owner, cancellation.Token); }
        catch (Exception exception)
        {
            failed = true;
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var current = await store.GetRunAsync(run.Id, persistenceTimeout.Token);
                if (current is not null && current.Owner == owner && current.Generation == run.Generation
                    && current.Status is RunStatus.Running or RunStatus.Pending)
                {
                    current.Status = exception is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed;
                    current.AiStatus = "WorkerInterrupted";
                    current.FinishedAt = DateTimeOffset.UtcNow;
                    current.Errors.Add(exception is OperationCanceledException ? "Worker cancelled before completion." : "Worker execution failed; persisted stage results retained.");
                    await store.SaveRunAsync(current, owner, run.Generation, persistenceTimeout.Token);
                }
            }
            catch (Exception) { logger.LogError("Unable to checkpoint interrupted run {RunId}; expiring claim permits recovery.", run.Id); }
            logger.LogError("Worker interrupted run {RunId}; no provider payloads or originals are logged.", run.Id);
            if (exception is OperationCanceledException) break;
        }
    }
    var exitCode = cancellation.IsCancellationRequested ? 130 : failed ? 1 : 0;
    await host.StopAsync();
    return exitCode;
}
catch (OperationCanceledException) { return 130; }
catch (Exception exception)
{
    Console.Error.WriteLine($"Worker startup/operation unavailable ({exception.GetType().Name}). Check secure configuration, local SQL/Blob services, or supported command flags. No exception payload is logged.");
    return 1;
}