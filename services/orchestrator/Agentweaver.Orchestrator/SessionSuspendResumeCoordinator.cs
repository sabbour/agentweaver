using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Npgsql;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

internal enum SessionSuspendResumeOperationKind
{
    Suspend,
    Resume
}

internal enum SessionSuspendResumeOperationPhase
{
    NotApplicable,
    Reserved,
    Fenced,
    Draining,
    Checkpointed,
    JournalFlushed,
    WorkspaceFlushed,
    ManifestCommitted,
    PlacementReleased,
    ManifestValidated,
    EnvironmentReady,
    EgressVerified,
    FenceAdvanced,
    Dispatched,
    Interrupted
}

internal static class SessionSuspendResumePhaseMachine
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static bool CanAdvance(
        SessionSuspendResumeOperationKind kind,
        SessionSuspendResumeOperationPhase current,
        SessionSuspendResumeOperationPhase next)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(current) || !Enum.IsDefined(next) ||
            IsTerminal(current))
            return false;

        if (next == SessionSuspendResumeOperationPhase.Interrupted)
            return current != SessionSuspendResumeOperationPhase.NotApplicable;

        return (kind, current, next) switch
        {
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.Reserved,
                SessionSuspendResumeOperationPhase.Fenced) => true,
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.Fenced,
                SessionSuspendResumeOperationPhase.Draining) => true,
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.Draining,
                SessionSuspendResumeOperationPhase.Checkpointed) => true,
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.Checkpointed,
                SessionSuspendResumeOperationPhase.JournalFlushed) => true,
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.JournalFlushed,
                SessionSuspendResumeOperationPhase.WorkspaceFlushed) => true,
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.WorkspaceFlushed,
                SessionSuspendResumeOperationPhase.ManifestCommitted) => true,
            (SessionSuspendResumeOperationKind.Suspend, SessionSuspendResumeOperationPhase.ManifestCommitted,
                SessionSuspendResumeOperationPhase.PlacementReleased) => true,
            (SessionSuspendResumeOperationKind.Resume, SessionSuspendResumeOperationPhase.Reserved,
                SessionSuspendResumeOperationPhase.ManifestValidated) => true,
            (SessionSuspendResumeOperationKind.Resume, SessionSuspendResumeOperationPhase.ManifestValidated,
                SessionSuspendResumeOperationPhase.EnvironmentReady) => true,
            (SessionSuspendResumeOperationKind.Resume, SessionSuspendResumeOperationPhase.EnvironmentReady,
                SessionSuspendResumeOperationPhase.EgressVerified) => true,
            (SessionSuspendResumeOperationKind.Resume, SessionSuspendResumeOperationPhase.EgressVerified,
                SessionSuspendResumeOperationPhase.FenceAdvanced) => true,
            (SessionSuspendResumeOperationKind.Resume, SessionSuspendResumeOperationPhase.FenceAdvanced,
                SessionSuspendResumeOperationPhase.Dispatched) => true,
            _ => false
        };
    }

    internal static void RequireValidAdvance(
        SessionSuspendResumeOperationKind kind,
        SessionSuspendResumeOperationPhase current,
        SessionSuspendResumeOperationPhase next)
    {
        if (!CanAdvance(kind, current, next))
            throw new CoordinationException(
                "suspend_resume_operation_transition_invalid", StatusCodes.Status409Conflict);
    }

    internal static bool IsTerminal(SessionSuspendResumeOperationPhase phase) =>
        phase is SessionSuspendResumeOperationPhase.NotApplicable
            or SessionSuspendResumeOperationPhase.PlacementReleased
            or SessionSuspendResumeOperationPhase.Dispatched
            or SessionSuspendResumeOperationPhase.Interrupted;

    internal static string ToPersistenceValue(SessionSuspendResumeOperationKind kind) =>
        kind switch
        {
            SessionSuspendResumeOperationKind.Suspend => "suspend",
            SessionSuspendResumeOperationKind.Resume => "resume",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    internal static string ToPersistenceValue(SessionSuspendResumeOperationPhase phase) =>
        phase switch
        {
            SessionSuspendResumeOperationPhase.NotApplicable => "not_applicable",
            SessionSuspendResumeOperationPhase.Reserved => "reserved",
            SessionSuspendResumeOperationPhase.Fenced => "fenced",
            SessionSuspendResumeOperationPhase.Draining => "draining",
            SessionSuspendResumeOperationPhase.Checkpointed => "checkpointed",
            SessionSuspendResumeOperationPhase.JournalFlushed => "journal_flushed",
            SessionSuspendResumeOperationPhase.WorkspaceFlushed => "workspace_flushed",
            SessionSuspendResumeOperationPhase.ManifestCommitted => "manifest_committed",
            SessionSuspendResumeOperationPhase.PlacementReleased => "placement_released",
            SessionSuspendResumeOperationPhase.ManifestValidated => "manifest_validated",
            SessionSuspendResumeOperationPhase.EnvironmentReady => "environment_ready",
            SessionSuspendResumeOperationPhase.EgressVerified => "egress_verified",
            SessionSuspendResumeOperationPhase.FenceAdvanced => "fence_advanced",
            SessionSuspendResumeOperationPhase.Dispatched => "dispatched",
            SessionSuspendResumeOperationPhase.Interrupted => "interrupted",
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };

    internal static SessionSuspendResumeOperationPhase ParsePhase(string value) =>
        value switch
        {
            "not_applicable" => SessionSuspendResumeOperationPhase.NotApplicable,
            "reserved" => SessionSuspendResumeOperationPhase.Reserved,
            "fenced" => SessionSuspendResumeOperationPhase.Fenced,
            "draining" => SessionSuspendResumeOperationPhase.Draining,
            "checkpointed" => SessionSuspendResumeOperationPhase.Checkpointed,
            "journal_flushed" => SessionSuspendResumeOperationPhase.JournalFlushed,
            "workspace_flushed" => SessionSuspendResumeOperationPhase.WorkspaceFlushed,
            "manifest_committed" => SessionSuspendResumeOperationPhase.ManifestCommitted,
            "placement_released" => SessionSuspendResumeOperationPhase.PlacementReleased,
            "manifest_validated" => SessionSuspendResumeOperationPhase.ManifestValidated,
            "environment_ready" => SessionSuspendResumeOperationPhase.EnvironmentReady,
            "egress_verified" => SessionSuspendResumeOperationPhase.EgressVerified,
            "fence_advanced" => SessionSuspendResumeOperationPhase.FenceAdvanced,
            "dispatched" => SessionSuspendResumeOperationPhase.Dispatched,
            "interrupted" => SessionSuspendResumeOperationPhase.Interrupted,
            _ => throw new CoordinationException(
                "suspend_resume_operation_phase_invalid", StatusCodes.Status503ServiceUnavailable)
        };

    internal static string HashRequest(
        SessionSuspendResumeOperationKind kind,
        SessionIdentity identity,
        object request)
    {
        CoordinationIdentity.ValidateIdentity(identity.ProjectId, nameof(identity.ProjectId));
        CoordinationIdentity.ValidateIdentity(identity.RunId, nameof(identity.RunId));
        CoordinationIdentity.ValidateIdentity(identity.SessionId, nameof(identity.SessionId));
        ArgumentNullException.ThrowIfNull(request);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            new { operationKind = ToPersistenceValue(kind), identity, request }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(serialized)).ToLowerInvariant();
    }

    internal static bool SameJson<T>(T left, T right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, JsonOptions),
            JsonSerializer.SerializeToElement(right, JsonOptions));

    internal static (Guid Bootstrap, Guid Consume, Guid Exchange) ResumeBootstrapOperationIds(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A resume operation ID is required.", nameof(operationId));

        Guid Derive(string purpose)
        {
            var input = Encoding.UTF8.GetBytes($"agentweaver.resume-bootstrap.v1\0{operationId:N}\0{purpose}");
            var digest = SHA256.HashData(input);
            var derived = new Guid(digest.AsSpan(0, 16));
            if (derived == Guid.Empty)
                derived = new Guid(digest.AsSpan(16, 16));
            if (derived == Guid.Empty || derived == operationId)
                throw new CoordinationException(
                    "runtime_resume_bootstrap_operation_id_invalid", StatusCodes.Status503ServiceUnavailable);
            return derived;
        }

        var ids = (Bootstrap: Derive("bootstrap"), Consume: Derive("consume"), Exchange: Derive("exchange"));
        if (ids.Bootstrap == ids.Consume || ids.Bootstrap == ids.Exchange || ids.Consume == ids.Exchange)
            throw new CoordinationException(
                "runtime_resume_bootstrap_operation_id_invalid", StatusCodes.Status503ServiceUnavailable);
        return ids;
    }
}

