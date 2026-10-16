using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Builds runtime delivery and workspace contexts from current Sandbox placement and owner evidence.</summary>
public sealed class EnvironmentRuntimePlacementReader(
    EnvironmentSandboxManager placements,
    EnvironmentRuntimeOwnerContextClient runtimeOwner,
    TimeProvider timeProvider)
{
    /// <summary>Resolves the new Sandbox placement for a resume and validates retained source-release evidence.</summary>
    public Task<EnvironmentRuntimeWorkspaceContext?> GetResumeWorkspaceContextAsync(
        RuntimeActorAuthorization actor,
        RuntimeWorkspaceResumeRequest request,
        EnvironmentRuntimeBootstrapProfileRegistry profiles,
        Func<CancellationToken, Task> requireCurrent,
        CancellationToken cancellationToken,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements = null)
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
                var sourceBinding = saved.SourceRelease.HostReceipt.Registration.Binding;
                var bootstrap = await ProjectBootstrapProfileAsync(
                    placement, sourceBinding.ProfileId, profiles, profileReplacements, token,
                    sourceBinding: sourceBinding).ConfigureAwait(false);
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

    /// <summary>Resolves a runtime bootstrap context only for the current owner, profile, and placement.</summary>
    public Task<EnvironmentRuntimeBootstrapContext?> GetBootstrapContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements = null)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        return placements.GetCurrentPlacementCoreAsync<EnvironmentRuntimeBootstrapContext?>(
            new(actor.Bearer.GetValue(), actor.TenantSelector), projectId, runId, environmentId,
            runBoundRead: true, (placement, token) => placement is null
                ? Task.FromResult<EnvironmentRuntimeBootstrapContext?>(null)
                : ProjectBootstrapAsync(actor, placement, sessionId, profileId, profiles, profileReplacements, token),
            cancellationToken, requireEndpointWhen: projection =>
                RequiresReplacementEndpoint(projection, profileId, profiles));
    }

    /// <summary>Combines the current bootstrap context with host readiness evidence for one session.</summary>
    public Task<EnvironmentRuntimeReadinessContext?> GetReadinessContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements = null)
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
                    actor, placement, sessionId, profileId, profiles, profileReplacements, token).ConfigureAwait(false)
                    ?? throw new RuntimeAuthorizationException("runtime_placement_unavailable");
                return new(1, bootstrap, evidence.LeaseCreatedAt, evidence.Observation,
                    evidence.StartupBudgets, evidence.WorkspaceMountPath);
            }, cancellationToken, includeRuntimeReadiness: true,
            requireEndpointWhen: projection => RequiresReplacementEndpoint(projection, profileId, profiles));
    }

    /// <summary>Combines current host placement with the persisted workspace binding for one session.</summary>
    public Task<EnvironmentRuntimeWorkspaceContext?> GetWorkspaceContextAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId, string environmentId,
        string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        CancellationToken cancellationToken,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements = null)
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
                    actor, placement, sessionId, profileId, profiles, profileReplacements, token).ConfigureAwait(false)
                    ?? throw new RuntimeAuthorizationException("runtime_placement_unavailable");
                return new(1, bootstrap, workspace.Workspace, workspace.TransitionRevision);
            }, cancellationToken, includeRuntimeWorkspace: true,
            requireEndpointWhen: projection => RequiresReplacementEndpoint(projection, profileId, profiles));
    }

    /// <summary>
    /// Replaces a stale profile only after current owner context and live endpoint checks succeed.
    /// The saved replacement is bound to the accepted actor, selection, and lease version.
    /// </summary>
    public Task<RuntimeBootstrapProfileResolveReceipt> ResolveCurrentProfileAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        string runId,
        string sessionId,
        string environmentId,
        string sourceProfileId,
        EnvironmentRuntimeBootstrapProfileRegistry profiles,
        IEnvironmentRuntimeBootstrapProfileReplacementStore replacements,
        EnvironmentRuntimeEndpointVerifier endpointVerifier,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        RuntimeContractValidation.ValidateIdentifier(sourceProfileId);
        return placements.GetCurrentPlacementCoreAsync(
            new(actor.Bearer.GetValue(), actor.TenantSelector),
            projectId,
            runId,
            environmentId,
            runBoundRead: true,
            async (placement, token) =>
            {
                if (placement is null || placement.LeaseExpiresAt <= timeProvider.GetUtcNow())
                    throw new RuntimeAuthorizationException("runtime_placement_unavailable");
                var owner = new EnvironmentOwnerIdentity(
                    placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId);
                var ownerContext = await ReadOwnerContextAsync(
                    actor, owner, projectId, runId, sessionId, token).ConfigureAwait(false);
                var runtimeEndpoint = placement.RuntimeReadiness?.Observation.RuntimeEndpoint?.Validate()
                    ?? throw new RuntimeAuthorizationException("runtime_bootstrap_profile_endpoint_unavailable");
                var configureEndpoint = runtimeEndpoint.ReceiverConfigureEndpoint
                    ?? throw new RuntimeAuthorizationException("runtime_bootstrap_profile_endpoint_unavailable");
                _ = runtimeEndpoint.ValidateForConfigureEndpoint(configureEndpoint);
                await endpointVerifier.VerifyLiveAsync(runtimeEndpoint, token).ConfigureAwait(false);
                await RequireSameOwnerContextAsync(
                    actor, ownerContext, owner, projectId, runId, sessionId, token).ConfigureAwait(false);

                EnvironmentRuntimeBootstrapProfileReplacement? sourceReplacement = null;
                EnvironmentRuntimeBootstrapProfileRegistration? sourceRegistration;
                if (!profiles.TryResolveSource(owner, sourceProfileId, out sourceRegistration))
                {
                    sourceReplacement = await replacements.ReadAsync(owner, sourceProfileId, token)
                        .ConfigureAwait(false);
                    if (sourceReplacement is not null &&
                        (!sourceReplacement.MatchesOwnerContext(ownerContext) ||
                         !profiles.TryResolveSource(owner, sourceReplacement.SourceProfileId, out sourceRegistration) ||
                         !sourceReplacement.MatchesSourceProfile(sourceRegistration!, sourceProfileId)))
                        throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_conflict");
                }
                if (sourceRegistration is null ||
                    sourceRegistration.Owner != owner ||
                    sourceRegistration.Profile.ProfileId !=
                        (sourceReplacement?.SourceProfileId ?? sourceProfileId))
                    throw new RuntimeAuthorizationException("runtime_delivery_unavailable");

                var sourceProfileConfigurationHash =
                    EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(sourceRegistration.Profile);
                if (sourceReplacement is { } existing &&
                    existing.MatchesCurrentPlacement(placement) &&
                    existing.RuntimeEndpoint == runtimeEndpoint &&
                    existing.Registration.Profile.ConfigureEndpoint == configureEndpoint)
                    return new RuntimeBootstrapProfileResolveReceipt(1, sourceProfileId, sourceProfileId)
                        .ValidateFor(sourceProfileId);

                var replacementProfileId = CreateReplacementProfileId(
                    owner, sourceProfileId, sourceRegistration.Profile.ProfileId,
                    ownerContext.ActorIssuer, ownerContext.ActorId, ownerContext.AcceptedSelectionHash,
                    placement, runtimeEndpoint);
                if (profiles.TryResolveSource(owner, replacementProfileId, out _))
                    throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_conflict");
                var registration = new EnvironmentRuntimeBootstrapProfileRegistration(
                    owner,
                    sourceRegistration.Profile with
                    {
                        ProfileId = replacementProfileId,
                        ConfigureEndpoint = configureEndpoint
                    },
                    placement.Resource).Validate();
                var replacement = new EnvironmentRuntimeBootstrapProfileReplacement(
                    1,
                    sourceRegistration.Profile.ProfileId,
                    sourceProfileId,
                    ownerContext.ActorIssuer,
                    ownerContext.ActorId,
                    ownerContext.AcceptedSelectionHash,
                    sourceProfileConfigurationHash,
                    EnvironmentRuntimeBootstrapProfileReplacement.ConfigurationHash(registration.Profile),
                    registration,
                    placement.LifecycleGeneration,
                    placement.CurrentFencingGeneration,
                    placement.ProviderFencingGeneration,
                    placement.LeaseRevision,
                    placement.LeaseExpiresAt,
                    runtimeEndpoint).Validate();
                if (!replacement.MatchesOwnerContext(ownerContext) ||
                    !replacement.MatchesSourceProfile(sourceRegistration, replacementProfileId))
                    throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_conflict");
                var saved = await replacements.AppendAsync(
                    replacement,
                    async currentToken =>
                    {
                        if (!replacement.MatchesOwnerContext(ownerContext) ||
                            !replacement.MatchesSourceProfile(sourceRegistration, replacementProfileId))
                            throw new RuntimeAuthorizationException(
                                "runtime_bootstrap_profile_replacement_conflict");
                        await RequireSameOwnerContextAsync(
                            actor, ownerContext, owner, projectId, runId, sessionId, currentToken)
                            .ConfigureAwait(false);
                    },
                    token).ConfigureAwait(false);
                if (saved != replacement ||
                    !saved.MatchesOwnerContext(ownerContext) ||
                    !saved.MatchesSourceProfile(sourceRegistration, replacementProfileId))
                    throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_conflict");
                await RequireSameOwnerContextAsync(
                    actor, ownerContext, owner, projectId, runId, sessionId, token).ConfigureAwait(false);
                return new RuntimeBootstrapProfileResolveReceipt(
                    1, sourceProfileId, saved.Registration.Profile.ProfileId).ValidateFor(sourceProfileId);
            },
            cancellationToken,
            includeRuntimeEndpoint: true);
    }

    private async Task<EnvironmentRuntimeBootstrapContext?> ProjectBootstrapAsync(
        RuntimeActorAuthorization actor, EnvironmentSandboxPlacementProjectionV1 placement,
        string sessionId, string profileId, EnvironmentRuntimeBootstrapProfileRegistry profiles,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements,
        CancellationToken token)
    {
        var ownerContext = await runtimeOwner.ReadAsync(
            actor, placement.ProjectId, placement.RunId, sessionId, token).ConfigureAwait(false);
        if (ownerContext.ContractVersion != 1 || ownerContext.TenantId != placement.TenantId ||
            ownerContext.ProjectId != placement.ProjectId || ownerContext.RunId != placement.RunId ||
            ownerContext.SessionId != sessionId || !actor.Bearer.IsUsable() ||
            placement.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_owner_context_stale");
        var bootstrap = await ProjectBootstrapProfileAsync(
            placement, profileId, profiles, profileReplacements, token, ownerContext: ownerContext)
            .ConfigureAwait(false);
        await RequireSameOwnerContextAsync(
            actor,
            ownerContext,
            new(placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId),
            placement.ProjectId,
            placement.RunId,
            sessionId,
            token).ConfigureAwait(false);
        return bootstrap with { RuntimeOwnerContext = ownerContext };
    }

    private async Task<RuntimeOwnerContext> ReadOwnerContextAsync(
        RuntimeActorAuthorization actor,
        EnvironmentOwnerIdentity owner,
        string projectId,
        string runId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var context = await runtimeOwner.ReadAsync(
            actor, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
        if (context.ContractVersion != 1 || context.TenantId != owner.TenantId ||
            context.ProjectId != projectId || context.RunId != runId ||
            context.SessionId != sessionId || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_owner_context_stale");
        return context;
    }

    private async Task RequireSameOwnerContextAsync(
        RuntimeActorAuthorization actor,
        RuntimeOwnerContext expected,
        EnvironmentOwnerIdentity owner,
        string projectId,
        string runId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (await ReadOwnerContextAsync(actor, owner, projectId, runId, sessionId, cancellationToken)
                .ConfigureAwait(false) != expected)
            throw new RuntimeAuthorizationException("runtime_owner_context_stale");
    }

    private async Task<EnvironmentRuntimeBootstrapContext> ProjectBootstrapProfileAsync(
        EnvironmentSandboxPlacementProjectionV1 placement,
        string profileId,
        EnvironmentRuntimeBootstrapProfileRegistry profiles,
        IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements,
        CancellationToken cancellationToken,
        RuntimeOwnerContext? ownerContext = null,
        RuntimeBinding? sourceBinding = null)
    {
        var target = GetProfileTarget(placement, profileId);
        if (profiles.TryResolve(target.Owner, profileId, target.Resource, out var configured))
            return ProjectBootstrapProfile(placement, profileId, configured!);

        var replacement = profileReplacements is null
            ? null
            : await profileReplacements.ReadAsync(target.Owner, profileId, cancellationToken).ConfigureAwait(false);
        if (replacement is null || replacement.Registration.Placement != target.Resource)
            throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
        var identityMatches = ownerContext is not null
            ? replacement.MatchesOwnerContext(ownerContext)
            : sourceBinding is not null && replacement.MatchesRuntimeBinding(sourceBinding);
        if (!identityMatches ||
            !profiles.TryResolveSource(target.Owner, replacement.SourceProfileId, out var sourceRegistration) ||
            !replacement.MatchesSourceProfile(sourceRegistration!, profileId))
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_conflict");

        if (placement.RuntimeResume is { } resume)
        {
            var binding = resume.SourceRelease.HostReceipt.Registration.Binding;
            if (profileId == binding.ProfileId)
            {
                if (replacement.LifecycleGeneration != binding.EnvironmentLifecycleGeneration ||
                    replacement.CurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
                    replacement.ProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
                    replacement.LeaseRevision != binding.EnvironmentLeaseRevision ||
                    replacement.Registration.Profile.ConfigureEndpoint != binding.ConfigureEndpoint)
                    throw new RuntimeAuthorizationException("runtime_resume_profile_stale");
            }
            else if (replacement.RequestedSourceProfileId != binding.ProfileId ||
                     !replacement.MatchesCurrentPlacement(placement) ||
                     replacement.RuntimeEndpoint != target.RuntimeEndpoint ||
                     replacement.Registration.Profile.ConfigureEndpoint != binding.ConfigureEndpoint)
                throw new RuntimeAuthorizationException("runtime_resume_profile_stale");
        }
        else
        {
            if (!replacement.MatchesCurrentPlacement(placement) ||
                placement.RuntimeReadiness?.Observation.RuntimeEndpoint != replacement.RuntimeEndpoint)
                throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
        }
        return ProjectBootstrapProfile(placement, profileId, replacement.Registration);
    }

    /// <summary>Projects a registered profile into the current placement context.</summary>
    internal static EnvironmentRuntimeBootstrapContext ProjectBootstrapProfile(
        EnvironmentSandboxPlacementProjectionV1 placement,
        string profileId,
        EnvironmentRuntimeBootstrapProfileRegistry profiles)
    {
        var target = GetProfileTarget(placement, profileId);
        var registration = profiles.Resolve(target.Owner, profileId, target.Resource);
        return ProjectBootstrapProfile(placement, profileId, new EnvironmentRuntimeBootstrapProfileRegistration(
            target.Owner, registration, target.Resource));
    }

    /// <summary>Builds a validated bootstrap context from the matching profile registration and runtime endpoint.</summary>
    private static EnvironmentRuntimeBootstrapContext ProjectBootstrapProfile(
        EnvironmentSandboxPlacementProjectionV1 placement,
        string profileId,
        EnvironmentRuntimeBootstrapProfileRegistration registration)
    {
        var target = GetProfileTarget(placement, profileId);
        if (registration.Owner != target.Owner ||
            registration.Profile.ProfileId != profileId ||
            registration.Placement != target.Resource)
            throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
        var profile = registration.Profile;
        var endpoint = target.RuntimeEndpoint;
        var configureEndpoint = endpoint?.ReceiverConfigureEndpoint ?? profile.ConfigureEndpoint;
        if (endpoint is not null)
            _ = endpoint.ValidateForConfigureEndpoint(configureEndpoint);
        return new EnvironmentRuntimeBootstrapContext(
            1, placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId,
            placement.LifecycleGeneration, placement.CurrentFencingGeneration,
            placement.ProviderFencingGeneration, placement.LeaseRevision, placement.LeaseExpiresAt,
            placement.Resource, placement.Endpoint, placement.Placement,
            profile.ProfileId,
            configureEndpoint,
            profile.ObservationEndpoint)
        {
            Image = placement.Image,
            McpServer = profile.McpServer,
            RuntimeEndpoint = endpoint
        };
    }

    /// <summary>Identifies the correct owner and resource when resolving an active or resume placement.</summary>
    private static ProfileTarget GetProfileTarget(
        EnvironmentSandboxPlacementProjectionV1 placement,
        string profileId)
    {
        var owner = new EnvironmentOwnerIdentity(
            placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId);
        var profileResource = placement.Resource;
        var endpoint = placement.RuntimeReadiness?.Observation.RuntimeEndpoint?.Validate();
        if (placement.RuntimeResume is { } resume)
        {
            var binding = resume.SourceRelease.HostReceipt.Registration.Binding;
            if (resume.SourceManifest.EnvironmentFence!.Owner != owner ||
                placement.LifecycleGeneration != checked(resume.SourceManifest.EnvironmentFence.LifecycleGeneration + 1))
                throw new RuntimeAuthorizationException("runtime_resume_profile_stale");
            profileResource = profileId == binding.ProfileId
                ? new(ProviderSeam.Sandbox, binding.PlacementProviderId!,
                    binding.PlacementUid, binding.PlacementGeneration)
                : placement.Resource;
            endpoint = placement.RuntimeReadiness?.Observation.RuntimeEndpoint?.Validate()
                ?? throw new RuntimeAuthorizationException("runtime_resume_endpoint_unavailable");
        }
        return new(owner, profileResource, endpoint);
    }

    /// <summary>Checks whether the requested profile needs a persisted replacement endpoint before projection.</summary>
    private static bool RequiresReplacementEndpoint(
        EnvironmentSandboxPlacementProjectionV1 placement,
        string profileId,
        EnvironmentRuntimeBootstrapProfileRegistry profiles)
    {
        var owner = new EnvironmentOwnerIdentity(
            placement.TenantId, placement.ProjectId, placement.RunId, placement.EnvironmentId);
        return !profiles.TryResolve(owner, profileId, placement.Resource, out _);
    }

    /// <summary>Derives a deterministic replacement profile ID from actor, selection, placement, and endpoint data.</summary>
    internal static string CreateReplacementProfileId(
        EnvironmentOwnerIdentity owner,
        string requestedSourceProfileId,
        string sourceProfileId,
        string actorIssuer,
        string actorId,
        string acceptedSelectionHash,
        EnvironmentSandboxPlacementProjectionV1 placement,
        SandboxRuntimeHttpsEndpoint endpoint)
    {
        var identity = string.Join('\0',
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            requestedSourceProfileId,
            sourceProfileId,
            actorIssuer,
            actorId,
            acceptedSelectionHash,
            placement.Resource.ProviderId,
            placement.Resource.ResourceId,
            placement.Resource.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            placement.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            placement.CurrentFencingGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            placement.ProviderFencingGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            placement.LeaseRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            placement.LeaseExpiresAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            endpoint.Address.AbsoluteUri,
            endpoint.PodUid,
            endpoint.ServiceUid);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return $"{requestedSourceProfileId[..Math.Min(requestedSourceProfileId.Length, 220)]}.r.{hash}";
    }

    /// <summary>Bundles the owner, exact Sandbox resource, and optional runtime endpoint used for profile resolution.</summary>
    private sealed record ProfileTarget(
        EnvironmentOwnerIdentity Owner,
        ProviderResourceRef Resource,
        SandboxRuntimeHttpsEndpoint? RuntimeEndpoint);

    private static bool SameJson<T>(T left, T right) =>
        System.Text.Json.JsonElement.DeepEquals(
            System.Text.Json.JsonSerializer.SerializeToElement(left, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)),
            System.Text.Json.JsonSerializer.SerializeToElement(right, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)));
}
