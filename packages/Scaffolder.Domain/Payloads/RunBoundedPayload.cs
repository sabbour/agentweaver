using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record RunBoundedPayload
{
    /// <summary>Either "step-count" or "wall-clock".</summary>
    [JsonPropertyName("limit_type")]
    public required string LimitType { get; init; }

    [JsonPropertyName("step_count")]
    public required int StepCount { get; init; }
}
