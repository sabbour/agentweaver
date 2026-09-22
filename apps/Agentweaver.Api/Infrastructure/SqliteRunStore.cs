using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteRunStore : IRunStore
{
    private readonly SqliteDb _db;
    private readonly ILogger<SqliteRunStore>? _logger;

    public SqliteRunStore(SqliteDb db, ILogger<SqliteRunStore>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task InsertAsync(Run run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                              submitting_user, status, approval_generation, started_at, ended_at, result,
                              worktree_path, worktree_branch, project_id, model_id,
                              agent_name, agent_charter, workflow_run_id, parent_run_id, subtask_id,
                              origin, retried_from, archived_at, sandbox_backend, sandbox_claim_name,
                              sandbox_pod_name, sandbox_namespace, workflow_selection_reason,
                              launch_auto_approve_tools, launch_autopilot, approval_policy_snapshot_id,
                              approval_policy_source,
                              approval_policy_captured_at, approval_policy_settings_updated_at,
                              approval_policy_inherited_from_run_id)
            VALUES ($runId, $repo, $branch, $modelSource, $task,
                    $user, $status, $approvalGeneration, $startedAt, $endedAt, $result,
                    $worktreePath, $worktreeBranch, $projectId, $modelId,
                    $agentName, $agentCharter, $workflowRunId, $parentRunId, $subtaskId,
                    $origin, $retriedFrom, $archivedAt, $sandboxBackend, $sandboxClaimName,
                    $sandboxPodName, $sandboxNamespace, $workflowSelectionReason,
                    $launchAutoApproveTools, $launchAutopilot, $approvalPolicySnapshotId,
                    $approvalPolicySource,
                    $approvalPolicyCapturedAt, $approvalPolicySettingsUpdatedAt,
                    $approvalPolicyInheritedFromRunId);
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$repo", run.RepositoryPath);
        command.Parameters.AddWithValue("$branch", run.OriginatingBranch);
        command.Parameters.AddWithValue("$modelSource", run.ModelSource.ToApiString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$status", run.Status.ToApiString());
        command.Parameters.AddWithValue("$approvalGeneration", run.ApprovalGeneration);
        command.Parameters.AddWithValue("$startedAt", Ts(run.StartedAt));
        command.Parameters.AddWithValue("$endedAt", NullableTs(run.EndedAt));
        command.Parameters.AddWithValue("$result", (object?)run.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreePath", (object?)run.WorktreePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreeBranch", (object?)run.WorktreeBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("$projectId", (object?)run.ProjectId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)run.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentName", (object?)run.AgentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentCharter", (object?)run.AgentCharter ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowRunId", (object?)run.WorkflowRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", (object?)run.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtaskId", (object?)run.SubtaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", run.Origin.ToApiString());
        command.Parameters.AddWithValue("$retriedFrom", (object?)run.RetriedFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$archivedAt", NullableTs(run.ArchivedAt));
        command.Parameters.AddWithValue("$sandboxBackend", (object?)run.SandboxBackend ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxClaimName", (object?)run.SandboxClaimName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxPodName", (object?)run.SandboxPodName ?? DBNull.Value);
        command.Parameters.AddWithValue("$sandboxNamespace", (object?)run.SandboxNamespace ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowSelectionReason", (object?)run.WorkflowSelectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$launchAutoApproveTools", run.LaunchAutoApproveTools is { } autoApproveTools ? autoApproveTools ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$launchAutopilot", run.LaunchAutopilot is { } autopilot ? autopilot ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySnapshotId", (object?)run.ApprovalPolicySnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySource", (object?)run.ApprovalPolicySource ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicyCapturedAt", NullableTs(run.ApprovalPolicyCapturedAt));
        command.Parameters.AddWithValue("$approvalPolicySettingsUpdatedAt", NullableTs(run.ApprovalPolicySettingsUpdatedAt));
        command.Parameters.AddWithValue("$approvalPolicyInheritedFromRunId", (object?)run.ApprovalPolicyInheritedFromRunId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Run?> GetAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE status = $status;";
        command.Parameters.AddWithValue("$status", status.ToApiString());

        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default)
    {
        RejectTerminalStatus(status);
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' AND $status <> 'in_progress' THEN 1 ELSE 0 END
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", NullableTs(endedAt));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, $"update status to {status.ToApiString()}");
    }

    public async Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        RejectTerminalStatus(status);
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt, result = $result,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' AND $status <> 'in_progress' THEN 1 ELSE 0 END
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", Ts(endedAt));
                cmd.Parameters.AddWithValue("$result", result);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, $"update result to {status.ToApiString()}");
    }

    public async Task UpdateAssemblyArtifactsAsync(
        RunId runId, string treeHash, string diff, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET tree_hash = $treeHash, diff = $diff
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", treeHash);
                cmd.Parameters.AddWithValue("$diff", diff);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "persist assembly artifacts");
    }

    public async Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default,
        DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET tree_hash = $treeHash, diff = $diff, status = $status, review_ready_at = $now,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' THEN 1 ELSE 0 END
             WHERE run_id = $runId
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", treeHash);
                cmd.Parameters.AddWithValue("$diff", diff);
                cmd.Parameters.AddWithValue("$status", RunStatus.AwaitingReview.ToApiString());
                cmd.Parameters.AddWithValue("$now", Ts(ts));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "mark review ready");
    }

    /// <summary>
    /// Atomically transitions a run from AwaitingReview to InProgress.
    /// Returns true if the CAS succeeded (request-changes won the race),
    /// false if another request already moved the run out of AwaitingReview.
    /// Used by the request-changes endpoint (B3) to reclaim the run for a new revision.
    /// </summary>
    public async Task<bool> TryTransitionReviewToInProgressAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = 'in_progress', ended_at = NULL, review_ready_at = NULL,
                   lifecycle_generation = lifecycle_generation + 1
             WHERE run_id = $runId AND status = 'awaiting_review';
            """;
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }
    /// Returns true if the transition was applied (exactly one row updated), false if a
    /// concurrent request already changed the status. This single-row conditional UPDATE
    /// is the idempotency and concurrency guard for the review endpoint (design issue #4).
    /// </summary>
    public async Task<bool> TryTransitionReviewAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.AwaitingReview) return false;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(toStatus, EventTypes.ReviewDeclined, new { result, reviewer }, endedAt, run.LifecycleGeneration),
            result, new HashSet<RunStatus> { RunStatus.AwaitingReview }, Reviewer: reviewer), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically transitions a run from AwaitingReview to Committing.
    /// Returns true if the CAS succeeded (this /commit request owns the run),
    /// false if another request already moved the run out of AwaitingReview.
    /// Must be called BEFORE CommitChanges to prevent TOCTOU races.
    /// </summary>
    public async Task<bool> TryTransitionToCommittingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = 'committing', review_ready_at = NULL
             WHERE run_id = $runId AND status = 'awaiting_review';
            """;
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Reverts a run from Committing back to AwaitingReview.
    /// Optionally updates tree_hash (used by restart recovery to record the
    /// committed HEAD after a crash between CommitChanges and ExecuteMergeAsync).
    /// Returns true if a row was updated; false if the run was no longer in Committing.
    /// </summary>
    public async Task<bool> TryRevertCommittingAsync(
        RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'awaiting_review', tree_hash = COALESCE($treeHash, tree_hash), review_ready_at = $now WHERE run_id = $runId AND status = 'committing';";
        command.Parameters.AddWithValue("$treeHash", (object?)treeHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }


    /// <summary>
    /// Atomically transitions a run from AwaitingReview or Committing to Merging.
    /// Accepts both source states so the /commit flow (Committing → Merging) and
    /// the /review flow (AwaitingReview → Merging) share a single CAS guard.
    /// Returns true if the CAS succeeded (this request owns the merge slot),
    /// false if another request already moved the run out of the expected state (MF3).
    /// </summary>
    public async Task<bool> TryStartMergingAsync(
        RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
              SET status = 'merging', reviewed_by = $reviewer,
                  review_ready_at = NULL
             WHERE run_id = $runId AND status IN ('awaiting_review', 'committing');
            """;
        command.Parameters.AddWithValue("$reviewer", (object?)reviewer ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Reverts a run from Merging back to AwaitingReview.
    /// Used on Blocked outcome or on exception fail-safe to keep the run recoverable (MF6).
    /// Returns true if a row was reverted; false if the run was no longer in Merging
    /// (a no-op the caller may log for observability).
    /// </summary>
    public async Task<bool> RevertMergingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'awaiting_review', review_ready_at = $now WHERE run_id = $runId AND status = 'merging';";
        command.Parameters.AddWithValue("$now", Ts(ts));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Transitions a run from Merging to a terminal status (Merged or MergeFailed).
    /// Called after MergeWorktree returns a Merged or Conflict outcome.
    /// <paramref name="mergeConflicts"/> is a JSON array of conflicting file paths; pass null on success.
    /// <paramref name="mergedCommitHash"/> is the commit SHA produced by the merge; pass null for non-merge transitions.
    /// Returns true if the transition was applied (one row updated); false on a concurrency conflict.
    /// </summary>
    public async Task<bool> CompleteMergingAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.Merging) return false;
        var eventType = toStatus == RunStatus.Merged ? EventTypes.MergeCompleted : EventTypes.MergeFailed;
        var outcome = TerminalRunOutcome.Create(toStatus, eventType,
            new { result, mergeConflicts, mergedCommitHash }, endedAt, run.LifecycleGeneration);
        return await TryMutateTerminalOutcomeAsync(runId,
            new TerminalRunMutation(outcome, result, new HashSet<RunStatus> { RunStatus.Merging },
                MergeConflicts: mergeConflicts, MergedCommitHash: mergedCommitHash), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the tree_hash of a run that is in Committing status.
    /// Used by the /commit endpoint after staging and committing any remaining
    /// uncommitted changes on top of the agent's commit.
    /// </summary>
    public async Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            "UPDATE runs SET tree_hash = $treeHash WHERE run_id = $runId AND status = 'committing';",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", newTreeHash);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "update tree hash after commit");
    }

    public async Task<bool> SetAssembleReadyAsync(
        RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount,
        DateTimeOffset endedAt, CancellationToken ct = default)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return false;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(RunStatus.AssembleReady, EventTypes.RunAssembleReady,
                new { treeHash, worktreeBranch, diff, stepCount }, endedAt, run.LifecycleGeneration),
            null, TreeHash: treeHash, WorktreeBranch: worktreeBranch, Diff: diff), ct).ConfigureAwait(false);
    }

    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Status-only terminal mutations are not supported; persist a typed TerminalRunOutcome instead.");
    }

    public async Task<bool> TrySetTerminalOutcomeAsync(
        RunId runId,
        TerminalRunOutcome outcome,
        string? result,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText =
            """
            UPDATE runs
               SET status = $status, ended_at = $endedAt, result = $result,
                   approval_generation = approval_generation +
                       CASE WHEN status = 'in_progress' THEN 1 ELSE 0 END
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """;
        update.Parameters.AddWithValue("$status", outcome.Status.ToApiString());
        update.Parameters.AddWithValue("$endedAt", Ts(outcome.OccurredAt));
        update.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        update.Parameters.AddWithValue("$runId", runId.ToString());
        update.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            return false;

        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO terminal_run_outcomes
                (run_id, lifecycle_generation, status, event_type, payload_json, occurred_at)
            VALUES ($runId, $generation, $status, $eventType, $payload, $occurredAt);
            """;
        insert.Parameters.AddWithValue("$runId", runId.ToString());
        insert.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        insert.Parameters.AddWithValue("$status", outcome.Status.ToApiString());
        insert.Parameters.AddWithValue("$eventType", outcome.EventType);
        insert.Parameters.AddWithValue("$payload", outcome.Payload.GetRawText());
        insert.Parameters.AddWithValue("$occurredAt", Ts(outcome.OccurredAt));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryMutateTerminalOutcomeAsync(
        RunId runId, TerminalRunMutation mutation, CancellationToken ct = default)
    {
        // Specialized callers currently require one precise source status (merging or
        // awaiting_review). Keep that compare-and-swap in the same SQLite transaction as the outbox.
        var expected = mutation.ExpectedStatuses?.SingleOrDefault();
        if (mutation.ExpectedStatuses is { Count: > 1 })
            throw new NotSupportedException("Terminal mutations require one expected source status.");
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText =
            """
            UPDATE runs SET status=$status, ended_at=$endedAt, result=$result,
              reviewed_by=COALESCE($reviewer, reviewed_by),
              merge_conflicts=COALESCE($mergeConflicts, merge_conflicts),
              merged_commit_hash=COALESCE($mergedCommitHash, merged_commit_hash),
              tree_hash=COALESCE($treeHash, tree_hash),
              worktree_branch=COALESCE($worktreeBranch, worktree_branch),
              diff=COALESCE($diff, diff)
             WHERE run_id=$runId AND lifecycle_generation=$generation
               AND ($expectedStatus IS NULL OR status=$expectedStatus)
               AND status NOT IN ('merged','declined','failed','completed','merge_failed','assemble_ready','cancelled');
            """;
        update.Parameters.AddWithValue("$status", mutation.Outcome.Status.ToApiString());
        update.Parameters.AddWithValue("$endedAt", Ts(mutation.Outcome.OccurredAt));
        update.Parameters.AddWithValue("$result", (object?)mutation.Result ?? DBNull.Value);
        update.Parameters.AddWithValue("$reviewer", (object?)mutation.Reviewer ?? DBNull.Value);
        update.Parameters.AddWithValue("$mergeConflicts", (object?)mutation.MergeConflicts ?? DBNull.Value);
        update.Parameters.AddWithValue("$mergedCommitHash", (object?)mutation.MergedCommitHash ?? DBNull.Value);
        update.Parameters.AddWithValue("$treeHash", (object?)mutation.TreeHash ?? DBNull.Value);
        update.Parameters.AddWithValue("$worktreeBranch", (object?)mutation.WorktreeBranch ?? DBNull.Value);
        update.Parameters.AddWithValue("$diff", (object?)mutation.Diff ?? DBNull.Value);
        update.Parameters.AddWithValue("$runId", runId.ToString());
        update.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
        update.Parameters.AddWithValue("$expectedStatus", expected is null ? DBNull.Value : expected.Value.ToApiString());
        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0) return false;
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO terminal_run_outcomes (run_id,lifecycle_generation,status,event_type,payload_json,occurred_at) VALUES ($runId,$generation,$status,$eventType,$payload,$occurredAt);";
        insert.Parameters.AddWithValue("$runId", runId.ToString()); insert.Parameters.AddWithValue("$generation", mutation.Outcome.ExpectedLifecycleGeneration);
        insert.Parameters.AddWithValue("$status", mutation.Outcome.Status.ToApiString()); insert.Parameters.AddWithValue("$eventType", mutation.Outcome.EventType);
        insert.Parameters.AddWithValue("$payload", mutation.Outcome.Payload.GetRawText()); insert.Parameters.AddWithValue("$occurredAt", Ts(mutation.Outcome.OccurredAt));
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<PendingTerminalRunOutcome>> GetUnprojectedTerminalOutcomesAsync(
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, lifecycle_generation, status, event_type, payload_json, occurred_at
              FROM terminal_run_outcomes
             WHERE projected_at IS NULL
             ORDER BY occurred_at;
            """;
        var result = new List<PendingTerminalRunOutcome>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            using var payload = JsonDocument.Parse(reader.GetString(4));
            result.Add(new PendingTerminalRunOutcome(
                RunId.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                new TerminalRunOutcome(
                    RunStatusExtensions.ParseStatus(reader.GetString(2)),
                    reader.GetString(3),
                    payload.RootElement.Clone(),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.GetInt32(1))));
        }
        return result;
    }

    public async Task MarkTerminalOutcomeProjectedAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE terminal_run_outcomes
               SET projected_at = $projectedAt
             WHERE run_id = $runId
               AND lifecycle_generation = $generation
               AND projected_at IS NULL;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$projectedAt", Ts(DateTimeOffset.UtcNow));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
                cmd.Parameters.AddWithValue("$generation", lifecycleGeneration);
            }, ct).ConfigureAwait(false);
    }

    public async Task<bool> TryAdoptLegacyTerminalOutcomeAsync(
        RunId runId,
        TerminalRunOutcome outcome,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO terminal_run_outcomes
                (run_id, lifecycle_generation, status, event_type, payload_json, occurred_at, projected_at)
            SELECT $runId, $generation, $status, $eventType, $payload, $occurredAt, $occurredAt
              WHERE EXISTS (
                  SELECT 1 FROM runs
                   WHERE run_id = $runId
                     AND lifecycle_generation = $generation
                     AND status = $status)
            ON CONFLICT (run_id, lifecycle_generation) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$runId", runId.ToString());
        insert.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        insert.Parameters.AddWithValue("$status", outcome.Status.ToApiString());
        insert.Parameters.AddWithValue("$eventType", outcome.EventType);
        insert.Parameters.AddWithValue("$payload", outcome.Payload.GetRawText());
        insert.Parameters.AddWithValue("$occurredAt", Ts(outcome.OccurredAt));
        var rows = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryBeginPreviewPublicationAsync(
        RunId runId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryAcquirePreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = $ownerId,
                   preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled')
               AND (preview_publication_lease_until IS NULL
                    OR preview_publication_lease_until <= $now);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryRenewPreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_until = $leaseUntil
             WHERE run_id = $runId
               AND preview_publication_lease_owner = $ownerId
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed', 'assemble_ready', 'cancelled');
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$leaseUntil", Ts(leaseUntil));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task EndPreviewPublicationAsync(RunId runId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = NULL,
                   preview_publication_lease_until = NULL
             WHERE run_id = $runId;
            """,
            cmd => cmd.Parameters.AddWithValue("$runId", runId.ToString()),
            ct).ConfigureAwait(false);
    }

    public async Task EndPreviewPublicationAsync(
        RunId runId, string ownerId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET preview_publication_lease_owner = NULL,
                   preview_publication_lease_until = NULL
             WHERE run_id = $runId
               AND preview_publication_lease_owner = $ownerId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$ownerId", ownerId);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            },
            ct).ConfigureAwait(false);
    }

    public async Task<bool> IsPreviewPublicationOwnerAsync(
        RunId runId, string ownerId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
              FROM runs
             WHERE run_id = $runId
               AND preview_publication_lease_owner = $ownerId
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$ownerId", ownerId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public async Task<DateTimeOffset?> GetPreviewPublicationLeaseAsync(
        RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT preview_publication_lease_until FROM runs WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null || value is DBNull
            ? null
            : DateTimeOffset.TryParse(
                value.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }

    public async Task<bool> TryTransitionToIdleAsync(RunId runId, CancellationToken ct = default)
    {
        // CAS: only the replica that still sees this run as in_progress parks it dormant. Deliberately
        // does NOT set ended_at — an Idle run is paused, not ended (woken via TryWakeFromIdleAsync).
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = $idle, approval_generation = approval_generation + 1
             WHERE run_id = $runId AND status = 'in_progress';
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$idle", RunStatus.Idle.ToApiString());
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<bool> TryWakeFromIdleAsync(RunId runId, CancellationToken ct = default)
    {
        // CAS: only the replica that still sees this run as idle wakes it back to in_progress.
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = 'in_progress', lifecycle_generation = lifecycle_generation + 1
             WHERE run_id = $runId AND status = 'idle';
            """,
            cmd => cmd.Parameters.AddWithValue("$runId", runId.ToString()),
            ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Transitions a pre-inserted Pending run to InProgress, recording the worktree path, branch,
    /// and actual start time. Called by the project-run path after TryCreateProjectRunAsync reserves
    /// the row atomically.
    /// </summary>
    public async Task UpdateToInProgressAsync(
        RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET status = 'in_progress', worktree_path = $worktreePath,
                   worktree_branch = $worktreeBranch, started_at = $startedAt
             WHERE run_id = $runId AND status = 'pending';
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$worktreePath", worktreePath);
                cmd.Parameters.AddWithValue("$worktreeBranch", worktreeBranch);
                cmd.Parameters.AddWithValue("$startedAt", startedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "transition to in_progress");
    }

    public async Task DeleteAsync(RunId runId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            "DELETE FROM runs WHERE run_id = $runId;",
            cmd => cmd.Parameters.AddWithValue("$runId", runId.ToString()), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the shared orchestration worktree path and branch on a coordinator run.
    /// Called once when the shared worktree is first provisioned for the orchestration.
    /// Idempotent: second call with same values is a no-op (WHERE guards against overwrite).
    /// </summary>
    public async Task UpdateWorktreeAsync(
        RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET worktree_path = $worktreePath, worktree_branch = $worktreeBranch
             WHERE run_id = $runId AND worktree_path IS NULL;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$worktreePath", worktreePath);
                cmd.Parameters.AddWithValue("$worktreeBranch", worktreeBranch);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    public async Task SetSandboxInfoAsync(
        RunId runId,
        string? backend,
        string? claimName,
        string? podName,
        string? @namespace,
        CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET sandbox_backend = COALESCE($backend, sandbox_backend),
                   sandbox_claim_name = COALESCE($claimName, sandbox_claim_name),
                   sandbox_pod_name = COALESCE($podName, sandbox_pod_name),
                   sandbox_namespace = COALESCE($namespace, sandbox_namespace)
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$backend", (object?)backend ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$claimName", (object?)claimName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$podName", (object?)podName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$namespace", (object?)@namespace ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "set sandbox info");
    }

    public async Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET archived_at = $archivedAt
             WHERE run_id = $runId AND archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("$archivedAt", Ts(archivedAt));
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default)
    {
        // Returns the first child run for (parentRunId, subtaskId) whose status indicates it is
        // actively executing — states that mean a second dispatch would create a duplicate worker
        // for the same subtask. Delivered/terminal states such as assemble_ready are deliberately
        // excluded: when assembly review resets a subtask to pending, the next dispatch must create
        // a new child turn instead of reusing the old terminal output.
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql +
            " WHERE parent_run_id = $parentRunId AND subtask_id = $subtaskId" +
            " AND status IN ('in_progress', 'awaiting_review', 'assembling', 'in_review')" +
            " ORDER BY started_at DESC LIMIT 1;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        command.Parameters.AddWithValue("$subtaskId", subtaskId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private async Task<int> ExecuteNonQueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private void WarnIfNoRows(int rows, RunId runId, string operation)
    {
        if (rows == 0)
            _logger?.LogWarning("Run transition no-op while attempting to {Operation} for run {RunId}", operation, runId);
    }

    private static void RejectTerminalStatus(RunStatus status)
    {
        if (TerminalRunOutcome.IsTerminal(status))
            throw new InvalidOperationException(
                $"Terminal status '{status}' requires TrySetTerminalOutcomeAsync or a typed terminal mutation.");
    }

    /// <summary>
    /// Lists the runs for a project. Coordinator CHILD runs (those with a non-null
    /// <see cref="Run.ParentRunId"/>) are EXCLUDED by default: per the "children-as-nodes"
    /// directive a coordinator's children are nodes inside the single coordinator topology, not
    /// separate top-level workflows, so they must not surface in the project-wide runs list. The
    /// child rows themselves are retained (they still execute, stream, and are reachable by id for
    /// drill-down). Pass <paramref name="includeChildren"/> = true to include them (internal callers
    /// that genuinely need the full set).
    /// </summary>
    public async Task<IReadOnlyList<Run>> GetRunsByProjectAsync(
        ProjectId projectId, bool includeChildren = false, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var childFilter = includeChildren ? string.Empty : " AND parent_run_id IS NULL";
        command.CommandText = SelectSql + " WHERE project_id = $projectId AND archived_at IS NULL" + childFilter + " ORDER BY started_at DESC;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE parent_run_id = $parentRunId ORDER BY started_at DESC;";
        command.Parameters.AddWithValue("$parentRunId", parentRunId);
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(
        ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default)
    {
        var statusStrings = statuses.Select(s => s.ToApiString()).ToList();
        var paramNames = statusStrings.Select((_, i) => $"$s{i}").ToList();
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + $" WHERE project_id = $projectId AND status IN ({string.Join(", ", paramNames)});";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        for (int i = 0; i < statusStrings.Count; i++)
            command.Parameters.AddWithValue(paramNames[i], statusStrings[i]);
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<IReadOnlyList<Run>> GetRunsBySubmittingUserAsync(
        string submittingUser, string? agentName, int limit, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var agentFilter = agentName is null ? string.Empty : " AND agent_name = $agentName";
        command.CommandText = SelectSql +
            " WHERE submitting_user = $user AND archived_at IS NULL" + agentFilter +
            " ORDER BY started_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$user", submittingUser);
        if (agentName is not null)
            command.Parameters.AddWithValue("$agentName", agentName);
        command.Parameters.AddWithValue("$limit", limit);
        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    /// <summary>
    /// Atomically inserts a new run row with status Pending only when the
    /// referenced project is still Active. Returns true if the row was inserted
    /// (project was Active); returns false if the project is Deleting or missing
    /// (the run should be rejected with 409).
    /// </summary>
    public async Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                              submitting_user, status, started_at, ended_at, result,
                              worktree_path, worktree_branch, project_id, model_id,
                              agent_name, agent_charter, workflow_run_id, parent_run_id, subtask_id,
                              retried_from, launch_auto_approve_tools, launch_autopilot,
                              approval_policy_snapshot_id,
                              approval_policy_source, approval_policy_captured_at,
                              approval_policy_settings_updated_at, approval_policy_inherited_from_run_id)
            SELECT $runId, $repo, $branch, $modelSource, $task,
                   $user, $status, $startedAt, NULL, NULL,
                   NULL, NULL, $projectId, $modelId,
                   $agentName, $agentCharter, $workflowRunId, $parentRunId, $subtaskId,
                   $retriedFrom, $launchAutoApproveTools, $launchAutopilot,
                   $approvalPolicySnapshotId,
                   $approvalPolicySource, $approvalPolicyCapturedAt,
                   $approvalPolicySettingsUpdatedAt, $approvalPolicyInheritedFromRunId
            WHERE EXISTS (
                SELECT 1 FROM projects WHERE project_id = $projectId AND state = 'active'
            );
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$repo", run.RepositoryPath);
        command.Parameters.AddWithValue("$branch", run.OriginatingBranch);
        command.Parameters.AddWithValue("$modelSource", run.ModelSource.ToApiString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$status", RunStatus.Pending.ToApiString());
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$projectId", run.ProjectId!.Value.ToString());
        command.Parameters.AddWithValue("$modelId", (object?)run.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentName", (object?)run.AgentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$agentCharter", (object?)run.AgentCharter ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowRunId", (object?)run.WorkflowRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentRunId", (object?)run.ParentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$subtaskId", (object?)run.SubtaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$retriedFrom", (object?)run.RetriedFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("$launchAutoApproveTools", run.LaunchAutoApproveTools is { } autoApproveTools ? autoApproveTools ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$launchAutopilot", run.LaunchAutopilot is { } autopilot ? autopilot ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySnapshotId", (object?)run.ApprovalPolicySnapshotId ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicySource", (object?)run.ApprovalPolicySource ?? DBNull.Value);
        command.Parameters.AddWithValue("$approvalPolicyCapturedAt", NullableTs(run.ApprovalPolicyCapturedAt));
        command.Parameters.AddWithValue("$approvalPolicySettingsUpdatedAt", NullableTs(run.ApprovalPolicySettingsUpdatedAt));
        command.Parameters.AddWithValue("$approvalPolicyInheritedFromRunId", (object?)run.ApprovalPolicyInheritedFromRunId ?? DBNull.Value);
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    // Ordinals: 0=run_id 1=repository_path 2=originating_branch 3=model_source 4=task
    //           5=submitting_user 6=status 7=approval_generation 8=lifecycle_generation 9=started_at 10=ended_at 11=result
    //           12=worktree_path 13=worktree_branch 14=tree_hash 15=diff 16=merge_conflicts
    //           17=project_id 18=model_id 19=agent_name 20=agent_charter 21=reviewed_by
    //           22=workflow_run_id 23=merged_commit_hash 24=parent_run_id 25=subtask_id
    //           26=origin 27=retried_from 28=archived_at 29=sandbox_backend 30=sandbox_claim_name
    //           31=sandbox_pod_name 32=sandbox_namespace 33=workflow_selection_reason
    //           34=launch_auto_approve_tools 35=launch_autopilot 36=approval_policy_snapshot_id
    //           37=approval_policy_source 38=approval_policy_captured_at
    //           39=approval_policy_settings_updated_at 40=approval_policy_inherited_from_run_id
    private const string SelectSql =
        """
        SELECT run_id, repository_path, originating_branch, model_source, task,
               submitting_user, status, approval_generation, lifecycle_generation, started_at, ended_at, result,
               worktree_path, worktree_branch, tree_hash, diff, merge_conflicts,
               project_id, model_id, agent_name, agent_charter, reviewed_by,
               workflow_run_id, merged_commit_hash, parent_run_id, subtask_id,
               origin, retried_from, archived_at, sandbox_backend, sandbox_claim_name,
               sandbox_pod_name, sandbox_namespace, workflow_selection_reason,
               launch_auto_approve_tools, launch_autopilot, approval_policy_snapshot_id,
               approval_policy_source,
               approval_policy_captured_at, approval_policy_settings_updated_at,
               approval_policy_inherited_from_run_id
          FROM runs
        """;

    private static Run Map(SqliteDataReader r) => new()
    {
        Id               = RunId.Parse(r.GetString(0)),
        RepositoryPath   = r.GetString(1),
        OriginatingBranch = r.GetString(2),
        ModelSource      = ModelSourceExtensions.FromApiString(r.GetString(3)),
        Task             = r.GetString(4),
        SubmittingUser   = r.GetString(5),
        Status           = RunStatusExtensions.ParseStatus(r.GetString(6)),
        ApprovalGeneration = r.GetInt32(7),
        LifecycleGeneration = r.GetInt32(8),
        StartedAt        = DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        EndedAt          = r.IsDBNull(10)  ? null : DateTimeOffset.Parse(r.GetString(10),  CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Result           = r.IsDBNull(11) ? null : r.GetString(11),
        WorktreePath     = r.IsDBNull(12) ? null : r.GetString(12),
        WorktreeBranch   = r.IsDBNull(13) ? null : r.GetString(13),
        TreeHash         = r.IsDBNull(14) ? null : r.GetString(14),
        StepCount        = 0,
        Diff             = r.IsDBNull(15) ? null : r.GetString(15),
        MergeConflicts   = r.IsDBNull(16) ? null : r.GetString(16),
        ProjectId        = r.IsDBNull(17) ? null : ProjectId.Parse(r.GetString(17)),
        ModelId          = r.IsDBNull(18) ? null : r.GetString(18),
        AgentName        = r.IsDBNull(19) ? null : r.GetString(19),
        AgentCharter     = r.IsDBNull(20) ? null : r.GetString(20),
        ReviewedBy       = r.IsDBNull(21) ? null : r.GetString(21),
        WorkflowRunId    = r.IsDBNull(22) ? null : r.GetString(22),
        MergedCommitHash = r.IsDBNull(23) ? null : r.GetString(23),
        ParentRunId      = r.IsDBNull(24) ? null : r.GetString(24),
        SubtaskId        = r.IsDBNull(25) ? null : r.GetString(25),
        Origin           = RunOriginExtensions.ParseOrigin(r.IsDBNull(26) ? null : r.GetString(26)),
        RetriedFrom      = r.IsDBNull(27) ? null : r.GetString(27),
        ArchivedAt       = r.IsDBNull(28) ? null : DateTimeOffset.Parse(r.GetString(28), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SandboxBackend   = r.IsDBNull(29) ? null : r.GetString(29),
        SandboxClaimName = r.IsDBNull(30) ? null : r.GetString(30),
        SandboxPodName   = r.IsDBNull(31) ? null : r.GetString(31),
        SandboxNamespace = r.IsDBNull(32) ? null : r.GetString(32),
        WorkflowSelectionReason = r.IsDBNull(33) ? null : r.GetString(33),
        LaunchAutoApproveTools = r.IsDBNull(34) ? null : r.GetInt32(34) != 0,
        LaunchAutopilot = r.IsDBNull(35) ? null : r.GetInt32(35) != 0,
        ApprovalPolicySnapshotId = r.IsDBNull(36) ? null : r.GetString(36),
        ApprovalPolicySource = r.IsDBNull(37) ? null : r.GetString(37),
        ApprovalPolicyCapturedAt = r.IsDBNull(38) ? null : DateTimeOffset.Parse(r.GetString(38), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ApprovalPolicySettingsUpdatedAt = r.IsDBNull(39) ? null : DateTimeOffset.Parse(r.GetString(39), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ApprovalPolicyInheritedFromRunId = r.IsDBNull(40) ? null : r.GetString(40),
    };

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
    private static object NullableTs(DateTimeOffset? v) => v is null ? DBNull.Value : Ts(v.Value);

    /// <summary>
    /// Returns the run whose workflow_run_id matches, falling back to run_id for
    /// legacy runs that have no workflow_run_id (COALESCE behaviour).
    /// </summary>
    public async Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE COALESCE(workflow_run_id, run_id) = $workflowRunId;";
        command.Parameters.AddWithValue("$workflowRunId", workflowRunId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            "UPDATE runs SET workflow_selection_reason = $reason WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "update workflow selection reason");
    }

    public async Task UpdateModelSourceAsync(RunId runId, ModelSource modelSource, CancellationToken ct = default)
    {
        var rows = await ExecuteNonQueryAsync(
            "UPDATE runs SET model_source = $modelSource WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$modelSource", modelSource.ToApiString());
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
        WarnIfNoRows(rows, runId, "update model source");
    }
}
