using System.Text.Json.Serialization;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Blueprints;

/// <summary>
/// Wire shape for a blueprint (snake_case). Mirrors <see cref="Blueprint"/>. Used by
/// GET /api/blueprints, POST /api/blueprints/validate, POST /api/blueprints/generate, and the
/// optional inline blueprint on project creation. Input fields are nullable so malformed payloads
/// are reported by validation rather than failing deserialization.
/// </summary>
public sealed record BlueprintDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("roster")] public IReadOnlyList<string>? Roster { get; init; }
    [JsonPropertyName("workflow")] public string? Workflow { get; init; }
    [JsonPropertyName("review_policy")] public string? ReviewPolicy { get; init; }
    [JsonPropertyName("sandbox_profile")] public string? SandboxProfile { get; init; }

    public static BlueprintDto FromModel(Blueprint b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Description = b.Description,
        Roster = b.Roster,
        Workflow = b.Workflow,
        ReviewPolicy = b.ReviewPolicy,
        SandboxProfile = b.SandboxProfile,
    };

    public Blueprint ToModel() => new(
        Id ?? string.Empty,
        Name ?? string.Empty,
        Description ?? string.Empty,
        Roster ?? [],
        Workflow ?? string.Empty,
        ReviewPolicy ?? string.Empty,
        SandboxProfile ?? string.Empty);
}

public sealed record ListBlueprintsResponse
{
    [JsonPropertyName("blueprints")] public required IReadOnlyList<BlueprintDto> Blueprints { get; init; }
}

public sealed record GenerateBlueprintRequest
{
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed record GenerateBlueprintResponse
{
    [JsonPropertyName("blueprint")] public required BlueprintDto Blueprint { get; init; }
}

public sealed record ValidateBlueprintRequest
{
    [JsonPropertyName("blueprint")] public BlueprintDto? Blueprint { get; init; }
}

public sealed record ValidateBlueprintResponse
{
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("errors")] public required IReadOnlyList<string> Errors { get; init; }
}
