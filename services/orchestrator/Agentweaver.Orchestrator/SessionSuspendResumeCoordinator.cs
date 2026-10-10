using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;

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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
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
}

internal sealed class SessionSuspendResumeCoordinator
{
    private readonly CoordinationOwnerStore? _ownerStore;

    public SessionSuspendResumeCoordinator(CoordinationOwnerStore? ownerStore = null)
    {
        _ownerStore = ownerStore;
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
}
