using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Model-provider bookkeeping regressions on the pod-per-run AgentHost launch path:
///
/// <list type="bullet">
/// <item>The connection-required error must name the scope the RESOLVER actually selected
/// (platform-default binding vs project binding) instead of "does the project id string parse",
/// which told users to reconnect the project's Copilot App for platform-scoped failures.</item>
/// <item>A successful launch must leave durable provenance (<c>run.model_provider_resolved</c>)
/// naming the provider/binding/account that served the run.</item>
/// <item>Coordinator sub-run ids (<c>{parent}-coordinator-decompose</c>) must persist their claim.
/// The un-normalized RunId parse threw on every decompose turn, failing the launch and silently
/// degrading decomposition to the deterministic (non-AI) fallback.</item>
/// </list>
/// </summary>
public sealed class KubernetesSandboxExecutorProvenanceTests
{
    private static KubernetesSandboxOptions Options() => new()
    {
        Namespace = "agentweaver",
        WarmPoolRef = "agentweaver-sandbox",
        AgentHostWarmPoolRef = "agentweaver-agent-host",
        TimeoutSeconds = 600,
        RequireMtls = false,
        AgentHostPort = 8088,
        AgentHostA2APath = "/a2a/agent",
        WorkspaceMountPath = "/workspace",
    };

