using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxManagerTests
{
    [Fact]
    public void PlacementProjectionPreservesOwnedCurrentLeaseIdentity()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "aw-claim-original-id");

        var projection = EnvironmentSandboxManager.ProjectCurrentPlacement(owner, fence, lease, now);

        Assert.Equal(1, projection.ContractVersion);
        Assert.Equal(owner.TenantId, projection.TenantId);
        Assert.Equal(owner.ProjectId, projection.ProjectId);
        Assert.Equal(owner.RunId, projection.RunId);
        Assert.Equal(owner.EnvironmentId, projection.EnvironmentId);
        Assert.Equal(fence.LifecycleGeneration, projection.LifecycleGeneration);
        Assert.Equal(lease.CurrentFencingGeneration, projection.CurrentFencingGeneration);
        Assert.Equal(lease.ProviderFencingGeneration, projection.ProviderFencingGeneration);
        Assert.Equal(lease.LeaseRevision, projection.LeaseRevision);
        Assert.Equal(lease.LeaseExpiresAt, projection.LeaseExpiresAt);
        Assert.True(projection.IsCurrent);
        Assert.Equal(SandboxLeaseState.Active, projection.State);
        Assert.Equal(lease.ProvisionedResource!.Resource, projection.Resource);
        Assert.Equal("aw-claim-original-id", projection.Resource.ResourceId);
        Assert.Equal(lease.ProvisionedResource.Endpoint, projection.Endpoint);
        Assert.Equal(lease.ProvisionedResource.Placement, projection.Placement);
        Assert.Null(projection.Image);
        Assert.DoesNotContain("\"image\"", JsonSerializer.Serialize(projection,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void PlacementImageComesFromProvisionedBindingRatherThanCurrentConfiguration()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "sandbox-uid");
        var options = new AgentSandboxOptions(
            2, "image-options-1", "agentweaver", "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('b', 64),
            "linux/amd64", 10_000_000_000, "kata-vm", "kata-qemu",
            "500m", "512Mi", 1, 1, new(30, 30, 30, 30, 30, 120));
        var binding = lease.ProvisionedResource!.ProviderBinding with
        {
            OptionsSchemaVersion = 2,
            OptionsRevision = options.OptionsRevision,
            OptionsSnapshot = JsonSerializer.SerializeToElement(options,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var pinned = lease with
        {
            ProvisionedResource = lease.ProvisionedResource with { ProviderBinding = binding }
        };
        var projection = EnvironmentSandboxManager.ProjectCurrentPlacement(owner, fence, pinned, now);
        Assert.Equal(new SandboxImageIdentity("sha256:" + new string('b', 64), "linux/amd64", 10_000_000_000),
            projection.Image);
        Assert.Throws<EnvironmentLifecycleException>(() => EnvironmentSandboxManager.ProjectCurrentPlacement(
            owner, fence, pinned with
            {
                ProvisionedResource = pinned.ProvisionedResource! with
                {
                    ProviderBinding = binding with { OptionsRevision = "different-options" }
                }
            }, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnvironmentSandboxManager.ProjectCurrentPlacement(
            owner, fence, pinned with
            {
                ProvisionedResource = pinned.ProvisionedResource! with
                {
                    ProviderBinding = binding with
                    {
                        OptionsSnapshot = JsonSerializer.SerializeToElement(
                            options with { ContainerImageCompressedPullBytes = 0 },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    }
                }
            }, now));
    }

    [Fact]
    public void PlacementProjectionRejectsExpiredStaleNonCurrentAndForeignLeases()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "sandbox-uid");
        var foreignOwner = new EnvironmentOwnerIdentity("tenant-b", "project-a", "run-a", "environment-a");

        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, fence, lease with { LeaseExpiresAt = now.AddSeconds(-1) }, now));
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, fence, lease with { Fence = new EnvironmentGenerationFence(owner, 5) }, now));
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, fence, lease with { IsCurrent = false }, now));
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner,
                fence,
                lease with { Fence = new EnvironmentGenerationFence(foreignOwner, fence.LifecycleGeneration) },
                now));
    }

    [Fact]
    public void PlacementProjectionDetectsLeaseChangesDuringAuthorization()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "sandbox-uid");

        Assert.True(EnvironmentSandboxManager.SameCurrentPlacementLease(lease, lease));
        Assert.True(EnvironmentSandboxManager.SameCurrentPlacementLease(null, null));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(lease, null));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { OperationId = Guid.NewGuid() }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { Fence = new EnvironmentGenerationFence(owner, 5) }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { ResourceGeneration = lease.ResourceGeneration + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { LeaseRevision = lease.LeaseRevision + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { CurrentFencingGeneration = lease.CurrentFencingGeneration + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { ProviderFencingGeneration = lease.ProviderFencingGeneration + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { State = SandboxLeaseState.Releasing }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { IsCurrent = false }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { LeaseExpiresAt = now.AddMinutes(2) }));
    }

    [Fact]
    public async Task ProvisionRequiresFreshWriteProjectsAuthorityBeforeSelectionOrProviderAccess()
    {
        var projects = new FakeProjectsConfigClient();
        var provider = new RecordingSandboxProvider();
        var options = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options-1",
            "agentweaver",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "linux/amd64",
            1,
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1,
            new(30, 30, 30, 30, 30, 120));
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: null!);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore: null!,
            leaseStore: null!,
            provider,
            options,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ProvisionAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a",
                "run-a",
                "environment-a",
                new SandboxProvisionApiRequest(
                    "workspace-a",
                    1,
                    0,
                    "/workspace/agentweaver/project",
                    ReadOnly: false,
                    NetworkPolicyGeneration: 1,
                    IdempotencyKey: "provision-a"),
                CancellationToken.None));

        Assert.Equal("project_write_not_authorized", exception.Code);
        Assert.Equal(0, projects.RunSelectionCalls);
        Assert.Equal(0, provider.ProvisionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderAttemptReadRechecksAuthorityAfterStoreRead(bool changeAuthorityDuringRead)
    {
        var (manager, projects, leaseStore, fence, operationId) = CreateAuthorityReadManager();
        if (changeAuthorityDuringRead)
            leaseStore.DuringProviderAttemptRead = () => projects.ChangeMembershipRevision();

        if (changeAuthorityDuringRead)
        {
            var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
                manager.GetProviderAttemptsAsync(
                    Caller(), "project-a", "run-a", "environment-a", operationId, CancellationToken.None));

            Assert.Equal("authorization_changed", exception.Code);
        }
        else
        {
            var response = await manager.GetProviderAttemptsAsync(
                Caller(), "project-a", "run-a", "environment-a", operationId, CancellationToken.None);

            Assert.Equal(1, response.ContractVersion);
            Assert.Equal(fence, response.CurrentFence);
            Assert.Equal(operationId, response.OperationId);
            Assert.Empty(response.Attempts);
        }

        Assert.Equal(1, leaseStore.ProviderAttemptReadCalls);
        Assert.Equal(5, projects.AuthorizationReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCleanupReadRechecksAuthorityAfterStoreRead(bool changeAuthorityDuringRead)
    {
        var (manager, projects, leaseStore, fence, _) = CreateAuthorityReadManager();
        if (changeAuthorityDuringRead)
            leaseStore.DuringLateCleanupRead = () => projects.ChangeMembershipRevision();

        if (changeAuthorityDuringRead)
        {
            var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
                manager.GetLateResourceCleanupsAsync(
                    Caller(), "project-a", "run-a", "environment-a", CancellationToken.None));

            Assert.Equal("authorization_changed", exception.Code);
        }
        else
        {
            var response = await manager.GetLateResourceCleanupsAsync(
                Caller(), "project-a", "run-a", "environment-a", CancellationToken.None);

            Assert.Equal(1, response.ContractVersion);
            Assert.Equal(fence, response.CurrentFence);
            Assert.Empty(response.Items);
            Assert.False(response.HasMore);
        }

        Assert.Equal(1, leaseStore.LateCleanupReadCalls);
        Assert.Equal(5, projects.AuthorizationReads);
    }

    [Fact]
    public async Task LateCleanupFromOlderLifecycleGenerationDoesNotReadOrDetachReplacementWorkspace()
    {
        var (manager, _, leaseStore, lifecycleStore, provider, currentFence, sourceFence) =
            CreateLateCleanupManager();

        var result = await manager.ReconcileAsync(
            Caller(), "project-a", "run-a", "environment-a", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(sourceFence, leaseStore.ProviderAttemptRequest!.Fence);
        Assert.Equal(currentFence, leaseStore.ProviderAttemptAuthorizationFence);
        Assert.Equal(SandboxProviderEffectKind.LateResourceRelease,
            leaseStore.ProviderAttemptRequest.EffectKind);
        Assert.Equal(1, provider.ReleaseCalls);
        Assert.Equal(1, leaseStore.CompletedCleanupCalls);
        Assert.Equal(0, lifecycleStore.WorkspaceReadCalls);
    }

    [Fact]
    public async Task LateCleanupRechecksAuthorityAfterProviderAttemptReservation()
    {
        var (manager, projects, leaseStore, _, provider, _, _) = CreateLateCleanupManager();
        leaseStore.DuringProviderAttemptReservation = projects.ChangeMembershipRevision;

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ReconcileAsync(
                Caller(), "project-a", "run-a", "environment-a", null, CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal(0, provider.ReleaseCalls);
        Assert.Equal(SandboxProviderAttemptState.FailedNoEffect, leaseStore.RecordedAttempt!.State);
        Assert.Equal("late_cleanup_precondition_failed", leaseStore.RecordedAttempt.FailureCode);
        Assert.False(leaseStore.RecordedEffectMayHaveApplied);
        Assert.Equal(0, leaseStore.CompletedCleanupCalls);
    }

    private static (
        EnvironmentSandboxManager Manager,
        MutableProjectsConfigClient Projects,
        LateCleanupLeaseStore LeaseStore,
        WorkspaceAccessTrackingLifecycleStore LifecycleStore,
        RecordingSandboxProvider Provider,
        EnvironmentGenerationFence CurrentFence,
        EnvironmentGenerationFence SourceFence) CreateLateCleanupManager()
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var currentFence = new EnvironmentGenerationFence(owner, 2);
        var sourceFence = new EnvironmentGenerationFence(owner, 1);
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var sourceLease = CreateCurrentLease(sourceFence, now, "old-sandbox") with
        {
            State = SandboxLeaseState.Released,
            IsCurrent = false,
            LeaseRevision = 2,
            LeaseExpiresAt = null
        };
        var cleanup = new SandboxLateResourceCleanupClaim(
            sourceLease,
            sourceLease.ProvisionedResource!,
            "late-cleanup-1",
            Guid.NewGuid(),
            now.AddMinutes(1))
        {
            ResourceFingerprint = SandboxProviderAttemptRequest.Fingerprint("late-cleanup")
        }.Validate();
        var projects = new MutableProjectsConfigClient(
            CreateReadAuthorization(),
            EnvironmentEgressManagerTests.Selection());
        var lifecycleStore = new WorkspaceAccessTrackingLifecycleStore(currentFence);
        var leaseStore = new LateCleanupLeaseStore(currentFence, cleanup);
        var provider = new RecordingSandboxProvider();
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: lifecycleStore);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore,
            leaseStore,
            provider,
            sandboxOptions: null!,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager: egressManager);

        return (manager, projects, leaseStore, lifecycleStore, provider, currentFence, sourceFence);
    }

    private static (
        EnvironmentSandboxManager Manager,
        MutableProjectsConfigClient Projects,
        AuthorityReadLeaseStore LeaseStore,
        EnvironmentGenerationFence Fence,
        Guid OperationId) CreateAuthorityReadManager()
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 1);
        var projects = new MutableProjectsConfigClient(
            CreateReadAuthorization(),
            EnvironmentEgressManagerTests.Selection());
        var lifecycleStore = new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
        var leaseStore = new AuthorityReadLeaseStore(
            CreateCurrentLease(fence, DateTimeOffset.Parse("2026-10-06T12:00:00Z"), "sandbox-uid"));
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: lifecycleStore);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore,
            leaseStore,
            sandboxProvider: null!,
            sandboxOptions: null!,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager: egressManager);

        return (manager, projects, leaseStore, fence, Guid.NewGuid());
    }

    private static ProjectAuthorizationContextResponse CreateReadAuthorization() =>
        new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://projects.example",
            "actor-a",
            "tenant-a",
            3,
            "project-a",
            "run-a",
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    [
                        new(ProjectAuthorizationPermission.WriteProjects, 1),
                        new(ProjectAuthorizationPermission.ReadProjects, 1),
                        new(ProjectAuthorizationPermission.ReadRunSelection, 1)
                    ])
            ]);

    private static CurrentCallerRequest Caller() => new("validated.jwt.token", "tenant-a");

    private static SandboxLeaseSnapshot CreateCurrentLease(
        EnvironmentGenerationFence fence,
        DateTimeOffset now,
        string resourceId)
    {
        using var json = JsonDocument.Parse("{}");
        var binding = new SandboxProviderBindingSnapshot(
            "agent-sandbox",
            "1.0.0",
            1,
            "options-1",
            json.RootElement.Clone(),
            json.RootElement.Clone());
        var intent = new SandboxLeaseProvisionIntent(
            "agent-sandbox",
            "1.0.0",
            1,
            "options-1",
            json.RootElement.Clone(),
            json.RootElement.Clone(),
            json.RootElement.Clone());
        var resource = new SandboxProvisionedResource(
            new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", resourceId, 1),
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("placement-a"),
            ImmutableHashSet.Create(StringComparer.Ordinal, SandboxCapabilities.VmIsolation),
            [],
            binding);
        return new SandboxLeaseSnapshot(
            fence,
            1,
            1,
            1,
            Guid.NewGuid(),
            SandboxLeaseState.Active,
            intent,
            resource,
            null,
            null,
            null,
            null,
            null,
            IsCurrent: true,
            now)
        {
            LeaseRevision = 1,
            LeaseExpiresAt = now.AddMinutes(1),
            CreatedAt = now.AddMinutes(-1)
        };
    }

    private sealed class MutableProjectsConfigClient(
        ProjectAuthorizationContextResponse authorization,
        EffectiveNetworkPolicySelection selection) : IProjectsConfigClient
    {
        public ProjectAuthorizationContextResponse CurrentAuthorization { get; private set; } = authorization;
        public int AuthorizationReads { get; private set; }

        public void ChangeMembershipRevision() =>
            CurrentAuthorization = CurrentAuthorization with
            {
                MembershipRevision = CurrentAuthorization.MembershipRevision + 1
            };

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            AuthorizationReads++;
            return Task.FromResult(CurrentAuthorization);
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(selection);
    }

    private sealed class AuthorityReadLeaseStore : RuntimeResumeBootstrapTests.SourceLeases
    {
        private readonly EnvironmentGenerationFence _fence;

        public AuthorityReadLeaseStore(SandboxLeaseSnapshot lease) : base(lease) =>
            _fence = lease.Fence;

        public int ProviderAttemptReadCalls { get; private set; }
        public int LateCleanupReadCalls { get; private set; }
        public Action? DuringProviderAttemptRead { get; set; }
        public Action? DuringLateCleanupRead { get; set; }

        public override Task<ImmutableArray<SandboxProviderAttemptSnapshot>> GetProviderAttemptsAsync(
            EnvironmentGenerationFence currentFence,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_fence, currentFence);
            Assert.NotEqual(Guid.Empty, operationId);
            ProviderAttemptReadCalls++;
            DuringProviderAttemptRead?.Invoke();
            return Task.FromResult(ImmutableArray<SandboxProviderAttemptSnapshot>.Empty);
        }

        public override Task<(ImmutableArray<SandboxLateResourceCleanupRecord> Items, bool HasMore)>
            GetLateResourceCleanupRecordsAsync(
                EnvironmentGenerationFence currentFence,
                CancellationToken cancellationToken)
        {
            Assert.Equal(_fence, currentFence);
            LateCleanupReadCalls++;
            DuringLateCleanupRead?.Invoke();
            return Task.FromResult((ImmutableArray<SandboxLateResourceCleanupRecord>.Empty, false));
        }
    }

    private sealed class LateCleanupLeaseStore(
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim cleanup) : RuntimeResumeBootstrapTests.SourceLeases(cleanup.Lease)
    {
        private SandboxProviderAttemptSnapshot? _attempt;

        public SandboxProviderAttemptRequest? ProviderAttemptRequest { get; private set; }
        public EnvironmentGenerationFence? ProviderAttemptAuthorizationFence { get; private set; }
        public Action? DuringProviderAttemptReservation { get; set; }
        public SandboxProviderAttemptSnapshot? RecordedAttempt { get; private set; }
        public bool RecordedEffectMayHaveApplied { get; private set; }
        public int CompletedCleanupCalls { get; private set; }

        public override Task<SandboxLeaseSnapshot?> GetCurrentAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentFence, fence);
            return Task.FromResult<SandboxLeaseSnapshot?>(null);
        }

        public override Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentFence, fence);
            return Task.FromResult<SandboxLateResourceCleanupClaim?>(cleanup);
        }

        public override Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest request,
            EnvironmentGenerationFence authorizationFence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentFence, authorizationFence);
            Assert.Equal(cleanup.Lease.Fence, request.Fence);
            ProviderAttemptRequest = request;
            ProviderAttemptAuthorizationFence = authorizationFence;
            DuringProviderAttemptReservation?.Invoke();
            _attempt = new SandboxProviderAttemptSnapshot(
                Guid.NewGuid(),
                request,
                SandboxProviderAttemptState.InProgress,
                Outcome: null,
                FailureCode: null,
                DateTimeOffset.UtcNow,
                OutcomeRecordedAt: null,
                ResolvedAt: null).Validate();
            return Task.FromResult(new SandboxProviderAttemptReservation(_attempt, Replayed: false));
        }

        public override Task<SandboxProviderAttemptSnapshot> RecordProviderAttemptOutcomeAsync(
            Guid attemptId,
            EnvironmentGenerationFence fence,
            JsonElement outcome,
            bool effectMayHaveApplied,
            string? failureCode,
            CancellationToken cancellationToken)
        {
            Assert.Equal(cleanup.Lease.Fence, fence);
            Assert.Equal(_attempt!.AttemptId, attemptId);
            RecordedEffectMayHaveApplied = effectMayHaveApplied;
            var recordedAt = DateTimeOffset.UtcNow;
            RecordedAttempt = _attempt with
            {
                State = effectMayHaveApplied
                    ? SandboxProviderAttemptState.ResultRecorded
                    : SandboxProviderAttemptState.FailedNoEffect,
                Outcome = outcome.Clone(),
                FailureCode = failureCode,
                OutcomeRecordedAt = recordedAt,
                ResolvedAt = effectMayHaveApplied ? null : recordedAt
            };
            _attempt = RecordedAttempt.Validate();
            return Task.FromResult(_attempt);
        }

        public override Task CompleteLateResourceCleanupAsync(
            EnvironmentGenerationFence authorizationFence,
            SandboxLateResourceCleanupClaim claim,
            SandboxReleaseReceipt receipt,
            CancellationToken cancellationToken,
            Guid? providerAttemptId = null)
        {
            Assert.Equal(currentFence, authorizationFence);
            Assert.Equal(cleanup, claim);
            Assert.Equal(_attempt!.AttemptId, providerAttemptId);
            Assert.Equal(SandboxProviderAttemptState.ResultRecorded, _attempt.State);
            Assert.Equal(cleanup.ProvisionedResource.Resource, receipt.Resource);
            Assert.Equal(cleanup.IdempotencyKey, receipt.IdempotencyKey);
            CompletedCleanupCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class WorkspaceAccessTrackingLifecycleStore : EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore
    {
        public WorkspaceAccessTrackingLifecycleStore(EnvironmentGenerationFence currentFence)
            : base(currentFence)
        {
        }

        public int WorkspaceReadCalls { get; private set; }

        public override Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            CancellationToken cancellationToken)
        {
            WorkspaceReadCalls++;
            return Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(null);
        }
    }

    private sealed class FakeProjectsConfigClient : IProjectsConfigClient
    {
        private static readonly ProjectAuthorizationContextResponse Context = new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://projects.example",
            "actor-a",
            "tenant-a",
            3,
            "project-a",
            "run-a",
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    [new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1)])
            ]);

        public int RunSelectionCalls { get; private set; }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(Context);

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            RunSelectionCalls++;
            return Task.FromResult(new EffectiveNetworkPolicySelection(
                projectId,
                runId,
                1,
                1,
                1,
                "selection-1",
                [],
                [],
                null,
                [],
                []));
        }
    }

    private sealed class RecordingSandboxProvider : ISandboxProvider
    {
        public int ProvisionCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<SandboxProvisionedResource> ProvisionAsync(
            SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            throw new InvalidOperationException("The provider must not be reached without WriteProjects.");
        }

        public Task<SandboxObservation> DescribeAsync(
            SandboxDescribeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Describe is not part of an unauthorized provision.");

        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
            SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ListOwned is not part of an unauthorized provision.");

        public Task<SandboxReleaseReceipt> ReleaseAsync(
            SandboxReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return Task.FromResult(new SandboxReleaseReceipt(
                request.Resource,
                request.IdempotencyKey,
                SandboxReleaseDisposition.Released));
        }

        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
            SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Partial release is not part of an unauthorized provision.");
    }
}
