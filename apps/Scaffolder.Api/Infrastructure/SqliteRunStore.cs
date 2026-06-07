using System.Globalization;
using Microsoft.Data.Sqlite;
using Scaffolder.Api.Contracts;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// Stores and retrieves <see cref="Run"/> records. The run record holds the
/// mutable lifecycle fields (status, timing, worktree location, committed tree
/// hash); the immutable history of the run lives in the event log.
/// </summary>
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
            INSERT INTO runs
                (run_id, repository_path, originating_branch, model_source, task,
                 submitting_user, status, started_at, ended_at, step_count,
                 worktree_path, worktree_branch, committed_tree_hash)
            VALUES
                ($runId, $repo, $branch, $modelSource, $task,
                 $user, $status, $startedAt, $endedAt, $stepCount,
                 $worktreePath, $worktreeBranch, $treeHash);
            """;
        command.Parameters.AddWithValue("$runId", run.Id.ToString());
        command.Parameters.AddWithValue("$repo", run.RepositoryPath);
        command.Parameters.AddWithValue("$branch", run.OriginatingBranch);
        command.Parameters.AddWithValue("$modelSource", run.ModelSource.ToApiString());
        command.Parameters.AddWithValue("$task", run.Task);
        command.Parameters.AddWithValue("$user", run.SubmittingUser);
        command.Parameters.AddWithValue("$status", run.Status.ToApiString());
        command.Parameters.AddWithValue("$startedAt", run.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endedAt", ToNullableTimestamp(run.EndedAt));
        command.Parameters.AddWithValue("$stepCount", run.StepCount);
        command.Parameters.AddWithValue("$worktreePath", (object?)run.WorktreePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreeBranch", (object?)run.WorktreeBranch ?? DBNull.Value);
        command.Parameters.AddWithValue("$treeHash", (object?)run.CommittedTreeHash ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Run?> GetAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return Map(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE status = $status;";
        command.Parameters.AddWithValue("$status", status.ToApiString());

        var results = new List<Run>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    public async Task UpdateWorktreeAsync(
        RunId runId,
        string worktreePath,
        string worktreeBranch,
        CancellationToken ct = default)
    {
        await ExecuteAsync(
            "UPDATE runs SET worktree_path = $worktreePath, worktree_branch = $worktreeBranch WHERE run_id = $runId;",
            command =>
            {
                command.Parameters.AddWithValue("$worktreePath", worktreePath);
                command.Parameters.AddWithValue("$worktreeBranch", worktreeBranch);
                command.Parameters.AddWithValue("$runId", runId.ToString());
            },
            ct).ConfigureAwait(false);
    }

    public async Task UpdateCommittedTreeHashAsync(RunId runId, string treeHash, CancellationToken ct = default)
    {
        await ExecuteAsync(
            "UPDATE runs SET committed_tree_hash = $treeHash WHERE run_id = $runId;",
            command =>
            {
                command.Parameters.AddWithValue("$treeHash", treeHash);
                command.Parameters.AddWithValue("$runId", runId.ToString());
            },
            ct).ConfigureAwait(false);
    }

    public async Task UpdateStepCountAsync(RunId runId, int stepCount, CancellationToken ct = default)
    {
        await ExecuteAsync(
            "UPDATE runs SET step_count = $stepCount WHERE run_id = $runId;",
            command =>
            {
                command.Parameters.AddWithValue("$stepCount", stepCount);
                command.Parameters.AddWithValue("$runId", runId.ToString());
            },
            ct).ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(
        RunId runId,
        RunStatus status,
        DateTimeOffset? endedAt,
        CancellationToken ct = default)
    {
        await ExecuteAsync(
            "UPDATE runs SET status = $status, ended_at = $endedAt WHERE run_id = $runId;",
            command =>
            {
                command.Parameters.AddWithValue("$status", status.ToApiString());
                command.Parameters.AddWithValue("$endedAt", ToNullableTimestamp(endedAt));
                command.Parameters.AddWithValue("$runId", runId.ToString());
            },
            ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string SelectColumns =
        """
        SELECT run_id, repository_path, originating_branch, model_source, task,
               submitting_user, status, started_at, ended_at, step_count,
               worktree_path, worktree_branch, committed_tree_hash
        FROM runs
        """;

    private static Run Map(SqliteDataReader reader) => new()
    {
        Id = RunId.Parse(reader.GetString(0)),
        RepositoryPath = reader.GetString(1),
        OriginatingBranch = reader.GetString(2),
        ModelSource = ModelSourceExtensions.FromApiString(reader.GetString(3)),
        Task = reader.GetString(4),
        SubmittingUser = reader.GetString(5),
        Status = ParseStatus(reader.GetString(6)),
        StartedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        EndedAt = reader.IsDBNull(8)
            ? null
            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        StepCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
        WorktreePath = reader.IsDBNull(10) ? null : reader.GetString(10),
        WorktreeBranch = reader.IsDBNull(11) ? null : reader.GetString(11),
        CommittedTreeHash = reader.IsDBNull(12) ? null : reader.GetString(12)
    };

    private static object ToNullableTimestamp(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static RunStatus ParseStatus(string value) => value switch
    {
        "pending" => RunStatus.Pending,
        "in_progress" => RunStatus.InProgress,
        "completed" => RunStatus.Completed,
        "failed" => RunStatus.Failed,
        "bounded" => RunStatus.Bounded,
        "reviewing" => RunStatus.Reviewing,
        "approved" => RunStatus.Approved,
        "declined" => RunStatus.Declined,
        _ => throw new ArgumentException($"Unknown run status: {value}", nameof(value))
    };
}
