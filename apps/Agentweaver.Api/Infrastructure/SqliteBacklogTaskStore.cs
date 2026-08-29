using System.Globalization;
using Microsoft.Data.Sqlite;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="IBacklogTaskStore"/>. Every read and mutation is project-scoped. Single-row
/// mutations are conditional UPDATEs returning rows-affected &gt; 0. Move/reorder operations retry on
/// the order_key UNIQUE constraint by regenerating a key in the destination bucket. The claim+reserve
/// is one atomic transaction so the board never observes an orphaned Claimed task.
/// </summary>
public sealed class SqliteBacklogTaskStore : IBacklogTaskStore
{
    private const int SqliteConstraint = 19;   // SQLITE_CONSTRAINT
    private const int MaxOrderKeyRetries = 5;

    private readonly SqliteDb _db;

    public SqliteBacklogTaskStore(SqliteDb db) => _db = db;

    public async Task InsertAsync(BacklogTask task, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO backlog_tasks (task_id, project_id, title, description, state, order_key,
                                       captured_by, captured_by_user_id, created_at, committed_at, claimed_at, run_id,
                                       workflow_override_id, archived_at, source_file_path,
                                       parent_prd_run_id, promotion_key, promotion_reason, automation_invocation_pending)
            VALUES ($taskId, $projectId, $title, $description, $state, $orderKey,
                    $capturedBy, $capturedByUserId, $createdAt, $committedAt, $claimedAt, $runId,
                    $workflowOverrideId, $archivedAt, $sourceFilePath,
                    $parentPrdRunId, $promotionKey, $promotionReason, $automationInvocationPending);
            """;
        BindFullRow(command, task);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<BacklogTask?> GetAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE task_id = $taskId AND project_id = $projectId;";
        command.Parameters.AddWithValue("$taskId", id.ToString());
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<BacklogTask?> GetByRunIdAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<BacklogTask>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql +
            " WHERE project_id = $projectId AND archived_at IS NULL ORDER BY state, order_key, committed_at, task_id;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BacklogTaskDependency>> ListDependenciesAsync(
        ProjectId projectId,
        IReadOnlyCollection<BacklogTaskId> taskIds,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return Array.Empty<BacklogTaskDependency>();

        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var taskPlaceholders = AddTaskIdParameters(command, taskIds);
        command.CommandText =
            $"""
            SELECT project_id, task_id, depends_on_task_id, created_at
              FROM backlog_task_dependencies
             WHERE project_id = $projectId
               AND task_id IN ({taskPlaceholders})
             ORDER BY task_id, depends_on_task_id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());

