using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed partial class EnvironmentRuntimeBootstrapDelivery
{
    private static readonly JsonSerializerOptions SuspendJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnvironmentRuntimeWorkspaceContext> ReadSuspendWorkspaceAsync(
        RuntimeHostSuspendRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Proof);
        ArgumentNullException.ThrowIfNull(request.Proof.Registration);
        var registration = await ReadCurrentAsync(
            request.Proof.Registration.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        if (registration != request.Proof.Registration)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        var placement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        var context = await placements.GetWorkspaceContextAsync(
            actor, binding.ProjectId, binding.RunId, binding.SessionId, binding.EnvironmentId,
            binding.ProfileId, profiles, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_suspend_workspace_unavailable");
        if (!SameSuspendJson(context.Placement, placement))
            throw new RuntimeAuthorizationException("runtime_suspend_placement_stale");
        return context;
    }

    public async Task RequireCurrentSuspendAsync(
        RuntimeHostSuspendRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Proof);
        ArgumentNullException.ThrowIfNull(request.Proof.Registration);
        ArgumentNullException.ThrowIfNull(request.WorkspaceFlush);
        request.WorkspaceFlush.Validate();
        if (request.OperationId == Guid.Empty || request.ManifestId == Guid.Empty || request.PhaseVersion < 1 ||
            request.Proof.ContractVersion != 1 || request.Proof.SourceGrantId == Guid.Empty ||
            request.Proof.SourceGrantRevision < 1 || request.Proof.Purpose != RuntimeCredentialPurpose.Observe)
            throw new RuntimeAuthorizationException("runtime_suspend_request_invalid");
        var registration = await ReadCurrentAsync(
            request.Proof.Registration.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        if (registration != request.Proof.Registration)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        var confirmed = await RuntimeOwnerHttpTransport.SendAsync<RuntimeHostSuspendRequest>(
            client, options.OrchestratorOwnerAddress, "/internal/runtime/suspend/require-current",
            actor, request, cancellationToken).ConfigureAwait(false);
        if (!SameSuspendJson(confirmed, request) || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_suspend_authority_stale");
    }

    public async Task<WorkspaceVolumeFlushEvidence> SuspendWorkspaceAsync(
        RuntimeHostSuspendRequest request,
        WorkspaceVolumeFlushRequest exactWorkspace,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.WorkspaceFlush);
        exactWorkspace.Validate();
        var supplied = request.WorkspaceFlush;
        supplied.Validate();
        if (!SameSuspendJson(supplied.Spec, exactWorkspace.Spec) ||
            supplied.EnvironmentFence != exactWorkspace.EnvironmentFence ||
            supplied.Resource != exactWorkspace.Resource ||
            supplied.ResourceGeneration != exactWorkspace.ResourceGeneration ||
            supplied.ExpectedDataGeneration != exactWorkspace.ExpectedDataGeneration ||
            supplied.IdempotencyKey != exactWorkspace.IdempotencyKey ||
            supplied.ProviderBinding is not null &&
                !SameSuspendJson(supplied.ProviderBinding, exactWorkspace.ProviderBinding))
            throw new RuntimeAuthorizationException("runtime_suspend_workspace_binding_invalid");
        var bound = request with { WorkspaceFlush = exactWorkspace };
        await RequireCurrentSuspendAsync(bound, actor, cancellationToken).ConfigureAwait(false);
        var registration = request.Proof.Registration;
        var binding = registration.Binding;
        var owner = exactWorkspace.EnvironmentFence.Owner;
        if (binding.TenantId != owner.TenantId || binding.ProjectId != owner.ProjectId ||
            binding.RunId != owner.RunId || binding.EnvironmentId != owner.EnvironmentId ||
            binding.EnvironmentLifecycleGeneration != exactWorkspace.EnvironmentFence.LifecycleGeneration)
            throw new RuntimeAuthorizationException("runtime_suspend_owner_mismatch");
        var placement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        var workspace = await placements.GetWorkspaceContextAsync(
            actor, binding.ProjectId, binding.RunId, binding.SessionId, binding.EnvironmentId,
            binding.ProfileId, profiles, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_suspend_workspace_unavailable");
        RequireSuspendWorkspace(workspace, exactWorkspace, registration);
        await RequireCurrentSuspendAsync(bound, actor, cancellationToken).ConfigureAwait(false);
        var endpoint = placement.ConfigureEndpoint;
        if (endpoint.AbsolutePath != "/runtime/v1/configure")
            throw new RuntimeAuthorizationException("runtime_suspend_host_version_unavailable");
        var receipt = await SendToHostAsync<RuntimeHostSuspendReceipt>(
            placement, "/runtime/v1/suspend",
            actor, bound, cancellationToken).ConfigureAwait(false);
        ValidateSuspendReceipt(receipt, bound, timeProvider.GetUtcNow());
        var flush = receipt.WorkspaceFlush ??
            throw new RuntimeAuthorizationException("runtime_suspend_receipt_invalid");
        flush.ValidateFor(exactWorkspace);
        var afterPlacement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        var afterWorkspace = await placements.GetWorkspaceContextAsync(
            actor, binding.ProjectId, binding.RunId, binding.SessionId, binding.EnvironmentId,
            binding.ProfileId, profiles, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_suspend_workspace_unavailable");
        RequireSuspendWorkspace(afterWorkspace, exactWorkspace, registration);
        await RequireCurrentSuspendAsync(bound, actor, cancellationToken).ConfigureAwait(false);
        if (!SameSuspendJson(placement, afterPlacement) || !SameSuspendJson(workspace, afterWorkspace))
            throw new RuntimeAuthorizationException("runtime_suspend_placement_stale");
        return new WorkspaceVolumeFlushEvidence(
            flush, JsonSerializer.SerializeToElement(receipt, SuspendJsonOptions)).Validate();
    }

    private static void RequireSuspendWorkspace(
        EnvironmentRuntimeWorkspaceContext context,
        WorkspaceVolumeFlushRequest request,
        RuntimeRegistration registration)
    {
        var workspace = context.Workspace.Validate();
        if (context.ContractVersion != 1 ||
            workspace.EnvironmentFence != request.EnvironmentFence ||
            workspace.Volume != new WorkspaceVolumeReference(
                request.Spec.ProjectId, request.Spec.VolumeId, request.ResourceGeneration) ||
            workspace.StorageResource != request.Resource ||
            workspace.DataGeneration != request.ExpectedDataGeneration ||
            workspace.ReadOnly ||
            workspace.Protocol != WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim ||
            workspace.SandboxResource.ProviderId != registration.Binding.PlacementProviderId ||
            workspace.SandboxResource.ResourceId != registration.Binding.PlacementUid ||
            workspace.SandboxResource.Generation != registration.Binding.PlacementGeneration)
            throw new RuntimeAuthorizationException("runtime_suspend_workspace_stale");
    }

    internal static void ValidateSuspendReceipt(
        RuntimeHostSuspendReceipt receipt,
        RuntimeHostSuspendRequest request,
        DateTimeOffset now) =>
        RuntimeWorkspaceSuspendHttpClient.ValidateHostReceipt(receipt, request, now);

    private static bool SameSuspendJson<T>(T expected, T actual) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected, SuspendJsonOptions),
            JsonSerializer.SerializeToElement(actual, SuspendJsonOptions));
}
