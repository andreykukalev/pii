namespace Pii.Domain;

public enum Quality { Known, Partial, Unavailable }
public enum TransactionKind { Buy, Sell, Opening, Split, Dividend, Withholding, Deposit, Withdrawal, Fee, OpeningCash }
public enum RecommendationCategory { BUY, HOLD, REDUCE, SELL, REVIEW, WATCHLIST }
public enum Horizon { LongTerm, Tactical }
public record Instrument(string Id, string Symbol, string Exchange, string Currency, string Kind, string Sector, string? Cik = null);
public record Transaction(string Id, string InstrumentId, DateTimeOffset Time, TransactionKind Kind,
    decimal Quantity, decimal Gross, decimal Fee, string Currency, decimal? EurPerUnit, string Source,
    decimal? SplitRatio = null, bool CashLedgerComplete = false);
public record Quote(string InstrumentId, decimal Price, string Currency, decimal? EurPerUnit, DateTimeOffset AsOf, string EvidenceId);
public record Holding(string InstrumentId, decimal Quantity, decimal KnownBasisEur, decimal UnknownBasisQuantity,
    decimal? MarketValueEur, decimal? UnrealizedGainEur, decimal KnownRealizedGainEur, decimal UnknownDisposalQuantity,
    Quality BasisQuality, IReadOnlyList<string> Issues);
public record CashBalance(string Currency, decimal? Balance, decimal? ValueEur, Quality BalanceQuality,
    Quality ValuationQuality, DateTimeOffset? CoverageFrom, DateTimeOffset? FxAsOf, string? FxEvidenceId,
    IReadOnlyList<string> Issues);
public record PortfolioFacts(int Revision, IReadOnlyList<Holding> Holdings, decimal KnownMarketValueEur,
    decimal KnownRealizedGainEur, decimal KnownIncomeEur, decimal KnownWithholdingEur, Quality ValuationQuality,
    Quality CashQuality, IReadOnlyList<string> Issues, DateTimeOffset AsOf,
    IReadOnlyList<CashBalance>? CashBalances = null, decimal? KnownCashValueEur = null,
    Quality? CashValuationQuality = null, decimal? KnownFeesEur = null);
public record RiskPolicy(int Version = 0, decimal? MaximumPositionPercent = null, decimal? MaximumTacticalPercent = null)
{
    public bool Configured => Version > 0 && MaximumPositionPercent is > 0 and <= 100;
}
public record GateResult(RecommendationCategory Category, Horizon Horizon, bool Eligible, string Reason);

public static class RecommendationPolicyEvaluator
{
    public static GateResult Evaluate(RecommendationCategory category, Horizon horizon, RiskPolicy policy,
        bool supportedThesis, bool completeInputs, decimal? proposedPositionPercent = null)
    {
        if (!supportedThesis && category == RecommendationCategory.HOLD)
            return new(RecommendationCategory.REVIEW, horizon, false, "HOLD requires supported thesis evidence.");
        if (category is RecommendationCategory.BUY or RecommendationCategory.REDUCE or RecommendationCategory.SELL)
        {
            if (!policy.Configured) return new(category, horizon, false, "PolicyPending: no numerical investment policy configured.");
            if (!supportedThesis || !completeInputs || proposedPositionPercent is null)
                return new(category, horizon, false, "InputsIncomplete: suitability and scenario inputs required; no sizing generated.");
            if (proposedPositionPercent < 0 || proposedPositionPercent > policy.MaximumPositionPercent)
                return new(category, horizon, false, "Configured position limit violated.");
            if (horizon == Horizon.Tactical && (policy.MaximumTacticalPercent is null || proposedPositionPercent > policy.MaximumTacticalPercent))
                return new(category, horizon, false, "Configured tactical allocation unavailable or violated.");
        }
        return new(category, horizon, true, "Informational research only; never execution or tax advice.");
    }
}

public sealed class PortfolioReconstructor
{
    private sealed class Lot(decimal quantity, decimal? basis)
    {
        public decimal Quantity = quantity;
        public decimal? Basis = basis;
    }

