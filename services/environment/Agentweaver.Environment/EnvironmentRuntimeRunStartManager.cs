using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Storage.AzureFiles;

namespace Agentweaver.Environment;

internal sealed class EnvironmentRuntimeRunStartManager(
    EnvironmentLifecycleManager lifecycle,
    IEnvironmentLifecycleStore lifecycleStore,
    EnvironmentEgressManager egress,
    WorkspaceVolumeService workspaceVolumes,
    EnvironmentSandboxManager sandboxes,
    EnvironmentRuntimeOwnerContextClient ownerContexts,
    EnvironmentRuntimeBootstrapProfileRegistry profiles,
    AzureFilesCsiOptions storageOptions,
    EnvironmentRuntimeEndpointVerifier endpointVerifier,
    TimeProvider timeProvider)
{
    private const long FirstStartCapacityGiB = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeRunStartPlacementReceipt> ProvisionAsync(
        RuntimeRunStartPlacementRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var owner = request.Owner
            ?? throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        if (request.Cleanup is not null)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_request_invalid");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_start_actor_expired");
        var caller = new CurrentCallerRequest(actor.Bearer.GetValue(), actor.TenantSelector);
        var lifecycleResult = await lifecycle.RegisterCoordinatorRootAsync(
            actor, owner, cancellationToken).ConfigureAwait(false);
        var fence = lifecycleResult.Snapshot.Fence;
        if (fence.Owner.EnvironmentId != EnvironmentLifecycleManager.CreateEnvironmentId(
                owner.TenantId, owner.ProjectId, owner.RunId, RuntimeRunStartKeys.IdempotencyKey(owner)))
            throw new RuntimeAuthorizationException("runtime_start_environment_binding_invalid");

        async Task RequireCurrentAsync(CancellationToken token)
        {
            if (!actor.Bearer.IsUsable())
                throw new RuntimeAuthorizationException("runtime_start_actor_expired");
            var current = await ownerContexts.ReadAsync(
                actor, owner.ProjectId, owner.RunId, owner.SessionId, token).ConfigureAwait(false);
            if (current != owner)
                throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
            var selection = await egress.GetAuthorizedRunEnvironmentSelectionAsync(
                caller, fence.Owner, token).ConfigureAwait(false);
            if (selection.Authorization.TenantId != owner.TenantId ||
                selection.Authorization.Issuer != owner.ActorIssuer ||
                selection.Authorization.ActorId != owner.ActorId ||
                selection.RunSelection.ProjectRevision != owner.ProjectRevision ||
                selection.RunSelection.ProjectConfigurationRevision != owner.ProjectConfigurationRevision ||
                selection.RunSelection.PlatformRuntimeRevision != owner.PlatformRuntimeRevision ||
                selection.RunSelection.ContextRevision != owner.ContextRevision)
                throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
            await lifecycleStore.RequireActiveAsync(fence, token).ConfigureAwait(false);
        }

        if (storageOptions.MaximumCapacityGiB < FirstStartCapacityGiB)
            throw new RuntimeAuthorizationException("runtime_start_workspace_capacity_unavailable");

        var egressResult = await egress.ApplyCoordinatorFirstStartAndVerifyAsync(
            caller,
            owner,
            new ApplyEnvironmentEgressRequest(
                fence, 1, 0, RuntimeRunStartKeys.IdempotencyKey(owner) + ":network"),
            cancellationToken,
            RequireCurrentAsync).ConfigureAwait(false);
        if (!egressResult.ReadyForDispatch ||
            egressResult.Binding?.AppliedIntentGeneration != 1)
            throw new RuntimeAuthorizationException(
                egressResult.FailureCode ?? "runtime_start_network_unavailable");

        var volumeId = RuntimeRunStartKeys.WorkspaceVolumeId(owner);
        var spec = new WorkspaceVolumeSpec(
            volumeId,
            owner.ProjectId,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, owner.RunId),
            fence.Owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            FirstStartCapacityGiB,
            storageOptions.StorageClassName,
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Retain,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            ImmutableArray<string>.Empty);
        if (spec.CapacityGiB != FirstStartCapacityGiB ||
            spec.BindingMode != WorkspaceVolumeBindingMode.Environment ||
            spec.AccessMode != WorkspaceVolumeAccessMode.ReadWriteMany ||
            spec.Consistency != WorkspaceVolumeConsistency.Strict ||
            spec.ReclaimPolicy != WorkspaceVolumeReclaimPolicy.Retain ||
            spec.OwnerDeletionPolicy != WorkspaceVolumeOwnerDeletionPolicy.Retain ||
            !spec.AuthorizedEnvironmentIds.IsEmpty)
            throw new RuntimeAuthorizationException("runtime_start_workspace_spec_invalid");

        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var volume = await workspaceVolumes.CreateAsync(
            fence,
            new WorkspaceVolumeTransitionRequest(
                volumeId, WorkspaceVolumeTransitionKind.Create, 0, 0, 0, null,
                RuntimeRunStartKeys.IdempotencyKey(owner) + ":workspace-create", spec),
            cancellationToken,
            RequireCurrentAsync).ConfigureAwait(false);
        if (volume.VolumeId != volumeId ||
            !HasFirstStartWorkspaceSpec(volume.Specification, spec) ||
            volume.Phase is not (EnvironmentWorkspaceVolumeState.Requested or
                EnvironmentWorkspaceVolumeState.Ready or EnvironmentWorkspaceVolumeState.Bound or
                EnvironmentWorkspaceVolumeState.Attached))
            throw new RuntimeAuthorizationException("runtime_start_workspace_invalid");
        if (volume.Phase == EnvironmentWorkspaceVolumeState.Requested)
        {
            _ = await workspaceVolumes.ProvisionAsync(
                fence,
                new WorkspaceVolumeTransitionRequest(
                    volumeId, WorkspaceVolumeTransitionKind.Provision,
                    volume.TransitionRevision, volume.ResourceGeneration, volume.DataGeneration, null,
                    RuntimeRunStartKeys.IdempotencyKey(owner) + ":workspace-provision"),
                cancellationToken,
                RequireCurrentAsync).ConfigureAwait(false);
        }
        var currentVolume = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, volumeId, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_start_workspace_unavailable");
        if (currentVolume.Phase is not (EnvironmentWorkspaceVolumeState.Ready or
                EnvironmentWorkspaceVolumeState.Bound or EnvironmentWorkspaceVolumeState.Attached) ||
            currentVolume.Resource is null ||
            !HasFirstStartWorkspaceSpec(currentVolume.Specification, spec) ||
            currentVolume.DataGeneration != 0)
            throw new RuntimeAuthorizationException("runtime_start_workspace_unavailable");
        if (currentVolume.Phase == EnvironmentWorkspaceVolumeState.Ready)
        {
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            _ = await workspaceVolumes.BindAsync(
                fence,
                new WorkspaceVolumeTransitionRequest(
                    volumeId,
                    WorkspaceVolumeTransitionKind.Bind,
                    currentVolume.TransitionRevision,
                    currentVolume.ResourceGeneration,
                    currentVolume.DataGeneration,
                    null,
                    RuntimeRunStartKeys.IdempotencyKey(owner) + ":workspace-bind"),
                cancellationToken,
                RequireCurrentAsync).ConfigureAwait(false);
            currentVolume = await lifecycleStore.GetWorkspaceVolumeAsync(
                fence, volumeId, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_start_workspace_unavailable");
        }
        if (currentVolume.Phase is not (EnvironmentWorkspaceVolumeState.Bound or
                EnvironmentWorkspaceVolumeState.Attached) ||
            currentVolume.Resource is null ||
            !HasFirstStartWorkspaceSpec(currentVolume.Specification, spec) ||
            currentVolume.DataGeneration != 0)
            throw new RuntimeAuthorizationException("runtime_start_workspace_unavailable");

        var sandbox = await sandboxes.ProvisionCoordinatorFirstStartAsync(
            caller,
            owner,
            new SandboxProvisionApiRequest(
                volumeId,
                currentVolume.ResourceGeneration,
                currentVolume.DataGeneration,
                "/workspace",
                ReadOnly: false,
                egressResult.Binding.AppliedIntentGeneration,
                RuntimeRunStartKeys.SandboxIdempotencyKey(owner, request.BootstrapOperationId)),
            RequireCurrentAsync,
            cancellationToken).ConfigureAwait(false);
        if (sandbox.Resource is null || sandbox.Resource.Seam != ProviderSeam.Sandbox ||
                sandbox.State != SandboxLeaseState.Active)
                throw new RuntimeAuthorizationException("runtime_start_sandbox_unavailable");
        var placement = await sandboxes.GetCurrentRunBoundPlacementAsync(
                caller, owner.ProjectId, owner.RunId, fence.Owner.EnvironmentId, cancellationToken,
                includeRuntimeReadiness: true,
                includeRuntimeEndpoint: true)
                .ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_start_placement_unavailable");
        var readiness = placement.RuntimeReadiness;
        var observation = readiness?.Observation;
        var endpoint = observation?.RuntimeEndpoint?.Validate();
        if (!IsUsableFirstStartPlacement(
                    placement, owner, sandbox.Resource, egressResult.Binding.AppliedIntentGeneration,
                    timeProvider.GetUtcNow()) ||
                observation is null || endpoint is null)
                throw new RuntimeAuthorizationException("runtime_start_placement_stale");
        await endpointVerifier.VerifyLiveAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var currentPlacement = await sandboxes.GetCurrentRunBoundPlacementAsync(
                caller, owner.ProjectId, owner.RunId, fence.Owner.EnvironmentId, cancellationToken,
                includeRuntimeReadiness: true,
                includeRuntimeEndpoint: true).ConfigureAwait(false);
        if (currentPlacement is null ||
                !IsUsableFirstStartPlacement(
                    currentPlacement, owner, sandbox.Resource, egressResult.Binding.AppliedIntentGeneration,
                    timeProvider.GetUtcNow()) ||
                currentPlacement.LifecycleGeneration != placement.LifecycleGeneration ||
                currentPlacement.LeaseRevision != placement.LeaseRevision ||
                currentPlacement.Resource != placement.Resource ||
                currentPlacement.Endpoint != placement.Endpoint ||
                currentPlacement.Placement != placement.Placement ||
                currentPlacement.CurrentFencingGeneration != placement.CurrentFencingGeneration ||
                currentPlacement.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
                currentPlacement.LeaseExpiresAt != placement.LeaseExpiresAt ||
                currentPlacement.RuntimeReadiness?.Observation.RuntimeEndpoint != endpoint)
                throw new RuntimeAuthorizationException("runtime_start_placement_stale");

        return new RuntimeRunStartPlacementReceipt(
            1,
            fence.Owner.EnvironmentId,
            profiles.DefaultSourceProfileId,
            volumeId,
            RuntimeRunStartKeys.SandboxIdempotencyKey(owner, request.BootstrapOperationId),
            currentVolume.ResourceGeneration,
            currentVolume.DataGeneration,
            egressResult.Binding.AppliedIntentGeneration,
            sandbox.OperationId,
            currentPlacement.Resource,
            currentPlacement.LifecycleGeneration,
            currentPlacement.LeaseRevision,
            currentPlacement.CurrentFencingGeneration,
            currentPlacement.ProviderFencingGeneration,
            currentPlacement.LeaseExpiresAt).Validate();
    }

    public async Task<RuntimeRunStartCleanupReceipt> CleanupAsync(
        RuntimeRunStartPlacementRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var projection = request.Cleanup
            ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_request_invalid");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_start_actor_expired");
        var current = await ownerContexts.ReadCleanupAsync(
            actor, projection.ProjectId, projection.RunId, projection.RootSessionId, cancellationToken)
            .ConfigureAwait(false);
        projection.ValidateFor(current);
        if (projection.CleanupDisposition == "cleanup_complete")
        {
            if (!projection.ReceiptLinks.TryGetProperty("cleanup", out var savedCleanup) ||
                savedCleanup.Deserialize<RuntimeRunStartCleanupReceipt>(JsonOptions) is not { } cleanup ||
                cleanup.IntentId != projection.IntentId ||
                cleanup.LeaseState != "absent" &&
                cleanup.SandboxIdempotencyKey != RuntimeRunStartKeys.SandboxIdempotencyKey(
                    projection.TenantId, projection.ProjectId, projection.RunId,
                    projection.RootSessionId, projection.AcceptedSelectionHash,
                    projection.BootstrapOperationId))
                throw new RuntimeAuthorizationException("runtime_start_cleanup_receipt_missing");
            return cleanup.Validate();
        }

        RuntimeRunStartPlacementReceipt? placement = null;
        if (projection.ReceiptLinks.TryGetProperty("placement", out var savedPlacement))
        {
            placement = savedPlacement.Deserialize<RuntimeRunStartPlacementReceipt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_attempt_invalid");
            placement.Validate();
        }
        var sandboxIdempotencyKey = RuntimeRunStartKeys.SandboxIdempotencyKey(
            projection.TenantId, projection.ProjectId, projection.RunId,
            projection.RootSessionId, projection.AcceptedSelectionHash,
            projection.BootstrapOperationId);
        if (placement is not null && placement.SandboxIdempotencyKey != sandboxIdempotencyKey)
            throw new RuntimeAuthorizationException("runtime_start_cleanup_attempt_stale");
        if (projection.ReceiptLinks.TryGetProperty("cleanupAttempt", out var cleanupAttemptElement))
        {
            var cleanupAttempt = cleanupAttemptElement.Deserialize<RuntimeRunStartCleanupAttempt>(JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_start_cleanup_attempt_invalid");
            cleanupAttempt.Validate();
            if (cleanupAttempt.SandboxIdempotencyKey != sandboxIdempotencyKey ||
                placement is not null &&
                (cleanupAttempt.SandboxOperationId != placement.SandboxOperationId ||
                 cleanupAttempt.ResourceGeneration != placement.SandboxResource.Generation ||
                 cleanupAttempt.ProviderFencingGeneration != placement.ProviderFencingGeneration))
                throw new RuntimeAuthorizationException("runtime_start_cleanup_attempt_stale");
        }

        var caller = new CurrentCallerRequest(actor.Bearer.GetValue(), actor.TenantSelector);
        async Task RequireCurrentAsync(CancellationToken token)
        {
            if (!actor.Bearer.IsUsable())
                throw new RuntimeAuthorizationException("runtime_start_actor_expired");
            var latest = await ownerContexts.ReadCleanupAsync(
                actor, projection.ProjectId, projection.RunId, projection.RootSessionId, token)
                .ConfigureAwait(false);
            projection.ValidateFor(latest);
        }

        var result = await sandboxes.AbandonCoordinatorFirstStartAsync(
            caller, projection, placement, RequireCurrentAsync, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return new RuntimeRunStartCleanupReceipt(
                1, projection.IntentId, Guid.Empty, 0, 0, null,
                "absent", "cleanup_complete").Validate();
        var disposition = result.State == SandboxLeaseState.Released
            ? "cleanup_complete"
            : "cleanup_pending";
        return new RuntimeRunStartCleanupReceipt(
            1,
            projection.IntentId,
            result.OperationId,
            result.ResourceGeneration,
            result.ProviderFencingGeneration,
            sandboxIdempotencyKey,
            JsonNamingPolicy.CamelCase.ConvertName(result.State.ToString()),
            disposition).Validate();
    }

    public async Task<RuntimeRunStartRetirementReceipt> RetireFailedBootstrapAsync(
        RuntimeRunStartRetirementRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_start_actor_expired");
        var caller = new CurrentCallerRequest(actor.Bearer.GetValue(), actor.TenantSelector);

        async Task RequireCurrentAsync(CancellationToken token)
        {
            if (!actor.Bearer.IsUsable())
                throw new RuntimeAuthorizationException("runtime_start_actor_expired");
            var current = await ownerContexts.ReadAsync(
                actor,
                request.Owner.ProjectId,
                request.Owner.RunId,
                request.Owner.SessionId,
                token).ConfigureAwait(false);
            if (current != request.Owner)
                throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
        }

        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var placement = request.Placement;
        var result = await sandboxes.RetireCoordinatorFirstStartAsync(
            caller, request, RequireCurrentAsync, cancellationToken).ConfigureAwait(false);
        if (result.OperationId != placement.SandboxOperationId ||
            result.ResourceGeneration != placement.SandboxResource.Generation ||
            result.ProviderFencingGeneration != placement.ProviderFencingGeneration ||
            result.Resource != placement.SandboxResource)
            throw new RuntimeAuthorizationException("runtime_start_retirement_receipt_invalid");
        return new RuntimeRunStartRetirementReceipt(
            1,
            request.BootstrapOperationId,
            request.RuntimeInstanceId,
            request.RegistrationRevision,
            result.OperationId,
            result.ResourceGeneration,
            result.ProviderFencingGeneration,
            placement.SandboxIdempotencyKey,
            result.State == SandboxLeaseState.Released ? "released" : "pending")
            .ValidateFor(request);
    }

    private static bool HasFirstStartWorkspaceSpec(JsonElement value, WorkspaceVolumeSpec expected)
    {
        var saved = value.Deserialize<WorkspaceVolumeSpec>(JsonOptions);
        return saved is not null &&
            saved.VolumeId == expected.VolumeId &&
            saved.ProjectId == expected.ProjectId &&
            saved.Owner.Kind == expected.Owner.Kind &&
            saved.Owner.Id == expected.Owner.Id &&
            saved.EnvironmentId == expected.EnvironmentId &&
            saved.BindingMode == WorkspaceVolumeBindingMode.Environment &&
            saved.AccessMode == WorkspaceVolumeAccessMode.ReadWriteMany &&
            saved.CapacityGiB == FirstStartCapacityGiB &&
            saved.StorageClass == expected.StorageClass &&
            saved.Consistency == WorkspaceVolumeConsistency.Strict &&
            saved.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Retain &&
            saved.OwnerDeletionPolicy == WorkspaceVolumeOwnerDeletionPolicy.Retain &&
            saved.AuthorizedEnvironmentIds.IsEmpty;
    }

    private static bool IsUsableFirstStartPlacement(
        EnvironmentSandboxPlacementProjectionV1 placement,
        RuntimeOwnerContext owner,
        ProviderResourceRef expectedResource,
        long expectedNetworkGeneration,
        DateTimeOffset now)
    {
        var readiness = placement.RuntimeReadiness;
        var observation = readiness?.Observation;
        return placement.ContractVersion == 1 &&
            placement.TenantId == owner.TenantId &&
            placement.ProjectId == owner.ProjectId &&
            placement.RunId == owner.RunId &&
            placement.EnvironmentId == RuntimeRunStartKeys.EnvironmentId(owner) &&
            placement.IsCurrent &&
            placement.State == SandboxLeaseState.Active &&
            placement.Resource == expectedResource &&
            placement.LeaseExpiresAt > now &&
            placement.CurrentFencingGeneration == placement.ProviderFencingGeneration &&
            observation is not null &&
            observation.State is SandboxObservedState.Pending or SandboxObservedState.Ready &&
            observation.VmIsolationVerified &&
            observation.WorkspaceAttachmentVerified &&
            observation.VerifiedNetworkGeneration == expectedNetworkGeneration &&
            observation.StartupFailure is null &&
            observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Scheduled) &&
            observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.ImageReady) &&
            observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Started) &&
            observation.RuntimeEndpoint is not null;
    }
}
