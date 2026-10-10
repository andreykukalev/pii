using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pii.Application;
using Pii.Domain;

namespace Pii.Infrastructure;

public sealed class WorkspaceConflictException(string message) : InvalidOperationException(message);

public sealed class SqlWorkspaceStore(IDbContextFactory<WorkspaceDbContext> factory, RuntimeConfiguration configuration) : IWorkspaceStore
{
    private const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public async Task InitializeAsync(CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await database.Database.EnsureCreatedAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        if (await HeadAsync(database, cancellation) is null) database.Heads.Add(new());
        await database.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
    }

    public async Task<PortfolioRevision> GetPortfolioAsync(CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        var payload = await (from head in database.Heads
            join revision in database.Revisions on head.CurrentRevision equals revision.Revision
            select revision.Payload).SingleOrDefaultAsync(cancellation);
        return payload is null ? EmptyPortfolio() : JsonDefaults.Parse<PortfolioRevision>(payload);
    }

    public async Task SavePreviewAsync(ImportPreview preview, CancellationToken cancellation)
    {
        ValidateKey(preview.Id, 128);
        ValidateKey(preview.Hash, 128);
        ValidateLedger(preview.ProposedTransactions);
        var payload = SerializeBounded(preview);
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        await RequireHeadAsync(database, cancellation);
        var existing = await database.Previews.SingleOrDefaultAsync(row => row.Id == preview.Id, cancellation);
        if (existing is not null)
        {
            if (existing.Payload != payload) throw new WorkspaceConflictException("Preview IDs are immutable.");
        }
        else
        {
            var duplicate = await database.Previews.SingleOrDefaultAsync(row => row.Hash == preview.Hash && row.BaselineRevision == preview.BaselineRevision, cancellation);
            if (duplicate is not null) throw new WorkspaceConflictException($"Already previewed as {duplicate.Id}.");
            database.Previews.Add(new() { Id = preview.Id, Hash = preview.Hash, BaselineRevision = preview.BaselineRevision, Payload = payload });
            await database.SaveChangesAsync(cancellation);
        }
        await transaction.CommitAsync(cancellation);
    }

