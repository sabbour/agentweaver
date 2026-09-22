using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Domain;
using FluentAssertions;

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
