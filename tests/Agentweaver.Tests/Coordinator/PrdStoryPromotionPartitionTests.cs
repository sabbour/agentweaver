using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;

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

    private sealed class TestStoryIndependenceClassifier(
        Func<StoryIndependenceClassificationContext, StoryIndependenceClassificationResult?> impl)
        : IStoryIndependenceClassifier
    {
        public int CallCount { get; private set; }

        public Task<StoryIndependenceClassificationResult?> ClassifyAsync(
            StoryIndependenceClassificationContext context,
            CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(impl(context));
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
