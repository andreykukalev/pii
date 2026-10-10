using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pii.Application;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Infrastructure.Tests;

public sealed class SourceCreditTests : IAsyncLifetime
{
    private readonly TestDatabaseFactory factory = new();
    private SqlSourceCreditStore Store => new(factory);
    public async Task InitializeAsync()
    {
        await using var database = factory.CreateDbContext();
        await database.Database.EnsureCreatedAsync();
    }
    public async Task DisposeAsync()
    {
        await using var database = factory.CreateDbContext();
        await database.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task ParallelRequestsNeverExceedDailyQuota()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Store.ReserveAsync(Guid.NewGuid(), "eod", 2, 10, "test-v1", CancellationToken.None)));
        Assert.Equal(5, outcomes.Count(outcome => outcome == SourceCreditAdmission.Reserved));
        Assert.Equal(3, outcomes.Count(outcome => outcome == SourceCreditAdmission.Exhausted));
        await using var database = factory.CreateDbContext();
        Assert.Equal(10, (await database.SourceCreditDays.SingleAsync()).ConsumedCredits);
        Assert.Equal(5, await database.SourceCreditEntries.CountAsync());
    }

    [Fact]
    public async Task DuplicateReservationDoesNotAuthorizeAnotherHttpRequest()
    {
        var reservationId = Guid.NewGuid();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Store.ReserveAsync(reservationId, "quote", 3, 10, "test-v1", CancellationToken.None)));
        Assert.Single(outcomes, outcome => outcome == SourceCreditAdmission.Reserved);
        Assert.Equal(3, outcomes.Count(outcome => outcome == SourceCreditAdmission.AlreadyReserved));
        await using var database = factory.CreateDbContext();
        Assert.Equal(3, (await database.SourceCreditDays.SingleAsync()).ConsumedCredits);
        Assert.Equal("ChargedUncertain", (await database.SourceCreditEntries.SingleAsync()).Outcome);
    }

    private sealed class TestDatabaseFactory : IDbContextFactory<WorkspaceDbContext>
    {
        private readonly DbContextOptions<WorkspaceDbContext> options = new DbContextOptionsBuilder<WorkspaceDbContext>().UseSqlServer(
            "Server=(localdb)\\MSSQLLocalDB;Database=PiiSourceCreditTests_" + Guid.NewGuid().ToString("N")
            + ";Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30", sql => sql.CommandTimeout(30)).Options;
        public WorkspaceDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task ServerUtcDayAndQuotaChangesPreserveConsumedCreditsAndOldDays()
    {
        await using var database = factory.CreateDbContext();
        var serverBefore = await database.Database.SqlQueryRaw<DateOnly>("SELECT CONVERT(date, SYSUTCDATETIME()) AS [Value]").SingleAsync();
        database.SourceCreditDays.Add(new() { UtcDate = serverBefore.AddDays(-1), MaximumCredits = 1, ConsumedCredits = 1 });
        await database.SaveChangesAsync();
        Assert.Equal(SourceCreditAdmission.Reserved, await Store.ReserveAsync(Guid.NewGuid(), "eod", 4, 5, "test-v1", default));
        Assert.Equal(SourceCreditAdmission.Exhausted, await Store.ReserveAsync(Guid.NewGuid(), "eod", 1, 3, "test-v2", default));
        Assert.Equal(SourceCreditAdmission.Reserved, await Store.ReserveAsync(Guid.NewGuid(), "quote", 2, 6, "test-v2", default));
        Assert.Equal(SourceCreditAdmission.Exhausted, await Store.ReserveAsync(Guid.NewGuid(), "quote", 1, 6, "test-v2", default));
        var serverAfter = await database.Database.SqlQueryRaw<DateOnly>("SELECT CONVERT(date, SYSUTCDATETIME()) AS [Value]").SingleAsync();
        var current = await database.SourceCreditDays.SingleAsync(row => row.UtcDate >= serverBefore);
        Assert.InRange(current.UtcDate, serverBefore, serverAfter);
        Assert.Equal(6, current.MaximumCredits);
        Assert.Equal(6, current.ConsumedCredits);
        Assert.Equal(1, (await database.SourceCreditDays.SingleAsync(row => row.UtcDate == serverBefore.AddDays(-1))).ConsumedCredits);
        Assert.All(await database.SourceCreditEntries.ToListAsync(), row => Assert.Equal(current.UtcDate, row.UtcDate));
    }

    [Fact]
    public async Task ChangedReservationIdentityIsRejectedAndCreditsSurvivePortfolioDeletionAndReconnection()
    {
        var reservationId = Guid.NewGuid();
        Assert.Equal(SourceCreditAdmission.Reserved, await Store.ReserveAsync(reservationId, "eod", 5, 5, "test-v1", default));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => Store.ReserveAsync(reservationId, "quote", 5, 5, "test-v1", default));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => Store.ReserveAsync(reservationId, "eod", 4, 5, "test-v1", default));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => Store.ReserveAsync(reservationId, "eod", 5, 5, "test-v2", default));
        var workspace = new SqlWorkspaceStore(factory, new(ExecutionMode.FixtureReplay, new(), [], "test", false, "test"));
        await workspace.InitializeAsync(default);
        Assert.True(await workspace.ReserveMonthlyAsync("monthly-independent", .5m, 1m, DateTimeOffset.MinValue, default));
        await workspace.SavePreviewAsync(new("credit-delete", 0, "credit-delete-hash", "originals/synthetic.csv", [], [], [], 0, DateTimeOffset.UtcNow), default);
        await workspace.ConfirmAsync("credit-delete", 0, default);
        Assert.Single(await workspace.ListRunsAsync(default));
        await workspace.DeletePortfolioAsync(default);
        Assert.Empty(await workspace.ListRunsAsync(default));
        var reconnected = new SqlSourceCreditStore(factory);
        Assert.Equal(SourceCreditAdmission.Exhausted, await reconnected.ReserveAsync(Guid.NewGuid(), "eod", 1, 5, "test-v1", default));
        Assert.Equal(SourceCreditAdmission.AlreadyReserved, await reconnected.ReserveAsync(reservationId, "eod", 5, 5, "test-v1", default));
        await using var database = factory.CreateDbContext();
        Assert.Equal(5, (await database.SourceCreditDays.SingleAsync()).ConsumedCredits);
        Assert.Equal("ChargedUncertain", (await database.SourceCreditEntries.SingleAsync()).Outcome);
        Assert.Equal(.5m, (await database.Months.SingleAsync()).TotalUsd);
    }

    [Fact]
    public async Task ExplicitAdditiveSchemaUpgradePreservesExistingWorkspaceAndIsIdempotent()
    {
        await using var database = factory.CreateDbContext();
        await database.Database.ExecuteSqlRawAsync("DROP TABLE dbo.SourceCreditEntries; DROP TABLE dbo.SourceCreditDays;");
        database.Months.Add(new() { Month = 202610, TotalUsd = .75m });
        await database.SaveChangesAsync();
        Assert.False(await database.Database.EnsureCreatedAsync());
        await SqlSourceCreditStore.AddSchemaAsync(factory, default);
        Assert.Equal(SourceCreditAdmission.Reserved, await Store.ReserveAsync(Guid.NewGuid(), "eod", 2, 2, "test-v1", default));
        await SqlSourceCreditStore.AddSchemaAsync(factory, default);
        Assert.Equal(.75m, (await database.Months.SingleAsync()).TotalUsd);
        Assert.Equal(2, (await database.SourceCreditDays.SingleAsync()).ConsumedCredits);
        Assert.Single(await database.SourceCreditEntries.ToListAsync());
    }

    [Fact]
    public async Task PaidUncertainHttpFailureKeepsDurableCreditsAndNextCallDoesNotReachHttp()
    {
        var instrument = new Pii.Domain.Instrument("RECORDED:XNAS:EUR", "RECORDED", "XNAS", "EUR", "Stock", "test");
        var options = new ResearchSourceOptions
        {
            Authorized = true, TwelveDataRights = true, TwelveDataEvidenceRetentionAuthorized = true,
            TwelveDataApiKey = "synthetic-test-key", TwelveDataEntitlements = ["quote"],
            TwelveDataRequestCredits = new() { Authorized = true, RateCardVersion = "test-v1", MaximumCreditsPerRequest = 2,
                DailyMaximumCredits = 2, EndpointWeights = new() { ["quote"] = 2 } }
        };
        using var transport = new RecordedHttp((_, _) => throw new HttpRequestException("synthetic uncertain transport"));
        using var client = new HttpClient(transport);
        var sources = new ResearchSources(client, options, new(ExecutionMode.Live, new(), [], "test", true, "test"), false, Store);
        Assert.Equal("Unavailable", (await sources.FetchAsync("quote", instrument, ExecutionMode.Live, DateTimeOffset.MinValue, default)).Status);
        Assert.Equal("TwelveDataDailyCreditBudgetExhausted", (await sources.FetchAsync("quote", instrument, ExecutionMode.Live, DateTimeOffset.MaxValue, default)).Error);
        Assert.Equal(1, transport.Calls);
        await using var database = factory.CreateDbContext();
        Assert.Equal(2, (await database.SourceCreditDays.SingleAsync()).ConsumedCredits);
        Assert.Equal("ChargedUncertain", (await database.SourceCreditEntries.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task ProductionBootstrapAndExplicitUpgradeMatchActualEfSqlSchema()
    {
        var bootstrapFactory = new TestDatabaseFactory();
        await using var bootstrap = bootstrapFactory.CreateDbContext();
        var connection = new SqlConnectionStringBuilder(bootstrap.Database.GetConnectionString());
        var databaseName = connection.InitialCatalog;
        connection.InitialCatalog = "master";
        await using var master = new SqlConnection(connection.ConnectionString);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{databaseName}] COLLATE Latin1_General_100_BIN2;";
        await create.ExecuteNonQueryAsync();
        try
        {
            var root = RepositoryRoot();
            var sql = File.ReadAllText(Path.Combine(root, "infra", "sql", "schema-bootstrap.sql"));
            sql = System.Text.RegularExpressions.Regex.Replace(sql, "(?m)^:ON ERROR EXIT\\r?\\n", "").Replace("$(DatabaseName)", databaseName, StringComparison.Ordinal);
            await bootstrap.Database.ExecuteSqlRawAsync(sql);
            await using var ef = factory.CreateDbContext();
            var expected = await SchemaMetadataAsync(ef);
            Assert.Equal(expected, await SchemaMetadataAsync(bootstrap));
            await bootstrap.Database.ExecuteSqlRawAsync("DROP TABLE dbo.SourceCreditEntries; DROP TABLE dbo.SourceCreditDays;");
            await SqlSourceCreditStore.AddSchemaAsync(bootstrapFactory, default);
            Assert.Equal(expected, await SchemaMetadataAsync(bootstrap));
        }
        finally { await bootstrap.Database.EnsureDeletedAsync(); }
    }

    [Fact]
    public async Task ActualReservationsRunWithOnlyReviewedWorkerGrants()
    {
        await using var database = factory.CreateDbContext();
        await database.Database.ExecuteSqlRawAsync("""
            CREATE USER [pii_worker_runtime] WITHOUT LOGIN;
            GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.SourceCreditDays TO [pii_worker_runtime];
            GRANT SELECT, INSERT ON OBJECT::dbo.SourceCreditEntries TO [pii_worker_runtime];
            """);
        var restricted = new SqlSourceCreditStore(new RestrictedDatabaseFactory(database.Database.GetConnectionString()!));
        var reservationId = Guid.NewGuid();
        Assert.Equal(SourceCreditAdmission.Reserved, await restricted.ReserveAsync(reservationId, "eod", 2, 2, "test-v1", default));
        Assert.Equal(SourceCreditAdmission.AlreadyReserved, await restricted.ReserveAsync(reservationId, "eod", 2, 2, "test-v1", default));
        Assert.Equal(SourceCreditAdmission.Exhausted, await restricted.ReserveAsync(Guid.NewGuid(), "eod", 1, 2, "test-v1", default));
    }

    private sealed class RestrictedDatabaseFactory(string connection) : IDbContextFactory<WorkspaceDbContext>
    {
        public WorkspaceDbContext CreateDbContext() => new(new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlServer(new SqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString)
            .AddInterceptors(new WorkerCommandInterceptor()).Options);
    }

    private sealed class WorkerCommandInterceptor : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection,
            ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXECUTE AS USER = N'pii_worker_runtime';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    private static async Task<string[]> SchemaMetadataAsync(WorkspaceDbContext database) =>
        (await database.Database.SqlQueryRaw<string>("""
            SELECT CONCAT('column:', tables.name, ':', columns.name, ':', types.name, ':', columns.max_length, ':',
                columns.precision, ':', columns.scale, ':', columns.is_nullable, ':', columns.collation_name) AS [Value]
            FROM sys.tables AS tables JOIN sys.columns AS columns ON columns.object_id = tables.object_id
            JOIN sys.types AS types ON types.user_type_id = columns.user_type_id
            WHERE tables.name IN (N'SourceCreditDays', N'SourceCreditEntries')
            UNION ALL
            SELECT CONCAT('index:', tables.name, ':', indexes.name, ':', indexes.is_unique, ':', columns.name, ':', indexColumns.key_ordinal)
            FROM sys.tables AS tables JOIN sys.indexes AS indexes ON indexes.object_id = tables.object_id
            JOIN sys.index_columns AS indexColumns ON indexColumns.object_id = indexes.object_id AND indexColumns.index_id = indexes.index_id
            JOIN sys.columns AS columns ON columns.object_id = indexColumns.object_id AND columns.column_id = indexColumns.column_id
            WHERE tables.name IN (N'SourceCreditDays', N'SourceCreditEntries')
            UNION ALL
            SELECT CONCAT('foreignkey:', keys.name, ':', keys.delete_referential_action, ':', keys.update_referential_action)
            FROM sys.foreign_keys AS keys WHERE keys.parent_object_id = OBJECT_ID(N'dbo.SourceCreditEntries')
            UNION ALL
            SELECT CONCAT('check:', constraints.name) FROM sys.check_constraints AS constraints
            WHERE constraints.parent_object_id IN (OBJECT_ID(N'dbo.SourceCreditDays'), OBJECT_ID(N'dbo.SourceCreditEntries'))
            """).ToListAsync()).Order(StringComparer.Ordinal).ToArray();
}