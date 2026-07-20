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
    [JsonPropertyName("skill_bindings")] public IReadOnlyList<BlueprintSkillBindingDto?>? SkillBindings { get; init; }
    /// <summary>Bespoke (non-catalog) roles minted by generation; each id also appears in <see cref="Roster"/>.</summary>
    [JsonPropertyName("bespoke_roles")] public IReadOnlyList<BespokeRoleDto>? BespokeRoles { get; init; }

    [JsonPropertyName("exportability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlueprintExportabilityDto? Exportability { get; init; }

    public static BlueprintDto FromModel(Blueprint b, BlueprintExportability? exportability = null) => new()
    {
        Id = b.Id,
        Name = b.Name,
        Description = b.Description,
        Roster = b.Roster,
        Workflow = b.Workflow,
        Workflows = b.Workflows,
        ReviewPolicy = b.ReviewPolicy,
        SandboxProfile = b.SandboxProfile,
        SkillBindings = b.SkillBindings.Select(BlueprintSkillBindingDto.FromModel).ToList(),
        BespokeRoles = b.BespokeRoles.Select(BespokeRoleDto.FromModel).ToList(),
        Exportability = exportability is null ? null : BlueprintExportabilityDto.FromModel(exportability),
    };

    public Blueprint ToModel() => new(
        Id ?? string.Empty,
        Name ?? string.Empty,
        Description ?? string.Empty,
        Roster ?? [],
        Workflows is { Count: > 0 } ? Workflows : (Workflow is not null ? [Workflow] : ["default"]),
        ReviewPolicy ?? string.Empty,
        SandboxProfile ?? string.Empty)
    {
        BespokeRoles = BespokeRoles is { Count: > 0 }
            ? BespokeRoles.Select(r => r.ToModel()).ToList()
            : [],
        SkillBindings = SkillBindings is { Count: > 0 }
            ? SkillBindings
                .Select(binding => binding?.ToModel() ?? new BlueprintSkillBinding(string.Empty, []))
                .ToList()
            : [],
    };
}

/// <summary>Stable availability diagnostics for a built-in blueprint.</summary>
public sealed record BlueprintExportabilityDto
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("codes")] public required IReadOnlyList<string> Codes { get; init; }

    public static BlueprintExportabilityDto FromModel(BlueprintExportability exportability) => new()
    {
        Status = exportability.Status,
        Codes = exportability.Codes,
    };
}

public sealed record BlueprintSkillBindingDto
{
    [JsonPropertyName("role_id")] public string? RoleId { get; init; }
    [JsonPropertyName("skills")] public IReadOnlyList<string>? Skills { get; init; }

    public static BlueprintSkillBindingDto FromModel(BlueprintSkillBinding binding) => new()
    {
        RoleId = binding.RoleId,
        Skills = binding.Skills,
    };

    public BlueprintSkillBinding ToModel() => new(RoleId ?? string.Empty, Skills ?? []);
}

/// <summary>Wire shape for a <see cref="BespokeRole"/> (snake_case). Mirrors its three fields.</summary>
public sealed record BespokeRoleDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("charter")] public string? Charter { get; init; }

    public static BespokeRoleDto FromModel(BespokeRole r) => new()
    {
        Id = r.Id,
        Title = r.Title,
        Charter = r.Charter,
    };

    public BespokeRole ToModel() => new(
        Id ?? string.Empty,
        Title ?? string.Empty,
        Charter ?? string.Empty);
}

public sealed record ListBlueprintsResponse
{
    /// <summary>The catalog blueprints available to apply or inspect.</summary>
    [JsonPropertyName("blueprints")] public required IReadOnlyList<BlueprintDto> Blueprints { get; init; }
}

/// <summary>Request body for generating a blueprint draft from prose.</summary>
public sealed record GenerateBlueprintRequest
{
    /// <summary>Natural-language description of the team or workflow shape to generate.</summary>
    [JsonPropertyName("description")] public string? Description { get; init; }
    /// <summary>Optional project id whose existing defaults should ground model selection and validation.</summary>
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    /// <summary>Optional repository hint that helps the model tailor workflows and roles to the target codebase.</summary>
    [JsonPropertyName("target_repository")] public string? TargetRepository { get; init; }
}

/// <summary>Generated blueprint payload returned from the blueprint-generation endpoint.</summary>
public sealed record GenerateBlueprintResponse
{
    /// <summary>The validated blueprint draft inferred from the prompt.</summary>
    [JsonPropertyName("blueprint")] public required BlueprintDto Blueprint { get; init; }
    /// <summary>
    /// Present when the LLM found no suitable library workflow and <c>IWorkflowGenerator</c> produced
    /// a custom workflow (FR-063). Pass this back as <c>generated_workflow_yaml</c> on project
    /// creation so the workflow is materialized to the project workspace on apply.
    /// </summary>
    [JsonPropertyName("generated_workflow_yaml")] public string? GeneratedWorkflowYaml { get; init; }
    /// <summary>Non-fatal generation warnings the caller may want to inspect before applying the result.</summary>
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Request body for validating a blueprint payload offline.</summary>
public sealed record ValidateBlueprintRequest
{
    [JsonPropertyName("blueprint")] public BlueprintDto? Blueprint { get; init; }
}

/// <summary>Validation result for an inline or generated blueprint.</summary>
public sealed record ValidateBlueprintResponse
{
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("errors")] public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>Request body for asking the backend to recommend a catalog blueprint for a GitHub repository.</summary>
public sealed record SuggestBlueprintRequest
{
    [JsonPropertyName("repository")] public string? Repository { get; init; }
}

/// <summary>Repository-to-blueprint recommendation returned by the suggestion endpoint.</summary>
public sealed record SuggestBlueprintResponse
{
    [JsonPropertyName("recommended_blueprint")] public BlueprintDto? RecommendedBlueprint { get; init; }
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("signals")] public IReadOnlyList<string> Signals { get; init; } = [];
    [JsonPropertyName("fallback")] public bool Fallback { get; init; }
}
