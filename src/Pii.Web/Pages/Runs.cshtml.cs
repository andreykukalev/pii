using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;

namespace Pii.Web.Pages;

public sealed class RunsModel(IWorkspaceStore store, RunCoordinator coordinator, RuntimeConfiguration runtime) : PageModel
{
    public List<AnalysisRun> Runs { get; private set; } = [];
    public RuntimeConfiguration Runtime => runtime;
    [BindProperty] public string SubmissionKey { get; set; } = Guid.NewGuid().ToString("N");
    [BindProperty] public bool AcceptSpend { get; set; }
    public async Task OnGetAsync(CancellationToken cancellation) => await LoadAsync(cancellation);
    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken cancellation)
    {
        if (!Display.CanonicalId(SubmissionKey)) ModelState.AddModelError("", "Invalid submission key. Reload before submitting.");
        if (!AcceptSpend) ModelState.AddModelError("", "Acknowledge the configured budget before submitting.");
        if (!ModelState.IsValid) { await LoadAsync(cancellation); return Page(); }
        await store.InitializeAsync(cancellation);
        try
        {
            var run = await coordinator.SubmitAsync("on-demand:" + SubmissionKey, cancellation);
            return RedirectToPage("/Run", new { id = run.Id });
        }
        catch (InvalidOperationException)
        {
            ModelState.AddModelError("", "Submission was not accepted. Confirm a portfolio revision first, or reload to resolve a changed baseline.");
            await LoadAsync(cancellation);
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
    }
    private async Task LoadAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Runs = (await store.ListRunsAsync(cancellation)).OrderByDescending(run => run.CreatedAt).ToList();
    }
}