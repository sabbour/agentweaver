using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

/// <summary>Model binding reference, reasoning effort, and optional exact credential or hosted-connection binding.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelSelectionSettings(
    string Reference, SecretRef? CredentialReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModelSourceMode? SourceMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ConnectionId = null)
{
    /// <summary>Requested native-SDK reasoning effort; omission resolves to the platform default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ModelBindingPin { get; init; }
}

/// <summary>Optional model-reference and reasoning-effort changes at one configuration layer.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelSelectionOverride(
    string? Reference = null,
    string? ReasoningEffort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuntimeModelBindingPin? ModelBindingPin = null);

/// <summary>Platform or project model override for one task category.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelTaskSelectionOverride(ModelTaskKind TaskKind, ModelSelectionOverride Selection);

/// <summary>Platform or project model override for all work assigned to one role.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelRoleSelectionOverride(string RoleId, ModelSelectionOverride Selection);

/// <summary>Project model override for all work assigned to one project agent.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelAgentSelectionOverride(string AgentId, ModelSelectionOverride Selection);

/// <summary>Accepted model choice with the authority scope that supplied its credentials.</summary>
public sealed record EffectiveModelSelection(
    ModelSelectionSettings Selection,
    ProjectAuthorityResourceType ConnectionScope);

/// <summary>Project-level provider choice for an exclusive or singleton provider seam.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectProviderOverride(ProviderSeam Seam, string ProviderId);

/// <summary>Ordered project-level provider choices for a composite provider seam.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectOrderedProviderOverride(
    ProviderSeam Seam,
    ImmutableArray<string> ProviderIds);

/// <summary>Project-defined agent name, role, and instructions.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectAgentCharter(string AgentId, string Name, string Role, string Charter);

/// <summary>Project agent's role and stable order in the project cast.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectAgentCast(string AgentId, string Role, int Order);

/// <summary>Project reference connecting a blueprint to one workflow identifier.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlueprintWorkflowReference(string BlueprintId, string WorkflowId);

/// <summary>Skill enablement and order, optionally pinned to imported bytes and assigned project agents.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillCatalogSetting(string SkillId, bool Enabled, int Order)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Revision { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentDigest { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<string>? AgentIds { get; init; }
}

/// <summary>Optional project overrides that may only narrow platform run budgets.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopilotRunLimitOverrides
{
    public int? MaxModelTurns { get; init; }
    public int? MaxToolCalls { get; init; }
    public int? MaxChildren { get; init; }
    public int? MaxConcurrentChildren { get; init; }
    public int? MaxWallTimeSeconds { get; init; }
    public int? MaxPromptTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxRevisionAttempts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotSoftCreditLimit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotHardCreditLimit { get; init; }
    /// <summary>Spend budgets. A budget that reuses a platform budget ID can only make that platform budget stricter.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<SpendBudget>? SpendBudgets { get; init; }
}

/// <summary>Validated effective runtime limits after project overrides are bounded by platform defaults.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopilotRunLimits
{
    public int MaxModelTurns { get; init; }
    public int MaxToolCalls { get; init; }
    public int MaxChildren { get; init; }
    public int MaxConcurrentChildren { get; init; }
    public int MaxWallTimeSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPromptTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxRevisionAttempts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotSoftCreditLimit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotHardCreditLimit { get; init; }
    /// <summary>Effective spend budgets for the run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<SpendBudget>? SpendBudgets { get; init; }
}

