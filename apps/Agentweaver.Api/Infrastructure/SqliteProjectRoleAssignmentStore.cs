using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

public sealed class SqliteProjectRoleAssignmentStore(SqliteDb db) : IProjectRoleAssignmentStore
{
    public async Task UpsertAsync(ProjectRoleAssignment assignment, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO project_role_assignments (project_id, principal_id, role, granted_by, granted_at)
            VALUES ($projectId, $principalId, $role, $grantedBy, $grantedAt)
            ON CONFLICT(project_id, principal_id) DO UPDATE SET
                role = excluded.role,
                granted_by = excluded.granted_by,
                granted_at = excluded.granted_at;
            """;
        command.Parameters.AddWithValue("$projectId", assignment.ProjectId.ToString());
        command.Parameters.AddWithValue("$principalId", assignment.PrincipalId);
        command.Parameters.AddWithValue("$role", assignment.Role.ToApiString());
        command.Parameters.AddWithValue("$grantedBy", assignment.GrantedBy);
        command.Parameters.AddWithValue("$grantedAt", Ts(assignment.GrantedAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(
        ProjectRoleAssignment assignment,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await BeginImmediateAsync(connection, ct).ConfigureAwait(false);
        try
        {
            var existingRole = await GetRoleAsync(connection, assignment.ProjectId, assignment.PrincipalId, ct).ConfigureAwait(false);
            if (existingRole is ProjectRole.Owner && assignment.Role != ProjectRole.Owner)
            {
                var otherOwners = await CountOtherOwnersAsync(connection, assignment.ProjectId, assignment.PrincipalId, ct).ConfigureAwait(false);
                if (otherOwners == 0)
                {
                    await RollbackAsync(connection, ct).ConfigureAwait(false);
                    return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict);
                }
            }

            await UpsertWithinConnectionAsync(connection, assignment, ct).ConfigureAwait(false);
            await CommitAsync(connection, ct).ConfigureAwait(false);
            return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.Ok, assignment);
        }
        catch
        {
            await RollbackQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ProjectRoleAssignment?> GetAsync(ProjectId projectId, string principalId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_id, principal_id, role, granted_by, granted_at
              FROM project_role_assignments
             WHERE project_id = $projectId
               AND principal_id = $principalId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$principalId", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_id, principal_id, role, granted_by, granted_at
              FROM project_role_assignments
             WHERE project_id = $projectId
             ORDER BY role DESC, principal_id ASC;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        var assignments = new List<ProjectRoleAssignment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            assignments.Add(Map(reader));
        return assignments;
    }

    public async Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string principalId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_id, principal_id, role, granted_by, granted_at
              FROM project_role_assignments
             WHERE principal_id = $principalId;
            """;
        command.Parameters.AddWithValue("$principalId", principalId);
        var assignments = new List<ProjectRoleAssignment>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            assignments.Add(Map(reader));
        return assignments;
    }

    public async Task<bool> DeleteAsync(ProjectId projectId, string principalId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM project_role_assignments
             WHERE project_id = $projectId
               AND principal_id = $principalId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$principalId", principalId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(
        ProjectId projectId,
        string principalId,
        CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await BeginImmediateAsync(connection, ct).ConfigureAwait(false);
        try
        {
            var existing = await GetAsyncWithinConnection(connection, projectId, principalId, ct).ConfigureAwait(false);
            if (existing is null)
            {
                await RollbackAsync(connection, ct).ConfigureAwait(false);
                return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.NotFound);
            }

            if (existing.Role == ProjectRole.Owner)
            {
                var otherOwners = await CountOtherOwnersAsync(connection, projectId, principalId, ct).ConfigureAwait(false);
                if (otherOwners == 0)
                {
                    await RollbackAsync(connection, ct).ConfigureAwait(false);
                    return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.LastOwnerConflict, existing);
                }
            }

            await DeleteWithinConnectionAsync(connection, projectId, principalId, ct).ConfigureAwait(false);
            await CommitAsync(connection, ct).ConfigureAwait(false);
            return new ProjectRoleAssignmentStoreMutationResult(ProjectRoleAssignmentStoreMutationStatus.Ok, existing);
        }
        catch
        {
            await RollbackQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private static ProjectRoleAssignment Map(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        ProjectId = ProjectId.Parse(reader.GetString(0)),
        PrincipalId = reader.GetString(1),
        Role = ProjectRoleExtensions.Parse(reader.GetString(2)),
        GrantedBy = reader.GetString(3),
        GrantedAt = DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private static string Ts(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static async Task BeginImmediateAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task CommitAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "COMMIT;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RollbackAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "ROLLBACK;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RollbackQuietlyAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "ROLLBACK;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async Task<ProjectRole?> GetRoleAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ProjectId projectId,
        string principalId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT role
              FROM project_role_assignments
             WHERE project_id = $projectId
               AND principal_id = $principalId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$principalId", principalId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return value is null ? null : ProjectRoleExtensions.Parse(value);
    }

    private static async Task<int> CountOtherOwnersAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ProjectId projectId,
        string principalId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
              FROM project_role_assignments
             WHERE project_id = $projectId
               AND role = 'Owner'
               AND principal_id <> $principalId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$principalId", principalId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<ProjectRoleAssignment?> GetAsyncWithinConnection(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ProjectId projectId,
        string principalId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT project_id, principal_id, role, granted_by, granted_at
              FROM project_role_assignments
             WHERE project_id = $projectId
               AND principal_id = $principalId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$principalId", principalId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    private static async Task UpsertWithinConnectionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ProjectRoleAssignment assignment,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO project_role_assignments (project_id, principal_id, role, granted_by, granted_at)
            VALUES ($projectId, $principalId, $role, $grantedBy, $grantedAt)
            ON CONFLICT(project_id, principal_id) DO UPDATE SET
                role = excluded.role,
                granted_by = excluded.granted_by,
                granted_at = excluded.granted_at;
            """;
        command.Parameters.AddWithValue("$projectId", assignment.ProjectId.ToString());
        command.Parameters.AddWithValue("$principalId", assignment.PrincipalId);
        command.Parameters.AddWithValue("$role", assignment.Role.ToApiString());
        command.Parameters.AddWithValue("$grantedBy", assignment.GrantedBy);
        command.Parameters.AddWithValue("$grantedAt", Ts(assignment.GrantedAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteWithinConnectionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ProjectId projectId,
        string principalId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM project_role_assignments
             WHERE project_id = $projectId
               AND principal_id = $principalId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$principalId", principalId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