    private static IKubernetes ClientFor(FakeKubeHandler handler) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);

    /// <summary>Fake cluster that binds the AgentHost claim for <paramref name="runId"/> to a pod with an IP.</summary>
    private static FakeKubeHandler BoundClusterFor(string runId)
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var handler = new FakeKubeHandler();
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/agentweaver/sandboxclaims/{claimName}",
            """{"status":{"conditions":[{"type":"Ready","status":"True"}],"sandbox":{"name":"agent-pod-1"}}}""");
        handler.OnAny(@"^/api/v1/namespaces/agentweaver/pods/agent-pod-1$",
            """{"kind":"Pod","metadata":{"name":"agent-pod-1"},"status":{"podIP":"10.0.0.7"}}""");
        return handler;
    }

    [Fact]
    public async Task Platform_default_binding_failure_routes_the_human_to_platform_settings()
    {
        var projectId = ProjectId.New();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(new FakeKubeHandler()),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubRunIdentityResolver("sabbour", projectId.ToString()),
            copilotCredentials: new UnredeemableCopilotCredentialProvider(),
            effectiveProviderResolver: (_, _) => Task.FromResult<EffectiveModelProviderResult>(
                new EffectiveModelProviderResult.PlatformGitHubCopilot("platform-binding", "platform-bot")));

        var act = () => executor.LaunchAgentHostPodAsync(RunId.New().ToString());

        var exception = await act.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        exception.Which.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigurePlatformModelProvider,
                "the failing binding was the platform default, not the project's Copilot App");
        exception.Which.Requirement.Message.Should()
            .Be(ModelProviderConnectionRequirement.PlatformDefaultRequirementMessage);
        exception.Which.Requirement.Action.ProjectId.Should().BeEmpty();
    }

    [Fact]
    public async Task Project_binding_failure_routes_the_human_to_the_project_model_provider_settings()
    {
        var projectId = ProjectId.New();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(new FakeKubeHandler()),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubRunIdentityResolver("sabbour", projectId.ToString()),
            copilotCredentials: new UnredeemableCopilotCredentialProvider(),
            effectiveProviderResolver: (_, _) => Task.FromResult<EffectiveModelProviderResult>(
                new EffectiveModelProviderResult.ProjectGitHubCopilot("project-binding", "octocat")));

        var act = () => executor.LaunchAgentHostPodAsync(RunId.New().ToString());

        var exception = await act.Should().ThrowAsync<ModelProviderConnectionRequiredException>();
        exception.Which.Requirement.Action.Type.Should()
            .Be(ModelProviderConnectionAction.ConfigureProjectModelProvider);
        exception.Which.Requirement.Action.ProjectId.Should().Be(projectId.ToString());
    }

    [Fact]
    public async Task Coordinator_sub_run_persists_its_claim_against_the_parent_run()
    {
        var parentRunId = RunId.New();
        var subRunId = $"{parentRunId}-coordinator-decompose";
        var runStore = new RecordingSandboxInfoRunStore();

        var executor = new KubernetesSandboxExecutor(
            ClientFor(BoundClusterFor(subRunId)),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubRunIdentityResolver("sabbour"),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider(),
            runStore: runStore);

        await executor.LaunchAgentHostPodAsync(subRunId);

        runStore.Calls.Should().ContainSingle(
            "the coordinator decompose sub-run must persist its claim instead of failing the launch");
        var call = runStore.Calls[0];
        call.RunId.Should().Be(parentRunId,
            "a synthetic sub-run id has no row of its own; its claim belongs to the parent run");
        call.ClaimName.Should().Be(SandboxClaimConventions.DeriveAgentHostClaimName(subRunId));
        call.Namespace.Should().Be("agentweaver");
    }

    [Fact]
    public async Task Unparseable_run_id_still_fails_the_launch_closed()
    {
        var runStore = new RecordingSandboxInfoRunStore();
        var executor = new KubernetesSandboxExecutor(
            ClientFor(BoundClusterFor("not-a-run-id")),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubRunIdentityResolver("sabbour"),
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider(),
            runStore: runStore);

        var act = () => executor.LaunchAgentHostPodAsync("not-a-run-id");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not parse as a RunId*");
        runStore.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Configured_pod_emits_byok_model_provider_provenance()
    {
        var runId = RunId.New().ToString();
        var events = new RecordingRunEventStream();

        var executor = new KubernetesSandboxExecutor(
            ClientFor(BoundClusterFor(runId)),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubRunIdentityResolver("sabbour"),
            httpClientFactory: new StubConfigureHttpClientFactory(),
            runEventStream: events,
            copilotCredentials: new UnredeemableCopilotCredentialProvider(),
            byokProviderConfiguration: new StubByokProviderConfigurationProvider(
                new ByokProviderConfiguration(
                    Id: "provider-1",
                    Name: "Test Azure provider",
                    Type: "azure",
                    BaseUrl: "https://byok-resource.openai.azure.com",
                    Model: "gpt-4.1",
                    ApiKey: "test-byok-key")),
            effectiveProviderResolver: (_, _) => Task.FromResult<EffectiveModelProviderResult>(
                new EffectiveModelProviderResult.Byok("provider-1", "azure")));

        await executor.LaunchAgentHostPodAsync(runId);

        var provenance = events.Events.Should().ContainSingle(
            e => e.Type == EventTypes.RunModelProviderResolved).Subject;
        var payload = JsonSerializer.SerializeToElement(provenance.Payload);
        payload.GetProperty("runId").GetString().Should().Be(runId);
        payload.GetProperty("providerKind").GetString()
            .Should().Be(EffectiveModelProviderProvenance.KindByok);
        payload.GetProperty("providerId").GetString().Should().Be("provider-1");
        payload.GetProperty("providerType").GetString().Should().Be("azure");
        payload.GetProperty("modelSource").GetString().Should().Be("byok");
        payload.GetProperty("modelId").GetString().Should().Be("gpt-4.1");
    }

    [Fact]
    public async Task Configured_pod_emits_platform_copilot_provenance_with_the_account_login()
    {
        var runId = RunId.New().ToString();
        var events = new RecordingRunEventStream();

        var executor = new KubernetesSandboxExecutor(
            ClientFor(BoundClusterFor(runId)),
            Options(),
            NullLogger<KubernetesSandboxExecutor>.Instance,
            readinessProbe: null,
            submittingUserResolver: new StubRunIdentityResolver("sabbour"),
            httpClientFactory: new StubConfigureHttpClientFactory(),
            runEventStream: events,
            copilotCredentials: new FixedGitHubCopilotCapabilityCredentialProvider(),
            effectiveProviderResolver: (_, _) => Task.FromResult<EffectiveModelProviderResult>(
                new EffectiveModelProviderResult.PlatformGitHubCopilot("platform-binding", "platform-bot")));

        await executor.LaunchAgentHostPodAsync(runId);

        var provenance = events.Events.Should().ContainSingle(
            e => e.Type == EventTypes.RunModelProviderResolved).Subject;
        var payload = JsonSerializer.SerializeToElement(provenance.Payload);
        payload.GetProperty("providerKind").GetString()
            .Should().Be(EffectiveModelProviderProvenance.KindPlatformGitHubCopilot);
        payload.GetProperty("providerId").GetString().Should().Be("platform-binding");
        payload.GetProperty("githubLogin").GetString().Should().Be("platform-bot");
        payload.GetProperty("modelSource").GetString().Should().Be("github-copilot");
    }

    // ── test doubles ────────────────────────────────────────────────────────

    private sealed class StubRunIdentityResolver(string? user, string? projectId = null)
        : IRunSubmittingUserResolver
    {
        public Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(user);

        public Task<string?> GetWorkingDirectoryAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<(string? ProjectId, string? AgentName)> GetRunIdentityAsync(
            string runId, CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>((projectId, null));
    }

    private sealed class UnredeemableCopilotCredentialProvider : IGitHubCopilotCapabilityCredentialProvider
    {
        public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
            string runId, CancellationToken ct = default) =>
            Task.FromResult<GitHubCapabilitySnapshotCredential?>(null);
    }

    private sealed class StubByokProviderConfigurationProvider(ByokProviderConfiguration configuration)
        : IByokProviderConfigurationProvider
    {
        public Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct) =>
            Task.FromResult<ByokProviderConfiguration?>(configuration);
    }

    private sealed class StubConfigureHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new OkConfigureHandler(), disposeHandler: false);

        private sealed class OkConfigureHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"configured":true}"""),
                });
        }
    }

    private sealed class RecordingRunEventStream : IRunEventStream
    {
        public List<RunEvent> Events { get; } = new();

        public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.FromResult(Events.Count);
        }

        public IAsyncEnumerable<RunEvent> SubscribeAsync(
            string runId, int fromSequence = 0, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public ValueTask CompleteAsync(string runId, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    /// <summary>Minimal <see cref="IRunStore"/> that only records <c>SetSandboxInfoAsync</c> writes.</summary>
    private sealed class RecordingSandboxInfoRunStore : IRunStore
    {
        public sealed record SandboxInfoCall(
            RunId RunId, string? Backend, string? ClaimName, string? PodName, string? Namespace);

        public List<SandboxInfoCall> Calls { get; } = new();

        public Task SetSandboxInfoAsync(
            RunId runId, string? backend, string? claimName, string? podName, string? @namespace,
            CancellationToken ct = default)
        {
            Calls.Add(new SandboxInfoCall(runId, backend, claimName, podName, @namespace));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Run>>(Array.Empty<Run>());
        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TrySetTerminalStatusAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
