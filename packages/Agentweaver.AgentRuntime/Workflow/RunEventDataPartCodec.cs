using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Encodes/decodes <see cref="RunEvent"/> instances as A2A <c>DataContent</c> parts
/// (<c>application/x-agentweaver-run-event+json</c>).
///
/// <para>
/// Used on both sides of the A2A bridge (§4.7.2, §10.1):
/// <list type="bullet">
///   <item><b>Pod (encoder, in <c>Agentweaver.AgentHost</c>):</b> encodes each
///     <c>RunEvent</c> emitted by <c>CopilotAIAgent</c> as a <c>DataContent</c> appended to
///     the A2A <c>message:stream</c> <c>AgentResponseUpdate</c>.</item>
///   <item><b>Worker (decoder, in <see cref="RemoteAgentProxy"/>):</b> detects DataContent
///     items with <see cref="MediaType"/> and re-emits them to the worker's
///     <c>ChannelWriter&lt;RunEvent&gt;</c>, restoring the side-channel event stream that
///     feeds <c>RunStreamStore</c> → SSE.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Sequence numbers:</b> the Sequence field in the decoded <c>RunEvent</c> is from the
/// pod's counter and is discarded by <c>RecordingChannelWriter</c>, which reassigns a fresh
/// monotonic sequence from <c>RunStreamEntry.RecordNext</c> in arrival order, preserving
/// total ordering as specified in §4.4.
/// </para>
/// </summary>
public static class RunEventDataPartCodec
{
    /// <summary>A2A DataContent media type that identifies a forwarded RunEvent.</summary>
    public const string MediaType = "application/x-agentweaver-run-event+json";

    /// <summary>
    /// Tries to decode a <see cref="DataContent"/> item as a <see cref="RunEvent"/>.
    /// Returns <see langword="null"/> if the content's media type does not match or the
    /// payload cannot be parsed.
    /// </summary>
    public static RunEvent? TryDecodeRunEvent(DataContent content)
    {
        if (!string.Equals(content.MediaType, MediaType, StringComparison.OrdinalIgnoreCase))
            return null;

        ReadOnlyMemory<byte>? dataBytes = content.Data;
        if (dataBytes is null || dataBytes.Value.IsEmpty)
            return null;

        try
        {
            var dto = JsonSerializer.Deserialize(
                dataBytes.Value.Span,
                RunEventDtoJsonContext.Default.RunEventDto);

            if (dto is null || dto.Type is null)
                return null;

            return new RunEvent(dto.Sequence, dto.Type, dto.Payload!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Encodes a <see cref="RunEvent"/> as a <see cref="DataContent"/> for inclusion in an
    /// A2A <c>AgentResponseUpdate</c>. Called by the pod's <c>AgentHost</c> (Morpheus's
    /// territory) — defined here so both ends share the same codec.
    /// </summary>
    public static DataContent EncodeRunEvent(RunEvent runEvent)
    {
        var dto = new RunEventDto
        {
            Sequence = runEvent.Sequence,
            Type = runEvent.Type,
            Payload = runEvent.Payload,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, RunEventDtoJsonContext.Default.RunEventDto);
        return new DataContent(new ReadOnlyMemory<byte>(bytes), MediaType);
    }

    // DTO used internally for serialization / deserialization.
    internal sealed class RunEventDto
    {
        [JsonPropertyName("sequence")]
        public int Sequence { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("payload")]
        public object? Payload { get; set; }
    }
}

[JsonSerializable(typeof(RunEventDataPartCodec.RunEventDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class RunEventDtoJsonContext : JsonSerializerContext { }
