using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimePlacementReader(
    EnvironmentSandboxManager placements,
    EnvironmentRuntimeOwnerContextClient runtimeOwner,
    TimeProvider timeProvider)
{
    public Task<EnvironmentRuntimeBootstrapContext?> GetBootstrapContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        return placements.GetCurrentPlacementCoreAsync<EnvironmentRuntimeBootstrapContext?>(
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
            }, cancellationToken);
    }
}
