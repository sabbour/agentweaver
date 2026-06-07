using System.Text.Json;
using Scaffolder.Domain;

namespace Scaffolder.Api.Contracts;

/// <summary>
/// Renders a <see cref="RunEvent"/> as the client-facing JSON envelope defined
/// by FR-018: runId, sequence, type, timestamp, payload (embedded object), and
/// an optional callId. The stored payload string is embedded as a JSON object
/// rather than re-encoded as a string.
/// </summary>
public static class EventEnvelope
{
    public static string ToClientJson(RunEvent evt)
    {
        using var payloadDoc = JsonDocument.Parse(evt.Payload);
        var envelope = new EventEnvelopeDto
        {
            RunId = evt.RunId.ToString(),
            Sequence = evt.Sequence,
            Type = evt.Type,
            Timestamp = evt.Timestamp,
            Payload = payloadDoc.RootElement.Clone(),
            CallId = evt.CallId
        };

        return JsonSerializer.Serialize(envelope, JsonDefaults.Options);
    }
}
