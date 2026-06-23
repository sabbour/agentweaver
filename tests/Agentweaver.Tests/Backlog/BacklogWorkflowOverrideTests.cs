using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Store-level tests for the per-task workflow override (Feature 010, FR-042) over a REAL temp SQLite
/// DB. Proves the override persists while a task is unclaimed (Backlog or Ready), can be cleared, and
/// is GATED once the task is claimed — <see cref="SqliteBacklogTaskStore.UpdateWorkflowOverrideAsync"/>
/// returns false for a claimed task and never mutates it. No store logic is mocked (Principle VII).
/// </summary>
public sealed class BacklogWorkflowOverrideTests
{
    private static async Task<(TestSqliteDb Db, SqliteBacklogTaskStore Store, SqliteRunStore Runs, Project Project)>
        NewStoreWithProjectAsync()
    {
        var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);
        var runs = new SqliteRunStore(testDb.Db);
        var project = MakeProject();
        await projects.InsertAsync(project);
        return (testDb, store, runs, project);
    }

    [Fact]
    public async Task UpdateWorkflowOverride_OnBacklogTask_Persists()
    {
        var (testDb, store, _, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var task = MakeBacklogTask(project.Id, "n");
        await store.InsertAsync(task);

        var applied = await store.UpdateWorkflowOverrideAsync(project.Id, task.Id, "custom-flow");
        applied.Should().BeTrue();

        var reread = await store.GetAsync(project.Id, task.Id);
        reread!.WorkflowOverrideId.Should().Be("custom-flow");
    }

    [Fact]
    public async Task UpdateWorkflowOverride_OnReadyTask_Persists_AndCanBeCleared()
    {
        var (testDb, store, _, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        (await store.UpdateWorkflowOverrideAsync(project.Id, task.Id, "custom-flow")).Should().BeTrue();
        (await store.GetAsync(project.Id, task.Id))!.WorkflowOverrideId.Should().Be("custom-flow");

        // Clearing (null) is allowed while unclaimed and resets to the project default.
        (await store.UpdateWorkflowOverrideAsync(project.Id, task.Id, null)).Should().BeTrue();
        (await store.GetAsync(project.Id, task.Id))!.WorkflowOverrideId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateWorkflowOverride_OnClaimedTask_IsRejected_AndDoesNotMutate()
    {
        var (testDb, store, _, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        // Claim the task (Ready -> Claimed) through the real atomic claim+reserve transaction.
        var claim = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id, MakeCoordinatorRun(project.Id, RunId.New()), DateTimeOffset.UtcNow);
        claim.Should().Be(ClaimReserveResult.Won);

        // FR-042 gate: an override may only be chosen while the task is unclaimed.
        var applied = await store.UpdateWorkflowOverrideAsync(project.Id, task.Id, "custom-flow");
        applied.Should().BeFalse();

        var reread = await store.GetAsync(project.Id, task.Id);
        reread!.State.Should().Be(BacklogTaskState.Claimed);
        reread.WorkflowOverrideId.Should().BeNull("a claimed task's override must not be mutated");
    }

    [Fact]
    public async Task UpdateWorkflowOverride_UnknownTask_ReturnsFalse()
    {
        var (testDb, store, _, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var applied = await store.UpdateWorkflowOverrideAsync(project.Id, BacklogTaskId.New(), "custom-flow");
        applied.Should().BeFalse();
    }
}
