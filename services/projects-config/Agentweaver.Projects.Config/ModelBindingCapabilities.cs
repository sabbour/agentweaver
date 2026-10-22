using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Projects.Config;

/// <summary>
/// One model reference from the read-only model-binding copy: whether it is enabled, how it is sourced, and the
/// reasoning levels configured for bring-your-own-key bindings. It never holds model IDs, provider endpoints,
/// headers, or credential references.
/// </summary>
public sealed record ModelBindingCapability(
    string Reference,
    bool Enabled,
    string SourceMode,
    ImmutableArray<string> SupportedReasoningEfforts);

/// <summary>
/// Startup snapshot of the eligibility fields of the AgentHost model bindings, supplied to Projects &amp; Config
/// through its deployment profile. AgentHost stays the owner of the full bindings and checks them again when a run
/// starts; this copy only lets the owner list which references are enabled and which reasoning levels they accept.
/// </summary>
public sealed partial class ModelBindingCapabilities
{
    /// <summary>Configuration path of the binding map; it matches the AgentHost and Events &amp; Sessions path.</summary>
    public const string ConfigurationSection = "AgentHost:ModelBindings";

    /// <summary>Configuration path of the binding revision shared with AgentHost.</summary>
    public const string RevisionConfigurationKey = "AgentHost:ModelBindingsRevision";

    private static readonly string[] ReasoningEffortOrder = ["low", "medium", "high", "xhigh"];

    private ModelBindingCapabilities(string? revision, ImmutableArray<ModelBindingCapability> bindings)
    {
        Revision = revision;
        Bindings = bindings;
    }

    /// <summary>The snapshot used when the deployment supplies no model-binding copy.</summary>
    public static ModelBindingCapabilities Unavailable { get; } = new(null, []);

    /// <summary>Binding revision, or <see langword="null"/> when no copy was configured.</summary>
    public string? Revision { get; }

    /// <summary>All configured bindings, enabled or not, ordered by reference.</summary>
    public ImmutableArray<ModelBindingCapability> Bindings { get; }

    /// <summary>True when the deployment supplied a validated model-binding copy.</summary>
    public bool IsAvailable => Revision is not null;

    /// <summary>
    /// Reads and validates the copy. Returns <see cref="Unavailable"/> only when both the revision and the binding
    /// map are absent; any partial or malformed copy throws so the service fails at startup instead of guessing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model-binding copy is incomplete or malformed.</exception>
    public static ModelBindingCapabilities FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var revision = configuration[RevisionConfigurationKey];
        var section = configuration.GetSection(ConfigurationSection);
        var entries = section.GetChildren().ToArray();
        if (revision is null && entries.Length == 0 && section.Value is null)
            return Unavailable;
        if (revision is null || !IdentifierPattern().IsMatch(revision))
            throw Invalid($"'{RevisionConfigurationKey}' must be a valid revision when model bindings are configured.");
        if (section.Value is not null || entries.Length == 0)
            throw Invalid($"'{ConfigurationSection}' must contain at least one model binding.");

        var bindings = ImmutableArray.CreateBuilder<ModelBindingCapability>(entries.Length);
        foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            bindings.Add(ReadBinding(entry));
        return new ModelBindingCapabilities(revision, bindings.MoveToImmutable());
    }

    private static ModelBindingCapability ReadBinding(IConfigurationSection entry)
    {
        var reference = entry.Key;
        if (!IdentifierPattern().IsMatch(reference) || entry.Value is not null)
            throw Invalid($"Model binding '{reference}' must be a valid reference with named fields.");
        // Only eligibility fields are allowed. Model IDs, provider URLs, headers, and credential references stay in
        // the AgentHost configuration, so any other field means the profile copied too much.
        foreach (var field in entry.GetChildren())
        {
            if (field.Key is not ("SourceMode" or "Enabled" or "SupportedReasoningEfforts"))
                throw Invalid($"Model binding '{reference}' has unsupported field '{field.Key}'.");
        }

        var sourceMode = entry["SourceMode"];
        if (sourceMode is not ("HostedCopilot" or "Byok"))
            throw Invalid($"Model binding '{reference}' requires SourceMode 'HostedCopilot' or 'Byok'.");
        var enabled = entry["Enabled"] switch
        {
            "true" => true,
            "false" => false,
            _ => throw Invalid($"Model binding '{reference}' requires Enabled 'true' or 'false'."),
        };

        var effortsSection = entry.GetSection("SupportedReasoningEfforts");
        if (effortsSection.Value is not null)
            throw Invalid($"Model binding '{reference}' must list reasoning efforts as indexed entries.");
        var effortEntries = effortsSection.GetChildren().ToArray();
        // Indexed configuration keys must be exactly 0..n-1 so a missing index cannot hide a configured value.
        var indexes = effortEntries.Select(item => int.TryParse(item.Key, out var index) &&
                item.Key == index.ToString(System.Globalization.CultureInfo.InvariantCulture) ? index : -1)
            .Order().ToArray();
        if (!indexes.SequenceEqual(Enumerable.Range(0, effortEntries.Length)))
            throw Invalid($"Model binding '{reference}' reasoning efforts must use contiguous indexes from 0.");
        var efforts = effortEntries.Select(item => item.Value).ToArray();
        // These rules match AgentHost's RuntimeModelBinding validation, so any binding AgentHost accepts can be
        // copied here. AgentHost ignores binding efforts for hosted models and verifies them against the SDK model
        // list at run start; BuildModelChoices therefore never reports them.
        if (efforts.Any(effort => effort is null || !ReasoningEffortOrder.Contains(effort)) ||
            efforts.Distinct(StringComparer.Ordinal).Count() != efforts.Length)
            throw Invalid($"Model binding '{reference}' reasoning efforts must be distinct values from low, medium, high, xhigh.");

        return new ModelBindingCapability(
            reference,
            enabled,
            sourceMode,
            ReasoningEffortOrder.Where(efforts.Contains).ToImmutableArray());
    }

    private static InvalidOperationException Invalid(string message) =>
        new($"Projects & Config model-binding configuration is invalid: {message}");

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
