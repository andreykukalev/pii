using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;

namespace Pii.Web.Pages;

public sealed class IndexModel(IWorkspaceStore store) : PageModel
{
    public AnalysisRun? Latest { get; private set; }
    public List<AnalysisRun> Recent { get; private set; } = [];
    public async Task OnGetAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        var runs = await store.ListRunsAsync(cancellation);
        Recent = runs.OrderByDescending(run => run.CreatedAt).Take(5).ToList();
        Latest = runs.Where(run => run.Brief is not null)
            .OrderByDescending(run => run.FinishedAt ?? run.CreatedAt).FirstOrDefault();
    }
}