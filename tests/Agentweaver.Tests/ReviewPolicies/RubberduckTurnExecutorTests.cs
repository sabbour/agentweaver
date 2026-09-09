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
            config, new FixedGitHubCopilotCapabilityCredentialProvider());
        var runner = new TestFileEditAgentRunner();
        var agentFactory = new FakeWorkflowAgentFactory(runner);

        var executor = new RubberduckTurnExecutor(
            copilotFactory,
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: agentFactory);

        var input = new AgentTurnOutput(
            RunId: "rubberduck-test-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ModelSource: ModelSource.Byok.ToApiString(),
            ModelId: "byok-model",
            ByokProviderFingerprint: "byok-fingerprint");

        var decision = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        decision.Approved.Should().BeTrue();
        decision.RequestChanges.Should().BeFalse();
        agentFactory.LastRubberduckAgent!.ProviderModelSource.Should().Be(ModelSource.Byok);
        agentFactory.LastRubberduckAgent.ByokProviderFingerprint.Should().Be("byok-fingerprint");
    }

    [Fact]
    public async Task HandleAsync_ProviderFailure_IsNotConvertedToPass()
    {
        var agentFactory = new FakeWorkflowAgentFactory(new TestFileEditAgentRunner())
        {
            ProviderFailureRole = FakeAgentRole.Rubberduck,
        };
        var executor = new RubberduckTurnExecutor(
            new GitHubCopilotClientFactory(
                new ConfigurationBuilder().Build(),
                new FixedGitHubCopilotCapabilityCredentialProvider()),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: agentFactory);

        var act = () => executor.HandleAsync(new AgentTurnOutput(
            RunId: "rubberduck-provider-failure",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ModelSource: ModelSource.Byok.ToApiString(),
            ByokProviderFingerprint: "byok-fingerprint"),
            context: null!,
            CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<AgentProviderException>();
    }

    [Fact]
    public async Task HandleAsync_RemoteProviderChange_IsProjectedAsProviderFailure()
    {
        var agentFactory = new FakeWorkflowAgentFactory(new TestFileEditAgentRunner())
        {
            InfrastructureProviderFailureRole = FakeAgentRole.Rubberduck,
        };
        var executor = new RubberduckTurnExecutor(
            new GitHubCopilotClientFactory(
                new ConfigurationBuilder().Build(),
                new FixedGitHubCopilotCapabilityCredentialProvider()),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: agentFactory);

        var act = () => executor.HandleAsync(new AgentTurnOutput(
            RunId: "rubberduck-remote-provider-change",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ModelSource: ModelSource.Byok.ToApiString(),
            ByokProviderFingerprint: "byok-fingerprint"),
            context: null!,
            CancellationToken.None).AsTask();

        var failure = (await act.Should().ThrowAsync<AgentProviderException>()).Which;
        failure.ErrorCode.Should().Be("model_provider_changed");
        failure.ModelSource.Should().Be(ModelSource.Byok);
    }
}
