using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record MergeCompletedPayload
{
    [JsonPropertyName("merged_commit_hash")]
    public required string MergedCommitHash { get; init; }
}
