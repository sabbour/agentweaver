using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ToolResultPayload
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("bytes_read_or_written")]
    public required long BytesReadOrWritten { get; init; }
}
