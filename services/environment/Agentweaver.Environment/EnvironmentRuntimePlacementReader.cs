using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed record EnvironmentSandboxPlacementProjectionV1(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string EnvironmentId,
    long LifecycleGeneration,
    long CurrentFencingGeneration,
    long ProviderFencingGeneration,
    long LeaseRevision,
    DateTimeOffset LeaseExpiresAt,
    bool IsCurrent,
    SandboxLeaseState State,
    ProviderResourceRef Resource,
    SandboxEndpointReference Endpoint,
    SandboxPlacementReference Placement);

public sealed class EnvironmentRuntimePlacementReader(
    IEnvironmentLifecycleStore lifecycleStore,
    ISandboxLeaseStore leaseStore,
    EnvironmentEgressManager egressManager,
    TimeProvider timeProvider)
{
    public async Task<EnvironmentRuntimeBootstrapContext?> GetBootstrapContextAsync(
        CurrentCallerRequest caller, string projectId, string runId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        var placement = await GetCurrentPlacementAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        if (placement is null)
            return null;
        var owner = new EnvironmentOwnerIdentity(placement.TenantId, projectId, runId, environmentId);
        var profile = profiles.Resolve(owner, profileId, placement.Resource);
        return new(
            1, placement.TenantId, projectId, runId, environmentId,
            placement.LifecycleGeneration, placement.CurrentFencingGeneration,
            placement.ProviderFencingGeneration, placement.LeaseRevision, placement.LeaseExpiresAt,
            placement.Resource, placement.Endpoint, placement.Placement,
            profile.ProfileId, profile.ConfigureEndpoint, profile.ObservationEndpoint);
    }

    public async Task<EnvironmentSandboxPlacementProjectionV1?> GetCurrentPlacementAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var authorization = await egressManager.GetAuthorizedRunEnvironmentControlAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var lifecycle = await lifecycleStore.GetAsync(authorization.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetCurrentAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        await egressManager.EnsureRunEnvironmentControlAuthorizationUnchangedAsync(
            caller, authorization.Owner, authorization.Authorization, cancellationToken).ConfigureAwait(false);
        var currentLease = await leaseStore.GetCurrentAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        if (!SameCurrentPlacementLease(lease, currentLease))
            throw new EnvironmentLifecycleException(
                "sandbox_lease_stale",
                "The current Sandbox lease changed while its placement was being authorized.");
        return currentLease is null
            ? null
            : ProjectCurrentPlacement(
                authorization.Owner,
                lifecycle.Fence,
                currentLease,
                timeProvider.GetUtcNow());
    }

    internal static EnvironmentSandboxPlacementProjectionV1 ProjectCurrentPlacement(
        EnvironmentOwnerIdentity owner,
        EnvironmentGenerationFence fence,
        SandboxLeaseSnapshot lease,
        DateTimeOffset now)
    {
        if (lease.Fence != fence ||
            lease.Fence.Owner != owner ||
            !lease.IsCurrent ||
            lease.ProviderFencingGeneration != lease.CurrentFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_stale",
                "The current Sandbox lease does not match the active Environment owner fence.");
        if (lease.LeaseExpiresAt is not { } expiresAt || expiresAt <= now)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_expired",
                "The current Sandbox lease is expired.");
        if (lease.State != SandboxLeaseState.Active ||
            lease.ProvisionedResource is not { } provisionedResource)
            throw new EnvironmentLifecycleException(
                "sandbox_placement_unavailable",
                "The current Sandbox lease has no active provisioned placement.");

        _ = lease.Validate();
        _ = provisionedResource.Validate();
        if (provisionedResource.Resource.Generation != lease.ResourceGeneration ||
            !string.Equals(
                provisionedResource.Resource.ProviderId,
                lease.ProvisionIntent.ProviderId,
                StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_lease_stale",
                "The current Sandbox placement does not match its recorded lease.");

        return new(
            ContractVersion: 1,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            fence.LifecycleGeneration,
            lease.CurrentFencingGeneration,
            lease.ProviderFencingGeneration,
            lease.LeaseRevision,
            expiresAt,
            lease.IsCurrent,
            lease.State,
            provisionedResource.Resource,
            provisionedResource.Endpoint,
            provisionedResource.Placement);
    }

    internal static bool SameCurrentPlacementLease(
        SandboxLeaseSnapshot? left,
        SandboxLeaseSnapshot? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.OperationId == right.OperationId &&
              left.Fence == right.Fence &&
              left.ResourceGeneration == right.ResourceGeneration &&
              left.LeaseRevision == right.LeaseRevision &&
              left.CurrentFencingGeneration == right.CurrentFencingGeneration &&
              left.ProviderFencingGeneration == right.ProviderFencingGeneration &&
              left.State == right.State &&
              left.IsCurrent == right.IsCurrent &&
              left.LeaseExpiresAt == right.LeaseExpiresAt;

}
