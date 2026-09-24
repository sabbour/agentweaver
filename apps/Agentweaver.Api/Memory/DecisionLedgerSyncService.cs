using Agentweaver.Api.Git;
using Agentweaver.Domain;

namespace Agentweaver.Api.Memory;

public sealed record DecisionLedgerSyncResult(
    MemoryLedgerExporter.ExportResult Export,
    int Imported,
    int Promoted);

public sealed class DecisionLedgerSyncService(
    MemoryDbContext memoryDb,
    RepositoryMergeLock mergeLock,
    ILogger<DecisionLedgerSyncService> logger)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    public async Task<bool> TryRefreshAsync(
        string projectId,
        string workingDirectory,
        CancellationToken ct)
    {
        try
        {
            await RefreshAsync(projectId, workingDirectory, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to synchronize the decision ledger for project {ProjectId}.",
                projectId);
            return false;
        }
    }

    public Task<DecisionLedgerSyncResult> RefreshAsync(
        string projectId,
        string workingDirectory,
        CancellationToken ct) =>
        WithLockAsync(
            workingDirectory,
            async () =>
            {
                var accepted = await MemoryLedgerExporter.ReconcileAcceptedDecisionsAsync(
                    projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
                var reconciled = await MemoryLedgerExporter.ReconcileInboxAsync(
                    projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
                var export = await MemoryLedgerExporter.ExportAsync(
                    projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
                return new DecisionLedgerSyncResult(
                    export, accepted.Imported + reconciled.Imported, 0);
            },
            ct);

    public Task<DecisionLedgerSyncResult> ExportAndCommitAsync(
        string projectId,
        string workingDirectory,
        string defaultBranch,
        CancellationToken ct,
        string? operationKey = null) =>
        WithLockAsync(
            workingDirectory,
            async () =>
            {
                var accepted = await MemoryLedgerExporter.ReconcileAcceptedDecisionsAsync(
                    projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
                var reconciled = await MemoryLedgerExporter.ReconcileInboxAsync(
                    projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
                var export = await MemoryLedgerExporter.ExportAsync(
                    projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
                await MemoryLedgerExporter.CommitExportAsync(
                    workingDirectory, defaultBranch, ct, operationKey).ConfigureAwait(false);
                return new DecisionLedgerSyncResult(
                    export, accepted.Imported + reconciled.Imported, 0);
            },
            ct);

    public Task<DecisionLedgerSyncResult> ConsolidateAsync(Project project, CancellationToken ct) =>
        WithLockAsync(
            project.WorkingDirectory,
            async () =>
            {
                var accepted = await MemoryLedgerExporter.ReconcileAcceptedDecisionsAsync(
                    project.Id.ToString(), project.WorkingDirectory, memoryDb, ct).ConfigureAwait(false);
                var reconciled = await MemoryLedgerExporter.ReconcileInboxAsync(
                    project.Id.ToString(), project.WorkingDirectory, memoryDb, ct).ConfigureAwait(false);
                var promoted = 0;
                foreach (var entry in reconciled.Entries.Where(entry => entry.Status == "pending"))
                {
                    if (entry.SourceKind == MemorySourceKinds.Legacy)
                        continue;
                    var promotion = await DecisionPromotion.PromoteEntryAsync(
                        memoryDb,
                        project.Id.ToString(),
                        entry.Id,
                        DateTimeOffset.UtcNow,
                        entry.SourceIdentity ?? "repository",
                        ct).ConfigureAwait(false);
                    if (promotion?.Promoted == true)
                        promoted++;
                }

                var export = await MemoryLedgerExporter.ExportAsync(
                    project.Id.ToString(), project.WorkingDirectory, memoryDb, ct).ConfigureAwait(false);
                await MemoryLedgerExporter.CommitExportAsync(
                    project.WorkingDirectory, project.DefaultBranch, ct).ConfigureAwait(false);
                return new DecisionLedgerSyncResult(
                    export, accepted.Imported + reconciled.Imported, promoted);
            },
            ct);

    private async Task<T> WithLockAsync<T>(
        string workingDirectory,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        var handle = await mergeLock.TryAcquireAsync(
            Path.GetFullPath(workingDirectory), LockTimeout, ct).ConfigureAwait(false);
        if (handle is null)
            throw new InvalidOperationException("Decision ledger synchronization is busy; retry the operation.");

        using (handle)
            return await action().ConfigureAwait(false);
    }
}
