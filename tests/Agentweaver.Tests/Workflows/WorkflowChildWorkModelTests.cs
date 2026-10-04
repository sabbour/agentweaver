using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Workflows;

public sealed class WorkflowChildWorkModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelationAndBranchIndexes_AreUniqueAndFiltered_ForBothProviders(bool postgres)
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>();
        if (postgres)
            options.UseNpgsql("Host=localhost;Database=model;Username=model;Password=model");
        else
            options.UseSqlite("Data Source=:memory:");

        using var db = new MemoryDbContext(options.Options);
        var workPlan = db.Model.FindEntityType(typeof(WorkPlan))!;
        var correlation = workPlan.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(WorkPlan.ParentRunId), nameof(WorkPlan.ParentWorkflowNodeId) }));
        correlation.IsUnique.Should().BeTrue();
        correlation.GetFilter().Should().Contain("\"ParentRunId\" IS NOT NULL");
        correlation.GetFilter().Should().Contain("\"ParentWorkflowNodeId\" IS NOT NULL");

        var subtask = db.Model.FindEntityType(typeof(Subtask))!;
        subtask.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Subtask.WorkPlanId), nameof(Subtask.WorkflowBranchNodeId) }))
            .IsUnique.Should().BeTrue();
        subtask.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(Subtask.WorkPlanId), nameof(Subtask.WorkflowBranchOrdinal) }))
            .IsUnique.Should().BeTrue();
    }

    [Fact]
    public void ProviderMigrations_ContainDurableCorrelationIndex()
    {
        var root = RepositoryRoot();
        var sqlite = Directory.GetFiles(
            Path.Combine(root, "apps", "Agentweaver.Api", "Migrations"),
            "*_AddWorkflowChildWorkCorrelation.cs").Single();
        var postgres = Directory.GetFiles(
            Path.Combine(root, "apps", "Agentweaver.Api.Migrations.Postgres", "Migrations"),
            "*_AddWorkflowChildWorkCorrelation.cs").Single();

        foreach (var migration in new[] { sqlite, postgres })
        {
            var source = File.ReadAllText(migration);
            source.Should().Contain("IX_WorkPlans_ParentRunId_ParentWorkflowNodeId");
            source.Should().Contain("unique: true");
            source.Should().Contain("\\\"ParentRunId\\\" IS NOT NULL AND \\\"ParentWorkflowNodeId\\\" IS NOT NULL");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunEventIdentity_IsUniquePerRun_ForBothProviders(bool postgres)
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>();
        if (postgres)
            options.UseNpgsql("Host=localhost;Database=model;Username=model;******");
        else
            options.UseSqlite("Data Source=:memory:");

        using var db = new MemoryDbContext(options.Options);
        var runEvent = db.Model.FindEntityType(typeof(RunEventRecord))!;
        var identity = runEvent.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(RunEventRecord.RunId), nameof(RunEventRecord.EventIdentity) }));
        identity.IsUnique.Should().BeTrue();
        identity.GetFilter().Should().Contain("\"EventIdentity\" IS NOT NULL");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "agentweaver.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
