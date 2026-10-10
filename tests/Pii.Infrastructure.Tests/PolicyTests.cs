using Microsoft.EntityFrameworkCore;
using Pii.Application;
using Pii.Domain;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class PolicyTests : IAsyncLifetime
{
    private readonly PolicyDatabaseFactory factory = new();
    private SqlWorkspaceStore Store => new(factory, new(ExecutionMode.FixtureReplay, new(), [], "test", false, "test"));
    private static CancellationToken Cancellation => CancellationToken.None;
    public Task InitializeAsync() => Store.InitializeAsync(Cancellation);
    public async Task DisposeAsync()
    {
        await using var database = factory.CreateDbContext();
        await database.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task PolicyCreatesImmutableRevisionAndPinnedWorkWithoutChangingLedger()
    {
        var snapshot = new StatementSnapshot(DateTimeOffset.UtcNow, [], [], "independent");
        var preview = new ImportPreview("policy-test", 0, "hash-policy-test", "originals/test.csv",
            [new("event-1", "test", DateTimeOffset.UtcNow, TransactionKind.Buy, 1, 10, 0, "EUR", 1, "test")],
            [], [], 0, DateTimeOffset.UtcNow, Snapshot: snapshot,
            Reconciliation: new(0, snapshot.AsOf, []));
        await Store.SavePreviewAsync(preview, Cancellation);
        var before = await Store.ConfirmAsync(preview.Id, 0, Cancellation);
        var after = await Store.UpdatePolicyAsync(before.Revision, 12.5m, null, Cancellation);
        Assert.Equal(2, after.Revision);
        Assert.Equal(new RiskPolicy(1, 12.5m, null), after.Policy);
        Assert.Equal(JsonDefaults.Serialize(before.Transactions), JsonDefaults.Serialize(after.Transactions));
        Assert.Equal(JsonDefaults.Serialize(before.Snapshot), JsonDefaults.Serialize(after.Snapshot));
        Assert.Equal(JsonDefaults.Serialize(before.Reconciliation), JsonDefaults.Serialize(after.Reconciliation));
        var pending = Assert.Single(await Store.ListRunsAsync(Cancellation), run => run.IdempotencyKey == "policy:2");
        Assert.Equal(RunStatus.Pending, pending.Status);
        Assert.Equal("PendingWorkerBinding", pending.ModelBinding);
        Assert.Equal(JsonDefaults.Serialize(after), JsonDefaults.Serialize(pending.Portfolio));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.UpdatePolicyAsync(1, 20, 5, Cancellation));
        Assert.Equal(1, (await Store.GetPortfolioAsync(Cancellation)).Policy.Version);
        var next = await Store.UpdatePolicyAsync(2, 20, 5, Cancellation);
        Assert.Equal(2, next.Policy.Version);
        Assert.Equal(JsonDefaults.Serialize(before), JsonDefaults.Serialize(await Store.ConfirmAsync(preview.Id, 0, Cancellation)));
        await using var database = factory.CreateDbContext();
        Assert.Equal(3, await database.Revisions.CountAsync());
        Assert.Equal(3, await database.LedgerIdentities.CountAsync());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(10, -1)]
    [InlineData(10, 101)]
    public async Task InvalidPercentagesDoNotWrite(int position, int? tactical)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Store.UpdatePolicyAsync(0, position, tactical, Cancellation));
        Assert.Empty(await Store.ListRunsAsync(Cancellation));
    }

    [Fact]
    public async Task EmptyPortfolioCannotBeConfigured()
    {
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.UpdatePolicyAsync(0, 10, null, Cancellation));
        Assert.Equal(0, (await Store.GetPortfolioAsync(Cancellation)).Policy.Version);
    }

    private sealed class PolicyDatabaseFactory : IDbContextFactory<WorkspaceDbContext>
    {
        private readonly DbContextOptions<WorkspaceDbContext> options = new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=PiiPolicyTests_" + Guid.NewGuid().ToString("N")
                + ";Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30").Options;
        public WorkspaceDbContext CreateDbContext() => new(options);
    }
}