using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;

namespace Pii.Web.Pages;

[RequestSizeLimit(65536)]
public sealed class SettingsModel(IWorkspaceStore store, RuntimeConfiguration runtime, IWebHostEnvironment environment,
    IConfiguration configuration) : PageModel
{
    public RuntimeConfiguration Runtime => runtime;
    public string EnvironmentName => environment.EnvironmentName;
    public string Authentication => environment.IsDevelopment() && configuration.GetValue<bool>("Pii:AllowDevelopmentIdentity") ? "Explicit loopback Development identity" : "Microsoft Entra / authenticated tenant and object allowlist";
    public PortfolioRevision Portfolio { get; private set; } = null!;
    [BindProperty] public int ExpectedRevision { get; set; }
    [BindProperty, Required, Range(typeof(decimal), "0", "100")]
    public decimal? MaximumPositionPercent { get; set; }
    [BindProperty, Range(typeof(decimal), "0", "100")]
    public decimal? MaximumTacticalPercent { get; set; }
    [BindProperty] public bool AcceptInformationalPolicy { get; set; }
    public async Task OnGetAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Portfolio = await store.GetPortfolioAsync(cancellation);
        ExpectedRevision = Portfolio.Revision;
        MaximumPositionPercent = Portfolio.Policy.MaximumPositionPercent;
        MaximumTacticalPercent = Portfolio.Policy.MaximumTacticalPercent;
    }

    public async Task<IActionResult> OnPostPolicyAsync(CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Portfolio = await store.GetPortfolioAsync(cancellation);
        if (MaximumPositionPercent is <= 0)
            ModelState.AddModelError(nameof(MaximumPositionPercent), "Position percentage must be greater than zero.");
        if (!AcceptInformationalPolicy)
            ModelState.AddModelError(nameof(AcceptInformationalPolicy), "Explicit acknowledgement of informational policy and pending research is required.");
        if (!ModelState.IsValid) { Response.StatusCode = StatusCodes.Status400BadRequest; return Page(); }
        try
        {
            await store.UpdatePolicyAsync(ExpectedRevision, MaximumPositionPercent!.Value, MaximumTacticalPercent, cancellation);
            return RedirectToPage();
        }
        catch (InvalidOperationException)
        {
            ModelState.AddModelError("", "Policy update refused. Reload Settings and review a confirmed current revision before submitting again.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
        catch (NotSupportedException)
        {
            ModelState.AddModelError("", "Policy updates are unavailable for this workspace store; no policy was saved.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Page();
        }
    }
}