using System.Globalization;
using System.Text.Json;

namespace Agentweaver.Gateway;

internal sealed class GatewayEventProjectionException : Exception
{
    public GatewayEventProjectionException(string code) : base(code) => Code = code;

    public string Code { get; }
}

internal static class GatewayRunEventProjection
{
    private const int MaximumPageEvents = 200;
    private const int MaximumCursorLength = 8192;
    private const string RemoteToolCallIdPrefix = "remote_tool_call_";
    private const string RemoteToolNamePrefix = "remote_tool_";

    private static readonly string[] EventProperties =
    [
        "schemaVersion", "eventVersion", "eventId", "identity", "position",
        "occurredAt", "kind", "payload", "objectReferences"
    ];

    public static byte[] ProjectPage(ReadOnlyMemory<byte> utf8Json, string? cursor, int limit)
    {
        if (limit is < 1 or > MaximumPageEvents ||
            cursor is not null && !IsSafeCursor(cursor))
            throw InvalidCursor();
        try
        {
            using var document = JsonDocument.Parse(
                utf8Json, new JsonDocumentOptions { MaxDepth = 16 });
            var page = document.RootElement;
            RequireProperties(page, ["events", "nextCursor", "hasMore"]);
            var events = Property(page, "events");
            if (events.ValueKind != JsonValueKind.Array ||
                events.GetArrayLength() > Math.Min(limit, MaximumPageEvents))
                throw InvalidProjection();

            var nextCursor = NullableString(Property(page, "nextCursor"));
            var hasMore = Property(page, "hasMore");
            if (hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidProjection();
            var hasMoreValue = hasMore.GetBoolean();
            if (events.GetArrayLength() == 0)
            {
                if (!string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                    throw InvalidCursor();
                if (hasMoreValue)
                    throw InvalidProjection();
            }
            else if (!IsSafeCursor(nextCursor) ||
                string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                throw InvalidCursor();

            foreach (var sessionEvent in events.EnumerateArray())
                _ = ProjectEvent(sessionEvent);
            return JsonSerializer.SerializeToUtf8Bytes(page);
        }
        catch (JsonException)
        {
            throw InvalidProjection();
        }
    }

    public static JsonElement ProjectEvent(JsonElement sessionEvent)
    {
        RequireProperties(sessionEvent, EventProperties);
        if (Integer(Property(sessionEvent, "schemaVersion")) != 1 ||
            Integer(Property(sessionEvent, "eventVersion")) is < 1 or > 2 ||
            !Guid.TryParseExact(RequiredString(Property(sessionEvent, "eventId")), "D", out var eventId) ||
            eventId == Guid.Empty ||
            Integer64(Property(sessionEvent, "position")) <= 0 ||
            !DateTimeOffset.TryParse(
                RequiredString(Property(sessionEvent, "occurredAt")),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            throw InvalidProjection();

        ValidateIdentity(Property(sessionEvent, "identity"));
        var eventKind = RequiredString(Property(sessionEvent, "kind"));
        var payload = Property(sessionEvent, "payload");
        var payloadKind = RequiredString(Property(payload, "kind"));
        ValidatePayload(eventKind, payloadKind, payload, eventId);
        ValidateObjectReferences(Property(sessionEvent, "objectReferences"));
        if (payloadKind == "runtime_configuration" &&
            Property(sessionEvent, "objectReferences").GetArrayLength() != 0)
            throw InvalidProjection();
        return sessionEvent.Clone();
    }

    private static void ValidatePayload(
        string eventKind, string payloadKind, JsonElement payload, Guid eventId)
    {
        switch (payloadKind)
        {
            case "turn":
                RequireProperties(payload, ["kind", "role", "content"]);
                _ = RequiredString(Property(payload, "role"));
                ValidateReference(Property(payload, "content"));
                RequireEventKind(eventKind, "turn");
                break;
            case "tool_call":
                RequireProperties(payload, ["kind", "callId", "toolName", "state"],
                    ["arguments", "result"]);
                var callId = RequiredString(Property(payload, "callId"));
                var toolName = RequiredString(Property(payload, "toolName"));
                if (callId.StartsWith(RemoteToolCallIdPrefix, StringComparison.OrdinalIgnoreCase) ||
                    toolName.StartsWith(RemoteToolNamePrefix, StringComparison.OrdinalIgnoreCase))
                    throw InvalidProjection();
                _ = RequiredString(Property(payload, "state"));
                ValidateOptionalReference(payload, "arguments");
                ValidateOptionalReference(payload, "result");
                RequireEventKind(eventKind, "toolCall");
                break;
            case "policy_evaluation":
                RequireProperties(payload,
                [
                    "kind", "actorId", "tenantId", "stepId", "grantId", "grantRevision",
                    "purpose", "actionId", "outcome", "reasonCode", "fence", "providerId",
                    "adapterVersion", "optionsSchemaVersion", "optionsRevision"
                ]);
                RequireStringProperties(payload,
                [
                    "actorId", "tenantId", "stepId", "grantId", "grantRevision", "purpose",
                    "actionId", "outcome", "reasonCode", "providerId", "adapterVersion", "optionsRevision"
                ]);
                _ = Integer64(Property(payload, "fence"));
                _ = Integer(Property(payload, "optionsSchemaVersion"));
                RequireEventKind(eventKind, "policyEvaluation");
                break;
            case "decision_accepted":
                RequireProperties(payload,
                    ["kind", "decisionId", "decisionType", "selectedOption", "acceptedEffectIds"],
                    ["rationale"]);
                RequireStringProperties(payload, ["decisionId", "decisionType", "selectedOption"]);
                ValidateOptionalReference(payload, "rationale");
                ValidateStringArray(Property(payload, "acceptedEffectIds"));
                RequireEventKind(eventKind, "decisionAccepted");
                break;
            case "effect_accepted":
                RequireProperties(payload, ["kind", "effectId", "effectType"], ["receipt"]);
                RequireStringProperties(payload, ["effectId", "effectType"]);
                ValidateOptionalReference(payload, "receipt");
                RequireEventKind(eventKind, "effectAccepted");
                break;
            case "artifact_reference":
                RequireProperties(payload, ["kind", "artifact"]);
                ValidateReference(Property(payload, "artifact"));
                RequireEventKind(eventKind, "artifactReference");
                break;
            case "cache_reference":
                RequireProperties(payload, ["kind", "cache", "runtimeVersion", "bindingId"]);
                ValidateReference(Property(payload, "cache"));
                RequireStringProperties(payload, ["runtimeVersion", "bindingId"]);
                RequireEventKind(eventKind, "cacheReference");
                break;
            case "runtime_configuration":
                RequireProperties(payload, ["kind", "configuration"]);
                if (Property(payload, "configuration").ValueKind != JsonValueKind.Null)
                    throw InvalidProjection();
                RequireEventKind(eventKind, "runtimeConfiguration");
                break;
            case "addressed_message":
                RequireProperties(payload,
                [
                    "kind", "messageId", "sender", "recipient", "threadId",
                    "threadSequence", "purpose"
                ]);
                RequireGuid(Property(payload, "messageId"));
                RequireGuid(Property(payload, "threadId"));
                ValidateIdentity(Property(payload, "sender"));
                ValidateIdentity(Property(payload, "recipient"));
                _ = Integer64(Property(payload, "threadSequence"));
                _ = RequiredString(Property(payload, "purpose"));
                RequireEventKind(eventKind, "addressedMessage");
                break;
            case "produced_run_capture":
                RequireProperties(payload, ["kind", "capture", "package"]);
                ValidateCapture(Property(payload, "capture"));
                ValidateReference(Property(payload, "package"));
                RequireEventKind(eventKind, "producedRunCapture");
                break;
            default:
                throw InvalidProjection();
        }
    }

    private static void ValidateCapture(JsonElement capture)
    {
        RequireProperties(capture,
        [
            "contractVersion", "identity", "captureId", "eventId", "actorIssuer",
            "actorSubject", "tenantId", "sourceControlPinId", "acceptedSelectionHash",
            "workspaceId", "workspaceIncarnationId", "branchName", "repositoryId",
            "resourceGeneration", "baseSha", "outputTreeSha", "manifestSha256",
            "manifestByteLength", "patchSha256", "patchByteLength", "packageSha256",
            "packageByteLength", "capturedAt"
        ]);
        _ = Integer(Property(capture, "contractVersion"));
        ValidateIdentity(Property(capture, "identity"));
        RequireStringProperties(capture,
        [
            "captureId", "actorIssuer", "actorSubject", "tenantId", "sourceControlPinId",
            "acceptedSelectionHash", "workspaceId", "branchName", "repositoryId",
            "baseSha", "outputTreeSha", "manifestSha256", "patchSha256", "packageSha256"
        ]);
        RequireGuid(Property(capture, "eventId"));
        RequireGuid(Property(capture, "workspaceIncarnationId"));
        _ = Integer64(Property(capture, "resourceGeneration"));
        _ = Integer64(Property(capture, "manifestByteLength"));
        _ = Integer64(Property(capture, "patchByteLength"));
        _ = Integer64(Property(capture, "packageByteLength"));
        _ = Date(Property(capture, "capturedAt"));
    }

    private static void ValidateObjectReferences(JsonElement references)
    {
        if (references.ValueKind != JsonValueKind.Array)
            throw InvalidProjection();
        foreach (var storedReference in references.EnumerateArray())
        {
            RequireProperties(storedReference, ["reference", "retention"]);
            ValidateReference(Property(storedReference, "reference"));
            var retention = Property(storedReference, "retention");
            RequireProperties(retention, ["ownerId", "retainUntil"]);
            _ = RequiredString(Property(retention, "ownerId"));
            _ = Date(Property(retention, "retainUntil"));
        }
    }

    private static void ValidateReference(JsonElement reference)
    {
        RequireProperties(reference, ["key", "purpose"], ["byteLength", "material"]);
        var key = RequiredString(Property(reference, "key"));
        var purpose = RequiredString(Property(reference, "purpose"));
        if (key.Contains("remote-tool-result", StringComparison.OrdinalIgnoreCase) ||
            purpose.Contains("remote-tool-result", StringComparison.OrdinalIgnoreCase) ||
            purpose.Contains("runtime-configuration", StringComparison.OrdinalIgnoreCase))
            throw InvalidProjection();
        if (reference.TryGetProperty("byteLength", out var byteLength) &&
            byteLength.ValueKind is not JsonValueKind.Null and not JsonValueKind.Number)
            throw InvalidProjection();
        if (!reference.TryGetProperty("material", out var material) ||
            material.ValueKind == JsonValueKind.Null)
            return;

        RequireProperties(material,
        [
            "contractVersion", "kind", "tenantId", "sha256", "runtimeInstanceId",
            "registrationRevision", "executionFence", "acceptedSelectionHash", "sdkVersion",
            "runtimeVersion", "modelSelectionReference", "modelId"
        ], ["maxPromptTokens"]);
        var materialKind = RequiredString(Property(material, "kind"));
        if (materialKind is not ("turnContent" or "sdkCache") ||
            material.TryGetProperty("remoteToolResultProof", out _))
            throw InvalidProjection();
        _ = Integer(Property(material, "contractVersion"));
        RequireStringProperties(material,
        [
            "tenantId", "sha256", "acceptedSelectionHash", "sdkVersion", "runtimeVersion",
            "modelSelectionReference", "modelId"
        ]);
        RequireGuid(Property(material, "runtimeInstanceId"));
        _ = Integer64(Property(material, "registrationRevision"));
        _ = Integer64(Property(material, "executionFence"));
        if (material.TryGetProperty("maxPromptTokens", out var maxPromptTokens) &&
            maxPromptTokens.ValueKind is not JsonValueKind.Null and not JsonValueKind.Number)
            throw InvalidProjection();
    }

    private static void ValidateOptionalReference(JsonElement value, string name)
    {
        var reference = Property(value, name);
        if (reference.ValueKind != JsonValueKind.Null)
            ValidateReference(reference);
    }

    private static void ValidateIdentity(JsonElement identity)
    {
        RequireProperties(identity, ["projectId", "runId", "sessionId"]);
        RequireStringProperties(identity, ["projectId", "runId", "sessionId"]);
    }

    private static void ValidateStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw InvalidProjection();
        foreach (var item in value.EnumerateArray())
            _ = RequiredString(item);
    }

    private static void RequireStringProperties(JsonElement value, string[] names)
    {
        foreach (var name in names)
            _ = RequiredString(Property(value, name));
    }

    private static void RequireEventKind(string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw InvalidProjection();
    }

    private static void RequireProperties(
        JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidProjection();
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        if (optional is not null)
            allowed.UnionWith(optional);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw InvalidProjection();
        }
        if (required.Any(name => !seen.Contains(name)))
            throw InvalidProjection();
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property : throw InvalidProjection();

    private static string RequiredString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text : throw InvalidProjection();

    private static string? NullableString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : RequiredString(value);

    private static int Integer(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result : throw InvalidProjection();

    private static long Integer64(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result : throw InvalidProjection();

    private static Guid RequireGuid(JsonElement value) =>
        Guid.TryParseExact(RequiredString(value), "D", out var result) && result != Guid.Empty
            ? result : throw InvalidProjection();

    private static DateTimeOffset Date(JsonElement value) =>
        DateTimeOffset.TryParse(
            RequiredString(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
            ? result : throw InvalidProjection();

    private static bool IsSafeCursor(string? value) =>
        value is { Length: > 0 and <= MaximumCursorLength } &&
        value.All(character => character is not '\r' and not '\n' and not '\0');

    private static GatewayEventProjectionException InvalidProjection() =>
        new("events_page_contract_invalid");

    private static GatewayEventProjectionException InvalidCursor() =>
        new("events_page_cursor_invalid");
}
