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
                              submitting_user, status, started_at, ended_at, result)
            VALUES ($runId, $repo, $branch, $modelSource, $task,
                    $user, $status, $startedAt, $endedAt, $result);
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
        await ExecuteAsync(
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
        await ExecuteAsync(
            "UPDATE runs SET status = $status, ended_at = $endedAt, result = $result WHERE run_id = $runId;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$status", status.ToApiString());
                cmd.Parameters.AddWithValue("$endedAt", Ts(endedAt));
                cmd.Parameters.AddWithValue("$result", result);
                cmd.Parameters.AddWithValue("$runId", runId.ToString());
            }, ct).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string SelectSql =
        "SELECT run_id, repository_path, originating_branch, model_source, task, submitting_user, status, started_at, ended_at, result FROM runs";

    private static Run Map(SqliteDataReader r) => new()
    {
        Id = RunId.Parse(r.GetString(0)),
        RepositoryPath = r.GetString(1),
        OriginatingBranch = r.GetString(2),
        ModelSource = ModelSourceExtensions.FromApiString(r.GetString(3)),
        Task = r.GetString(4),
        SubmittingUser = r.GetString(5),
        Status = ParseStatus(r.GetString(6)),
        StartedAt = DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        EndedAt = r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Result = r.IsDBNull(9) ? null : r.GetString(9),
    };

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
    private static object NullableTs(DateTimeOffset? v) => v is null ? DBNull.Value : Ts(v.Value);

    private static RunStatus ParseStatus(string value) => value switch
    {
        "pending"     => RunStatus.Pending,
        "in_progress" => RunStatus.InProgress,
        "completed"   => RunStatus.Completed,
        "failed"      => RunStatus.Failed,
        _ => throw new ArgumentException($"Unknown run status: {value}", nameof(value))
    };
}
