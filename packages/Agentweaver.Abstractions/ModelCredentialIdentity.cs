namespace Agentweaver.Abstractions;

/// <summary>Owner-approved identity of a model-provider credential slot.</summary>
public sealed record ModelCredentialIdentity(
    Guid CredentialSlotId,
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string ProviderId);