internal sealed record SessionSuspendResumeProgress(
    RuntimeRegistration? Registration,
    EnvironmentGenerationFence? EnvironmentFence,
    long? NetworkIntentGeneration,
    WorkspaceVolumeApiFlushRequest? ResolvedRequest,
    RuntimeHostSuspendReceipt? HostReceipt,
    SessionSuspendResumeCheckpointReference? Checkpoint,
    long? FlushedJournalPosition,
    WorkspaceVolumeFlushReceipt? WorkspaceFlushReceipt,
    RuntimeSuspendPlacementReleaseReceipt? PlacementReleaseReceipt,
    long? FlushTransitionRevision = null,
    RuntimeWorkspaceResumeRequest? ResumeRequest = null,
    RuntimeWorkspaceResumePlacementReceipt? ResumePlacementReceipt = null,
    RuntimeWorkspaceResumeVerificationReceipt? ResumeVerificationReceipt = null,
    long? ResumeExecutionFence = null,
    RuntimeResumeBootstrapRequest? ResumeBootstrapRequest = null,
    RuntimeResumeBootstrapReceipt? ResumeBootstrapReceipt = null)
{
    internal static SessionSuspendResumeProgress Empty => new(
        Registration: null,
        EnvironmentFence: null,
        NetworkIntentGeneration: null,
        ResolvedRequest: null,
        HostReceipt: null,
        Checkpoint: null,
        FlushedJournalPosition: null,
        WorkspaceFlushReceipt: null,
        PlacementReleaseReceipt: null,
        FlushTransitionRevision: null,
        ResumeRequest: null,
        ResumePlacementReceipt: null,
        ResumeVerificationReceipt: null,
        ResumeExecutionFence: null,
        ResumeBootstrapRequest: null,
        ResumeBootstrapReceipt: null);
}

internal sealed class SessionSuspendResumeCoordinator
{
    private readonly CoordinationOwnerStore? _ownerStore;

    public SessionSuspendResumeCoordinator(CoordinationOwnerStore? ownerStore = null)
    {
        _ownerStore = ownerStore;
    }

