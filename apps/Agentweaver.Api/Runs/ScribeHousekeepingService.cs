using System.Data;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Runs;

public enum ScribeAuthority
{
    Worker,
    CoordinatorFinalization,
}

public sealed record ScribeHousekeepingRequest(
    Run Run,
    ScribeAuthority Authority,
    string TerminalStatus);

public sealed record ScribeFinalizationResult(bool Completed, string? Error)
{
    public static ScribeFinalizationResult Success { get; } = new(true, null);
    public static ScribeFinalizationResult Failed(string error) => new(false, error);
}

public interface IScribeExportOperation
{
    Task<bool> IsAppliedAsync(
        string workingDirectory,
        string defaultBranch,
        string operationKey,
        CancellationToken ct);

    Task ApplyAsync(
        string projectId,
        string workingDirectory,
        string defaultBranch,
        string operationKey,
        CancellationToken ct);
}

public sealed class ScribeExportOperation(MemoryDbContext memoryDb) : IScribeExportOperation
{
    public Task<bool> IsAppliedAsync(
        string workingDirectory,
        string defaultBranch,
        string operationKey,
        CancellationToken ct) =>
        MemoryLedgerExporter.HasCommittedOperationAsync(
            workingDirectory, defaultBranch, operationKey, ct);

    public async Task ApplyAsync(
        string projectId,
        string workingDirectory,
        string defaultBranch,
        string operationKey,
        CancellationToken ct)
    {
        await MemoryLedgerExporter.ExportAsync(
            projectId, workingDirectory, memoryDb, ct).ConfigureAwait(false);
        await MemoryLedgerExporter.CommitExportAsync(
            workingDirectory, defaultBranch, ct, operationKey).ConfigureAwait(false);
    }
}