    public async Task<ImportPreview?> GetPreviewAsync(string id, CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        var row = await database.Previews.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellation);
        return row is null ? null : JsonDefaults.Parse<ImportPreview>(row.Payload);
    }

    public async Task<PortfolioRevision> ConfirmAsync(string previewId, int expectedRevision, CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        var head = await RequireHeadAsync(database, cancellation);
        var row = await database.Previews.SingleOrDefaultAsync(row => row.Id == previewId, cancellation)
            ?? throw new KeyNotFoundException("Preview not found.");
        if (row.BaselineRevision != expectedRevision) throw new WorkspaceConflictException("Expected revision differs from the preview baseline.");
        if (row.ConfirmedRevision is { } confirmed)
            return JsonDefaults.Parse<PortfolioRevision>((await database.Revisions.SingleAsync(item => item.Revision == confirmed, cancellation)).Payload);
        if (head.CurrentRevision != expectedRevision) throw new WorkspaceConflictException("Portfolio changed; recompute and review this preview.");
        var preview = JsonDefaults.Parse<ImportPreview>(row.Payload);
        if (preview.Issues.Count > 0) throw new WorkspaceConflictException("Preview contains blocking issues; resolve and review before confirmation.");
        ValidateLedger(preview.ProposedTransactions);
        var previous = head.CurrentRevision == 0 ? EmptyPortfolio()
            : JsonDefaults.Parse<PortfolioRevision>((await database.Revisions.SingleAsync(item => item.Revision == head.CurrentRevision, cancellation)).Payload);
        var now = await DatabaseNowAsync(database, cancellation);
        var revision = new PortfolioRevision(checked(head.LastRevision + 1), preview.ProposedTransactions, preview.Instruments,
            previous.Policy, now, $"Confirmed:{preview.Hash};{preview.Format}",
            Snapshot: preview.Snapshot, Reconciliation: preview.Reconciliation);
        database.Revisions.Add(new() { Revision = revision.Revision, Payload = SerializeBounded(revision) });
        database.LedgerIdentities.AddRange(revision.Transactions.Select(item => new LedgerIdentityRow { Revision = revision.Revision, TransactionId = item.Id }));
        head.CurrentRevision = head.LastRevision = revision.Revision;
        row.ConfirmedRevision = revision.Revision;
        var run = NewPendingRun(new AnalysisRun { IdempotencyKey = $"confirm:{previewId}" }, revision, now);
        database.Runs.Add(ToRow(run));
        await database.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return revision;
    }

    public async Task<PortfolioRevision> UpdatePolicyAsync(int expectedRevision, decimal maximumPositionPercent,
        decimal? maximumTacticalPercent, CancellationToken cancellation)
    {
        if (maximumPositionPercent is <= 0 or > 100 || maximumTacticalPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maximumPositionPercent), "Policy percentages must be within 0 to 100; position must be greater than zero.");
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        var head = await RequireHeadAsync(database, cancellation);
        if (head.CurrentRevision == 0 || head.CurrentRevision != expectedRevision)
            throw new WorkspaceConflictException("Portfolio changed or is empty; review the current revision before setting policy.");
        var previous = JsonDefaults.Parse<PortfolioRevision>((await database.Revisions.SingleAsync(
            item => item.Revision == head.CurrentRevision, cancellation)).Payload);
        var now = await DatabaseNowAsync(database, cancellation);
        var revision = previous with
        {
            Revision = checked(head.LastRevision + 1),
            Policy = new RiskPolicy(checked(previous.Policy.Version + 1), maximumPositionPercent, maximumTacticalPercent),
            ConfirmedAt = now,
            Provenance = $"UserPolicyUpdate;baseline:{previous.Revision};{previous.Provenance}"
        };
        database.Revisions.Add(new() { Revision = revision.Revision, Payload = SerializeBounded(revision) });
        database.LedgerIdentities.AddRange(revision.Transactions.Select(item => new LedgerIdentityRow
            { Revision = revision.Revision, TransactionId = item.Id }));
        head.CurrentRevision = head.LastRevision = revision.Revision;
        database.Runs.Add(ToRow(NewPendingRun(new AnalysisRun { IdempotencyKey = $"policy:{revision.Revision}" }, revision, now)));
        await database.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return revision;
    }

    public async Task<AnalysisRun> CreateRunAsync(AnalysisRun run, CancellationToken cancellation)
    {
        ValidateKey(run.Id, 128);
        ValidateKey(run.IdempotencyKey, 256);
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        var head = await RequireHeadAsync(database, cancellation);
        var existing = await database.Runs.SingleOrDefaultAsync(row => row.IdempotencyKey == run.IdempotencyKey, cancellation);
        if (existing is not null)
        {
            if (run.Revision != 0 && ReadRun(existing).Revision != run.Revision)
                throw new WorkspaceConflictException("Idempotency key belongs to another revision.");
            return ReadRun(existing);
        }
        var revisionId = run.Revision == 0 ? head.CurrentRevision : run.Revision;
        if (revisionId == 0) throw new WorkspaceConflictException("Confirm a portfolio before creating research work.");
        var revision = await database.Revisions.SingleOrDefaultAsync(row => row.Revision == revisionId, cancellation)
            ?? throw new WorkspaceConflictException("Pinned revision does not exist.");
        var accepted = NewPendingRun(run, JsonDefaults.Parse<PortfolioRevision>(revision.Payload), await DatabaseNowAsync(database, cancellation));
        database.Runs.Add(ToRow(accepted));
        await database.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return accepted;
    }

    public async Task<AnalysisRun?> GetRunAsync(string id, CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        var row = await database.Runs.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellation);
        return row is null ? null : ReadRun(row);
    }

    public async Task<List<AnalysisRun>> ListRunsAsync(CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        return (await database.Runs.AsNoTracking().OrderByDescending(row => row.CreatedAt).Take(100).ToListAsync(cancellation)).Select(ReadRun).ToList();
    }

    public async Task<AnalysisRun?> ClaimAsync(string owner, DateTimeOffset now, TimeSpan lifetime, CancellationToken cancellation)
    {
        ValidateKey(owner, 128);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(15)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        for (var scan = 0; scan < 32; scan++)
        {
            now = await DatabaseNowAsync(database, cancellation);
            var row = await database.Runs.FromSqlInterpolated($"SELECT TOP (1) * FROM AnalysisRuns WITH (UPDLOCK, HOLDLOCK) WHERE Status = {(int)RunStatus.Pending} OR (Status = {(int)RunStatus.Running} AND (ClaimExpires IS NULL OR ClaimExpires <= {now})) ORDER BY CreatedAt, Id").SingleOrDefaultAsync(cancellation);
            if (row is null) break;
            var run = ReadRun(row);
            if (run.CancelRequested || run.Generation >= 3 || run.Generation > 0 && run.Deadline <= now)
            {
                run.Status = run.CancelRequested ? RunStatus.Cancelled : RunStatus.Failed;
                run.AiStatus = run.CancelRequested ? "Cancelled" : "RecoveryExhausted";
                run.Errors.Add("Pending/expired work stopped: cancellation, deadline, or three-attempt limit.");
                run.FinishedAt = now;
                run.Owner = null;
                run.ClaimExpires = null;
                run.Version++;
                UpdateRow(row, run);
                await database.SaveChangesAsync(cancellation);
                continue;
            }
            if (run.Generation == 0)
            {
                run.Deadline = now.AddMinutes(configuration.Limits.MaximumRunMinutes);
                run.ModelBinding = configuration.ModelBinding;
            }
            run.Generation++;
            run.Version++;
            run.Owner = owner;
            run.ClaimExpires = now + lifetime < run.Deadline ? now + lifetime : run.Deadline;
            run.Status = RunStatus.Running;
            UpdateRow(row, run);
            await database.SaveChangesAsync(cancellation);
            await transaction.CommitAsync(cancellation);
            return run;
        }
        await transaction.CommitAsync(cancellation);
        return null;
    }

    public async Task SaveRunAsync(AnalysisRun run, string owner, int generation, CancellationToken cancellation)
    {
        var proposed = run;
        run = JsonDefaults.Parse<AnalysisRun>(SerializeBounded(run));
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        var row = await database.Runs.FromSqlInterpolated($"SELECT * FROM AnalysisRuns WITH (UPDLOCK, HOLDLOCK) WHERE Id = {run.Id}").SingleOrDefaultAsync(cancellation)
            ?? throw new WorkspaceConflictException("Run no longer exists.");
        var current = ReadRun(row);
        var now = await DatabaseNowAsync(database, cancellation);
        RunCheckpoint.Validate(current, run, owner, generation, now);
        run.CancelRequested |= current.CancelRequested;
        if (run.CancelRequested)
        {
            run.Status = RunStatus.Cancelled;
            run.AiStatus = "Cancelled";
            run.Brief = null;
            run.ReportBlob = null;
        }
        if (run.Status is RunStatus.Completed or RunStatus.Partial)
        {
            if (run.Brief is null || run.Facts is null || string.IsNullOrEmpty(run.ReportBlob)
                || run.Brief.RunId != run.Id || run.Brief.Revision != current.Revision || run.Facts.Revision != current.Revision)
                throw new WorkspaceConflictException("Publication requires a complete matching brief, facts, and immutable report manifest.");
            ArtifactNames.Validate(run.ReportBlob);
        }
        var terminal = run.Status != RunStatus.Running;
        run.Portfolio = current.Portfolio;
        run.Deadline = current.Deadline;
        run.CreatedAt = current.CreatedAt;
        run.ClaimExpires = terminal ? null : current.ClaimExpires;
        run.Owner = terminal ? null : owner;
        run.Generation = generation;
        run.FinishedAt = terminal ? now : null;
        var nextVersion = checked(current.Version + 1);
        run.Version = nextVersion;
        var payload = SerializeBounded(run);
        var affected = await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE AnalysisRuns SET Status = {(int)run.Status}, Owner = {run.Owner}, ClaimExpires = {run.ClaimExpires}, Version = {nextVersion}, CancelRequested = {run.CancelRequested}, Payload = {payload} WHERE Id = {run.Id} AND Owner = {owner} AND Generation = {generation} AND Version = {current.Version} AND RowVersion = {row.RowVersion} AND Status = {(int)RunStatus.Running} AND ClaimExpires > SYSUTCDATETIME() AND Deadline > SYSUTCDATETIME()", cancellation);
        if (affected != 1) throw new WorkspaceConflictException("Claim expired or changed at SQL checkpoint write; publication rejected.");
        await transaction.CommitAsync(cancellation);
        proposed.Version = nextVersion;
        proposed.Status = run.Status;
        proposed.AiStatus = run.AiStatus;
        proposed.CancelRequested = run.CancelRequested;
        proposed.Owner = run.Owner;
        proposed.ClaimExpires = run.ClaimExpires;
        proposed.FinishedAt = run.FinishedAt;
        proposed.Portfolio = run.Portfolio;
        proposed.Deadline = run.Deadline;
        proposed.CreatedAt = run.CreatedAt;
        proposed.Brief = run.Brief;
        proposed.ReportBlob = run.ReportBlob;
    }

    public async Task CancelAsync(string id, CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        var row = await database.Runs.FromSqlInterpolated($"SELECT * FROM AnalysisRuns WITH (UPDLOCK, HOLDLOCK) WHERE Id = {id}").SingleOrDefaultAsync(cancellation);
        if (row is not null && row.Status is RunStatus.Pending or RunStatus.Running)
        {
            row.CancelRequested = true;
            if (row.Status == RunStatus.Pending) row.Status = RunStatus.Cancelled;
            await database.SaveChangesAsync(cancellation);
        }
        await transaction.CommitAsync(cancellation);
    }

    public async Task<bool> ReserveMonthlyAsync(string reservationId, decimal usd, decimal maximumUsd, DateTimeOffset now, CancellationToken cancellation)
    {
        ValidateKey(reservationId, 256);
        if (usd < 0 || maximumUsd < 0 || usd != decimal.Round(usd, 8) || maximumUsd > 1_000_000m) throw new ArgumentOutOfRangeException(nameof(usd));
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        now = await DatabaseNowAsync(database, cancellation);
        var month = now.UtcDateTime.Year * 100 + now.UtcDateTime.Month;
        var budget = await database.Months.FromSqlInterpolated($"SELECT * FROM BudgetMonths WITH (UPDLOCK, HOLDLOCK) WHERE Month = {month}").SingleOrDefaultAsync(cancellation);
        var existing = await database.Reservations.SingleOrDefaultAsync(row => row.Id == reservationId, cancellation);
        if (existing is not null)
        {
            if (existing.Usd != usd) throw new WorkspaceConflictException("Reservation IDs cannot change amount, even across a month boundary.");
            return true;
        }
        if (usd > maximumUsd || (budget?.TotalUsd ?? 0) > maximumUsd - usd) return false;
        if (budget is null) { budget = new() { Month = month }; database.Months.Add(budget); }
        budget.TotalUsd += usd;
        database.Reservations.Add(new() { Id = reservationId, Month = month, Usd = usd });
        await database.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return true;
    }

    public async Task DeletePortfolioAsync(CancellationToken cancellation)
    {
        await using var database = await factory.CreateDbContextAsync(cancellation);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        var head = await RequireHeadAsync(database, cancellation);
        await database.Runs.ExecuteDeleteAsync(cancellation);
        await database.Previews.ExecuteDeleteAsync(cancellation);
        await database.LedgerIdentities.ExecuteDeleteAsync(cancellation);
        await database.Revisions.ExecuteDeleteAsync(cancellation);
        head.CurrentRevision = 0;
        await database.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
    }

    private AnalysisRun NewPendingRun(AnalysisRun requested, PortfolioRevision portfolio, DateTimeOffset now) => new()
    {
        Id = requested.Id, IdempotencyKey = requested.IdempotencyKey, Revision = portfolio.Revision, Portfolio = portfolio,
        Mode = configuration.Mode, AsOf = requested.AsOf <= now ? requested.AsOf : now, CreatedAt = now,
        Deadline = DateTimeOffset.MaxValue, ModelBinding = "PendingWorkerBinding",
        PreviousRunId = requested.PreviousRunId, Version = 1
    };

    private static Task<WorkspaceHead?> HeadAsync(WorkspaceDbContext database, CancellationToken cancellation) =>
        database.Heads.FromSqlRaw("SELECT * FROM WorkspaceHeads WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1").SingleOrDefaultAsync(cancellation);
    private static async Task<WorkspaceHead> RequireHeadAsync(WorkspaceDbContext database, CancellationToken cancellation) =>
        await HeadAsync(database, cancellation) ?? throw new InvalidOperationException("Call InitializeAsync at explicit launch, not registration.");
    private static Task<DateTimeOffset> DatabaseNowAsync(WorkspaceDbContext database, CancellationToken cancellation) =>
        database.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS Value").SingleAsync(cancellation);
    private static PortfolioRevision EmptyPortfolio() => new(0, [], [], new(), DateTimeOffset.MinValue, "Empty; no seeded holdings");
    private static string SerializeBounded<T>(T value)
    {
        var payload = JsonDefaults.Serialize(value);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes) throw new InvalidDataException("Workspace record exceeds 4 MiB.");
        return payload;
    }
    private static void ValidateLedger(List<Transaction> transactions)
    {
        if (transactions.Count > 10000) throw new InvalidDataException("Ledger exceeds 10000 events.");
        foreach (var item in transactions) ValidateKey(item.Id, 256);
        if (transactions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != transactions.Count)
            throw new WorkspaceConflictException("Duplicate economic IDs; application must preserve multiplicity with distinct stable IDs.");
    }
    private static void ValidateKey(string key, int length)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > length || key != key.Trim() || key.Any(char.IsControl))
            throw new ArgumentException("Invalid bounded key.");
    }
    private static RunRow ToRow(AnalysisRun run) { var row = new RunRow(); UpdateRow(row, run); return row; }
    private static void UpdateRow(RunRow row, AnalysisRun run)
    {
        row.Id = run.Id; row.IdempotencyKey = run.IdempotencyKey; row.Status = run.Status; row.CreatedAt = run.CreatedAt;
        row.Deadline = run.Deadline; row.Owner = run.Owner; row.Generation = run.Generation; row.Version = run.Version;
        row.ClaimExpires = run.ClaimExpires; row.CancelRequested = run.CancelRequested; row.Payload = SerializeBounded(run);
    }
    private static AnalysisRun ReadRun(RunRow row)
    {
        var run = JsonDefaults.Parse<AnalysisRun>(row.Payload);
        run.Status = row.Status; run.Owner = row.Owner; run.Generation = row.Generation; run.Version = row.Version;
        run.CancelRequested = row.CancelRequested; run.ClaimExpires = row.ClaimExpires; run.Deadline = row.Deadline;
        if (run.Status is not (RunStatus.Completed or RunStatus.Partial)) { run.Brief = null; run.ReportBlob = null; }
        return run;
    }
}

