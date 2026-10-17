using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>
/// Immutable model-configuration identity carried with a runtime selection so later
/// consumers can verify the exact model, source mode, and configuration revision.
/// </summary>
public sealed record RuntimeModelBindingPin(
    int ContractVersion,
    string ModelSelectionReference,
    string ModelId,
    ModelSourceMode SourceMode,
    string ConfigurationRevision,
    string ConfigurationHash)
{
    /// <summary>Optional provider type for selections whose source is a named provider.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderType { get; init; }
}