public sealed class ScribeFinalizationService(
    IRunStore runStore,
    ScribeHousekeepingService housekeeping)
{
    public async Task<ScribeFinalizationResult> FinalizeAsync(
        ProjectId projectId,
        RunId runId,
        int lifecycleGeneration,
        string? expectedAgentName,
        string? expectedSubmittingUser,
        string? terminalStatus,
        CancellationToken ct)
    {
        var run = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return ScribeFinalizationResult.Failed("scribe_run_not_found");
        if (run.ProjectId != projectId || run.LifecycleGeneration != lifecycleGeneration)
            return ScribeFinalizationResult.Failed("scribe_scope_mismatch");
        if (string.IsNullOrWhiteSpace(run.AgentName)
            || !string.Equals(run.AgentName, expectedAgentName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(run.SubmittingUser, expectedSubmittingUser, StringComparison.Ordinal))
            return ScribeFinalizationResult.Failed("scribe_identity_mismatch");

        var authority = string.Equals(run.AgentName, "coordinator", StringComparison.OrdinalIgnoreCase)
            ? ScribeAuthority.CoordinatorFinalization
            : ScribeAuthority.Worker;
        await housekeeping.RunAsync(
            new ScribeHousekeepingRequest(
                run,
                authority,
                string.IsNullOrWhiteSpace(terminalStatus)
                    ? run.Status.ToApiString()
                    : terminalStatus),
            ct).ConfigureAwait(false);
        return ScribeFinalizationResult.Success;
    }
}

public sealed class ScribeHousekeepingService(
    MemoryDbContext memoryDb,
    IProjectStore? projectStore,
    ILogger<ScribeHousekeepingService> logger,
    IScribeExportOperation? exportOperation = null)
{
    private static readonly TimeSpan AbandonedExportClaimAge = TimeSpan.FromMinutes(10);
    private static readonly string[] WorkerMergeTypes = ["learning", "pattern", "update"];
    private static readonly string[] CoordinatorMergeTypes =
        ["learning", "pattern", "update", "architectural", "scope"];
    private readonly IScribeExportOperation _exportOperation =
        exportOperation ?? new ScribeExportOperation(memoryDb);

    public async Task RunAsync(ScribeHousekeepingRequest request, CancellationToken ct)
    {
        var run = request.Run;
        if (!run.ProjectId.HasValue || string.IsNullOrWhiteSpace(run.AgentName))
            return;

        var projectId = run.ProjectId.Value.ToString();
        var runId = run.Id.ToString();
        var allowedTypes = request.Authority == ScribeAuthority.CoordinatorFinalization
            ? CoordinatorMergeTypes
            : WorkerMergeTypes;

        var pending = (await memoryDb.DecisionInbox
            .AsNoTracking()
            .Where(entry => entry.ProjectId == projectId
                && entry.Status == "pending"
                && entry.SourceKind == MemorySourceKinds.Run
                && allowedTypes.Contains(entry.Type))
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .Where(entry => string.Equals(entry.SourceRunId, runId, StringComparison.Ordinal)
                || request.Authority == ScribeAuthority.CoordinatorFinalization
                    && entry.SourceRunId?.StartsWith(
                        runId + "-coordinator-", StringComparison.Ordinal) == true)
            .Select(entry => entry.Id)
            .ToList();

        foreach (var entryId in pending)
        {
            await ExecuteAsync(
                request,
                $"decision:{entryId}:inbox-merge",
                "decision_inbox_merge",
                async token =>
                {
                    var entry = await memoryDb.DecisionInbox
                        .SingleOrDefaultAsync(candidate =>
                            candidate.Id == entryId
                            && candidate.ProjectId == projectId
                            && candidate.Status == "pending", token)
                        .ConfigureAwait(false);
                    if (entry is null)
                        return;

                    await DecisionPromotion.PromoteEntry(
                        memoryDb,
                        entry,
                        DateTimeOffset.UtcNow,
                        $"scribe:{runId}",
                        token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        await ExecuteAsync(
            request,
            "memory",
            "memory",
            _ => Task.CompletedTask,
            ct).ConfigureAwait(false);

        await ExecuteAsync(
            request,
            "session",
            "session",
            async token =>
            {
                var session = (await memoryDb.SessionContexts
                        .Where(candidate => candidate.ProjectId == projectId && candidate.EndedAt == null)
                        .ToListAsync(token).ConfigureAwait(false))
                    .OrderByDescending(candidate => candidate.StartedAt)
                    .FirstOrDefault();
                if (session is null)
                    return;

                var outcome = $"Run {runId} by {run.AgentName} reached {request.TerminalStatus}.";
                if (session.Summary?.Split('\n').Contains(outcome, StringComparer.Ordinal) == true)
                    return;
                session.Summary = string.IsNullOrWhiteSpace(session.Summary)
                    ? outcome
                    : session.Summary + "\n" + outcome;
            },
            ct).ConfigureAwait(false);

        await ExecuteAsync(
            request,
            "history",
            "history",
            _ => Task.CompletedTask,
            ct).ConfigureAwait(false);

        await ExecuteExportAsync(request, projectId, ct).ConfigureAwait(false);
    }

    private async Task ExecuteExportAsync(
        ScribeHousekeepingRequest request,
        string projectId,
        CancellationToken ct)
    {
        if (projectStore is null)
        {
            await ExecuteAsync(request, "export", "export", _ => Task.CompletedTask, ct)
                .ConfigureAwait(false);
            return;
        }

        var project = await projectStore.GetAsync(request.Run.ProjectId!.Value, ct)
            .ConfigureAwait(false);
        if (project is null || string.IsNullOrWhiteSpace(project.WorkingDirectory))
        {
            await ExecuteAsync(request, "export", "export", _ => Task.CompletedTask, ct)
                .ConfigureAwait(false);
            return;
        }

        var operationKey =
            $"scribe:{request.Run.Id}:generation:{request.Run.LifecycleGeneration}:export";

        while (true)
        {
            var attempt = new ScribeOperationAttempt
            {
                OperationKey = operationKey,
                ProjectId = projectId,
                RunId = request.Run.Id.ToString(),
                LifecycleGeneration = request.Run.LifecycleGeneration,
                OperationType = "export",
                Status = "started",
                StartedAt = DateTimeOffset.UtcNow,
            };
            memoryDb.ScribeOperationAttempts.Add(attempt);
            var claimed = true;

            try
            {
                await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                memoryDb.ChangeTracker.Clear();
                claimed = false;
            }

            if (claimed)
            {
                if (await _exportOperation.IsAppliedAsync(
                        project.WorkingDirectory, project.DefaultBranch, operationKey, ct)
                    .ConfigureAwait(false))
                {
                    attempt.Status = "completed";
                    attempt.CompletedAt = DateTimeOffset.UtcNow;
                    await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
                    return;
                }

                await ApplyClaimedExportAsync(
                    attempt, project.WorkingDirectory, project.DefaultBranch, ct).ConfigureAwait(false);
                return;
            }

            var active = await memoryDb.ScribeOperationAttempts
                .SingleOrDefaultAsync(candidate =>
                    candidate.OperationKey == operationKey
                    && (candidate.Status == "started" || candidate.Status == "completed"), ct)
                .ConfigureAwait(false);
            if (active is null)
                continue;
            if (active.Status == "completed")
                return;

            if (await _exportOperation.IsAppliedAsync(
                    project.WorkingDirectory, project.DefaultBranch, operationKey, ct)
                .ConfigureAwait(false))
            {
                active.Status = "completed";
                active.CompletedAt = DateTimeOffset.UtcNow;
                await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }

            if (active.StartedAt <= DateTimeOffset.UtcNow - AbandonedExportClaimAge)
            {
                active.Status = "failed";
                active.FailureCode = "scribe_abandoned_claim";
                active.CompletedAt = DateTimeOffset.UtcNow;
                await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            memoryDb.Entry(active).State = EntityState.Detached;
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyClaimedExportAsync(
        ScribeOperationAttempt attempt,
        string workingDirectory,
        string defaultBranch,
        CancellationToken ct)
    {
        try
        {
            await _exportOperation.ApplyAsync(
                attempt.ProjectId,
                workingDirectory,
                defaultBranch,
                attempt.OperationKey,
                ct).ConfigureAwait(false);
            attempt.Status = "completed";
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            attempt.Status = "failed";
            attempt.FailureCode = ScribeFailureClassifier.Classify(ex).Code;
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await memoryDb.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning(
                "Scribe housekeeping failed for run {RunId}; operation=export; code={FailureCode}",
                attempt.RunId,
                attempt.FailureCode);
            throw;
        }
    }

    private async Task ExecuteAsync(
        ScribeHousekeepingRequest request,
        string suffix,
        string operationType,
        Func<CancellationToken, Task> mutation,
        CancellationToken ct)
    {
        var run = request.Run;
        var operationKey =
            $"scribe:{run.Id}:generation:{run.LifecycleGeneration}:{suffix}";

        if (await memoryDb.ScribeOperationAttempts.AsNoTracking().AnyAsync(
                attempt => attempt.OperationKey == operationKey && attempt.Status == "completed", ct)
            .ConfigureAwait(false))
            return;

        await using var transaction = await memoryDb.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);
        var attempt = new ScribeOperationAttempt
        {
            OperationKey = operationKey,
            ProjectId = run.ProjectId!.Value.ToString(),
            RunId = run.Id.ToString(),
            LifecycleGeneration = run.LifecycleGeneration,
            OperationType = operationType,
            Status = "started",
            StartedAt = DateTimeOffset.UtcNow,
        };
        memoryDb.ScribeOperationAttempts.Add(attempt);

        try
        {
            if (await memoryDb.ScribeOperationAttempts.AsNoTracking().AnyAsync(
                    candidate => candidate.OperationKey == operationKey
                        && candidate.Status == "completed", ct)
                .ConfigureAwait(false))
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            await mutation(ct).ConfigureAwait(false);
            attempt.Status = "completed";
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await memoryDb.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            memoryDb.ChangeTracker.Clear();
            memoryDb.ScribeOperationAttempts.Add(new ScribeOperationAttempt
            {
                OperationKey = operationKey,
                ProjectId = run.ProjectId!.Value.ToString(),
                RunId = run.Id.ToString(),
                LifecycleGeneration = run.LifecycleGeneration,
                OperationType = operationType,
                Status = "failed",
                FailureCode = ScribeFailureClassifier.Classify(ex).Code,
                StartedAt = attempt.StartedAt,
                CompletedAt = DateTimeOffset.UtcNow,
            });
            await memoryDb.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning(
                "Scribe housekeeping failed for run {RunId}; operation={OperationType}; code={FailureCode}",
                run.Id,
                operationType,
                ScribeFailureClassifier.Classify(ex).Code);
            throw;
        }
    }
}

public sealed record ScribeFailureDiagnostic(string Code, bool Retryable);

public static class ScribeFailureClassifier
{
    public static ScribeFailureDiagnostic Classify(Exception exception) =>
        exception switch
        {
            OperationCanceledException => new("scribe_timeout", true),
            HttpRequestException => new("scribe_transport_failure", true),
            DbUpdateConcurrencyException => new("scribe_concurrency_conflict", true),
            DbUpdateException => new("scribe_persistence_failure", true),
            UnauthorizedAccessException => new("scribe_authorization_failure", false),
            _ => new("scribe_internal_failure", false),
        };
}
