using Pii.Application;
using Pii.Domain;

namespace Pii.Web.Tests;

public sealed class TestWorkspace : IWorkspaceStore, IArtifactStore, IWorkDispatch
{
    public PortfolioRevision Portfolio { get; set; } = new(1, [], [], new(), DateTimeOffset.UtcNow, "TestOnly");
    public Dictionary<string, ImportPreview> Previews { get; } = [];
    public Dictionary<string, AnalysisRun> Runs { get; } = [];
    public Dictionary<string, byte[]> Artifacts { get; } = [];
    public int InitializationCalls { get; private set; }
    public int ConfirmationCalls { get; private set; }
    public int ArtifactReads { get; private set; }
    public int PolicyCalls { get; private set; }
    public int DeletionCalls { get; private set; }
    public bool FailArtifactDeletion { get; set; }
    private static T Copy<T>(T value) => JsonDefaults.Parse<T>(JsonDefaults.Serialize(value));
    public Task InitializeAsync(CancellationToken cancellation) { InitializationCalls++; return Task.CompletedTask; }
    public Task<PortfolioRevision> GetPortfolioAsync(CancellationToken cancellation) => Task.FromResult(Copy(Portfolio));
    public Task SavePreviewAsync(ImportPreview preview, CancellationToken cancellation) { Previews[preview.Id] = Copy(preview); return Task.CompletedTask; }
    public Task<ImportPreview?> GetPreviewAsync(string id, CancellationToken cancellation) => Task.FromResult(Previews.TryGetValue(id, out var value) ? Copy(value) : null);
    public Task<PortfolioRevision> ConfirmAsync(string previewId, int expectedRevision, CancellationToken cancellation)
    {
        var preview = Previews[previewId];
        if (expectedRevision != Portfolio.Revision || preview.BaselineRevision != expectedRevision || preview.Issues.Count != 0)
            throw new InvalidOperationException("Stale or invalid preview.");
        ConfirmationCalls++;
        Portfolio = new(Portfolio.Revision + 1, preview.ProposedTransactions, preview.Instruments, Portfolio.Policy, DateTimeOffset.UtcNow, "ConfirmedSyntheticTest");
        return Task.FromResult(Copy(Portfolio));
    }
    public Task<AnalysisRun> CreateRunAsync(AnalysisRun run, CancellationToken cancellation)
    {
        var previous = Runs.Values.FirstOrDefault(item => item.IdempotencyKey == run.IdempotencyKey);
        if (previous is not null) return Task.FromResult(Copy(previous));
        run.Portfolio = Copy(Portfolio);
        run.Revision = Portfolio.Revision;
        Runs[run.Id] = Copy(run);
        return Task.FromResult(Copy(run));
    }
    public Task<AnalysisRun?> GetRunAsync(string id, CancellationToken cancellation) => Task.FromResult(Runs.TryGetValue(id, out var value) ? Copy(value) : null);
    public Task<List<AnalysisRun>> ListRunsAsync(CancellationToken cancellation) => Task.FromResult(Runs.Values.Select(Copy).ToList());
    public Task<AnalysisRun?> ClaimAsync(string owner, DateTimeOffset now, TimeSpan lifetime, CancellationToken cancellation) => Task.FromResult<AnalysisRun?>(null);
    public Task SaveRunAsync(AnalysisRun run, string owner, int generation, CancellationToken cancellation) { Runs[run.Id] = Copy(run); return Task.CompletedTask; }
    public Task CancelAsync(string id, CancellationToken cancellation) { Runs[id].CancelRequested = true; return Task.CompletedTask; }
    public Task<bool> ReserveMonthlyAsync(string reservationId, decimal usd, decimal maximumUsd, DateTimeOffset now, CancellationToken cancellation) => Task.FromResult(true);
    public Task<PortfolioRevision> UpdatePolicyAsync(int expectedRevision, decimal maximumPositionPercent, decimal? maximumTacticalPercent, CancellationToken cancellation)
    {
        if (Portfolio.Revision == 0 || Portfolio.Revision != expectedRevision) throw new InvalidOperationException("Stale revision.");
        if (maximumPositionPercent is <= 0 or > 100 || maximumTacticalPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumPositionPercent));
        PolicyCalls++;
        Portfolio = Portfolio with { Revision = Portfolio.Revision + 1, Policy = new(Portfolio.Policy.Version + 1, maximumPositionPercent, maximumTacticalPercent) };
        var run = new AnalysisRun { IdempotencyKey = $"policy:{Portfolio.Revision}", Revision = Portfolio.Revision, Portfolio = Copy(Portfolio) };
        Runs[run.Id] = run;
        return Task.FromResult(Copy(Portfolio));
    }
    public Task DeletePortfolioAsync(CancellationToken cancellation)
    {
        DeletionCalls++;
        Portfolio = new(0, [], [], new(), DateTimeOffset.MinValue, "DeletedTestWorkspace");
        Runs.Clear();
        Previews.Clear();
        return Task.CompletedTask;
    }
    public Task<string> WriteAsync(string name, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellation) { Artifacts[name] = bytes.ToArray(); return Task.FromResult(name); }
    public Task<byte[]> ReadAsync(string name, CancellationToken cancellation) { ArtifactReads++; return Task.FromResult(Artifacts[name]); }
    public Task DeleteAllAsync(CancellationToken cancellation)
    {
        if (FailArtifactDeletion) throw new IOException("Test failure with sensitive diagnostic not for display.");
        Artifacts.Clear();
        return Task.CompletedTask;
    }
    public Task SignalAsync(string runId, CancellationToken cancellation) => Task.CompletedTask;
}