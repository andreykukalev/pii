using System.Text;
using Pii.Application;
using Pii.Domain;
using Xunit;

namespace Pii.Tests;

public sealed class ReconciliationTests
{
    private const string TradeHeader = "id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio";
    private const string SnapshotHeader = "asOf,recordType,instrumentId,currency,quantity,cashBalance\n";
    private static readonly DateTimeOffset Cutoff = new(2026, 1, 3, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly RuntimeConfiguration Configuration = new(ExecutionMode.FixtureReplay, new(), [], "Test", false, "Test");

    private static PortfolioRevision Ledger(bool complete = true) => new(4,
        [new("opening", "", Cutoff.AddDays(-3), TransactionKind.OpeningCash, 0, 2000, 0, "USD", null, "Independent fixture", CashLedgerComplete: complete),
            new("buy", "SAMPLE:XNAS:USD", Cutoff.AddDays(-2), TransactionKind.Buy, 10, 1000, 2, "USD", .9m, "Independent fixture"),
            new("sell", "SAMPLE:XNAS:USD", Cutoff.AddDays(-1), TransactionKind.Sell, 2, 240, 1, "USD", .92m, "Independent fixture"),
            new("future", "SAMPLE:XNAS:USD", Cutoff.AddDays(1), TransactionKind.Buy, 2, 100, 0, "USD", .9m, "Independent fixture")],
        [new("SAMPLE:XNAS:USD", "SAMPLE", "XNAS", "USD", "Stock", "Technology")], new(), Cutoff, "Test");

    private static StatementSnapshot Snapshot(decimal quantity = 8, decimal balance = 1237) => new(Cutoff,
        [new("SAMPLE:XNAS:USD", "USD", quantity, "Independent quantity row")], [new("USD", balance, "Independent cash row")], "Independent statement");

    [Fact]
    public void IndependentSnapshotMatchesDatedQuantityAndNativeCashNotFutureEvents()
    {
        var ledger = Ledger();
        var result = new StatementReconciliation().Compare(ledger, Snapshot());
        Assert.Equal(ReconciliationStatus.Match, result.Status);
        Assert.False(result.RequiresAcknowledgment);
        Assert.Equal(Cutoff, result.AsOf);
        Assert.Equal(4, result.LedgerRevision);
        Assert.Equal(8, result.Items.Single(item => item.Metric == "Quantity").LedgerAmount);
        Assert.Equal(1237, result.Items.Single(item => item.Metric == "Cash").LedgerAmount);
        Assert.All(result.Items, item => Assert.Equal(0, item.Difference));
        Assert.Equal(4, ledger.Transactions.Count);
        var facts = new PortfolioReconstructor().Calculate(4, ledger.Transactions,
            [new("SAMPLE:XNAS:USD", 120, "USD", .9m, Cutoff, "Independent dated valuation")], Cutoff);
        Assert.Equal(864, facts.KnownMarketValueEur);
        Assert.Equal(721.44m, Assert.Single(facts.Holdings).KnownBasisEur);
        Assert.Equal(39.52m, facts.KnownRealizedGainEur);
    }

    [Fact]
    public void MismatchAndUnknownAreDifferentAndSnapshotCannotSupplyCashOpeningOrBasis()
    {
        var mismatch = new StatementReconciliation().Compare(Ledger(), Snapshot(9, 1238));
        Assert.Equal(ReconciliationStatus.Mismatch, mismatch.Status);
        Assert.All(mismatch.Items, item => Assert.Equal(ReconciliationStatus.Mismatch, item.Status));
        var unknown = new StatementReconciliation().Compare(Ledger(false), Snapshot());
        Assert.Equal(ReconciliationStatus.Unknown, unknown.Status);
        Assert.True(unknown.RequiresAcknowledgment);
        Assert.Null(unknown.Items.Single(item => item.Metric == "Cash").LedgerAmount);
        var openingHolding = Ledger() with { Transactions = [new("opening", "SAMPLE:XNAS:USD", Cutoff, TransactionKind.Opening, 8, 0, 0, "USD", null, "Test")] };
        var result = new StatementReconciliation().Compare(openingHolding, Snapshot());
        Assert.Equal(ReconciliationStatus.Match, result.Items.Single(item => item.Metric == "Quantity").Status);
        var facts = new PortfolioReconstructor().Calculate(4, openingHolding.Transactions, [], Cutoff);
        Assert.Equal(8, Assert.Single(facts.Holdings).UnknownBasisQuantity);
        Assert.Null(facts.Holdings[0].UnrealizedGainEur);
    }

    [Fact]
    public void CurrencyConflictIsMismatchButMissingHistoryOrObservationIsUnknown()
    {
        var changedCurrency = Snapshot() with { Holdings = [new("SAMPLE:XNAS:USD", "EUR", 8, "Independent row")] };
        Assert.Equal(ReconciliationStatus.Mismatch, new StatementReconciliation().Compare(Ledger(), changedCurrency).Status);
        var emptyLedger = Ledger() with { Transactions = [] };
        var unknown = new StatementReconciliation().Compare(emptyLedger, Snapshot(0, 0));
        Assert.All(unknown.Items, item => Assert.Equal(ReconciliationStatus.Unknown, item.Status));
        Assert.All(unknown.Items, item => Assert.Null(item.LedgerAmount));
        var omitted = Snapshot() with { Holdings = [], CashBalances = [] };
        Assert.Equal(ReconciliationStatus.Unknown, new StatementReconciliation().Compare(Ledger(), omitted).Status);
    }

    [Fact]
    public void DividendOnlyAndOversoldHistoryCannotMatchAnInventedZeroHolding()
    {
        foreach (var kind in new[] { TransactionKind.Dividend, TransactionKind.Sell })
        {
            var ledger = Ledger() with { Transactions = [new("gap", "SAMPLE:XNAS:USD", Cutoff, kind,
                kind == TransactionKind.Sell ? 2 : 0, 10, 0, "USD", .9m, "History gap")] };
            var result = new StatementReconciliation().Compare(ledger, Snapshot(0));
            var quantity = Assert.Single(result.Items, item => item.Metric == "Quantity");
            Assert.Equal(ReconciliationStatus.Unknown, quantity.Status);
            Assert.Null(quantity.LedgerAmount);
            Assert.Null(quantity.Difference);
        }
    }

    [Fact]
    public async Task CashCompletenessChangesConflictWithStableSourceIdentity()
    {
        var store = new TestStore(Configuration);
        var csv = TradeHeader + ",cashLedgerComplete\nopen,,,,EUR,,,2026-01-01T10:00:00Z,OpeningCash,0,100,0,,,,true\n";
        var first = await PreviewAsync(store, csv);
        Assert.Empty(first.Issues);
        await store.ConfirmAsync(first.Id, 0, default);
        var conflict = await PreviewAsync(store, csv.Replace(",true\n", ",false\n", StringComparison.Ordinal));
        Assert.NotEmpty(conflict.Issues);
        Assert.True(Assert.Single(conflict.ProposedTransactions).CashLedgerComplete);
    }

    [Fact]
    public async Task SnapshotPreviewPreservesIndependentInstantAndDoesNotMutateLedgerOrInstruments()
    {
        var store = await SeedAsync();
        var before = await store.GetPortfolioAsync(default);
        var preview = await PreviewAsync(store, SnapshotHeader
            + "2026-01-03T12:00:00+02:00,Holding,SAMPLE:XNAS:USD,USD,8,\n"
            + "2026-01-03T12:00:00+02:00,Cash,,USD,,1237\n");
        Assert.Empty(preview.Issues);
        Assert.Equal(ReconciliationStatus.Match, preview.Reconciliation!.Status);
        Assert.Equal(TimeSpan.FromHours(2), preview.Snapshot!.AsOf.Offset);
        Assert.Equal(before.Transactions, preview.ProposedTransactions);
        Assert.Equal(before.Instruments, preview.Instruments);
        Assert.Same(before, await store.GetPortfolioAsync(default));
        var reloaded = JsonDefaults.Parse<ImportPreview>(JsonDefaults.Serialize(preview));
        Assert.Equal(1237, Assert.Single(reloaded.Snapshot!.CashBalances).Balance);
        Assert.Equal(ReconciliationStatus.Match, reloaded.Reconciliation!.Status);
    }

    [Fact]
    public async Task SnapshotMismatchBlocksExistingConfirmationGateWhileUnknownRemainsAcknowledgment()
    {
        var store = await SeedAsync();
        var mismatch = await PreviewAsync(store, SnapshotHeader + "2026-01-03T10:00:00Z,Holding,SAMPLE:XNAS:USD,USD,9,\n");
        Assert.NotEmpty(mismatch.Issues);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmAsync(mismatch.Id, mismatch.BaselineRevision, default));
        var unknown = await PreviewAsync(store, SnapshotHeader + "2026-01-03T10:00:00Z,Cash,,EUR,,100\n");
        Assert.Empty(unknown.Issues);
        Assert.Equal(ReconciliationStatus.Unknown, unknown.Reconciliation!.Status);
        Assert.True(unknown.Reconciliation.RequiresAcknowledgment);
    }

