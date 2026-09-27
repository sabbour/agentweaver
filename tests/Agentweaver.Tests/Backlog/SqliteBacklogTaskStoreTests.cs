using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task EnsureCreated_MigratesLegacyBacklogTableBeforeCreatingPromotionIndex()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"agentweaver-legacy-backlog-{Guid.NewGuid():N}.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={filePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE backlog_tasks (
                        task_id TEXT PRIMARY KEY,
                        project_id TEXT NOT NULL,
                        title TEXT NOT NULL,
                        description TEXT,
                        state TEXT NOT NULL,
                        order_key TEXT NOT NULL,
                        captured_by TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        committed_at TEXT,
                        claimed_at TEXT,
                        run_id TEXT,
                        archived_at TEXT,
                        source_file_path TEXT
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = filePath })
                .Build();

            var db = new SqliteDb(config);
            var act = async () => await db.EnsureCreatedAsync();

            await act.Should().NotThrowAsync();

            await using var migrated = await db.OpenConnectionAsync();
            await using var verify = migrated.CreateCommand();
            verify.CommandText =
                "SELECT COUNT(*) FROM pragma_index_list('backlog_tasks') WHERE name = 'idx_backlog_tasks_parent_promotion_key';";
            Convert.ToInt64(await verify.ExecuteScalarAsync()).Should().Be(1);
            verify.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('backlog_tasks') WHERE name = 'automation_invocation_pending';";
            Convert.ToInt64(await verify.ExecuteScalarAsync()).Should().Be(1,
                "upgraded SQLite databases must retain the durable provisional-invocation marker");
            verify.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('backlog_tasks') WHERE name = 'ai_execution_provider_key';";
            Convert.ToInt64(await verify.ExecuteScalarAsync()).Should().Be(1,
                "queued AI work must retain its accepted execution plan across pickup");
        }

        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { filePath, filePath + "-wal", filePath + "-shm" })
            {
                try { File.Delete(path); }
                catch { }
            }
        }
    }

    [Fact]
    public async Task InsertAndRead_RoundTripsAcceptedAiExecutionProviderKey()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var task = MakeReadyTask(project.Id, "m") with
        {
            AiExecutionProviderKey = "signed.execution-plan",
        };

        await store.InsertAsync(task);
        var stored = await store.GetAsync(project.Id, task.Id);

        stored!.AiExecutionProviderKey.Should().Be("signed.execution-plan");
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

    [Fact]
    public async Task ReadyTask_WithUnmergedPrerequisite_IsBlockedUntilDependencyRunMerges()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var runStore = new SqliteRunStore(testDb.Db);

        var prerequisiteRun = MakeCoordinatorRun(project.Id, RunId.New()) with
        {
            Status = RunStatus.AwaitingReview,
            EndedAt = DateTimeOffset.UtcNow,
        };
        await runStore.InsertAsync(prerequisiteRun);

        var prerequisiteTask = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = project.Id,
            Title = "Prerequisite",
            Description = "must merge first",
            State = BacklogTaskState.Claimed,
            OrderKey = "a",
            CapturedBy = "alice",
            CreatedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
            ClaimedAt = DateTimeOffset.UtcNow,
            RunId = prerequisiteRun.Id,
        };
        var dependentTask = MakeReadyTask(project.Id, "b");
        await store.InsertAsync(prerequisiteTask);
        await store.InsertAsync(dependentTask);
        await InsertDependencyAsync(testDb.Db, project.Id, dependentTask.Id, prerequisiteTask.Id);

        (await store.ListReadyForClaimAsync(project.Id, 10)).Should().BeEmpty();
        (await store.CountReadyForPickupAsync()).Should().Be(0);

        var blockedClaim = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id,
            dependentTask.Id,
            MakeCoordinatorRun(project.Id, RunId.New()),
            DateTimeOffset.UtcNow);
        blockedClaim.Should().Be(ClaimReserveResult.Lost);

        await PublishIntegratedAsync(runStore, prerequisiteRun.Id, RunStatus.Merged, "complete",
            "accepted-commit", "accepted-tree");

        (await store.ListReadyForClaimAsync(project.Id, 10)).Select(t => t.Id).Should().Equal(dependentTask.Id);
        (await store.CountReadyForPickupAsync()).Should().Be(1);

        var readyClaim = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id,
            dependentTask.Id,
            MakeCoordinatorRun(project.Id, RunId.New()),
            DateTimeOffset.UtcNow);
        readyClaim.Should().Be(ClaimReserveResult.Won);
    }

    [Theory]
    [InlineData("completed", "assembly_complete", "integrated", true)]
    [InlineData("completed", "complete", "integrated", true)]
    [InlineData("completed", "confirmed", "accepted_no_change", true)]
    [InlineData("completed", "delegated_to_backlog", "delegated", false)]
    [InlineData("completed", null, "pending", false)]
    [InlineData("failed", "cancelled", "cancelled", false)]
    [InlineData("failed", "assembly_failed", "failed", false)]
    public async Task PrerequisiteOutcome_ControlsReadinessAndExplainsReason(
        string status, string? result, string reason, bool satisfied)
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var run = MakeCoordinatorRun(project.Id, RunId.New()) with
        {
            Status = reason == "integrated" ? RunStatus.InProgress
                : Agentweaver.Api.Contracts.RunStatusExtensions.ParseStatus(status),
            Result = reason == "integrated" ? null : result,
        };
        var runs = new SqliteRunStore(testDb.Db);
        await runs.InsertAsync(run);
        if (reason == "integrated")
            await PublishIntegratedAsync(runs, run.Id,
                Agentweaver.Api.Contracts.RunStatusExtensions.ParseStatus(status), result!,
                "accepted-commit", "accepted-tree");
        var prerequisite = MakeReadyTask(project.Id, "a") with
        {
            State = BacklogTaskState.Claimed,
            RunId = run.Id,
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        var dependent = MakeReadyTask(project.Id, "b");
        await store.InsertAsync(prerequisite);
        await store.InsertAsync(dependent);
        await InsertDependencyAsync(testDb.Db, project.Id, dependent.Id, prerequisite.Id);

        var dependency = (await store.ListDependencyStatusesAsync(project.Id, [dependent.Id])).Single();
        dependency.Reason.Should().Be(reason);
        dependency.IsSatisfied.Should().Be(satisfied);
        (await store.ListReadyForClaimAsync(project.Id, 1)).Select(t => t.Id)
            .Should().Equal(satisfied ? [dependent.Id] : []);
        (await store.CountReadyForPickupAsync()).Should().Be(satisfied ? 1 : 0);
    }

    [Fact]
    public async Task IntegratedRunWithoutCompleteIdentity_BlocksClaimWithMachineReadableReason()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var runs = new SqliteRunStore(testDb.Db);
        var sourceRun = MakeCoordinatorRun(project.Id, RunId.New()) with
        {
            Status = RunStatus.Completed,
            Result = "assembly_complete",
            MergedCommitHash = "accepted-commit",
        };
        await runs.InsertAsync(sourceRun);
        var source = MakeReadyTask(project.Id, "a") with
        {
            State = BacklogTaskState.Claimed,
            RunId = sourceRun.Id,
            ClaimedAt = DateTimeOffset.UtcNow,
        };
        var dependent = MakeReadyTask(project.Id, "b");
        await store.InsertAsync(source);
        await store.InsertAsync(dependent);
        await InsertDependencyAsync(testDb.Db, project.Id, dependent.Id, source.Id);

        (await store.ListDependencyStatusesAsync(project.Id, [dependent.Id]))
            .Should().ContainSingle(s => !s.IsSatisfied && s.Reason == "upstream_output_identity_unavailable");
        (await store.ListReadyForClaimAsync(project.Id, 1)).Should().BeEmpty();
        (await store.TryClaimAndReserveCoordinatorRunAsync(project.Id, dependent.Id,
            MakeCoordinatorRun(project.Id, RunId.New()), DateTimeOffset.UtcNow))
            .Should().Be(ClaimReserveResult.Lost);
        (await store.GetAsync(project.Id, dependent.Id))!.State.Should().Be(BacklogTaskState.Ready);
    }

    [Fact]
    public async Task BlockedPrefixBeyondFourPickupWindows_DoesNotHideReadyTail()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var prerequisite = MakeBacklogTask(project.Id, "prerequisite");
        await store.InsertAsync(prerequisite);
        for (var i = 0; i < 25; i++)
        {
            var blocked = MakeReadyTask(project.Id, $"a{i:D3}");
            await store.InsertAsync(blocked);
            await InsertDependencyAsync(testDb.Db, project.Id, blocked.Id, prerequisite.Id);
        }
        var ready = MakeReadyTask(project.Id, "z");
        await store.InsertAsync(ready);

        (await store.ListReadyForClaimAsync(project.Id, 3)).Select(t => t.Id).Should().Equal(ready.Id);
        (await store.CountReadyForPickupAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ReverseIdTransitiveJoin_FiltersBeforeLimitAndOrdersEligibleTies()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var ids = Enumerable.Range(0, 30).Select(_ => BacklogTaskId.New())
            .OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        var upstream = MakeBacklogTask(project.Id, "source", ids[^1]);
        var middle = MakeReadyTask(project.Id, "a000", ids[1]);
        var downstream = MakeReadyTask(project.Id, "a001", ids[0]);
        await store.InsertAsync(upstream);
        await store.InsertAsync(middle);
        await store.InsertAsync(downstream);
        await InsertDependencyAsync(testDb.Db, project.Id, middle.Id, upstream.Id);
        await InsertDependencyAsync(testDb.Db, project.Id, downstream.Id, middle.Id);
        for (var i = 2; i < 27; i++)
        {
            var blocked = MakeReadyTask(project.Id, $"a{i:D3}", ids[i]);
            await store.InsertAsync(blocked);
            await InsertDependencyAsync(testDb.Db, project.Id, blocked.Id, upstream.Id);
        }
        var tieA = MakeReadyTask(project.Id, "z", ids[27]) with { CommittedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var tieB = MakeReadyTask(project.Id, "z", ids[28]) with { CommittedAt = DateTimeOffset.UtcNow };
        // Ready order keys are unique per bucket; use distinct keys for the order assertion.
        tieB = tieB with { OrderKey = "zz" };
        await store.InsertAsync(tieA);
        await store.InsertAsync(tieB);

        (await store.ListReadyForClaimAsync(project.Id, 1)).Select(t => t.Id).Should().Equal(tieA.Id);
        (await store.ListReadyForClaimAsync(project.Id, 2)).Select(t => t.Id).Should().Equal(tieA.Id, tieB.Id);
        (await store.CountReadyForPickupAsync()).Should().Be(2);
    }

    [Fact]
    public async Task DependencyEditor_RejectsInvalidGraphsWithoutPartialMutation()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var a = MakeReadyTask(project.Id, "a");
        var b = MakeReadyTask(project.Id, "b");
        var c = MakeReadyTask(project.Id, "c");
        await store.InsertAsync(a);
        await store.InsertAsync(b);
        await store.InsertAsync(c);
        var other = MakeProject();
        await new SqliteProjectStore(testDb.Db).InsertAsync(other);
        var foreign = MakeReadyTask(other.Id, "foreign");
        await store.InsertAsync(foreign);

        var first = await store.EditDependenciesAsync(project.Id, 0,
            new BacklogDependencyEdit(b.Id, [a.Id], []));
        first.Revision.Should().Be(1);
        var second = await store.EditDependenciesAsync(project.Id, 1,
            new BacklogDependencyEdit(c.Id, [b.Id], []));
        second.Revision.Should().Be(2);

        foreach (var invalid in new[]
        {
            new BacklogDependencyEdit(a.Id, [a.Id], []),
            new BacklogDependencyEdit(a.Id, [BacklogTaskId.New()], []),
            new BacklogDependencyEdit(a.Id, [foreign.Id], []),
            new BacklogDependencyEdit(a.Id, [c.Id], []),
        })
        {
            var act = () => store.EditDependenciesAsync(project.Id, 2, invalid);
            await act.Should().ThrowAsync<BacklogDependencyEditException>();
            (await store.GetDependencyRevisionAsync(project.Id)).Should().Be(2);
            (await store.ListDependenciesAsync(project.Id, [a.Id])).Should().BeEmpty();
        }

        var invalidPreview = () => store.EditDependenciesAsync(project.Id, 2,
            new BacklogDependencyEdit(b.Id, [], [], [c.Id]), preview: true);
        await invalidPreview.Should().ThrowAsync<BacklogDependencyEditException>();
    }

    [Fact]
    public async Task DependencyEditor_ReplaceRemovePreviewAndConcurrentOppositeEdges()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var a = MakeReadyTask(project.Id, "a");
        var b = MakeReadyTask(project.Id, "b");
        var c = MakeReadyTask(project.Id, "c");
        await store.InsertAsync(a);
        await store.InsertAsync(b);
        await store.InsertAsync(c);

        var preview = await store.EditDependenciesAsync(project.Id, 0,
            new BacklogDependencyEdit(b.Id, [a.Id], []), preview: true);
        preview.AffectedTaskIds.Should().Contain(b.Id);
        preview.Revision.Should().Be(1);
        (await store.GetDependencyRevisionAsync(project.Id)).Should().Be(0);

        var results = await Task.WhenAll(
            new Func<Task<bool>>[]
            {
                async () => await TryEditAsync(store, project.Id, a.Id, b.Id),
                async () => await TryEditAsync(store, project.Id, b.Id, a.Id),
            }.Select(f => f()));
        results.Should().ContainSingle(value => value);
        (await store.GetDependencyRevisionAsync(project.Id)).Should().Be(1);
        var target = results[0] ? a.Id : b.Id;
        var source = results[0] ? b.Id : a.Id;
        var replaced = await store.EditDependenciesAsync(project.Id, 1,
            new BacklogDependencyEdit(target, [], [], [c.Id]));
        replaced.Prerequisites.Should().Equal(c.Id);
        var removed = await store.EditDependenciesAsync(project.Id, 2,
            new BacklogDependencyEdit(target, [], [c.Id]));
        removed.Prerequisites.Should().BeEmpty();
        (await store.ListDependenciesAsync(project.Id, [target])).Should().BeEmpty();
        var stale = () => store.EditDependenciesAsync(project.Id, 1,
            new BacklogDependencyEdit(source, [c.Id], []));
        (await stale.Should().ThrowAsync<BacklogDependencyEditException>())
            .Which.Message.Should().Be("stale_graph_revision");
    }

    private static async Task<bool> TryEditAsync(
        SqliteBacklogTaskStore store, ProjectId project, BacklogTaskId task, BacklogTaskId prerequisite)
    {
        try
        {
            await store.EditDependenciesAsync(project, 0,
                new BacklogDependencyEdit(task, [prerequisite], []));
            return true;
        }
        catch (BacklogDependencyEditException ex) when (ex.Message is "stale_graph_revision" or "dependency_cycle")
        {
            return false;
        }
    }

        [Fact]
        public async Task ThreeStories_ParallelBranchesJoinFailureRecoveryAndClaimRevision()
        {
            var (testDb, store, project) = await NewStoreWithProjectAsync();
            await using var _ = testDb;
            var runs = new SqliteRunStore(testDb.Db);
            var a = MakeReadyTask(project.Id, "a");
            var b = MakeReadyTask(project.Id, "b");
            var join = MakeReadyTask(project.Id, "c");
            await store.InsertAsync(a);
            await store.InsertAsync(b);
            await store.InsertAsync(join);
            await store.EditDependenciesAsync(project.Id, 0, new BacklogDependencyEdit(join.Id, [a.Id, b.Id], []));
            (await store.ListReadyForClaimAsync(project.Id, 3)).Select(t => t.Id).Should().Equal(a.Id, b.Id);

            var aRun = MakeCoordinatorRun(project.Id, RunId.New());
            var bRun = MakeCoordinatorRun(project.Id, RunId.New());
            (await store.TryClaimAndReserveCoordinatorRunAsync(project.Id, a.Id, aRun, DateTimeOffset.UtcNow))
                .Should().Be(ClaimReserveResult.Won);
            (await store.TryClaimAndReserveCoordinatorRunAsync(project.Id, b.Id, bRun, DateTimeOffset.UtcNow))
                .Should().Be(ClaimReserveResult.Won);
            await PublishIntegratedAsync(runs, aRun.Id, RunStatus.Completed, "assembly_complete",
                "a-commit", "a-tree");
            (await runs.TerminalizeForTestAsync(bRun.Id, RunStatus.Failed, "assembly_failed")).Should().BeTrue();
            (await store.ListReadyForClaimAsync(project.Id, 1)).Should().BeEmpty();
            (await store.ListDependencyStatusesAsync(project.Id, [join.Id]))
                .Should().ContainSingle(s => s.DependsOnTaskId == b.Id && s.Reason == "failed");

            await runs.UpdateStatusAsync(bRun.Id, RunStatus.InProgress, null);
            await PublishIntegratedAsync(runs, bRun.Id, RunStatus.Completed, "assembly_complete",
                "b-commit", "b-tree");
            (await store.ListReadyForClaimAsync(project.Id, 1)).Select(t => t.Id).Should().Equal(join.Id);
            var joinRun = MakeCoordinatorRun(project.Id, RunId.New());
            (await store.TryClaimAndReserveCoordinatorRunAsync(project.Id, join.Id, joinRun, DateTimeOffset.UtcNow))
                .Should().Be(ClaimReserveResult.Won);
            var claimed = await store.GetAsync(project.Id, join.Id);
            claimed!.ClaimedGraphRevision.Should().Be(1);
            var accepted = System.Text.Json.JsonSerializer.Deserialize<BacklogClaimedPrerequisite[]>(
                claimed.ClaimedPrerequisitesJson!)!;
            accepted.Select(input => input.RunId).Should().BeEquivalentTo(
                [aRun.Id.ToString(), bRun.Id.ToString()]);
            accepted.Should().OnlyContain(input => input.Outcome == "integrated");
            accepted.Single(input => input.RunId == bRun.Id.ToString())
                .LifecycleGeneration.Should().BeGreaterThan(1);

            (await store.TryArchiveAsync(project.Id, a.Id, DateTimeOffset.UtcNow)).Should().BeTrue();
            (await store.GetAsync(project.Id, join.Id))!.ClaimedPrerequisitesJson.Should().Be(claimed.ClaimedPrerequisitesJson);
            var editClaimed = () => store.EditDependenciesAsync(project.Id, 1,
                new BacklogDependencyEdit(join.Id, [], [], []));
            (await editClaimed.Should().ThrowAsync<BacklogDependencyEditException>())
                .Which.Message.Should().Be("task_claimed_or_archived");
        }
    private static async Task PublishIntegratedAsync(
        SqliteRunStore runs, RunId id, RunStatus status, string result, string commit, string tree)
    {
        var run = await runs.GetAsync(id);
        (await runs.TryMutateTerminalOutcomeAsync(id,
            new TerminalRunMutation(
                TerminalRunOutcome.Create(status, "run.completed", new { result },
                    DateTimeOffset.UtcNow, run!.LifecycleGeneration),
                result,
                MergedCommitHash: commit,
                TreeHash: tree))).Should().BeTrue();
    }

    [Fact]
    public async Task TryDelete_WhenTaskIsDependencyTarget_ThrowsFriendlyDependencyException()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;

        var prerequisiteTask = MakeBacklogTask(project.Id, "a");
        var dependentTask = MakeBacklogTask(project.Id, "b");
        await store.InsertAsync(prerequisiteTask);
        await store.InsertAsync(dependentTask);
        await InsertDependencyAsync(testDb.Db, project.Id, dependentTask.Id, prerequisiteTask.Id);

        var act = async () => await store.TryDeleteAsync(project.Id, prerequisiteTask.Id);

        (await act.Should().ThrowAsync<BacklogTaskDependencyException>())
            .Which.Message.Should().Be("task_is_dependency");
    }

    [Fact]
    public async Task DeletingUnclaimedDependent_AdvancesGraphRevision()
    {
        var (testDb, store, project) = await NewStoreWithProjectAsync();
        await using var _ = testDb;
        var upstream = MakeBacklogTask(project.Id, "a");
        var downstream = MakeBacklogTask(project.Id, "b");
        await store.InsertAsync(upstream);
        await store.InsertAsync(downstream);
        await store.EditDependenciesAsync(project.Id, 0,
            new BacklogDependencyEdit(downstream.Id, [upstream.Id], []));

        (await store.TryDeleteAsync(project.Id, downstream.Id)).Should().BeTrue();
        (await store.GetDependencyRevisionAsync(project.Id)).Should().Be(2);
        (await store.ListDependenciesAsync(project.Id, [downstream.Id])).Should().BeEmpty();
    }

    private static async Task InsertDependencyAsync(
        SqliteDb db,
        ProjectId projectId,
        BacklogTaskId taskId,
        BacklogTaskId dependsOnTaskId)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO backlog_task_dependencies (project_id, task_id, depends_on_task_id, created_at)
            VALUES ($projectId, $taskId, $dependsOnTaskId, $createdAt);
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString());
        command.Parameters.AddWithValue("$taskId", taskId.ToString());
        command.Parameters.AddWithValue("$dependsOnTaskId", dependsOnTaskId.ToString());
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
