using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Api;

public sealed class TerminalRunOutcomeStoreTests
{
    [Fact]
    public async Task TerminalOutcome_SeparateSqliteInstances_RaceToOneTypedWinner()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var first = new SqliteRunStore(testDb.Db);
        var second = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(first);
        var winner = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "done" },
            DateTimeOffset.UtcNow, 1);
        var loser = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "late" },
            DateTimeOffset.UtcNow, 1);

        var results = await Task.WhenAll(
            first.TrySetTerminalOutcomeAsync(run, winner, "done"),
            second.TrySetTerminalOutcomeAsync(run, loser, "late"));

        results.Count(changed => changed).Should().Be(1);
        var outcomes = await first.GetUnprojectedTerminalOutcomesAsync();
        outcomes.Should().ContainSingle();
        outcomes[0].RunId.Should().Be(run);
        outcomes[0].LifecycleGeneration.Should().Be(1);
        outcomes[0].Outcome.EventType.Should().Be(results[0] ? EventTypes.RunCompleted : EventTypes.RunFailed);
    }

    [Fact]
    public async Task TerminalOutcome_StaleGenerationCannotTerminalizeReopenedRun()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        await store.UpdateReviewReadyAsync(run, "tree", "diff", 1);
        (await store.TryTransitionReviewToInProgressAsync(run)).Should().BeTrue();

        var stale = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old_generation" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, stale, "old_generation")).Should().BeFalse();

        var current = await store.GetAsync(run);
        current!.LifecycleGeneration.Should().Be(2);
        current.Status.Should().Be(RunStatus.InProgress);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TerminalOutcome_RemainsUnprojectedUntilProjectorAcknowledgesExactWinner()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed,
            new { reason = "workflow_start_failed", errorCode = "agent_turn_internal_error" },
            DateTimeOffset.UtcNow, 1);

        (await store.TrySetTerminalOutcomeAsync(run, outcome, "workflow_start_failed")).Should().BeTrue();
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle();

        await store.MarkTerminalOutcomeProjectedAsync(run, 1);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyTerminalStatusWriter_IsRejected()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);

        var act = () => store.TrySetTerminalStatusAsync(
            run, RunStatus.Failed, DateTimeOffset.UtcNow, "workflow_start_failed");

        await act.Should().ThrowAsync<NotSupportedException>();
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GenericStatusApis_RejectTerminalTransitions()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);

        var updateStatus = () => store.UpdateStatusAsync(run, RunStatus.Failed, DateTimeOffset.UtcNow);
        var updateResult = () => store.UpdateResultAsync(run, RunStatus.Completed, "done", DateTimeOffset.UtcNow);

        await updateStatus.Should().ThrowAsync<InvalidOperationException>();
        await updateResult.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetAsync(run))!.Status.Should().Be(RunStatus.InProgress);
    }

    [Fact]
    public async Task AssembleReady_AtomicallyPersistsArtifactsAndCanonicalWinner_AndRejectsLoser()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var first = new SqliteRunStore(testDb.Db);
        var second = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(first);

        (await first.SetAssembleReadyAsync(
            run, "tree-winner", "agent/assembly", "winner diff", 3, DateTimeOffset.UtcNow)).Should().BeTrue();
        (await second.TrySetTerminalOutcomeAsync(run,
            TerminalRunOutcome.Create(RunStatus.Failed, EventTypes.RunFailed, new { reason = "loser" },
                DateTimeOffset.UtcNow, 1), "loser")).Should().BeFalse();

        var persisted = (await first.GetAsync(run))!;
        persisted.Status.Should().Be(RunStatus.AssembleReady);
        persisted.TreeHash.Should().Be("tree-winner");
        persisted.WorktreeBranch.Should().Be("agent/assembly");
        persisted.Diff.Should().Be("winner diff");
        var winner = (await first.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle().Subject;
        winner.Outcome.EventType.Should().Be(EventTypes.RunAssembleReady);
        winner.Outcome.Payload.GetProperty("treeHash").GetString().Should().Be("tree-winner");
    }

    [Fact]
    public async Task ReviewDecline_AtomicallyPersistsReviewerAndRichWinner_AndRejectsLoser()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var first = new SqliteRunStore(testDb.Db);
        var second = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(first);
        await first.UpdateReviewReadyAsync(run, "tree", "diff", 1);

        var results = await Task.WhenAll(
            first.TryTransitionReviewAsync(run, RunStatus.Declined, DateTimeOffset.UtcNow, "first reason", "first reviewer"),
            second.TryTransitionReviewAsync(run, RunStatus.Declined, DateTimeOffset.UtcNow, "second reason", "second reviewer"));

        results.Count(changed => changed).Should().Be(1);
        var persisted = (await first.GetAsync(run))!;
        persisted.Status.Should().Be(RunStatus.Declined);
        persisted.Result.Should().Be(results[0] ? "first reason" : "second reason");
        persisted.ReviewedBy.Should().Be(results[0] ? "first reviewer" : "second reviewer");

        var winner = (await first.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle().Subject;
        winner.Outcome.EventType.Should().Be(EventTypes.ReviewDeclined);
        winner.Outcome.Payload.GetProperty("result").GetString().Should().Be(persisted.Result);
        winner.Outcome.Payload.GetProperty("reviewer").GetString().Should().Be(persisted.ReviewedBy);
    }

    [Fact]
    public async Task MergeCompletion_AtomicallyPersistsRichWinner_AndRejectsLoser()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var first = new SqliteRunStore(testDb.Db);
        var second = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(first);
        await first.UpdateReviewReadyAsync(run, "tree", "diff", 1);
        (await first.TryStartMergingAsync(run, "reviewer")).Should().BeTrue();

        var results = await Task.WhenAll(
            first.CompleteMergingAsync(run, RunStatus.Merged, DateTimeOffset.UtcNow, "first result", null, mergedCommitHash: "first-sha"),
            second.CompleteMergingAsync(run, RunStatus.MergeFailed, DateTimeOffset.UtcNow, "second result", "[\"conflict.cs\"]"));

        results.Count(changed => changed).Should().Be(1);
        var persisted = (await first.GetAsync(run))!;
        persisted.Status.Should().Be(results[0] ? RunStatus.Merged : RunStatus.MergeFailed);
        persisted.Result.Should().Be(results[0] ? "first result" : "second result");
        persisted.MergedCommitHash.Should().Be(results[0] ? "first-sha" : null);
        persisted.MergeConflicts.Should().Be(results[0] ? null : "[\"conflict.cs\"]");

        var winner = (await first.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle().Subject;
        winner.Outcome.EventType.Should().Be(results[0] ? EventTypes.MergeCompleted : EventTypes.MergeFailed);
        winner.Outcome.Payload.GetProperty("result").GetString().Should().Be(persisted.Result);
    }

    [Fact]
    public async Task Projector_AfterAppendFailure_FreshProjectorRecoversWithoutDuplicateAndClosesStream()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "done" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "done")).Should().BeTrue();

        var stream = new RecordingEventStream { ThrowAfterAppend = true };
        var failedProjector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance);
        var act = () => failedProjector.ProjectPendingAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle();

        stream.ThrowAfterAppend = false;
        var recoveredProjector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance);
        await recoveredProjector.ProjectPendingAsync();
        await recoveredProjector.ProjectPendingAsync();

        stream.Events.Should().ContainSingle();
        stream.Events[0].Type.Should().Be(EventTypes.RunCompleted);
        stream.CompletedRunIds.Should().ContainSingle().Which.Should().Be(run.ToString());
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projectors_RacingProjection_AppendTheStoredWinnerOnce()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "done" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "done")).Should().BeTrue();

        var stream = new RecordingEventStream();
        var first = new TerminalOutcomeProjector(store, stream, NullLogger<TerminalOutcomeProjector>.Instance);
        var second = new TerminalOutcomeProjector(store, stream, NullLogger<TerminalOutcomeProjector>.Instance);

        await Task.WhenAll(first.ProjectPendingAsync(), second.ProjectPendingAsync());

        stream.Events.Should().ContainSingle().Which.Type.Should().Be(EventTypes.RunCompleted);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_RestartReusesExistingRichTerminalEvent()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var payload = new
        {
            reason = "workflow_start_failed",
            detail = "binding failed",
            retryable = true,
        };
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, payload, DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "workflow_start_failed")).Should().BeTrue();

        var stream = new RecordingEventStream();
        await stream.AppendTerminalOutcomeAsync(run.ToString(), outcome);
        var restartedProjector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance);

        await restartedProjector.ProjectPendingAsync();

        stream.Events.Should().ContainSingle().Which.Payload.Should().BeEquivalentTo(outcome.Payload);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_ReopenedSameStatus_AppendsTheCurrentGenerationWinner()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var first = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "retriable_failure" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, first, "retriable_failure")).Should().BeTrue();

        var stream = new RecordingEventStream();
        await stream.AppendTerminalOutcomeAsync(run.ToString(), first);
        await store.MarkTerminalOutcomeProjectedAsync(run, 1);
        (await store.TryReopenTerminalToInProgressAsync(run)).Should().BeTrue();

        var current = (await store.GetAsync(run))!;
        var second = first with
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ExpectedLifecycleGeneration = current.LifecycleGeneration,
        };
        (await store.TrySetTerminalOutcomeAsync(run, second, "retriable_failure")).Should().BeTrue();

        await new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance).ProjectPendingAsync();

        stream.Events.Where(evt => evt.Type == EventTypes.RunFailed).Should().HaveCount(2);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateProviderAfterReopen_DoesNotBindPreservedPriorGenerationTerminal()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var directory = Path.Combine(Path.GetTempPath(), "aw-terminal-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(directory, "agentweaver.db"),
            }).Build();
            CreateRunEventsTable(Path.Combine(directory, "memory.db"));
            var stream = new SqliteRunEventStream(config);
            var live = new RunStreamStore(stream);
            var entry = live.Create(run.ToString(), "test");
            var first = TerminalRunOutcome.Create(
                RunStatus.Failed, EventTypes.RunFailed, new { reason = "retriable_failure" },
                DateTimeOffset.UtcNow, 1);

            (await store.TrySetTerminalOutcomeAsync(run, first, "retriable_failure")).Should().BeTrue();
            var historical = await stream.AppendTerminalOutcomeAsync(run.ToString(), first);
            entry.RecordDurable(historical);
            await store.MarkTerminalOutcomeProjectedAsync(run, 1);
            (await store.TryReopenTerminalToInProgressAsync(run)).Should().BeTrue();

            var reopened = (await store.GetAsync(run))!;
            live.Reopen(run.ToString(), reopened.LifecycleGeneration);
            var current = first with
            {
                OccurredAt = DateTimeOffset.UtcNow,
                ExpectedLifecycleGeneration = reopened.LifecycleGeneration,
            };
            (await store.TrySetTerminalOutcomeAsync(run, current, "retriable_failure")).Should().BeTrue();

            var projector = new TerminalOutcomeProjector(
                store, stream, NullLogger<TerminalOutcomeProjector>.Instance, live);
            (await projector.TryProjectExistingTerminalAsync(
                run, reopened.LifecycleGeneration, historical, targetStreamStore: live)).Should().BeFalse(
                "the preserved generation-one event is history, not a generation-two winner");
            (await store.GetUnprojectedTerminalOutcomesAsync())
                .Should().ContainSingle(outcome => outcome.LifecycleGeneration == reopened.LifecycleGeneration);
            entry.IsCompleted.Should().BeFalse();

            await projector.ProjectPendingAsync();
            await projector.ProjectPendingAsync();

            var events = await new SqliteRunEventStream(config).GetPersistedEventsAsync(run.ToString());
            events.Where(evt => evt.Type == EventTypes.RunFailed).Select(evt => evt.Sequence).Should().Equal(
                historical.Sequence, historical.Sequence + 1);
            entry.GetSnapshotSince(0).Events.Select(evt => evt.Sequence).Should().Equal(
                historical.Sequence, historical.Sequence + 1);
            entry.IsCompleted.Should().BeTrue("only the generation-two terminal closes the reopened stream");
            (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Projector_AdoptsOnlyCompatibleLegacyTerminalEvent()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        await using (var connection = await testDb.Db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE runs SET status = 'failed', ended_at = $endedAt WHERE run_id = $runId;";
            command.Parameters.AddWithValue("$endedAt", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$runId", run.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var stream = new RecordingEventStream();
        await stream.AppendAsync(run.ToString(), new RunEvent(0, EventTypes.RunFailed,
            new { reason = "legacy-rich-reason" }, DateTimeOffset.UtcNow));
        var projector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance);

        await projector.AdoptCompatibleLegacyOutcomesAsync();
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
        stream.Events.Should().ContainSingle(evt => evt.Type == EventTypes.RunFailed);

        await using var verify = await testDb.Db.OpenConnectionAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT event_type, payload_json, projected_at FROM terminal_run_outcomes WHERE run_id = $runId;";
        verifyCommand.Parameters.AddWithValue("$runId", run.ToString());
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(EventTypes.RunFailed);
        reader.GetString(1).Should().Contain("legacy-rich-reason");
        reader.IsDBNull(2).Should().BeFalse();
    }

    [Fact]
    public async Task RecoveryService_ExecuteAsync_YieldsBeforeRecoveryAndThenProjects()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "startup_recovery" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "startup_recovery")).Should().BeTrue();

        var projectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new RecordingEventStream { TerminalAppendStarted = projectionStarted };
        var service = new TerminalOutcomeRecoveryService(
            new TerminalOutcomeProjector(store, stream, NullLogger<TerminalOutcomeProjector>.Instance),
            NullLogger<TerminalOutcomeRecoveryService>.Instance);
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        using var stopping = new CancellationTokenSource();

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var executeAsync = typeof(TerminalOutcomeRecoveryService).GetMethod(
                "ExecuteAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var execution = (Task)executeAsync!.Invoke(service, [stopping.Token])!;

            execution.IsCompleted.Should().BeFalse();
            projectionStarted.Task.IsCompleted.Should().BeFalse();
            context.PendingCount.Should().Be(1);

            SynchronizationContext.SetSynchronizationContext(previousContext);
            context.RunOne();
            await projectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stream.Events.Should().ContainSingle(evt => evt.Type == EventTypes.RunFailed);
            stopping.Cancel();
            var cancelled = async () => await execution;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            stopping.Cancel();
        }
    }

    [Fact]
    public async Task Projector_StaleGenerationDoesNotCloseReopenedTerminalStream()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "old")).Should().BeTrue();
        await using (var connection = await testDb.Db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE runs SET status = 'in_progress', ended_at = NULL, lifecycle_generation = 2 WHERE run_id = $runId;";
            command.Parameters.AddWithValue("$runId", run.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var stream = new RecordingEventStream();
        var projector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance);
        await projector.ProjectPendingAsync();

        stream.Events.Should().BeEmpty();
        stream.CompletedRunIds.Should().BeEmpty();
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Projector_InterleavedReopenAfterInitialRead_DoesNotCloseNewGenerationStream()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old" },
            DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "old")).Should().BeTrue();

        var stream = new RecordingEventStream
        {
            TerminalAppendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueTerminalAppend = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var live = new RunStreamStore(stream);
        var entry = live.Create(run.ToString(), "test");
        var projector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance, live);

        var projection = projector.ProjectPendingAsync();
        await stream.TerminalAppendStarted.Task;
        await using (var connection = await testDb.Db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE runs SET status = 'in_progress', ended_at = NULL, lifecycle_generation = 2 WHERE run_id = $runId;";
            command.Parameters.AddWithValue("$runId", run.ToString());
            await command.ExecuteNonQueryAsync();
        }
        live.Reopen(run.ToString(), 2);
        stream.ContinueTerminalAppend.TrySetResult();
        await projection;

        entry.IsCompleted.Should().BeFalse();
        entry.GetSnapshotSince(0).Events.Should().BeEmpty();
        stream.Events.Should().ContainSingle(evt => evt.Type == EventTypes.RunFailed);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AssembleReady_ProjectsCanonicalPayloadOnce_AcrossStreamRestart()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var run = await InsertInProgressAsync(store);
        var directory = Path.Combine(Path.GetTempPath(), "aw-terminal-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(directory, "agentweaver.db"),
            }).Build();
            CreateRunEventsTable(Path.Combine(directory, "memory.db"));
            var stream = new SqliteRunEventStream(config);
            var live = new RunStreamStore(stream);
            var entry = live.Create(run.ToString(), "test");

            (await store.SetAssembleReadyAsync(
                run, "tree-winner", "agent/assembly", "winner diff", 3, DateTimeOffset.UtcNow)).Should().BeTrue();
            await new TerminalOutcomeProjector(
                store, stream, NullLogger<TerminalOutcomeProjector>.Instance, live).ProjectPendingAsync();
            await new TerminalOutcomeProjector(
                store, new SqliteRunEventStream(config), NullLogger<TerminalOutcomeProjector>.Instance, live)
                .ProjectPendingAsync();

            var persisted = await new SqliteRunEventStream(config).GetPersistedEventsAsync(run.ToString());
            var terminal = persisted.Where(evt => evt.Type == EventTypes.RunAssembleReady)
                .Should().ContainSingle().Subject;
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(terminal.Payload);
            payload.GetProperty("treeHash").GetString().Should().Be("tree-winner");
            payload.GetProperty("worktreeBranch").GetString().Should().Be("agent/assembly");
            payload.GetProperty("diff").GetString().Should().Be("winner diff");
            payload.GetProperty("stepCount").GetInt32().Should().Be(3);
            var liveEvent = entry.GetSnapshotSince(0).Events.Should().ContainSingle().Subject;
            System.Text.Json.JsonSerializer.Serialize(liveEvent.Payload)
                .Should().Be(System.Text.Json.JsonSerializer.Serialize(terminal.Payload));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateRunEventsTable(string memoryDbPath)
    {
        using var connection = new SqliteConnection($"Data Source={memoryDbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "RunEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX "IX_RunEvents_RunId_Sequence" ON "RunEvents" ("RunId", "Sequence");
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<RunId> InsertInProgressAsync(SqliteRunStore store)
    {
        var run = RunId.New();
        await store.InsertAsync(new Run
        {
            Id = run,
            RepositoryPath = "test",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "terminal outcome test",
            SubmittingUser = "test",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });
        return run;
    }

    private sealed class RecordingEventStream : IRunEventStream
    {
        public List<RunEvent> Events { get; } = [];
        public List<string> CompletedRunIds { get; } = [];
        public bool ThrowAfterAppend { get; set; }
        public TaskCompletionSource? TerminalAppendStarted { get; init; }
        public TaskCompletionSource? ContinueTerminalAppend { get; init; }
        private readonly Dictionary<(string RunId, int Generation), RunEvent> _terminalOutcomes = [];

        public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
        {
            var persisted = evt with { Sequence = Events.Count + 1 };
            Events.Add(persisted);
            if (ThrowAfterAppend)
                throw new InvalidOperationException("simulated failure after durable append");
            return ValueTask.FromResult(persisted.Sequence);
        }

        public async Task<RunEvent> AppendTerminalOutcomeAsync(
            string runId,
            TerminalRunOutcome outcome,
            CancellationToken ct = default)
        {
            TerminalAppendStarted?.TrySetResult();
            if (ContinueTerminalAppend is not null)
                await ContinueTerminalAppend.Task.WaitAsync(ct);
            var key = (runId, outcome.ExpectedLifecycleGeneration);
            if (_terminalOutcomes.TryGetValue(key, out var existing))
                return existing;

            var persisted = outcome.ToRunEvent(Events.Count + 1);
            Events.Add(persisted);
            _terminalOutcomes.Add(key, persisted);
            if (ThrowAfterAppend)
                throw new InvalidOperationException("simulated failure after durable append");
            return persisted;
        }

        public async IAsyncEnumerable<RunEvent> SubscribeAsync(
            string runId, int fromSequence = 0, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var evt in Events.Where(evt => evt.Sequence > fromSequence))
            {
                ct.ThrowIfCancellationRequested();
                yield return evt;
            }
            await Task.CompletedTask;
        }

        public ValueTask CompleteAsync(string runId, CancellationToken ct = default)
        {
            CompletedRunIds.Add(runId);
            return ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(
            string runId, int fromSequence = 0, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunEvent>>(
                Events.Where(evt => evt.Sequence > fromSequence).ToArray());
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _continuations = [];

        public int PendingCount => _continuations.Count;

        public override void Post(SendOrPostCallback d, object? state) =>
            _continuations.Enqueue((d, state));

        public void RunOne()
        {
            if (!_continuations.TryDequeue(out var continuation))
                throw new InvalidOperationException("No queued continuation.");

            continuation.Callback(continuation.State);
        }
    }
}
