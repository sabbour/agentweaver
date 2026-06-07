using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record RunStartedPayload
{
    [JsonPropertyName("submitting_user")]
    public required string SubmittingUser { get; init; }

    [JsonPropertyName("model_source")]
    public required ModelSource ModelSource { get; init; }

    [JsonPropertyName("repository_path")]
    public required string RepositoryPath { get; init; }

    [JsonPropertyName("originating_branch")]
    public required string OriginatingBranch { get; init; }
}
