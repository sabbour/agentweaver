using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimePlacementReader(
    IEnvironmentLifecycleStore lifecycleStore,
    ISandboxLeaseStore leaseStore,
    EnvironmentEgressManager egressManager,
    TimeProvider timeProvider,
    EnvironmentRuntimeOwnerContextClient? runtimeOwner = null)
{
    public async Task<EnvironmentRuntimeBootstrapContext?> GetBootstrapContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        if (runtimeOwner is null)
            throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        return await ReadCurrentPlacementCoreAsync<EnvironmentRuntimeBootstrapContext?>(
            new(actor.Bearer.GetValue(), actor.TenantSelector), projectId, runId, environmentId,
            runBoundRead: true, async (placement, token) =>
            {
                if (placement is null)
                    return null;
                var ownerContext = await runtimeOwner.ReadAsync(actor, projectId, runId, sessionId, token)
                    .ConfigureAwait(false);
                if (ownerContext.ContractVersion != 1 || ownerContext.TenantId != placement.TenantId ||
                    ownerContext.ProjectId != projectId || ownerContext.RunId != runId ||
                    ownerContext.SessionId != sessionId || !actor.Bearer.IsUsable() ||
                    placement.LeaseExpiresAt <= timeProvider.GetUtcNow())
                    throw new RuntimeAuthorizationException("runtime_owner_context_stale");
                var owner = new EnvironmentOwnerIdentity(placement.TenantId, projectId, runId, environmentId);
                var profile = profiles.Resolve(owner, profileId, placement.Resource);
                return new EnvironmentRuntimeBootstrapContext(
                    1, placement.TenantId, projectId, runId, environmentId,
                    placement.LifecycleGeneration, placement.CurrentFencingGeneration,
                    placement.ProviderFencingGeneration, placement.LeaseRevision, placement.LeaseExpiresAt,
                    placement.Resource, placement.Endpoint, placement.Placement,
                    profile.ProfileId, profile.ConfigureEndpoint, profile.ObservationEndpoint)
                {
                    RuntimeOwnerContext = ownerContext
                };
            }, cancellationToken).ConfigureAwait(false);
    }

    public Task<EnvironmentSandboxPlacementProjectionV1?> GetCurrentPlacementAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken) =>
        ReadCurrentPlacementCoreAsync(
            caller, projectId, runId, environmentId, runBoundRead: false,
            (placement, _) => Task.FromResult(placement), cancellationToken);

    internal Task<EnvironmentSandboxPlacementProjectionV1?> GetCurrentRunBoundPlacementAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken) =>
        ReadCurrentPlacementCoreAsync(
            caller, projectId, runId, environmentId, runBoundRead: true,
            (placement, _) => Task.FromResult(placement), cancellationToken);

    private async Task<TResult> ReadCurrentPlacementCoreAsync<TResult>(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        bool runBoundRead,
        Func<EnvironmentSandboxPlacementProjectionV1?, CancellationToken, Task<TResult>> project,
        CancellationToken cancellationToken)
    {
        var authorization = runBoundRead
            ? await egressManager.GetAuthorizedRunEnvironmentPlacementReadAsync(
                caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false)
            : await egressManager.GetAuthorizedRunEnvironmentControlAsync(
                caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var lifecycle = await lifecycleStore.GetAsync(authorization.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetCurrentAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        if (runBoundRead)
            await egressManager.EnsureRunEnvironmentPlacementReadAuthorizationUnchangedAsync(
                caller, authorization.Owner, authorization.Authorization, cancellationToken).ConfigureAwait(false);
        else
            await egressManager.EnsureRunEnvironmentControlAuthorizationUnchangedAsync(
                caller, authorization.Owner, authorization.Authorization, cancellationToken).ConfigureAwait(false);
        return await leaseStore.GetCurrentAsync(
            lifecycle.Fence, async (currentLease, token) =>
            {
                if (!SameCurrentPlacementLease(lease, currentLease))
                    throw new EnvironmentLifecycleException(
                        "sandbox_lease_stale",
                        "The current Sandbox lease changed while its placement was being authorized.");
                if (runBoundRead)
                    await egressManager.EnsureRunEnvironmentPlacementReadAuthorizationUnchangedAsync(
                        caller, authorization.Owner, authorization.Authorization, token).ConfigureAwait(false);
                else
                    await egressManager.EnsureRunEnvironmentControlAuthorizationUnchangedAsync(
                        caller, authorization.Owner, authorization.Authorization, token).ConfigureAwait(false);
                var projection = currentLease is null ? null : ProjectCurrentPlacement(
                    authorization.Owner, lifecycle.Fence, currentLease, timeProvider.GetUtcNow());
                return await project(projection, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
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
