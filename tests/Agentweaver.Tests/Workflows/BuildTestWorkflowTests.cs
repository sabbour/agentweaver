using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
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
        // The model-mediated preview paragraph (start_preview_process -> observe_bound_port ->
        // start_preview) was intentionally removed from the CannedPrompt: preview is now provisioned
        // by the deterministic platform-owned PreviewStep after build-test, not by the agent.
        BuildTestTurnExecutor.CannedPrompt.Should().NotContain("start_preview");
    }

    [Fact]
    public async Task BuildTestExecutor_PassesProjectApiContextAndRealRunIdToAgentSetup()
    {
        var factory = new CapturingBuildTestAgentFactory();
        var executor = new BuildTestTurnExecutor(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub()),
            new FixedInstallationScopeStub(),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: factory,
            agentId: "qa-engineer",
            projectId: "project-from-constructor",
            apiBaseUrl: "https://agentweaver.example",
            apiKey: "test-api-key");

        var decision = await executor.HandleAsync(new AgentTurnOutput(
            RunId: "real-persisted-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/real-persisted-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            SubmittingUser: "owner",
            ProjectId: "project-from-input",
            AgentName: "worker"),
            context: null!,
            CancellationToken.None);

        decision.Approved.Should().BeTrue();
        factory.Agent.SetupRunId.Should().Be("real-persisted-run",
            because: "start_preview must target the persisted run whose SandboxClaim and run options are keyed by the real run id");
        factory.Agent.SetupProjectId.Should().Be("project-from-constructor");
        factory.Agent.SetupAgentName.Should().Be("qa-engineer");
        factory.Agent.SetupApiBaseUrl.Should().Be("https://agentweaver.example");
        factory.Agent.SetupApiKey.Should().Be("test-api-key");
    }

    [Fact]
    public async Task BuildTestExecutor_FallsBackToInputProjectId_WhenConstructorProjectIdMissing()
    {
        var factory = new CapturingBuildTestAgentFactory();
        var executor = new BuildTestTurnExecutor(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub()),
            new FixedInstallationScopeStub(),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: factory,
            apiBaseUrl: "https://agentweaver.example");

        await executor.HandleAsync(new AgentTurnOutput(
            RunId: "real-persisted-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "branch",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ProjectId: "project-from-input"),
            context: null!,
            CancellationToken.None);

        factory.Agent.SetupProjectId.Should().Be("project-from-input");
        factory.Agent.SetupRunId.Should().Be("real-persisted-run");
    }

    [Fact]
    public async Task BuildTestExecutor_total_wall_clock_timeout_fails_instead_of_livelocking()
    {
        var executor = new BuildTestTurnExecutor(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub()),
            new FixedInstallationScopeStub(),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            agentFactory: new HangingBuildTestAgentFactory(),
            totalTimeout: TimeSpan.FromMilliseconds(50),
            stallTimeout: TimeSpan.FromSeconds(5));

        var act = () => executor.HandleAsync(new AgentTurnOutput(
            RunId: "build-test-timeout",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 0,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "integration",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None).AsTask();

        var exception = await act.Should().ThrowAsync<WorkflowAgentInfrastructureException>();
        exception.Which.Reason.Should().Be(BuildTestTurnExecutor.WallClockTimeoutReason);
    }

    [Fact]
    public void BuildTestVerdictParser_AcceptsCurlPrefixedApprovedVerdict()
    {
        var parsed = BuildTestTurnExecutor.TryParseVerdict(
            "curlAPPROVED\nBuild passed and preview responded with HTTP 200.",
            out var decision);

        parsed.Should().BeTrue();
        decision.Approved.Should().BeTrue();
        decision.RequestChanges.Should().BeFalse();
    }

    [Theory]
    [InlineData("not APPROVED")]
    [InlineData("UNAPPROVED")]
    [InlineData("preAPPROVED")]
    public void BuildTestVerdictParser_DoesNotTreatUnsafeApprovalSubstringsAsApproved(string response)
    {
        var parsed = BuildTestTurnExecutor.TryParseVerdict(response, out var decision);

        parsed.Should().BeFalse();
        decision.Approved.Should().BeFalse();
    }
}

internal sealed class CapturingBuildTestAgentFactory : IWorkflowAgentFactory
{
    public CapturingWorkflowTurnAgent Agent { get; } = new();

    public IWorkflowTurnAgent CreateWorkerAgent() => Agent;
    public IWorkflowTurnAgent CreateRaiAgent() => Agent;
    public IWorkflowTurnAgent CreateRubberduckAgent() => Agent;
    public IWorkflowTurnAgent CreateBuildTestAgent() => Agent;
    public IWorkflowTurnAgent CreateScribeAgent() => Agent;
}

internal sealed class CapturingWorkflowTurnAgent : IWorkflowTurnAgent
{
    public string? SetupRunId { get; private set; }
    public string? SetupProjectId { get; private set; }
    public string? SetupAgentName { get; private set; }
    public string? SetupApiBaseUrl { get; private set; }
    public string? SetupApiKey { get; private set; }

    public Task SetupAsync(
        string workingDirectory,
        string repositoryPath,
        string runId,
        string? modelId,
        string? systemPromptContext,
        ChannelWriter<RunEvent>? streamWriter,
        string? projectId,
        string? agentName,
        string? apiBaseUrl,
        string? apiKey,
        CancellationToken ct,
        string? userId = null)
    {
        SetupRunId = runId;
        SetupProjectId = projectId;
        SetupAgentName = agentName;
        SetupApiBaseUrl = apiBaseUrl;
        SetupApiKey = apiKey;
        return Task.CompletedTask;
    }

    public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct) =>
        Task.FromResult("APPROVED - build and test gate passed.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class HangingBuildTestAgentFactory : IWorkflowAgentFactory
{
    public IWorkflowTurnAgent CreateWorkerAgent() => new HangingBuildTestAgent();
    public IWorkflowTurnAgent CreateRaiAgent() => new HangingBuildTestAgent();
    public IWorkflowTurnAgent CreateRubberduckAgent() => new HangingBuildTestAgent();
    public IWorkflowTurnAgent CreateBuildTestAgent() => new HangingBuildTestAgent();
    public IWorkflowTurnAgent CreateScribeAgent() => new HangingBuildTestAgent();
}

internal sealed class HangingBuildTestAgent : IWorkflowTurnAgent
{
    public Task SetupAsync(
        string workingDirectory,
        string repositoryPath,
        string runId,
        string? modelId,
        string? systemPromptContext,
        ChannelWriter<RunEvent>? streamWriter,
        string? projectId,
        string? agentName,
        string? apiBaseUrl,
        string? apiKey,
        CancellationToken ct,
        string? userId = null) => Task.CompletedTask;

    public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        return "";
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
