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
        // Drop malformed codes, dedupe, and sort so the output is stable across calls and safe to show in the UI.
        // Cap at 16 codes to keep the response small.
        var normalized = codes
            .Where(code => code.Length is > 0 and <= 64 &&
                code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(16)
            .ToImmutableArray();
        // No remaining codes means every check passed.
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

/// <summary>A blueprint skill binding that doesn't take effect in the draft, with a stable reason code.</summary>
public sealed record BlueprintUnresolvedSkillBinding(string Role, string SkillId, string Code)
{
    /// <summary>The project has no skill with this ID.</summary>
    public const string SkillNotInstalled = "skill_not_installed";

    /// <summary>The skill has no pinned revision, so it can't be assigned to agents.</summary>
    public const string SkillNotPinned = "skill_not_pinned";

    /// <summary>The skill is assigned but stays disabled, because the blueprint doesn't change the user's setting.</summary>
    public const string SkillDisabled = "skill_disabled";
}

/// <summary>A casting draft produced by a blueprint and the skill bindings it couldn't apply.</summary>
public sealed record BlueprintApplication(
    ProjectConfiguration Draft,
    ImmutableArray<BlueprintUnresolvedSkillBinding> UnresolvedSkillBindings);

/// <summary>A pending blueprint casting proposal and the skill bindings it couldn't apply.</summary>
public sealed record BlueprintCastingProposalResult(
    ProjectCastingProposal Proposal,
    ImmutableArray<BlueprintUnresolvedSkillBinding> UnresolvedSkillBindings);

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

    // Built-in blueprints ship as embedded JSON resources whose names start with this prefix.
    private const string ResourcePrefix = "Agentweaver.Projects.Config.Resources.Blueprints.";

    // The resource files use the 0.x snake_case keys; duplicate keys are rejected so a typo can't hide a value.
    private static readonly JsonSerializerOptions ResourceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowDuplicateProperties = false,
    };

    // Loaded once on first use; Lazy makes the first load thread-safe.
    private static readonly Lazy<ImmutableArray<BlueprintDefinition>> BuiltIn = new(LoadBuiltIn);

    /// <summary>Returns the built-in blueprints in ID order.</summary>
    public static ImmutableArray<BlueprintDefinition> Blueprints => BuiltIn.Value;

    /// <summary>Lists every built-in blueprint with its default workflow and availability.</summary>
    public static BlueprintCatalogResponse List()
    {
        // Find IDs shared by more than one built-in blueprint; each copy is then reported as unavailable,
        // because an ID lookup could not tell them apart.
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
        // A default (uninitialized) ImmutableArray throws on use, so treat it as empty.
        var roster = blueprint.Roster.IsDefault ? [] : blueprint.Roster;
        var workflows = blueprint.Workflows.IsDefault ? [] : blueprint.Workflows;
        // Blueprint-level shape checks.
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

        // Each role must be castable: a valid ID, not reserved for the platform, and defined either in the
        // v1 role catalog or as a bespoke role in this blueprint.
        foreach (var roleId in roster)
        {
            if (!IsSafeId(roleId))
                codes.Add("role_invalid_id");
            else if (ProjectCastingCatalog.IsReservedRole(roleId))
                codes.Add("role_reserved");
            else if (FindCatalogRole(roleId) is null && FindBespokeRole(blueprint, roleId) is null)
                codes.Add("role_missing");
        }

        // Each workflow must be registered in v1. Only the default workflow exists today, so blueprints that
        // name other workflows report workflow_unavailable until more workflows are registered.
        foreach (var workflowId in workflows)
        {
            if (!IsSafeId(workflowId))
                codes.Add("workflow_invalid_id");
            else if (!AvailableWorkflowIds.Contains(workflowId))
                codes.Add("workflow_unavailable");
        }

        // Every other Validate rule, so an exportable blueprint is never rejected as invalid_blueprint.
        // Workflow availability is already reported above with its own code, so it is skipped here.
        if (!Validate(blueprint, requireAvailableWorkflows: false).Valid)
            codes.Add("blueprint_invalid");

        return BlueprintExportability.FromCodes(codes);
    }

    /// <summary>Checks a blueprint with the 0.x rules and returns every error without changing state.</summary>
    public static BlueprintValidationResult Validate(BlueprintDefinition blueprint) =>
        Validate(blueprint, requireAvailableWorkflows: true);

    /// <summary>
    /// Checks a blueprint with the 0.x rules. With <paramref name="requireAvailableWorkflows"/> false, workflows only
    /// need valid, unique IDs; the AgentSuite converter uses this because availability is checked at apply time.
    /// </summary>
    internal static BlueprintValidationResult Validate(BlueprintDefinition blueprint, bool requireAvailableWorkflows)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        // Collect every error instead of stopping at the first, so the caller can fix them all at once.
        var errors = new List<string>();
        // Arrays missing from JSON arrive as default ImmutableArrays; treat them as empty.
        var roster = blueprint.Roster.IsDefault ? [] : blueprint.Roster;
        var workflows = blueprint.Workflows.IsDefault ? [] : blueprint.Workflows;
        var bindings = blueprint.SkillBindings.IsDefault ? [] : blueprint.SkillBindings;
        var bespokeRoles = blueprint.BespokeRoles.IsDefault ? [] : blueprint.BespokeRoles;

        // Identity: ID and name are required, and the ID must be a safe slug.
        if (string.IsNullOrWhiteSpace(blueprint.Id))
            errors.Add("Blueprint id is required.");
        else if (!IsSafeId(blueprint.Id))
            errors.Add($"Blueprint id '{blueprint.Id}' must use lowercase letters, digits, and hyphens (at most 64 characters).");
        if (string.IsNullOrWhiteSpace(blueprint.Name))
            errors.Add("Blueprint name is required.");

        // Review policy: only "default" is supported.
        if (string.IsNullOrWhiteSpace(blueprint.ReviewPolicy))
            errors.Add("review_policy is required.");
        else if (!string.Equals(blueprint.ReviewPolicy, DefaultReviewPolicy, StringComparison.Ordinal))
            errors.Add($"Unknown review_policy '{blueprint.ReviewPolicy}'. Supported: default.");

        // Workflows: at least one, no empty or duplicate IDs. Skip further checks on an entry that already failed
        // so one bad entry reports one error.
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
            // Strict mode needs a registered workflow; relaxed mode only needs a well-formed ID.
            if (requireAvailableWorkflows && !AvailableWorkflowIds.Contains(workflowId))
                errors.Add($"Unknown workflow '{workflowId}'.");
            else if (!requireAvailableWorkflows && !IsSafeId(workflowId))
                errors.Add($"Workflow id '{workflowId}' must use lowercase letters, digits, and hyphens.");
        }

        // Sandbox profile: "default" keeps the platform baseline, "restricted" removes project egress.
        if (!string.Equals(blueprint.SandboxProfile, DefaultSandboxProfile, StringComparison.Ordinal) &&
            !string.Equals(blueprint.SandboxProfile, RestrictedSandboxProfile, StringComparison.Ordinal))
            errors.Add($"Unknown sandbox_profile '{blueprint.SandboxProfile}'. Supported: default, restricted.");

        // Bespoke roles are checked before the roster, because roster entries may refer to them.
        var bespokeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bespoke in bespokeRoles)
        {
            // Without an ID nothing else about the role can be checked or reported by name.
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
            // A bespoke role can't take a platform-reserved name or shadow a catalog role.
            if (ProjectCastingCatalog.IsReservedRole(bespoke.Id))
                errors.Add($"Bespoke role '{bespoke.Id}' uses a reserved role name.");
            else if (FindCatalogRole(bespoke.Id) is not null)
                errors.Add($"Bespoke role '{bespoke.Id}' collides with a catalog role.");
            if (!bespokeIds.Add(bespoke.Id))
                errors.Add($"Duplicate bespoke role '{bespoke.Id}'.");
            // A bespoke role that is never cast is almost certainly a mistake.
            if (!roster.Contains(bespoke.Id, StringComparer.Ordinal))
                errors.Add($"Bespoke role '{bespoke.Id}' is not in the roster.");
        }

        // Roster: at least one role; each role must be unique, not reserved, and known to the catalog or
        // defined as a bespoke role above.
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

        // Skill bindings: each must name a roster role and list at least one non-empty skill ID. Whether the
        // skill is installed is not checked here; that is reported at apply time as an unresolved binding.
        foreach (var binding in bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.RoleId))
            {
                errors.Add("Skill bindings must have a role_id.");
                continue;
            }
            // seenRoles holds the valid, unique roster entries collected above.
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
        // Use List() rather than Blueprints so the lookup sees the same availability codes the UI shows.
        var entry = List().Blueprints.FirstOrDefault(item =>
            string.Equals(item.Blueprint.Id, blueprintId, StringComparison.Ordinal))
            ?? throw ProjectConfigException.Invalid(
                "unknown_blueprint",
                $"No predefined blueprint with id '{blueprintId}'.");
        // A known but unavailable blueprint is rejected with its reason codes so the caller can explain why.
        if (entry.Exportability.Status != BlueprintExportability.Exportable)
            throw ProjectConfigException.Invalid(
                "blueprint_unavailable",
                $"Blueprint '{blueprintId}' is unavailable: {string.Join(", ", entry.Exportability.Codes)}.");
        return entry.Blueprint;
    }

    /// <summary>
    /// Resolves exactly one of a built-in ID or an inline blueprint and returns a validated casting draft with the
    /// skill bindings it couldn't assign.
    /// </summary>
    /// <exception cref="ProjectConfigException">The request is ambiguous, unknown, unavailable, or invalid.</exception>
    public static BlueprintApplication CreateDraft(
        ProjectConfiguration current,
        string? blueprintId,
        BlueprintDefinition? inline)
    {
        ArgumentNullException.ThrowIfNull(current);
        var hasId = !string.IsNullOrWhiteSpace(blueprintId);
        // Exactly one source is allowed: true == true means both were given, false == false means neither.
        if (hasId == (inline is not null))
            throw ProjectConfigException.Invalid(
                "invalid_blueprint_request",
                "Provide either blueprintId or blueprint, not both.");

        // Built-ins must be exportable; inline blueprints rely on the full validation below.
        var blueprint = hasId ? GetExportable(blueprintId!) : inline!;
        // Validate both sources in strict mode, so unregistered workflows are rejected before a draft is built.
        var validation = Validate(blueprint);
        if (!validation.Valid)
            throw ProjectConfigException.Invalid(
                "invalid_blueprint",
                $"Blueprint is invalid: {string.Join(" ", validation.Errors)}");
        return CreateDraft(current, blueprint);
    }

    /// <summary>Builds charters and casting from the roster, then applies the blueprint projection.</summary>
    private static BlueprintApplication CreateDraft(ProjectConfiguration current, BlueprintDefinition blueprint)
    {
        var charters = ImmutableArray.CreateBuilder<ProjectAgentCharter>(blueprint.Roster.Length);
        var casting = ImmutableArray.CreateBuilder<ProjectAgentCast>(blueprint.Roster.Length);
        for (var index = 0; index < blueprint.Roster.Length; index++)
        {
            // Each role becomes one agent whose ID equals the role ID; roster order sets the cast order.
            var roleId = blueprint.Roster[index];
            var (title, charter) = ResolveRole(blueprint, roleId);
            charters.Add(new ProjectAgentCharter(roleId, title, roleId, charter));
            casting.Add(new ProjectAgentCast(roleId, roleId, index));
        }

        // The blueprint replaces the whole roster; all other settings start from the current configuration.
        var draft = current with { AgentCharters = charters.ToImmutable(), Casting = casting.ToImmutable() };
        // Reuse the same apply step as the AgentSuite import. Agent IDs equal role IDs here, so the map is identity.
        return ApplyBlueprint(
            draft,
            ToAgentSuiteBlueprint(blueprint),
            blueprint.Roster.ToDictionary(roleId => roleId, roleId => roleId, StringComparer.Ordinal));
    }

    /// <summary>Returns the title and charter text for a roster role, from a bespoke role or the role catalog.</summary>
    internal static (string Title, string Charter) ResolveRole(BlueprintDefinition blueprint, string roleId)
    {
        // A bespoke role wins over the catalog; its title falls back to the role ID when blank.
        if (FindBespokeRole(blueprint, roleId) is { } bespoke)
            return (string.IsNullOrWhiteSpace(bespoke.Title) ? roleId : bespoke.Title, bespoke.Charter);
        // Validation already guarantees the role exists, so a miss here is a programming error.
        var role = FindCatalogRole(roleId)
            ?? throw new ArgumentException($"Role '{roleId}' is not in the role catalog or the blueprint.", nameof(roleId));
        return (role.Title, ProjectCastingCatalog.BuildCharter(role));
    }

    /// <summary>
    /// Converts a 0.x blueprint to the projection that travels with an AgentSuite import. The roster becomes suite
    /// agents, so it isn't part of the projection.
    /// </summary>
    public static ProjectAgentSuiteBlueprint ToAgentSuiteBlueprint(BlueprintDefinition blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        return new()
        {
            SourceBlueprintId = blueprint.Id,
            DisplayName = blueprint.Name,
            // Blank text becomes null so the projection doesn't carry empty strings.
            Description = string.IsNullOrWhiteSpace(blueprint.Description) ? null : blueprint.Description,
            // With no workflows, fall back to the default workflow so DefaultWorkflow is always in the list.
            Workflows = blueprint.Workflows.IsDefaultOrEmpty ? ["default"] : blueprint.Workflows,
            DefaultWorkflow = DefaultWorkflowId(blueprint),
            ReviewPolicy = blueprint.ReviewPolicy,
            SandboxProfile = blueprint.SandboxProfile,
            // Roster role IDs become the suite's native agent IDs.
            SkillBindings = (blueprint.SkillBindings.IsDefault ? [] : blueprint.SkillBindings)
                .Select(binding => new ProjectAgentSuiteSkillBinding(binding.RoleId, binding.Skills))
                .ToImmutableArray(),
        };
    }

    /// <summary>
    /// Checks a blueprint projection against the 0.x rules and the suite's native agent IDs. Returns every error;
    /// an empty result means the projection is valid.
    /// </summary>
    public static ImmutableArray<string> ValidateBlueprint(
        ProjectAgentSuiteBlueprint blueprint,
        IReadOnlyCollection<string> nativeAgentIds)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(nativeAgentIds);
        // Collect every error so the import can report them all in one response.
        var errors = new List<string>();
        var workflows = blueprint.Workflows.IsDefault ? [] : blueprint.Workflows;
        // The source ID is optional, but when present it must be a safe slug because it keys workflow references.
        if (blueprint.SourceBlueprintId is { } sourceId && !IsSafeId(sourceId))
            errors.Add($"Blueprint id '{sourceId}' must use lowercase letters, digits, and hyphens (at most 64 characters).");
        if (workflows.IsEmpty)
            errors.Add("workflows must list at least one workflow.");
        var seenWorkflows = new HashSet<string>(StringComparer.Ordinal);
        // Apply time is strict: every workflow must be registered in v1, not just well-formed.
        foreach (var workflowId in workflows)
        {
            if (string.IsNullOrWhiteSpace(workflowId))
                errors.Add("workflows must not contain empty ids.");
            else if (!seenWorkflows.Add(workflowId))
                errors.Add($"Duplicate workflow '{workflowId}'.");
            else if (!AvailableWorkflowIds.Contains(workflowId))
                errors.Add($"Unknown workflow '{workflowId}'.");
        }
        // The default workflow must be one of the listed workflows; a null default never matches.
        if (!seenWorkflows.Contains(blueprint.DefaultWorkflow ?? string.Empty))
            errors.Add($"defaultWorkflow '{blueprint.DefaultWorkflow}' must be one of the workflows.");
        if (!string.Equals(blueprint.ReviewPolicy, DefaultReviewPolicy, StringComparison.Ordinal))
            errors.Add($"Unknown review_policy '{blueprint.ReviewPolicy}'. Supported: default.");
        if (!string.Equals(blueprint.SandboxProfile, DefaultSandboxProfile, StringComparison.Ordinal) &&
            !string.Equals(blueprint.SandboxProfile, RestrictedSandboxProfile, StringComparison.Ordinal))
            errors.Add($"Unknown sandbox_profile '{blueprint.SandboxProfile}'. Supported: default, restricted.");
        // Bindings use the suite's native agent IDs, because the projection has no roster of its own.
        foreach (var binding in blueprint.SkillBindings.IsDefault ? [] : blueprint.SkillBindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Role))
            {
                errors.Add("Skill bindings must have a role.");
                continue;
            }
            if (!nativeAgentIds.Contains(binding.Role))
                errors.Add($"Skill binding role '{binding.Role}' is not a suite agent.");
            if (binding.Skills.IsDefaultOrEmpty || binding.Skills.Any(string.IsNullOrWhiteSpace))
                errors.Add($"Skill binding for role '{binding.Role}' must list at least one skill.");
        }
        return errors.ToImmutableArray();
    }

    /// <summary>
    /// Applies a validated blueprint projection to a casting draft that already has its charters and casting.
    /// It references every workflow under the source blueprint, sets the default workflow, adds bound agents to
    /// skills the project already installed, and removes all project egress for the <c>restricted</c> sandbox.
    /// </summary>
    /// <param name="draft">The casting draft.</param>
    /// <param name="blueprint">A projection that passed <see cref="ValidateBlueprint"/>.</param>
    /// <param name="agentIds">Maps each native agent ID (role) to its cast agent ID.</param>
    public static BlueprintApplication ApplyBlueprint(
        ProjectConfiguration draft,
        ProjectAgentSuiteBlueprint blueprint,
        IReadOnlyDictionary<string, string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(blueprint);
        ArgumentNullException.ThrowIfNull(agentIds);
        // Flatten bindings to unique (role, skill) pairs. Roles with no cast agent are skipped, since there is
        // no agent to assign the skill to.
        var bindings = (blueprint.SkillBindings.IsDefault ? [] : blueprint.SkillBindings)
            .Where(binding => agentIds.ContainsKey(binding.Role))
            .SelectMany(binding => binding.Skills.Select(skill => (Role: binding.Role, Skill: skill)))
            .Distinct()
            .ToList();
        // Invert to skill ID -> cast agent IDs, so each skill setting is updated in one pass.
        var boundAgents = bindings
            .GroupBy(item => item.Skill, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => agentIds[item.Role]).Distinct(StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        // Agents cast in the draft; existing assignments to anyone else are dropped.
        var cast = (draft.Casting.IsDefault ? [] : draft.Casting)
            .Select(item => item.AgentId)
            .ToHashSet(StringComparer.Ordinal);
        var currentSkills = draft.Skills.IsDefault ? [] : draft.Skills;
        // Only existing skill settings are updated; a blueprint never installs a new skill.
        var skills = currentSkills
            .Select(skill => ApplySkillBinding(skill, boundAgents, cast))
            .ToImmutableArray();
        // Report bindings that won't take effect, in a stable role-then-skill order for the reviewer.
        var unresolved = bindings
            .Select(binding => UnresolvedCode(currentSkills, binding.Skill) is { } code
                ? new BlueprintUnresolvedSkillBinding(binding.Role, binding.Skill, code)
                : null)
            .OfType<BlueprintUnresolvedSkillBinding>()
            .OrderBy(item => item.Role, StringComparer.Ordinal)
            .ThenBy(item => item.SkillId, StringComparer.Ordinal)
            .ToImmutableArray();

        return new(draft with
        {
            // Record which blueprint each workflow came from. Without a source ID, keep the existing references.
            BlueprintWorkflowReferences = blueprint.SourceBlueprintId is { } sourceId
                ? blueprint.Workflows
                    .Select(workflowId => new BlueprintWorkflowReference(sourceId, workflowId))
                    .ToImmutableArray()
                : draft.BlueprintWorkflowReferences,
            DefaultWorkflowId = blueprint.DefaultWorkflow,
            Skills = skills,
            // Restricted sandbox: an empty narrowing list means no project egress at all.
            // The default profile leaves the current narrowing as is.
            EgressNarrowing = string.Equals(blueprint.SandboxProfile, RestrictedSandboxProfile, StringComparison.Ordinal)
                ? ImmutableArray<NetworkEgressRule>.Empty
                : draft.EgressNarrowing,
        }, unresolved);
    }

    /// <summary>
    /// Keeps a pinned skill's assignments on agents that stay cast and adds bound roster agents. It never changes
    /// <see cref="SkillCatalogSetting.Enabled"/>. Unpinned skills are unchanged.
    /// </summary>
    private static SkillCatalogSetting ApplySkillBinding(
        SkillCatalogSetting skill,
        IReadOnlyDictionary<string, ImmutableArray<string>> boundAgents,
        IReadOnlySet<string> cast)
    {
        // Null AgentIds means the skill isn't pinned and can't be assigned per agent; leave it alone.
        if (skill.AgentIds is not { } assigned)
            return skill;
        // Drop assignments to agents that are no longer cast, keeping the original order.
        var agents = assigned.Where(cast.Contains).ToList();
        // Append newly bound agents without duplicating ones already assigned.
        if (boundAgents.TryGetValue(skill.SkillId, out var bound))
            agents.AddRange(bound.Where(agent => !agents.Contains(agent, StringComparer.Ordinal)));
        // Only AgentIds changes; Enabled stays as the user set it.
        return skill with { AgentIds = agents.ToImmutableArray() };
    }

    /// <summary>Returns why a bound skill doesn't take effect in the draft, or null when it does.</summary>
    private static string? UnresolvedCode(ImmutableArray<SkillCatalogSetting> skills, string skillId)
    {
        var skill = skills.FirstOrDefault(item => string.Equals(item.SkillId, skillId, StringComparison.Ordinal));
        // Checks run in order of severity, so each binding gets the single most relevant code.
        if (skill is null)
            return BlueprintUnresolvedSkillBinding.SkillNotInstalled;
        if (skill.AgentIds is null)
            return BlueprintUnresolvedSkillBinding.SkillNotPinned;
        // The agent is assigned, but a disabled skill won't load until the user enables it.
        return skill.Enabled ? null : BlueprintUnresolvedSkillBinding.SkillDisabled;
    }

    private static ProjectCastingRoleDefinition? FindCatalogRole(string roleId) =>
        ProjectCastingCatalog.Roles.FirstOrDefault(role => string.Equals(role.Id, roleId, StringComparison.Ordinal));

    private static BlueprintBespokeRole? FindBespokeRole(BlueprintDefinition blueprint, string roleId) =>
        blueprint.BespokeRoles.IsDefault
            ? null
            : blueprint.BespokeRoles.FirstOrDefault(role =>
                role is not null && string.Equals(role.Id, roleId, StringComparison.Ordinal));

    // Ignores invalid IDs (they are reported separately) and compares case-insensitively to catch near-duplicates.
    private static bool HasDuplicate(ImmutableArray<string> values) =>
        values.Where(IsSafeId).GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);

    /// <summary>Lowercase letters, digits, and inner hyphens, at most 64 characters.</summary>
    private static bool IsSafeId(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 64 &&
        (char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])) &&
        value[^1] != '-' &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    // Reads every embedded blueprint JSON file. A malformed file throws, so a bad build fails on first use
    // instead of silently dropping a blueprint.
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
            // Sort by ID so the list order doesn't depend on resource enumeration order.
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
