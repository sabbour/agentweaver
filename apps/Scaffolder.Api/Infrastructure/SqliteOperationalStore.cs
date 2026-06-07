using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Scaffolder.Api.Contracts;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="IOperationalStore"/>. A partial record is written at
/// run start and updated once at the terminal event. This record is separate
/// from the event log so compliance consumers can read run outcomes and policy
/// decisions without replaying events.
/// </summary>
public sealed class SqliteOperationalStore : IOperationalStore
{
    private readonly SqliteDb _db;

    public SqliteOperationalStore(SqliteDb db) => _db = db;

    public async Task CreateAsync(
        RunId runId,
        string submittingUser,
        ModelSource modelSource,
        DateTimeOffset startedAt,
        IReadOnlyList<string> policyDecisions,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO run_operational_records
                (run_id, submitting_user, model_source, started_at, policy_decisions)
            VALUES ($runId, $user, $modelSource, $startedAt, $policy)
            ON CONFLICT(run_id) DO UPDATE SET
                submitting_user = excluded.submitting_user,
                model_source = excluded.model_source,
                started_at = excluded.started_at,
                policy_decisions = excluded.policy_decisions;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$user", submittingUser);
        command.Parameters.AddWithValue("$modelSource", modelSource.ToApiString());
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(policyDecisions, JsonDefaults.Options));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(
        RunId runId,
        DateTimeOffset endedAt,
        int stepCount,
        string outcome,
        IReadOnlyList<string> policyDecisions,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE run_operational_records
            SET ended_at = $endedAt,
                step_count = $stepCount,
                outcome = $outcome,
                policy_decisions = $policy
            WHERE run_id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$stepCount", stepCount);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$policy", JsonSerializer.Serialize(policyDecisions, JsonDefaults.Options));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