    internal async Task<IResult> SuspendAsync(
        HttpContext context,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        string idempotencyKey,
        OrchestratorOptions options,
        RuntimeRegistrationOwner registrations,
        CoordinatorDecisionOwnerStore decisions,
        PostgresMafCheckpointStore checkpoints,
        EventsAddressedMessageClient events,
        RuntimeWorkspaceSuspendHttpClient environment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var operation = await BeginSuspendAsync(
            actor, selection, identity, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (operation.Phase == SessionSuspendResumeOperationPhase.Interrupted)
            return Results.Json(
                new { error = "suspend_resume_operation_interrupted", operationId = operation.OperationId },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (operation.Phase is SessionSuspendResumeOperationPhase.ManifestCommitted or
            SessionSuspendResumeOperationPhase.PlacementReleased)
        {
            var committed = RequireCommittedProgress(operation);
            var retryActor = await CreateRuntimeActorAsync(
                context, actor, options, timeProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                var releaseRequest = CreatePlacementReleaseRequest(committed);
                await RequireCurrentSuspendReleaseAsync(
                    actor, selection, releaseRequest, cancellationToken).ConfigureAwait(false);
                if (operation.Phase == SessionSuspendResumeOperationPhase.ManifestCommitted)
                {
                    var releaseReceipt = await environment.ReleasePlacementAsync(
                        releaseRequest, retryActor, cancellationToken).ConfigureAwait(false);
                    await RequireCurrentSuspendReleaseAsync(
                        actor, selection, releaseRequest, cancellationToken).ConfigureAwait(false);
                    committed = committed with { PlacementReleaseReceipt = releaseReceipt };
                    operation = await AdvanceAsync(
                        actor, selection, operation,
                        SessionSuspendResumeOperationPhase.PlacementReleased, committed, cancellationToken)
                        .ConfigureAwait(false);
                }
                var registration = committed.Registration
                    ?? throw new CoordinationException(
                        "runtime_registration_unavailable", StatusCodes.Status503ServiceUnavailable);
                await registrations.RevokeAfterPlacementReleaseAsync(registration, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(
                    new
                    {
                        operationId = operation.OperationId,
                        manifestId = operation.ManifestId,
                        manifestHash = operation.ManifestHash,
                        state = "suspended"
                    },
                    statusCode: StatusCodes.Status202Accepted);
            }
            finally
            {
                retryActor.Bearer.Invalidate();
            }
        }

        var decision = await decisions.ReadCurrentAsync(
            actor, identity, selection, cancellationToken).ConfigureAwait(false);
        if (decision.State.Fence != operation.OwnerExecutionFence ||
            decision.State.ConfirmedWorkPlan is not { } confirmedPlan)
            throw new CoordinationException(
                "suspend_resume_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        var checkpointBinding = CoordinationEndpoints.CreateExecutionCheckpointBinding(
            identity,
            actor,
            operation.OwnerExecutionFence,
            MafExecutionCheckpointStore.CurrentSdkVersion,
            PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot));
        var execution = new MafExecutionCheckpointStore(checkpoints, checkpointBinding);
        var checkpoint = await execution.ReadLatestAsync(cancellationToken).ConfigureAwait(false);
        if (checkpoint is null ||
            checkpoint.State.WorkPlanId != confirmedPlan.Plan.Id ||
            checkpoint.State.DecisionStateVersion != decision.StateVersion)
            throw new CoordinationException(
                "suspend_resume_checkpoint_unavailable", StatusCodes.Status503ServiceUnavailable);
        var checkpointReference = new SessionSuspendResumeCheckpointReference(
            identity,
            SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName,
            checkpoint.Info.CheckpointId,
            checkpoint.State.Revision,
            checkpoint.State.WorkPlanId,
            checkpoint.State.DecisionStateVersion).Validate();

        var runtimeActor = await CreateRuntimeActorAsync(context, actor, options, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var registration = await registrations.ReadCurrentForSessionAsync(
                context, identity.ProjectId, identity.RunId, identity.SessionId, cancellationToken)
                .ConfigureAwait(false);
            ValidateSuspendRegistration(registration, identity, actor, selection, operation, timeProvider);

            var readiness = await ReadEnvironmentReadinessAsync(
                context, registration, runtimeActor, timeProvider, cancellationToken).ConfigureAwait(false);
            if (operation.Phase == SessionSuspendResumeOperationPhase.Reserved)
            {
                var hostReadiness = await ReadHostReadinessAsync(
                    context, registration, runtimeActor, timeProvider, cancellationToken).ConfigureAwait(false);
                var proof = new RuntimeHostSessionProof(
                    1,
                    registration,
                    hostReadiness.SourceGrant.GrantId,
                    hostReadiness.SourceGrant.Revision,
                    RuntimeCredentialPurpose.Observe);
                var request = new RuntimeHostSuspendRequest(
                    proof,
                    operation.OperationId,
                    operation.ManifestId,
                    1);
                await RequireCurrentSuspendAsync(actor, selection, request, cancellationToken)
                    .ConfigureAwait(false);
                await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
                    context, identity.ProjectId, identity.RunId, selection,
                    context.RequestServices.GetRequiredService<ProjectsRunSelectionClient>(), cancellationToken)
                    .ConfigureAwait(false);
                var resolved = await environment.ResolveAsync(
                    request, runtimeActor, cancellationToken).ConfigureAwait(false);
                if (resolved.Suspend.PhaseVersion != 1 ||
                    resolved.Suspend.OperationId != operation.OperationId ||
                    resolved.Suspend.ManifestId != operation.ManifestId)
                    throw new CoordinationException(
                        "suspend_resume_resolved_request_invalid", StatusCodes.Status503ServiceUnavailable);
                var progress = new SessionSuspendResumeProgress(
                    registration,
                    resolved.Suspend.WorkspaceFlush!.EnvironmentFence,
                    readiness.Observation.VerifiedNetworkGeneration,
                    resolved,
                    HostReceipt: null,
                    checkpointReference,
                    FlushedJournalPosition: null,
                    WorkspaceFlushReceipt: null,
                    PlacementReleaseReceipt: null);
                operation = await AdvanceAsync(
                    actor,
                    selection,
                    operation,
                    SessionSuspendResumeOperationPhase.Fenced,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (operation.Kind != SessionSuspendResumeOperationKind.Suspend ||
                operation.Progress.ValueKind != JsonValueKind.Object)
                throw new CoordinationException(
                    "suspend_resume_operation_invalid", StatusCodes.Status503ServiceUnavailable);
            var saved = DeserializeProgress(operation.Progress);
            if (saved.Registration != registration ||
                saved.Checkpoint != checkpointReference ||
                saved.ResolvedRequest is null ||
                saved.EnvironmentFence is null ||
                saved.NetworkIntentGeneration != readiness.Observation.VerifiedNetworkGeneration)
                throw new CoordinationException(
                    "suspend_resume_operation_stale", StatusCodes.Status409Conflict);

            if (operation.Phase == SessionSuspendResumeOperationPhase.Fenced)
            {
                await RequireCurrentSuspendAsync(
                    actor, selection, saved.ResolvedRequest.Suspend, cancellationToken).ConfigureAwait(false);
                var flushed = await environment.FlushAsync(
                    saved.ResolvedRequest, runtimeActor, cancellationToken).ConfigureAwait(false);
                var hostReceipt = flushed.HostSuspendReceipt
                    ?? throw new CoordinationException(
                        "runtime_suspend_receipt_missing", StatusCodes.Status503ServiceUnavailable);
                RuntimeWorkspaceSuspendHttpClient.ValidateHostReceipt(
                    hostReceipt, saved.ResolvedRequest.Suspend, timeProvider.GetUtcNow());
                var cache = await events.ReadSessionMaterialAsync(
                    context,
                    identity,
                    hostReceipt.CacheAcknowledgment.EventId,
                    SessionMaterialKind.SdkCache,
                    runtimeActor,
                    cancellationToken).ConfigureAwait(false);
                if (cache.Material != hostReceipt.CacheAcknowledgment ||
                    !string.Equals(
                        RuntimeContractValidation.Hash(cache.Bytes),
                        hostReceipt.CacheAcknowledgment.Reference.Material!.Sha256,
                        StringComparison.Ordinal))
                    throw new CoordinationException(
                        "events_session_material_invalid", StatusCodes.Status503ServiceUnavailable);
                var latestRegistration = await registrations.ReadCurrentAsync(
                    context, registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
                if (latestRegistration != registration)
                    throw new CoordinationException(
                        "runtime_registration_stale", StatusCodes.Status409Conflict);
                var latestReadiness = await ReadEnvironmentReadinessAsync(
                    context, registration, runtimeActor, timeProvider, cancellationToken).ConfigureAwait(false);
                if (latestReadiness.Observation.VerifiedNetworkGeneration != saved.NetworkIntentGeneration)
                    throw new CoordinationException(
                        "suspend_resume_network_generation_stale", StatusCodes.Status409Conflict);
                var flushReceipt = flushed.FlushReceipt
                    ?? throw new CoordinationException(
                        "runtime_workspace_flush_receipt_missing", StatusCodes.Status503ServiceUnavailable);
                saved = saved with
                {
                    HostReceipt = hostReceipt,
                    FlushedJournalPosition = cache.Material.Position,
                    WorkspaceFlushReceipt = flushReceipt
                };
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.Draining, saved, cancellationToken).ConfigureAwait(false);
            }

            if (operation.Phase == SessionSuspendResumeOperationPhase.Draining)
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.Checkpointed, saved, cancellationToken).ConfigureAwait(false);
            if (operation.Phase == SessionSuspendResumeOperationPhase.Checkpointed)
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.JournalFlushed, saved, cancellationToken).ConfigureAwait(false);
            if (operation.Phase == SessionSuspendResumeOperationPhase.JournalFlushed)
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.WorkspaceFlushed, saved, cancellationToken).ConfigureAwait(false);

            if (operation.Phase == SessionSuspendResumeOperationPhase.WorkspaceFlushed)
            {
                var host = saved.HostReceipt
                    ?? throw new CoordinationException(
                        "runtime_suspend_receipt_missing", StatusCodes.Status503ServiceUnavailable);
                var flush = saved.WorkspaceFlushReceipt
                    ?? throw new CoordinationException(
                        "runtime_workspace_flush_receipt_missing", StatusCodes.Status503ServiceUnavailable);
                var currentRegistration = await registrations.ReadCurrentAsync(
                    context, registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
                if (currentRegistration != registration)
                    throw new CoordinationException(
                        "runtime_registration_stale", StatusCodes.Status409Conflict);
                var currentReadiness = await ReadEnvironmentReadinessAsync(
                    context, registration, runtimeActor, timeProvider, cancellationToken).ConfigureAwait(false);
                if (currentReadiness.Observation.VerifiedNetworkGeneration != saved.NetworkIntentGeneration)
                    throw new CoordinationException(
                        "suspend_resume_network_generation_stale", StatusCodes.Status409Conflict);
                var manifest = new SessionSuspendResumeManifest(
                    SessionSuspendResumeContractVersions.CurrentManifestVersion,
                    operation.ManifestId,
                    identity,
                    SessionSuspendResumeManifestState.Suspended,
                    saved.EnvironmentFence,
                    operation.OwnerExecutionFence,
                    host.CacheAcknowledgment,
                    saved.Checkpoint,
                    saved.FlushedJournalPosition,
                    flush.Volume,
                    flush.Resource,
                    flush.DataGeneration,
                    flush.TreeSha256,
                    flush.ProviderCheckpointId,
                    GuestSnapshotResource: null,
                    GuestSnapshotLifecycleGeneration: null,
                    saved.NetworkIntentGeneration,
                    ImmutableArray<string>.Empty).Validate();
                operation = await CommitManifestAsync(
                    actor, selection, operation, manifest, saved, cancellationToken).ConfigureAwait(false);
            }

            if (operation.Phase is SessionSuspendResumeOperationPhase.ManifestCommitted or
                SessionSuspendResumeOperationPhase.PlacementReleased)
            {
                saved = RequireCommittedProgress(operation);
                var releaseRequest = CreatePlacementReleaseRequest(saved);
                await RequireCurrentSuspendReleaseAsync(
                    actor, selection, releaseRequest, cancellationToken).ConfigureAwait(false);
                if (operation.Phase == SessionSuspendResumeOperationPhase.ManifestCommitted)
                {
                    var releaseReceipt = await environment.ReleasePlacementAsync(
                        releaseRequest, runtimeActor, cancellationToken).ConfigureAwait(false);
                    await RequireCurrentSuspendReleaseAsync(
                        actor, selection, releaseRequest, cancellationToken).ConfigureAwait(false);
                    saved = saved with { PlacementReleaseReceipt = releaseReceipt };
                    operation = await AdvanceAsync(
                        actor, selection, operation,
                        SessionSuspendResumeOperationPhase.PlacementReleased, saved, cancellationToken)
                        .ConfigureAwait(false);
                }

                var releasedRegistration = saved.Registration
                    ?? throw new CoordinationException(
                        "runtime_registration_unavailable", StatusCodes.Status503ServiceUnavailable);
                await registrations.RevokeAfterPlacementReleaseAsync(
                    releasedRegistration, cancellationToken).ConfigureAwait(false);
            }

            return Results.Json(
                new
                {
                    operationId = operation.OperationId,
                    manifestId = operation.ManifestId,
                    manifestHash = operation.ManifestHash,
                    state = operation.Phase == SessionSuspendResumeOperationPhase.PlacementReleased
                        ? "suspended"
                        : "manifest_committed"
                },
                statusCode: StatusCodes.Status202Accepted);
        }
        finally
        {
            runtimeActor.Bearer.Invalidate();
        }
    }

    private static SessionSuspendResumeProgress DeserializeProgress(JsonElement value)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionSuspendResumeProgress>(
                value, SessionSuspendResumePhaseMachine.JsonOptions)
                ?? throw new CoordinationException(
                    "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "suspend_resume_progress_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal static SessionSuspendResumeProgress ReadFenceAdvancedProgress(
        SessionSuspendResumeOperationRecord operation)
    {
        if (operation.Kind != SessionSuspendResumeOperationKind.Resume ||
            operation.Phase != SessionSuspendResumeOperationPhase.FenceAdvanced ||
            operation.PhaseVersion != 4)
            throw new CoordinationException(
                "runtime_resume_bootstrap_request_invalid", StatusCodes.Status503ServiceUnavailable);
        var progress = DeserializeProgress(operation.Progress);
        if (progress.ResumeExecutionFence != operation.OwnerExecutionFence)
            throw new CoordinationException(
                "runtime_resume_bootstrap_request_invalid", StatusCodes.Status503ServiceUnavailable);
        return progress;
    }

    internal async Task<SessionSuspendResumeOperationRecord> BeginResumeAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        string idempotencyKey,
        Guid sourceManifestId,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        try
        {
            return await ownerStore.BeginSessionResumeAsync(
                actor, selection, identity, idempotencyKey, sourceManifestId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal async Task<RuntimeWorkspaceResumeRequest> RequireCurrentResumeAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeWorkspaceResumeRequest request,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await ownerStore.RequireCurrentSessionResumeAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
        return request;
    }

    internal async Task<RuntimeResumeBootstrapRequest> RequireCurrentResumeBootstrapAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeResumeBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await ownerStore.RequireCurrentSessionResumeBootstrapAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
        return request;
    }

    internal async Task<RuntimeSuspendPlacementReleaseRequest> RequireCurrentSuspendReleaseAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeSuspendPlacementReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await ownerStore.RequireCurrentSessionSuspendReleaseAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
        return request;
    }

    internal async Task<IResult> ResumeAsync(
        HttpContext context,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        string idempotencyKey,
        Guid sourceManifestId,
        OrchestratorOptions options,
        RuntimeWorkspaceResumeHttpClient environment,
        RuntimeResumeBootstrapHttpClient bootstrap,
        RuntimeRegistrationOwner registrations,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var operation = await BeginResumeAsync(
            actor, selection, identity, idempotencyKey, sourceManifestId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.Phase == SessionSuspendResumeOperationPhase.Interrupted)
            return Results.Json(
                new { error = "suspend_resume_operation_interrupted", operationId = operation.OperationId },
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var saved = DeserializeProgress(operation.Progress);
        var request = saved.ResumeRequest
            ?? throw new CoordinationException(
                "suspend_resume_resume_request_missing", StatusCodes.Status503ServiceUnavailable);
        var runtimeActor = await CreateRuntimeActorAsync(
            context, actor, options, timeProvider, cancellationToken).ConfigureAwait(false);
        try
        {
            var projects = context.RequestServices.GetRequiredService<ProjectsRunSelectionClient>();
            if (operation.Phase == SessionSuspendResumeOperationPhase.Reserved)
            {
                request.Validate();
                if (request.OperationId != operation.OperationId ||
                    request.SourceManifest.ManifestId != sourceManifestId)
                    throw new CoordinationException(
                        "suspend_resume_resume_request_invalid", StatusCodes.Status503ServiceUnavailable);
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.ManifestValidated, saved, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (operation.Phase == SessionSuspendResumeOperationPhase.ManifestValidated)
            {
                var prepare = request with
                {
                    PhaseVersion = checked(operation.PhaseVersion + 1),
                    RestoredWorkspace = null
                };
                await RequireCurrentResumeAsync(actor, selection, prepare, cancellationToken)
                    .ConfigureAwait(false);
                await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
                    context, identity.ProjectId, identity.RunId, selection, projects, cancellationToken)
                    .ConfigureAwait(false);
                var placement = await environment.PrepareAsync(
                    prepare, runtimeActor, cancellationToken).ConfigureAwait(false);
                await RequireCurrentResumeAsync(actor, selection, prepare, cancellationToken)
                    .ConfigureAwait(false);
                await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
                    context, identity.ProjectId, identity.RunId, selection, projects, cancellationToken)
                    .ConfigureAwait(false);
                saved = saved with { ResumeRequest = prepare, ResumePlacementReceipt = placement };
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.EnvironmentReady, saved, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (operation.Phase == SessionSuspendResumeOperationPhase.EnvironmentReady)
            {
                var placement = saved.ResumePlacementReceipt
                    ?? throw new CoordinationException(
                        "runtime_resume_placement_receipt_missing", StatusCodes.Status503ServiceUnavailable);
                var verify = request with
                {
                    PhaseVersion = checked(operation.PhaseVersion + 1),
                    RestoredWorkspace = placement.Workspace
                };
                await RequireCurrentResumeAsync(actor, selection, verify, cancellationToken)
                    .ConfigureAwait(false);
                await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
                    context, identity.ProjectId, identity.RunId, selection, projects, cancellationToken)
                    .ConfigureAwait(false);
                var verification = await environment.VerifyAsync(
                    verify, runtimeActor, cancellationToken).ConfigureAwait(false);
                await RequireCurrentResumeAsync(actor, selection, verify, cancellationToken)
                    .ConfigureAwait(false);
                await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
                    context, identity.ProjectId, identity.RunId, selection, projects, cancellationToken)
                    .ConfigureAwait(false);
                verification.ValidateFor(verify);
                saved = saved with
                {
                    ResumeRequest = verify,
                    ResumeVerificationReceipt = verification
                };
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.EgressVerified, saved, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (operation.Phase == SessionSuspendResumeOperationPhase.EgressVerified)
            {
                if (saved.ResumeRequest is not { RestoredWorkspace: not null } resumeRequest ||
                    saved.ResumeVerificationReceipt is not { } verification ||
                    saved.ResumePlacementReceipt is null)
                    throw new CoordinationException(
                        "runtime_resume_verification_missing", StatusCodes.Status503ServiceUnavailable);
                verification.ValidateFor(resumeRequest);
                operation = await AdvanceResumeFenceAsync(
                    actor, selection, operation, saved, cancellationToken).ConfigureAwait(false);
            }

            if (operation.Phase == SessionSuspendResumeOperationPhase.FenceAdvanced)
            {
                saved = ReadFenceAdvancedProgress(operation);
                var bootstrapRequest = await CreateResumeBootstrapRequestAsync(
                    context, operation, saved, registrations, cancellationToken).ConfigureAwait(false);
                await RequireResumeBootstrapCurrentAsync(
                    context, actor, selection, bootstrapRequest, registrations, projects, cancellationToken)
                    .ConfigureAwait(false);
                var receipt = await bootstrap.BootstrapAsync(
                    bootstrapRequest, runtimeActor, cancellationToken).ConfigureAwait(false);
                receipt.ValidateFor(bootstrapRequest);
                await RequireResumeBootstrapCurrentAsync(
                    context, actor, selection, bootstrapRequest, registrations, projects, cancellationToken)
                    .ConfigureAwait(false);
                saved = saved with
                {
                    ResumeBootstrapRequest = bootstrapRequest,
                    ResumeBootstrapReceipt = receipt
                };
                operation = await AdvanceAsync(
                    actor, selection, operation,
                    SessionSuspendResumeOperationPhase.Dispatched, saved, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (operation.Phase == SessionSuspendResumeOperationPhase.Dispatched)
                ValidateDispatchedResume(operation, saved);

            return Results.Json(
                new
                {
                    operationId = operation.OperationId,
                    manifestId = sourceManifestId,
                    state = SessionSuspendResumePhaseMachine.ToPersistenceValue(operation.Phase)
                },
                statusCode: StatusCodes.Status202Accepted);
        }
        finally
        {
            runtimeActor.Bearer.Invalidate();
        }
    }

    private static async Task<RuntimeResumeBootstrapRequest> CreateResumeBootstrapRequestAsync(
        HttpContext context,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeProgress progress,
        RuntimeRegistrationOwner registrations,
        CancellationToken cancellationToken)
    {
        if (operation.Kind != SessionSuspendResumeOperationKind.Resume ||
            operation.Phase != SessionSuspendResumeOperationPhase.FenceAdvanced ||
            operation.PhaseVersion != 4 ||
            progress.ResumeExecutionFence != operation.OwnerExecutionFence ||
            progress.ResumeRequest is not { RestoredWorkspace: not null } sourceRequest ||
            progress.ResumePlacementReceipt is not { } placement ||
            progress.ResumeVerificationReceipt is not { } verification)
            throw new CoordinationException(
                "runtime_resume_bootstrap_request_invalid", StatusCodes.Status503ServiceUnavailable);
        verification.ValidateFor(sourceRequest);
        placement.ValidateFor(sourceRequest);
        var resume = sourceRequest with { PhaseVersion = operation.PhaseVersion };
        var registration = await registrations.ReplaceAfterResumeFenceAsync(
            context,
            sourceRequest.SourceRelease.HostReceipt.Registration,
            operation.OperationId,
            operation.Identity.ProjectId,
            operation.Identity.RunId,
            operation.Identity.SessionId,
            new(placement.Workspace.Placement.EnvironmentId, placement.Workspace.Placement.ProfileId),
            cancellationToken).ConfigureAwait(false);
        var ids = SessionSuspendResumePhaseMachine.ResumeBootstrapOperationIds(operation.OperationId);
        return new RuntimeResumeBootstrapRequest(
            1, resume, registration, ids.Bootstrap, ids.Consume, ids.Exchange).Validate();
    }

    private async Task RequireResumeBootstrapCurrentAsync(
        HttpContext context,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeResumeBootstrapRequest request,
        RuntimeRegistrationOwner registrations,
        ProjectsRunSelectionClient projects,
        CancellationToken cancellationToken)
    {
        var current = await registrations.ReadCurrentAsync(
            context, request.Registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        if (current != request.Registration)
            throw new CoordinationException(
                "runtime_registration_stale", StatusCodes.Status409Conflict);
        await RequireCurrentResumeBootstrapAsync(actor, selection, request, cancellationToken)
            .ConfigureAwait(false);
        await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
            context, request.Resume.SourceManifest.Identity.ProjectId,
            request.Resume.SourceManifest.Identity.RunId, selection, projects, cancellationToken)
            .ConfigureAwait(false);
        var final = await registrations.ReadCurrentAsync(
            context, request.Registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        if (final != current)
            throw new CoordinationException(
                "runtime_registration_stale", StatusCodes.Status409Conflict);
    }

    private static void ValidateDispatchedResume(
        SessionSuspendResumeOperationRecord operation, SessionSuspendResumeProgress progress)
    {
        ValidateResumeBootstrapCompletion(operation, progress);
        if (operation.Phase != SessionSuspendResumeOperationPhase.Dispatched)
            throw new CoordinationException(
                "runtime_resume_bootstrap_receipt_missing", StatusCodes.Status503ServiceUnavailable);
    }

    internal static void ValidateResumeBootstrapCompletion(
        SessionSuspendResumeOperationRecord operation, SessionSuspendResumeProgress progress)
    {
        var dispatched = operation.Phase == SessionSuspendResumeOperationPhase.Dispatched;
        var expectedVersion = dispatched ? 5 : 4;
        if (operation.Kind != SessionSuspendResumeOperationKind.Resume ||
            operation.Phase is not (SessionSuspendResumeOperationPhase.FenceAdvanced or
                SessionSuspendResumeOperationPhase.Dispatched) ||
            operation.PhaseVersion != expectedVersion ||
            progress.ResumeExecutionFence != operation.OwnerExecutionFence ||
            progress.ResumeRequest is not { RestoredWorkspace: not null } savedResume ||
            progress.ResumePlacementReceipt is not { } placement ||
            progress.ResumeVerificationReceipt is not { } verification ||
            progress.ResumeBootstrapRequest is not { } request ||
            request.Resume.OperationId != operation.OperationId ||
            request.Resume.PhaseVersion != 4 ||
            request.Resume.SourceManifest.ManifestId != operation.ManifestId ||
            request.Resume.SourceManifest.CoreExecutionFence == long.MaxValue ||
            request.Resume.SourceManifest.CoreExecutionFence + 1 != operation.OwnerExecutionFence ||
            request.Registration.Binding.ExecutionFence != operation.OwnerExecutionFence ||
            !SessionSuspendResumePhaseMachine.SameJson(
                request.Resume, savedResume with { PhaseVersion = 4 }) ||
            !SessionSuspendResumePhaseMachine.SameJson(placement.Workspace, verification.Workspace) ||
            progress.ResumeBootstrapReceipt is not { } receipt)
            throw new CoordinationException(
                "runtime_resume_bootstrap_receipt_missing", StatusCodes.Status503ServiceUnavailable);
        try
        {
            savedResume.Validate();
            placement.ValidateFor(savedResume);
            verification.ValidateFor(savedResume);
            request.Validate();
            receipt.ValidateFor(request);
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code, StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal static SessionSuspendResumeProgress RequireCommittedProgress(
        SessionSuspendResumeOperationRecord operation)
    {
        if (operation.Kind != SessionSuspendResumeOperationKind.Suspend ||
            operation.Phase is not (SessionSuspendResumeOperationPhase.ManifestCommitted or
                SessionSuspendResumeOperationPhase.PlacementReleased) ||
            operation.Manifest is not { } manifest ||
            string.IsNullOrWhiteSpace(operation.ManifestHash))
            throw new CoordinationException(
                "suspend_resume_manifest_unavailable", StatusCodes.Status503ServiceUnavailable);

        SessionSuspendResumeProgress saved;
        try
        {
            saved = DeserializeProgress(operation.Progress);
            manifest.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new CoordinationException(
                "suspend_resume_manifest_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
        var registration = saved.Registration;
        var resolved = saved.ResolvedRequest;
        var host = saved.HostReceipt;
        var flush = saved.WorkspaceFlushReceipt;
        if (registration is null || resolved is null || host is null || flush is null ||
            saved.EnvironmentFence is null || saved.Checkpoint is null ||
            saved.NetworkIntentGeneration is not > 0 || saved.FlushedJournalPosition is not > 0 ||
            saved.FlushTransitionRevision is null ||
            resolved.Suspend.WorkspaceFlush is not { } flushRequest ||
            registration != host.Registration ||
            registration.Binding.ExecutionFence != operation.OwnerExecutionFence ||
            resolved.Suspend.OperationId != operation.OperationId ||
            resolved.Suspend.ManifestId != operation.ManifestId ||
            host.OperationId != operation.OperationId ||
            host.ManifestId != operation.ManifestId ||
            host.WorkspaceFlush != flush ||
            saved.FlushedJournalPosition != host.CacheAcknowledgment.Position ||
            flush.EnvironmentFence != saved.EnvironmentFence ||
            manifest.ManifestId != operation.ManifestId ||
            manifest.Identity != operation.Identity ||
            manifest.State != SessionSuspendResumeManifestState.Suspended ||
            manifest.EnvironmentFence != saved.EnvironmentFence ||
            manifest.CoreExecutionFence != operation.OwnerExecutionFence ||
            manifest.CacheAcknowledgment != host.CacheAcknowledgment ||
            manifest.Checkpoint != saved.Checkpoint ||
            manifest.FlushedJournalPosition != saved.FlushedJournalPosition ||
            manifest.WorkspaceVolume != flush.Volume ||
            manifest.WorkspaceResource != flush.Resource ||
            manifest.WorkspaceDataGeneration != flush.DataGeneration ||
            manifest.WorkspaceTreeSha256 != flush.TreeSha256 ||
            manifest.WorkspaceProviderCheckpointId != flush.ProviderCheckpointId ||
            manifest.NetworkIntentGeneration != saved.NetworkIntentGeneration)
            throw new CoordinationException(
                "suspend_resume_manifest_binding_invalid", StatusCodes.Status503ServiceUnavailable);

        try
        {
            resolved.Validate();
            flush.ValidateFor(flushRequest);
            var release = CreatePlacementReleaseRequest(saved);
            if (operation.Phase == SessionSuspendResumeOperationPhase.PlacementReleased)
                (saved.PlacementReleaseReceipt
                    ?? throw new RuntimeAuthorizationException("runtime_suspend_release_receipt_missing"))
                    .ValidateFor(release);
            else if (saved.PlacementReleaseReceipt is not null)
                throw new RuntimeAuthorizationException("runtime_suspend_release_receipt_unexpected");
        }
        catch (Exception exception) when (exception is ArgumentException or RuntimeAuthorizationException)
        {
            throw new CoordinationException(
                "suspend_resume_manifest_binding_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
        return saved;
    }

    internal static RuntimeSuspendPlacementReleaseRequest CreatePlacementReleaseRequest(
        SessionSuspendResumeProgress progress)
    {
        var host = progress.HostReceipt
            ?? throw new CoordinationException(
                "runtime_suspend_receipt_missing", StatusCodes.Status503ServiceUnavailable);
        var transitionRevision = progress.FlushTransitionRevision
            ?? throw new CoordinationException(
                "runtime_workspace_flush_receipt_missing", StatusCodes.Status503ServiceUnavailable);
        try
        {
            return new RuntimeSuspendPlacementReleaseRequest(host, transitionRevision).Validate();
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                "runtime_suspend_release_request_invalid", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    private static void ValidateSuspendRegistration(
        RuntimeRegistration registration,
        SessionIdentity identity,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        TimeProvider timeProvider)
    {
        RuntimeContractValidation.Validate(registration);
        var binding = registration.Binding;
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() ||
            binding.ActorIssuer != actor.Issuer || binding.ActorId != actor.Subject ||
            binding.TenantId != selection.Authorization.TenantId ||
            binding.ProjectId != identity.ProjectId || binding.RunId != identity.RunId ||
            binding.SessionId != identity.SessionId ||
            binding.ExecutionFence != operation.OwnerExecutionFence ||
            binding.AcceptedSelectionHash != CoordinationOwnerStore.HashSelection(selection.Selection))
            throw new CoordinationException(
                "runtime_registration_stale", StatusCodes.Status409Conflict);
    }

    private static async Task<RuntimeActorAuthorization> CreateRuntimeActorAsync(
        HttpContext context,
        CoordinationActor actor,
        OrchestratorOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow() ||
            CoordinationIdentity.RequireActor(context.User, options.Issuer) != actor)
            throw new CoordinationException(
                "runtime_actor_expired", StatusCodes.Status403Forbidden);
        cancellationToken.ThrowIfCancellationRequested();
        return new RuntimeActorAuthorization(
            new SecretCredential(CoordinationIdentity.RequireBearer(context).Parameter!, expiry.Value, timeProvider),
            CoordinationIdentity.ReadTenantSelector(context));
    }

    private static async Task<EnvironmentRuntimeReadinessContext> ReadEnvironmentReadinessAsync(
        HttpContext context,
        RuntimeRegistration registration,
        RuntimeActorAuthorization actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var path = $"/internal/projects/{Uri.EscapeDataString(binding.ProjectId)}" +
            $"/runs/{Uri.EscapeDataString(binding.RunId)}" +
            $"/environments/{Uri.EscapeDataString(binding.EnvironmentId)}" +
            $"/coordination/sessions/{Uri.EscapeDataString(binding.SessionId)}" +
            $"/runtime-bootstrap/profiles/{Uri.EscapeDataString(binding.ProfileId)}/readiness";
        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>()
            .CreateClient("runtime-suspend-environment");
        var readiness = await RuntimeOwnerHttpTransport.SendBoundedAsync<EnvironmentRuntimeReadinessContext>(
            client,
            context.RequestServices.GetRequiredService<RuntimeRegistrationOwnerOptions>().EnvironmentOwnerAddress,
            path,
            actor,
            body: null,
            maximumBytes: SessionMaterialValidation.MaximumBytes,
            allowNotFound: false,
            cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "runtime_readiness_unavailable", StatusCodes.Status503ServiceUnavailable);
        var placement = readiness.Placement;
        var observation = readiness.Observation;
        var owner = placement.RuntimeOwnerContext;
        if (readiness.ContractVersion != 1 || owner is null ||
            owner.ProjectId != binding.ProjectId || owner.RunId != binding.RunId ||
            owner.SessionId != binding.SessionId || owner.TenantId != binding.TenantId ||
            owner.ActorIssuer != binding.ActorIssuer || owner.ActorId != binding.ActorId ||
            owner.ExecutionFence != binding.ExecutionFence ||
            owner.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            placement.TenantId != binding.TenantId ||
            placement.ProjectId != binding.ProjectId || placement.RunId != binding.RunId ||
            placement.EnvironmentId != binding.EnvironmentId ||
            placement.LifecycleGeneration != binding.EnvironmentLifecycleGeneration ||
            placement.LeaseRevision != binding.EnvironmentLeaseRevision ||
            placement.CurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            placement.ProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            placement.Resource != new ProviderResourceRef(
                ProviderSeam.Sandbox, binding.PlacementProviderId!, binding.PlacementUid, binding.PlacementGeneration) ||
            placement.ConfigureEndpoint != binding.ConfigureEndpoint ||
            placement.ObservationEndpoint != binding.ObservationEndpoint ||
            placement.LeaseExpiresAt <= timeProvider.GetUtcNow() ||
            observation.Resource != placement.Resource || observation.State != SandboxObservedState.Ready ||
            observation.FencingGeneration != placement.CurrentFencingGeneration ||
            !observation.VmIsolationVerified || !observation.WorkspaceAttachmentVerified ||
            observation.VerifiedNetworkGeneration is not > 0)
            throw new CoordinationException(
                "runtime_readiness_binding_invalid", StatusCodes.Status503ServiceUnavailable);
        return readiness;
    }

    private static async Task<RuntimeHostReadinessReceipt> ReadHostReadinessAsync(
        HttpContext context,
        RuntimeRegistration registration,
        RuntimeActorAuthorization actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var client = context.RequestServices
            .GetRequiredService<IHttpClientFactory>().CreateClient("runtime-suspend-host");
        var endpoint = CoordinationEndpoints.RuntimeHostEndpoint(
            registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness");
        var readiness = await CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
            client, endpoint, actor, body: null, allowNotReady: false, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "runtime_host_unavailable", StatusCodes.Status503ServiceUnavailable);
        CoordinationEndpoints.ValidateHostReadiness(readiness, registration, timeProvider);
        return readiness;
    }

    internal async Task<SessionSuspendResumeOperationRecord> BeginSuspendAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        try
        {
            return await ownerStore.BeginSessionSuspendAsync(
                actor, selection, identity, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal async Task<SessionSuspendResumeOperationRecord> AdvanceAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeOperationPhase next,
        SessionSuspendResumeProgress progress,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        try
        {
            return await ownerStore.AdvanceSessionSuspendResumeAsync(
                actor, selection, operation, next, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal async Task<SessionSuspendResumeOperationRecord> AdvanceResumeFenceAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeProgress progress,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        try
        {
            return await ownerStore.AdvanceSessionResumeFenceAsync(
                actor, selection, operation, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal async Task<SessionSuspendResumeOperationRecord> CommitManifestAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionSuspendResumeOperationRecord operation,
        SessionSuspendResumeManifest manifest,
        SessionSuspendResumeProgress progress,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        try
        {
            return await ownerStore.CommitSuspendedSessionManifestAsync(
                actor, selection, operation, manifest, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    internal async Task<RuntimeHostSuspendRequest> RequireCurrentSuspendAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        RuntimeHostSuspendRequest request,
        CancellationToken cancellationToken)
    {
        var ownerStore = RequireOwnerStore();
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await ownerStore.RequireCurrentSessionSuspendAsync(
                actor, selection, request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            throw new CoordinationException(
                "suspend_resume_owner_schema_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
        return request;
    }

    internal IResult Unavailable() =>
        Results.Json(
            new { error = "suspend_resume_owner_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    internal async Task<IResult> RecordUnavailableAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        SessionIdentity identity,
        SessionSuspendResumeOperationKind kind,
        string idempotencyKey,
        Guid? sourceManifestId,
        CancellationToken cancellationToken)
    {
        var ownerStore = _ownerStore
            ?? throw new InvalidOperationException("The coordination owner store is not configured.");
        SessionSuspendResumeOperationSnapshot operation;
        try
        {
            operation = await ownerStore.RecordUnavailableSessionSuspendResumeAsync(
                actor,
                selection,
                identity,
                kind,
                idempotencyKey,
                sourceManifestId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (
            exception.SqlState is PostgresErrorCodes.UndefinedTable or
                PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.UndefinedObject)
        {
            return Results.Json(
                new { error = "suspend_resume_owner_schema_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(
            new
            {
                error = "suspend_resume_owner_unavailable",
                operationId = operation.OperationId,
                manifestId = operation.ManifestId,
                state = "interrupted",
                ownerExecutionFence = operation.OwnerExecutionFence
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private CoordinationOwnerStore RequireOwnerStore() =>
        _ownerStore ?? throw new InvalidOperationException(
            "The coordination owner store is not configured.");
}
