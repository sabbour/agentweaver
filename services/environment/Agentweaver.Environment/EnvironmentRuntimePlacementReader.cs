using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimePlacementReader(
    EnvironmentSandboxManager placements,
    EnvironmentRuntimeOwnerContextClient runtimeOwner,
    TimeProvider timeProvider)
{
    public Task<EnvironmentRuntimeWorkspaceContext?> GetResumeWorkspaceContextAsync(
        RuntimeActorAuthorization actor,
        RuntimeWorkspaceResumeRequest request,
        EnvironmentRuntimeBootstrapProfileRegistry profiles,
        Func<CancellationToken, Task> requireCurrent,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var owner = request.SourceManifest.EnvironmentFence!.Owner;
        return placements.GetCurrentPlacementCoreAsync<EnvironmentRuntimeWorkspaceContext?>(
            new(actor.Bearer.GetValue(), actor.TenantSelector), owner.ProjectId, owner.RunId, owner.EnvironmentId,
            runBoundRead: true, async (placement, token) =>
            {
                await requireCurrent(token).ConfigureAwait(false);
                if (placement is null)
                    return null;
                var saved = placement.RuntimeResume
                    ?? throw new RuntimeAuthorizationException("runtime_resume_binding_unavailable");
                if (saved.OperationId != request.OperationId ||
                    !SameJson(saved.SourceManifest, request.SourceManifest) ||
                    !SameJson(saved.SourceRelease, request.SourceRelease))
                    throw new RuntimeAuthorizationException("runtime_resume_binding_changed");
                var evidence = placement.RuntimeWorkspace
                    ?? throw new RuntimeAuthorizationException("runtime_resume_workspace_unavailable");
                var readiness = placement.RuntimeReadiness
                    ?? throw new RuntimeAuthorizationException("runtime_resume_placement_unavailable");
                if (readiness.Observation.State is not (SandboxObservedState.Pending or SandboxObservedState.Ready) ||
                    !readiness.Observation.VmIsolationVerified || !readiness.Observation.WorkspaceAttachmentVerified ||
                    readiness.Observation.StartupFailure is not null ||
                    !readiness.Observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Scheduled) ||
                    !readiness.Observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.ImageReady) ||
                    !readiness.Observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Started))
                    throw new RuntimeAuthorizationException("runtime_resume_placement_unavailable");
                var bootstrap = ProjectBootstrapProfile(
                    placement, saved.SourceRelease.HostReceipt.Registration.Binding.ProfileId, profiles);
                if (bootstrap.LeaseExpiresAt <= timeProvider.GetUtcNow() || !actor.Bearer.IsUsable())
                    throw new RuntimeAuthorizationException("runtime_resume_placement_stale");
                var context = new EnvironmentRuntimeWorkspaceContext(
                    1, bootstrap, evidence.Workspace, evidence.TransitionRevision);
                new RuntimeWorkspaceResumePlacementReceipt(
                    1, request.OperationId, request.SourceManifest.ManifestId, context).ValidateFor(request);
                await requireCurrent(token).ConfigureAwait(false);
                return context;
            }, cancellationToken, includeRuntimeReadiness: true, includeRuntimeWorkspace: true);
    }

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

    public Task<EnvironmentRuntimeWorkspaceContext?> GetWorkspaceContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        return placements.GetCurrentPlacementCoreAsync<EnvironmentRuntimeWorkspaceContext?>(
            new(actor.Bearer.GetValue(), actor.TenantSelector), projectId, runId, environmentId,
            runBoundRead: true, async (placement, token) =>
            {
                if (placement is null)
                    return null;
                var workspace = placement.RuntimeWorkspace
                    ?? throw new RuntimeAuthorizationException("runtime_workspace_unavailable");
                var bootstrap = await ProjectBootstrapAsync(
                    actor, placement, sessionId, profileId, profiles, token).ConfigureAwait(false)
                    ?? throw new RuntimeAuthorizationException("runtime_placement_unavailable");
                return new(1, bootstrap, workspace.Workspace, workspace.TransitionRevision);
            }, cancellationToken, includeRuntimeWorkspace: true);
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
        return ProjectBootstrapProfile(placement, profileId, profiles) with { RuntimeOwnerContext = ownerContext };
    }

    internal static EnvironmentRuntimeBootstrapContext ProjectBootstrapProfile(
        EnvironmentSandboxPlacementProjectionV1 placement,
        string profileId,
        EnvironmentRuntimeBootstrapProfileRegistry profiles)
    {
        var owner = new EnvironmentOwnerIdentity(
            placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId);
        var profileResource = placement.Resource;
        SandboxRuntimeHttpsEndpoint? endpoint = null;
        if (placement.RuntimeResume is { } resume)
        {
            var binding = resume.SourceRelease.HostReceipt.Registration.Binding;
            if (resume.SourceManifest.EnvironmentFence!.Owner != owner ||
                placement.LifecycleGeneration != checked(resume.SourceManifest.EnvironmentFence.LifecycleGeneration + 1) ||
                binding.ProfileId != profileId)
                throw new RuntimeAuthorizationException("runtime_resume_profile_stale");
            profileResource = new(ProviderSeam.Sandbox, binding.PlacementProviderId!,
                binding.PlacementUid, binding.PlacementGeneration);
            endpoint = placement.RuntimeReadiness?.Observation.RuntimeEndpoint?.Validate()
                ?? throw new RuntimeAuthorizationException("runtime_resume_endpoint_unavailable");
        }
        var profile = profiles.Resolve(owner, profileId, profileResource);
        return new EnvironmentRuntimeBootstrapContext(
            1, placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId,
            placement.LifecycleGeneration, placement.CurrentFencingGeneration,
            placement.ProviderFencingGeneration, placement.LeaseRevision, placement.LeaseExpiresAt,
            placement.Resource, placement.Endpoint, placement.Placement,
            profile.ProfileId,
            endpoint is null ? profile.ConfigureEndpoint : new Uri(endpoint.Address, "runtime/v1/configure"),
            profile.ObservationEndpoint)
        {
            Image = placement.Image,
            McpServer = profile.McpServer,
            RuntimeEndpoint = endpoint
        };
    }

    private static bool SameJson<T>(T left, T right) =>
        System.Text.Json.JsonElement.DeepEquals(
            System.Text.Json.JsonSerializer.SerializeToElement(left, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)),
            System.Text.Json.JsonSerializer.SerializeToElement(right, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)));
}
