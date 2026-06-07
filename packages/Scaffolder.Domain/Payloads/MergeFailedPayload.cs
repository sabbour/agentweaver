using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record MergeFailedPayload
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
