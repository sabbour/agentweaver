using FluentAssertions;
using Microsoft.Data.Sqlite;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Store-level tests for <see cref="SqliteBacklogTaskStore"/> over a REAL temp SQLite DB: top-N
/// priority ordering (FR-008a / FR-018a), order_key uniqueness + retry (FR-018a), and project
/// scoping / no cross-leakage (FR-003 / SC-007).
/// </summary>
public sealed class SqliteBacklogTaskStoreTests
{
    private static async Task<(TestSqliteDb, SqliteBacklogTaskStore, Project)> NewStoreWithProjectAsync()
    {
        var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);
        var project = MakeProject();
        await projects.InsertAsync(project);
        return (testDb, store, project);
    }

    // =========================================================================
    // 3. TOP-N PRIORITY ORDER (FR-008a / FR-018a).
    // =========================================================================
    [Fact]
    public async Task ListReadyForClaim_ReturnsByOrderKey_RespectsCap_AndKeepsRemainder()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        // Insert Ready tasks out of priority order; ascending order_key == highest pickup priority.
        var keys = new[] { "g", "b", "t", "n", "c" };           // sorted: b,c,g,n,t
        var byKey = new Dictionary<string, BacklogTaskId>();
        foreach (var k in keys)
        {
            var t = MakeReadyTask(project.Id, k);
            byKey[k] = t.Id;
            await store.InsertAsync(t);
        }

        // N=3 (the heartbeat's default claim cap) returns the top-3 by order_key, deterministically.
        var top3 = await store.ListReadyForClaimAsync(project.Id, 3);
        top3.Select(t => t.OrderKey).Should().Equal("b", "c", "g");

        // Beyond-N items remain Ready and surface when the cap is raised.
        var all = await store.ListReadyForClaimAsync(project.Id, 100);
        all.Select(t => t.OrderKey).Should().Equal("b", "c", "g", "n", "t");

        // Reorder the lowest-priority task ("t") to the very top: pickup order changes.
        var reordered = await store.TryReorderAsync(
            project.Id, byKey["t"], BacklogTaskState.Ready, OrderKey.Between(null, "b"));
        reordered.Should().BeTrue();

        var afterReorder = await store.ListReadyForClaimAsync(project.Id, 3);
        afterReorder[0].Id.Should().Be(byKey["t"], "the reordered task is now top priority");
    }

    // =========================================================================
    // 4. ORDER_KEY UNIQUENESS (FR-018a): partial unique index holds + reorder retry.
    // =========================================================================
    [Fact]
    public async Task DuplicateOrderKey_InSameUnclaimedBucket_IsRejected()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        await store.InsertAsync(MakeReadyTask(project.Id, "n"));

        var dup = MakeReadyTask(project.Id, "n");
        var act = async () => await store.InsertAsync(dup);

        (await act.Should().ThrowAsync<SqliteException>())
            .Which.SqliteErrorCode.Should().Be(19, "SQLITE_CONSTRAINT from the partial unique index");
    }

    [Fact]
    public async Task CollidingReorder_RetriesToADistinctKey_NoTwoTasksShareAnOrderKey()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var a = MakeReadyTask(project.Id, "a");
        var b = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(a);
        await store.InsertAsync(b);

        // Ask to place b on top of a's exact key. The store catches the UNIQUE conflict and retries
        // to a distinct key in the destination bucket rather than corrupting the index.
        var ok = await store.TryReorderAsync(project.Id, b.Id, BacklogTaskState.Ready, "a");
        ok.Should().BeTrue();

        var bAfter = await store.GetAsync(project.Id, b.Id);
        bAfter!.OrderKey.Should().NotBe("a");
        string.CompareOrdinal(bAfter.OrderKey, "a").Should().BeGreaterThan(0, "retry resolves above the colliding key");

        // Invariant: no two unclaimed tasks in the same bucket share an order_key.
        var ready = await store.ListReadyForClaimAsync(project.Id, 100);
        ready.Select(t => t.OrderKey).Should().OnlyHaveUniqueItems();
    }

    // =========================================================================
    // 5. PROJECT SCOPING / NO CROSS-LEAKAGE (FR-003 / SC-007).
    // =========================================================================
    [Fact]
    public async Task EveryReadAndMutation_IsProjectScoped_AcrossProjects()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);

        var projectA = MakeProject();
        var projectB = MakeProject();
        await projects.InsertAsync(projectA);
        await projects.InsertAsync(projectB);

        var readyInA = MakeReadyTask(projectA.Id, "n");
        var backlogInA = MakeBacklogTask(projectA.Id, "n");
        await store.InsertAsync(readyInA);
        await store.InsertAsync(backlogInA);

        // get is project-scoped: project B cannot see project A's task.
        (await store.GetAsync(projectB.Id, readyInA.Id)).Should().BeNull();
        (await store.GetAsync(projectA.Id, readyInA.Id)).Should().NotBeNull();

        // edit via the wrong project mutates zero rows and leaves content intact.
        (await store.UpdateContentAsync(projectB.Id, readyInA.Id, "HACKED", "x")).Should().BeFalse();
        (await store.GetAsync(projectA.Id, readyInA.Id))!.Title.Should().Be("A task");

        // move via the wrong project is rejected and state is preserved.
        (await store.TryMoveToBacklogAsync(projectB.Id, readyInA.Id, OrderKey.Between(null, null))).Should().BeFalse();
        (await store.TryMoveToReadyAsync(projectB.Id, backlogInA.Id, OrderKey.Between(null, null), DateTimeOffset.UtcNow)).Should().BeFalse();
        (await store.GetAsync(projectA.Id, readyInA.Id))!.State.Should().Be(BacklogTaskState.Ready);
        (await store.GetAsync(projectA.Id, backlogInA.Id))!.State.Should().Be(BacklogTaskState.Backlog);

        // reorder via the wrong project is rejected.
        (await store.TryReorderAsync(projectB.Id, readyInA.Id, BacklogTaskState.Ready, "z")).Should().BeFalse();

        // delete via the wrong project affects zero rows; the task survives.
        (await store.TryDeleteAsync(projectB.Id, readyInA.Id)).Should().BeFalse();
        (await store.GetAsync(projectA.Id, readyInA.Id)).Should().NotBeNull();

        // list/list-ready are project-scoped: project B sees none of project A's tasks.
        (await store.ListByProjectAsync(projectB.Id)).Should().BeEmpty();
        (await store.ListReadyForClaimAsync(projectB.Id, 100)).Should().BeEmpty();
        (await store.ListByProjectAsync(projectA.Id)).Should().HaveCount(2);
    }

    // =========================================================================
    // 5b. ON DELETE CASCADE: deleting a project removes its backlog tasks.
    // =========================================================================
    [Fact]
    public async Task DeletingProject_CascadesToBacklogTasks()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);

        var project = MakeProject();
        await projects.InsertAsync(project);
        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        await projects.DeleteAsync(project.Id);

        (await store.GetAsync(project.Id, task.Id)).Should().BeNull();
        (await store.ListByProjectAsync(project.Id)).Should().BeEmpty();
    }

    // =========================================================================
    // 7. BULK PROMOTE (MoveAllBacklogToReadyAsync): all Backlog -> Ready, appended after
    //    existing Ready, preserving relative backlog order; atomic; idempotent.
    // =========================================================================
    [Fact]
    public async Task MoveAllBacklogToReady_PromotesAll_PreservingOrder_AppendedAfterExistingReady()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        // One pre-existing Ready item the promoted tasks must land AFTER (lower priority).
        var existingReady = MakeReadyTask(project.Id, "c");
        await store.InsertAsync(existingReady);

        // Backlog items inserted out of order; their relative backlog order is order_key ascending:
        // b -> g -> t. (Insertion order deliberately differs to prove ordering is by order_key.)
        var backlogG = MakeBacklogTask(project.Id, "g");
        var backlogB = MakeBacklogTask(project.Id, "b");
        var backlogT = MakeBacklogTask(project.Id, "t");
        await store.InsertAsync(backlogG);
        await store.InsertAsync(backlogB);
        await store.InsertAsync(backlogT);

        var before = DateTimeOffset.UtcNow;
        var moved = await store.MoveAllBacklogToReadyAsync(project.Id, before);
        moved.Should().Be(3, "every backlog task is promoted");

        // Backlog bucket is now empty; all three are Ready.
        var all = await store.ListByProjectAsync(project.Id);
        all.Should().OnlyContain(t => t.State == BacklogTaskState.Ready);

        // Ready order: the pre-existing Ready item first, then the promoted tasks in their preserved
        // relative backlog order (b, g, t) appended at the bottom.
        var ready = await store.ListReadyForClaimAsync(project.Id, 100);
        ready.Select(t => t.Id).Should().Equal(
            existingReady.Id, backlogB.Id, backlogG.Id, backlogT.Id);

        // Each promoted task got committed_at stamped and a unique order_key strictly above existing.
        foreach (var id in new[] { backlogB.Id, backlogG.Id, backlogT.Id })
        {
            var t = await store.GetAsync(project.Id, id);
            t!.CommittedAt.Should().NotBeNull();
            string.CompareOrdinal(t.OrderKey, existingReady.OrderKey).Should().BeGreaterThan(0);
        }
        ready.Select(t => t.OrderKey).Should().OnlyHaveUniqueItems();

        // Idempotent: a second call with an empty backlog moves nothing.
        (await store.MoveAllBacklogToReadyAsync(project.Id, DateTimeOffset.UtcNow)).Should().Be(0);
    }

    [Fact]
    public async Task MoveAllBacklogToReady_EmptyBacklog_ReturnsZero()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        // Only a Ready item exists; there is nothing in the backlog bucket to promote.
        await store.InsertAsync(MakeReadyTask(project.Id, "n"));

        (await store.MoveAllBacklogToReadyAsync(project.Id, DateTimeOffset.UtcNow)).Should().Be(0);

        // The existing Ready item is untouched.
        (await store.ListReadyForClaimAsync(project.Id, 100)).Should().ContainSingle()
            .Which.OrderKey.Should().Be("n");
    }

    [Fact]
    public async Task Archive_RemovesReadyTaskFromActiveLists_AndKeepsOtherReadyClaimable()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var archivedReady = MakeReadyTask(project.Id, "n");
        var liveReady = MakeReadyTask(project.Id, "t");
        await store.InsertAsync(archivedReady);
        await store.InsertAsync(liveReady);

        var archivedAt = DateTimeOffset.UtcNow;
        (await store.TryArchiveAsync(project.Id, archivedReady.Id, archivedAt)).Should().BeTrue();

        (await store.GetAsync(project.Id, archivedReady.Id))!.ArchivedAt.Should().NotBeNull();
        (await store.ListByProjectAsync(project.Id)).Should().ContainSingle()
            .Which.Id.Should().Be(liveReady.Id);
        (await store.ListReadyForClaimAsync(project.Id, 10)).Should().ContainSingle()
            .Which.Id.Should().Be(liveReady.Id);

        var newReadyReusingOrderKey = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(newReadyReusingOrderKey);
        (await store.ListReadyForClaimAsync(project.Id, 10)).Select(t => t.Id)
            .Should().Equal(newReadyReusingOrderKey.Id, liveReady.Id);
    }
}
