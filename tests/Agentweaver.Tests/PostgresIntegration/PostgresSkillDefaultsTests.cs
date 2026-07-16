using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using FluentAssertions;
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
    public async Task DeleteProjectSkillState_RemovesOnlyTargetProject()
    {
        var store = new EfSkillStore(pg.Factory);
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

        await store.DeleteProjectSkillStateAsync(first);

        (await store.ListByProjectAsync(first)).Should().BeEmpty();
        (await store.ListAssignmentsByProjectAsync(first)).Should().BeEmpty();
        (await store.ListByProjectAsync(second)).Should().ContainSingle(skill => skill.Id == secondSkill.Id);
        (await store.ListAssignmentsByProjectAsync(second))
            .Should().ContainSingle(assignment => assignment.SkillId == secondSkill.Id);
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
                DefaultProvider = ModelSource.GitHubCopilot,
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
