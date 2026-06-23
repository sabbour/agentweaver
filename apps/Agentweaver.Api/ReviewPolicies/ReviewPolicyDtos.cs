using System.Text.Json.Serialization;

namespace Agentweaver.Api.ReviewPolicies;

/// <summary>A review step in API responses (snake_case).</summary>
public sealed record ReviewStepDto
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
}

/// <summary>A review policy in a list response: identity, steps, and validation status (FR-033).</summary>
public sealed record ReviewPolicySummaryDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_active")] public required bool IsActive { get; init; }
}

/// <summary>Response body for the project's review-policies list (GET/POST sync).</summary>
public sealed record ReviewPolicyListResponse
{
    [JsonPropertyName("active_policy_name")] public required string ActivePolicyName { get; init; }
    [JsonPropertyName("policies")] public required IReadOnlyList<ReviewPolicySummaryDto> Policies { get; init; }
}

/// <summary>Full definition for GET a single review policy.</summary>
public sealed record ReviewPolicyDetailDto
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_active")] public required bool IsActive { get; init; }
    [JsonPropertyName("steps")] public required IReadOnlyList<ReviewStepDto> Steps { get; init; }
}

/// <summary>Request body for PUT the project's active review policy.</summary>
public sealed record SetActiveReviewPolicyRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>Maps the review-policy domain model to API DTOs (server-side only, Principles III/IV).</summary>
public static class ReviewPolicyDtoMapper
{
    public static string StepKindToApi(ReviewStepKind kind) => kind switch
    {
        ReviewStepKind.Rai => "rai",
        ReviewStepKind.Rubberduck => "rubberduck",
        ReviewStepKind.HumanReview => "human-review",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ReviewStepDto ToDto(ReviewStep step) => new()
    {
        Kind = StepKindToApi(step.Kind),
        Label = step.Label,
    };

    public static ReviewPolicySummaryDto ToSummary(ReviewPolicyLoadResult result, string activeName)
    {
        var policy = result.Policy;
        return new ReviewPolicySummaryDto
        {
            Name = policy?.Name,
            Description = policy?.Description,
            Source = result.Source,
            Valid = result.IsValid,
            Error = result.Error,
            IsBuiltIn = result.IsBuiltIn,
            IsActive = policy is not null && string.Equals(policy.Name, activeName, StringComparison.Ordinal),
        };
    }

    public static ReviewPolicyDetailDto ToDetail(ReviewPolicyLoadResult result, string activeName)
    {
        var policy = result.Policy!;
        return new ReviewPolicyDetailDto
        {
            Name = policy.Name,
            Description = policy.Description,
            Source = result.Source,
            IsBuiltIn = result.IsBuiltIn,
            IsActive = string.Equals(policy.Name, activeName, StringComparison.Ordinal),
            Steps = policy.Steps.Select(ToDto).ToList(),
        };
    }
}
