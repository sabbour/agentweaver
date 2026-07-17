using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Tests.Skills;

public sealed class SkillOwnershipMigrationTests
{
    [Fact]
    public async Task EnsureCreated_UpgradesLegacySkillTablesAndCleansOrphans()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var project = TestProject(ProjectId.New());
        await new SqliteProjectStore(testDb.Db).InsertAsync(project);
        var validSkillId = Guid.NewGuid().ToString();
        var orphanProjectId = Guid.NewGuid().ToString();

        await using (var connection = await testDb.Db.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA foreign_keys=OFF;
                DROP TABLE skill_assignments;
                DROP TABLE skills;
                CREATE TABLE skills (
                    skill_id TEXT PRIMARY KEY,
                    project_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    description TEXT NOT NULL,
                    instructions TEXT NOT NULL,
                    resources TEXT,
                    provenance TEXT NOT NULL,
                    source_repository TEXT,
                    source_location TEXT,
                    content_hash TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'active',
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX idx_skills_project_name
                    ON skills (project_id, name COLLATE NOCASE);
                CREATE TABLE skill_assignments (
                    project_id TEXT NOT NULL,
                    skill_id TEXT NOT NULL,
                    agent_name TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (project_id, skill_id, agent_name)
                );
                CREATE INDEX idx_skill_assignments_agent
                    ON skill_assignments (project_id, agent_name);

                INSERT INTO skills VALUES (
                    $validSkillId, $validProjectId, 'valid', 'valid', 'instructions',
                    NULL, 'built-in', NULL, 'catalog/valid', 'hash', 'active', $now, $now);
                INSERT INTO skill_assignments VALUES (
                    $validProjectId, $validSkillId, 'Tank', $now);
                INSERT INTO skills VALUES (
                    $orphanSkillId, $orphanProjectId, 'orphan', 'orphan', 'instructions',
                    NULL, 'built-in', NULL, 'catalog/orphan', 'hash', 'active', $now, $now);
                INSERT INTO skill_assignments VALUES (
                    $orphanProjectId, $orphanSkillId, 'Smith', $now);
                INSERT INTO skill_assignments VALUES (
                    $validProjectId, $missingSkillId, 'Trinity', $now);
                """;
            command.Parameters.AddWithValue("$validSkillId", validSkillId);
            command.Parameters.AddWithValue("$validProjectId", project.Id.ToString());
            command.Parameters.AddWithValue("$orphanSkillId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$orphanProjectId", orphanProjectId);
            command.Parameters.AddWithValue("$missingSkillId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await testDb.Db.EnsureCreatedAsync();

        var skillStore = new SqliteSkillStore(testDb.Db);
        (await skillStore.ListByProjectAsync(project.Id)).Should().ContainSingle();
        (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().ContainSingle();
        await using var verify = await testDb.Db.OpenConnectionAsync();
        (await ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM skills WHERE project_id = $id;",
            orphanProjectId)).Should().Be(0);
        (await ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM skill_assignments WHERE project_id = $id;",
            orphanProjectId)).Should().Be(0);
        (await ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('idx_skills_project_name', 'idx_skill_assignments_agent');"))
            .Should().Be(2);
        (await ScalarLongAsync(
            verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('trg_run_revisions_no_update', 'trg_run_revisions_no_delete');"))
            .Should().Be(2);
        await using (var check = verify.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check;";
            (await check.ExecuteScalarAsync()).Should().BeNull();
        }

        await new SqliteProjectStore(testDb.Db).DeleteAsync(project.Id);

        (await skillStore.ListByProjectAsync(project.Id)).Should().BeEmpty();
        (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().BeEmpty();

        var insertOrphan = async () =>
        {
            await using var connection = await testDb.Db.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO skills (
                    skill_id, project_id, name, description, instructions, provenance,
                    content_hash, status, created_at, updated_at)
                VALUES ($skillId, $projectId, 'blocked-orphan', 'blocked', 'instructions',
                        'built-in', 'hash', 'active', $now, $now);
                """;
            command.Parameters.AddWithValue("$skillId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$projectId", project.Id.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        };
        await insertOrphan.Should().ThrowAsync<SqliteException>();
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        string? id = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null)
            command.Parameters.AddWithValue("$id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Project TestProject(ProjectId id)
    {
        var now = DateTimeOffset.UtcNow;
        return new Project
        {
            Id = id,
            Name = "Skill migration project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = Environment.CurrentDirectory,
            DefaultBranch = "main",
            Owner = "migration-test",
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
