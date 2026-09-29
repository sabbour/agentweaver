using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentweaver.Tests.Coordinator;

public sealed class TerminalCoordinatorChildSandboxCleanupTests : IAsyncDisposable
{
    private readonly TestSqliteDb _database;
    private readonly SqliteRunStore _runs;
    private readonly SqliteRunLeaseStore _leases;
    private readonly RecordingPods _pods = new();

    public TerminalCoordinatorChildSandboxCleanupTests()
    {
        _database = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runs = new SqliteRunStore(_database.Db);
        _leases = new SqliteRunLeaseStore(_database.Db);
    }

    [Theory]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Declined)]
    [InlineData(RunStatus.Completed)]
    public async Task TerminalParent_ReleasesAllAssembleReadyAndRevisionChildren(RunStatus terminalStatus)
    {
        var parent = await AddRunAsync(terminalStatus);
        var original = await AddRunAsync(RunStatus.AssembleReady, parent);
        var revision = await AddRunAsync(RunStatus.AssembleReady, parent);
        var otherSubtask = await AddRunAsync(RunStatus.Failed, parent);
        var sut = CreateCleanup();

        await sut.ReleaseForParentAsync(parent);

        _pods.Releases.Select(r => r.RunId).Should()
            .BeEquivalentTo([original, revision, otherSubtask]);
        _pods.Releases.Should().OnlyContain(r => r.Token == "holder");
        await sut.ReleaseForParentAsync(parent);
        _pods.Releases.Should().HaveCount(3, "deleted claims do not need another release");
    }

    [Fact]
    public async Task RestartSweep_CleansExistingTerminalParentClaims()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        var child = await AddRunAsync(RunStatus.AssembleReady, parent);
        await CreateCleanup().SweepAsync();
        _pods.Releases.Should().ContainSingle().Which.RunId.Should().Be(child);

        // A fresh service instance has no process-local cursor; repeated startup is harmless.
        await CreateCleanup().SweepAsync();
        _pods.Releases.Should().ContainSingle();
    }

    [Fact]
    public async Task Sweep_PagesThroughOldTerminalCoordinators()
    {
        for (var index = 0; index < 17; index++)
        {
            var parent = await AddRunAsync(RunStatus.Failed);
            await AddRunAsync(RunStatus.AssembleReady, parent);
        }

        var sut = CreateCleanup();
        await sut.SweepAsync();
        _pods.Releases.Should().HaveCount(16);
        await sut.SweepAsync();
        _pods.Releases.Should().HaveCount(17);
    }

    [Fact]
    public async Task ActiveParentAndActiveChildren_DoNotRelease()
    {
        var activeParent = await AddRunAsync(RunStatus.InProgress);
        await AddRunAsync(RunStatus.AssembleReady, activeParent);
        var terminalParent = await AddRunAsync(RunStatus.Failed);
        var executing = await AddRunAsync(RunStatus.InProgress, terminalParent);
        var executingLease = await _leases.TryClaimAsync(executing, "worker", TimeSpan.FromMinutes(5));
        executingLease.Claimed.Should().BeTrue();
        _pods.Claims[executing] = _pods.Claims[executing] with
        {
            DispatchFencingToken = executingLease.FencingToken,
        };
        await AddRunAsync(RunStatus.AwaitingReview, terminalParent);
        var pending = await AddRunAsync(RunStatus.Pending, terminalParent);
        var pendingLease = await _leases.TryClaimAsync(pending, "queued-worker", TimeSpan.FromMinutes(5));
        pendingLease.Claimed.Should().BeTrue();
        _pods.Claims[pending] = _pods.Claims[pending] with { DispatchFencingToken = pendingLease.FencingToken };

        await CreateCleanup().ReleaseForParentAsync(activeParent);
        await CreateCleanup().ReleaseForParentAsync(terminalParent);
        await CreateCleanup().SweepAsync();

        _pods.Releases.Should().BeEmpty();
    }

    [Fact]
    public async Task RestartSweep_ReclaimsAbandonedPendingAndInProgressChildren()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        var pending = await AddRunAsync(RunStatus.Pending, parent);
        var abandoned = await AddRunAsync(RunStatus.InProgress, parent);
        var expired = await AddRunAsync(RunStatus.InProgress, parent);
        var (claimed, token) = await _leases.TryClaimAsync(expired, "crashed-worker", TimeSpan.FromMinutes(5));
        claimed.Should().BeTrue();
        await _leases.ReleaseAsync(expired, "crashed-worker", token);

        await CreateCleanup().SweepAsync();

        _pods.Releases.Select(r => r.RunId).Should().BeEquivalentTo([pending, abandoned, expired]);
        await CreateCleanup().SweepAsync();
        _pods.Releases.Should().HaveCount(3);
    }

    [Fact]
    public async Task LeaseFromNewOwner_DoesNotProtectPriorExecutionClaim()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        var child = await AddRunAsync(RunStatus.InProgress, parent);
        var (claimed, token) = await _leases.TryClaimAsync(child, "new-worker", TimeSpan.FromMinutes(5));
        claimed.Should().BeTrue();
        _pods.Claims[child] = new AgentHostLaunchContext(
            null, HolderToken: "old-holder", LifecycleGeneration: 1,
            DispatchFencingToken: token + 1);

        await CreateCleanup().SweepAsync();

        _pods.Releases.Select(r => r.RunId).Should().ContainSingle().Which.Should().Be(child);
    }

    [Fact]
    public async Task InApiExecution_DoesNotTouchPodLifecycle()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        await AddRunAsync(RunStatus.AssembleReady, parent);
        var sut = new TerminalCoordinatorChildSandboxCleanup(
            _runs, NullLogger<TerminalCoordinatorChildSandboxCleanup>.Instance,
            Options.Create(new SandboxRuntimeOptions()), _pods);

        await sut.ReleaseForParentAsync(parent);
        await sut.SweepAsync();

        _pods.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleGenerationAndReplacedHolder_DoNotRelease()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        var stale = await AddRunAsync(RunStatus.AssembleReady, parent);
        _pods.Claims[stale] = new AgentHostLaunchContext(null, HolderToken: "holder", LifecycleGeneration: 2);
        var replaced = await AddRunAsync(RunStatus.AssembleReady, parent);
        _pods.Replaced.Add(replaced);

        await CreateCleanup().ReleaseForParentAsync(parent);

        _pods.Releases.Should().BeEmpty();
        _pods.Attempts.Should().ContainSingle().Which.Should().Be(replaced);
    }

    [Fact]
    public async Task ParentReactivatedAfterClaimRead_DoesNotReleaseFormerChild()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        await AddRunAsync(RunStatus.AssembleReady, parent);
        _pods.OnReadClaim = async _ =>
        {
            _pods.OnReadClaim = null;
            await _runs.UpdateStatusAsync(RunId.Parse(parent), RunStatus.InProgress, null);
        };

        await CreateCleanup().ReleaseForParentAsync(parent);

        _pods.Attempts.Should().BeEmpty();
    }

    [Fact]
    public async Task PreviewRetentionAndReleaseFailure_DoNotBlockOtherChildren()
    {
        var parent = await AddRunAsync(RunStatus.Failed);
        var failed = await AddRunAsync(RunStatus.Failed, parent);
        var preview = await AddRunAsync(RunStatus.AssembleReady, parent);
        var ordinary = await AddRunAsync(RunStatus.AssembleReady, parent);
        _pods.ThrowOn.Add(failed);
        _pods.PreviewActive.Add(preview);

        await CreateCleanup().ReleaseForParentAsync(parent);

        _pods.Releases.Select(r => r.RunId).Should().ContainSingle().Which.Should().Be(ordinary);
        _pods.Claims.Should().ContainKeys(failed, preview);
        _pods.ThrowOn.Clear();
        _pods.PreviewActive.Clear();
        await CreateCleanup().SweepAsync();
        _pods.Releases.Select(r => r.RunId).Should().BeEquivalentTo([failed, preview, ordinary]);
    }

    private TerminalCoordinatorChildSandboxCleanup CreateCleanup() =>
        new(_runs,
            NullLogger<TerminalCoordinatorChildSandboxCleanup>.Instance,
            Options.Create(new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" }), _pods, _leases);

    private async Task<string> AddRunAsync(RunStatus status, string? parentId = null)
    {
        var id = RunId.New().ToString();
        await _runs.InsertAsync(new Run
        {
            Id = RunId.Parse(id),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "task",
            SubmittingUser = "owner",
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = parentId is null ? "Coordinator" : "agent",
            ParentRunId = parentId,
            SubtaskId = parentId is null ? null : "1",
        });
        _pods.Claims[id] = new AgentHostLaunchContext(null, HolderToken: "holder", LifecycleGeneration: 1);
        return id;
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    private sealed class RecordingPods : IAgentHostPodLifecycle
    {
        public Dictionary<string, AgentHostLaunchContext> Claims { get; } = [];
        public List<(string RunId, string Token)> Releases { get; } = [];
        public List<string> Attempts { get; } = [];
        public HashSet<string> Replaced { get; } = [];
        public HashSet<string> PreviewActive { get; } = [];
        public HashSet<string> ThrowOn { get; } = [];
        public Func<string, Task>? OnReadClaim { get; set; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> LaunchAgentHostPodAsync(
            string runId, string? workingDirectoryOverride, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public async Task<AgentHostLaunchContext?> GetAgentHostDispatchContextAsync(
            string runId, CancellationToken ct = default)
        {
            if (OnReadClaim is not null)
                await OnReadClaim(runId);
            return Claims.GetValueOrDefault(runId);
        }
        public async Task<AgentHostClaimSnapshot?> GetAgentHostClaimSnapshotAsync(
            string runId, CancellationToken ct = default)
        {
            var context = await GetAgentHostDispatchContextAsync(runId, ct);
            return context is null ? null : new AgentHostClaimSnapshot(context, "uid-" + runId, "1");
        }
        public Task<bool> TryReleaseHeldAgentHostPodAsync(
            string runId, AgentHostClaimSnapshot claim, CancellationToken ct = default) =>
            TryReleaseHeldAgentHostPodAsync(runId, claim.Context.HolderToken!, ct);
        public Task<bool> TryReleaseHeldAgentHostPodAsync(
            string runId, string holderToken, CancellationToken ct = default)
        {
            Attempts.Add(runId);
            if (ThrowOn.Contains(runId))
                throw new InvalidOperationException("transient claim release fault");
            if (PreviewActive.Contains(runId))
                return Task.FromResult(true);
            if (Replaced.Contains(runId) || !Claims.Remove(runId, out var claim)
                || claim.HolderToken != holderToken)
                return Task.FromResult(false);
            Releases.Add((runId, holderToken));
            return Task.FromResult(true);
        }
    }
}
