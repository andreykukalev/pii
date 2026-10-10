using Pii.Domain;
using Xunit;

namespace Pii.Tests;

public class FinancialTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static Transaction Trade(string id, int days, TransactionKind kind, decimal quantity, decimal gross, decimal fee = 0, decimal? fx = 1) =>
        new(id, "TEST:XNAS:USD", Start.AddDays(days), kind, quantity, gross, fee, "USD", fx, "Synthetic");

    [Fact]
    public void PartialFifoCapitalizesBuyAndDeductsSellFees()
    {
        var facts = new PortfolioReconstructor().Calculate(1,
            [Trade("buy1", 0, TransactionKind.Buy, 10, 100, 2), Trade("buy2", 1, TransactionKind.Buy, 5, 100), Trade("sell", 2, TransactionKind.Sell, 12, 240, 3)],
            [new("TEST:XNAS:USD", 25, "USD", 1, Start.AddDays(3), "quote")], Start.AddDays(3));
        var holding = Assert.Single(facts.Holdings);
        Assert.Equal(3, holding.Quantity);
        Assert.Equal(60, holding.KnownBasisEur);
        Assert.Equal(95, holding.KnownRealizedGainEur);
        Assert.Equal(15, holding.UnrealizedGainEur);
    }

    [Fact]
    public void OpeningHistoryNeverBecomesZeroCostPurchase()
    {
        var facts = new PortfolioReconstructor().Calculate(1,
            [Trade("opening", 0, TransactionKind.Opening, 10, 0), Trade("sell", 1, TransactionKind.Sell, 2, 40)], [], Start.AddDays(2));
        var holding = Assert.Single(facts.Holdings);
        Assert.Equal(8, holding.UnknownBasisQuantity);
        Assert.Equal(2, holding.UnknownDisposalQuantity);
        Assert.Null(holding.UnrealizedGainEur);
        Assert.Equal(Quality.Partial, holding.BasisQuality);
    }

    [Fact]
    public void SplitPreservesAggregateBasisAndDividendDoesNotBuyShares()
    {
        var split = Trade("split", 1, TransactionKind.Split, 0, 0) with { SplitRatio = 2 };
        var facts = new PortfolioReconstructor().Calculate(1, [Trade("buy", 0, TransactionKind.Buy, 10, 100), split,
            Trade("div", 2, TransactionKind.Dividend, 0, 8), Trade("tax", 3, TransactionKind.Withholding, 0, 2)], [], Start.AddDays(4));
        Assert.Equal(20, Assert.Single(facts.Holdings).Quantity);
        Assert.Equal(100, facts.Holdings[0].KnownBasisEur);
        Assert.Equal(8, facts.KnownIncomeEur);
        Assert.Equal(2, facts.KnownWithholdingEur);
    }

    [Fact]
    public void MissingFxAndUnconfiguredPolicyFailClosed()
    {
        var facts = new PortfolioReconstructor().Calculate(1, [Trade("buy", 0, TransactionKind.Buy, 1, 100, fx: null)], [], Start);
        Assert.Equal(1, Assert.Single(facts.Holdings).UnknownBasisQuantity);
        Assert.False(RecommendationPolicyEvaluator.Evaluate(RecommendationCategory.BUY, Horizon.LongTerm, new(), true, true, 1).Eligible);
        Assert.Equal(RecommendationCategory.REVIEW, RecommendationPolicyEvaluator.Evaluate(RecommendationCategory.HOLD, Horizon.LongTerm, new(), false, true).Category);
    }

    [Fact]
    public void FutureTransactionsCannotChangeEarlierFacts()
    {
        var facts = new PortfolioReconstructor().Calculate(1, [Trade("buy", 0, TransactionKind.Buy, 10, 100),
            Trade("sell", 2, TransactionKind.Sell, 2, 40), Trade("div", 2, TransactionKind.Dividend, 0, 8),
            Trade("tax", 2, TransactionKind.Withholding, 0, 2), Trade("fee", 2, TransactionKind.Fee, 0, 3)],
            [new("TEST:XNAS:USD", 12, "USD", 1, Start, "dated"), new("TEST:XNAS:USD", 100, "USD", 1, Start.AddDays(2), "future")], Start);
        Assert.Equal(10, Assert.Single(facts.Holdings).Quantity);
        Assert.Equal(100, facts.Holdings[0].KnownBasisEur);
        Assert.Equal(120, facts.KnownMarketValueEur);
        Assert.Equal(0, facts.KnownRealizedGainEur);
        Assert.Equal(0, facts.KnownIncomeEur);
        Assert.Equal(0, facts.KnownWithholdingEur);
        Assert.Equal(0, facts.KnownFeesEur);
    }

    [Fact]
    public void CashRequiresOpeningAndDeclaredCompleteLedgerNeverInferredFromDeposits()
    {
        var deposit = new Transaction("deposit", "", Start, TransactionKind.Deposit, 0, 100, 0, "EUR", null, "Synthetic");
        var opening = deposit with { Id = "opening", Kind = TransactionKind.OpeningCash };
        foreach (var ledger in new Transaction[][] { [deposit], [opening, deposit] })
        {
            var facts = new PortfolioReconstructor().Calculate(1, ledger, [], Start);
            Assert.Equal(Quality.Unavailable, facts.CashQuality);
            Assert.Null(Assert.Single(facts.CashBalances!).Balance);
            Assert.Null(facts.KnownCashValueEur);
        }
        var known = new PortfolioReconstructor().Calculate(1, [opening with { CashLedgerComplete = true }], [], Start);
        Assert.Equal(100, Assert.Single(known.CashBalances!).Balance);
        Assert.Equal(Quality.Known, known.CashQuality);
    }

    [Fact]
    public void NativeCashUsesGrossAndFeesWithSeparateDividendWithholdingAndFeeTotals()
    {
        var opening = new Transaction("opening", "", Start.AddDays(-1), TransactionKind.OpeningCash, 0, 1000, 0, "USD", null, "Synthetic", CashLedgerComplete: true);
        var deposit = opening with { Id = "deposit", Kind = TransactionKind.Deposit, Gross = 50, CashLedgerComplete = false };
        var withdrawal = deposit with { Id = "withdrawal", Kind = TransactionKind.Withdrawal, Gross = 20 };
        var fee = deposit with { Id = "fee", Kind = TransactionKind.Fee, Gross = 3, EurPerUnit = .9m };
        var facts = new PortfolioReconstructor().Calculate(1, [opening, deposit, withdrawal, fee,
            Trade("buy", 0, TransactionKind.Buy, 10, 100, 2, .9m), Trade("sell", 1, TransactionKind.Sell, 2, 30, 1, .9m),
            Trade("div", 2, TransactionKind.Dividend, 0, 10, 1, .9m), Trade("tax", 2, TransactionKind.Withholding, 0, 2, 0, .9m),
            Trade("future", 4, TransactionKind.Buy, 1, 500)], [], Start.AddDays(3));
        var cash = Assert.Single(facts.CashBalances!);
        Assert.Equal(961, cash.Balance);
        Assert.Equal(Quality.Known, facts.CashQuality);
        Assert.Equal(Quality.Partial, facts.CashValuationQuality);
        Assert.Null(cash.ValueEur);
        Assert.Equal(9, facts.KnownIncomeEur);
        Assert.Equal(1.8m, facts.KnownWithholdingEur);
        Assert.Equal(6.3m, facts.KnownFeesEur);
        Assert.Equal(7.74m, facts.KnownRealizedGainEur);
    }

    [Fact]
    public void CashValuationUsesLatestSourcedFxAtOrBeforeCutoffNotTradeFx()
    {
        var opening = new Transaction("opening", "", Start, TransactionKind.OpeningCash, 0, 100, 0, "USD", .5m, "Synthetic", CashLedgerComplete: true);
        var facts = new PortfolioReconstructor().Calculate(1, [opening],
            [new("fx", 0, "USD", .8m, Start, "old"), new("fx", 0, "USD", .9m, Start.AddDays(1), "latest"),
                new("fx", 0, "USD", 2, Start.AddDays(3), "future")], Start.AddDays(2));
        var cash = Assert.Single(facts.CashBalances!);
        Assert.Equal(90, cash.ValueEur);
        Assert.Equal("latest", cash.FxEvidenceId);
        Assert.Equal(Start.AddDays(1), cash.FxAsOf);
        Assert.Equal(Quality.Known, facts.CashValuationQuality);
    }

    [Fact]
    public void MixedCurrencyCoverageAndDuplicateOpeningStayPartial()
    {
        var eur = new Transaction("eur", "", Start, TransactionKind.OpeningCash, 0, 0, 0, "EUR", null, "Synthetic", CashLedgerComplete: true);
        var usd = eur with { Id = "usd", Currency = "USD", Gross = 50, CashLedgerComplete = false };
        var facts = new PortfolioReconstructor().Calculate(1, [eur, usd], [], Start);
        Assert.Equal(Quality.Partial, facts.CashQuality);
        Assert.Equal(0, facts.KnownCashValueEur);
        Assert.Null(facts.CashBalances!.Single(item => item.Currency == "USD").Balance);
        var duplicate = new PortfolioReconstructor().Calculate(1, [eur, eur with { Id = "second" }], [], Start);
        Assert.Null(Assert.Single(duplicate.CashBalances!).Balance);
    }

    [Fact]
    public void FractionalDecimalFifoRetainsExactUnconsumedBasis()
    {
        var facts = new PortfolioReconstructor().Calculate(1, [Trade("first", 0, TransactionKind.Buy, .1m, 10, .1m),
            Trade("second", 1, TransactionKind.Buy, .2m, 30), Trade("sell", 2, TransactionKind.Sell, .25m, 40, .2m)], [], Start.AddDays(3));
        var holding = Assert.Single(facts.Holdings);
        Assert.Equal(.05m, holding.Quantity);
        Assert.Equal(7.5m, holding.KnownBasisEur);
        Assert.Equal(7.2m, holding.KnownRealizedGainEur);
        Assert.Equal(0, holding.UnknownBasisQuantity);
    }
}