namespace Agentweaver.Abstractions;

public sealed record RuntimeModelBindingPin(
    int ContractVersion,
    string ModelSelectionReference,
    string ModelId,
    ModelSourceMode SourceMode,
    string ConfigurationRevision,
    string ConfigurationHash);
