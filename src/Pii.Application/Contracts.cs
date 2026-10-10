using System.Text.Json;
using System.Text.Json.Serialization;
using Pii.Domain;

namespace Pii.Application;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Empty JSON payload.");
}

public enum ExecutionMode { FixtureReplay, ModelOnFixtures, Live }
public enum RunStatus { Pending, Running, Completed, Partial, Failed, Cancelled, BudgetExceeded }
public enum AgentRole { InvestmentOrchestrator, PortfolioAnalyst, MarketSectorAnalyst, CompanyEtfResearchAnalyst, ResearchCritic, InvestmentSupervisor }
public enum ClaimKind { SourcedFact, DerivedFact, Interpretation, Hypothesis, Unresolved }
public record PortfolioRevision(int Revision, List<Transaction> Transactions, List<Instrument> Instruments,
    RiskPolicy Policy, DateTimeOffset ConfirmedAt, string Provenance,
    StatementSnapshot? Snapshot = null, ReconciliationResult? Reconciliation = null);
public record ImportPreview(string Id, int BaselineRevision, string Hash, string OriginalBlob,
    List<Transaction> ProposedTransactions, List<Instrument> Instruments, List<string> Issues, int DuplicateCount,
    DateTimeOffset CreatedAt, string Format = "PII-Sample-v1 (not verified Revolut)",
    StatementSnapshot? Snapshot = null, ReconciliationResult? Reconciliation = null);
public record SnapshotHolding(string InstrumentId, string Currency, decimal Quantity, string Source);
public record SnapshotCash(string Currency, decimal Balance, string Source);
public record StatementSnapshot(DateTimeOffset AsOf, List<SnapshotHolding> Holdings, List<SnapshotCash> CashBalances, string Source);
public enum ReconciliationStatus { Match, Mismatch, Unknown }
public record ReconciliationItem(string Metric, string InstrumentId, string Currency, decimal? SnapshotAmount,
    decimal? LedgerAmount, ReconciliationStatus Status, string Reason, string? Source = null)
{
    public decimal? Difference => SnapshotAmount - LedgerAmount;
}
public record ReconciliationResult(int LedgerRevision, DateTimeOffset AsOf, List<ReconciliationItem> Items)
{
    public ReconciliationStatus Status => Items.Any(item => item.Status == ReconciliationStatus.Mismatch) ? ReconciliationStatus.Mismatch
        : Items.Count == 0 || Items.Any(item => item.Status == ReconciliationStatus.Unknown) ? ReconciliationStatus.Unknown : ReconciliationStatus.Match;
    public bool RequiresAcknowledgment => Items.Count == 0 || Items.Any(item => item.Status == ReconciliationStatus.Unknown);
}
public record EvidenceRecord(string Id, string InstrumentId, string Source, string Url, string Kind,
    DateTimeOffset PublishedAt, DateTimeOffset RetrievedAt, string Text, Dictionary<string, decimal> Numbers,
    bool ModelProcessingAllowed, string Provenance, DateTimeOffset? PeriodEnd = null);
public record SourceResult(List<EvidenceRecord> Evidence, List<Quote> Quotes, string Status, string? Error = null);
public record Claim(string Id, string InstrumentId, Horizon Horizon, ClaimKind Kind, string Text,
    List<string> EvidenceIds, string? NumericReference = null, List<string>? CounterEvidenceIds = null);
public record Assessment(string InstrumentId, Horizon Horizon, RecommendationCategory Category,
    string Thesis, string Counterargument, List<string> ClaimIds, List<string> Risks);
public record FollowUp(string InstrumentId, string Question, string Reason);
public record AgentOutput(List<Claim> Claims, List<Assessment> Assessments, List<FollowUp> FollowUps,
    List<string> Uncertainties, string Status = "Succeeded");
public record AgentTaskRequest(string SchemaVersion, string TaskId, string? ParentTaskId, string RunId,
    int Revision, AgentRole Role, string InstrumentId, string Intent, List<string> EvidenceIds,
    DateTimeOffset Deadline, int Attempt, string PermissionProfile = "ResearchReadOnly");
public record AgentTaskResult(AgentTaskRequest Request, AgentOutput Output, string Status, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);
public record OrchestratorDecision(string Action, string Target, string InstrumentId, string Question, string Rationale);
public record ToolDescriptor(string Name, string Description, string InputSchema, List<AgentRole> AllowedRoles);
public record ModelMessage(string Role, string Content, List<ModelToolCall>? ToolCalls = null, string? ToolCallId = null);
public record ModelToolCall(string Id, string Name, string Arguments);
public record ModelReply(string Content, List<ModelToolCall> ToolCalls, long? InputTokens, long? OutputTokens,
    string? ProviderRequestId, string Status = "Succeeded");
public record ModelRequest(List<ModelMessage> Messages, List<ToolDescriptor> Tools, string OutputSchema,
    int MaximumOutputTokens, string RunId, string TaskId);
public record TraceRecord(string Id, string Kind, string Role, string Summary, DateTimeOffset Time,
    string? Artifact = null, string Status = "Succeeded");
public record UsageRecord(string Id, string TaskId, long? InputTokens, long? OutputTokens, decimal ReservedUsd,
    decimal? ActualUsd, string State, string? ProviderRequestId, DateTimeOffset Time);
