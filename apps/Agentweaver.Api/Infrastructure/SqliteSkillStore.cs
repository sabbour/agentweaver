using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ISkillStore"/> for the per-project skill catalog and skill→agent
/// assignments. Used when Database:Provider != postgres. Bundled resources are stored as a JSON
/// array on the skill row; assignments live in a child table cascaded on skill delete.
/// </summary>
public sealed class SqliteSkillStore : ISkillStore
{
    private readonly SqliteDb _db;

    public SqliteSkillStore(SqliteDb db) => _db = db;

    private static readonly JsonSerializerOptions JsonOpts = new();

    public async Task InsertAsync(Skill skill, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO skills (skill_id, project_id, name, description, instructions, resources,
                                provenance, source_repository, source_location, content_hash, status,
                                created_at, updated_at)
            VALUES ($skillId, $projectId, $name, $description, $instructions, $resources,
                    $provenance, $sourceRepository, $sourceLocation, $contentHash, $status,
                    $createdAt, $updatedAt);
            """;
        BindSkill(command, skill);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Skill skill, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE skills
               SET name = $name, description = $description, instructions = $instructions,
                   resources = $resources, provenance = $provenance,
                   source_repository = $sourceRepository, source_location = $sourceLocation,
                   content_hash = $contentHash, status = $status, updated_at = $updatedAt
             WHERE project_id = $projectId AND skill_id = $skillId;
            """;
        BindSkill(command, skill);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Skill?> GetAsync(ProjectId projectId, SkillId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId AND skill_id = $skillId;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$skillId", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<Skill?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId AND name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<Skill>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE project_id = $projectId ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var results = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<bool> DeleteAsync(ProjectId projectId, SkillId id, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var del = connection.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM skill_assignments WHERE project_id = $projectId AND skill_id = $skillId;";
            del.Parameters.AddWithValue("$projectId", projectId.ToString());
            del.Parameters.AddWithValue("$skillId", id.ToString());
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        int rows;
        await using (var del = connection.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM skills WHERE project_id = $projectId AND skill_id = $skillId;";
            del.Parameters.AddWithValue("$projectId", projectId.ToString());
            del.Parameters.AddWithValue("$skillId", id.ToString());
            rows = await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task AssignAsync(ProjectId projectId, SkillId skillId, string agentName, DateTimeOffset createdAt, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO skill_assignments (project_id, skill_id, agent_name, created_at)
            VALUES ($projectId, $skillId, $agentName, $createdAt)
            ON CONFLICT (project_id, skill_id, agent_name) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$skillId", skillId.ToString());
        command.Parameters.AddWithValue("$agentName", agentName);
        command.Parameters.AddWithValue("$createdAt", Ts(createdAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UnassignAsync(ProjectId projectId, SkillId skillId, string agentName, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM skill_assignments WHERE project_id = $projectId AND skill_id = $skillId AND agent_name = $agentName;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$skillId", skillId.ToString());
        command.Parameters.AddWithValue("$agentName", agentName);
        var rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rows > 0;
    }

    public async Task<IReadOnlyList<SkillAssignment>> ListAssignmentsByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT project_id, skill_id, agent_name, created_at FROM skill_assignments WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var results = new List<SkillAssignment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new SkillAssignment
            {
                ProjectId = ProjectId.Parse(reader.GetString(0)),
                SkillId = SkillId.Parse(reader.GetString(1)),
                AgentName = reader.GetString(2),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3), null, DateTimeStyles.RoundtripKind),
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<Skill>> ListActiveSkillsForAgentAsync(ProjectId projectId, string agentName, CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.skill_id, s.project_id, s.name, s.description, s.instructions, s.resources,
                   s.provenance, s.source_repository, s.source_location, s.content_hash, s.status,
                   s.created_at, s.updated_at
              FROM skills AS s
              INNER JOIN skill_assignments a
                 ON a.project_id = s.project_id AND a.skill_id = s.skill_id
             WHERE s.project_id = $projectId AND a.agent_name = $agentName AND s.status = 'active'
             ORDER BY s.name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$agentName", agentName);
        var results = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(Map(reader));
        return results;
    }

    public async Task<SkillDefaultsStoreApplyResult> ApplyDefaultsAsync(
        SkillDefaultsStorePlan plan,
        CancellationToken ct = default)
    {
        await using var connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct)
            .ConfigureAwait(false);

        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText =
                """
                UPDATE projects
                   SET team_revision = team_revision
                 WHERE project_id = $projectId
                   AND state = 'active'
                   AND team_revision = $expectedTeamRevision;
                """;
            guard.Parameters.AddWithValue("$projectId", plan.ProjectId.ToString());
            guard.Parameters.AddWithValue("$expectedTeamRevision", plan.ExpectedTeamRevision);
            if (await guard.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return SkillDefaultsStoreApplyResult.Stale;
            }
        }

        var currentSkills = await ReadSkillsAsync(connection, transaction, plan.ProjectId, ct).ConfigureAwait(false);
        var currentAssignments = await ReadAssignmentsAsync(connection, transaction, plan.ProjectId, ct).ConfigureAwait(false);
        if (!string.Equals(
                SkillCatalogStateFingerprint.Compute(currentSkills, currentAssignments),
                plan.ExpectedStateFingerprint,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return SkillDefaultsStoreApplyResult.Stale;
        }

        foreach (var skill in plan.SkillsToInsert)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO skills (skill_id, project_id, name, description, instructions, resources,
                                    provenance, source_repository, source_location, content_hash, status,
                                    created_at, updated_at)
                VALUES ($skillId, $projectId, $name, $description, $instructions, $resources,
                        $provenance, $sourceRepository, $sourceLocation, $contentHash, $status,
                        $createdAt, $updatedAt);
                """;
            BindSkill(command, skill);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var skill in plan.SkillsToActivate)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE skills
                   SET status = $status, updated_at = $updatedAt
                 WHERE project_id = $projectId AND skill_id = $skillId;
                """;
            command.Parameters.AddWithValue("$status", skill.Status.ToApiString());
            command.Parameters.AddWithValue("$updatedAt", Ts(skill.UpdatedAt));
            command.Parameters.AddWithValue("$projectId", skill.ProjectId.ToString());
            command.Parameters.AddWithValue("$skillId", skill.Id.ToString());
            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("A guarded built-in skill was not found during apply.");
        }

        foreach (var assignment in plan.AssignmentsToAdd)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO skill_assignments (project_id, skill_id, agent_name, created_at)
                VALUES ($projectId, $skillId, $agentName, $createdAt)
                ON CONFLICT (project_id, skill_id, agent_name) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$projectId", assignment.ProjectId.ToString());
            command.Parameters.AddWithValue("$skillId", assignment.SkillId.ToString());
            command.Parameters.AddWithValue("$agentName", assignment.AgentName);
            command.Parameters.AddWithValue("$createdAt", Ts(assignment.CreatedAt));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return SkillDefaultsStoreApplyResult.Applied;
    }

    private static async Task<IReadOnlyList<Skill>> ReadSkillsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE project_id = $projectId ORDER BY name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var skills = new List<Skill>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            skills.Add(Map(reader));
        return skills;
    }

    private static async Task<IReadOnlyList<SkillAssignment>> ReadAssignmentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT project_id, skill_id, agent_name, created_at FROM skill_assignments WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var assignments = new List<SkillAssignment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            assignments.Add(new SkillAssignment
            {
                ProjectId = ProjectId.Parse(reader.GetString(0)),
                SkillId = SkillId.Parse(reader.GetString(1)),
                AgentName = reader.GetString(2),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3), null, DateTimeStyles.RoundtripKind),
            });
        }
        return assignments;
    }

    private static void BindSkill(SqliteCommand command, Skill skill)
    {
        command.Parameters.AddWithValue("$skillId", skill.Id.ToString());
        command.Parameters.AddWithValue("$projectId", skill.ProjectId.ToString());
        command.Parameters.AddWithValue("$name", skill.Name);
        command.Parameters.AddWithValue("$description", skill.Description);
        command.Parameters.AddWithValue("$instructions", skill.Instructions);
        command.Parameters.AddWithValue("$resources", SerializeResources(skill.Resources));
        command.Parameters.AddWithValue("$provenance", skill.Provenance.ToApiString());
        command.Parameters.AddWithValue("$sourceRepository", (object?)skill.SourceRepository ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceLocation", (object?)skill.SourceLocation ?? DBNull.Value);
        command.Parameters.AddWithValue("$contentHash", skill.ContentHash);
        command.Parameters.AddWithValue("$status", skill.Status.ToApiString());
        command.Parameters.AddWithValue("$createdAt", Ts(skill.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", Ts(skill.UpdatedAt));
    }

    // Ordinals: 0=skill_id 1=project_id 2=name 3=description 4=instructions 5=resources
    //           6=provenance 7=source_repository 8=source_location 9=content_hash 10=status
    //           11=created_at 12=updated_at
    private const string SelectSql =
        """
        SELECT skill_id, project_id, name, description, instructions, resources,
               provenance, source_repository, source_location, content_hash, status,
               created_at, updated_at
          FROM skills
        """;

    private static Skill Map(SqliteDataReader r) => new()
    {
        Id = SkillId.Parse(r.GetString(0)),
        ProjectId = ProjectId.Parse(r.GetString(1)),
        Name = r.GetString(2),
        Description = r.GetString(3),
        Instructions = r.GetString(4),
        Resources = DeserializeResources(r.IsDBNull(5) ? null : r.GetString(5)),
        Provenance = SkillProvenanceExtensions.ParseProvenance(r.GetString(6)),
        SourceRepository = r.IsDBNull(7) ? null : r.GetString(7),
        SourceLocation = r.IsDBNull(8) ? null : r.GetString(8),
        ContentHash = r.GetString(9),
        Status = SkillStatusExtensions.ParseStatus(r.GetString(10)),
        CreatedAt = DateTimeOffset.Parse(r.GetString(11), null, DateTimeStyles.RoundtripKind),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(12), null, DateTimeStyles.RoundtripKind),
    };

    private static string SerializeResources(IReadOnlyList<SkillResource> resources) =>
        JsonSerializer.Serialize(resources, JsonOpts);

    private static IReadOnlyList<SkillResource> DeserializeResources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<SkillResource>();
        return JsonSerializer.Deserialize<List<SkillResource>>(json, JsonOpts) ?? new List<SkillResource>();
    }

    private static string Ts(DateTimeOffset v) => v.ToString("O", CultureInfo.InvariantCulture);
}
