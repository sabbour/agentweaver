using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ToolCallPayload
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("operation")]
    public required ToolOperation Operation { get; init; }
}
