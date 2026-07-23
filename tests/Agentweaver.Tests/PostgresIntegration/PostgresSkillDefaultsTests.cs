using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>Docker-backed parity and race coverage for guarded skill-default materialization.</summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class PostgresSkillDefaultsTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task ApplyDefaults_ConcurrentSameDigest_HasOneWinnerAndNoPartialRows()
    {
        var store = new EfSkillStore(pg.Factory);
        var project = ProjectId.New();
        await InsertProjectAsync(project);
        var skill = NewBuiltIn(project, "concurrent-default");
        var plan = new SkillDefaultsStorePlan(
            project,
            0,
            SkillCatalogStateFingerprint.Compute([], []),
            [skill],
            [],
            [new SkillAssignment
            {
                ProjectId = project,
                SkillId = skill.Id,
                AgentName = "Tank",
                CreatedAt = DateTimeOffset.UtcNow,
            }]);

        var results = await Task.WhenAll(
            store.ApplyDefaultsAsync(plan),
            store.ApplyDefaultsAsync(plan));

        results.Count(result => result == SkillDefaultsStoreApplyResult.Applied).Should().Be(1);
        results.Count(result => result == SkillDefaultsStoreApplyResult.Stale).Should().Be(1);
        (await store.ListByProjectAsync(project)).Should().ContainSingle(s => s.Id == skill.Id);
        (await store.ListAssignmentsByProjectAsync(project))
            .Should().ContainSingle(a => a.SkillId == skill.Id && a.AgentName == "Tank");
    }

    [PostgresFact]
    public async Task ApplyDefaults_UnrelatedUniqueViolationSurfaces()
    {
        var store = new EfSkillStore(pg.Factory);
        var project = ProjectId.New();
        await InsertProjectAsync(project);
        var first = NewBuiltIn(project, "same-name");
        var second = NewBuiltIn(project, "same-name");
        var plan = new SkillDefaultsStorePlan(
            project,
            0,
            SkillCatalogStateFingerprint.Compute([], []),
            [first, second],
            [],
            []);

        var act = () => store.ApplyDefaultsAsync(plan);

        var exception = await act.Should().ThrowAsync<Exception>();
        exception.Which.ToString().Should().Contain("23505");
        (await store.ListByProjectAsync(project)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task ApplyDefaults_UnrelatedCheckViolationSurfaces()
    {
        const string constraint = "ck_role_skill_defaults_name";
        await ExecuteAsync($"ALTER TABLE skills ADD CONSTRAINT {constraint} CHECK (name <> 'constraint-check');");
        try
        {
            var store = new EfSkillStore(pg.Factory);
            var project = ProjectId.New();
            await InsertProjectAsync(project);
            var plan = new SkillDefaultsStorePlan(
                project,
                0,
                SkillCatalogStateFingerprint.Compute([], []),
                [NewBuiltIn(project, "constraint-check")],
                [],
                []);

            var act = () => store.ApplyDefaultsAsync(plan);

            var exception = await act.Should().ThrowAsync<Exception>();
            exception.Which.ToString().Should().Contain("23514");
            (await store.ListByProjectAsync(project)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync($"ALTER TABLE skills DROP CONSTRAINT IF EXISTS {constraint};");
        }
    }

    [PostgresFact]
    public async Task ApplyDefaults_UnrelatedForeignKeyViolationSurfaces()
    {
        const string constraint = "fk_role_skill_defaults_assignment";
        await ExecuteAsync(
            $"ALTER TABLE skill_assignments ADD CONSTRAINT {constraint} FOREIGN KEY (skill_id) REFERENCES skills(skill_id) NOT VALID;");
        try
        {
            var store = new EfSkillStore(pg.Factory);
            var project = ProjectId.New();
            await InsertProjectAsync(project);
            var missing = SkillId.New();
            var plan = new SkillDefaultsStorePlan(
                project,
                0,
                SkillCatalogStateFingerprint.Compute([], []),
                [],
                [],
                [new SkillAssignment
                {
                    ProjectId = project,
                    SkillId = missing,
                    AgentName = "Tank",
                    CreatedAt = DateTimeOffset.UtcNow,
                }]);

            var act = () => store.ApplyDefaultsAsync(plan);

            var exception = await act.Should().ThrowAsync<Exception>();
            exception.Which.ToString().Should().Contain("23503");
            (await store.ListAssignmentsByProjectAsync(project)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync($"ALTER TABLE skill_assignments DROP CONSTRAINT IF EXISTS {constraint};");
        }
    }

    [PostgresFact]
    public async Task ApplyDefaults_SharedContentAndProductSkillsUseOneCatalogIdentity()
    {
        foreach (var testCase in new[]
        {
            (
                BlueprintId: "blueprint-content-authoring",
                SkillName: "writing-editing-fact-checking",
                Roles: new[] { "writer", "editor" }),
            (
                BlueprintId: "blueprint-product-management",
                SkillName: "prototype-ux",
                Roles: new[] { "prototype-designer", "ux-designer" }),
        })
        {
            var store = new EfSkillStore(pg.Factory);
            var project = NewProject(ProjectId.New());
            await new EfProjectStore(pg.Factory).InsertAsync(project);
            var blueprint = new CatalogReader().LoadBlueprint(testCase.BlueprintId)!;
            var members = blueprint.Roster
                .Select((roleId, index) => Member($"Agent-{index}", roleId))
                .ToList();
            var team = new Team(project.Name, "test", members);
            var preview = await new SkillDefaultsService(store, null!)
                .PreviewAsync(project, blueprint, team);

            preview.CanApply.Should().BeTrue(string.Join("; ", preview.Errors));
            (await store.ApplyDefaultsAsync(preview.StorePlan!))
                .Should().Be(SkillDefaultsStoreApplyResult.Applied);

            var skills = await store.ListByProjectAsync(project.Id);
            var assignments = await store.ListAssignmentsByProjectAsync(project.Id);
            skills.Should().ContainSingle(skill => skill.Name == testCase.SkillName);
            var sharedSkill = skills.Single(skill => skill.Name == testCase.SkillName);
            var expectedAgents = members
                .Where(member => testCase.Roles.Contains(member.Role.Id))
                .Select(member => member.Name)
                .ToHashSet(StringComparer.Ordinal);
            assignments
                .Where(assignment => assignment.SkillId == sharedSkill.Id)
                .Select(assignment => assignment.AgentName)
                .Should().BeEquivalentTo(expectedAgents);
            assignments.Should().OnlyContain(
                assignment => skills.Any(skill => skill.Id == assignment.SkillId));
        }
    }

    [PostgresFact]
    public async Task ApplyDefaults_TeamMutationCommittedAfterPreview_IsStaleWithoutPartialWrites()
    {
        var skillStore = new EfSkillStore(pg.Factory);
        var projectStore = new EfProjectStore(pg.Factory);
        var project = NewProject(ProjectId.New());
        await projectStore.InsertAsync(project);
        var planned = NewBuiltIn(project.Id, "system-design");
        var plan = new SkillDefaultsStorePlan(
            project.Id,
            project.TeamRevision,
            SkillCatalogStateFingerprint.Compute([], []),
            [planned],
            [],
            [new SkillAssignment
            {
                ProjectId = project.Id,
                SkillId = planned.Id,
                AgentName = "Tank",
                CreatedAt = DateTimeOffset.UtcNow,
            }]);

        await using var mutation = await projectStore.TryBeginTeamMutationAsync(
            project.Id,
            project.TeamRevision);
        mutation.Should().NotBeNull();
        var applyTask = Task.Run(() => skillStore.ApplyDefaultsAsync(plan));
        await Task.Delay(50);
        await mutation!.CompleteAsync(CancellationToken.None);

        (await applyTask).Should().Be(SkillDefaultsStoreApplyResult.Stale);
        (await skillStore.ListByProjectAsync(project.Id)).Should().BeEmpty();
        (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().BeEmpty();
        (await projectStore.GetAsync(project.Id))!.TeamRevision.Should().Be(1);
    }

    [PostgresFact]
    public async Task DeleteProject_CascadesOnlyTargetProjectSkillState()
    {
        var store = new EfSkillStore(pg.Factory);
        var projectStore = new EfProjectStore(pg.Factory);
        var first = ProjectId.New();
        var second = ProjectId.New();
        await InsertProjectAsync(first);
        await InsertProjectAsync(second);
        var firstSkill = NewBuiltIn(first, "first-project-skill");
        var secondSkill = NewBuiltIn(second, "second-project-skill");
        await store.InsertAsync(firstSkill);
        await store.InsertAsync(secondSkill);
        await store.AssignAsync(first, firstSkill.Id, "Tank", DateTimeOffset.UtcNow);
        await store.AssignAsync(second, secondSkill.Id, "Trinity", DateTimeOffset.UtcNow);

        await projectStore.DeleteAsync(first);

        (await store.ListByProjectAsync(first)).Should().BeEmpty();
        (await store.ListAssignmentsByProjectAsync(first)).Should().BeEmpty();
        (await store.ListByProjectAsync(second)).Should().ContainSingle(skill => skill.Id == secondSkill.Id);
        (await store.ListAssignmentsByProjectAsync(second))
            .Should().ContainSingle(assignment => assignment.SkillId == secondSkill.Id);
    }

    [PostgresFact]
    public async Task ConcurrentDefaultsApplyAndProjectDelete_LeavesNoSkillState()
    {
        var skillStore = new EfSkillStore(pg.Factory);
        var projectStore = new EfProjectStore(pg.Factory);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var project = NewProject(ProjectId.New());
            await projectStore.InsertAsync(project);
            var skill = NewBuiltIn(project.Id, $"postgres-delete-race-{iteration}");
            var plan = new SkillDefaultsStorePlan(
                project.Id,
                project.TeamRevision,
                SkillCatalogStateFingerprint.Compute([], []),
                [skill],
                [],
                [new SkillAssignment
                {
                    ProjectId = project.Id,
                    SkillId = skill.Id,
                    AgentName = "Tank",
                    CreatedAt = DateTimeOffset.UtcNow,
                }]);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var apply = Task.Run(async () =>
            {
                await start.Task;
                return await skillStore.ApplyDefaultsAsync(plan);
            });
            var delete = Task.Run(async () =>
            {
                await start.Task;
                await projectStore.DeleteAsync(project.Id);
            });

            start.SetResult();
            await Task.WhenAll(apply, delete);

            (await projectStore.GetAsync(project.Id)).Should().BeNull();
            (await skillStore.ListByProjectAsync(project.Id)).Should().BeEmpty();
            (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().BeEmpty();
        }
    }

    [PostgresFact]
    public async Task Migration_UpgradeCleansOrphansBeforeAddingOwnershipCascades()
    {
        var schema = $"skill_upgrade_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE SCHEMA \"{schema}\";");
        try
        {
            var connectionBuilder = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
            {
                SearchPath = schema,
            };
            var services = new ServiceCollection();
            services.AddDbContextFactory<MemoryDbContext>(options =>
                options.UseNpgsql(
                    connectionBuilder.ConnectionString,
                    postgres => postgres.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
            using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
            await using (var before = await factory.CreateDbContextAsync())
            {
                await before.GetService<IMigrator>()
                    .MigrateAsync("20260716213000_AddProjectTeamRevision");
            }
            var project = NewProject(ProjectId.New());
            var projectStore = new EfProjectStore(factory);
            var skillStore = new EfSkillStore(factory);
            // This fixture deliberately targets the historical schema before the current webhook
            // migration, so insert the project through SQL rather than using today's EF model.
            await using (var connection = new NpgsqlConnection(connectionBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO projects (
                        project_id, name, origin_kind, working_directory, default_branch, owner,
                        default_provider, state, created_at, updated_at, team_revision)
                    VALUES (@projectId, @name, 'blank', @workingDirectory, 'main', @owner,
                        'github-copilot', 'active', @createdAt, @updatedAt, 0);
                    """;
                command.Parameters.AddWithValue("projectId", project.Id.ToString());
                command.Parameters.AddWithValue("name", project.Name);
                command.Parameters.AddWithValue("workingDirectory", project.WorkingDirectory);
                command.Parameters.AddWithValue("owner", project.Owner);
                command.Parameters.AddWithValue("createdAt", project.CreatedAt);
                command.Parameters.AddWithValue("updatedAt", project.UpdatedAt);
                await command.ExecuteNonQueryAsync();
            }
            var validSkill = NewBuiltIn(project.Id, "valid-upgrade-skill");
            // This intentionally targets the schema before the marketplace-provenance migration.
            // Insert the valid fixture through SQL so the current EF model does not require the
            // pending marketplace_name column before Database.MigrateAsync below applies it.
            await using (var connection = new NpgsqlConnection(connectionBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO skills (
                        skill_id, project_id, name, description, instructions, provenance,
                        content_hash, status, created_at, updated_at)
                    VALUES (@validSkill, @validProject, 'valid-upgrade-skill', 'valid',
                        'instructions', 'built-in', 'hash', 'active', now(), now());
                    INSERT INTO skill_assignments (project_id, skill_id, agent_name, created_at)
                    VALUES (@validProject, @validSkill, 'Tank', now());
                    INSERT INTO skills (
                        skill_id, project_id, name, description, instructions, provenance,
                        content_hash, status, created_at, updated_at)
                    VALUES (
                        @orphanSkill, @orphanProject, 'orphan-upgrade-skill', 'orphan',
                        'instructions', 'built-in', 'hash', 'active', now(), now());
                    INSERT INTO skill_assignments (project_id, skill_id, agent_name, created_at)
                    VALUES (@orphanProject, @orphanSkill, 'Smith', now());
                    INSERT INTO skill_assignments (project_id, skill_id, agent_name, created_at)
                    VALUES (@validProject, @missingSkill, 'Trinity', now());
                    """;
                command.Parameters.AddWithValue("validSkill", validSkill.Id.ToString());
                command.Parameters.AddWithValue("orphanSkill", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("orphanProject", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("validProject", project.Id.ToString());
                command.Parameters.AddWithValue("missingSkill", Guid.NewGuid().ToString());
                await command.ExecuteNonQueryAsync();
            }

            await using (var after = await factory.CreateDbContextAsync())
                await after.Database.MigrateAsync();

            (await skillStore.ListByProjectAsync(project.Id)).Should().ContainSingle();
            (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().ContainSingle();
            await using (var connection = new NpgsqlConnection(connectionBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT COUNT(*)
                      FROM pg_constraint
                     WHERE connamespace = current_schema()::regnamespace
                       AND conname IN (
                           'FK_skills_projects_project_id',
                           'FK_skill_assignments_projects_project_id',
                           'FK_skill_assignments_skills_project_id_skill_id');
                    """;
                ((long)(await command.ExecuteScalarAsync())!).Should().Be(3);
            }

            await projectStore.DeleteAsync(project.Id);
            (await skillStore.ListByProjectAsync(project.Id)).Should().BeEmpty();
            (await skillStore.ListAssignmentsByProjectAsync(project.Id)).Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private Task InsertProjectAsync(ProjectId projectId)
    {
        return new EfProjectStore(pg.Factory).InsertAsync(NewProject(projectId));
    }

    private static Project NewProject(ProjectId projectId)
    {
        var now = DateTimeOffset.UtcNow;
        return new Project
        {
            Id = projectId,
            Name = $"Skill defaults {projectId}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = Environment.CurrentDirectory,
            DefaultBranch = "main",
            Owner = "postgres-test",
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = Agentweaver.Domain.ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static CastMember Member(string name, string roleId) => new(
        name,
        new Role(roleId, roleId, "test role", "test", [], [], []),
        "charter.md",
        CastMemberStatus.Active,
        false);

    private static Skill NewBuiltIn(ProjectId project, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new Skill
        {
            Id = SkillId.New(),
            ProjectId = project,
            Name = name,
            Description = name,
            Instructions = "instructions",
            Provenance = SkillProvenance.BuiltIn,
            SourceLocation = $"catalog/skills/{name}",
            ContentHash = SkillParser.ComputeContentHash(name, name, "instructions", []),
            Status = SkillStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
