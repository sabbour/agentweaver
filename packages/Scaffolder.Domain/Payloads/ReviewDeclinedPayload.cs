using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ReviewDeclinedPayload
{
    [JsonPropertyName("declined_by")]
    public required string DeclinedBy { get; init; }
}
