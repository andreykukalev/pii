using System.Text.Json;

namespace Pii.Application;

public enum WorkspaceDeletionState { CurrentDataDeleted, SqlDataDeletedArtifactsPending }
public record WorkspaceExport(int SchemaVersion, DateTimeOffset ExportedAt, PortfolioRevision Portfolio,
    List<AnalysisRun> Runs, int MaximumRuns, bool HistoryMayBeTruncated,
    string Scope = "Current immutable portfolio and latest stored runs, including complete embedded evidence, tasks, briefs and usage. Originals and referenced artifact bodies are not included. Not a restore package.");

public sealed class WorkspaceMaintenance(IWorkspaceStore store, IArtifactStore artifacts)
{
    public const int MaximumExportBytes = 8 * 1024 * 1024;
    public const int MaximumExportRuns = 100;

    public async Task<byte[]> ExportAsync(int expectedRevision, CancellationToken cancellation)
    {
        var portfolio = await store.GetPortfolioAsync(cancellation);
        if (portfolio.Revision != expectedRevision) throw new InvalidOperationException("Review the current portfolio revision.");
        var runs = (await store.ListRunsAsync(cancellation)).OrderByDescending(run => run.CreatedAt)
            .ThenBy(run => run.Id, StringComparer.Ordinal).Take(MaximumExportRuns).ToList();
        if ((await store.GetPortfolioAsync(cancellation)).Revision != expectedRevision)
            throw new InvalidOperationException("Portfolio changed during export; retry from the current revision.");
        await using var output = new ExportStream();
        await JsonSerializer.SerializeAsync(output, new WorkspaceExport(1, DateTimeOffset.UtcNow, portfolio,
            runs, MaximumExportRuns, runs.Count == MaximumExportRuns), JsonDefaults.Options, cancellation);
        return output.ToArray();
    }

    public async Task<WorkspaceDeletionState> DeleteAsync(int expectedRevision, bool exclusiveMaintenance,
        Func<CancellationToken, Task<bool>> hasRunningClaims, CancellationToken cancellation)
    {
        if (!exclusiveMaintenance) throw new InvalidOperationException("Stop workers and pause other submissions before deletion.");
        if ((await store.GetPortfolioAsync(cancellation)).Revision != expectedRevision)
            throw new InvalidOperationException("Portfolio changed; review deletion again.");
        if (await hasRunningClaims(cancellation))
            throw new InvalidOperationException("Running claims must finish or be cancelled and settled before deletion, including expired claims.");
        await store.DeletePortfolioAsync(cancellation);
        try
        {
            await artifacts.DeleteAllAsync(cancellation);
            return WorkspaceDeletionState.CurrentDataDeleted;
        }
        catch (Exception)
        {
            return WorkspaceDeletionState.SqlDataDeletedArtifactsPending;
        }
    }

    private sealed class ExportStream : MemoryStream
    {
        private void RequireCapacity(int count)
        {
            if (Position + count > MaximumExportBytes)
                throw new InvalidDataException("Export exceeds 8 MiB; no partial download is returned.");
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireCapacity(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            base.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RequireCapacity(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            RequireCapacity(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}