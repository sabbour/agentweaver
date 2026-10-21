using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Reports whether Source Control pull request records exist and when their coverage starts.
/// </summary>
/// <remarks>
/// Migration 032 adds the record table. Startup accepts a schema without it, so the record writer
/// skips the write and the pull request list returns <c>pull_request_records_unavailable</c> until
/// the migration is applied. Coverage starts at the migration's <c>applied_at</c> time; pull requests
/// from before then were never recorded.
/// </remarks>
internal static class SourceControlPullRequestRecords
{
    /// <summary>The coordination schema version that adds pull request records.</summary>
    public const int MigrationVersion = 32;

    /// <summary>The error code returned when the record table isn't available yet.</summary>
    public const string UnavailableCode = "pull_request_records_unavailable";

    /// <summary>
    /// Reads when record coverage started, or null when migration 032 isn't applied.
    /// </summary>
    /// <param name="connection">An open connection to the coordination owner database.</param>
    /// <param name="quotedSchema">The validated, quoted coordination schema name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<DateTimeOffset?> ReadCoverageStartAsync(
        NpgsqlConnection connection,
        string quotedSchema,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT applied_at
            FROM {quotedSchema}.coordination_schema_migrations
            WHERE version = {MigrationVersion} AND to_regclass(@records) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue(
            "records", NpgsqlDbType.Text, $"{quotedSchema}.source_control_pull_requests");
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is DateTime applied
            ? new DateTimeOffset(DateTime.SpecifyKind(applied, DateTimeKind.Utc))
            : value as DateTimeOffset?;
    }
}
