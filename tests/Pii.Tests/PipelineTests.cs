using System.Text;
using Pii.Application;
using Pii.Domain;
using Pii.Infrastructure;
using Xunit;

namespace Pii.Tests;

public sealed class PipelineTests
{
    private static readonly RuntimeConfiguration Configuration = new(ExecutionMode.FixtureReplay, new(), SyntheticResearchFixture.Catalog, "Fixture-v1", false, "Fixtures only");

    [Fact]
    public async Task ActualRuntimeDelegatesBothHorizonsOutsideAndCriticFollowUp()
    {
        var (store, artifacts, run) = await Setup();
        var sources = new ResearchSources(ResearchHttp.CreateClient(), new(), Configuration, true);
        await new InvestmentPipeline(store, artifacts, sources, new FixtureModelGateway(), Configuration).ExecuteAsync(run, "test-owner", default);
        var result = await store.GetRunAsync(run.Id, default);
        Assert.Equal(RunStatus.Completed, result!.Status);
        Assert.NotNull(result.Brief);
        Assert.Equal(9, result.Brief.Sections.Count);
        foreach (var role in Enum.GetValues<AgentRole>()) Assert.Contains(result.Tasks, item => item.Request.Role == role);
        Assert.Equal(2, result.CriticPasses);
        Assert.Contains(result.Tasks, item => item.Request.Intent.StartsWith("Verify", StringComparison.Ordinal));
        Assert.Contains(result.Brief.Assessments, item => item.InstrumentId == "OTHER:XNAS:USD" && item.Horizon == Horizon.Tactical);
        Assert.All(result.Usage, item => { Assert.Null(item.ActualUsd); Assert.Equal("FixtureReplay/NoPaidCall", item.State); });
        Assert.All(result.Brief.Gates, item => Assert.DoesNotContain("suitability approval", item.Reason));
        Assert.True(result.ModelSubmissions > 10);
        Assert.Contains(result.Trace, item => item.Kind == "DelegationResult");
        Assert.Contains(result.Trace, item => item.Kind == "FollowUpDisposition" && item.Summary.StartsWith("Accepted:", StringComparison.Ordinal));
        Assert.Contains(result.Trace, item => item.Artifact is not null);
    }

