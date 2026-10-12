using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeWorkspaceResumeRequest(
    int ContractVersion,
    Guid OperationId,
    long PhaseVersion,
    SessionSuspendResumeManifest SourceManifest,
    RuntimeSuspendPlacementReleaseRequest SourceRelease)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentRuntimeWorkspaceContext? RestoredWorkspace { get; init; }

    public RuntimeWorkspaceResumeRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(SourceManifest);
        ArgumentNullException.ThrowIfNull(SourceRelease);
        SourceManifest.Validate();
        SourceRelease.Validate();
        var host = SourceRelease.HostReceipt;
        var binding = host.Registration.Binding;
        var flush = host.WorkspaceFlush!;
        if (ContractVersion != 1 || OperationId == Guid.Empty || OperationId == host.OperationId ||
            PhaseVersion < 1 || SourceManifest.State != SessionSuspendResumeManifestState.Suspended ||
            SourceManifest.ManifestId != host.ManifestId ||
            SourceManifest.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
            SourceManifest.EnvironmentFence != flush.EnvironmentFence ||
            SourceManifest.EnvironmentFence.LifecycleGeneration == long.MaxValue ||
            SourceManifest.NetworkIntentGeneration == long.MaxValue ||
            SourceManifest.WorkspaceVolume != flush.Volume ||
            SourceManifest.WorkspaceResource != flush.Resource ||
            SourceManifest.WorkspaceDataGeneration != flush.DataGeneration ||
            SourceManifest.WorkspaceTreeSha256 != flush.TreeSha256 ||
            !RuntimeWorkspaceResumeHttpClient.SameJson(
                SourceManifest.CacheAcknowledgment, host.CacheAcknowledgment))
            throw new RuntimeAuthorizationException("runtime_resume_request_invalid");
        if (SourceManifest.WorkspaceTreeSha256 is null ||
            SourceManifest.WorkspaceProviderCheckpointId is not null ||
            SourceManifest.GuestSnapshotResource is not null)
            throw new RuntimeAuthorizationException("runtime_resume_content_unsupported");
        RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(host);
        if (RestoredWorkspace is not null)
            ValidateWorkspace(RestoredWorkspace);
        return this;
    }

    internal void ValidateWorkspace(EnvironmentRuntimeWorkspaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Placement);
        ArgumentNullException.ThrowIfNull(context.Workspace);
        var workspace = context.Workspace.Validate();
        var placement = context.Placement;
        var sourceFence = SourceManifest.EnvironmentFence!;
        var sourceBinding = SourceRelease.HostReceipt.Registration.Binding;
        var targetFence = new EnvironmentGenerationFence(
            sourceFence.Owner, checked(sourceFence.LifecycleGeneration + 1));
        if (context.ContractVersion != 1 || placement.ContractVersion != 1 ||
            workspace.EnvironmentFence != targetFence ||
            workspace.Volume != SourceManifest.WorkspaceVolume ||
            workspace.StorageResource != SourceManifest.WorkspaceResource ||
            workspace.DataGeneration != SourceManifest.WorkspaceDataGeneration ||
            workspace.ReadOnly ||
            workspace.Protocol != WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim ||
            workspace.SandboxResource == new ProviderResourceRef(
                ProviderSeam.Sandbox, sourceBinding.PlacementProviderId!,
                sourceBinding.PlacementUid, sourceBinding.PlacementGeneration) ||
            placement.TenantId != targetFence.Owner.TenantId ||
            placement.ProjectId != targetFence.Owner.ProjectId ||
            placement.RunId != targetFence.Owner.RunId ||
            placement.EnvironmentId != targetFence.Owner.EnvironmentId ||
            placement.LifecycleGeneration != targetFence.LifecycleGeneration ||
            placement.Resource != workspace.SandboxResource ||
            placement.ProfileId != sourceBinding.ProfileId ||
            placement.CurrentFencingGeneration < 1 ||
            placement.CurrentFencingGeneration != placement.ProviderFencingGeneration ||
            placement.LeaseRevision < 1 ||
            !RuntimeContractValidation.IsHttpsEndpoint(placement.ConfigureEndpoint) ||
            placement.ConfigureEndpoint.AbsolutePath != "/runtime/v1/configure" ||
            !RuntimeContractValidation.IsHttpsEndpoint(placement.ObservationEndpoint) ||
            context.TransitionRevision <= checked(SourceRelease.FlushTransitionRevision + 1))
            throw new RuntimeAuthorizationException("runtime_resume_workspace_invalid");
    }
}

