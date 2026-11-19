using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class CheckpointRecoveryEventProjection
{
    private const string Prefix = "orchestrator.run.checkpoint_recovery_";

    internal static CoordinationException Unavailable() =>
        new("checkpoint_recovery_events_unavailable", StatusCodes.Status503ServiceUnavailable);

    internal static CheckpointRecoveryEvent Parse(
        Guid eventId, long sequence, string eventType, int version, DateTimeOffset occurredAt,
        string key, string json, Guid operationId, SessionIdentity identity,
        CheckpointRecoveryEventStatus? admission)
    {
        try
        {
            if (eventId == Guid.Empty || operationId == Guid.Empty || sequence < 1 ||
                version != 1 || json.Length > 1024 * 1024)
                throw Unavailable();
            using var document = JsonDocument.Parse(json);
            var payload = document.RootElement;
            JsonElement status;
            string? effects = null;
            if (admission is null)
            {
                if (eventId != operationId || eventType is not
                    (Prefix + "pending" or Prefix + "blocked" or Prefix + "indeterminate"))
                    throw Unavailable();
                var result = RequiredObject(payload, "result");
                if (RequiredGuid(result, "operationId") != operationId)
                    throw Unavailable();
                var session = RequiredObject(result, "session");
                if (RequiredString(session, "projectId") != identity.ProjectId ||
                    RequiredString(session, "runId") != identity.RunId ||
                    RequiredString(session, "sessionId") != identity.SessionId)
                    throw Unavailable();
                status = RequiredObject(result, "checkpointRecovery");
                if (RequiredLong(result, "previousExecutionFence") != RequiredLong(status, "previousExecutionFence") ||
                    RequiredLong(result, "executionFence") != RequiredLong(status, "executionFence"))
                    throw Unavailable();
                effects = OptionalString(payload, "runtimeEffectsState");
            }
            else
            {
                if (RequiredGuid(payload, "operationId") != operationId)
                    throw Unavailable();
                var expectedPrefix = $"checkpoint-recovery:{operationId:D}:";
                if (!key.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
                    !long.TryParse(key.AsSpan(expectedPrefix.Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var phaseVersion) ||
                    phaseVersion < 0 ||
                    key != expectedPrefix + phaseVersion.ToString(CultureInfo.InvariantCulture))
                    throw Unavailable();
                var expectedId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16]);
                if (expectedId != eventId)
                    throw Unavailable();
                status = RequiredObject(payload, "status");
            }

            var savedOperation = OptionalGuid(status, "operationId");
            if (savedOperation is not null && savedOperation != operationId)
                throw Unavailable();
            var snapshot = ReadStatus(status);
            if ((snapshot.BoundaryKind ?? admission?.BoundaryKind) == "childJoin" &&
                snapshot.DispatchHeld is { } held && held != (snapshot.Phase != "ready"))
                throw Unavailable();
            if (admission is null)
            {
                if (snapshot.Phase != "pending" ||
                    snapshot.BoundaryKind == "childJoin" && eventType == Prefix + "pending" ||
                    snapshot.BoundaryKind is null && eventType != Prefix + "pending" ||
                    effects is not null && effects != eventType[(Prefix.Length)..])
                    throw Unavailable();
            }
            else
            {
                var suffix = snapshot.Phase switch
                {
                    "pending" => "progress",
                    "ready" => "ready",
                    "blocked" => "blocked",
                    "indeterminate" => "indeterminate",
                    "failed" => "failed",
                    _ => throw Unavailable()
                };
                if (eventType != Prefix + suffix ||
                    snapshot.SourceCheckpointId != admission.SourceCheckpointId ||
                    snapshot.SourceCheckpointRevision != admission.SourceCheckpointRevision ||
                    snapshot.PreviousExecutionFence != admission.PreviousExecutionFence ||
                    snapshot.ExecutionFence != admission.ExecutionFence ||
                    snapshot.PreviousRuntimeInstanceId != admission.PreviousRuntimeInstanceId ||
                    snapshot.PreviousRegistrationRevision != admission.PreviousRegistrationRevision ||
                    Conflict(snapshot.SourceCheckpointHash, admission.SourceCheckpointHash) ||
                    Conflict(snapshot.RecoveryCheckpointId, admission.RecoveryCheckpointId) ||
                    Conflict(snapshot.RecoveryCheckpointRevision, admission.RecoveryCheckpointRevision) ||
                    Conflict(snapshot.RecoveryCheckpointHash, admission.RecoveryCheckpointHash) ||
                    Conflict(snapshot.BoundaryKind, admission.BoundaryKind) ||
                    Conflict(snapshot.ChildManifestSha256, admission.ChildManifestSha256) ||
                    Conflict(snapshot.DecisionStateVersion, admission.DecisionStateVersion) ||
                    Conflict(snapshot.RuntimeInstanceId, admission.RuntimeInstanceId) ||
                    Conflict(snapshot.RegistrationRevision, admission.RegistrationRevision))
                    throw Unavailable();
            }
            return new(eventId, sequence, eventType, version, occurredAt, operationId, snapshot)
            {
                RuntimeEffectsState = effects
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Unavailable();
        }
    }

    private static CheckpointRecoveryEventStatus ReadStatus(JsonElement value)
    {
        var previousFence = RequiredLong(value, "previousExecutionFence");
        var fence = RequiredLong(value, "executionFence");
        if (previousFence == long.MaxValue || fence != previousFence + 1)
            throw Unavailable();
        var result = new CheckpointRecoveryEventStatus(
            RequiredString(value, "phase"), RequiredString(value, "sourceCheckpointId"),
            RequiredLong(value, "sourceCheckpointRevision"), previousFence, fence,
            RequiredGuid(value, "previousRuntimeInstanceId"), RequiredLong(value, "previousRegistrationRevision"))
        {
            CurrentStep = OptionalString(value, "currentStep"),
            ReasonCode = OptionalString(value, "reasonCode"),
            RecoveryMaterialMode = OptionalString(value, "recoveryMaterialMode"),
            BoundaryKind = OptionalString(value, "boundaryKind"),
            DispatchHeld = OptionalBool(value, "dispatchHeld"),
            SourceCheckpointHash = OptionalHash(value, "sourceCheckpointHash"),
            RecoveryCheckpointId = OptionalString(value, "recoveryCheckpointId"),
            RecoveryCheckpointRevision = OptionalLong(value, "recoveryCheckpointRevision"),
            RecoveryCheckpointHash = OptionalHash(value, "recoveryCheckpointHash"),
            DecisionStateVersion = OptionalLong(value, "decisionStateVersion", allowZero: true),
            RuntimeInstanceId = OptionalGuid(value, "runtimeInstanceId"),
            RegistrationRevision = OptionalLong(value, "registrationRevision"),
            ChildManifestSha256 = OptionalHash(value, "childManifestSha256")
        };
        if (result.BoundaryKind is not (null or "childJoin") ||
            result.RecoveryMaterialMode is not (null or "nativeCache" or "journalRebuild"))
            throw Unavailable();
        return result;
    }

    private static bool Conflict<T>(T? left, T? right) where T : struct =>
        left.HasValue && right.HasValue && !left.Value.Equals(right.Value);
    private static bool Conflict(string? left, string? right) =>
        left is not null && right is not null && !string.Equals(left, right, StringComparison.Ordinal);
    private static JsonElement RequiredObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value : throw Unavailable();
    private static string RequiredString(JsonElement parent, string name) =>
        OptionalString(parent, name) ?? throw Unavailable();
    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 1024 } text ||
            string.IsNullOrWhiteSpace(text))
            throw Unavailable();
        return text;
    }
    private static string? OptionalHash(JsonElement parent, string name)
    {
        var value = OptionalString(parent, name);
        if (value is not null && (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c))))
            throw Unavailable();
        return value;
    }
    private static long RequiredLong(JsonElement parent, string name) =>
        OptionalLong(parent, name) ?? throw Unavailable();
    private static long? OptionalLong(JsonElement parent, string name, bool allowZero = false)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) ||
            number < (allowZero ? 0 : 1))
            throw Unavailable();
        return number;
    }
    private static Guid RequiredGuid(JsonElement parent, string name) =>
        OptionalGuid(parent, name) ?? throw Unavailable();
    private static Guid? OptionalGuid(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var result) || result == Guid.Empty)
            throw Unavailable();
        return result;
    }
    private static bool? OptionalBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Unavailable()
        };
    }
}
