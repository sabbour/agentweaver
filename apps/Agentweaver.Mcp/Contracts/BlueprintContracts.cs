using System.Text.Json;
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
    [JsonPropertyName("workflows")] public IReadOnlyList<string>? Workflows { get; init; }
    [JsonPropertyName("review_policy")] public string? ReviewPolicy { get; init; }
    [JsonPropertyName("sandbox_profile")] public string? SandboxProfile { get; init; }
    [JsonPropertyName("skill_bindings")] public IReadOnlyList<BlueprintSkillBindingDto>? SkillBindings { get; init; }
    [JsonPropertyName("bespoke_roles")] public IReadOnlyList<BespokeRoleDto>? BespokeRoles { get; init; }
    [JsonPropertyName("generated_workflow_yaml")] public string? GeneratedWorkflowYaml { get; init; }
    [JsonPropertyName("exportability")] public BlueprintExportabilityDto? Exportability { get; init; }
}

public sealed record BlueprintExportabilityDto
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("codes")] public IReadOnlyList<string>? Codes { get; init; }
}

public sealed record BlueprintSkillBindingDto
{
    [JsonPropertyName("role_id")] public string? RoleId { get; init; }
    [JsonPropertyName("skills")] public IReadOnlyList<string>? Skills { get; init; }
}

public sealed record BespokeRoleDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("charter")] public string? Charter { get; init; }
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
    [JsonPropertyName("generated_workflow_yaml")] public string? GeneratedWorkflowYaml { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BlueprintGenerationArtifactDto
{
    [JsonPropertyName("artifact_id")] public required string ArtifactId { get; init; }
    [JsonPropertyName("logical_id")] public required string LogicalId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
}

public sealed record BlueprintGenerationFailureDto
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }
}

public sealed record BlueprintGenerationProviderSnapshotDto
{
    [JsonPropertyName("provider_kind")] public required string ProviderKind { get; init; }
    [JsonPropertyName("provider_type")] public string? ProviderType { get; init; }
    [JsonPropertyName("provider_key")] public required string ProviderKey { get; init; }
    [JsonPropertyName("provider_scope")] public required string ProviderScope { get; init; }
    [JsonPropertyName("resolution_scope")] public required string ResolutionScope { get; init; }
    [JsonPropertyName("blueprint_model")] public string? BlueprintModel { get; init; }
    [JsonPropertyName("workflow_model")] public string? WorkflowModel { get; init; }
    [JsonPropertyName("credential_binding_version")] public string? CredentialBindingVersion { get; init; }
}

public sealed record BlueprintGenerationJobResponse
{
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("attempt")] public int Attempt { get; init; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("target_repository")] public string? TargetRepository { get; init; }
    [JsonPropertyName("provider_snapshot")] public required BlueprintGenerationProviderSnapshotDto ProviderSnapshot { get; init; }
    [JsonPropertyName("artifact")] public BlueprintGenerationArtifactDto? Artifact { get; init; }
    [JsonPropertyName("failure")] public BlueprintGenerationFailureDto? Failure { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("status_url")] public required string StatusUrl { get; init; }
    [JsonPropertyName("result_url")] public required string ResultUrl { get; init; }
    [JsonPropertyName("cancel_url")] public required string CancelUrl { get; init; }
    [JsonPropertyName("retry_url")] public required string RetryUrl { get; init; }
    [JsonPropertyName("ai_execution_context")] public JsonElement? AiExecutionContext { get; init; }
}

public sealed record BlueprintGenerationResultResponse
{
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("artifact_id")] public required string ArtifactId { get; init; }
    [JsonPropertyName("logical_id")] public required string LogicalId { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("blueprint")] public required BlueprintDto Blueprint { get; init; }
    [JsonPropertyName("generated_workflow_yaml")] public string? GeneratedWorkflowYaml { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Response for POST /api/blueprints/validate.</summary>
public sealed record ValidateBlueprintResponse
{
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("errors")] public required IReadOnlyList<string> Errors { get; init; }
}