public sealed record RuntimeWorkspaceResumePlacementReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    EnvironmentRuntimeWorkspaceContext Workspace)
{
    public RuntimeWorkspaceResumePlacementReceipt ValidateFor(RuntimeWorkspaceResumeRequest request)
    {
        request.Validate();
        if (ContractVersion != 1 || OperationId != request.OperationId ||
            SourceManifestId != request.SourceManifest.ManifestId)
            throw new RuntimeAuthorizationException("runtime_resume_placement_receipt_invalid");
        request.ValidateWorkspace(Workspace);
        if (request.RestoredWorkspace is not null &&
            !RuntimeWorkspaceResumeHttpClient.SameJson(Workspace, request.RestoredWorkspace))
            throw new RuntimeAuthorizationException("runtime_resume_placement_changed");
        return this;
    }
}

public sealed record RuntimeHostWorkspaceResumeReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    EnvironmentRuntimeWorkspaceContext Workspace,
    string ActualTreeSha256)
{
    public RuntimeHostWorkspaceResumeReceipt ValidateFor(RuntimeWorkspaceResumeRequest request)
    {
        request.Validate();
        if (request.RestoredWorkspace is null ||
            ContractVersion != 1 || OperationId != request.OperationId ||
            SourceManifestId != request.SourceManifest.ManifestId ||
            !RuntimeWorkspaceResumeHttpClient.SameJson(Workspace, request.RestoredWorkspace) ||
            ActualTreeSha256 != request.SourceManifest.WorkspaceTreeSha256)
            throw new RuntimeAuthorizationException("runtime_resume_content_receipt_invalid");
        return this;
    }
}

public sealed record RuntimeWorkspaceResumeVerificationReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid SourceManifestId,
    EnvironmentRuntimeWorkspaceContext Workspace,
    RuntimeHostWorkspaceResumeReceipt HostReceipt,
    long NetworkIntentGeneration)
{
    public RuntimeWorkspaceResumeVerificationReceipt ValidateFor(RuntimeWorkspaceResumeRequest request)
    {
        request.Validate();
        if (HostReceipt is null)
            throw new RuntimeAuthorizationException("runtime_resume_verification_receipt_invalid");
        HostReceipt.ValidateFor(request);
        if (request.RestoredWorkspace is null ||
            ContractVersion != 1 || OperationId != request.OperationId ||
            SourceManifestId != request.SourceManifest.ManifestId ||
            !RuntimeWorkspaceResumeHttpClient.SameJson(Workspace, request.RestoredWorkspace) ||
            NetworkIntentGeneration != checked(request.SourceManifest.NetworkIntentGeneration!.Value + 1))
            throw new RuntimeAuthorizationException("runtime_resume_verification_receipt_invalid");
        return this;
    }
}

public sealed class RuntimeWorkspaceResumeHttpClient(HttpClient client, Uri environmentOwnerAddress)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentOwnerAddress);

    public async Task<RuntimeWorkspaceResumePlacementReceipt> PrepareAsync(
        RuntimeWorkspaceResumeRequest request, RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.RestoredWorkspace is not null)
            throw new RuntimeAuthorizationException("runtime_resume_prepare_workspace_not_allowed");
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeWorkspaceResumePlacementReceipt>(
            client, _owner, "/internal/runtime/resume/prepare", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_prepare_unavailable");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        return result.ValidateFor(request);
    }

    public async Task<RuntimeWorkspaceResumeVerificationReceipt> VerifyAsync(
        RuntimeWorkspaceResumeRequest request, RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.RestoredWorkspace is null)
            throw new RuntimeAuthorizationException("runtime_resume_verify_workspace_required");
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeWorkspaceResumeVerificationReceipt>(
            client, _owner, "/internal/runtime/resume/verify", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_resume_verify_unavailable");
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        return result.ValidateFor(request);
    }

    public static bool SameJson<T>(T expected, T actual) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected, JsonOptions),
            JsonSerializer.SerializeToElement(actual, JsonOptions));
}
