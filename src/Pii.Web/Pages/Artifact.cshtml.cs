using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text;
using Pii.Application;
using Pii.Infrastructure;

namespace Pii.Web.Pages;

public sealed class ArtifactModel(IWorkspaceStore store, IArtifactStore artifacts) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string kind, string id, CancellationToken cancellation)
    {
        if (!Display.CanonicalId(id)) return NotFound();
        await store.InitializeAsync(cancellation);
        string? reference;
        string filename;
        switch (kind)
        {
            case "audit":
                var audit = await store.GetRunAsync(id, cancellation);
                if (audit is null) return NotFound();
                var payload = new
                {
                    audit.Id, audit.Revision, audit.Mode, audit.Status, audit.AiStatus, audit.AsOf, audit.CreatedAt, audit.FinishedAt,
                    audit.Generation, audit.Version, audit.SchemaVersion, audit.CodeVersion, audit.PromptVersion, audit.ConfigVersion,
                    audit.ModelBinding, audit.Decisions, audit.ToolInvocations, audit.ResearchTasks, audit.CriticPasses,
                    audit.ModelSubmissions, audit.ReservedTokens, audit.ReservedUsd, audit.Errors, audit.Facts,
                    Tasks = audit.Tasks.Select(task => new { task.Request.TaskId, task.Request.ParentTaskId, task.Request.Role,
                        task.Request.InstrumentId, task.Request.Attempt, task.StartedAt, task.FinishedAt, task.Status, task.Output }),
                    Trace = audit.Trace.Select(trace => new { trace.Id, trace.Kind, trace.Role, trace.Summary, trace.Time, trace.Status }),
                    audit.Evidence, audit.Usage, audit.Brief
                };
                return File(Encoding.UTF8.GetBytes(JsonDefaults.Serialize(payload)), "application/json", "run-audit.json");
            case "report":
                var run = await store.GetRunAsync(id, cancellation);
                reference = run?.Brief is null ? null : run.ReportBlob;
                filename = "investment-brief.json";
                break;
            case "original":
                reference = (await store.GetPreviewAsync(id, cancellation))?.OriginalBlob;
                filename = "original-import.csv";
                break;
            default: return NotFound();
        }
        if (string.IsNullOrWhiteSpace(reference)) return NotFound();
        if (kind == "original" && (!reference.StartsWith("originals/", StringComparison.Ordinal) || !reference.EndsWith(".csv", StringComparison.Ordinal))
            || kind == "report" && !reference.StartsWith("reports/", StringComparison.Ordinal)) return NotFound();
        try { ArtifactNames.Validate(reference); }
        catch (ArgumentException) { return NotFound(); }
        if (kind == "report") filename = reference.EndsWith(".html", StringComparison.Ordinal) ? "investment-brief.html"
            : reference.EndsWith(".md", StringComparison.Ordinal) ? "investment-brief.md" : "investment-brief.json";
        var bytes = await artifacts.ReadAsync(reference, cancellation);
        return File(bytes, "application/octet-stream", filename);
    }
}