    [Fact]
    public async Task ChangedEvidenceChangesFollowUpGraph()
    {
        var (store, artifacts, run) = await Setup();
        await new InvestmentPipeline(store, artifacts, new ClearEvidenceSources(), new FixtureModelGateway(), Configuration).ExecuteAsync(run, "test-owner", default);
        var result = await store.GetRunAsync(run.Id, default);
        Assert.Equal(RunStatus.Completed, result!.Status);
        Assert.Equal(1, result.CriticPasses);
        Assert.DoesNotContain(result.Tasks, item => item.Request.Intent.StartsWith("Verify", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalBudgetStopsWithoutSuccessfulAi()
    {
        var (store, artifacts, run) = await Setup();
        var config = Configuration with { Limits = new(MaximumModelSubmissions: 1) };
        await new InvestmentPipeline(store, artifacts, new ClearEvidenceSources(), new FixtureModelGateway(), config).ExecuteAsync(run, "test-owner", default);
        Assert.Equal("BudgetExceeded", run.AiStatus);
        Assert.Empty(run.Brief!.Assessments);
        Assert.Equal(RunStatus.Partial, run.Status);
    }

    [Fact]
    public async Task UnknownCitationCannotPassClaimValidation()
    {
        var (_, _, run) = await Setup();
        var task = new AgentTaskRequest("v1", "task", run.Id, run.Id, run.Revision, AgentRole.CompanyEtfResearchAnalyst, "SAMPLE:XNAS:USD", "Research", [], run.Deadline, 1);
        Assert.Throws<ResearchValidationException>(() => EvidenceValidator.Validate(new([new("claim", "SAMPLE:XNAS:USD", Horizon.LongTerm,
            ClaimKind.SourcedFact, "Unsupported source claim", ["invented"])], [], [], []), run, task, Configuration.CandidateCatalog));
        Assert.Throws<ResearchValidationException>(() => EvidenceValidator.Validate(new([new("claim", "SAMPLE:XNAS:USD", Horizon.LongTerm,
            ClaimKind.Hypothesis, "Invented return of 10 percent", [])], [], [], []), run, task, Configuration.CandidateCatalog));
    }

    [Fact]
    public async Task OverlapsPreserveGenuineIdenticalOccurrencesAndStableConflicts()
    {
        var store = new TestStore(Configuration);
        var importer = new ImportCoordinator(store, new TestArtifacts());
        const string header = "id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio\n";
        const string trade = ",SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,1,100,0,0.9,,\n";
        var preview = await importer.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(header + trade + trade)), "sample.csv", default);
        Assert.Empty(preview.Issues);
        Assert.Equal(2, preview.ProposedTransactions.Count);
        await store.ConfirmAsync(preview.Id, 0, default);
        var overlap = await importer.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(header + trade + trade + trade)), "renamed.csv", default);
        Assert.Equal(2, overlap.DuplicateCount);
        Assert.Equal(3, overlap.ProposedTransactions.Count);
        var stable = await importer.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(header + "same" + trade)), "stable.csv", default);
        await store.ConfirmAsync(stable.Id, 1, default);
        var conflict = await importer.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes((header + "same" + trade).Replace(",100,", ",200,"))), "conflict.csv", default);
        Assert.NotEmpty(conflict.Issues);
    }

    internal static async Task<(TestStore Store, TestArtifacts Artifacts, AnalysisRun Run)> Setup()
    {
        var store = new TestStore(Configuration);
        var artifacts = new TestArtifacts();
        var importer = new ImportCoordinator(store, artifacts);
        const string csv = "id,instrumentId,symbol,exchange,currency,kind,sector,time,type,quantity,gross,fee,eurPerUnit,cik,splitRatio\n"
            + "buy,SAMPLE:XNAS:USD,SAMPLE,XNAS,USD,Stock,Technology,2026-01-01T10:00:00Z,Buy,10,1000,2,0.9,,\n";
        var preview = await importer.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "sample.csv", default);
        await store.ConfirmAsync(preview.Id, 0, default);
        await new RunCoordinator(store, new DatabasePollingDispatch(), Configuration).SubmitAsync(Guid.NewGuid().ToString("N"), default);
        return (store, artifacts, (await store.ClaimAsync("test-owner", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default))!);
    }
    private sealed class ClearEvidenceSources : IResearchSources
    {
        public Task<SourceResult> FetchAsync(string kind, Instrument instrument, ExecutionMode mode, DateTimeOffset cutoff, CancellationToken cancellation)
        {
            var result = SyntheticResearchFixture.Fetch(kind, instrument, cutoff);
            return Task.FromResult(result with { Evidence = result.Evidence.Select(item => item with { Text = "Dated declared test source." }).ToList() });
        }
    }
}

