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
            runBoundRead: true, (placement, token) => placement is null
                ? Task.FromResult<EnvironmentRuntimeBootstrapContext?>(null)
                : ProjectBootstrapAsync(actor, placement, sessionId, profileId, profiles, token),
            cancellationToken);
    }

    public Task<EnvironmentRuntimeReadinessContext?> GetReadinessContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        return placements.GetCurrentPlacementCoreAsync<EnvironmentRuntimeReadinessContext?>(
            new(actor.Bearer.GetValue(), actor.TenantSelector), projectId, runId, environmentId,
            runBoundRead: true, async (placement, token) =>
            {
                if (placement is null)
                    return null;
                var evidence = placement.RuntimeReadiness
                    ?? throw new RuntimeAuthorizationException("runtime_readiness_unavailable");
                var bootstrap = await ProjectBootstrapAsync(
                    actor, placement, sessionId, profileId, profiles, token).ConfigureAwait(false)
                    ?? throw new RuntimeAuthorizationException("runtime_placement_unavailable");
                return new(1, bootstrap, evidence.LeaseCreatedAt, evidence.Observation,
                    evidence.StartupBudgets, evidence.WorkspaceMountPath);
            }, cancellationToken, includeRuntimeReadiness: true);
    }

    private async Task<EnvironmentRuntimeBootstrapContext?> ProjectBootstrapAsync(
        RuntimeActorAuthorization actor, EnvironmentSandboxPlacementProjectionV1 placement,
        string sessionId, string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken token)
    {
        var ownerContext = await runtimeOwner.ReadAsync(
            actor, placement.ProjectId, placement.RunId, sessionId, token).ConfigureAwait(false);
        if (ownerContext.ContractVersion != 1 || ownerContext.TenantId != placement.TenantId ||
            ownerContext.ProjectId != placement.ProjectId || ownerContext.RunId != placement.RunId ||
            ownerContext.SessionId != sessionId || !actor.Bearer.IsUsable() ||
            placement.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_owner_context_stale");
        var owner = new EnvironmentOwnerIdentity(
            placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId);
        var profile = profiles.Resolve(owner, profileId, placement.Resource);
        return new EnvironmentRuntimeBootstrapContext(
            1, placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId,
            placement.LifecycleGeneration, placement.CurrentFencingGeneration,
            placement.ProviderFencingGeneration, placement.LeaseRevision, placement.LeaseExpiresAt,
            placement.Resource, placement.Endpoint, placement.Placement,
            profile.ProfileId, profile.ConfigureEndpoint, profile.ObservationEndpoint)
        {
            RuntimeOwnerContext = ownerContext,
            Image = placement.Image
        };
    }
}
