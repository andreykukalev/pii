using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;

namespace Pii.Web.Pages;

public sealed class ResearchModel(IWorkspaceStore store) : PageModel
{
    public AnalysisRun? Latest { get; private set; }
    public async Task OnGetAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Latest = (await store.ListRunsAsync(cancellation)).Where(run => run.Brief is not null)
            .OrderByDescending(run => run.FinishedAt ?? run.CreatedAt).FirstOrDefault();
    }
}