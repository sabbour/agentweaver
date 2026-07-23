using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

public sealed class PrdStoryPromotionPartitionTests
{
    [Fact]
    public async Task PartitionStoriesAsync_PromotesWholeDependencyComponent_WhenClassifierSaysIndependent()
    {
        var classifier = new TestStoryIndependenceClassifier(_ =>
            new StoryIndependenceClassificationResult(true, "Separate pipeline service deliverable."));
        var executor = CreateExecutor(classifier);
        var input = new CoordinatorDraftInput("run-1", "proj-1", "Create an online shopping experience", "alice", "repo", null);
        var spec = CreateSpec("run-1", "proj-1", "Create an online shopping experience", "Storefront + services");
        var drafts = new[]
        {
            new CoordinatorOrchestratorExecutor.SubtaskDraft("storefront", "Create storefront", "Build storefront", "dev", "medium", "execution", "worktree", [2]),
            new CoordinatorOrchestratorExecutor.SubtaskDraft("pipeline", "Create pipeline service", "Build pipeline service", "dev", "medium", "execution", "worktree", []),
        };

        var result = await executor.PartitionStoriesAsync(input, spec, drafts, CancellationToken.None);

        result.PromotedIndices.Should().Equal(0, 1);
        result.InlineIndices.Should().BeEmpty();
        result.PromotionReasons[0].Should().Be("LLM judged this dependency component to be an independent deliverable.");
        result.PromotionReasons[1].Should().Be("Promoted with dependency component rooted at storefront.");
    }

    [Fact]
    public async Task PartitionStoriesAsync_FailsClosedToInline_WhenClassifierUnavailable()
    {
        var classifier = new TestStoryIndependenceClassifier(_ => null);
        var executor = CreateExecutor(classifier);
        var input = new CoordinatorDraftInput("run-1", "proj-1", "Create storefront", "alice", "repo", null);
        var spec = CreateSpec("run-1", "proj-1", "Create storefront", "Frontend and backend");
        var drafts = new[]
        {
            new CoordinatorOrchestratorExecutor.SubtaskDraft("frontend", "Create frontend", "Build UI", "dev", "medium", "execution", "worktree", []),
            new CoordinatorOrchestratorExecutor.SubtaskDraft("backend", "Create backend", "Build API", "dev", "medium", "execution", "worktree", []),
        };

        var result = await executor.PartitionStoriesAsync(input, spec, drafts, CancellationToken.None);

        result.PromotedIndices.Should().BeEmpty();
        result.InlineIndices.Should().Equal(0, 1);
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("kept inline: frontend, backend");
    }

