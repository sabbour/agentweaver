using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceVolumeApiFlushRequest(
    WorkspaceVolumeApiTransitionRequest Transition,
    RuntimeHostSuspendRequest Suspend)
{
    public WorkspaceVolumeApiFlushRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Transition);
        ArgumentNullException.ThrowIfNull(Suspend);
        ArgumentNullException.ThrowIfNull(Suspend.Proof);
        ArgumentNullException.ThrowIfNull(Suspend.Proof.Registration);
        ArgumentNullException.ThrowIfNull(Suspend.Proof.Registration.Binding);
        ArgumentNullException.ThrowIfNull(Suspend.WorkspaceFlush);
        var flush = Suspend.WorkspaceFlush.Validate();
        var binding = Suspend.Proof.Registration.Binding;
        var owner = flush.EnvironmentFence.Owner;
        if (Suspend.OperationId == Guid.Empty || Suspend.ManifestId == Guid.Empty || Suspend.PhaseVersion < 1 ||
            Suspend.Proof.ContractVersion != 1 || Suspend.Proof.SourceGrantId == Guid.Empty ||
            Suspend.Proof.SourceGrantRevision < 1 || Suspend.Proof.Purpose != RuntimeCredentialPurpose.Observe ||
            binding.TenantId != owner.TenantId || binding.ProjectId != owner.ProjectId ||
            binding.RunId != owner.RunId || binding.EnvironmentId != owner.EnvironmentId ||
            binding.EnvironmentLifecycleGeneration != flush.EnvironmentFence.LifecycleGeneration ||
            Transition.ExpectedResourceGeneration != flush.ResourceGeneration ||
            Transition.ExpectedDataGeneration != flush.ExpectedDataGeneration ||
            Transition.IdempotencyKey != flush.IdempotencyKey ||
            Transition.NextDataGeneration != checked(flush.ExpectedDataGeneration + 1))
            throw new RuntimeAuthorizationException("runtime_workspace_flush_request_invalid");
        Transition.ToTransition(flush.Spec.VolumeId, WorkspaceVolumeTransitionKind.Flush);
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceVolumeApiTransitionRequest(
    long ExpectedTransitionRevision,
    long ExpectedResourceGeneration,
    long ExpectedDataGeneration,
    string IdempotencyKey,
    long? NextDataGeneration = null)
{
    public WorkspaceVolumeTransitionRequest ToTransition(
        string volumeId,
        WorkspaceVolumeTransitionKind operation) =>
        new WorkspaceVolumeTransitionRequest(
            volumeId,
            operation,
            ExpectedTransitionRevision,
            ExpectedResourceGeneration,
            ExpectedDataGeneration,
            NextDataGeneration,
            IdempotencyKey).ValidateFor(operation);
}

public sealed record WorkspaceVolumeLifecycleResult(
    EnvironmentWorkspaceVolumeTransitionReservation Reservation,
    EnvironmentWorkspaceVolumeTransitionResult? Completion,
    EnvironmentWorkspaceVolumeSnapshot? CurrentVolume,
    EnvironmentWorkspaceVolumeCleanupStatus? CleanupStatus = null,
    string? CleanupFailureCode = null,
    string? CleanupFailureType = null)
{
    public WorkspaceVolumeFlushReceipt? FlushReceipt { get; init; }
    public RuntimeHostSuspendReceipt? HostSuspendReceipt { get; init; }

    public bool CleanupPending =>
        CleanupFailureCode is not null ||
        CleanupStatus is { State: not EnvironmentWorkspaceVolumeCleanupState.Completed } ||
        Reservation.Operation == EnvironmentWorkspaceVolumeOperation.Replace &&
        CleanupStatus is null &&
        (Completion?.TransitionState == EnvironmentWorkspaceVolumeTransitionState.Completed ||
         Reservation.Replayed &&
         Reservation.TransitionState == EnvironmentWorkspaceVolumeTransitionState.Completed);

    public bool CleanupBlocked =>
        CleanupStatus is { State: EnvironmentWorkspaceVolumeCleanupState.Blocked };
}

