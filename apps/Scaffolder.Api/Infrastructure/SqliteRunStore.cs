using System.Globalization;
using Microsoft.Data.Sqlite;
using Scaffolder.Api.Contracts;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

public sealed class SqliteRunStore
{
    private readonly SqliteDb _db;

    public SqliteRunStore(SqliteDb db) => _db = db;

    public async Task InsertAsync(Run run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                              submitting_user, status, started_at, ended_at, result,
                              worktree_path, worktree_branch, project_id, model_id,
                              agent_name, agent_charter, workflow_run_id)
            VALUES ($runId, $repo, $branch, $modelSource, $task,
                    $user, $status, $startedAt, $endedAt, $result,
                    $worktreePath, $worktreeBranch, $projectId, $modelId,
                    $agentName, $agentCharter, $workflowRunId);
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$repo", run.RepositoryPath);
        command.Parameters.AddWithValue("$branch", run.OriginatingBranch);
        command.Parameters.AddWithValue("$modelSource", run.ModelSource.ToApiString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$status", run.Status.ToApiString());
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
        await ExecuteNonQueryAsync(
            "UPDATE runs SET status = $status, ended_at = $endedAt WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", NullableTs(endedAt));
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    public async Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            "UPDATE runs SET status = $status, ended_at = $endedAt, result = $result WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", Ts(endedAt));
                cmd.Parameters.AddWithValue("$result", result);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets tree_hash, diff, and step_count after the agent commits its changes,
    /// and advances status to AwaitingReview. ended_at is intentionally not set
    /// because the run is still awaiting a human decision.
    /// </summary>
    public async Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            """
            UPDATE runs
               SET tree_hash = $treeHash, diff = $diff, step_count = $stepCount,
                   status = $status
             WHERE run_id = $runId;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", treeHash);
                cmd.Parameters.AddWithValue("$diff", diff);
                cmd.Parameters.AddWithValue("$stepCount", stepCount);
                cmd.Parameters.AddWithValue("$status", RunStatus.AwaitingReview.ToApiString());
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically transitions a run from AwaitingReview to InProgress.
    /// Returns true if the CAS succeeded (request-changes won the race),
    /// false if another request already moved the run out of AwaitingReview.
    /// Used by the request-changes endpoint (B3) to reclaim the run for a new revision.
    /// </summary>
    public async Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'in_progress', ended_at = NULL WHERE run_id = $runId AND status = 'awaiting_review';";
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
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = $toStatus, ended_at = $endedAt, result = $result, reviewed_by = $reviewer
             WHERE run_id = $runId AND status = 'awaiting_review';
            """;
        command.Parameters.AddWithValue("$toStatus", toStatus.ToApiString());
        command.Parameters.AddWithValue("$endedAt", Ts(endedAt));
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$reviewer", (object?)reviewer ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Atomically transitions a run from AwaitingReview to Committing.
    /// Returns true if the CAS succeeded (this /commit request owns the run),
    /// false if another request already moved the run out of AwaitingReview.
    /// Must be called BEFORE CommitChanges to prevent TOCTOU races.
    /// </summary>
    public async Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'committing' WHERE run_id = $runId AND status = 'awaiting_review';";
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
    public async Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'awaiting_review', tree_hash = COALESCE($treeHash, tree_hash) WHERE run_id = $runId AND status = 'committing';";
        command.Parameters.AddWithValue("$treeHash", (object?)treeHash ?? DBNull.Value);
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
    public async Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'merging', reviewed_by = $reviewer WHERE run_id = $runId AND status IN ('awaiting_review', 'committing');";
        command.Parameters.AddWithValue("$reviewer", (object?)reviewer ?? DBNull.Value);
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
    public async Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE runs SET status = 'awaiting_review' WHERE run_id = $runId AND status = 'merging';";
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
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = $toStatus, ended_at = $endedAt, result = $result, merge_conflicts = $mergeConflicts,
                   merged_commit_hash = COALESCE($mergedCommitHash, merged_commit_hash)
             WHERE run_id = $runId AND status = 'merging';
            """;
        command.Parameters.AddWithValue("$toStatus", toStatus.ToApiString());
        command.Parameters.AddWithValue("$endedAt", Ts(endedAt));
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$mergeConflicts", (object?)mergeConflicts ?? DBNull.Value);
        command.Parameters.AddWithValue("$mergedCommitHash", (object?)mergedCommitHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Updates the tree_hash of a run that is in Committing status.
    /// Used by the /commit endpoint after staging and committing any remaining
    /// uncommitted changes on top of the agent's commit.
    /// </summary>
    public async Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            "UPDATE runs SET tree_hash = $treeHash WHERE run_id = $runId AND status = 'committing';",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$treeHash", newTreeHash);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Conditionally sets a terminal status. Only writes if the current status is NOT already
    /// terminal (Guardrail 3: dual-writer safety). Returns true if the update was applied.
    /// </summary>
    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
               SET status = $toStatus, ended_at = $endedAt, result = $result
             WHERE run_id = $runId
               AND status NOT IN ('merged', 'declined', 'failed', 'completed', 'merge_failed');
            """;
        command.Parameters.AddWithValue("$toStatus", toStatus.ToApiString());
        command.Parameters.AddWithValue("$endedAt", Ts(endedAt));
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        await ExecuteNonQueryAsync(
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
    }

