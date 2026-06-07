using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record RunFailedPayload
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
