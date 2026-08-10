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

    // Issue #542: a completed subtask's claim is an "orphan" per the active-run map the instant its
    // turn ends. The reaper must NOT reap it while the run still has a live preview (that would 404 the
    // preview URL), but MUST reap it once no preview is active (bounded eventual teardown — no leak).
    [Fact]
    public async Task Sweep_OrphanClaim_WithActivePreview_IsDeferred_NotReaped()
    {
        const string runId = "run-542-reaper-defer";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(ListPath, ClaimsListJson(claimName, runId));

        var preview = new StubPreviewService(hasActivePreview: true);
        var reaper = new AgentHostReaperService(
            ClientFor(handler),
            new EmptyRunStore(), // no active runs → the claim is an orphan by the active-run map
            new KubernetesSandboxOptions { Namespace = Namespace },
            NullLogger<AgentHostReaperService>.Instance,
            new InMemorySecretStore(),
            preview);

        var reaped = await reaper.SweepOrphanedPodsAsync();

        reaped.Should().Be(0,
            "an orphaned claim whose run still has a live preview must be deferred, not reaped (#542)");
        preview.RenewedRunId.Should().Be(runId,
            "#560: deferring the reap must also renew the claim's cluster-side TTL so the sandbox " +
            "controller does not reap the pod out from under the live preview");
        preview.SafeToEvictCalls.Should().ContainSingle().Which.Should().Be((runId, false),
            "#574: deferring the reap must also pin the backing pod (safe-to-evict=false) so the " +
            "cluster-autoscaler does not drain the kata node and kill the pod during a scale-down");
    }

    [Fact]
    public async Task Sweep_OrphanClaim_WithClusterVisiblePreview_StillDefers_WhenPreviewFeatureFlagIsOff()
    {
        // #578: the worker heartbeat reaper must honor a live preview even if that process is not the
        // role that provisions preview routes. "Enabled=false" here simulates the old worker-config
        // drift; the preview service can still see the durable cluster route state, so the claim must
        // NOT be deleted.
        const string runId = "run-578-worker-reaper";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(ListPath, ClaimsListJson(claimName, runId));

        var preview = new StubPreviewService(hasActivePreview: true, enabled: false);
        var reaper = new AgentHostReaperService(
            ClientFor(handler),
            new EmptyRunStore(),
            new KubernetesSandboxOptions { Namespace = Namespace },
            NullLogger<AgentHostReaperService>.Instance,
            new InMemorySecretStore(),
            preview);

        var reaped = await reaper.SweepOrphanedPodsAsync();

        reaped.Should().Be(0,
            "the reaper must not delete a claim when cluster preview state still proves a live preview, " +
            "even if the local process is not the one that provisions previews");
        preview.RenewedRunId.Should().Be(runId);
        preview.SafeToEvictCalls.Should().ContainSingle().Which.Should().Be((runId, false));
    }

    [Fact]
    public async Task Sweep_TerminalDirectBackedPreview_SurvivesWorkerSweeps_ThenReapsAfterIdleExpiry()
    {
        // #560's real execution subtask reports sandbox.backend=direct, but its serving process is
        // still backed by an agent-* SandboxClaim. Exercise the actual cluster-backed preview service
        // with worker-style Enabled=false, rather than a stub, across the original 20+ minute repro
        // window and then beyond the bounded idle expiry.
        const string runId = "run-560-direct-terminal";
        const string podName = "agentweaver-agent-host-direct";
        const string token = "swift-falcon-amber-k7m2q9x4n8b3r6t5w1z0c2";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);

        var handler = new FakeKubeHandler();
        handler.OnGet(ListPath, ClaimsListJson(claimName, runId));
        handler.OnGet(
            $"/apis/{SandboxClaimConventions.ApiGroup}/{SandboxClaimConventions.ApiVersion}/namespaces/{Namespace}/sandboxclaims/{claimName}",
            JsonSerializer.Serialize(new
            {
                status = new
                {
                    conditions = new[] { new { type = "Ready", status = "True" } },
                    sandbox = new { name = podName },
                },
            }));
        handler.OnGet(
            "/apis/gateway.networking.k8s.io/v1/namespaces/agentweaver/httproutes",
            $$"""
            {
              "kind": "HTTPRouteList",
              "items": [{
                "metadata": {
                  "name": "preview-direct-terminal",
                  "annotations": {
                    "agentweaver.dev/preview-token": "{{token}}",
                    "agentweaver.dev/preview-run": "{{sanitizedRun}}",
                    "agentweaver.dev/preview-expires-at": "{{now.AddMinutes(30):O}}",
                    "agentweaver.dev/preview-max-until": "{{now.AddHours(8):O}}"
                  }
                }
              }]
            }
            """);

        var preview = new SandboxPreviewService(
            ClientFor(handler),
            new SandboxPreviewOptions
            {
                Enabled = false, // worker does not provision routes, but must read their cluster state
                Namespace = Namespace,
                MaxLifetimeHours = 8,
            },
            NullLogger<SandboxPreviewService>.Instance,
            clock);
        var secrets = new InMemorySecretStore();
        var credentialKey = PreviewRunnerCredential.SecretKey(runId);
        await secrets.SetSecretAsync(credentialKey, PreviewRunnerCredential.Mint());
        var reaper = new AgentHostReaperService(
            ClientFor(handler),
            new EmptyRunStore(), // terminal child => orphaned by active-run status
            new KubernetesSandboxOptions { Namespace = Namespace },
            NullLogger<AgentHostReaperService>.Instance,
            secrets,
            preview);

        (await reaper.SweepOrphanedPodsAsync()).Should().Be(0);
        clock.Advance(TimeSpan.FromMinutes(20));
        (await reaper.SweepOrphanedPodsAsync()).Should().Be(0,
            "a direct-backed terminal subtask preview must survive repeated worker sweeps through the reported 20+ minute window");

        handler.Requests.Should().NotContain(r =>
            r.Method == "DELETE" && r.Path.EndsWith($"/sandboxclaims/{claimName}"));
        handler.Requests.Count(r =>
                r.Method == "PATCH" && r.Path.Contains("/sandboxclaims/"))
            .Should().BeGreaterThanOrEqualTo(4,
                "each active-preview sweep renews both possible backing claim conventions");
        handler.Requests.Count(r =>
                r.Method == "PATCH" && r.Path.EndsWith($"/pods/{podName}") &&
                r.Body!.Contains("\"false\"", StringComparison.Ordinal))
            .Should().Be(2,
                "each worker sweep re-pins the live preview pod against autoscaler scale-down");
        (await secrets.GetSecretAsync(credentialKey)).Found.Should().BeTrue();

        clock.Advance(TimeSpan.FromMinutes(11));
        (await reaper.SweepOrphanedPodsAsync()).Should().Be(1,
            "once the route's idle lifetime ends, the terminal claim must be reaped instead of leaking");
        handler.Requests.Should().Contain(r =>
            r.Method == "DELETE" && r.Path.EndsWith($"/sandboxclaims/{claimName}"));
        (await secrets.GetSecretAsync(credentialKey)).Found.Should().BeFalse(
            "bounded teardown must also remove the terminal run's preview credential");
    }

    [Fact]
    public async Task Sweep_OrphanClaim_WithNoActivePreview_IsReaped()
    {
        const string runId = "run-542-reaper-noactive";
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        var handler = new FakeKubeHandler();
        handler.OnGet(ListPath, ClaimsListJson(claimName, runId));

        var reaper = new AgentHostReaperService(
            ClientFor(handler),
            new EmptyRunStore(),
            new KubernetesSandboxOptions { Namespace = Namespace },
            NullLogger<AgentHostReaperService>.Instance,
            new InMemorySecretStore(),
            new StubPreviewService(hasActivePreview: false));

        var reaped = await reaper.SweepOrphanedPodsAsync();

        reaped.Should().Be(1,
            "once no preview is active the orphaned claim must be reaped (bounded eventual teardown)");
    }

    [Fact]
    public void IsReapable_YoungInactiveClaim_IsProtectedByCreationGrace()
    {
        var now = new DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.Zero);
        AgentHostReaperService.IsReapable(
                ClaimCreatedAt(now - TimeSpan.FromSeconds(10)),
                isActive: false,
                now,
                TimeSpan.FromMinutes(5))
            .Should().BeFalse();
    }

    [Fact]
    public void IsReapable_OldInactiveClaim_IsReaped()
    {
        var now = new DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.Zero);
        AgentHostReaperService.IsReapable(
                ClaimCreatedAt(now - TimeSpan.FromMinutes(10)),
                isActive: false,
                now,
                TimeSpan.FromMinutes(5))
            .Should().BeTrue();
    }

    [Fact]
    public void IsReapable_ActiveClaim_IsNeverReaped()
    {
        var now = new DateTimeOffset(2026, 7, 10, 18, 0, 0, TimeSpan.Zero);
        AgentHostReaperService.IsReapable(
                ClaimCreatedAt(now - TimeSpan.FromMinutes(10)),
                isActive: true,
                now,
                TimeSpan.FromMinutes(5))
            .Should().BeFalse();
    }

    [Fact]
    public void EffectiveCreationGrace_IsFlooredAboveReadinessTimeout()
    {
        var options = new KubernetesSandboxOptions
        {
            AgentHostReadyTimeoutSeconds = 400,
            AgentHostClaimCreationGraceSeconds = 60,
        };

        AgentHostReaperService.EffectiveCreationGrace(options)
            .Should().Be(TimeSpan.FromSeconds(430));
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

    private static AgentHostClaimInfo ClaimCreatedAt(DateTimeOffset? createdAt) =>
        new(
            ClaimName: "agent-test",
            RunId: null,
            PodName: null,
            Ready: false,
            CreatedAt: createdAt,
            Orphaned: true,
            AnnotatedRunId: null);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    // Minimal ISandboxPreviewService test double for the reaper defer path (#542): only
    // HasActivePreviewAsync is consulted; every other member throws so an unexpected call is loud.
    private sealed class StubPreviewService : ISandboxPreviewService
    {
        private readonly bool _hasActivePreview;
        private readonly bool _enabled;
        public StubPreviewService(bool hasActivePreview, bool enabled = true)
        {
            _hasActivePreview = hasActivePreview;
            _enabled = enabled;
        }

        public Task<bool> HasActivePreviewAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(_hasActivePreview);

        /// <summary>Run id passed to <see cref="RenewBackingClaimTtlAsync"/>, or null if never called (#560).</summary>
        public string? RenewedRunId { get; private set; }

        public Task RenewBackingClaimTtlAsync(string runId, CancellationToken ct = default)
        {
            RenewedRunId = runId;
            return Task.CompletedTask;
        }

        /// <summary>(runId, safeToEvict) tuples passed to <see cref="SetBackingPodSafeToEvictAsync"/> (#574).</summary>
        public List<(string RunId, bool SafeToEvict)> SafeToEvictCalls { get; } = new();

        public Task SetBackingPodSafeToEvictAsync(string runId, bool safeToEvict, CancellationToken ct = default)
        {
            SafeToEvictCalls.Add((runId, safeToEvict));
            return Task.CompletedTask;
        }

        public bool Enabled => _enabled;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;
        public Task<PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null) => throw new NotImplementedException();
        public Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task KeepAliveAsync(string token, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task StopPreviewAsync(string token, CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<int> ReapAsync(CancellationToken ct = default) => throw new NotImplementedException();
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