/// <summary>Versioned project runtime settings, team, provider choices, skills, and optional source-control references.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectConfiguration
{
    public ModelSelectionSettings? ModelSelection { get; init; }
    /// <summary>Project overrides for one task category.</summary>
    public ImmutableArray<ModelTaskSelectionOverride> ModelTaskOverrides { get; init; } = [];
    /// <summary>Project overrides for all work assigned to one role.</summary>
    public ImmutableArray<ModelRoleSelectionOverride> ModelRoleOverrides { get; init; } = [];
    /// <summary>Project overrides for all work assigned to one agent.</summary>
    public ImmutableArray<ModelAgentSelectionOverride> ModelAgentOverrides { get; init; } = [];
    public ImmutableArray<ProjectProviderOverride> ProviderOverrides { get; init; } = [];
    public ImmutableArray<ProjectOrderedProviderOverride> OrderedProviderOverrides { get; init; } = [];
    public ImmutableArray<ProjectAgentCharter> AgentCharters { get; init; } = [];
    public ImmutableArray<ProjectAgentCast> Casting { get; init; } = [];
    public ImmutableArray<BlueprintWorkflowReference> BlueprintWorkflowReferences { get; init; } = [];
    public string? DefaultWorkflowId { get; init; }
    public ImmutableArray<SkillCatalogSetting> Skills { get; init; } = [];
    public ImmutableArray<NetworkEgressRule>? EgressNarrowing { get; init; }
    public CopilotRunLimitOverrides RunLimits { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceControlProjectSettings? SourceControl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<ReviewedRemoteToolSnapshotReference>? ReviewedRemoteToolSnapshots { get; init; }
}

/// <summary>Platform-wide model, egress, and run-limit defaults inherited by projects.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformRuntimeDefaults
{
    public ModelSelectionSettings? ModelSelection { get; init; }
    /// <summary>Platform task defaults, including the built-in task-based model routes.</summary>
    public ImmutableArray<ModelTaskSelectionOverride> ModelTaskDefaults { get; init; } =
        ModelTaskSelectionDefaults.Create();
    /// <summary>Platform overrides for work assigned to one role.</summary>
    public ImmutableArray<ModelRoleSelectionOverride> ModelRoleDefaults { get; init; } = [];
    /// <summary>Platform overrides for work assigned to one agent.</summary>
    public ImmutableArray<ModelAgentSelectionOverride> ModelAgentDefaults { get; init; } = [];
    public ImmutableArray<NetworkEgressRule> EgressBaseline { get; init; } = [];
    public CopilotRunLimits RunLimits { get; init; } = new();
}

/// <summary>Built-in task routing used until a platform administrator supplies narrower defaults.</summary>
public static class ModelTaskSelectionDefaults
{
    /// <summary>Returns task defaults for reasoning-heavy work and direct implementation work.</summary>
    public static ImmutableArray<ModelTaskSelectionOverride> Create() =>
    [
        new(ModelTaskKind.Planning, new("gpt-6.1-sol", "high")),
        new(ModelTaskKind.Review, new("gpt-6.1-sol", "high")),
        new(ModelTaskKind.Design, new("gpt-6.1-sol", "high")),
        new(ModelTaskKind.Security, new("gpt-6.1-sol", "high")),
        new(ModelTaskKind.CreativeWriting, new("gpt-6.1-sol", "high")),
        new(ModelTaskKind.Complex, new("gpt-6.1-sol", "high")),
        new(ModelTaskKind.CodeWriting, new("gpt-6-luna", "high")),
        new(ModelTaskKind.Straightforward, new("gpt-6-luna", "high"))
    ];
}

/// <summary>Provider seam, adapter/options versions, and capability requirements supplied for run selection.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderRequirement
{
    public ProviderSeam Seam { get; init; }
    public string? MeterSource { get; init; }
    public string RequiredAdapterVersion { get; init; } = string.Empty;
    public int RequiredOptionsSchemaVersion { get; init; }
    public ImmutableHashSet<string> RequiredCapabilities { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public ImmutableHashSet<string> RequiredL3L4Capabilities { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public ImmutableHashSet<string> RequiredL7Capabilities { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}

/// <summary>Immutable owner-provided candidates and run needs used to resolve a run's effective configuration.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunSelectionContext
{
    public string Revision { get; init; } = string.Empty;
    public ImmutableHashSet<string> AvailableModelSelectionReferences { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public ImmutableArray<ProviderRequirement> ProviderRequirements { get; init; } = [];
    public ImmutableArray<NetworkEgressRule> RequiredEgress { get; init; } = [];
}

/// <summary>Revision-fenced request for accepting one run's immutable effective selection.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptRunSelectionRequest
{
    public required long ExpectedProjectConfigRevision { get; init; }
    public required long ExpectedPlatformRuntimeRevision { get; init; }
    public required RunSelectionContext Context { get; init; }
}

/// <summary>
/// Validates project and platform configuration and resolves project limits and egress without widening platform policy.
/// </summary>
public static class ProjectConfigurationValidator
{
    /// <summary>Validates and normalizes a project configuration, rejecting invalid references and inconsistent pins.</summary>
    /// <exception cref="ProjectConfigException">A field, collection, role projection, or runtime setting violates the contract.</exception>
    public static ProjectConfiguration Validate(ProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ProviderOverrides.IsDefault ||
            configuration.OrderedProviderOverrides.IsDefault ||
            configuration.ModelTaskOverrides.IsDefault ||
            configuration.ModelRoleOverrides.IsDefault ||
            configuration.ModelAgentOverrides.IsDefault ||
            configuration.AgentCharters.IsDefault ||
            configuration.Casting.IsDefault ||
            configuration.BlueprintWorkflowReferences.IsDefault ||
            configuration.Skills.IsDefault ||
            configuration.EgressNarrowing is { IsDefault: true } ||
            configuration.RunLimits is null)
            throw Invalid("Configuration collections and run-limit settings must be present.");

        if (configuration.ReviewedRemoteToolSnapshots is { } reviewedSnapshots)
        {
            if (reviewedSnapshots.IsDefault || reviewedSnapshots.Any(snapshot => snapshot is null))
            throw Invalid("Reviewed remote tool snapshot references must be present and non-null.");
            if (reviewedSnapshots
            .Select(snapshot => (snapshot.ProjectId, snapshot.SnapshotId))
            .Distinct()
            .Count() != reviewedSnapshots.Length)
            throw Invalid("Reviewed remote tool snapshot references must not contain duplicate project and snapshot IDs.");
        }

        if (configuration.ModelSelection is { } model)
        {
            ValidateIdentifier(model.Reference, "modelSelection.reference");
            ValidateModelSourceMode(model);
            ValidateReasoningEffort(model.ReasoningEffort, "modelSelection.reasoningEffort");
        }
        if (configuration.ModelSelection?.CredentialReference is { } secret &&
            (string.IsNullOrWhiteSpace(secret.Id) || string.IsNullOrWhiteSpace(secret.Version)))
            throw Invalid("Model credential references must contain an exact secret ID and version.");

        var providerSeams = new HashSet<ProviderSeam>();
        foreach (var item in configuration.ProviderOverrides)
        {
            if (item is null)
                throw Invalid("Configuration contains a null provider override.");
            if (!Enum.IsDefined(item.Seam) ||
                ProviderSeams.Cardinality(item.Seam) != ProviderCardinality.Exclusive)
                throw Invalid($"Project overrides are not supported for provider seam '{item.Seam}'.");
            ValidateIdentifier(item.ProviderId, "providerOverrides.providerId");
            if (!providerSeams.Add(item.Seam))
                throw Invalid($"Provider seam '{item.Seam}' has more than one project override.");
        }

        var orderedSeams = new HashSet<ProviderSeam>();
        foreach (var item in configuration.OrderedProviderOverrides)
        {
            if (item is null)
                throw Invalid("Configuration contains a null ordered provider override.");
            if (!Enum.IsDefined(item.Seam) ||
                ProviderSeams.Cardinality(item.Seam) != ProviderCardinality.OrderedComposite ||
                item.ProviderIds.IsDefault)
                throw Invalid($"Ordered provider override for '{item.Seam}' is invalid.");
            if (!orderedSeams.Add(item.Seam) ||
                item.ProviderIds.Any(string.IsNullOrWhiteSpace) ||
                item.ProviderIds.Distinct(StringComparer.Ordinal).Count() != item.ProviderIds.Length)
                throw Invalid($"Ordered provider override for '{item.Seam}' has duplicate or invalid entries.");
            foreach (var id in item.ProviderIds)
                ValidateIdentifier(id, "orderedProviderOverrides.providerIds");
        }

        if (configuration.AgentCharters.Any(item => item is null))
            throw Invalid("Configuration contains a null agent charter.");
        EnsureUnique(configuration.AgentCharters.Select(item => item.AgentId), "agent charter");
        foreach (var item in configuration.AgentCharters)
        {
            ValidateIdentifier(item.AgentId, "agentCharters.agentId");
            ValidateText(item.Name, 160, "agentCharters.name");
            ValidateText(item.Role, 120, "agentCharters.role");
            ValidateText(item.Charter, 20000, "agentCharters.charter");
        }

        if (configuration.Casting.Any(item => item is null))
            throw Invalid("Configuration contains a null casting entry.");
        EnsureUnique(configuration.Casting.Select(item => item.AgentId), "casting entry");
        foreach (var item in configuration.Casting)
        {
            ValidateIdentifier(item.AgentId, "casting.agentId");
            ValidateIdentifier(item.Role, "casting.role");
            if (item.Order < 0)
                throw Invalid("Casting order cannot be negative.");
        }

        ValidateCastingProjection(configuration.AgentCharters, configuration.Casting);
        ValidateModelOverrides(
            configuration.ModelTaskOverrides,
            configuration.ModelRoleOverrides,
            configuration.ModelAgentOverrides,
            configuration.Casting.Select(item => item.Role),
            configuration.AgentCharters.Select(item => item.AgentId)
                .Concat(configuration.Casting.Select(item => item.AgentId)),
            "project");

        if (configuration.BlueprintWorkflowReferences.Any(item => item is null))
            throw Invalid("Configuration contains a null blueprint reference.");
        // One blueprint can contribute several workflows, so uniqueness is per (blueprint, workflow) pair.
        // IDs are slugs without newlines, so the newline-joined key can't collide.
        EnsureUnique(
            configuration.BlueprintWorkflowReferences.Select(item => $"{item.BlueprintId}\n{item.WorkflowId}"),
            "blueprint workflow");
        foreach (var item in configuration.BlueprintWorkflowReferences)
        {
            ValidateIdentifier(item.BlueprintId, "blueprintWorkflowReferences.blueprintId");
            ValidateIdentifier(item.WorkflowId, "blueprintWorkflowReferences.workflowId");
        }
        if (configuration.DefaultWorkflowId is { } defaultWorkflowId)
            ValidateIdentifier(defaultWorkflowId, "defaultWorkflowId");

        if (configuration.Skills.Any(item => item is null))
            throw Invalid("Configuration contains a null skill setting.");
        EnsureUnique(configuration.Skills.Select(item => item.SkillId), "skill");
        foreach (var item in configuration.Skills)
        {
            ValidateIdentifier(item.SkillId, "skills.skillId");
            if (item.Order < 0)
                throw Invalid("Skill order cannot be negative.");

            var hasRevision = item.Revision is not null;
            var hasDigest = item.ContentDigest is not null;
            var hasAgentIds = item.AgentIds is not null;
            if (item.Enabled && !hasRevision)
                throw Invalid("Enabled skills must pin an imported revision, content digest, and agent assignment.");
            if (hasRevision != hasDigest || hasRevision != hasAgentIds)
                throw Invalid("Skill revision, content digest, and agent assignments must be provided together.");
            if (!hasRevision)
                continue;
            if (item.Revision <= 0)
                throw Invalid("Skill revision must be positive.");
            if (item.ContentDigest is not { Length: 64 } digest ||
                digest.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
                throw Invalid("Skill content digest must be a lowercase SHA-256 digest.");
            if (item.AgentIds!.Value.IsDefault)
                throw Invalid("Skill agent assignments must be present.");
            if (item.Enabled && item.AgentIds.Value.IsEmpty)
                throw Invalid("Enabled skills must be assigned to at least one project agent.");
            EnsureUnique(item.AgentIds.Value, "skill agent");
            foreach (var agentId in item.AgentIds.Value)
                ValidateIdentifier(agentId, "skills.agentIds");
        }

        var normalizedEgress = configuration.EgressNarrowing is { } narrowing
            ? ValidateAndNormalizeRules(narrowing, "egressNarrowing")
            : (ImmutableArray<NetworkEgressRule>?)null;
        ValidateLimitOverrides(configuration.RunLimits);
        ValidateSourceControl(configuration.SourceControl);
        return configuration with { EgressNarrowing = normalizedEgress };
    }

    /// <summary>
    /// Validates a new project configuration against the previous revision, preserving only unchanged legacy unpinned skills.
    /// </summary>
    /// <exception cref="ProjectConfigException">A legacy skill is changed without a content pin or the new configuration is invalid.</exception>
    public static ProjectConfiguration ValidateTransition(
        ProjectConfiguration previous,
        ProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Skills.IsDefault)
            return Validate(configuration);

        foreach (var skill in configuration.Skills)
        {
            if (skill is not { Enabled: true } || !IsUnpinned(skill))
                continue;
            if (previous.Skills.IsDefaultOrEmpty || !previous.Skills.Contains(skill))
                throw Invalid(
                    "Enabled skills without imported pins may only be preserved unchanged from the previous configuration.");
        }

        // Treat newly introduced legacy-shaped enabled entries as disabled for validation, then preserve old bytes only when unchanged.
        var strictSkills = configuration.Skills
            .Select(skill => skill is { Enabled: true } && IsUnpinned(skill)
                ? skill with { Enabled = false }
                : skill)
            .ToImmutableArray();
        var validated = Validate(configuration with { Skills = strictSkills });
        return validated with { Skills = configuration.Skills };
    }

    private static bool IsUnpinned(SkillCatalogSetting skill) =>
        skill.Revision is null && skill.ContentDigest is null && skill.AgentIds is null;

    private static void ValidateCastingProjection(
        ImmutableArray<ProjectAgentCharter> charters,
        ImmutableArray<ProjectAgentCast> casting)
    {
        var chartersByAgent = charters.ToDictionary(item => item.AgentId, StringComparer.Ordinal);
        var castingByAgent = casting.ToDictionary(item => item.AgentId, StringComparer.Ordinal);
        if (chartersByAgent.Count != castingByAgent.Count)
            throw Invalid("Every cast agent must have exactly one matching agent charter, and every charter must be cast.");

        foreach (var cast in casting)
        {
            if (IsReservedOrchestrationRole(cast.AgentId) || IsReservedOrchestrationRole(cast.Role))
                throw Invalid($"Casting agent '{cast.AgentId}' uses reserved orchestration role '{cast.Role}'.");
            if (!chartersByAgent.TryGetValue(cast.AgentId, out var charter) ||
                !string.Equals(charter.Role, cast.Role, StringComparison.Ordinal))
                throw Invalid(
                    $"Casting agent '{cast.AgentId}' role '{cast.Role}' must exactly match its agent charter role.");
        }
    }

    internal static bool IsReservedOrchestrationRole(string value) =>
        value.Equals("scribe", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("work-monitor", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("coordinator", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("rai", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSourceControl(SourceControlProjectSettings? sourceControl)
    {
        if (sourceControl is null)
            return;
        var usesAppConnection = sourceControl.AuthMode == SourceControlAuthMode.GitHubApp;
        var usesSecret = sourceControl.AuthMode is null or SourceControlAuthMode.Secret;
        if (sourceControl.Repository is null ||
            (!usesAppConnection && !usesSecret) ||
            usesSecret &&
                (!IsExactSecretReference(sourceControl.ApiSecretReference) ||
                 sourceControl.IdentityConnectionId is not null) ||
            usesAppConnection &&
                (sourceControl.ApiSecretReference is not null ||
                 sourceControl.CheckoutSecretReference is not null ||
                 !IsExactIdentityConnectionId(sourceControl.IdentityConnectionId)) ||
            sourceControl.CheckoutSecretReference is { } checkout &&
                !IsExactSecretReference(checkout) ||
            sourceControl.WebhookSecretReference is { } webhook &&
                !IsExactSecretReference(webhook))
            throw Invalid(
                "SourceControl settings require one repository and either exact secret authentication or a GitHub App connection.");
    }

    private static bool IsExactSecretReference(SecretRef? secret) =>
        secret is not null &&
        !string.IsNullOrWhiteSpace(secret.Id) &&
        !string.IsNullOrWhiteSpace(secret.Version);

    private static void ValidateModelOverrides(
        ImmutableArray<ModelTaskSelectionOverride> taskOverrides,
        ImmutableArray<ModelRoleSelectionOverride> roleOverrides,
        ImmutableArray<ModelAgentSelectionOverride> agentOverrides,
        IEnumerable<string> configuredRoles,
        IEnumerable<string> configuredAgents,
        string scope)
    {
        if (taskOverrides.IsDefault || roleOverrides.IsDefault || agentOverrides.IsDefault ||
            taskOverrides.Any(item => item is null) ||
            roleOverrides.Any(item => item is null) ||
            agentOverrides.Any(item => item is null))
            throw Invalid($"The {scope} model override collections must be present and non-null.");

        if (taskOverrides.Any(item => !Enum.IsDefined(item.TaskKind)) ||
            taskOverrides.Select(item => item.TaskKind).Distinct().Count() != taskOverrides.Length)
            throw Invalid($"The {scope} model task overrides contain an unknown or duplicate task kind.");

        EnsureUnique(roleOverrides.Select(item => item.RoleId), $"{scope} model role override");
        EnsureUnique(agentOverrides.Select(item => item.AgentId), $"{scope} model agent override");

        var roles = configuredRoles.ToHashSet(StringComparer.Ordinal);
        var agents = configuredAgents.ToHashSet(StringComparer.Ordinal);
        foreach (var item in roleOverrides)
        {
            ValidateIdentifier(item.RoleId, $"{scope}.modelRoleOverrides.roleId");
            if (roles.Count > 0 && !roles.Contains(item.RoleId))
                throw Invalid($"The {scope} model role override names an unconfigured role.");
            ValidateModelSelectionOverride(item.Selection, $"{scope}.modelRoleOverrides.selection");
        }
        foreach (var item in agentOverrides)
        {
            ValidateIdentifier(item.AgentId, $"{scope}.modelAgentOverrides.agentId");
            if (agents.Count > 0 && !agents.Contains(item.AgentId))
                throw Invalid($"The {scope} model agent override names an unconfigured agent.");
            ValidateModelSelectionOverride(item.Selection, $"{scope}.modelAgentOverrides.selection");
        }
        foreach (var item in taskOverrides)
            ValidateModelSelectionOverride(item.Selection, $"{scope}.modelTaskOverrides.selection");
    }

    private static void ValidateModelSelectionOverride(ModelSelectionOverride selection, string path)
    {
        if (selection is null ||
            selection.Reference is null && selection.ReasoningEffort is null && selection.ModelBindingPin is null)
            throw Invalid($"'{path}' must change a model reference or reasoning effort.");
        if (selection.Reference is { } reference)
            ValidateIdentifier(reference, path + ".reference");
        ValidateReasoningEffort(selection.ReasoningEffort, path + ".reasoningEffort");
        if (selection.ModelBindingPin is { } pin)
        {
            if (pin.ContractVersion != 1 || !Enum.IsDefined(pin.SourceMode) ||
                pin.SourceMode == ModelSourceMode.HostedCopilot && pin.ProviderType is not null ||
                pin.SourceMode == ModelSourceMode.Byok &&
                    pin.ProviderType is not ("azure" or "openai" or "anthropic") ||
                pin.ConfigurationHash is not { Length: 64 } ||
                pin.ConfigurationHash.Any(character => character is not
                    (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw Invalid($"'{path}.modelBindingPin' is invalid.");
            ValidateIdentifier(pin.ModelSelectionReference, path + ".modelBindingPin.modelSelectionReference");
            ValidateIdentifier(pin.ModelId, path + ".modelBindingPin.modelId");
            ValidateIdentifier(pin.ConfigurationRevision, path + ".modelBindingPin.configurationRevision");
            ValidateReasoningEffort(pin.ReasoningEffort, path + ".modelBindingPin.reasoningEffort");
            if (selection.Reference != pin.ModelSelectionReference ||
                selection.ReasoningEffort is not null && selection.ReasoningEffort != pin.ReasoningEffort)
                throw Invalid($"'{path}.modelBindingPin' must match the selected model reference.");
        }
    }

    private static void ValidateReasoningEffort(string? effort, string path)
    {
        if (effort is not null && effort is not ("low" or "medium" or "high" or "xhigh"))
            throw Invalid($"'{path}' must be low, medium, high, or xhigh.");
    }

    private static void ValidateModelSourceMode(ModelSelectionSettings model)
    {
        if (model.ModelBindingPin is { } pin &&
            (model.SourceMode is null || pin.ContractVersion != 1 ||
             pin.SourceMode != model.SourceMode || pin.ModelSelectionReference != model.Reference ||
             model.SourceMode == ModelSourceMode.Byok &&
                pin.ProviderType is not ("azure" or "openai" or "anthropic") ||
             model.SourceMode == ModelSourceMode.HostedCopilot && pin.ProviderType is not null ||
             string.IsNullOrWhiteSpace(pin.ModelId) || string.IsNullOrWhiteSpace(pin.ConfigurationRevision) ||
             pin.ConfigurationHash is not { Length: 64 } ||
             model.ReasoningEffort is not null && model.ReasoningEffort != pin.ReasoningEffort ||
             pin.ConfigurationHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw Invalid("The model binding pin must match the selected source and reference.");
        if (model.ModelBindingPin is { } validatedPin)
        {
            ValidateIdentifier(validatedPin.ModelId, "modelSelection.modelBindingPin.modelId");
            ValidateIdentifier(validatedPin.ConfigurationRevision, "modelSelection.modelBindingPin.configurationRevision");
            ValidateReasoningEffort(validatedPin.ReasoningEffort, "modelSelection.modelBindingPin.reasoningEffort");
        }
        if (model.SourceMode is { } mode && !Enum.IsDefined(mode))
            throw Invalid("Model source mode must be hostedCopilot or byok.");
        if (model.ConnectionId == Guid.Empty ||
            model.ConnectionId is not null && model.SourceMode != ModelSourceMode.HostedCopilot ||
            model.SourceMode == ModelSourceMode.HostedCopilot &&
                (model.ConnectionId is null || model.CredentialReference is not null) ||
            model.SourceMode == ModelSourceMode.Byok && model.CredentialReference is null)
            throw Invalid("Hosted Copilot requires one connection ID; BYOK requires one exact credential reference.");
    }

    private static bool IsExactIdentityConnectionId(string? connectionId) =>
        !string.IsNullOrWhiteSpace(connectionId) &&
        connectionId.Length <= 128 &&
        connectionId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>Validates platform defaults and returns normalized egress rules.</summary>
    /// <exception cref="ProjectConfigException">A default model, egress rule, or runtime limit is invalid.</exception>
    public static PlatformRuntimeDefaults Validate(PlatformRuntimeDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (defaults.ModelTaskDefaults.IsDefault || defaults.ModelRoleDefaults.IsDefault ||
            defaults.ModelAgentDefaults.IsDefault || defaults.EgressBaseline.IsDefault || defaults.RunLimits is null)
            throw Invalid("Platform egress baseline and run limits must be present.");
        if (defaults.ModelSelection is { } model)
        {
            ValidateIdentifier(model.Reference, "modelSelection.reference");
            ValidateModelSourceMode(model);
            ValidateReasoningEffort(model.ReasoningEffort, "modelSelection.reasoningEffort");
        }
        if (defaults.ModelSelection?.CredentialReference is { } secret &&
            (string.IsNullOrWhiteSpace(secret.Id) || string.IsNullOrWhiteSpace(secret.Version)))
            throw Invalid("Model credential references must contain an exact secret ID and version.");

        ValidateModelOverrides(
            defaults.ModelTaskDefaults,
            defaults.ModelRoleDefaults,
            defaults.ModelAgentDefaults,
            [],
            [],
            "platform");
        var normalizedEgress = ValidateAndNormalizeRules(defaults.EgressBaseline, "egressBaseline");
        ValidateLimits(defaults.RunLimits);
        return defaults with { EgressBaseline = normalizedEgress };
    }

    /// <summary>
    /// Builds the effective run limits. A limit the project leaves out takes the platform value. A limit the
    /// project sets must not be above the platform value. Spend budgets follow the same rule per budget ID.
    /// </summary>
    /// <exception cref="ProjectConfigException">An override is outside its supported range or widens a platform limit.</exception>
    public static CopilotRunLimits ResolveLimits(
        PlatformRuntimeDefaults platform,
        CopilotRunLimitOverrides project)
    {
        // Check both inputs on their own first, so errors name the layer that is wrong.
        ValidateLimits(platform.RunLimits);
        ValidateLimitOverrides(project);

        // Each Resolve* call returns the platform value when the project value is missing,
        // and throws when the project value is above the platform value.
        var resolved = new CopilotRunLimits
        {
            MaxModelTurns = ResolveLimit(project.MaxModelTurns, platform.RunLimits.MaxModelTurns, nameof(project.MaxModelTurns)),
            MaxToolCalls = ResolveLimit(project.MaxToolCalls, platform.RunLimits.MaxToolCalls, nameof(project.MaxToolCalls)),
            MaxChildren = ResolveLimit(project.MaxChildren, platform.RunLimits.MaxChildren, nameof(project.MaxChildren), allowZero: true),
            MaxConcurrentChildren = ResolveLimit(project.MaxConcurrentChildren, platform.RunLimits.MaxConcurrentChildren, nameof(project.MaxConcurrentChildren)),
            MaxWallTimeSeconds = ResolveLimit(project.MaxWallTimeSeconds, platform.RunLimits.MaxWallTimeSeconds, nameof(project.MaxWallTimeSeconds)),
            MaxPromptTokens = ResolveOptionalPromptTokenLimit(
                project.MaxPromptTokens, platform.RunLimits.MaxPromptTokens, nameof(project.MaxPromptTokens)),
            MaxRevisionAttempts = ResolveOptionalLimit(
                project.MaxRevisionAttempts, platform.RunLimits.MaxRevisionAttempts, nameof(project.MaxRevisionAttempts)),
            CopilotSoftCreditLimit = ResolveOptionalCreditLimit(
                project.CopilotSoftCreditLimit, platform.RunLimits.CopilotSoftCreditLimit,
                nameof(project.CopilotSoftCreditLimit)),
            CopilotHardCreditLimit = ResolveOptionalCreditLimit(
                project.CopilotHardCreditLimit, platform.RunLimits.CopilotHardCreditLimit,
                nameof(project.CopilotHardCreditLimit)),
            SpendBudgets = ResolveSpendBudgets(platform.RunLimits.SpendBudgets, project.SpendBudgets),
        };
        // Check the merged result too, because a merge of two valid layers can still break a cross-field rule.
        ValidateLimits(resolved);
        return resolved;
    }

    /// <summary>Returns the effective allow list after checking project narrowing and all required run destinations.</summary>
    /// <exception cref="ProjectConfigException">Project egress widens the baseline or required egress is not allowed.</exception>
    public static ImmutableArray<NetworkEgressRule> ResolveEgress(
        ImmutableArray<NetworkEgressRule> baseline,
        ImmutableArray<NetworkEgressRule>? projectNarrowing,
        ImmutableArray<NetworkEgressRule> required)
    {
        var allowed = ValidateAndNormalizeRules(baseline, "egressBaseline");
        var requested = projectNarrowing is { } narrowing
            ? ValidateAndNormalizeRules(narrowing, "egressNarrowing")
            : allowed;
        var runNeeds = ValidateAndNormalizeRules(required, "requiredEgress");
        if (requested.Any(rule => !NetworkEgressRuleSemantics.IsContainedBy(rule, allowed)))
            throw Invalid("Project egress settings cannot widen the platform baseline.");

        if (runNeeds.Any(rule => !NetworkEgressRuleSemantics.IsContainedBy(rule, requested)))
            throw Invalid("Required run egress is outside the effective platform and project allowlist.");
        return requested;
    }

    /// <summary>Validates and canonicalizes a set of egress rules without applying a platform subset check.</summary>
    public static ImmutableArray<NetworkEgressRule> ValidateEgressRules(
        ImmutableArray<NetworkEgressRule> rules) =>
        ValidateAndNormalizeRules(rules, "egress");

    private static ImmutableArray<NetworkEgressRule> ValidateAndNormalizeRules(
        ImmutableArray<NetworkEgressRule> rules,
        string location)
    {
        try
        {
            return NetworkEgressRuleSemantics.NormalizeSet(rules, location);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(exception.Message);
        }
    }

    private static int ResolveLimit(int? value, int platformValue, string name, bool allowZero = false)
    {
        if (value is null) return platformValue;
        ValidateRange(value.Value, allowZero ? 0 : 1, int.MaxValue, name);
        if (value.Value > platformValue)
            throw Invalid($"Project run limit '{name}' cannot exceed the platform limit.");
        return value.Value;
    }

    private static int? ResolveOptionalLimit(int? value, int? platformValue, string name)
    {
        if (value is null)
            return platformValue;
        ValidateOptionalRange(value, 0, int.MaxValue, name);
        if (platformValue is { } platformLimit && value > platformLimit)
            throw Invalid($"Project run limit '{name}' cannot exceed the platform limit.");
        return value;
    }

    private static int? ResolveOptionalPromptTokenLimit(int? value, int? platformValue, string name)
    {
        if (value is null)
            return platformValue;
        ValidateOptionalRange(value, 1024, 200000, name);
        if (platformValue is { } platformLimit && value > platformLimit)
            throw Invalid($"Project run limit '{name}' cannot exceed the platform limit.");
        return value;
    }

    private static decimal? ResolveOptionalCreditLimit(decimal? value, decimal? platformValue, string name)
    {
        if (value is null)
            return platformValue;
        ValidateCreditLimit(value, name);
        if (platformValue is { } platformLimit && value > platformLimit)
            throw Invalid($"Project run limit '{name}' cannot exceed the platform limit.");
        return value;
    }

    // Checks a complete set of limits, such as platform defaults or a resolved result. Every value is required.
    private static void ValidateLimits(CopilotRunLimits limits)
    {
        ValidateRange(limits.MaxModelTurns, 1, 1000, "runLimits.maxModelTurns");
        ValidateRange(limits.MaxToolCalls, 1, 10000, "runLimits.maxToolCalls");
        ValidateRange(limits.MaxChildren, 0, 100, "runLimits.maxChildren");
        ValidateRange(limits.MaxConcurrentChildren, 1, 32, "runLimits.maxConcurrentChildren");
        ValidateRange(limits.MaxWallTimeSeconds, 60, 86400, "runLimits.maxWallTimeSeconds");
        ValidateOptionalRange(limits.MaxPromptTokens, 1024, 200000, "runLimits.maxPromptTokens");
        ValidateOptionalRange(limits.MaxRevisionAttempts, 0, int.MaxValue, "runLimits.maxRevisionAttempts");
        ValidateCreditLimit(limits.CopilotSoftCreditLimit, "runLimits.copilotSoftCreditLimit");
        ValidateCreditLimit(limits.CopilotHardCreditLimit, "runLimits.copilotHardCreditLimit");
        ValidateCreditLimitOrder(limits.CopilotSoftCreditLimit, limits.CopilotHardCreditLimit);
        ValidateSpendBudgets(limits.SpendBudgets);
    }

    // Checks project overrides. Every value is optional, but any value that is present must be in range.
    private static void ValidateLimitOverrides(CopilotRunLimitOverrides limits)
    {
        ValidateOptionalRange(limits.MaxModelTurns, 1, 1000, "runLimits.maxModelTurns");
        ValidateOptionalRange(limits.MaxToolCalls, 1, 10000, "runLimits.maxToolCalls");
        ValidateOptionalRange(limits.MaxChildren, 0, 100, "runLimits.maxChildren");
        ValidateOptionalRange(limits.MaxConcurrentChildren, 1, 32, "runLimits.maxConcurrentChildren");
        ValidateOptionalRange(limits.MaxWallTimeSeconds, 60, 86400, "runLimits.maxWallTimeSeconds");
        ValidateOptionalRange(limits.MaxPromptTokens, 1024, 200000, "runLimits.maxPromptTokens");
        ValidateOptionalRange(limits.MaxRevisionAttempts, 0, int.MaxValue, "runLimits.maxRevisionAttempts");
        ValidateCreditLimit(limits.CopilotSoftCreditLimit, "runLimits.copilotSoftCreditLimit");
        ValidateCreditLimit(limits.CopilotHardCreditLimit, "runLimits.copilotHardCreditLimit");
        ValidateCreditLimitOrder(limits.CopilotSoftCreditLimit, limits.CopilotHardCreditLimit);
        ValidateSpendBudgets(limits.SpendBudgets);
    }

    // Merges platform and project spend budgets by budget ID:
    // - a platform budget the project doesn't mention stays as is;
    // - a project budget with the same ID as a platform budget replaces it, but only when it is equal or stricter;
    // - a project budget with a new ID is added.
    // A project can't remove a platform budget, so the platform limits always apply.
    private static ImmutableArray<SpendBudget>? ResolveSpendBudgets(
        ImmutableArray<SpendBudget>? platform,
        ImmutableArray<SpendBudget>? project)
    {
        // When the project sets no budgets, the platform budgets apply unchanged.
        if (project is not { } projectBudgets)
            return platform;
        var platformBudgets = platform ?? [];
        var resolved = ImmutableArray.CreateBuilder<SpendBudget>();
        foreach (var ceiling in platformBudgets)
        {
            var narrowed = projectBudgets.FirstOrDefault(budget => budget.BudgetId == ceiling.BudgetId);
            if (narrowed is null)
            {
                resolved.Add(ceiling);
                continue;
            }

            // Convert the contract error into a configuration validation error for the API response.
            try
            {
                SpendBudgetContract.RequireNarrowing(ceiling, narrowed);
            }
            catch (ArgumentException exception)
            {
                throw Invalid(exception.Message);
            }
            resolved.Add(narrowed);
        }

        // Add project budgets whose IDs don't match any platform budget.
        resolved.AddRange(projectBudgets.Where(budget =>
            !platformBudgets.Any(ceiling => ceiling.BudgetId == budget.BudgetId)));
        var budgets = resolved.ToImmutable();
        // Check the merged list, because a new project budget can repeat the bucket of a platform budget.
        ValidateSpendBudgets(budgets);
        return budgets;
    }

    // Checks an optional budget list. A missing list is valid and means "no budgets at this layer".
    private static void ValidateSpendBudgets(ImmutableArray<SpendBudget>? budgets)
    {
        if (budgets is not { } values)
            return;
        // Convert the contract error into a configuration validation error that names the field.
        try
        {
            SpendBudgetContract.ValidateSet(values);
        }
        catch (ArgumentException exception)
        {
            throw Invalid($"Invalid 'runLimits.spendBudgets': {exception.Message}");
        }
    }

    private static void ValidateCreditLimit(decimal? value, string name)
    {
        if (value is < 0)
            throw Invalid($"Run limit '{name}' must be greater than or equal to zero.");
    }

    private static void ValidateCreditLimitOrder(decimal? soft, decimal? hard)
    {
        if (soft is { } softLimit && hard is { } hardLimit && softLimit > hardLimit)
            throw Invalid("The Copilot soft credit limit cannot exceed the hard credit limit.");
    }

    private static void ValidateOptionalRange(int? value, int minimum, int maximum, string name)
    {
        if (value is { } actual)
            ValidateRange(actual, minimum, maximum, name);
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw Invalid($"'{name}' must be between {minimum} and {maximum}.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string kind)
    {
        var ids = values.ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw Invalid($"Configuration contains duplicate or invalid {kind} IDs.");
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw Invalid($"'{name}' must be a nonempty opaque identifier of at most 256 ASCII characters.");
    }

    private static void ValidateText(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw Invalid($"'{name}' must be nonempty and at most {maximumLength} characters.");
    }

    private static ProjectConfigException Invalid(string message) =>
        new("invalid_configuration", message, StatusCodes.Status400BadRequest);
}
