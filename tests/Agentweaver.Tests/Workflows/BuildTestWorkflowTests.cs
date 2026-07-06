using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Workflows;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

public sealed class BuildTestWorkflowTests
{
    [Fact]
    public void Loader_AcceptsBuildTestNodeType()
    {
        var yaml = """
        id: build-test-sample
        name: Build Test Sample
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
          - id: build-test
            type: build_test
            label: Build & Test
            agent: qa-engineer
          - id: done
            type: terminal
            label: Done
          - id: declined
            type: terminal
            label: Declined
        edges:
          - from: implement
            to: build-test
          - from: build-test
            to: done
            when: approved
          - from: build-test
            to: implement
            when: request-changes
          - from: build-test
            to: declined
            when: declined
        """;

        var result = WorkflowDefinitionLoader.Load(yaml, "test");

        result.IsValid.Should().BeTrue(result.Error);
        var node = result.Definition!.Nodes.Single(n => n.Id == "build-test");
        node.Type.Should().Be(WorkflowNodeType.BuildTest);
        node.Prompt.Should().BeNull();
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition).Should().BeEmpty();
    }

    [Fact]
    public async Task BuildTestExecutor_UsesCannedPromptAndParsesApprovedVerdict()
    {
        var runner = new TestFileEditAgentRunner();
        var executor = new BuildTestTurnExecutor(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub()),
            new FixedInstallationScopeStub(),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: new FakeWorkflowAgentFactory(runner));

        var decision = await executor.HandleAsync(new AgentTurnOutput(
            RunId: "build-test-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/build-test-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None);

        decision.Approved.Should().BeTrue();
        BuildTestTurnExecutor.CannedPrompt.Should().Contain("ALL tests");
        BuildTestTurnExecutor.CannedPrompt.Should().Contain("start_preview(port=PORT)");
    }
}
