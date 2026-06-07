using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record AgentMessagePayload
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}
