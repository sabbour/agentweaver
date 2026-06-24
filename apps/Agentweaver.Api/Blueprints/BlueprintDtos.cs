using System.Text.Json.Serialization;
using Agentweaver.Squad.Model;

namespace Agentweaver.Api.Blueprints;

/// <summary>
/// Wire shape for a blueprint (snake_case). Mirrors <see cref="Blueprint"/>. Used by
/// GET /api/blueprints, POST /api/blueprints/validate, POST /api/blueprints/generate, and the
/// optional inline blueprint on project creation. Input fields are nullable so malformed payloads
/// are reported by validation rather than failing deserialization.
/// Both <c>workflow</c> (legacy single id) and <c>workflows</c> (new array) are accepted as input;
/// the response always includes both for backward compatibility.
/// </summary>
public sealed record BlueprintDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("roster")] public IReadOnlyList<string>? Roster { get; init; }
    /// <summary>Legacy single workflow id (backward compat input and response default-workflow field).</summary>
    [JsonPropertyName("workflow")] public string? Workflow { get; init; }
    /// <summary>The full set of workflow ids this blueprint bundles (Feature 015 US3).</summary>
    [JsonPropertyName("workflows")] public IReadOnlyList<string>? Workflows { get; init; }
    [JsonPropertyName("review_policy")] public string? ReviewPolicy { get; init; }
    [JsonPropertyName("sandbox_profile")] public string? SandboxProfile { get; init; }

    public static BlueprintDto FromModel(Blueprint b) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Description = b.Description,
        Roster = b.Roster,
        Workflow = b.Workflow,
        Workflows = b.Workflows,
        ReviewPolicy = b.ReviewPolicy,
        SandboxProfile = b.SandboxProfile,
    };

    public Blueprint ToModel() => new(
        Id ?? string.Empty,
        Name ?? string.Empty,
        Description ?? string.Empty,
        Roster ?? [],
        Workflows is { Count: > 0 } ? Workflows : (Workflow is not null ? [Workflow] : ["default"]),
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
    /// <summary>
    /// Present when the LLM found no suitable library workflow and <c>IWorkflowGenerator</c> produced
    /// a custom workflow (FR-063). Pass this back as <c>generated_workflow_yaml</c> on project
    /// creation so the workflow is materialized to the project workspace on apply.
    /// </summary>
    [JsonPropertyName("generated_workflow_yaml")] public string? GeneratedWorkflowYaml { get; init; }
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
