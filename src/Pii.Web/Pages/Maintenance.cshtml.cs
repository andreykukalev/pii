using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Pii.Application;
using Pii.Infrastructure;

namespace Pii.Web.Pages;

[RequestSizeLimit(65536)]
public sealed class MaintenanceModel(IWorkspaceStore store, IArtifactStore artifacts,
    IDbContextFactory<WorkspaceDbContext> databaseFactory) : PageModel
{
    [BindProperty] public int ExpectedRevision { get; set; }
    [BindProperty] public bool AcceptExportScope { get; set; }
    [BindProperty] public bool ExclusiveMaintenance { get; set; }
    [BindProperty] public bool AcceptPermanentLoss { get; set; }
    [BindProperty] public string? Confirmation { get; set; }
    public PortfolioRevision Portfolio { get; private set; } = null!;
    public WorkspaceDeletionState? DeletionState { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellation)
    {
        await LoadAsync(cancellation);
        ExpectedRevision = Portfolio.Revision;
    }

    public async Task<IActionResult> OnPostExportAsync(CancellationToken cancellation)
    {
        await LoadAsync(cancellation);
        if (!AcceptExportScope) ModelState.AddModelError(nameof(AcceptExportScope), "Acknowledge the bounded export and source retention rights.");
        if (!ModelState.IsValid) { Response.StatusCode = StatusCodes.Status400BadRequest; return Page(); }
        try
        {
            var bytes = await new WorkspaceMaintenance(store, artifacts).ExportAsync(ExpectedRevision, cancellation);
            return File(bytes, "application/json", $"pii-workspace-revision-{ExpectedRevision}.json");
        }
        catch (InvalidDataException)
        {
            ModelState.AddModelError("", "Export exceeds 8 MiB. No partial file was returned; use individual downloads or an authorized administrator export.");
            Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return Page();
        }
        catch (InvalidOperationException)
        {
            ModelState.AddModelError("", "Portfolio changed. Reload Maintenance and review the current revision before exporting.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken cancellation)
    {
        await LoadAsync(cancellation);
        if (!AcceptPermanentLoss || Confirmation != "DELETE WORKSPACE" || !ExclusiveMaintenance)
            ModelState.AddModelError("", "Acknowledge permanent history loss, confirm worker shutdown and paused submissions, and type DELETE WORKSPACE.");
        if (!ModelState.IsValid) { Response.StatusCode = StatusCodes.Status400BadRequest; return Page(); }
        try
        {
            DeletionState = await new WorkspaceMaintenance(store, artifacts).DeleteAsync(ExpectedRevision,
                ExclusiveMaintenance, HasRunningClaimsAsync, cancellation);
        }
        catch (InvalidOperationException)
        {
            ModelState.AddModelError("", "Deletion refused: portfolio changed or a Running claim remains, even if expired. Cancel research in Runs, let workers settle and exit, then reload.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
        await LoadAsync(cancellation);
        ExpectedRevision = Portfolio.Revision;
        ModelState.Remove(nameof(ExpectedRevision));
        if (DeletionState == WorkspaceDeletionState.SqlDataDeletedArtifactsPending)
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return Page();
    }

    private async Task<bool> HasRunningClaimsAsync(CancellationToken cancellation)
    {
        if (store is not SqlWorkspaceStore)
            return (await store.ListRunsAsync(cancellation)).Any(run => run.Status == RunStatus.Running);
        await using var database = await databaseFactory.CreateDbContextAsync(cancellation);
        return await database.Runs.AnyAsync(run => run.Status == RunStatus.Running, cancellation);
    }

    private async Task LoadAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Portfolio = await store.GetPortfolioAsync(cancellation);
    }
}