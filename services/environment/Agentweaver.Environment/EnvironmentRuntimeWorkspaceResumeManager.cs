using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimeWorkspaceResumeManager(
    IEnvironmentLifecycleStore lifecycle,
    ISandboxLeaseStore leases,
    EnvironmentSandboxManager sandboxes,
    EnvironmentEgressManager egress,
    EnvironmentRuntimeBootstrapDelivery delivery,
    EnvironmentRuntimePlacementReader placements,
    EnvironmentRuntimeBootstrapProfileRegistry profiles,
    IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SelectionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<RuntimeWorkspaceResumePlacementReceipt> PrepareAsync(
        RuntimeWorkspaceResumeRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.RestoredWorkspace is not null)
            throw new RuntimeAuthorizationException("runtime_resume_prepare_workspace_not_allowed");
        await delivery.RequireCurrentResumeAsync(request, actor, cancellationToken).ConfigureAwait(false);
        var caller = new CurrentCallerRequest(actor.Bearer.GetValue(), actor.TenantSelector);
        var sourceFence = request.SourceManifest.EnvironmentFence!;
        var selection = await egress.GetAuthorizedRunSelectionAsync(
            caller, sourceFence.Owner, cancellationToken).ConfigureAwait(false);
        var current = await lifecycle.GetAsync(sourceFence.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException("environment_unknown", "The resume Environment is missing.");
        var sourceLease = await ReadSourceAsync(
            request, current.Fence, selection, cancellationToken).ConfigureAwait(false);
        var sourceProvision = EnvironmentSandboxManager.ReadProvisionRequest(sourceLease);
        var sourceBinding = request.SourceRelease.HostReceipt.Registration.Binding;
        var sourceResource = sourceLease.ProvisionedResource!.Resource;
        if (!profiles.TryResolve(sourceFence.Owner, sourceBinding.ProfileId, sourceResource, out _))
        {
            var replacement = profileReplacements is null
                ? null
                : await profileReplacements.ReadAsync(
                    sourceFence.Owner, sourceBinding.ProfileId, cancellationToken).ConfigureAwait(false);
            if (replacement is null ||
                replacement.Registration.Placement != sourceResource ||
                replacement.LifecycleGeneration != sourceBinding.EnvironmentLifecycleGeneration ||
                replacement.CurrentFencingGeneration != sourceBinding.EnvironmentCurrentFencingGeneration ||
                replacement.ProviderFencingGeneration != sourceBinding.EnvironmentProviderFencingGeneration ||
                replacement.LeaseRevision != sourceBinding.EnvironmentLeaseRevision)
                throw new RuntimeAuthorizationException("runtime_resume_profile_stale");
        }
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var transitioned = await lifecycle.TransitionAsync(
            new(sourceFence.Owner, sourceFence.LifecycleGeneration, EnvironmentLifecycleState.Active,
                OperationKey(request, "lifecycle")),
            cancellationToken, RequireCurrentAsync).ConfigureAwait(false);
        var targetFence = new EnvironmentGenerationFence(
            sourceFence.Owner, checked(sourceFence.LifecycleGeneration + 1));
        if (transitioned.Snapshot.Fence != targetFence)
            throw new RuntimeAuthorizationException("runtime_resume_lifecycle_changed");
        await lifecycle.RequireActiveAsync(targetFence, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var networkGeneration = checked(request.SourceManifest.NetworkIntentGeneration!.Value + 1);
        var network = await egress.ApplyAndVerifyAsync(caller,
            new(targetFence, networkGeneration, request.SourceManifest.NetworkIntentGeneration.Value,
                OperationKey(request, "network")), cancellationToken, RequireCurrentAsync).ConfigureAwait(false);
        if (!network.ReadyForDispatch || network.AppliedState?.ObjectVerified != true ||
            network.AppliedState.AppliedIntentGeneration != networkGeneration)
            throw new EnvironmentLifecycleException(
                network.FailureCode ?? "runtime_resume_network_unavailable",
                network.FailureMessage ?? "The restored Network policy has not been verified.");
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var result = await sandboxes.ProvisionResumeAsync(caller,
            new(sourceProvision.VolumeId, sourceProvision.VolumeResourceGeneration,
                request.SourceManifest.WorkspaceDataGeneration!.Value, sourceProvision.MountPath,
                false, networkGeneration, OperationKey(request, "placement")),
            request, RequireCurrentAsync, cancellationToken).ConfigureAwait(false);
        if (result.State != SandboxLeaseState.Active)
            throw new RuntimeAuthorizationException("runtime_resume_placement_unavailable");
        var workspace = await placements.GetResumeWorkspaceContextAsync(
            actor, request, profiles, RequireCurrentAsync, cancellationToken, profileReplacements).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_placement_unavailable");
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeWorkspaceResumePlacementReceipt(
            1, request.OperationId, request.SourceManifest.ManifestId, workspace).ValidateFor(request);

        async Task RequireCurrentAsync(CancellationToken token)
        {
            await delivery.RequireCurrentResumeAsync(request, actor, token).ConfigureAwait(false);
            await egress.EnsureAuthorizationUnchangedAsync(
                caller, sourceFence.Owner, selection.Authorization, requireRunSelection: true, token)
                .ConfigureAwait(false);
        }
    }

    public async Task<RuntimeWorkspaceResumeVerificationReceipt> VerifyAsync(
        RuntimeWorkspaceResumeRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.RestoredWorkspace is null)
            throw new RuntimeAuthorizationException("runtime_resume_verify_workspace_required");
        await delivery.RequireCurrentResumeAsync(request, actor, cancellationToken).ConfigureAwait(false);
        var caller = new CurrentCallerRequest(actor.Bearer.GetValue(), actor.TenantSelector);
        var targetFence = request.RestoredWorkspace.Workspace.EnvironmentFence;
        var selection = await egress.GetAuthorizedRunSelectionAsync(
            caller, targetFence.Owner, cancellationToken).ConfigureAwait(false);
        await ReadSourceAsync(request, targetFence, selection, cancellationToken).ConfigureAwait(false);
        var before = await placements.GetResumeWorkspaceContextAsync(
            actor, request, profiles, RequireCurrentAsync, cancellationToken, profileReplacements).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_placement_unavailable");
        if (!SameJson(before, request.RestoredWorkspace))
            throw new RuntimeAuthorizationException("runtime_resume_placement_changed");
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var host = await delivery.VerifyResumeWorkspaceAsync(request, actor, cancellationToken).ConfigureAwait(false);
        var after = await placements.GetResumeWorkspaceContextAsync(
            actor, request, profiles, RequireCurrentAsync, cancellationToken, profileReplacements).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_placement_unavailable");
        if (!SameJson(before, after))
            throw new RuntimeAuthorizationException("runtime_resume_placement_changed");
        var networkGeneration = checked(request.SourceManifest.NetworkIntentGeneration!.Value + 1);
        await egress.VerifyNetworkForSandboxAsync(
            caller, targetFence, selection, networkGeneration, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeWorkspaceResumeVerificationReceipt(
            1, request.OperationId, request.SourceManifest.ManifestId,
            after, host, networkGeneration).ValidateFor(request);

        async Task RequireCurrentAsync(CancellationToken token)
        {
            await delivery.RequireCurrentResumeAsync(request, actor, token).ConfigureAwait(false);
            await egress.EnsureAuthorizationUnchangedAsync(
                caller, targetFence.Owner, selection.Authorization, requireRunSelection: true, token)
                .ConfigureAwait(false);
        }
    }

    private async Task<SandboxLeaseSnapshot> ReadSourceAsync(
        RuntimeWorkspaceResumeRequest request,
        EnvironmentGenerationFence currentFence,
        EnvironmentEgressManager.AuthorizedSelection selection,
        CancellationToken cancellationToken)
    {
        var sourceFence = request.SourceManifest.EnvironmentFence!;
        if (currentFence.Owner != sourceFence.Owner ||
            currentFence.LifecycleGeneration != sourceFence.LifecycleGeneration &&
                currentFence.LifecycleGeneration != checked(sourceFence.LifecycleGeneration + 1))
            throw new RuntimeAuthorizationException("runtime_resume_lifecycle_changed");
        var binding = request.SourceRelease.HostReceipt.Registration.Binding;
        var lease = await leases.GetAsync(
            currentFence, binding.PlacementGeneration, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_source_placement_missing");
        var resource = new ProviderResourceRef(ProviderSeam.Sandbox, binding.PlacementProviderId!,
            binding.PlacementUid, binding.PlacementGeneration);
        if (lease.Fence != sourceFence || lease.State != SandboxLeaseState.Released || lease.IsCurrent ||
            lease.ProvisionedResource?.Resource != resource ||
            lease.ProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            !SameJson(lease.ProvisionIntent.SelectionSnapshot,
                JsonSerializer.SerializeToElement(selection.RunSelection, SelectionJsonOptions)))
            throw new RuntimeAuthorizationException("runtime_resume_source_placement_changed");
        var source = EnvironmentSandboxManager.ReadProvisionRequest(lease);
        if (source.VolumeId != request.SourceManifest.WorkspaceVolume!.VolumeId ||
            source.VolumeResourceGeneration != request.SourceManifest.WorkspaceVolume.ResourceGeneration ||
            source.DataGeneration != request.SourceRelease.HostReceipt.WorkspaceFlush!.ExpectedDataGeneration ||
            source.ReadOnly || source.NetworkPolicyGeneration != request.SourceManifest.NetworkIntentGeneration)
            throw new RuntimeAuthorizationException("runtime_resume_source_workspace_changed");
        var evidence = await lifecycle.GetRetainedWorkspaceVolumeFlushEvidenceAsync(
            currentFence, sourceFence, source.VolumeId, request.SourceRelease.FlushTransitionRevision,
            cancellationToken).ConfigureAwait(false);
        if (evidence?.RuntimeHostReceipt is not { } recorded ||
            !SameJson(evidence.Receipt, request.SourceRelease.HostReceipt.WorkspaceFlush) ||
            !SameJson(recorded, JsonSerializer.SerializeToElement(request.SourceRelease.HostReceipt, JsonOptions)))
            throw new RuntimeAuthorizationException("runtime_resume_source_flush_missing");
        var volume = await lifecycle.GetWorkspaceVolumeAsync(
            currentFence, source.VolumeId, cancellationToken).ConfigureAwait(false);
        if (volume is null || volume.Resource != request.SourceManifest.WorkspaceResource ||
            volume.ResourceGeneration != source.VolumeResourceGeneration ||
            volume.DataGeneration != request.SourceManifest.WorkspaceDataGeneration ||
            currentFence == sourceFence &&
                (volume.Phase != EnvironmentWorkspaceVolumeState.Bound ||
                 volume.TransitionRevision != checked(request.SourceRelease.FlushTransitionRevision + 1)))
            throw new RuntimeAuthorizationException("runtime_resume_retained_workspace_changed");
        await lifecycle.RequireActiveAsync(currentFence, cancellationToken).ConfigureAwait(false);
        return lease;
    }

    private static string OperationKey(RuntimeWorkspaceResumeRequest request, string stage) =>
        $"resume:{request.OperationId:N}:{request.SourceManifest.ManifestId:N}:{stage}";

    private static bool SameJson<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, JsonOptions), JsonSerializer.SerializeToElement(right, JsonOptions));
}
