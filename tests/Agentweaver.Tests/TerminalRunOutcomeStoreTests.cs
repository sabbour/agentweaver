using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
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
        await stream.AppendAsync(run.ToString(), outcome.ToRunEvent());
        var restartedProjector = new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance);

        await restartedProjector.ProjectPendingAsync();

        stream.Events.Should().ContainSingle().Which.Payload.Should().BeEquivalentTo(outcome.Payload);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
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

        public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
        {
            var persisted = evt with { Sequence = Events.Count + 1 };
            Events.Add(persisted);
            if (ThrowAfterAppend)
                throw new InvalidOperationException("simulated failure after durable append");
            return ValueTask.FromResult(persisted.Sequence);
        }

        public Task AppendTerminalOutcomeAsync(
            string runId,
            TerminalRunOutcome outcome,
            CancellationToken ct = default)
        {
            if (!Events.Any(evt => evt.Type == outcome.EventType
                && System.Text.Json.JsonSerializer.Serialize(evt.Payload) == outcome.Payload.GetRawText()))
            {
                var persisted = outcome.ToRunEvent(Events.Count + 1);
                Events.Add(persisted);
                if (ThrowAfterAppend)
                    throw new InvalidOperationException("simulated failure after durable append");
            }
            return Task.CompletedTask;
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
}
