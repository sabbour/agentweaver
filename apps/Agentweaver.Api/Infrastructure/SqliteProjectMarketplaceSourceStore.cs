using System.Globalization;
using Microsoft.Data.Sqlite;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="IProjectMarketplaceSourceStore"/> for project-scoped, user-added skill
/// marketplace sources. Used when Database:Provider != postgres (dev/staging). Semantics mirror
/// <see cref="EfProjectMarketplaceSourceStore"/> and follow the existing SQLite store conventions.
/// </summary>
public sealed class SqliteProjectMarketplaceSourceStore : IProjectMarketplaceSourceStore
{
    private readonly SqliteDb _db;

    public SqliteProjectMarketplaceSourceStore(SqliteDb db) => _db = db;

    public async Task<IReadOnlyList<ProjectMarketplaceSource>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var results = new List<ProjectMarketplaceSource>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<ProjectMarketplaceSource?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId AND name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<bool> InsertAsync(ProjectMarketplaceSource source, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO skill_marketplace_sources
                (source_id, project_id, name, repository, branch, subpath, parse_strategy, enabled, created_at, updated_at)
            VALUES
                ($sourceId, $projectId, $name, $repository, $branch, $subpath, $parseStrategy, $enabled, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$sourceId", source.SourceId);
        command.Parameters.AddWithValue("$projectId", source.ProjectId.ToString());
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$repository", source.Repository);
        command.Parameters.AddWithValue("$branch", (object?)source.Branch ?? DBNull.Value);
        command.Parameters.AddWithValue("$subpath", (object?)source.Subpath ?? DBNull.Value);
        command.Parameters.AddWithValue("$parseStrategy", (object?)source.ParseStrategy ?? DBNull.Value);
        command.Parameters.AddWithValue("$enabled", source.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", Ts(source.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Ts(source.UpdatedAt));
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067 || ex.SqliteExtendedErrorCode == 1555)
        {
            // SQLITE_CONSTRAINT_UNIQUE (2067) / _PRIMARYKEY (1555): a source with this (project, name)
            // already exists. Other constraint failures (e.g. FK) are real errors and propagate.
            return false;
        }
    }

    public async Task<bool> DeleteByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM skill_marketplace_sources WHERE project_id = $projectId AND name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$name", name);
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    // Ordinals: 0=source_id 1=project_id 2=name 3=repository 4=branch 5=subpath
    //           6=parse_strategy 7=enabled 8=created_at 9=updated_at
    private const string SelectSql =
        """
        SELECT source_id, project_id, name, repository, branch, subpath, parse_strategy, enabled, created_at, updated_at
          FROM skill_marketplace_sources
        """;

    private static ProjectMarketplaceSource Map(SqliteDataReader r) => new()
    {
        SourceId = r.GetString(0),
        ProjectId = ProjectId.Parse(r.GetString(1)),
        Name = r.GetString(2),
        Repository = r.GetString(3),
        Branch = r.IsDBNull(4) ? null : r.GetString(4),
        Subpath = r.IsDBNull(5) ? null : r.GetString(5),
        ParseStrategy = r.IsDBNull(6) ? null : r.GetString(6),
        Enabled = r.GetInt64(7) != 0,
        CreatedAt = DateTimeOffset.Parse(r.GetString(8), null, DateTimeStyles.RoundtripKind),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(9), null, DateTimeStyles.RoundtripKind),
    };

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
}
