using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>
/// Cross-connection coverage for the terminal-outcome transaction. These remain compile-visible
/// when Docker is unavailable and execute with the Postgres integration fixture when it is present.
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class TerminalRunOutcomePostgresTests(PostgresFixture pg)
{
    [PostgresFact]
    public async Task SeparateStores_RacingTerminalOutcomes_ChooseOneRichWinner()
    {
        var first = new EfRunStore(pg.Factory);
        var second = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(first);
        var completed = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "first" },
            DateTimeOffset.UtcNow, 1);
        var failed = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "second" },
            DateTimeOffset.UtcNow, 1);

        var results = await Task.WhenAll(
            first.TrySetTerminalOutcomeAsync(run, completed, "first"),
            second.TrySetTerminalOutcomeAsync(run, failed, "second"));

        results.Count(changed => changed).Should().Be(1);
        var persisted = (await first.GetAsync(run))!;
        persisted.Status.Should().Be(results[0] ? RunStatus.Completed : RunStatus.Failed);
        persisted.Result.Should().Be(results[0] ? "first" : "second");
        var winner = (await second.GetUnprojectedTerminalOutcomesAsync()).Should().ContainSingle().Subject;
        winner.Outcome.EventType.Should().Be(results[0] ? EventTypes.RunCompleted : EventTypes.RunFailed);
        winner.Outcome.Payload.GetProperty(results[0] ? "result" : "reason").GetString()
            .Should().Be(results[0] ? "first" : "second");
    }

    [PostgresFact]
    public async Task SeparateStores_StaleGenerationCannotTerminalizeReopenedRun()
    {
        var first = new EfRunStore(pg.Factory);
        var second = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(first);
        await first.UpdateReviewReadyAsync(run, "tree", "diff", 1);
        (await second.TryTransitionReviewToInProgressAsync(run)).Should().BeTrue();

        var stale = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old_generation" },
            DateTimeOffset.UtcNow, 1);
        (await first.TrySetTerminalOutcomeAsync(run, stale, "old_generation")).Should().BeFalse();

        var persisted = (await second.GetAsync(run))!;
        persisted.Status.Should().Be(RunStatus.InProgress);
        persisted.LifecycleGeneration.Should().Be(2);
        (await second.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task AssembleReady_RejectsCompetingTerminalOutcome()
    {
        var first = new EfRunStore(pg.Factory);
        var second = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(first);

        (await first.SetAssembleReadyAsync(
            run, "tree-winner", "agent/assembly", "winner diff", 3, DateTimeOffset.UtcNow)).Should().BeTrue();
        (await second.TrySetTerminalOutcomeAsync(
            run,
            TerminalRunOutcome.Create(
                RunStatus.Failed, EventTypes.RunFailed, new { reason = "loser" }, DateTimeOffset.UtcNow, 1),
            "loser")).Should().BeFalse();

        var persisted = (await second.GetAsync(run))!;
        persisted.Status.Should().Be(RunStatus.AssembleReady);
        var winner = (await second.GetUnprojectedTerminalOutcomesAsync())
            .Where(outcome => outcome.RunId == run)
            .Should().ContainSingle().Subject;
        winner.Outcome.EventType.Should().Be(EventTypes.RunAssembleReady);
    }

    [PostgresFact]
    public async Task IndependentProjectors_RaceToOneDurableTerminalProjection()
    {
        var store = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed,
            EventTypes.RunFailed,
            new { reason = "rich_failure", retryable = true },
            DateTimeOffset.UtcNow,
            1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "rich_failure")).Should().BeTrue();

        var first = new TerminalOutcomeProjector(
            new EfRunStore(pg.Factory),
            new EfRunEventStream(pg.Factory),
            NullLogger<TerminalOutcomeProjector>.Instance);
        var second = new TerminalOutcomeProjector(
            new EfRunStore(pg.Factory),
            new EfRunEventStream(pg.Factory),
            NullLogger<TerminalOutcomeProjector>.Instance);

        await Task.WhenAll(first.ProjectPendingAsync(), second.ProjectPendingAsync());

        var events = await new EfRunEventStream(pg.Factory).GetPersistedEventsAsync(run.ToString());
        events.Where(evt => evt.Type == EventTypes.RunFailed).Should().ContainSingle();
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task TerminalProjection_IdenticalPayloadAcrossReopenedGenerations_PersistsOnePerGeneration()
    {
        var runId = $"run-postgres-reopened-{Guid.NewGuid():N}";
        var stream = new EfRunEventStream(pg.Factory);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "retriable_failure" }, DateTimeOffset.UtcNow, 1);

        await stream.AppendTerminalOutcomeAsync(runId, outcome);
        await stream.AppendTerminalOutcomeAsync(runId, outcome with { ExpectedLifecycleGeneration = 2 });
        await stream.AppendTerminalOutcomeAsync(runId, outcome with { ExpectedLifecycleGeneration = 2 });

        var events = await new EfRunEventStream(pg.Factory).GetPersistedEventsAsync(runId);
        events.Where(evt => evt.Type == EventTypes.RunFailed).Should().HaveCount(2);
    }

    [PostgresFact]
    public async Task ReconnectAfterReopen_ReplaysHistoricalTerminalButClosesOnlyCurrentLifecycle()
    {
        var store = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(store);
        var firstOutcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old" }, DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, firstOutcome, "old")).Should().BeTrue();
        await new TerminalOutcomeProjector(
            store, new EfRunEventStream(pg.Factory), NullLogger<TerminalOutcomeProjector>.Instance).ProjectPendingAsync();
        (await store.TryReopenTerminalToInProgressAsync(run)).Should().BeTrue();

        var stream = new EfRunEventStream(pg.Factory);
        var observed = new List<RunEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in stream.SubscribeAsync(run.ToString(), ct: cancellation.Token))
                observed.Add(evt);
        }, cancellation.Token);
        await Task.Delay(500, cancellation.Token);
        subscriber.IsCompleted.Should().BeFalse();

        await stream.AppendAsync(run.ToString(), new RunEvent(0, "agent.message.delta", new { delta = "continued" }));
        var current = (await store.GetAsync(run))!;
        var secondOutcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "current" },
            DateTimeOffset.UtcNow, current.LifecycleGeneration);
        (await store.TrySetTerminalOutcomeAsync(run, secondOutcome, "current")).Should().BeTrue();
        await new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance).ProjectPendingAsync();
        await subscriber;

        observed.Select(evt => evt.Type).Should().ContainInOrder(
            EventTypes.RunFailed, "agent.message.delta", EventTypes.RunCompleted);
    }

    [PostgresFact]
    public async Task ReconnectAfterTerminalOutboxWindow_WaitsForCurrentProjectedTerminalSequence()
    {
        var store = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(store);
        var firstOutcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old" }, DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, firstOutcome, "old")).Should().BeTrue();
        await new TerminalOutcomeProjector(
            store, new EfRunEventStream(pg.Factory), NullLogger<TerminalOutcomeProjector>.Instance).ProjectPendingAsync();
        (await store.TryReopenTerminalToInProgressAsync(run)).Should().BeTrue();

        var current = (await store.GetAsync(run))!;
        var currentOutcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "current" },
            DateTimeOffset.UtcNow, current.LifecycleGeneration);
        (await store.TrySetTerminalOutcomeAsync(run, currentOutcome, "current")).Should().BeTrue();

        var stream = new EfRunEventStream(pg.Factory);
        var observed = new List<RunEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in stream.SubscribeAsync(run.ToString(), ct: cancellation.Token))
                observed.Add(evt);
        }, cancellation.Token);

        await Task.Delay(500, cancellation.Token);
        observed.Select(evt => evt.Type).Should().Equal(EventTypes.RunFailed);
        subscriber.IsCompleted.Should().BeFalse(
            "the terminal outbox row has not projected the current-generation event");

        await new TerminalOutcomeProjector(
            store, stream, NullLogger<TerminalOutcomeProjector>.Instance).ProjectPendingAsync();
        await subscriber;

        observed.Select(evt => evt.Type).Should().Equal(EventTypes.RunFailed, EventTypes.RunCompleted);
        observed.Count(evt => evt.Type == EventTypes.RunCompleted).Should().Be(1);
    }

    [PostgresFact]
    public async Task DuplicateProviderTerminal_ReconnectsAtLinkedCurrentGenerationSequence()
    {
        var store = new EfRunStore(pg.Factory);
        var run = await InsertInProgressAsync(store);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "outbox" }, DateTimeOffset.UtcNow, 1);
        (await store.TrySetTerminalOutcomeAsync(run, outcome, "outbox")).Should().BeTrue();

        var producer = new EfRunEventStream(pg.Factory);
        var canonical = new RunEvent(0, EventTypes.RunCompleted, new { result = "provider" });
        var sequence = await producer.AppendAsync(run.ToString(), canonical);
        (await producer.TryLinkTerminalOutcomeAsync(
            run.ToString(), outcome, canonical with { Sequence = sequence })).Should().BeTrue();
        await store.MarkTerminalOutcomeProjectedAsync(run, 1);

        var replayed = new List<RunEvent>();
        await foreach (var evt in new EfRunEventStream(pg.Factory).SubscribeAsync(run.ToString()))
            replayed.Add(evt);
        replayed.Should().ContainSingle().Which.Sequence.Should().Be(sequence);
        replayed.Should().ContainSingle().Which.Type.Should().Be(EventTypes.RunCompleted);
        (await store.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();
    }

    private static async Task<RunId> InsertInProgressAsync(EfRunStore store)
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
}