    public PortfolioFacts Calculate(int revision, IReadOnlyList<Transaction> transactions, IReadOnlyList<Quote> quotes,
        DateTimeOffset asOf)
    {
        var holdings = new List<Holding>();
        var allIssues = new List<string>();
        var effectiveTransactions = transactions.Where(item => item.Time <= asOf).ToArray();
        foreach (var transaction in effectiveTransactions)
        {
            if (!Enum.IsDefined(transaction.Kind) || transaction.Quantity < 0 || transaction.Gross < 0 || transaction.Fee < 0
                || transaction.EurPerUnit is <= 0 || transaction.Currency.Length != 3 || !transaction.Currency.All(char.IsAsciiLetterUpper))
                throw new ArgumentException("Invalid normalized transaction amounts, currency or kind.");
            if (transaction.CashLedgerComplete && transaction.Kind != TransactionKind.OpeningCash)
                throw new ArgumentException("Cash completeness must be declared on OpeningCash only.");
            if (transaction.Kind == TransactionKind.OpeningCash && (transaction.InstrumentId.Length > 0 || transaction.Quantity != 0 || transaction.Fee != 0))
                throw new ArgumentException("OpeningCash must have no instrument, quantity or fee.");
        }
        foreach (var group in effectiveTransactions.Where(item => item.InstrumentId.Length > 0).GroupBy(item => item.InstrumentId))
        {
            var lots = new List<Lot>();
            var issues = new List<string>();
            decimal realized = 0, unknownDisposals = 0;
            var ordered = group.OrderBy(item => item.Time).ToArray();
            var ambiguous = ordered.GroupBy(item => item.Time).Any(items => items.Select(item => item.Kind).Distinct().Count() > 1
                && items.Any(item => item.Kind is TransactionKind.Buy or TransactionKind.Sell or TransactionKind.Split));
            if (ambiguous) issues.Add("Ambiguous same-timestamp economic ordering: affected basis and gains unavailable.");
            foreach (var transaction in ordered)
            {
                if (transaction.Quantity < 0 || transaction.Gross < 0 || transaction.Fee < 0)
                    throw new ArgumentException("Negative amounts/quantities are not accepted by the normalized ledger.");
                var rate = transaction.Currency == "EUR" ? 1m : transaction.EurPerUnit;
                switch (transaction.Kind)
                {
                    case TransactionKind.Buy:
                        if (transaction.Quantity <= 0) throw new ArgumentException("Buy quantity must be positive.");
                        lots.Add(new(transaction.Quantity, ambiguous || rate is null ? null : (transaction.Gross + transaction.Fee) * rate));
                        if (rate is null) issues.Add($"Missing acquisition FX: {transaction.Id}.");
                        break;
                    case TransactionKind.Opening:
                        lots.Add(new(transaction.Quantity, null));
                        issues.Add("Opening quantity is known; acquisition basis is unknown.");
                        break;
                    case TransactionKind.Split:
                        if (transaction.SplitRatio is not > 0) throw new ArgumentException("Split ratio must be positive.");
                        foreach (var lot in lots) lot.Quantity *= transaction.SplitRatio.Value;
                        break;
                    case TransactionKind.Sell:
                        if (transaction.Quantity <= 0 || transaction.Fee > transaction.Gross) throw new ArgumentException("Invalid sale.");
                        var remaining = transaction.Quantity;
                        var netPerShare = rate is null ? (decimal?)null : (transaction.Gross - transaction.Fee) * rate / remaining;
                        foreach (var lot in lots.Where(item => item.Quantity > 0))
                        {
                            var matched = Math.Min(remaining, lot.Quantity);
                            var matchedBasis = lot.Basis * matched / lot.Quantity;
                            if (matchedBasis is null || netPerShare is null || ambiguous) unknownDisposals += matched;
                            else realized += netPerShare.Value * matched - matchedBasis.Value;
                            lot.Quantity -= matched;
                            lot.Basis -= matchedBasis;
                            remaining -= matched;
                            if (remaining == 0) break;
                        }
                        if (remaining > 0)
                        {
                            unknownDisposals += remaining;
                            issues.Add($"Oversell/history gap of {remaining}: {transaction.Id}.");
                        }
                        break;
                }
            }
            var quantity = lots.Sum(item => item.Quantity);
            var unknown = lots.Where(item => item.Basis is null).Sum(item => item.Quantity);
            var basis = lots.Sum(item => item.Basis ?? 0);
            var quote = quotes.Where(item => item.InstrumentId == group.Key && item.AsOf <= asOf).OrderByDescending(item => item.AsOf).FirstOrDefault();
            var quoteRate = quote?.Currency == "EUR" ? 1m : quote?.EurPerUnit;
            var value = quote is null || quoteRate is null ? (decimal?)null : quantity * quote.Price * quoteRate;
            if (value is null && quantity > 0) issues.Add("Dated price/FX unavailable.");
            if (unknownDisposals > 0) issues.Add("Realized gain only includes known matches; unknown disposal quantity retained.");
            holdings.Add(new(group.Key, quantity, basis, unknown, value,
                unknown == 0 && unknownDisposals == 0 && !ambiguous && value is not null ? value - basis : null,
                realized, unknownDisposals, unknown == 0 && unknownDisposals == 0 && !ambiguous ? Quality.Known : Quality.Partial, issues));
        }
        decimal income = 0, withholding = 0, fees = 0;
        foreach (var transaction in effectiveTransactions)
        {
            var rate = transaction.Currency == "EUR" ? 1m : transaction.EurPerUnit;
            if (transaction.Kind is TransactionKind.Dividend or TransactionKind.Withholding)
            {
                if (rate is null) allIssues.Add($"Income FX unavailable: {transaction.Id}.");
                else if (transaction.Kind == TransactionKind.Dividend) income += transaction.Gross * rate.Value;
                else withholding += transaction.Gross * rate.Value;
            }
            var feeAmount = transaction.Fee + (transaction.Kind == TransactionKind.Fee ? transaction.Gross : 0);
            if (rate is null && feeAmount > 0) allIssues.Add($"Fee FX unavailable: {transaction.Id}.");
            else if (rate is not null) fees += feeAmount * rate.Value;
        }
        var cash = CalculateCash(effectiveTransactions, quotes, asOf);
        var cashQuality = cash.Count == 0 || cash.All(item => item.Balance is null) ? Quality.Unavailable
            : cash.Any(item => item.Balance is null) ? Quality.Partial : Quality.Known;
        var cashValuationQuality = cashQuality == Quality.Unavailable ? Quality.Unavailable
            : cashQuality != Quality.Known || cash.Any(item => item.ValueEur is null) ? Quality.Partial : Quality.Known;
        allIssues.AddRange(cash.SelectMany(item => item.Issues));
        if (cashQuality != Quality.Known) allIssues.Add("CashUnknown: explicit opening balances and declared complete native-currency cash ledgers required; allocations are invested-position-only.");
        allIssues.Add("Invested value and cash subtotals are separate. Value change is not return. Economic FIFO, not tax calculation.");
        return new(revision, holdings, holdings.Sum(item => item.MarketValueEur ?? 0), holdings.Sum(item => item.KnownRealizedGainEur),
            income, withholding, holdings.Any(item => item.Quantity > 0 && item.MarketValueEur is null) ? Quality.Partial : Quality.Known,
            cashQuality, allIssues, asOf, cash, cash.Any(item => item.ValueEur is not null) ? cash.Sum(item => item.ValueEur ?? 0) : null,
            cashValuationQuality, fees);
    }