public static class RunCheckpoint
{
    public static void Validate(AnalysisRun current, AnalysisRun proposed, string owner, int generation, DateTimeOffset now)
    {
        if (current.Status != RunStatus.Running || current.Owner != owner || current.Generation != generation
            || current.Version != proposed.Version || current.ClaimExpires is null || current.ClaimExpires <= now || current.Deadline <= now)
            throw new WorkspaceConflictException("Claim/version lost or expired; stale worker cannot checkpoint or publish.");
        if (proposed.Id != current.Id || proposed.Revision != current.Revision || proposed.IdempotencyKey != current.IdempotencyKey
            || proposed.Mode != current.Mode || proposed.AsOf != current.AsOf || proposed.Status == RunStatus.Pending || !Enum.IsDefined(proposed.Status)
            || proposed.SchemaVersion != current.SchemaVersion || proposed.CodeVersion != current.CodeVersion || proposed.PromptVersion != current.PromptVersion
            || proposed.ConfigVersion != current.ConfigVersion || proposed.ModelBinding != current.ModelBinding || proposed.PreviousRunId != current.PreviousRunId)
            throw new WorkspaceConflictException("Immutable run context changed.");
        if (proposed.Decisions < current.Decisions || proposed.ModelSubmissions < current.ModelSubmissions
            || proposed.ToolInvocations < current.ToolInvocations || proposed.ResearchTasks < current.ResearchTasks
            || proposed.CriticPasses < current.CriticPasses || proposed.ReservedUsd < current.ReservedUsd || proposed.ReservedTokens < current.ReservedTokens
            || proposed.Tasks.Count < current.Tasks.Count || proposed.Usage.Count < current.Usage.Count)
            throw new WorkspaceConflictException("Recovery cannot reset cumulative counters or completed tasks.");
        RequirePrefix(current.Tasks, proposed.Tasks);
        RequirePrefix(current.Evidence, proposed.Evidence);
        RequirePrefix(current.Trace, proposed.Trace);
        RequirePrefix(current.Fingerprints, proposed.Fingerprints);
        RequirePrefix(current.Errors, proposed.Errors);
        for (var index = 0; index < current.Usage.Count; index++)
        {
            var before = current.Usage[index];
            var after = proposed.Usage[index];
            if (before.Id != after.Id || before.TaskId != after.TaskId || before.ReservedUsd != after.ReservedUsd || before.Time != after.Time
                || before.InputTokens.HasValue && before.InputTokens != after.InputTokens || before.OutputTokens.HasValue && before.OutputTokens != after.OutputTokens
                || before.ActualUsd.HasValue && before.ActualUsd != after.ActualUsd || before.ProviderRequestId is not null && before.ProviderRequestId != after.ProviderRequestId)
                throw new WorkspaceConflictException("Stored usage identity, reservations, and observed provider facts cannot be rewritten.");
        }
    }

    private static void RequirePrefix<T>(List<T> current, List<T> proposed)
    {
        if (proposed.Count < current.Count || !current.Select(JsonDefaults.Serialize).SequenceEqual(proposed.Take(current.Count).Select(JsonDefaults.Serialize), StringComparer.Ordinal))
            throw new WorkspaceConflictException("Checkpoint history is append-only; stored results cannot be reset or rewritten.");
    }
}