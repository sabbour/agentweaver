using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions;

internal static class PublicSessionEventProjection
{
    private const string RemoteToolCallIdPrefix = "remote_tool_call_";
    private const string RemoteToolNamePrefix = "remote_tool_";
    private const string ExternalToolCallName = "external-tool";

    public static SessionAppendResult Project(SessionAppendResult result) =>
        result with { Event = Project(result.Event) };

    public static SessionEventPage Project(SessionEventPage page)
    {
        if (page.Events.IsDefault)
            throw InvalidEvent();
        return page with { Events = page.Events.Select(Project).ToImmutableArray() };
    }

    public static SessionEventDelivery Project(SessionEventDelivery delivery) =>
        new(Project(delivery.Event), delivery.NextCursor);

    private static SessionEventEnvelope Project(SessionEventEnvelope envelope)
    {
        if (envelope.SchemaVersion != SessionsContractVersions.CurrentSchemaVersion ||
            envelope.EventId == Guid.Empty || envelope.Position <= 0 ||
            !SessionEventPayloadValidation.SupportsEventVersion(envelope.EventVersion, envelope.Payload) ||
            envelope.ObjectReferences.IsDefault)
            throw InvalidEvent();

        ImmutableArray<SessionObjectReference> payloadReferences;
        try
        {
            _ = SessionEventPayloadValidation.KindOf(envelope.Payload);
            payloadReferences = SessionEventPayloadValidation.ValidateAndGetReferences(envelope.Payload);
        }
        catch (ArgumentException)
        {
            throw InvalidEvent();
        }

        var storedReferences = envelope.ObjectReferences.Select(reference => reference.Reference).ToArray();
        var hasPrivateMarker =
            payloadReferences.Any(HasPrivateMarker) ||
            storedReferences.Any(HasPrivateMarker) ||
            envelope.Payload is ToolCallSessionPayload toolCall &&
                (HasReservedPrefix(toolCall.CallId, RemoteToolCallIdPrefix) ||
                 HasReservedPrefix(toolCall.ToolName, RemoteToolNamePrefix));
        if (!hasPrivateMarker)
            return envelope;

        if (envelope.Payload is not ToolCallSessionPayload
            {
                State: "completed",
                Arguments: null,
                Result: { Material: { Kind: SessionMaterialKind.RemoteToolResult, RemoteToolResultProof: { } proof } } result
            } privateCall ||
            privateCall.CallId != SessionMaterialValidation.RemoteToolResultCallId(proof) ||
            privateCall.ToolName != SessionMaterialValidation.RemoteToolResultToolName(proof) ||
            SessionMaterialValidation.RemoteToolResultEventId(proof) != envelope.EventId ||
            proof.ProjectId != envelope.Identity.ProjectId ||
            proof.RunId != envelope.Identity.RunId ||
            proof.SessionId != envelope.Identity.SessionId ||
            result.Purpose != SessionMaterialValidation.Purpose(SessionMaterialKind.RemoteToolResult) ||
            storedReferences.Length != 1 ||
            !SameReference(storedReferences[0], result))
            throw InvalidEvent();

        try
        {
            SessionMaterialValidation.Validate(result);
        }
        catch (ArgumentException)
        {
            throw InvalidEvent();
        }

        return new SessionEventEnvelope(
            envelope.SchemaVersion,
            envelope.EventVersion,
            envelope.EventId,
            envelope.Identity,
            envelope.Position,
            envelope.OccurredAt,
            privateCall with
            {
                CallId = envelope.EventId.ToString("N"),
                ToolName = ExternalToolCallName,
                Arguments = null,
                Result = null
            },
            ImmutableArray<StoredSessionObjectReference>.Empty);
    }

    private static bool HasPrivateMarker(SessionObjectReference reference) =>
        reference.Purpose.Contains("remote-tool-result", StringComparison.OrdinalIgnoreCase) ||
        reference.Material is
        {
            Kind: SessionMaterialKind.RemoteToolResult
        } ||
        reference.Material?.RemoteToolResultProof is not null;

    private static bool SameReference(SessionObjectReference first, SessionObjectReference second)
    {
        if (first.Key != second.Key || first.Purpose != second.Purpose || first.ByteLength != second.ByteLength)
            return false;
        if (first.Material is not { } firstMaterial || second.Material is not { } secondMaterial)
            return first.Material is null && second.Material is null;
        var proofMatches = firstMaterial.RemoteToolResultProof is null &&
                secondMaterial.RemoteToolResultProof is null ||
            SessionMaterialValidation.SameRemoteToolResultProof(
                firstMaterial.RemoteToolResultProof, secondMaterial.RemoteToolResultProof);
        return (firstMaterial with { RemoteToolResultProof = secondMaterial.RemoteToolResultProof }) == secondMaterial &&
            proofMatches;
    }

    private static bool HasReservedPrefix(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static SessionMaterialIntegrityException InvalidEvent() =>
        new("session_public_event_projection_invalid");
}
