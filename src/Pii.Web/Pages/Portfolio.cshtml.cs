using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;
using Pii.Domain;

namespace Pii.Web.Pages;

public sealed class PortfolioModel(IWorkspaceStore store) : PageModel
{
    public PortfolioRevision Portfolio { get; private set; } = null!;
    public PortfolioFacts Facts { get; private set; } = null!;
    public AnalysisRun? ValuationRun { get; private set; }
    public ReconciliationResult? Reconciliation { get; private set; }
    public async Task OnGetAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Portfolio = await store.GetPortfolioAsync(cancellation);
        ValuationRun = (await store.ListRunsAsync(cancellation)).Where(run => run.Revision == Portfolio.Revision && run.Facts is not null)
            .OrderByDescending(run => run.AsOf).FirstOrDefault();
        Facts = new PortfolioReconstructor().Calculate(Portfolio.Revision, Portfolio.Transactions,
            ValuationRun?.Quotes ?? [], ValuationRun?.AsOf ?? DateTimeOffset.UtcNow);
        Reconciliation = Portfolio.Snapshot is null ? null : new StatementReconciliation().Compare(Portfolio, Portfolio.Snapshot);
    }
}