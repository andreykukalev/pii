using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;

namespace Pii.Web.Pages;

public sealed class RunModel(IWorkspaceStore store, RunCoordinator coordinator) : PageModel
{
    public AnalysisRun Run { get; private set; } = null!;
    [BindProperty] public string RetryKey { get; set; } = Guid.NewGuid().ToString("N");
    [BindProperty] public bool AcceptSpend { get; set; }
    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellation) =>
        await LoadAsync(id, cancellation) ? Page() : NotFound();
    public async Task<IActionResult> OnPostCancelAsync(string id, CancellationToken cancellation)
    {
        if (!await LoadAsync(id, cancellation)) return NotFound();
        if (Display.Active(Run)) await store.CancelAsync(Run.Id, cancellation);
        return RedirectToPage(new { id = Run.Id });
    }
    public async Task<IActionResult> OnPostRetryAsync(string id, CancellationToken cancellation)
    {
        if (!await LoadAsync(id, cancellation)) return NotFound();
        if (Display.Active(Run) || !AcceptSpend || !Display.CanonicalId(RetryKey))
        {
            ModelState.AddModelError("", "A terminal run and explicit new-spend acknowledgement with a valid key are required.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
        try
        {
            var next = await coordinator.SubmitAsync($"retry:{Run.Id}:{RetryKey}", cancellation);
            return RedirectToPage(new { id = next.Id });
        }
        catch (InvalidOperationException)
        {
            ModelState.AddModelError("", "New-run submission was rejected. Confirm a current portfolio revision; the old run remains unchanged.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
    }
    private async Task<bool> LoadAsync(string id, CancellationToken cancellation)
    {
        if (!Display.CanonicalId(id)) return false;
        await store.InitializeAsync(cancellation);
        var run = await store.GetRunAsync(id, cancellation);
        if (run is null) return false;
        Run = run;
        return true;
    }
}