    private static List<CashBalance> CalculateCash(IReadOnlyList<Transaction> transactions, IReadOnlyList<Quote> quotes, DateTimeOffset asOf)
    {
        var balances = new List<CashBalance>();
        foreach (var group in transactions.Where(item => item.Kind is not (TransactionKind.Opening or TransactionKind.Split)).GroupBy(item => item.Currency))
        {
            var issues = new List<string>();
            var openings = group.Where(item => item.Kind == TransactionKind.OpeningCash).ToArray();
            var opening = openings.Length == 1 && openings[0].CashLedgerComplete ? openings[0] : null;
            if (opening is null)
            {
                issues.Add($"CashUnknown {group.Key}: one explicit OpeningCash and full ledger coverage declaration required; observed movements are not a balance.");
                balances.Add(new(group.Key, null, null, Quality.Unavailable, Quality.Unavailable, null, null, null, issues));
                continue;
            }
            var balance = opening.Gross;
            foreach (var transaction in group.Where(item => item.Time >= opening.Time && item.Kind != TransactionKind.OpeningCash))
                balance += transaction.Kind switch
                {
                    TransactionKind.Buy => -transaction.Gross - transaction.Fee,
                    TransactionKind.Sell or TransactionKind.Dividend or TransactionKind.Deposit => transaction.Gross - transaction.Fee,
                    TransactionKind.Withholding or TransactionKind.Withdrawal or TransactionKind.Fee => -transaction.Gross - transaction.Fee,
                    _ => throw new ArgumentException("Unsupported cash movement.")
                };
            var fx = quotes.Where(item => item.Currency == group.Key && item.AsOf <= asOf && item.EurPerUnit is > 0
                && !string.IsNullOrWhiteSpace(item.EvidenceId)).OrderByDescending(item => item.AsOf).FirstOrDefault();
            var rate = group.Key == "EUR" ? 1m : fx?.EurPerUnit;
            if (rate is null) issues.Add($"Cash valuation FX unavailable: {group.Key}; historical transaction FX is not valuation FX.");
            balances.Add(new(group.Key, balance, rate is null ? null : balance * rate.Value, Quality.Known,
                rate is null ? Quality.Partial : Quality.Known, opening.Time, group.Key == "EUR" ? null : fx?.AsOf,
                group.Key == "EUR" ? null : fx?.EvidenceId, issues));
        }
        return balances;
    }
}