public sealed class RuntimeWorkspaceSuspendHttpClient(
    HttpClient client, Uri environmentOwnerAddress, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentOwnerAddress);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<WorkspaceVolumeApiFlushRequest> ResolveAsync(
        RuntimeHostSuspendRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WorkspaceFlush is not null)
            throw new RuntimeAuthorizationException("runtime_suspend_resolve_evidence_not_allowed");
        var resolved = (await RuntimeOwnerHttpTransport.SendBoundedAsync<WorkspaceVolumeApiFlushRequest>(
            client, _owner, "/internal/runtime/suspend/resolve", actor, request,
            SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_suspend_resolve_unavailable")).Validate();
        if (!SameJson(resolved.Suspend with { WorkspaceFlush = null }, request))
            throw new RuntimeAuthorizationException("runtime_suspend_resolve_binding_changed");
        return resolved;
    }

    public async Task<WorkspaceVolumeLifecycleResult> FlushAsync(
        WorkspaceVolumeApiFlushRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        request.Validate();
        var flush = request.Suspend.WorkspaceFlush!;
        var owner = flush.EnvironmentFence.Owner;
        var path = $"/api/projects/{Uri.EscapeDataString(owner.ProjectId)}" +
            $"/runs/{Uri.EscapeDataString(owner.RunId)}/environments/{Uri.EscapeDataString(owner.EnvironmentId)}" +
            $"/workspace-volumes/{Uri.EscapeDataString(flush.Spec.VolumeId)}/flush";
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<WorkspaceVolumeLifecycleResult>(
            client, _owner, path, actor, request, SessionMaterialValidation.MaximumBytes,
            false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_workspace_flush_unavailable");
        var reservation = result.Reservation;
        if (reservation is null || reservation.Operation != EnvironmentWorkspaceVolumeOperation.Flush ||
            reservation.EnvironmentFence != flush.EnvironmentFence ||
            reservation.VolumeId != flush.Spec.VolumeId ||
            reservation.ExpectedTransitionRevision != request.Transition.ExpectedTransitionRevision ||
            reservation.ExpectedResourceGeneration != flush.ResourceGeneration ||
            reservation.ExpectedDataGeneration != flush.ExpectedDataGeneration ||
            reservation.CurrentResource != flush.Resource ||
            reservation.TargetResourceGeneration != flush.ResourceGeneration ||
            reservation.TargetDataGeneration != checked(flush.ExpectedDataGeneration + 1) ||
            reservation.TargetTransitionRevision != checked(request.Transition.ExpectedTransitionRevision + 1))
            throw new RuntimeAuthorizationException("runtime_workspace_flush_receipt_invalid");
        if ((result.Completion?.TransitionState ?? reservation.TransitionState) !=
            EnvironmentWorkspaceVolumeTransitionState.Completed)
            throw new RuntimeAuthorizationException("runtime_workspace_flush_incomplete");
        if (result.Completion is { } completion &&
            (completion.OperationId != reservation.OperationId ||
             completion.EnvironmentFence != reservation.EnvironmentFence ||
             completion.VolumeId != reservation.VolumeId ||
             completion.Operation != reservation.Operation ||
             completion.TargetDataGeneration != reservation.TargetDataGeneration ||
             completion.TargetResourceGeneration != reservation.TargetResourceGeneration ||
             completion.TargetTransitionRevision != reservation.TargetTransitionRevision))
            throw new RuntimeAuthorizationException("runtime_workspace_flush_receipt_invalid");
        var host = result.HostSuspendReceipt ??
            throw new RuntimeAuthorizationException("runtime_suspend_receipt_missing");
        var receipt = result.FlushReceipt ??
            throw new RuntimeAuthorizationException("runtime_workspace_flush_receipt_missing");
        receipt.ValidateFor(flush);
        if (host.OperationId != request.Suspend.OperationId || host.ManifestId != request.Suspend.ManifestId ||
            host.Registration != request.Suspend.Proof.Registration || host.WorkspaceFlush != receipt)
            throw new RuntimeAuthorizationException("runtime_suspend_receipt_binding_invalid");
        ValidateHostReceipt(host, request.Suspend, _time.GetUtcNow());
        return result;
    }

    public static void ValidateHostReceipt(
        RuntimeHostSuspendReceipt receipt, RuntimeHostSuspendRequest request, DateTimeOffset now)
    {
        if (receipt is null || receipt.SourceGrant is null || receipt.CacheAcknowledgment is null ||
            receipt.WorkspaceFlush is null || receipt.NativeTurn is null || request.WorkspaceFlush is null)
            throw new RuntimeAuthorizationException("runtime_suspend_receipt_invalid");
        var registration = request.Proof.Registration;
        var grant = receipt.SourceGrant;
        var cache = receipt.CacheAcknowledgment;
        var binding = registration.Binding;
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
        var material = cache.Reference?.Material;
        if (receipt.ContractVersion != 1 || receipt.OperationId != request.OperationId ||
            receipt.ManifestId != request.ManifestId || receipt.Registration != registration ||
            grant.GrantId != request.Proof.SourceGrantId || grant.Revision != request.Proof.SourceGrantRevision ||
            grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision ||
            grant.Purpose != RuntimeCredentialPurpose.Observe || grant.State != RuntimeCredentialState.Active ||
            grant.Audience != binding.ObservationEndpoint ||
            grant.ExpiresAt <= now || grant.ExpiresAt > registration.ExpiresAt ||
            cache.ContractVersion != 1 || cache.Identity != identity || cache.EventId != request.ManifestId ||
            cache.Position < 1 || material?.Kind != SessionMaterialKind.SdkCache ||
            material.TenantId != binding.TenantId || material.RuntimeInstanceId != registration.RuntimeInstanceId ||
            material.RegistrationRevision != registration.Revision || material.ExecutionFence != binding.ExecutionFence ||
            material.AcceptedSelectionHash != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_suspend_receipt_invalid");
        SessionMaterialValidation.Validate(cache.Reference!);
        RuntimeNativeTurnContract.ValidateRecorded(receipt.NativeTurn);
        if (receipt.NativeTurn.Admission.Registration != registration)
            throw new RuntimeAuthorizationException("runtime_suspend_native_turn_binding_invalid");
        receipt.WorkspaceFlush.ValidateFor(request.WorkspaceFlush);
    }

    private static bool SameJson<T>(T expected, T actual) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected, JsonOptions),
            JsonSerializer.SerializeToElement(actual, JsonOptions));
}
