using Microsoft.EntityFrameworkCore;
using Pii.Application;
using Pii.Domain;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class SqlWorkspaceTests : IAsyncLifetime
{
    private readonly TestDatabaseFactory factory = new();
    private SqlWorkspaceStore Store => new(factory, new(ExecutionMode.FixtureReplay, new(), SyntheticResearchFixture.Catalog, "fixture-test", false, "test"));
    private static CancellationToken Cancellation => CancellationToken.None;
    public Task InitializeAsync() => Store.InitializeAsync(Cancellation);
    public async Task DisposeAsync()
    {
        await using var database = factory.CreateDbContext();
        await database.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task ConfirmationIsAtomicIdempotentAndRevisionPinned()
    {
        Assert.Empty((await Store.GetPortfolioAsync(Cancellation)).Transactions);
        var preview = Preview("first", 0) with
        {
            Snapshot = new(DateTimeOffset.UtcNow, [], [], "independent-snapshot"),
            Reconciliation = new(0, DateTimeOffset.UtcNow, [])
        };
        await Store.SavePreviewAsync(preview, Cancellation);
        await Store.SavePreviewAsync(preview, Cancellation);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SavePreviewAsync(preview with { DuplicateCount = 1 }, Cancellation));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SavePreviewAsync(preview with { Id = "hash-duplicate" }, Cancellation));
        var first = await Store.ConfirmAsync(preview.Id, 0, Cancellation);
        Assert.Equal(JsonDefaults.Serialize(first), JsonDefaults.Serialize(await Store.ConfirmAsync(preview.Id, 0, Cancellation)));
        Assert.Equal(1, first.Revision);
        var runs = await Store.ListRunsAsync(Cancellation);
        var pending = Assert.Single(runs);
        Assert.Equal("confirm:first", pending.IdempotencyKey);
        Assert.Equal(1, pending.Portfolio!.Revision);
        Assert.Equal(JsonDefaults.Serialize(preview.Snapshot), JsonDefaults.Serialize(pending.Portfolio.Snapshot));
        Assert.Equal(JsonDefaults.Serialize(preview.Reconciliation), JsonDefaults.Serialize((await Store.GetPortfolioAsync(Cancellation)).Reconciliation));
        Assert.Equal(RunStatus.Pending, pending.Status);
        await using (var database = factory.CreateDbContext()) Assert.Equal(2, await database.LedgerIdentities.CountAsync());
        await Store.SavePreviewAsync(Preview("stale", 0), Cancellation);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.ConfirmAsync("stale", 0, Cancellation));
        await Store.SavePreviewAsync(Preview("second", 1), Cancellation);
        await Store.ConfirmAsync("second", 1, Cancellation);
        var created = await Store.CreateRunAsync(new() { IdempotencyKey = "manual", Revision = 1, Mode = ExecutionMode.Live, Decisions = 999 }, Cancellation);
        Assert.Equal(1, created.Revision);
        Assert.Equal(1, created.Portfolio!.Revision);
        Assert.Equal(0, created.Decisions);
        Assert.Equal(ExecutionMode.FixtureReplay, created.Mode);
        var replay = await Store.CreateRunAsync(new() { IdempotencyKey = "manual", Revision = 1 }, Cancellation);
        Assert.Equal(created.Id, replay.Id);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.CreateRunAsync(new() { IdempotencyKey = "manual", Revision = 2 }, Cancellation));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.CreateRunAsync(new() { Id = created.Id, IdempotencyKey = "manual", Revision = 2 }, Cancellation));
        await using var final = factory.CreateDbContext();
        Assert.Equal(2, await final.Revisions.CountAsync());
        Assert.Equal(3, await final.Runs.CountAsync());
    }

    [Fact]
    public async Task FirstWorkerClaimPinsBindingAndRecoveryDoesNotReplaceIt()
    {
        await Store.SavePreviewAsync(Preview("binding", 0), Cancellation);
        await Store.ConfirmAsync("binding", 0, Cancellation);
        Assert.Equal("PendingWorkerBinding", Assert.Single(await Store.ListRunsAsync(Cancellation)).ModelBinding);
        var worker = new SqlWorkspaceStore(factory, new(ExecutionMode.FixtureReplay, new(), SyntheticResearchFixture.Catalog, "worker-model", false, "test"));
        var run = (await worker.ClaimAsync("binding-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation))!;
        Assert.Equal("worker-model", run.ModelBinding);
        await ExpireAsync(run.Id);
        var changedWorker = new SqlWorkspaceStore(factory, new(ExecutionMode.FixtureReplay, new(), SyntheticResearchFixture.Catalog, "changed-model", false, "test"));
        var recovered = (await changedWorker.ClaimAsync("recovery", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation))!;
        Assert.Equal("worker-model", recovered.ModelBinding);
    }

    [Fact]
    public async Task ConcurrentConfirmAndClaimsHaveOneWinnerAndCancellationSurvivesCheckpoint()
    {
        await Store.SavePreviewAsync(Preview("one", 0), Cancellation);
        var confirmations = await Task.WhenAll(Store.ConfirmAsync("one", 0, Cancellation), Store.ConfirmAsync("one", 0, Cancellation));
        Assert.All(confirmations, revision => Assert.Equal(1, revision.Revision));
        var claims = await Task.WhenAll(Store.ClaimAsync("worker-a", DateTimeOffset.MinValue, TimeSpan.FromMinutes(5), Cancellation),
            Store.ClaimAsync("worker-b", DateTimeOffset.MaxValue, TimeSpan.FromMinutes(5), Cancellation));
        var run = Assert.Single(claims, claim => claim is not null)!;
        Assert.Equal(1, run.Generation);
        Assert.True(run.ClaimExpires > DateTimeOffset.UtcNow);
        var stale = JsonDefaults.Parse<AnalysisRun>(JsonDefaults.Serialize(run));
        run.Decisions++;
        run.ReservedUsd = .15m;
        await Store.SaveRunAsync(run, run.Owner!, run.Generation, Cancellation);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(stale, stale.Owner!, stale.Generation, Cancellation));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(run, "wrong-owner", run.Generation, Cancellation));
        await Store.CancelAsync(run.Id, Cancellation);
        var owner = run.Owner!;
        await Store.SaveRunAsync(run, owner, run.Generation, Cancellation);
        var cancelled = (await Store.GetRunAsync(run.Id, Cancellation))!;
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CancelRequested);
        Assert.Null(cancelled.Owner);
        Assert.Null(cancelled.ReportBlob);
        Assert.Equal(.15m, cancelled.ReservedUsd);
    }

    [Fact]
    public async Task RecoveryPreservesCountersAndStopsAfterThreeClaims()
    {
        await Store.SavePreviewAsync(Preview("recovery", 0), Cancellation);
        await Store.ConfirmAsync("recovery", 0, Cancellation);
        AnalysisRun? original = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var run = (await Store.ClaimAsync("worker-" + attempt, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation))!;
            Assert.Equal(attempt, run.Generation);
            Assert.Equal(attempt - 1, run.ModelSubmissions);
            original ??= JsonDefaults.Parse<AnalysisRun>(JsonDefaults.Serialize(run));
            run.ModelSubmissions++;
            run.ReservedTokens += 12000;
            run.ReservedUsd += .15m;
            await Store.SaveRunAsync(run, run.Owner!, attempt, Cancellation);
            await ExpireAsync(run.Id);
            await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(run, run.Owner!, attempt, Cancellation));
        }
        Assert.Null(await Store.ClaimAsync("fourth", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation));
        var stopped = (await Store.GetRunAsync(original!.Id, Cancellation))!;
        Assert.Equal(RunStatus.Failed, stopped.Status);
        Assert.Equal(3, stopped.ModelSubmissions);
        Assert.Equal(36000, stopped.ReservedTokens);
        Assert.Equal(.45m, stopped.ReservedUsd);
        Assert.Equal(original.Deadline, stopped.Deadline);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(original, original.Owner!, 1, Cancellation));
    }

    [Fact]
    public async Task MonthlyReservationsAreAtomicIdempotentAndSurviveDeletion()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Store.ReserveMonthlyAsync("reservation-" + index, .2m, 1m, DateTimeOffset.MinValue, Cancellation)));
        Assert.Equal(5, outcomes.Count(accepted => accepted));
        var winner = Array.FindIndex(outcomes, accepted => accepted);
        Assert.True(await Store.ReserveMonthlyAsync("reservation-" + winner, .2m, 1m, DateTimeOffset.MaxValue, Cancellation));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.ReserveMonthlyAsync("reservation-" + winner, .3m, 1m, DateTimeOffset.UtcNow, Cancellation));
        await Store.SavePreviewAsync(Preview("delete", 0), Cancellation);
        await Store.ConfirmAsync("delete", 0, Cancellation);
        await Store.DeletePortfolioAsync(Cancellation);
        Assert.Empty((await Store.GetPortfolioAsync(Cancellation)).Transactions);
        Assert.Empty(await Store.ListRunsAsync(Cancellation));
        Assert.False(await Store.ReserveMonthlyAsync("after-delete", .01m, 1m, DateTimeOffset.UtcNow, Cancellation));
        await Store.SavePreviewAsync(Preview("new", 0), Cancellation);
        Assert.Equal(2, (await Store.ConfirmAsync("new", 0, Cancellation)).Revision);
        await using var database = factory.CreateDbContext();
        Assert.Equal(1m, (await database.Months.SingleAsync()).TotalUsd);
        Assert.Equal(5, await database.Reservations.CountAsync());
    }

    [Fact]
    public async Task IncompletePublicationAndCounterResetAreRejected()
    {
        await Store.SavePreviewAsync(Preview("publish", 0), Cancellation);
        await Store.ConfirmAsync("publish", 0, Cancellation);
        var run = (await Store.ClaimAsync("publisher", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation))!;
        run.ModelSubmissions = 1;
        await Store.SaveRunAsync(run, "publisher", run.Generation, Cancellation);
        run.ModelSubmissions = 0;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(run, "publisher", run.Generation, Cancellation));
        run.ModelSubmissions = 1;
        run.Status = RunStatus.Completed;
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(run, "publisher", run.Generation, Cancellation));
        Assert.Equal(RunStatus.Running, (await Store.GetRunAsync(run.Id, Cancellation))!.Status);
    }

    private async Task ExpireAsync(string id)
    {
        await using var database = factory.CreateDbContext();
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE AnalysisRuns SET ClaimExpires = DATEADD(second, -1, SYSUTCDATETIME()) WHERE Id = {id}");
    }

    [Fact]
    public async Task ExhaustedWorkDoesNotHideNextPendingRun()
    {
        await Store.SavePreviewAsync(Preview("exhausted", 0), Cancellation);
        await Store.ConfirmAsync("exhausted", 0, Cancellation);
        var first = Assert.Single(await Store.ListRunsAsync(Cancellation));
        var next = await Store.CreateRunAsync(new() { IdempotencyKey = "next", Revision = 1 }, Cancellation);
        await using (var database = factory.CreateDbContext())
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE AnalysisRuns SET Generation = 1, Status = {(int)RunStatus.Running}, ClaimExpires = DATEADD(second, -1, SYSUTCDATETIME()), Deadline = DATEADD(second, -1, SYSUTCDATETIME()) WHERE Id = {first.Id}");
        var claimed = await Store.ClaimAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation);
        Assert.NotNull(claimed);
        Assert.Equal(next.Id, claimed.Id);
        Assert.Equal(RunStatus.Failed, (await Store.GetRunAsync(first.Id, Cancellation))!.Status);
    }

    [Fact]
    public async Task CheckpointCannotRewriteAlreadyStoredEvidence()
    {
        await Store.SavePreviewAsync(Preview("evidence", 0), Cancellation);
        await Store.ConfirmAsync("evidence", 0, Cancellation);
        var run = (await Store.ClaimAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), Cancellation))!;
        run.Evidence.Add(SyntheticResearchFixture.Fetch("facts", SyntheticResearchFixture.Catalog[0], SyntheticResearchFixture.PublishedAt).Evidence[0]);
        await Store.SaveRunAsync(run, "worker", run.Generation, Cancellation);
        run.Evidence[0] = run.Evidence[0] with { Text = "Changed after checkpoint" };
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveRunAsync(run, "worker", run.Generation, Cancellation));
    }

    private static ImportPreview Preview(string id, int baseline) => new(id, baseline, "hash-" + id, "originals/sample.csv",
        [new(id + "-buy-1", "SAMPLE:XNAS:USD", SyntheticResearchFixture.PublishedAt, TransactionKind.Buy, 1m, 100m, 0, "USD", .9m, "sample"),
         new(id + "-buy-2", "SAMPLE:XNAS:USD", SyntheticResearchFixture.PublishedAt, TransactionKind.Buy, 1m, 100m, 0, "USD", .9m, "sample")],
        SyntheticResearchFixture.Catalog, [], 0, DateTimeOffset.UtcNow);

    [Fact]
    public async Task QueuedWorkStartsItsExecutionDeadlineAtFirstClaim()
    {
        await Store.SavePreviewAsync(Preview("queued", 0), Cancellation);
        await Store.ConfirmAsync("queued", 0, Cancellation);
        var pending = Assert.Single(await Store.ListRunsAsync(Cancellation));
        await using (var database = factory.CreateDbContext())
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE AnalysisRuns SET CreatedAt = DATEADD(hour, -2, SYSUTCDATETIME()), Deadline = DATEADD(hour, -1, SYSUTCDATETIME()) WHERE Id = {pending.Id}");
        var claimed = await Store.ClaimAsync("worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), Cancellation);
        Assert.NotNull(claimed);
        Assert.True(claimed.Deadline > DateTimeOffset.UtcNow.AddMinutes(9));
    }

    [Fact]
    public async Task BlockingPreviewCannotBeConfirmedThroughStore()
    {
        await Store.SavePreviewAsync(Preview("invalid", 0) with { Issues = ["Stable ID economic conflict"] }, Cancellation);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.ConfirmAsync("invalid", 0, Cancellation));
        Assert.Equal(0, (await Store.GetPortfolioAsync(Cancellation)).Revision);
        Assert.Empty(await Store.ListRunsAsync(Cancellation));
    }

    private sealed class TestDatabaseFactory : IDbContextFactory<WorkspaceDbContext>
    {
        private readonly DbContextOptions<WorkspaceDbContext> options = new DbContextOptionsBuilder<WorkspaceDbContext>().UseSqlServer(
            "Server=(localdb)\\MSSQLLocalDB;Database=PiiInfraTests_" + Guid.NewGuid().ToString("N") + ";Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30",
            sql => sql.CommandTimeout(30)).Options;
        public WorkspaceDbContext CreateDbContext() => new(options);
    }
}