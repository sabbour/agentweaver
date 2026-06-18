using Microsoft.Data.Sqlite;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteWorkflowRunStore
{
    private readonly SqliteDb _db;
    public SqliteWorkflowRunStore(SqliteDb db) => _db = db;

    public async Task InsertAsync(WorkflowRun run, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO workflow_runs (workflow_run_id, project_id, task, submitting_user, started_at)
            VALUES ($id, $projectId, $task, $user, $startedAt);
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$projectId", run.ProjectId.ToString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
