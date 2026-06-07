using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Scaffolder.Api.Security;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// Durable, append-only, per-run event log backed by SQLite (FR-022). Sequence
/// numbers are allocated server-side inside a write transaction so they are
/// per-run monotonic regardless of the caller (FR-019). No UPDATE or DELETE is
/// ever issued; the schema triggers enforce that as well. SQLITE_BUSY is retried
/// with exponential backoff.
/// </summary>
public sealed class SqliteEventStore : IRunEventStore
{
    private static readonly int[] RetryDelaysMs = { 50, 100, 200 };

    private readonly SqliteDb _db;

    public SqliteEventStore(SqliteDb db) => _db = db;

    /// <summary>
    /// Appends a new event, allocating its per-run sequence, and returns the
    /// persisted event including the assigned sequence and timestamp. The
    /// <paramref name="payloadJson"/> is checked for emoji content before it is
    /// written (Principle VIII).
    /// </summary>
    public async Task<RunEvent> AppendNewAsync(
        RunId runId,
        string type,
        string payloadJson,
        string? callId,
        CancellationToken ct = default)
    {
        if (!EventType.All.Contains(type))
        {
            throw new ArgumentException($"Unknown event type: {type}", nameof(type));
        }

        EmojiGuard.EnsureNone(payloadJson, "event payload");

        var timestamp = DateTimeOffset.UtcNow;

        return await RetryAsync(async () =>
        {
            await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            int sequence;
            await using (var nextCommand = connection.CreateCommand())
            {
                nextCommand.Transaction = transaction;
                nextCommand.CommandText =
                    "SELECT COALESCE(MAX(sequence), 0) + 1 FROM run_events WHERE run_id = $runId;";
                nextCommand.Parameters.AddWithValue("$runId", runId.ToString());
                var scalar = await nextCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
                sequence = Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
            }

            await using (var insertCommand = connection.CreateCommand())
            {
                insertCommand.Transaction = transaction;
                insertCommand.CommandText =
                    """
                    INSERT INTO run_events (run_id, sequence, type, timestamp, call_id, payload)
                    VALUES ($runId, $sequence, $type, $timestamp, $callId, $payload);
                    """;
                insertCommand.Parameters.AddWithValue("$runId", runId.ToString());
                insertCommand.Parameters.AddWithValue("$sequence", sequence);
                insertCommand.Parameters.AddWithValue("$type", type);
                insertCommand.Parameters.AddWithValue("$timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture));
                insertCommand.Parameters.AddWithValue("$callId", (object?)callId ?? DBNull.Value);
                insertCommand.Parameters.AddWithValue("$payload", payloadJson);
                await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new RunEvent
            {
                RunId = runId,
                Sequence = sequence,
                Type = type,
                Timestamp = timestamp,
                Payload = payloadJson,
                CallId = callId
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<RunEvent> AppendAsync(RunEvent evt, CancellationToken ct = default)
    {
        // The sequence is always allocated server-side; any incoming value is ignored.
        // The returned event carries the allocated sequence for live publishing.
        return await AppendNewAsync(evt.RunId, evt.Type, evt.Payload, evt.CallId, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<RunEvent> ReadFromAsync(
        RunId runId,
        int afterSequence,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, type, timestamp, call_id, payload
            FROM run_events
            WHERE run_id = $runId AND sequence > $afterSequence
            ORDER BY sequence ASC;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$afterSequence", afterSequence);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return Map(runId, reader);
        }
    }

    public async Task<RunEvent?> GetLatestTerminalEventAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, type, timestamp, call_id, payload
            FROM run_events
            WHERE run_id = $runId AND type IN ($completed, $failed, $bounded)
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$completed", EventType.RunCompleted);
        command.Parameters.AddWithValue("$failed", EventType.RunFailed);
        command.Parameters.AddWithValue("$bounded", EventType.RunBounded);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return Map(runId, reader);
        }

        return null;
    }

    /// <summary>Returns whether any event of the given type exists for a run.</summary>
    public async Task<bool> HasEventOfTypeAsync(RunId runId, string type, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM run_events WHERE run_id = $runId AND type = $type);";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$type", type);
        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>Counts events of a given type for a run.</summary>
    public async Task<int> CountEventsOfTypeAsync(RunId runId, string type, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM run_events WHERE run_id = $runId AND type = $type;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$type", type);
        var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static RunEvent Map(RunId runId, SqliteDataReader reader) => new()
    {
        RunId = runId,
        Sequence = reader.GetInt32(0),
        Type = reader.GetString(1),
        Timestamp = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CallId = reader.IsDBNull(3) ? null : reader.GetString(3),
        Payload = reader.GetString(4)
    };

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < RetryDelaysMs.Length)
            {
                await Task.Delay(RetryDelaysMs[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY or SQLITE_LOCKED
}
