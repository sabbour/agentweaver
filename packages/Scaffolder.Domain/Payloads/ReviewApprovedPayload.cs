using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ReviewApprovedPayload
{
    [JsonPropertyName("tree_hash")]
    public required string TreeHash { get; init; }

    [JsonPropertyName("approved_by")]
    public required string ApprovedBy { get; init; }
}
