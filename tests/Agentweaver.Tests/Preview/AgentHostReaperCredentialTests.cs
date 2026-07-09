using System.Net.Http;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using FluentAssertions;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Regression coverage for spec-006 code-review Finding 1: the per-run preview-runner credential
/// must NOT leak for crash/stall-reaped runs. Those runs never call
/// <c>ReleaseAgentHostPodAsync</c> (the normal delete site), so the orphan-sweep path in
/// <see cref="AgentHostReaperService"/> is the only place that can bound the credential's durable
/// lifetime. It recovers the original run id from the claim's <c>agentweaver.io/run-id</c>
/// annotation and deletes <c>PreviewRunnerCredential.SecretKey(runId)</c>.
/// </summary>
public sealed class AgentHostReaperCredentialTests
{
    private const string Namespace = "agentweaver";
    private const string ListPath =
        "/apis/extensions.agents.x-k8s.io/v1beta1/namespaces/agentweaver/sandboxclaims";

    private static IKubernetes ClientFor(FakeKubeHandler handler) =>
        new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" }, handler);

    private static string ClaimsListJson(string claimName, string? runIdAnnotation)
    {
        var annotations = runIdAnnotation is null
            ? "{}"
            : $$"""{ "{{SandboxClaimConventions.RunIdAnnotation}}": "{{runIdAnnotation}}" }""";
        return $$"""
        {
          "apiVersion": "extensions.agents.x-k8s.io/v1beta1",
          "kind": "SandboxClaimList",
          "items": [
            {
              "metadata": {
                "name": "{{claimName}}",
                "namespace": "agentweaver",
                "annotations": {{annotations}}
              },
              "status": { "conditions": [] }
            }
          ]
        }
        """;
    }

    [Fact]
    public async Task Sweep_OrphanClaim_DeletesPreviewRunnerCredential()
    {
        const string runId = "run-crash-reaped-01";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(ListPath, ClaimsListJson(claimName, runId));

        var secrets = new InMemorySecretStore();
        var key = PreviewRunnerCredential.SecretKey(runId);
        await secrets.SetSecretAsync(key, PreviewRunnerCredential.Mint());
        (await secrets.GetSecretAsync(key)).Found.Should().BeTrue();

        var reaper = new AgentHostReaperService(
            ClientFor(handler),
            new EmptyRunStore(), // no active runs → the claim is an orphan
            new KubernetesSandboxOptions { Namespace = Namespace },
            NullLogger<AgentHostReaperService>.Instance,
            secrets);

        var reaped = await reaper.SweepOrphanedPodsAsync();

        reaped.Should().Be(1);
        (await secrets.GetSecretAsync(key)).Found.Should().BeFalse(
            "the crash-reaped run's credential must be deleted on the orphan-sweep path");
    }

    [Fact]
    public async Task Sweep_OrphanClaim_WithoutRunIdAnnotation_DoesNotThrow_AndStillReaps()
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName("run-legacy-noann");

        var handler = new FakeKubeHandler();
        handler.OnGet(ListPath, ClaimsListJson(claimName, runIdAnnotation: null));

        var reaper = new AgentHostReaperService(
            ClientFor(handler),
            new EmptyRunStore(),
            new KubernetesSandboxOptions { Namespace = Namespace },
            NullLogger<AgentHostReaperService>.Instance,
            new InMemorySecretStore());

        var reaped = await reaper.SweepOrphanedPodsAsync();

        reaped.Should().Be(1); // claim still reaped; credential delete is a best-effort no-op
    }

    [Fact]
    public void RunIdAnnotation_RoundTrips_ThroughClaimJson()
    {
        var json = ClaimsListJson(
            SandboxClaimConventions.DeriveAgentHostClaimName("run-xyz"), "run-xyz");
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];

        SandboxClaimConventions.TryGetRunIdAnnotation(item).Should().Be("run-xyz");
    }

    [Fact]
    public void RunIdAnnotation_Absent_ReturnsNull()
    {
        var json = ClaimsListJson(
            SandboxClaimConventions.DeriveAgentHostClaimName("run-xyz"), runIdAnnotation: null);
        using var doc = JsonDocument.Parse(json);
        var item = doc.RootElement.GetProperty("items")[0];

        SandboxClaimConventions.TryGetRunIdAnnotation(item).Should().BeNull();
    }

    /// <summary>Minimal <see cref="IRunStore"/> that reports no active runs (every claim is orphaned).</summary>
    private sealed class EmptyRunStore : IRunStore
    {
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
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
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
