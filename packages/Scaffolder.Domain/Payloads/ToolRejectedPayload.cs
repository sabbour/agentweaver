using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ToolRejectedPayload
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
