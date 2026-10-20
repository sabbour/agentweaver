using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>Skills that a blueprint assigns to one roster role.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlueprintSkillBinding
{
    public string RoleId { get; init; } = string.Empty;
    public ImmutableArray<string> Skills { get; init; } = [];
}

/// <summary>A role defined only by one blueprint, with its own charter text.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlueprintBespokeRole
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Charter { get; init; } = string.Empty;
}

/// <summary>
/// A team recipe: roster roles, workflows (the first is the default), review policy, sandbox profile, and skill bindings.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlueprintDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ImmutableArray<string> Roster { get; init; } = [];
    public ImmutableArray<string> Workflows { get; init; } = [];
    public string ReviewPolicy { get; init; } = string.Empty;
    public string SandboxProfile { get; init; } = string.Empty;
    public ImmutableArray<BlueprintSkillBinding> SkillBindings { get; init; } = [];
    public ImmutableArray<BlueprintBespokeRole> BespokeRoles { get; init; } = [];
}

/// <summary>Whether a catalog blueprint can be applied, with sorted, stable reason codes when it cannot.</summary>
public sealed record BlueprintExportability(string Status, ImmutableArray<string> Codes)
{
    /// <summary>Status for a blueprint that every check accepts.</summary>
    public const string Exportable = "exportable";

    /// <summary>Status for a blueprint with at least one reason code.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Normalizes codes to distinct, sorted, bounded <c>[a-z0-9_]</c> values and derives the status.</summary>
    public static BlueprintExportability FromCodes(IEnumerable<string> codes)
    {
        var normalized = codes
            .Where(code => code.Length is > 0 and <= 64 &&
                code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(16)
            .ToImmutableArray();
        return new(normalized.IsEmpty ? Exportable : Unavailable, normalized);
    }
}

/// <summary>One built-in blueprint with its default workflow and current availability.</summary>
public sealed record BlueprintCatalogEntry(
    BlueprintDefinition Blueprint,
    string DefaultWorkflowId,
    BlueprintExportability Exportability);

/// <summary>Response for the blueprint list route.</summary>
public sealed record BlueprintCatalogResponse(ImmutableArray<BlueprintCatalogEntry> Blueprints);

/// <summary>Request to check a blueprint without changing state.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ValidateBlueprintRequest
{
    public BlueprintDefinition? Blueprint { get; init; }
}

/// <summary>Result of blueprint validation; errors are empty when the blueprint is valid.</summary>
public sealed record BlueprintValidationResult(bool Valid, ImmutableArray<string> Errors);

/// <summary>Request to stage a casting proposal from a built-in blueprint ID or an inline blueprint.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateBlueprintProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public string? BlueprintId { get; init; }
    public BlueprintDefinition? Blueprint { get; init; }
}

/// <summary>
/// Owns the built-in 0.x blueprints, their availability checks, validation, and conversion into reviewed casting drafts.
/// </summary>
public static class ProjectBlueprintCatalog
{
    /// <summary>The only supported review policy.</summary>
    public const string DefaultReviewPolicy = "default";

    /// <summary>Sandbox profile that keeps the platform network baseline.</summary>
    public const string DefaultSandboxProfile = "default";

    /// <summary>Sandbox profile that removes all project network egress.</summary>
    public const string RestrictedSandboxProfile = "restricted";

    /// <summary>Largest bespoke role charter, in characters.</summary>
    public const int MaxBespokeCharterLength = 8192;

    /// <summary>
    /// Workflow IDs registered by the v1 Orchestrator. Blueprints that use any other workflow report
    /// <c>workflow_unavailable</c> until the workflow library adds it.
    /// </summary>
    public static readonly ImmutableHashSet<string> AvailableWorkflowIds =
        ImmutableHashSet.Create(StringComparer.Ordinal, "default");

    private const string ResourcePrefix = "Agentweaver.Projects.Config.Resources.Blueprints.";

