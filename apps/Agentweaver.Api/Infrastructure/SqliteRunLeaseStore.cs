using System.Globalization;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteRunLeaseStore(SqliteDb db) : IRunLeaseStore
{
    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public async Task<(bool Claimed, long FencingToken)> TryClaimAsync(
        string runId, string ownerId, TimeSpan leaseTtl, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO run_execution_leases (run_id, owner_id, fencing_token, lease_expires_at)
            SELECT $runId, $owner, 1, $deadline
             WHERE EXISTS (SELECT 1 FROM runs WHERE run_id=$runId)
            ON CONFLICT(run_id) DO UPDATE SET owner_id=$owner,
                fencing_token=fencing_token+1, lease_expires_at=$deadline
             WHERE lease_expires_at < $now
            RETURNING fencing_token;
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$deadline", Timestamp(now.Add(leaseTtl)));
        command.Parameters.AddWithValue("$now", Timestamp(now));
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result is null ? (false, 0) : (true, Convert.ToInt64(result, CultureInfo.InvariantCulture));
    }

    public async Task<bool> TryRenewAsync(
        string runId, string ownerId, long fencingToken, TimeSpan leaseTtl, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await UpdateAsync(
            "UPDATE run_execution_leases SET lease_expires_at=$deadline WHERE run_id=$runId AND owner_id=$owner AND fencing_token=$token AND lease_expires_at>$now;",
            runId, ownerId, fencingToken, Timestamp(now), Timestamp(now.Add(leaseTtl)), ct).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(
        string runId, string ownerId, long fencingToken, CancellationToken ct = default)
    {
        await UpdateAsync(
            "UPDATE run_execution_leases SET lease_expires_at=$now WHERE run_id=$runId AND owner_id=$owner AND fencing_token=$token;",
            runId, ownerId, fencingToken, Timestamp(DateTimeOffset.UtcNow), null, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsLeaseOwnerAsync(
        string runId, string ownerId, long fencingToken, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM run_execution_leases WHERE run_id=$runId AND owner_id=$owner AND fencing_token=$token AND lease_expires_at>$now);";
        Bind(command, runId, ownerId, fencingToken, Timestamp(DateTimeOffset.UtcNow), null);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task<RunLeaseClaim?> GetActiveClaimAsync(
        string runId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT l.owner_id, l.fencing_token, r.lifecycle_generation
              FROM run_execution_leases l
              JOIN runs r ON r.run_id = l.run_id
             WHERE l.run_id=$runId AND l.lease_expires_at>$now;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new RunLeaseClaim(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2))
            : null;
    }

    private async Task<bool> UpdateAsync(
        string sql, string runId, string ownerId, long fencingToken, string now, string? deadline, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, runId, ownerId, fencingToken, now, deadline);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 0;
    }

    private static void Bind(
        Microsoft.Data.Sqlite.SqliteCommand command, string runId, string ownerId,
        long fencingToken, string now, string? deadline)
    {
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$token", fencingToken);
        command.Parameters.AddWithValue("$now", now);
        if (deadline is not null)
            command.Parameters.AddWithValue("$deadline", deadline);
    }
}
