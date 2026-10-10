using Pii.Domain;

namespace Pii.Application;

public sealed class StatementReconciliation
{
    public ReconciliationResult Compare(PortfolioRevision ledger, StatementSnapshot snapshot)
    {
        var facts = new PortfolioReconstructor().Calculate(ledger.Revision, ledger.Transactions, [], snapshot.AsOf);
        var items = new List<ReconciliationItem>();
        var holdingsById = facts.Holdings.ToDictionary(item => item.InstrumentId, StringComparer.Ordinal);
        var instrumentsById = ledger.Instruments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var effectiveTransactions = ledger.Transactions.Where(item => item.Time <= snapshot.AsOf).ToArray();
        var quantityEvidence = effectiveTransactions.Where(item => item.Kind is TransactionKind.Buy or TransactionKind.Opening)
            .Select(item => item.InstrumentId).ToHashSet(StringComparer.Ordinal);
        var transactionCurrencies = effectiveTransactions.Where(item => item.InstrumentId.Length > 0).GroupBy(item => item.InstrumentId)
            .ToDictionary(group => group.Key, group => group.First().Currency, StringComparer.Ordinal);
        var observedInstruments = snapshot.Holdings.Select(item => item.InstrumentId).ToHashSet(StringComparer.Ordinal);
        var observedCashCurrencies = snapshot.CashBalances.Select(item => item.Currency).ToHashSet(StringComparer.Ordinal);
        foreach (var observed in snapshot.Holdings)
        {
            var holding = holdingsById.GetValueOrDefault(observed.InstrumentId);
            var currency = instrumentsById.GetValueOrDefault(observed.InstrumentId)?.Currency
                ?? transactionCurrencies.GetValueOrDefault(observed.InstrumentId);
            var historyGap = holding is null || !quantityEvidence.Contains(observed.InstrumentId)
                || holding.Issues.Any(item => item.StartsWith("Oversell", StringComparison.Ordinal));
            var status = historyGap ? ReconciliationStatus.Unknown
                : currency != observed.Currency || holding!.Quantity != observed.Quantity ? ReconciliationStatus.Mismatch : ReconciliationStatus.Match;
            var reason = historyGap ? "Quantity history unavailable at the snapshot cutoff; no zero holding inferred."
                : currency != observed.Currency ? $"Native currency differs: ledger {currency}, snapshot {observed.Currency}."
                : status == ReconciliationStatus.Match ? "Independent native quantity matches; this does not verify acquisition basis."
                : "Independent quantity differs from the dated ledger.";
            items.Add(new("Quantity", observed.InstrumentId, observed.Currency, observed.Quantity,
                historyGap ? null : holding!.Quantity, status, reason, observed.Source));
        }
        foreach (var holding in facts.Holdings.Where(item => item.Quantity > 0 && !observedInstruments.Contains(item.InstrumentId)))
            items.Add(new("Quantity", holding.InstrumentId, instrumentsById.GetValueOrDefault(holding.InstrumentId)?.Currency ?? "",
                null, holding.Quantity, ReconciliationStatus.Unknown, "No independent quantity observation; omission is not zero."));
        var cash = facts.CashBalances ?? [];
        var cashByCurrency = cash.ToDictionary(item => item.Currency, StringComparer.Ordinal);
        foreach (var observed in snapshot.CashBalances)
        {
            var balance = cashByCurrency.GetValueOrDefault(observed.Currency)?.Balance;
            var status = balance is null ? ReconciliationStatus.Unknown
                : balance == observed.Balance ? ReconciliationStatus.Match : ReconciliationStatus.Mismatch;
            items.Add(new("Cash", "", observed.Currency, observed.Balance, balance, status,
                balance is null ? "CashUnknown: opening balance or declared complete cash ledger unavailable; snapshot does not fill the gap."
                : status == ReconciliationStatus.Match ? "Independent native cash balance matches; valuation FX is separate."
                : "Independent native cash balance differs from the dated ledger.", observed.Source));
        }
        foreach (var balance in cash.Where(item => !observedCashCurrencies.Contains(item.Currency)))
            items.Add(new("Cash", "", balance.Currency, null, balance.Balance, ReconciliationStatus.Unknown,
                "No independent native cash observation; omission is not zero."));
        if (snapshot.CashBalances.Count == 0 && cash.Count == 0)
            items.Add(new("Cash", "", "", null, null, ReconciliationStatus.Unknown, "No independent cash observation or cash ledger coverage."));
        return new(ledger.Revision, snapshot.AsOf, items);
    }
}