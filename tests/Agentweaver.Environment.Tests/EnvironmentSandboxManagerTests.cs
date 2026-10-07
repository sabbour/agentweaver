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
            1,
            "sandbox-options-1",
            "agentweaver",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost:1",
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1);
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
            LeaseExpiresAt = now.AddMinutes(1)
        };
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
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Release is not part of an unauthorized provision.");

        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
            SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Partial release is not part of an unauthorized provision.");
    }
}
