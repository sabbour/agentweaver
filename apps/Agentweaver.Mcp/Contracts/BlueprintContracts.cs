using System.Text.Json.Serialization;

namespace Agentweaver.Mcp.Contracts;

// ── Blueprint ────────────────────────────────────────────────────────────────

/// <summary>
/// Wire shape for a blueprint. Mirrors the backend BlueprintDto (snake_case).
/// sandbox_profile is "default" or "restricted".
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
}

// ── Responses ────────────────────────────────────────────────────────────────

/// <summary>Response for GET /api/blueprints.</summary>
public sealed record ListBlueprintsResponse
{
    [JsonPropertyName("blueprints")] public required IReadOnlyList<BlueprintDto> Blueprints { get; init; }
}

/// <summary>Response for POST /api/blueprints/generate.</summary>
public sealed record GenerateBlueprintResponse
{
    [JsonPropertyName("blueprint")] public required BlueprintDto Blueprint { get; init; }
}

/// <summary>Response for POST /api/blueprints/validate.</summary>
public sealed record ValidateBlueprintResponse
{
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("errors")] public required IReadOnlyList<string> Errors { get; init; }
}
