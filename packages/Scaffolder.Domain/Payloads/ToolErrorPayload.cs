using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ToolErrorPayload
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("error_message")]
    public required string ErrorMessage { get; init; }
}
