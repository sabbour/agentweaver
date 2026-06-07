using System.Text.Json.Serialization;

namespace Scaffolder.Domain.Payloads;

public sealed record RunCompletedPayload
{
    [JsonPropertyName("step_count")]
    public required int StepCount { get; init; }
}
