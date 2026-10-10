using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Pii.Application;
using Pii.Domain;

namespace Pii.Web.Pages;

[RequestSizeLimit(MaximumFileBytes + 65536)]
[RequestFormLimits(MultipartBodyLengthLimit = MaximumFileBytes)]
public sealed class ImportsModel(IWorkspaceStore store, ImportCoordinator importer) : PageModel
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    [BindProperty] public IFormFile? Upload { get; set; }
    [BindProperty, Required] public string PreviewId { get; set; } = "";
    [BindProperty] public int BaselineRevision { get; set; }
    [BindProperty] public bool Acknowledge { get; set; }
    public ImportPreview? Preview { get; private set; }
    public PortfolioRevision Portfolio { get; private set; } = null!;
    public PortfolioFacts? ProposedFacts { get; private set; }

    public const string CashSampleCsv = """
        id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio,cashLedgerComplete
        open,,,,USD,,,2025-12-31T10:00:00Z,OpeningCash,0,2000,0,,,,true
        buy,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,10,1000,2,0.9,,,
        sell,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-02T10:00:00Z,Sell,2,240,1,0.92,,,
        """ + "\n";
    public const string SnapshotSampleCsv = """
        asOf,recordType,instrumentId,currency,quantity,cashBalance
        2026-01-03T12:00:00+02:00,Holding,SAMPLE:XNAS:USD,USD,8,
        2026-01-03T12:00:00+02:00,Cash,,USD,,1237
        """ + "\n";

    public IActionResult OnGetCashSample() => File(Encoding.UTF8.GetBytes(CashSampleCsv), "text/csv", "pii-native-cash-sample.csv");
    public IActionResult OnGetSnapshotSample() => File(Encoding.UTF8.GetBytes(SnapshotSampleCsv), "text/csv", "pii-independent-snapshot-sample.csv");

    public async Task<IActionResult> OnGetAsync(string? id, CancellationToken cancellation)
    {
        await LoadAsync(id, cancellation);
        return id is not null && Preview is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken cancellation)
    {
        ModelState.Clear();
        await LoadAsync(null, cancellation);
        var mediaTypes = new[] { "text/csv", "application/csv", "application/vnd.ms-excel", "text/plain", "application/octet-stream" };
        var mediaType = Upload is not null && MediaTypeHeaderValue.TryParse(Upload.ContentType, out var parsedType) ? parsedType.MediaType : null;
        if (Upload is null || Upload.Length is <= 0 or > MaximumFileBytes
            || !Path.GetExtension(Upload.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || !mediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Upload", "Choose a CSV file up to 2 MB. PDF exports are unverified and not supported.");
            return Page();
        }
        try
        {
            await using var input = Upload.OpenReadStream();
            var preview = await importer.PreviewAsync(input, Path.GetFileName(Upload.FileName), cancellation);
            return RedirectToPage(new { id = preview.Id });
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FormatException)
        {
            ModelState.AddModelError("Upload", "CSV could not be parsed as a neutral PII trade/cash or snapshot sample. Check exact headers, explicit timestamp offsets, column counts and invariant decimals.");
            return Page();
        }
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken cancellation)
    {
        var submittedRevision = BaselineRevision;
        await LoadAsync(PreviewId, cancellation);
        if (Preview is null) return NotFound();
        if (!Acknowledge) ModelState.AddModelError("Acknowledge", Preview.Reconciliation?.RequiresAcknowledgment == true
            ? "Explicit acknowledgement of Unknown reconciliation coverage and confirmation are required. Unknown is not a match."
            : "Explicit confirmation is required.");
        if (submittedRevision != Preview.BaselineRevision || Portfolio.Revision != Preview.BaselineRevision)
            ModelState.AddModelError("", "Baseline changed. Upload again and review a new preview before confirming.");
        if (Preview.Issues.Count > 0 || Preview.Reconciliation?.Status == ReconciliationStatus.Mismatch)
            ModelState.AddModelError("", "Resolve the preview issues and snapshot mismatches before confirming.");
        if (!ModelState.IsValid) { Response.StatusCode = StatusCodes.Status409Conflict; return Page(); }
        try
        {
            await store.ConfirmAsync(Preview.Id, Preview.BaselineRevision, cancellation);
            return RedirectToPage("/Portfolio");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            ModelState.AddModelError("", "Confirmation was rejected. The baseline or preview may have changed; review a fresh upload.");
            Response.StatusCode = StatusCodes.Status409Conflict;
            return Page();
        }
    }

    private async Task LoadAsync(string? id, CancellationToken cancellation)
    {
        await store.InitializeAsync(cancellation);
        Portfolio = await store.GetPortfolioAsync(cancellation);
        if (!Display.CanonicalId(id)) return;
        Preview = await store.GetPreviewAsync(id!, cancellation);
        if (Preview is null) return;
        PreviewId = Preview.Id;
        BaselineRevision = Preview.BaselineRevision;
        if (Preview.Issues.Count == 0 || Preview.Snapshot is not null)
            ProposedFacts = new PortfolioReconstructor().Calculate(Preview.BaselineRevision + 1,
            Preview.ProposedTransactions, [], Preview.Snapshot?.AsOf ?? Preview.CreatedAt);
    }
}