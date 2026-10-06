using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelSelectionSettings(string Reference, SecretRef? CredentialReference = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectProviderOverride(ProviderSeam Seam, string ProviderId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectOrderedProviderOverride(
    ProviderSeam Seam,
    ImmutableArray<string> ProviderIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectAgentCharter(string AgentId, string Name, string Role, string Charter);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectAgentCast(string AgentId, string Role, int Order);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlueprintWorkflowReference(string BlueprintId, string WorkflowId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillCatalogSetting(string SkillId, bool Enabled, int Order);

public enum EgressProtocol
{
    Tcp,
    Udp
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectEgressRule(string Host, int Port, EgressProtocol Protocol);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopilotRunLimitOverrides
{
    public int? MaxModelTurns { get; init; }
    public int? MaxToolCalls { get; init; }
    public int? MaxChildren { get; init; }
    public int? MaxConcurrentChildren { get; init; }
    public int? MaxWallTimeSeconds { get; init; }
    public int? MaxPromptTokens { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CopilotRunLimits
{
    public int MaxModelTurns { get; init; }
    public int MaxToolCalls { get; init; }
    public int MaxChildren { get; init; }
    public int MaxConcurrentChildren { get; init; }
    public int MaxWallTimeSeconds { get; init; }
    public int MaxPromptTokens { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectConfiguration
{
    public ModelSelectionSettings? ModelSelection { get; init; }
    public ImmutableArray<ProjectProviderOverride> ProviderOverrides { get; init; } = [];
    public ImmutableArray<ProjectOrderedProviderOverride> OrderedProviderOverrides { get; init; } = [];
    public ImmutableArray<ProjectAgentCharter> AgentCharters { get; init; } = [];
    public ImmutableArray<ProjectAgentCast> Casting { get; init; } = [];
    public ImmutableArray<BlueprintWorkflowReference> BlueprintWorkflowReferences { get; init; } = [];
    public ImmutableArray<SkillCatalogSetting> Skills { get; init; } = [];
    public ImmutableArray<ProjectEgressRule>? EgressNarrowing { get; init; }
    public CopilotRunLimitOverrides RunLimits { get; init; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformRuntimeDefaults
{
    public ModelSelectionSettings? ModelSelection { get; init; }
    public ImmutableArray<ProjectEgressRule> EgressBaseline { get; init; } = [];
    public CopilotRunLimits RunLimits { get; init; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderRequirement
{
    public ProviderSeam Seam { get; init; }
    public string RequiredAdapterVersion { get; init; } = string.Empty;
    public int RequiredOptionsSchemaVersion { get; init; }
    public ImmutableHashSet<string> RequiredCapabilities { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public ImmutableHashSet<string> RequiredL3L4Capabilities { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public ImmutableHashSet<string> RequiredL7Capabilities { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunSelectionContext
{
    public string Revision { get; init; } = string.Empty;
    public ImmutableHashSet<string> AvailableModelSelectionReferences { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    public ImmutableArray<ProviderRequirement> ProviderRequirements { get; init; } = [];
    public ImmutableArray<ProjectEgressRule> RequiredEgress { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcceptRunSelectionRequest
{
    public required long ExpectedProjectConfigRevision { get; init; }
    public required long ExpectedPlatformRuntimeRevision { get; init; }
    public required RunSelectionContext Context { get; init; }
}

public static class ProjectConfigurationValidator
{
    public static ProjectConfiguration Validate(ProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.ProviderOverrides.IsDefault ||
            configuration.OrderedProviderOverrides.IsDefault ||
            configuration.AgentCharters.IsDefault ||
            configuration.Casting.IsDefault ||
            configuration.BlueprintWorkflowReferences.IsDefault ||
            configuration.Skills.IsDefault ||
            configuration.EgressNarrowing is { IsDefault: true } ||
            configuration.RunLimits is null)
            throw Invalid("Configuration collections and run-limit settings must be present.");

        if (configuration.ModelSelection is { } model)
            ValidateIdentifier(model.Reference, "modelSelection.reference");
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
            ValidateText(item.Role, 120, "casting.role");
            if (item.Order < 0)
                throw Invalid("Casting order cannot be negative.");
        }

        if (configuration.BlueprintWorkflowReferences.Any(item => item is null))
            throw Invalid("Configuration contains a null blueprint reference.");
        EnsureUnique(configuration.BlueprintWorkflowReferences.Select(item => item.BlueprintId), "blueprint");
        foreach (var item in configuration.BlueprintWorkflowReferences)
        {
            ValidateIdentifier(item.BlueprintId, "blueprintWorkflowReferences.blueprintId");
            ValidateIdentifier(item.WorkflowId, "blueprintWorkflowReferences.workflowId");
        }

        if (configuration.Skills.Any(item => item is null))
            throw Invalid("Configuration contains a null skill setting.");
        EnsureUnique(configuration.Skills.Select(item => item.SkillId), "skill");
        foreach (var item in configuration.Skills)
        {
            ValidateIdentifier(item.SkillId, "skills.skillId");
            if (item.Order < 0)
                throw Invalid("Skill order cannot be negative.");
        }

        var normalizedEgress = configuration.EgressNarrowing is { } narrowing
            ? ValidateAndNormalizeRules(narrowing, "egressNarrowing")
            : (ImmutableArray<ProjectEgressRule>?)null;
        ValidateLimitOverrides(configuration.RunLimits);
        return configuration with { EgressNarrowing = normalizedEgress };
    }

    public static PlatformRuntimeDefaults Validate(PlatformRuntimeDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (defaults.EgressBaseline.IsDefault || defaults.RunLimits is null)
            throw Invalid("Platform egress baseline and run limits must be present.");
        if (defaults.ModelSelection is { } model)
            ValidateIdentifier(model.Reference, "modelSelection.reference");
        if (defaults.ModelSelection?.CredentialReference is { } secret &&
            (string.IsNullOrWhiteSpace(secret.Id) || string.IsNullOrWhiteSpace(secret.Version)))
            throw Invalid("Model credential references must contain an exact secret ID and version.");

        var normalizedEgress = ValidateAndNormalizeRules(defaults.EgressBaseline, "egressBaseline");
        ValidateLimits(defaults.RunLimits);
        return defaults with { EgressBaseline = normalizedEgress };
    }

    public static CopilotRunLimits ResolveLimits(
        PlatformRuntimeDefaults platform,
        CopilotRunLimitOverrides project)
    {
        ValidateLimits(platform.RunLimits);
        ValidateLimitOverrides(project);

        var resolved = new CopilotRunLimits
        {
            MaxModelTurns = ResolveLimit(project.MaxModelTurns, platform.RunLimits.MaxModelTurns, nameof(project.MaxModelTurns)),
            MaxToolCalls = ResolveLimit(project.MaxToolCalls, platform.RunLimits.MaxToolCalls, nameof(project.MaxToolCalls)),
            MaxChildren = ResolveLimit(project.MaxChildren, platform.RunLimits.MaxChildren, nameof(project.MaxChildren), allowZero: true),
            MaxConcurrentChildren = ResolveLimit(project.MaxConcurrentChildren, platform.RunLimits.MaxConcurrentChildren, nameof(project.MaxConcurrentChildren)),
            MaxWallTimeSeconds = ResolveLimit(project.MaxWallTimeSeconds, platform.RunLimits.MaxWallTimeSeconds, nameof(project.MaxWallTimeSeconds)),
            MaxPromptTokens = ResolveLimit(project.MaxPromptTokens, platform.RunLimits.MaxPromptTokens, nameof(project.MaxPromptTokens)),
        };
        ValidateLimits(resolved);
        return resolved;
    }

    public static ImmutableArray<ProjectEgressRule> ResolveEgress(
        ImmutableArray<ProjectEgressRule> baseline,
        ImmutableArray<ProjectEgressRule>? projectNarrowing,
        ImmutableArray<ProjectEgressRule> required)
    {
        var allowed = ValidateAndNormalizeRules(baseline, "egressBaseline");
        var requested = projectNarrowing is { } narrowing
            ? ValidateAndNormalizeRules(narrowing, "egressNarrowing")
            : allowed;
        var runNeeds = ValidateAndNormalizeRules(required, "requiredEgress");
        var baselineSet = allowed.ToHashSet();
        if (requested.Any(rule => !baselineSet.Contains(rule)))
            throw Invalid("Project egress settings cannot widen the platform baseline.");

        var effectiveSet = requested.ToHashSet();
        if (runNeeds.Any(rule => !effectiveSet.Contains(rule)))
            throw Invalid("Required run egress is outside the effective platform and project allowlist.");
        return requested;
    }

    public static ImmutableArray<ProjectEgressRule> ValidateEgressRules(
        ImmutableArray<ProjectEgressRule> rules) =>
        ValidateAndNormalizeRules(rules, "egress");

    private static ImmutableArray<ProjectEgressRule> ValidateAndNormalizeRules(
        ImmutableArray<ProjectEgressRule> rules,
        string location)
    {
        var result = ImmutableArray.CreateBuilder<ProjectEgressRule>();
        foreach (var rule in rules)
        {
            if (rule is null || !Enum.IsDefined(rule.Protocol) || rule.Port is < 1 or > 65535)
                throw Invalid($"{location} contains an invalid destination.");
            var host = NormalizeHost(rule.Host);
            result.Add(rule with { Host = host });
        }

        var normalized = result.ToImmutable();
        if (normalized.Distinct().Count() != normalized.Length)
            throw Invalid($"{location} contains duplicate destinations.");
        return normalized;
    }

    private static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('*') || host.Contains('/'))
            throw Invalid("Egress destinations must use an exact DNS host name.");
        try
        {
            var normalized = new IdnMapping().GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant();
            if (normalized.Length > 253 || !normalized.Contains('.', StringComparison.Ordinal) ||
                Uri.CheckHostName(normalized) != UriHostNameType.Dns ||
                IPAddress.TryParse(normalized, out _))
                throw Invalid("Egress destinations must use an exact DNS host name.");
            return normalized;
        }
        catch (ArgumentException)
        {
            throw Invalid("Egress destination host name is invalid.");
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

    private static void ValidateLimits(CopilotRunLimits limits)
    {
        ValidateRange(limits.MaxModelTurns, 1, 1000, "runLimits.maxModelTurns");
        ValidateRange(limits.MaxToolCalls, 1, 10000, "runLimits.maxToolCalls");
        ValidateRange(limits.MaxChildren, 0, 100, "runLimits.maxChildren");
        ValidateRange(limits.MaxConcurrentChildren, 1, 32, "runLimits.maxConcurrentChildren");
        ValidateRange(limits.MaxWallTimeSeconds, 60, 86400, "runLimits.maxWallTimeSeconds");
        ValidateRange(limits.MaxPromptTokens, 1024, 200000, "runLimits.maxPromptTokens");
    }

    private static void ValidateLimitOverrides(CopilotRunLimitOverrides limits)
    {
        ValidateOptionalRange(limits.MaxModelTurns, 1, 1000, "runLimits.maxModelTurns");
        ValidateOptionalRange(limits.MaxToolCalls, 1, 10000, "runLimits.maxToolCalls");
        ValidateOptionalRange(limits.MaxChildren, 0, 100, "runLimits.maxChildren");
        ValidateOptionalRange(limits.MaxConcurrentChildren, 1, 32, "runLimits.maxConcurrentChildren");
        ValidateOptionalRange(limits.MaxWallTimeSeconds, 60, 86400, "runLimits.maxWallTimeSeconds");
        ValidateOptionalRange(limits.MaxPromptTokens, 1024, 200000, "runLimits.maxPromptTokens");
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
