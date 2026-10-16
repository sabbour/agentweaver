using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public sealed record RuntimeModelBindingPin(
    int ContractVersion,
    string ModelSelectionReference,
    string ModelId,
    ModelSourceMode SourceMode,
    string ConfigurationRevision,
    string ConfigurationHash)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderType { get; init; }
}