    public async Task DeleteAsync(RunId runId, CancellationToken ct = default)
    {
        await ExecuteNonQueryAsync(
            "DELETE FROM runs WHERE run_id = $runId;",
            cmd => cmd.Parameters.AddWithValue("$runId", runId.ToString()), ct).ConfigureAwait(false);
    }

    private async Task ExecuteNonQueryAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId ORDER BY started_at DESC;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
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
                              agent_name, agent_charter, workflow_run_id)
            SELECT $runId, $repo, $branch, $modelSource, $task,
                   $user, $status, $startedAt, NULL, NULL,
                   NULL, NULL, $projectId, $modelId,
                   $agentName, $agentCharter, $workflowRunId
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
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    // Ordinals: 0=run_id 1=repository_path 2=originating_branch 3=model_source 4=task
    //           5=submitting_user 6=status 7=started_at 8=ended_at 9=result
    //           10=worktree_path 11=worktree_branch 12=tree_hash 13=step_count 14=diff
    //           15=merge_conflicts 16=project_id 17=model_id 18=agent_name 19=agent_charter
    //           20=reviewed_by 21=workflow_run_id 22=merged_commit_hash
    private const string SelectSql =
        """
        SELECT run_id, repository_path, originating_branch, model_source, task,
               submitting_user, status, started_at, ended_at, result,
               worktree_path, worktree_branch, tree_hash, step_count, diff,
               merge_conflicts, project_id, model_id, agent_name, agent_charter,
               reviewed_by, workflow_run_id, merged_commit_hash
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
        StartedAt        = DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        EndedAt          = r.IsDBNull(8)  ? null : DateTimeOffset.Parse(r.GetString(8),  CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Result           = r.IsDBNull(9)  ? null : r.GetString(9),
        WorktreePath     = r.IsDBNull(10) ? null : r.GetString(10),
        WorktreeBranch   = r.IsDBNull(11) ? null : r.GetString(11),
        TreeHash         = r.IsDBNull(12) ? null : r.GetString(12),
        StepCount        = r.IsDBNull(13) ? 0    : r.GetInt32(13),
        Diff             = r.IsDBNull(14) ? null : r.GetString(14),
        MergeConflicts   = r.IsDBNull(15) ? null : r.GetString(15),
        ProjectId        = r.IsDBNull(16) ? null : ProjectId.Parse(r.GetString(16)),
        ModelId          = r.IsDBNull(17) ? null : r.GetString(17),
        AgentName        = r.IsDBNull(18) ? null : r.GetString(18),
        AgentCharter     = r.IsDBNull(19) ? null : r.GetString(19),
        ReviewedBy       = r.IsDBNull(20) ? null : r.GetString(20),
        WorkflowRunId    = r.IsDBNull(21) ? null : r.GetString(21),
        MergedCommitHash = r.IsDBNull(22) ? null : r.GetString(22),
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
}