public record BriefSection(string Title, string Body);
public record InvestmentBrief(string RunId, int Revision, DateTimeOffset AsOf, ExecutionMode Mode, string AiStatus,
    PortfolioFacts Facts, List<BriefSection> Sections, List<Assessment> Assessments, List<GateResult> Gates,
    List<string> Limitations, string? PreviousRunId);
public sealed class AnalysisRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string IdempotencyKey { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public int Revision { get; set; }
    public ExecutionMode Mode { get; set; }
    public DateTimeOffset AsOf { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public string AiStatus { get; set; } = "Pending";
    public string? Owner { get; set; }
    public int Generation { get; set; }
    public long Version { get; set; }
    public DateTimeOffset? ClaimExpires { get; set; }
    public bool CancelRequested { get; set; }
    public int Decisions { get; set; }
    public int ModelSubmissions { get; set; }
    public int ToolInvocations { get; set; }
    public int ResearchTasks { get; set; }
    public int CriticPasses { get; set; }
    public decimal ReservedUsd { get; set; }
    public long ReservedTokens { get; set; }
    public List<string> Fingerprints { get; set; } = [];
    public List<TraceRecord> Trace { get; set; } = [];
    public List<EvidenceRecord> Evidence { get; set; } = [];
    public List<Quote> Quotes { get; set; } = [];
    public List<AgentTaskResult> Tasks { get; set; } = [];
    public List<UsageRecord> Usage { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public PortfolioRevision? Portfolio { get; set; }
    public PortfolioFacts? Facts { get; set; }
    public InvestmentBrief? Brief { get; set; }
    public string? ReportBlob { get; set; }
    public string? PreviousRunId { get; set; }
    public InvestmentBrief? PreviousBrief { get; set; }
    public string CodeVersion { get; set; } = "pii-v1";
    public string PromptVersion { get; set; } = "research-v1";
    public string ConfigVersion { get; set; } = "v1";
    public string ModelBinding { get; set; } = "Unconfigured";
}
public record RuntimeLimits(int MaximumDecisions = 12, int MaximumResearchTasks = 5, int MaximumCriticPasses = 2,
    int MaximumTools = 32, int MaximumModelSubmissions = 24, int MaximumRunMinutes = 10,
    int MaximumOutputTokens = 2500, int MaximumInputCharacters = 40000,
    decimal PerRunUsd = 1, decimal MonthlyAiUsd = 20, decimal MaximumCallUsd = .15m,
    long MaximumRunTokens = 350000, long MaximumCallTokens = 12000);
public record RuntimeConfiguration(ExecutionMode Mode, RuntimeLimits Limits, List<Instrument> CandidateCatalog,
    string ModelBinding, bool LiveAuthorized, string Diagnostic, List<string>? ExcludedInstrumentIds = null);

public interface IWorkspaceStore
{
    Task InitializeAsync(CancellationToken cancellation);
    Task<PortfolioRevision> GetPortfolioAsync(CancellationToken cancellation);
    Task SavePreviewAsync(ImportPreview preview, CancellationToken cancellation);
    Task<ImportPreview?> GetPreviewAsync(string id, CancellationToken cancellation);
    Task<PortfolioRevision> ConfirmAsync(string previewId, int expectedRevision, CancellationToken cancellation);
    Task<PortfolioRevision> UpdatePolicyAsync(int expectedRevision, decimal maximumPositionPercent, decimal? maximumTacticalPercent, CancellationToken cancellation)
        => throw new NotSupportedException("This workspace store does not support policy updates.");
    Task<AnalysisRun> CreateRunAsync(AnalysisRun run, CancellationToken cancellation);
    Task<AnalysisRun?> GetRunAsync(string id, CancellationToken cancellation);
    Task<List<AnalysisRun>> ListRunsAsync(CancellationToken cancellation);
    Task<AnalysisRun?> ClaimAsync(string owner, DateTimeOffset now, TimeSpan lifetime, CancellationToken cancellation);
    Task SaveRunAsync(AnalysisRun run, string owner, int generation, CancellationToken cancellation);
    Task CancelAsync(string id, CancellationToken cancellation);
    Task<bool> ReserveMonthlyAsync(string reservationId, decimal usd, decimal maximumUsd, DateTimeOffset now, CancellationToken cancellation);
    Task DeletePortfolioAsync(CancellationToken cancellation);
}
public interface IArtifactStore
{
    Task<string> WriteAsync(string name, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellation);
    Task<byte[]> ReadAsync(string name, CancellationToken cancellation);
    Task DeleteAllAsync(CancellationToken cancellation);
}
public interface IWorkDispatch
{
    Task SignalAsync(string runId, CancellationToken cancellation);
}
public interface IResearchSources
{
    Task<SourceResult> FetchAsync(string kind, Instrument instrument, ExecutionMode mode, DateTimeOffset cutoff, CancellationToken cancellation);
}
public interface IModelGateway
{
    Task<ModelReply> CompleteAsync(ModelRequest request, ExecutionMode mode, CancellationToken cancellation);
    decimal? EstimateUsd(long? inputTokens, long? outputTokens);
}