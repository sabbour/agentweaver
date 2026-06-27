using Microsoft.Data.Sqlite;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteWorkflowRunStore : IWorkflowRunStore
{
    private readonly SqliteDb _db;
    public SqliteWorkflowRunStore(SqliteDb db) => _db = db;

    public async Task InsertAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO workflow_runs (workflow_run_id, project_id, task, submitting_user, started_at, orchestration_worktree_path)
            VALUES ($id, $projectId, $task, $user, $startedAt, $orchestrationWorktreePath);
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$projectId", run.ProjectId.ToString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$orchestrationWorktreePath", (object?)run.OrchestrationWorktreePath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the provisioned shared worktree path for an orchestration's workflow_run record.
    /// Called once when the first child run is dispatched in a coordinator orchestration.
    /// Idempotent: a second call is a no-op if the path is already set (WHERE clause guards it).
    /// </summary>
    public async Task SetOrchestrationWorktreePathAsync(
        string workflowRunId, string worktreePath, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE workflow_runs
               SET orchestration_worktree_path = $path
             WHERE workflow_run_id = $id AND orchestration_worktree_path IS NULL;
            """;
        command.Parameters.AddWithValue("$path", worktreePath);
        command.Parameters.AddWithValue("$id", workflowRunId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the shared worktree path for a workflow run, or null when not yet provisioned.
    /// </summary>
    public async Task<string?> GetOrchestrationWorktreePathAsync(
        string workflowRunId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT orchestration_worktree_path FROM workflow_runs WHERE workflow_run_id = $id;";
        command.Parameters.AddWithValue("$id", workflowRunId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is DBNull or null ? null : (string)result;
    }
}
