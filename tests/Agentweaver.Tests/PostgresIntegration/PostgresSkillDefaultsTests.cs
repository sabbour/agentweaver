using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using Agentweaver.Domain.Skills;
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
        var skill = NewBuiltIn(project, "concurrent-default");
        var plan = new SkillDefaultsStorePlan(
            project,
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
        var first = NewBuiltIn(project, "same-name");
        var second = NewBuiltIn(project, "same-name");
        var plan = new SkillDefaultsStorePlan(
            project,
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
            var plan = new SkillDefaultsStorePlan(
                project,
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
            var missing = SkillId.New();
            var plan = new SkillDefaultsStorePlan(
                project,
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

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
