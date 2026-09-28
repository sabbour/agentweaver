using Agentweaver.Api.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Tests.Api;

public sealed class RunOutputRevisionMigrationTests
{
    [Fact]
    public void PostgresMigrationCreatesContentTableAndImmutableTrigger()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=design;Username=design;Password=design",
                npg => npg.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres"))
            .Options;
        using var db = new MemoryDbContext(options);
        db.Database.GetMigrations().Should().Contain("20260927171809_AddRunOutputRevisionsPostgres");
        var script = db.GetService<IMigrator>().GenerateScript(
            "20260926182628_AddCoordinatorMergeEffectsPostgres",
            "20260927171809_AddRunOutputRevisionsPostgres");
        script.Should().Contain("CREATE TABLE run_output_revisions");
        script.Should().Contain("CREATE UNIQUE INDEX");
        script.Should().Contain("reject_run_output_revision_mutation");
        script.Should().Contain("ADD approved_output_revision_id text");
        script.Should().Contain("ADD current_output_revision_id text");
        var collectiveMigration = db.Database.GetMigrations()
            .Single(migration => migration.EndsWith("_AddCollectiveOutputRevisions", StringComparison.Ordinal));
        var collectiveScript = db.GetService<IMigrator>().GenerateScript(
            "20260927171809_AddRunOutputRevisionsPostgres", collectiveMigration);
        collectiveScript.Should().Contain("ADD output_kind text");
        collectiveScript.Should().Contain("ADD merged_commit_hash text");
        collectiveScript.Should().Contain("ADD merge_effect_id text");
        collectiveScript.Should().Contain("ADD accepted_no_change boolean");
        collectiveScript.Should().Contain("ADD tree_content bytea");
        collectiveScript.Should().Contain("ADD tree_content_sha256 text");
        db.Model.FindEntityType(typeof(RunOutputRevisionRecord))!.GetTableName()
            .Should().Be("run_output_revisions");
    }
}
