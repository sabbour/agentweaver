using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record ReviewRequestedPayload
{
    /// <summary>The committed tree hash that anchors the approval gate.</summary>
    [JsonPropertyName("tree_hash")]
    public required string TreeHash { get; init; }
}
