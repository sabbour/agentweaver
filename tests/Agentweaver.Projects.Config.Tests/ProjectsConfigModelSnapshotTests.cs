using Agentweaver.Projects.Config;
using Agentweaver.Projects.Config.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigModelSnapshotTests
{
    [Fact]
    public void LatestMigrationTargetMatchesCurrentSnapshot()
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql("Host=localhost;Database=projects_config_model")
            .Options;
        using var db = new ProjectsConfigDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges());

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        var target = new AddProjectSkillTrustEvidence().TargetModel;
        var differ = db.GetService<IMigrationsModelDiffer>();
        var runtimeInitializer = db.GetService<IModelRuntimeInitializer>();
        snapshot = runtimeInitializer.Initialize(snapshot, designTime: true);
        target = runtimeInitializer.Initialize(target, designTime: true);

        Assert.False(differ.HasDifferences(target.GetRelationalModel(), snapshot.GetRelationalModel()));
    }
}
