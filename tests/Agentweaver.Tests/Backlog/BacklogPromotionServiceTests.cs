using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Backlog;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Backlog;

public sealed class BacklogPromotionServiceTests
{
    [Fact]
    public async Task PromoteAsync_CreatesDependencyBatch_AndReplayIsIdempotent()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var project = MakeProject();
        await projects.InsertAsync(project);

        var parentRun = MakeCoordinatorRun(project.Id, RunId.New());
        await runStore.InsertAsync(parentRun);

        var service = new BacklogPromotionService(BuildSqliteConfig(), runStore, testDb.Db);
        var stories = new[]
        {
            new PromotedStoryInput("story-a", "Story A", "First promoted story", "estimated_subtasks>=3", []),
            new PromotedStoryInput("story-b", "Story B", "Depends on A", "explicit [run] override", ["story-a"]),
        };

        var first = await service.PromoteAsync(project.Id, parentRun.Id, "Coordinator", stories);

        first.CreatedCount.Should().Be(2);
        first.Tasks.Select(t => t.PromotionKey).Should().Equal("story-a", "story-b");
        first.Tasks.Should().OnlyContain(t =>
            t.ParentPrdRunId == parentRun.Id
            && t.State == BacklogTaskState.Backlog
            && t.CapturedBy == "Coordinator");

        var storedTasks = await backlogStore.ListByProjectAsync(project.Id);
        var dependencies = await backlogStore.ListDependenciesAsync(project.Id, storedTasks.Select(t => t.Id).ToList());
        dependencies.Should().ContainSingle();
        dependencies[0].TaskId.Should().Be(first.Tasks.Single(t => t.PromotionKey == "story-b").Id);
        dependencies[0].DependsOnTaskId.Should().Be(first.Tasks.Single(t => t.PromotionKey == "story-a").Id);

        var replay = await service.PromoteAsync(project.Id, parentRun.Id, "Coordinator", stories);
        replay.CreatedCount.Should().Be(0);
        replay.Tasks.Select(t => t.Id).Should().Equal(first.Tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task PromoteAsync_WhenReplayPayloadDiffers_ThrowsPromotionKeyConflict()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var project = MakeProject();
        await projects.InsertAsync(project);

        var parentRun = MakeCoordinatorRun(project.Id, RunId.New());
        await runStore.InsertAsync(parentRun);

        var service = new BacklogPromotionService(BuildSqliteConfig(), runStore, testDb.Db);
        await service.PromoteAsync(project.Id, parentRun.Id, "Coordinator", [
            new PromotedStoryInput("story-a", "Story A", "First promoted story", "estimated_subtasks>=3", [])
        ]);

        var act = async () => await service.PromoteAsync(project.Id, parentRun.Id, "Coordinator", [
            new PromotedStoryInput("story-a", "Story A changed", "First promoted story", "estimated_subtasks>=3", [])
        ]);

        (await act.Should().ThrowAsync<PromotionKeyConflictException>())
            .Which.Message.Should().Be("promotion_key_conflict");
    }

    private static IConfiguration BuildSqliteConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Provider"] = "sqlite" })
            .Build();
}
