using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.ReviewPolicies;

public sealed class RubberduckTurnExecutorTests
{
    [Fact]
    public async Task HandleAsync_FakeRubberduckPass_ProducesApprovedDecision()
    {
        var config = new ConfigurationBuilder().Build();
        var copilotFactory = new GitHubCopilotClientFactory(
            config,
            new NullGitHubTokenStore(),
            new FixedInstallationScopeStub());
        var runner = new TestFileEditAgentRunner();

        var executor = new RubberduckTurnExecutor(
            copilotFactory,
            new FixedInstallationScopeStub(),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: new FakeWorkflowAgentFactory(runner));

        var input = new AgentTurnOutput(
            RunId: "rubberduck-test-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false);

        var decision = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        decision.Approved.Should().BeTrue();
        decision.RequestChanges.Should().BeFalse();
    }
}