    private static readonly JsonSerializerOptions ResourceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowDuplicateProperties = false,
    };

    private static readonly Lazy<ImmutableArray<BlueprintDefinition>> BuiltIn = new(LoadBuiltIn);

    /// <summary>Returns the built-in blueprints in ID order.</summary>
    public static ImmutableArray<BlueprintDefinition> Blueprints => BuiltIn.Value;

    /// <summary>Lists every built-in blueprint with its default workflow and availability.</summary>
    public static BlueprintCatalogResponse List()
    {
        var duplicateIds = Blueprints
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new(Blueprints
            .Select(item => new BlueprintCatalogEntry(
                item,
                DefaultWorkflowId(item),
                Exportability(item, duplicateIds.Contains(item.Id))))
            .ToImmutableArray());
    }

    /// <summary>Returns the first workflow, or <c>default</c> when the blueprint has none.</summary>
    public static string DefaultWorkflowId(BlueprintDefinition blueprint) =>
        blueprint.Workflows.IsDefaultOrEmpty ? "default" : blueprint.Workflows[0];

    /// <summary>Computes availability against the current v1 role catalog and registered workflows.</summary>
    public static BlueprintExportability Exportability(BlueprintDefinition blueprint, bool duplicateId = false)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var codes = new List<string>();
        var roster = blueprint.Roster.IsDefault ? [] : blueprint.Roster;
        var workflows = blueprint.Workflows.IsDefault ? [] : blueprint.Workflows;
        if (!IsSafeId(blueprint.Id))
            codes.Add("blueprint_invalid_id");
        if (duplicateId)
            codes.Add("blueprint_duplicate_id");
        if (roster.IsEmpty)
            codes.Add("blueprint_missing_roster");
        if (workflows.IsEmpty)
            codes.Add("blueprint_missing_workflow");
        if (HasDuplicate(roster))
            codes.Add("blueprint_duplicate_role");
        if (HasDuplicate(workflows))
            codes.Add("blueprint_duplicate_workflow");

        foreach (var roleId in roster)
        {
            if (!IsSafeId(roleId))
                codes.Add("role_invalid_id");
            else if (ProjectCastingCatalog.IsReservedRole(roleId))
                codes.Add("role_reserved");
            else if (FindCatalogRole(roleId) is null && FindBespokeRole(blueprint, roleId) is null)
                codes.Add("role_missing");
        }

        foreach (var workflowId in workflows)
        {
            if (!IsSafeId(workflowId))
                codes.Add("workflow_invalid_id");
            else if (!AvailableWorkflowIds.Contains(workflowId))
                codes.Add("workflow_unavailable");
        }

        return BlueprintExportability.FromCodes(codes);
    }

    /// <summary>Checks a blueprint with the 0.x rules and returns every error without changing state.</summary>
    public static BlueprintValidationResult Validate(BlueprintDefinition blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var errors = new List<string>();
        var roster = blueprint.Roster.IsDefault ? [] : blueprint.Roster;
        var workflows = blueprint.Workflows.IsDefault ? [] : blueprint.Workflows;
        var bindings = blueprint.SkillBindings.IsDefault ? [] : blueprint.SkillBindings;
        var bespokeRoles = blueprint.BespokeRoles.IsDefault ? [] : blueprint.BespokeRoles;

        if (string.IsNullOrWhiteSpace(blueprint.Id))
            errors.Add("Blueprint id is required.");
        else if (!IsSafeId(blueprint.Id))
            errors.Add($"Blueprint id '{blueprint.Id}' must use lowercase letters, digits, and hyphens (at most 64 characters).");
        if (string.IsNullOrWhiteSpace(blueprint.Name))
            errors.Add("Blueprint name is required.");

        if (string.IsNullOrWhiteSpace(blueprint.ReviewPolicy))
            errors.Add("review_policy is required.");
        else if (!string.Equals(blueprint.ReviewPolicy, DefaultReviewPolicy, StringComparison.Ordinal))
            errors.Add($"Unknown review_policy '{blueprint.ReviewPolicy}'. Supported: default.");

        if (workflows.IsEmpty)
            errors.Add("workflows must list at least one workflow.");
        var seenWorkflows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var workflowId in workflows)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
            {
                errors.Add("workflows must not contain empty ids.");
                continue;
            }
            if (!seenWorkflows.Add(workflowId))
            {
                errors.Add($"Duplicate workflow '{workflowId}'.");
                continue;
            }
            if (!AvailableWorkflowIds.Contains(workflowId))
                errors.Add($"Unknown workflow '{workflowId}'.");
        }

        if (!string.Equals(blueprint.SandboxProfile, DefaultSandboxProfile, StringComparison.Ordinal) &&
            !string.Equals(blueprint.SandboxProfile, RestrictedSandboxProfile, StringComparison.Ordinal))
            errors.Add($"Unknown sandbox_profile '{blueprint.SandboxProfile}'. Supported: default, restricted.");

        var bespokeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bespoke in bespokeRoles)
        {
            if (bespoke is null || string.IsNullOrWhiteSpace(bespoke.Id))
            {
                errors.Add("Bespoke roles must have an id.");
                continue;
            }
            if (!IsSafeId(bespoke.Id))
                errors.Add($"Bespoke role id '{bespoke.Id}' must use lowercase letters, digits, and hyphens.");
            if (string.IsNullOrWhiteSpace(bespoke.Charter))
                errors.Add($"Bespoke role '{bespoke.Id}' must have a charter.");
            else if (bespoke.Charter.Length > MaxBespokeCharterLength)
                errors.Add($"Bespoke role '{bespoke.Id}' charter exceeds {MaxBespokeCharterLength} characters.");
            if (ProjectCastingCatalog.IsReservedRole(bespoke.Id))
                errors.Add($"Bespoke role '{bespoke.Id}' uses a reserved role name.");
            else if (FindCatalogRole(bespoke.Id) is not null)
                errors.Add($"Bespoke role '{bespoke.Id}' collides with a catalog role.");
            if (!bespokeIds.Add(bespoke.Id))
                errors.Add($"Duplicate bespoke role '{bespoke.Id}'.");
            if (!roster.Contains(bespoke.Id, StringComparer.Ordinal))
                errors.Add($"Bespoke role '{bespoke.Id}' is not in the roster.");
        }

        if (roster.IsEmpty)
            errors.Add("roster must list at least one role.");
        var seenRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleId in roster)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                errors.Add("roster must not contain empty role ids.");
                continue;
            }
            if (!seenRoles.Add(roleId))
            {
                errors.Add($"Duplicate roster role '{roleId}'.");
                continue;
            }
            if (ProjectCastingCatalog.IsReservedRole(roleId))
                errors.Add($"Role '{roleId}' is reserved and cannot be cast.");
            else if (FindCatalogRole(roleId) is null && !bespokeIds.Contains(roleId))
                errors.Add($"Unknown role '{roleId}'.");
        }

        foreach (var binding in bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.RoleId))
            {
                errors.Add("Skill bindings must have a role_id.");
                continue;
            }
            if (!seenRoles.Contains(binding.RoleId))
                errors.Add($"Skill binding role '{binding.RoleId}' is not in the roster.");
            if (binding.Skills.IsDefaultOrEmpty || binding.Skills.Any(string.IsNullOrWhiteSpace))
                errors.Add($"Skill binding for role '{binding.RoleId}' must list at least one skill.");
        }

        return new(errors.Count == 0, errors.ToImmutableArray());
    }

    /// <summary>Finds an exportable built-in blueprint by ID.</summary>
    /// <exception cref="ProjectConfigException">The ID is unknown or the blueprint is unavailable in this version.</exception>
    public static BlueprintDefinition GetExportable(string blueprintId)
    {
        var entry = List().Blueprints.FirstOrDefault(item =>
            string.Equals(item.Blueprint.Id, blueprintId, StringComparison.Ordinal))
            ?? throw ProjectConfigException.Invalid(
                "unknown_blueprint",
                $"No predefined blueprint with id '{blueprintId}'.");
        if (entry.Exportability.Status != BlueprintExportability.Exportable)
            throw ProjectConfigException.Invalid(
                "blueprint_unavailable",
                $"Blueprint '{blueprintId}' is unavailable: {string.Join(", ", entry.Exportability.Codes)}.");
        return entry.Blueprint;
    }

    /// <summary>
    /// Resolves exactly one of a built-in ID or an inline blueprint and returns a validated casting draft.
    /// </summary>
    /// <exception cref="ProjectConfigException">The request is ambiguous, unknown, unavailable, or invalid.</exception>
    public static ProjectConfiguration CreateDraft(
        ProjectConfiguration current,
        string? blueprintId,
        BlueprintDefinition? inline)
    {
        ArgumentNullException.ThrowIfNull(current);
        var hasId = !string.IsNullOrWhiteSpace(blueprintId);
        if (hasId == (inline is not null))
            throw ProjectConfigException.Invalid(
                "invalid_blueprint_request",
                "Provide either blueprintId or blueprint, not both.");

        var blueprint = hasId ? GetExportable(blueprintId!) : inline!;
        var validation = Validate(blueprint);
        if (!validation.Valid)
            throw ProjectConfigException.Invalid(
                "invalid_blueprint",
                $"Blueprint is invalid: {string.Join(" ", validation.Errors)}");
        return CreateDraft(current, blueprint);
    }

    /// <summary>
    /// Builds charters and casting from the roster, references every workflow, makes the first workflow the default,
    /// assigns bound skills that the project already pins, and applies the sandbox profile.
    /// </summary>
    private static ProjectConfiguration CreateDraft(ProjectConfiguration current, BlueprintDefinition blueprint)
    {
        var charters = ImmutableArray.CreateBuilder<ProjectAgentCharter>(blueprint.Roster.Length);
        var casting = ImmutableArray.CreateBuilder<ProjectAgentCast>(blueprint.Roster.Length);
        for (var index = 0; index < blueprint.Roster.Length; index++)
        {
            var roleId = blueprint.Roster[index];
            if (FindBespokeRole(blueprint, roleId) is { } bespoke)
                charters.Add(new ProjectAgentCharter(
                    roleId,
                    string.IsNullOrWhiteSpace(bespoke.Title) ? roleId : bespoke.Title,
                    roleId,
                    bespoke.Charter));
            else
            {
                var role = FindCatalogRole(roleId)!;
                charters.Add(new ProjectAgentCharter(
                    role.Id,
                    role.Title,
                    role.Id,
                    ProjectCastingCatalog.BuildCharter(role)));
            }
            casting.Add(new ProjectAgentCast(roleId, roleId, index));
        }

        var boundAgents = (blueprint.SkillBindings.IsDefault ? [] : blueprint.SkillBindings)
            .SelectMany(binding => binding.Skills.Select(skill => (Skill: skill, Agent: binding.RoleId)))
            .GroupBy(item => item.Skill, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Agent).Distinct(StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        var cast = blueprint.Roster.ToHashSet(StringComparer.Ordinal);
        var skills = (current.Skills.IsDefault ? [] : current.Skills)
            .Select(skill => ApplySkillBinding(skill, boundAgents, cast))
            .ToImmutableArray();

        return current with
        {
            AgentCharters = charters.ToImmutable(),
            Casting = casting.ToImmutable(),
            BlueprintWorkflowReferences = blueprint.Workflows
                .Select(workflowId => new BlueprintWorkflowReference(blueprint.Id, workflowId))
                .ToImmutableArray(),
            DefaultWorkflowId = DefaultWorkflowId(blueprint),
            Skills = skills,
            EgressNarrowing = string.Equals(blueprint.SandboxProfile, RestrictedSandboxProfile, StringComparison.Ordinal)
                ? ImmutableArray<NetworkEgressRule>.Empty
                : current.EgressNarrowing,
        };
    }

    /// <summary>
    /// Keeps a pinned skill's assignments on agents that stay cast and adds bound roster agents. Unpinned skills are unchanged.
    /// </summary>
    private static SkillCatalogSetting ApplySkillBinding(
        SkillCatalogSetting skill,
        IReadOnlyDictionary<string, ImmutableArray<string>> boundAgents,
        IReadOnlySet<string> cast)
    {
        if (skill.AgentIds is not { } assigned)
            return skill;
        var agents = assigned.Where(cast.Contains).ToList();
        if (boundAgents.TryGetValue(skill.SkillId, out var bound))
            agents.AddRange(bound.Where(agent => !agents.Contains(agent, StringComparer.Ordinal)));
        return skill with
        {
            AgentIds = agents.ToImmutableArray(),
            Enabled = skill.Enabled && agents.Count > 0 || bound is { IsEmpty: false },
        };
    }

    private static ProjectCastingRoleDefinition? FindCatalogRole(string roleId) =>
        ProjectCastingCatalog.Roles.FirstOrDefault(role => string.Equals(role.Id, roleId, StringComparison.Ordinal));

    private static BlueprintBespokeRole? FindBespokeRole(BlueprintDefinition blueprint, string roleId) =>
        blueprint.BespokeRoles.IsDefault
            ? null
            : blueprint.BespokeRoles.FirstOrDefault(role =>
                role is not null && string.Equals(role.Id, roleId, StringComparison.Ordinal));

    private static bool HasDuplicate(ImmutableArray<string> values) =>
        values.Where(IsSafeId).GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    /// <summary>Lowercase letters, digits, and inner hyphens, at most 64 characters.</summary>
    private static bool IsSafeId(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 64 &&
        (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) &&
        value[^1] != '-' &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static ImmutableArray<BlueprintDefinition> LoadBuiltIn()
    {
        var assembly = typeof(ProjectBlueprintCatalog).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return JsonSerializer.Deserialize<BlueprintDefinition>(stream, ResourceJsonOptions)
                    ?? throw new InvalidOperationException($"Blueprint resource '{name}' is empty.");
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