        var results = new List<BacklogTaskDependency>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new BacklogTaskDependency
            {
                ProjectId = ProjectId.Parse(reader.GetString(0)),
                TaskId = BacklogTaskId.Parse(reader.GetString(1)),
                DependsOnTaskId = BacklogTaskId.Parse(reader.GetString(2)),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<BacklogDependencyStatus>> ListDependencyStatusesAsync(
        ProjectId projectId,
        IReadOnlyCollection<BacklogTaskId> taskIds,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return Array.Empty<BacklogDependencyStatus>();

        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var taskPlaceholders = AddTaskIdParameters(command, taskIds);
        command.CommandText =
            $"""
            SELECT d.task_id,
                   d.depends_on_task_id,
                   prerequisite.title,
                   prerequisite.run_id,
                   r.status,
                   CASE
                       WHEN prerequisite.archived_at IS NULL
                        AND prerequisite.run_id IS NOT NULL
                        AND r.run_id IS NOT NULL
                        AND r.status = 'merged'
                       THEN 1 ELSE 0
                   END AS is_satisfied
              FROM backlog_task_dependencies d
              JOIN backlog_tasks prerequisite ON prerequisite.task_id = d.depends_on_task_id
              LEFT JOIN runs r ON r.run_id = prerequisite.run_id
             WHERE d.project_id = $projectId
               AND d.task_id IN ({taskPlaceholders})
             ORDER BY d.task_id, d.depends_on_task_id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());

        var results = new List<BacklogDependencyStatus>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new BacklogDependencyStatus(
                BacklogTaskId.Parse(reader.GetString(0)),
                BacklogTaskId.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : RunId.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : RunStatusExtensions.ParseStatus(reader.GetString(4)),
                reader.GetInt64(5) == 1));
        }

        return results;
    }

    public async Task<IReadOnlyList<BacklogTask>> ListReadyForClaimAsync(
        ProjectId projectId, int limit, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql +
            """
             WHERE project_id = $projectId AND state = 'ready' AND run_id IS NULL AND archived_at IS NULL
               AND NOT EXISTS (
                    SELECT 1
                      FROM backlog_task_dependencies d
                      JOIN backlog_tasks prerequisite ON prerequisite.task_id = d.depends_on_task_id
                      LEFT JOIN runs r ON r.run_id = prerequisite.run_id
                     WHERE d.task_id = backlog_tasks.task_id
                       AND (
                           prerequisite.archived_at IS NOT NULL
                           OR prerequisite.run_id IS NULL
                           OR r.run_id IS NULL
                           OR r.status <> 'merged'
                       )
               )
             ORDER BY order_key ASC, committed_at ASC, task_id ASC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$limit", limit);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<int> CountReadyForPickupAsync(CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
              FROM backlog_tasks bt
              JOIN projects p ON p.project_id = bt.project_id
             WHERE bt.state = 'ready' AND bt.run_id IS NULL AND bt.archived_at IS NULL
               AND p.state = 'active'
               AND NOT EXISTS (
                    SELECT 1
                      FROM backlog_task_dependencies d
                      JOIN backlog_tasks prerequisite ON prerequisite.task_id = d.depends_on_task_id
                      LEFT JOIN runs r ON r.run_id = prerequisite.run_id
                     WHERE d.task_id = bt.task_id
                       AND (
                           prerequisite.archived_at IS NOT NULL
                           OR prerequisite.run_id IS NULL
                           OR r.run_id IS NULL
                           OR r.status <> 'merged'
                       )
               );
            """;

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value switch
        {
            long count => checked((int)count),
            int count => count,
            _ => 0,
        };
    }

    public async Task<bool> UpdateContentAsync(
        ProjectId projectId, BacklogTaskId id, string title, string? description, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE backlog_tasks SET title = $title, description = $description
             WHERE task_id = $taskId AND project_id = $projectId
               AND archived_at IS NULL AND automation_invocation_pending = 0;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", id.ToString());
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        try
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw new BacklogTaskDependencyException("task_is_dependency");
        }
    }

    public async Task<bool> UpdateWorkflowOverrideAsync(
        ProjectId projectId, BacklogTaskId id, string? workflowId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE backlog_tasks SET workflow_override_id = $workflowOverrideId
             WHERE task_id = $taskId AND project_id = $projectId
               AND state IN ('backlog','ready') AND run_id IS NULL AND archived_at IS NULL
               AND automation_invocation_pending = 0;
            """;
        command.Parameters.AddWithValue("$workflowOverrideId", (object?)workflowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$taskId", id.ToString());
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> TryDeleteAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM backlog_tasks
             WHERE task_id = $taskId AND project_id = $projectId
               AND state IN ('backlog','ready') AND run_id IS NULL AND archived_at IS NULL
               AND automation_invocation_pending = 0;
            """;
        command.Parameters.AddWithValue("$taskId", id.ToString());
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        try
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            throw new BacklogTaskDependencyException("task_is_dependency");
        }
    }

    public async Task<bool> TryDeleteProvisionalAutomationTaskAsync(
        ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM backlog_tasks
             WHERE task_id = $taskId AND project_id = $projectId
               AND state = 'backlog' AND run_id IS NULL AND archived_at IS NULL
               AND automation_invocation_pending = 1;
            """;
        command.Parameters.AddWithValue("$taskId", id.ToString());
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> TryArchiveAsync(
        ProjectId projectId, BacklogTaskId id, DateTimeOffset archivedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();

        string? linkedRunId = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText =
                """
                SELECT run_id FROM backlog_tasks
                 WHERE task_id = $taskId AND project_id = $projectId AND archived_at IS NULL
                   AND automation_invocation_pending = 0;
                """;
            read.Parameters.AddWithValue("$taskId", id.ToString());
            read.Parameters.AddWithValue("$projectId", projectId.ToString());
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
            linkedRunId = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        await using (var archiveTask = connection.CreateCommand())
        {
            archiveTask.Transaction = tx;
            archiveTask.CommandText =
                """
                UPDATE backlog_tasks
                   SET archived_at = $archivedAt
                 WHERE task_id = $taskId AND project_id = $projectId AND archived_at IS NULL;
                """;
            archiveTask.Parameters.AddWithValue("$archivedAt", Ts(archivedAt));
            archiveTask.Parameters.AddWithValue("$taskId", id.ToString());
            archiveTask.Parameters.AddWithValue("$projectId", projectId.ToString());
            var rows = await archiveTask.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        if (!string.IsNullOrEmpty(linkedRunId))
        {
            await using var archiveRun = connection.CreateCommand();
            archiveRun.Transaction = tx;
            archiveRun.CommandText =
                """
                UPDATE runs
                   SET archived_at = $archivedAt
                 WHERE run_id = $runId AND project_id = $projectId AND archived_at IS NULL;
                """;
            archiveRun.Parameters.AddWithValue("$archivedAt", Ts(archivedAt));
            archiveRun.Parameters.AddWithValue("$runId", linkedRunId);
            archiveRun.Parameters.AddWithValue("$projectId", projectId.ToString());
            await archiveRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public Task<bool> TryMoveToReadyAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "ready", newOrderKey, async (conn, key, c) =>
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                """
                UPDATE backlog_tasks
                   SET state = 'ready', order_key = $orderKey, committed_at = $committedAt
                 WHERE task_id = $taskId AND project_id = $projectId
                   AND state = 'backlog' AND archived_at IS NULL
                   AND automation_invocation_pending = 0;
                """;
            command.Parameters.AddWithValue("$orderKey", key);
            command.Parameters.AddWithValue("$committedAt", Ts(committedAt));
            command.Parameters.AddWithValue("$taskId", id.ToString());
            command.Parameters.AddWithValue("$projectId", projectId.ToString());
            return await command.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    public Task<bool> TryPublishAutomationInvocationTaskAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "ready", newOrderKey, async (conn, key, c) =>
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                """
                UPDATE backlog_tasks
                   SET state = 'ready', order_key = $orderKey, committed_at = $committedAt,
                       automation_invocation_pending = 0
                 WHERE task_id = $taskId AND project_id = $projectId
                   AND state = 'backlog' AND archived_at IS NULL
                   AND automation_invocation_pending = 1;
                """;
            command.Parameters.AddWithValue("$orderKey", key);
            command.Parameters.AddWithValue("$committedAt", Ts(committedAt));
            command.Parameters.AddWithValue("$taskId", id.ToString());
            command.Parameters.AddWithValue("$projectId", projectId.ToString());
            return await command.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    public Task<bool> TryMoveToBacklogAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "backlog", newOrderKey, async (conn, key, c) =>
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                """
                UPDATE backlog_tasks
                   SET state = 'backlog', order_key = $orderKey, committed_at = NULL
                 WHERE task_id = $taskId AND project_id = $projectId
                   AND state = 'ready' AND run_id IS NULL AND archived_at IS NULL
                   AND automation_invocation_pending = 0;
                """;
            command.Parameters.AddWithValue("$orderKey", key);
            command.Parameters.AddWithValue("$taskId", id.ToString());
            command.Parameters.AddWithValue("$projectId", projectId.ToString());
            return await command.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    public Task<bool> TryReorderAsync(
        ProjectId projectId, BacklogTaskId id, BacklogTaskState expectedState, string newOrderKey, CancellationToken ct = default)
    {
        var destState = expectedState.ToApiString();
        return RunWithOrderKeyRetryAsync(projectId, id, destState, newOrderKey, async (conn, key, c) =>
        {
            await using var command = conn.CreateCommand();
            command.CommandText =
                """
                UPDATE backlog_tasks
                   SET order_key = $orderKey
                 WHERE task_id = $taskId AND project_id = $projectId
                   AND state = $expectedState AND run_id IS NULL AND archived_at IS NULL
                   AND automation_invocation_pending = 0;
                """;
            command.Parameters.AddWithValue("$orderKey", key);
            command.Parameters.AddWithValue("$taskId", id.ToString());
            command.Parameters.AddWithValue("$projectId", projectId.ToString());
            command.Parameters.AddWithValue("$expectedState", destState);
            return await command.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);
    }

    public async Task<int> MoveAllBacklogToReadyAsync(
        ProjectId projectId, DateTimeOffset committedAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();

        // (a) Read the project's Backlog tasks in their current bucket order (order_key ordinal). This
        //     is the relative order to preserve when appending into Ready.
        var backlogIds = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText =
                """
                SELECT task_id FROM backlog_tasks
                 WHERE project_id = $projectId AND state = 'backlog' AND archived_at IS NULL
                   AND automation_invocation_pending = 0
                 ORDER BY order_key ASC, task_id ASC;
                """;
            read.Parameters.AddWithValue("$projectId", projectId.ToString());
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                backlogIds.Add(reader.GetString(0));
        }

        if (backlogIds.Count == 0)
        {
            // Idempotent no-op: nothing to move (empty backlog).
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return 0;
        }

        // (b) Seed the append cursor at the largest existing Ready order_key, so the moved tasks land
        //     AFTER all current Ready items (lowest priority), matching the single-item move-to-ready
        //     append scheme (OrderKey.Between(lastReadyKey, null)).
        string? lastKey;
        await using (var maxCmd = connection.CreateCommand())
        {
            maxCmd.Transaction = tx;
            maxCmd.CommandText =
                """
                SELECT order_key FROM backlog_tasks
                 WHERE project_id = $projectId AND state = 'ready' AND archived_at IS NULL
                 ORDER BY order_key DESC LIMIT 1;
                """;
            maxCmd.Parameters.AddWithValue("$projectId", projectId.ToString());
            var max = await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            lastKey = max is string s ? s : null;
        }

        // (c) Promote each Backlog task in order, assigning a strictly-increasing order_key appended
        //     after the running cursor. Keys are unique (each > the previous), so the
        //     (project_id, state, order_key) UNIQUE index is never violated.
        var moved = 0;
        foreach (var taskId in backlogIds)
        {
            var newKey = OrderKey.Between(lastKey, null);
            await using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE backlog_tasks
                   SET state = 'ready', order_key = $orderKey, committed_at = $committedAt
                 WHERE task_id = $taskId AND project_id = $projectId
                   AND state = 'backlog' AND archived_at IS NULL
                   AND automation_invocation_pending = 0;
                """;
            update.Parameters.AddWithValue("$orderKey", newKey);
            update.Parameters.AddWithValue("$committedAt", Ts(committedAt));
            update.Parameters.AddWithValue("$taskId", taskId);
            update.Parameters.AddWithValue("$projectId", projectId.ToString());
            moved += await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            lastKey = newKey;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return moved;
    }

    public async Task<ClaimReserveResult> TryClaimAndReserveCoordinatorRunAsync(
        ProjectId projectId,
        BacklogTaskId id,
        Run coordinatorRun,
        DateTimeOffset claimedAt,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = connection.BeginTransaction();

        // (a) exactly-once, project-scoped claim gate.
        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = tx;
            claim.CommandText =
                """
                UPDATE backlog_tasks
                   SET state = 'claimed', run_id = $runId, claimed_at = $claimedAt
                 WHERE task_id = $taskId AND project_id = $projectId
                   AND state = 'ready' AND run_id IS NULL AND archived_at IS NULL
                   AND NOT EXISTS (
                        SELECT 1
                          FROM backlog_task_dependencies d
                          JOIN backlog_tasks prerequisite ON prerequisite.task_id = d.depends_on_task_id
                          LEFT JOIN runs r ON r.run_id = prerequisite.run_id
                         WHERE d.task_id = backlog_tasks.task_id
                           AND (
                               prerequisite.archived_at IS NOT NULL
                               OR prerequisite.run_id IS NULL
                               OR r.run_id IS NULL
                               OR r.status <> 'merged'
                           )
                   );
                """;
            claim.Parameters.AddWithValue("$runId", coordinatorRun.Id.ToString());
            claim.Parameters.AddWithValue("$claimedAt", Ts(claimedAt));
            claim.Parameters.AddWithValue("$taskId", id.ToString());
            claim.Parameters.AddWithValue("$projectId", projectId.ToString());
            var claimedRows = await claim.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (claimedRows != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return ClaimReserveResult.Lost;
            }
        }

        // (b) persist the coordinator run row gated on the project still being active. Stamps the
        //     durable run-origin marker origin='backlog_pickup'. The run is identity-shaped EXACTLY
        //     like an interactive coordinator run: workflow_run_id IS NULL (coordinatorRun.WorkflowRunId
        //     is null) and NO workflow_runs envelope is written, so the board navigates by run_id and
        //     every coordinator-run detail endpoint (GetAsync, run_id-keyed) resolves it. Column list
        //     mirrors SqliteRunStore.TryCreateProjectRunAsync (one source of truth).
        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = tx;
            insertRun.CommandText =
                """
                INSERT INTO runs (run_id, repository_path, originating_branch, model_source, task,
                                  submitting_user, status, started_at, ended_at, result,
                                  worktree_path, worktree_branch, project_id, model_id,
                                  agent_name, agent_charter, workflow_run_id, parent_run_id, subtask_id, origin)
                SELECT $runId, $repo, $branch, $modelSource, $task,
                       $user, $status, $startedAt, $endedAt, $result,
                       NULL, NULL, $projectId, $modelId,
                       $agentName, $agentCharter, $workflowRunId, $parentRunId, $subtaskId, 'backlog_pickup'
                WHERE EXISTS (
                    SELECT 1 FROM projects WHERE project_id = $projectId AND state = 'active'
                );
                """;
            insertRun.Parameters.AddWithValue("$runId", coordinatorRun.Id.ToString());
            insertRun.Parameters.AddWithValue("$repo", coordinatorRun.RepositoryPath);
            insertRun.Parameters.AddWithValue("$branch", coordinatorRun.OriginatingBranch);
            insertRun.Parameters.AddWithValue("$modelSource", coordinatorRun.ModelSource.ToApiString());
            insertRun.Parameters.AddWithValue("$task", coordinatorRun.Task);
            insertRun.Parameters.AddWithValue("$user", coordinatorRun.SubmittingUser);
            insertRun.Parameters.AddWithValue("$status", coordinatorRun.Status.ToApiString());
            insertRun.Parameters.AddWithValue("$startedAt", Ts(coordinatorRun.StartedAt));
            insertRun.Parameters.AddWithValue("$endedAt", coordinatorRun.EndedAt is { } endedAt ? Ts(endedAt) : DBNull.Value);
            insertRun.Parameters.AddWithValue("$result", (object?)coordinatorRun.Result ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("$projectId", projectId.ToString());
            insertRun.Parameters.AddWithValue("$modelId", (object?)coordinatorRun.ModelId ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("$agentName", (object?)coordinatorRun.AgentName ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("$agentCharter", (object?)coordinatorRun.AgentCharter ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("$workflowRunId", (object?)coordinatorRun.WorkflowRunId ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("$parentRunId", (object?)coordinatorRun.ParentRunId ?? DBNull.Value);
            insertRun.Parameters.AddWithValue("$subtaskId", (object?)coordinatorRun.SubtaskId ?? DBNull.Value);
            var runRows = await insertRun.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (runRows != 1)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return ClaimReserveResult.ProjectUnavailable;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return ClaimReserveResult.Won;
    }

    /// <summary>
    /// Executes a single-row order-key mutation, retrying on an order_key UNIQUE conflict by
    /// regenerating a key in the destination bucket (re-reading neighbours). Returns the update's
    /// rows-affected &gt; 0. Throws <see cref="OrderKeyConflictException"/> if the retry budget is
    /// exhausted while still colliding.
    /// </summary>
    private async Task<bool> RunWithOrderKeyRetryAsync(
        ProjectId projectId,
        BacklogTaskId id,
        string destState,
        string initialKey,
        Func<SqliteConnection, string, CancellationToken, Task<int>> update,
        CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        var key = initialKey;
        for (var attempt = 0; attempt < MaxOrderKeyRetries; attempt++)
        {
            try
            {
                var rows = await update(connection, key, ct).ConfigureAwait(false);
                return rows > 0;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
            {
                key = await RegenerateKeyAsync(connection, projectId, destState, key, ct).ConfigureAwait(false);
            }
        }
        throw new OrderKeyConflictException(
            $"Could not place backlog task {id} in the '{destState}' bucket after {MaxOrderKeyRetries} attempts.");
    }

    /// <summary>Reads the destination bucket's next neighbour above the colliding key and returns a
    /// freshly extended key strictly between the colliding key and that neighbour.</summary>
    private static async Task<string> RegenerateKeyAsync(
        SqliteConnection connection, ProjectId projectId, string destState, string collidingKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT order_key FROM backlog_tasks
             WHERE project_id = $projectId AND state = $state AND order_key > $key
               AND archived_at IS NULL
             ORDER BY order_key ASC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$state", destState);
        command.Parameters.AddWithValue("$key", collidingKey);
        var next = (string?)await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return OrderKey.Between(collidingKey, next);
    }

    private static async Task<IReadOnlyList<BacklogTask>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var results = new List<BacklogTask>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    /// <summary>
    /// Returns the set of task titles that already exist for the given
    /// <paramref name="projectId"/> and <paramref name="sourceFilePath"/>. Used by the
    /// decompose endpoint to flag already-imported items without creating duplicates.
    /// </summary>
    public async Task<HashSet<string>> GetExistingTitlesFromSourceAsync(
        ProjectId projectId, string sourceFilePath, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT title FROM backlog_tasks
             WHERE project_id = $projectId
               AND source_file_path = $sourceFilePath
               AND archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$sourceFilePath", sourceFilePath);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            titles.Add(reader.GetString(0));
        return titles;
    }

    private static void BindFullRow(SqliteCommand command, BacklogTask task)
    {
        command.Parameters.AddWithValue("$taskId", task.Id.ToString());
        command.Parameters.AddWithValue("$projectId", task.ProjectId.ToString());
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", (object?)task.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", task.State.ToApiString());
        command.Parameters.AddWithValue("$orderKey", task.OrderKey);
        command.Parameters.AddWithValue("$capturedBy", task.CapturedBy);
        command.Parameters.AddWithValue("$capturedByUserId", (object?)task.CapturedByUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", Ts(task.CreatedAt));
        command.Parameters.AddWithValue("$committedAt", NullableTs(task.CommittedAt));
        command.Parameters.AddWithValue("$claimedAt", NullableTs(task.ClaimedAt));
        command.Parameters.AddWithValue("$runId", (object?)task.RunId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$workflowOverrideId", (object?)task.WorkflowOverrideId ?? DBNull.Value);
        command.Parameters.AddWithValue("$archivedAt", NullableTs(task.ArchivedAt));
        command.Parameters.AddWithValue("$sourceFilePath", (object?)task.SourceFilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$parentPrdRunId", (object?)task.ParentPrdRunId?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$promotionKey", (object?)task.PromotionKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$promotionReason", (object?)task.PromotionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$automationInvocationPending", task.IsAutomationInvocationPending ? 1 : 0);
    }

    // Ordinals: 0=task_id 1=project_id 2=title 3=description 4=state 5=order_key
    //           6=captured_by 7=captured_by_user_id 8=created_at 9=committed_at 10=claimed_at
    //           11=run_id 12=workflow_override_id 13=archived_at 14=source_file_path
    //           15=parent_prd_run_id 16=promotion_key 17=promotion_reason 18=automation_invocation_pending
    private const string SelectSql =
        """
        SELECT task_id, project_id, title, description, state, order_key,
              captured_by, captured_by_user_id, created_at, committed_at, claimed_at, run_id,
              workflow_override_id, archived_at, source_file_path,
              parent_prd_run_id, promotion_key, promotion_reason, automation_invocation_pending
          FROM backlog_tasks
        """;

    private static BacklogTask Map(SqliteDataReader r) => new()
    {
        Id          = BacklogTaskId.Parse(r.GetString(0)),
        ProjectId   = ProjectId.Parse(r.GetString(1)),
        Title       = r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        State       = BacklogTaskStateExtensions.ParseState(r.GetString(4)),
        OrderKey    = r.GetString(5),
        CapturedBy  = r.GetString(6),
        CapturedByUserId = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedAt   = DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CommittedAt = r.IsDBNull(9)  ? null : DateTimeOffset.Parse(r.GetString(9),  CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        ClaimedAt   = r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        RunId       = r.IsDBNull(11) ? null : RunId.Parse(r.GetString(11)),
        WorkflowOverrideId = r.IsDBNull(12) ? null : r.GetString(12),
        ArchivedAt  = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        SourceFilePath = r.IsDBNull(14) ? null : r.GetString(14),
        ParentPrdRunId = r.IsDBNull(15) ? null : RunId.Parse(r.GetString(15)),
        PromotionKey = r.IsDBNull(16) ? null : r.GetString(16),
        PromotionReason = r.IsDBNull(17) ? null : r.GetString(17),
        IsAutomationInvocationPending = r.GetInt64(18) != 0,
    };

    private static string AddTaskIdParameters(SqliteCommand command, IReadOnlyCollection<BacklogTaskId> taskIds)
    {
        var placeholders = new List<string>(taskIds.Count);
        var index = 0;
        foreach (var taskId in taskIds)
        {
            var name = $"$taskId{index++}";
            placeholders.Add(name);
            command.Parameters.AddWithValue(name, taskId.ToString());
        }

        return string.Join(", ", placeholders);
    }

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
    private static object NullableTs(DateTimeOffset? v) => v is null ? DBNull.Value : Ts(v.Value);
}