    [Fact]
    public async Task PartitionStoriesAsync_ClassifiesIndependentComponentsInParallel_NearOldTimeoutBound()
    {
        var classifier = new TestStoryIndependenceClassifier(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8.2), ct);
            return new StoryIndependenceClassificationResult(true, "Separately shippable deliverable.");
        });
        var executor = CreateExecutor(classifier);
        var input = new CoordinatorDraftInput("run-1", "proj-1", "Ship two products", "alice", "repo", null);
        var spec = CreateSpec("run-1", "proj-1", "Ship two products", "Product A and Product B");
        var drafts = new[]
        {
            new CoordinatorOrchestratorExecutor.SubtaskDraft("product-a", "Ship product A", "Build product A", "dev", "medium", "execution", "worktree", []),
            new CoordinatorOrchestratorExecutor.SubtaskDraft("product-b", "Ship product B", "Build product B", "dev", "medium", "execution", "worktree", []),
        };

        var stopwatch = Stopwatch.StartNew();
        var result = await executor.PartitionStoriesAsync(input, spec, drafts, CancellationToken.None);
        stopwatch.Stop();

        result.PromotedIndices.Should().Equal(0, 1);
        classifier.MaxConcurrentCalls.Should().Be(2);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task PartitionStoriesAsync_PreservesComponentMapping_WhenParallelResultsCompleteOutOfOrder()
    {
        var classifier = new TestStoryIndependenceClassifier(async (context, ct) =>
        {
            var isProductA = context.ComponentStories.Single().StoryKey == "product-a";
            await Task.Delay(isProductA ? 150 : 20, ct);
            return new StoryIndependenceClassificationResult(
                isProductA,
                isProductA ? "Product A is separately shippable." : "Product B remains coupled.");
        });
        var executor = CreateExecutor(classifier);
        var input = new CoordinatorDraftInput("run-1", "proj-1", "Ship products", "alice", "repo", null);
        var spec = CreateSpec("run-1", "proj-1", "Ship products", "Product A and Product B");
        var drafts = new[]
        {
            new CoordinatorOrchestratorExecutor.SubtaskDraft("product-a", "Ship product A", "Build product A", "dev", "medium", "execution", "worktree", []),
            new CoordinatorOrchestratorExecutor.SubtaskDraft("product-b", "Ship product B", "Build product B", "dev", "medium", "execution", "worktree", []),
        };

        var result = await executor.PartitionStoriesAsync(input, spec, drafts, CancellationToken.None);

        result.PromotedIndices.Should().Equal(0);
        result.InlineIndices.Should().Equal(1);
        result.PromotionReasons[0].Should().Be("LLM judged this dependency component to be an independent deliverable.");
    }

    [Fact]
    public async Task PartitionStoriesAsync_SkipsPromotionEntirely_WhenOptInIsDisabled()
    {
        var classifier = new TestStoryIndependenceClassifier(_ =>
            new StoryIndependenceClassificationResult(true, "Would promote if asked."));
        var executor = CreateExecutor(classifier);
        var input = new CoordinatorDraftInput("run-1", "proj-1", "Create storefront", "alice", "repo", null);
        var spec = CreateSpec("run-1", "proj-1", "Create storefront", "Frontend and backend", allowTaskPromotion: false);
        var drafts = new[]
        {
            new CoordinatorOrchestratorExecutor.SubtaskDraft("storefront", "Create storefront [run]", "Build storefront", "dev", "medium", "execution", "worktree", []),
        };

        var result = await executor.PartitionStoriesAsync(input, spec, drafts, CancellationToken.None);

        result.PromotedIndices.Should().BeEmpty();
        result.InlineIndices.Should().Equal(0);
        classifier.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PartitionStoriesAsync_ExplicitInlineOverride_WinsWithoutClassifierCall()
    {
        var classifier = new TestStoryIndependenceClassifier(_ =>
            new StoryIndependenceClassificationResult(true, "Would promote if asked."));
        var executor = CreateExecutor(classifier);
        var input = new CoordinatorDraftInput("run-1", "proj-1", "Create storefront", "alice", "repo", null);
        var spec = CreateSpec("run-1", "proj-1", "Create storefront", "Frontend and backend");
        var drafts = new[]
        {
            new CoordinatorOrchestratorExecutor.SubtaskDraft("storefront", "Create storefront", "Build storefront", "dev", "medium", "execution", "worktree", [], PromotionOverride: "inline"),
        };

        var result = await executor.PartitionStoriesAsync(input, spec, drafts, CancellationToken.None);

        result.PromotedIndices.Should().BeEmpty();
        result.InlineIndices.Should().Equal(0);
        classifier.CallCount.Should().Be(0);
    }

    private static CoordinatorOrchestratorExecutor CreateExecutor(IStoryIndependenceClassifier classifier)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new CoordinatorOrchestratorExecutor(
            new ThrowingWorkflowAgentFactory(),
            new RunStreamStore(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLoggerFactory.Instance,
            classifier,
            new FakeAssemblyGateCodeClassifier(),
            "gpt-5-mini",
            "http://localhost",
            null);
    }

    private static OutcomeSpec CreateSpec(string runId, string projectId, string goal, string scope, bool allowTaskPromotion = true) => new()
    {
        ProjectId = projectId,
        CoordinatorRunId = runId,
        Goal = goal,
        DesiredOutcome = goal,
        Scope = scope,
        Assumptions = "None",
        Status = "confirmed",
        AllowTaskPromotion = allowTaskPromotion,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class TestStoryIndependenceClassifier : IStoryIndependenceClassifier
    {
        private readonly Func<StoryIndependenceClassificationContext, CancellationToken, Task<StoryIndependenceClassificationResult?>> _impl;
        private int _callCount;
        private int _concurrentCalls;
        private int _maxConcurrentCalls;

        public TestStoryIndependenceClassifier(
            Func<StoryIndependenceClassificationContext, StoryIndependenceClassificationResult?> impl)
            : this((context, _) => Task.FromResult(impl(context)))
        {
        }

        public TestStoryIndependenceClassifier(
            Func<StoryIndependenceClassificationContext, CancellationToken, Task<StoryIndependenceClassificationResult?>> impl)
        {
            _impl = impl;
        }

        public int CallCount => _callCount;
        public int MaxConcurrentCalls => _maxConcurrentCalls;

        public async Task<StoryIndependenceClassificationResult?> ClassifyAsync(
            StoryIndependenceClassificationContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            var concurrent = Interlocked.Increment(ref _concurrentCalls);
            UpdateMaximum(ref _maxConcurrentCalls, concurrent);
            try
            {
                return await _impl(context, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    private sealed class ThrowingWorkflowAgentFactory : IWorkflowAgentFactory
    {
        public IWorkflowTurnAgent CreateWorkerAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateRaiAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateRubberduckAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateBuildTestAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateScribeAgent() => throw new NotSupportedException();
    }
}