    [Theory]
    [InlineData("2026-01-03,Holding,SAMPLE:XNAS:USD,USD,8,")]
    [InlineData("2026-01-03T10:00:00Z,Holding,SAMPLE:XNAS:USD,usd,8,")]
    [InlineData("2026-01-03T10:00:00Z,Holding,SAMPLE:XNAS:USD,USD,-8,")]
    [InlineData("2026-01-03T10:00:00Z,Holding,SAMPLE:XNAS:USD,USD,8,1237")]
    [InlineData("2026-01-03T10:00:00Z,Cash,SAMPLE:XNAS:USD,USD,,1237")]
    [InlineData("2026-01-03T10:00:00Z,OpeningCash,,USD,,1237")]
    [InlineData("9999-01-03T10:00:00Z,Cash,,USD,,1237")]
    public async Task InvalidSnapshotRowsCannotConfirm(string row)
    {
        var preview = await PreviewAsync(new TestStore(Configuration), SnapshotHeader + row + "\n");
        Assert.NotEmpty(preview.Issues);
    }

    [Fact]
    public async Task MixedDatesDuplicateRowsAndMalformedColumnCountsAreRejected()
    {
        var store = new TestStore(Configuration);
        var mixed = await PreviewAsync(store, SnapshotHeader + "2026-01-03T10:00:00Z,Cash,,USD,,1\n2026-01-04T10:00:00Z,Cash,,EUR,,1\n");
        Assert.NotEmpty(mixed.Issues);
        var duplicate = await PreviewAsync(store, SnapshotHeader + "2026-01-03T10:00:00Z,Cash,,USD,,1\n2026-01-03T10:00:00Z,Cash,,USD,,1\n");
        Assert.NotEmpty(duplicate.Issues);
        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewAsync(store, SnapshotHeader + "2026-01-03T10:00:00Z,Cash,,USD\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewAsync(store, SnapshotHeader.ToUpperInvariant()));
    }

    [Fact]
    public async Task OversizeAndTooManySnapshotRowsAreBounded()
    {
        var store = new TestStore(Configuration);
        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewAsync(store, new string('x', 2 * 1024 * 1024 + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewAsync(store, SnapshotHeader
            + string.Concat(Enumerable.Repeat("2026-01-03T10:00:00Z,Cash,,USD,,1\n", 10001))));
    }

    [Fact]
    public async Task DecimalReconstructionOverflowIsRejectedAsInvalidImport()
    {
        var csv = TradeHeader + "\nmax,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,1,79228162514264337593543950335,1,1,,\n";
        await Assert.ThrowsAsync<InvalidDataException>(() => PreviewAsync(new TestStore(Configuration), csv));
    }

    [Fact]
    public async Task SampleCashRowsUseBlankInstrumentsAndRequireOptInCompleteness()
    {
        var store = new TestStore(Configuration);
        var preview = await PreviewAsync(store, TradeHeader + ",cashLedgerComplete\n"
            + "open,,,,EUR,,,2026-01-01T10:00:00Z,OpeningCash,0,100,0,,,,true\n"
            + "deposit,,,,EUR,,,2026-01-02T10:00:00Z,Deposit,0,50,1,,,,\n"
            + "fee,,,,EUR,,,2026-01-03T10:00:00Z,Fee,0,3,0,,,,\n");
        Assert.Empty(preview.Issues);
        Assert.Empty(preview.Instruments);
        Assert.All(preview.ProposedTransactions, item => Assert.Equal("", item.InstrumentId));
        Assert.Equal(146, Assert.Single(new PortfolioReconstructor().Calculate(1, preview.ProposedTransactions, [], Cutoff).CashBalances!).Balance);
        var originalHeader = await PreviewAsync(store, TradeHeader + "\nopen,,,,EUR,,,2026-01-01T10:00:00Z,OpeningCash,0,100,0,,,\n");
        Assert.Empty(originalHeader.Issues);
        Assert.Null(Assert.Single(new PortfolioReconstructor().Calculate(1, originalHeader.ProposedTransactions, [], Cutoff).CashBalances!).Balance);
    }

    [Theory]
    [InlineData("deposit,,,,usd,,,2026-01-01T10:00:00Z,Deposit,0,100,0,,,")]
    [InlineData("deposit,,,,EUR,,,2026-01-01T10:00:00Z,6,0,100,0,,,")]
    [InlineData("buy,,,,EUR,,,2026-01-01T10:00:00Z,Buy,1,100,0,,,")]
    [InlineData("fee,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Fee,0,3,0,,,")]
    public async Task InvalidCashRowsDoNotIntroduceInstruments(string row)
    {
        var preview = await PreviewAsync(new TestStore(Configuration), TradeHeader + "\n" + row + "\n");
        Assert.NotEmpty(preview.Issues);
        Assert.Empty(preview.ProposedTransactions);
        Assert.Empty(preview.Instruments);
    }

    [Fact]
    public void AdditiveContractsReadLegacyJsonAndRetainSnapshotRoundTrip()
    {
        var revision = JsonDefaults.Parse<PortfolioRevision>("{\"revision\":1,\"transactions\":[],\"instruments\":[],\"policy\":{},\"confirmedAt\":\"2026-01-01T00:00:00Z\",\"provenance\":\"Legacy\"}");
        Assert.Null(revision.Snapshot);
        Assert.Null(revision.Reconciliation);
        var retained = revision with { Snapshot = Snapshot(), Reconciliation = new StatementReconciliation().Compare(Ledger(), Snapshot()) };
        var reloaded = JsonDefaults.Parse<PortfolioRevision>(JsonDefaults.Serialize(retained));
        Assert.Equal(Cutoff, reloaded.Snapshot!.AsOf);
        Assert.Equal(ReconciliationStatus.Match, reloaded.Reconciliation!.Status);
        var facts = JsonDefaults.Parse<PortfolioFacts>("{\"revision\":1,\"holdings\":[],\"knownMarketValueEur\":0,\"knownRealizedGainEur\":0,\"knownIncomeEur\":0,\"knownWithholdingEur\":0,\"valuationQuality\":\"Unavailable\",\"cashQuality\":\"Unavailable\",\"issues\":[],\"asOf\":\"2026-01-01T00:00:00Z\"}");
        Assert.Null(facts.CashBalances);
        Assert.Null(facts.KnownCashValueEur);
        Assert.Null(facts.KnownFeesEur);
    }

    private static Task<ImportPreview> PreviewAsync(TestStore store, string csv) => new ImportCoordinator(store, new TestArtifacts())
        .PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "neutral.csv", default);

    private static async Task<TestStore> SeedAsync()
    {
        var store = new TestStore(Configuration);
        var preview = await PreviewAsync(store, TradeHeader + ",cashLedgerComplete\n"
            + "open,,,,USD,,,2025-12-31T10:00:00Z,OpeningCash,0,2000,0,,,,true\n"
            + "buy,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,10,1000,2,0.9,,,\n"
            + "sell,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-02T10:00:00Z,Sell,2,240,1,0.92,,,\n"
            + "future,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-04T10:00:00Z,Buy,2,100,0,0.9,,,\n");
        Assert.Empty(preview.Issues);
        await store.ConfirmAsync(preview.Id, 0, default);
        return store;
    }
}