internal sealed class TestArtifacts : IArtifactStore
{
    private readonly Dictionary<string, byte[]> values = [];
    public Task<string> WriteAsync(string name, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellation)
    { values.TryAdd(name, bytes.ToArray()); return Task.FromResult(name); }
    public Task<byte[]> ReadAsync(string name, CancellationToken cancellation) => Task.FromResult(values[name]);
    public Task DeleteAllAsync(CancellationToken cancellation) { values.Clear(); return Task.CompletedTask; }
}
internal sealed class TestStore(RuntimeConfiguration configuration) : IWorkspaceStore
{
    private PortfolioRevision portfolio = new(0, [], [], new(), DateTimeOffset.MinValue, "Test");
    private readonly Dictionary<string, ImportPreview> previews = [];
    private readonly Dictionary<string, AnalysisRun> runs = [];
    private readonly Dictionary<string, decimal> reservations = [];
    private static AnalysisRun Copy(AnalysisRun run) => JsonDefaults.Parse<AnalysisRun>(JsonDefaults.Serialize(run));
    public Task InitializeAsync(CancellationToken cancellation) => Task.CompletedTask;
    public Task<PortfolioRevision> GetPortfolioAsync(CancellationToken cancellation) => Task.FromResult(portfolio);
    public Task SavePreviewAsync(ImportPreview preview, CancellationToken cancellation) { previews[preview.Id] = preview; return Task.CompletedTask; }
    public Task<ImportPreview?> GetPreviewAsync(string id, CancellationToken cancellation) => Task.FromResult(previews.GetValueOrDefault(id));
    public Task<PortfolioRevision> ConfirmAsync(string previewId, int expectedRevision, CancellationToken cancellation)
    {
        var preview = previews[previewId];
        if (portfolio.Revision != expectedRevision || preview.Issues.Count > 0) throw new InvalidOperationException("Conflict");
        portfolio = new(expectedRevision + 1, preview.ProposedTransactions, preview.Instruments, portfolio.Policy, DateTimeOffset.UtcNow, "Synthetic");
        return Task.FromResult(portfolio);
    }
    public Task<AnalysisRun> CreateRunAsync(AnalysisRun run, CancellationToken cancellation)
    {
        var existing = runs.Values.FirstOrDefault(item => item.IdempotencyKey == run.IdempotencyKey);
        if (existing is not null) return Task.FromResult(Copy(existing));
        run.Portfolio = portfolio; run.Deadline = DateTimeOffset.UtcNow.AddMinutes(configuration.Limits.MaximumRunMinutes);
        run.Mode = configuration.Mode; run.ModelBinding = configuration.ModelBinding; run.Version = 1;
        runs[run.Id] = Copy(run); return Task.FromResult(run);
    }
    public Task<AnalysisRun?> GetRunAsync(string id, CancellationToken cancellation) => Task.FromResult(runs.TryGetValue(id, out var run) ? Copy(run) : null);
    public Task<List<AnalysisRun>> ListRunsAsync(CancellationToken cancellation) => Task.FromResult(runs.Values.OrderByDescending(item => item.CreatedAt).Select(Copy).ToList());
    public Task<AnalysisRun?> ClaimAsync(string owner, DateTimeOffset now, TimeSpan lifetime, CancellationToken cancellation)
    {
        var run = runs.Values.FirstOrDefault(item => item.Status == RunStatus.Pending);
        if (run is null) return Task.FromResult<AnalysisRun?>(null);
        run.Owner = owner; run.Generation++; run.Status = RunStatus.Running; run.ClaimExpires = now + lifetime; run.Version++;
        return Task.FromResult<AnalysisRun?>(Copy(run));
    }
    public Task SaveRunAsync(AnalysisRun run, string owner, int generation, CancellationToken cancellation)
    { RunCheckpoint.Validate(runs[run.Id], run, owner, generation, DateTimeOffset.UtcNow); run.Version++; runs[run.Id] = Copy(run); return Task.CompletedTask; }
    public Task CancelAsync(string id, CancellationToken cancellation) { runs[id].CancelRequested = true; return Task.CompletedTask; }
    public Task<bool> ReserveMonthlyAsync(string id, decimal usd, decimal maximumUsd, DateTimeOffset now, CancellationToken cancellation)
    {
        if (reservations.ContainsKey(id)) return Task.FromResult(true);
        if (reservations.Values.Sum() + usd > maximumUsd) return Task.FromResult(false);
        reservations[id] = usd; return Task.FromResult(true);
    }
    public Task DeletePortfolioAsync(CancellationToken cancellation) { runs.Clear(); previews.Clear(); portfolio = new(0, [], [], new(), DateTimeOffset.MinValue, "Test"); return Task.CompletedTask; }
}