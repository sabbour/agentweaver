using System.Globalization;
using Microsoft.Data.Sqlite;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// Durable audit store for run revisions (B3 / Principle X).
/// Each row records a human-requested revision cycle: who requested it, when,
/// the raw and sanitized feedback comment, and the tree hash at the time of
/// the request so that the full history of changes can be reconstructed.
/// </summary>
public sealed class SqliteRunRevisionStore
{
    private readonly SqliteDb _db;

    public SqliteRunRevisionStore(SqliteDb db) => _db = db;

    /// <summary>
    /// Inserts a revision audit row. The caller must already hold the CAS transition
    /// (run is now in_progress) before calling this to ensure the row is written
    /// only when the revision is authoritative.
    /// </summary>
    public async Task InsertRevisionAsync(
        RunId runId,
        int revisionNumber,
        string reviewerUser,
        string rawComment,
        string sanitizedComment,
        string previousTreeHash,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO run_revisions
                (run_id, revision_number, reviewer_user, created_at, raw_comment, sanitized_comment, previous_tree_hash)
            VALUES
                ($runId, $revisionNumber, $reviewerUser, $createdAt, $rawComment, $sanitizedComment, $previousTreeHash);
            """;
        command.Parameters.AddWithValue("$runId", runId.ToString());
        command.Parameters.AddWithValue("$revisionNumber", revisionNumber);
        command.Parameters.AddWithValue("$reviewerUser", reviewerUser);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rawComment", rawComment);
        command.Parameters.AddWithValue("$sanitizedComment", sanitizedComment);
        command.Parameters.AddWithValue("$previousTreeHash", previousTreeHash);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the current maximum revision number for the given run, or 0 if no
    /// revisions have been recorded. Used by the soft-cap check before a new revision
    /// is started (Runs:MaxRevisions configuration key, default 10).
    /// </summary>
    public async Task<int> GetMaxRevisionNumberAsync(RunId runId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(revision_number), 0) FROM run_revisions WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId.ToString());
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }
}
