namespace Agentweaver.Abstractions;

/// <summary>Immutable identity and successful result of one retained creation operation.</summary>
public sealed record EnvironmentP1CreationReceipt(
    Guid OperationId,
    string ResourceKind,
    string ResourceId,
    long? ResourceGeneration,
    long? ProviderFencingGeneration,
    long? TransitionRevision,
    long? LeaseRevision,
    ProviderResourceRef? ProviderResource,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt);

/// <summary>
/// Current Environment-authorized join of actual creation records to a Core root and Pod.
/// Provider options, recovery descriptors and credentials are not projected.
/// </summary>
public sealed record EnvironmentP1CreationBinding(
    int ContractVersion,
    P1InvocationBinding Binding,
    EnvironmentGenerationFence EnvironmentFence,
    EnvironmentP1CreationReceipt Environment,
    EnvironmentP1CreationReceipt WorkspaceMetadata,
    EnvironmentP1CreationReceipt WorkspaceProvision,
    EnvironmentP1CreationReceipt SandboxProvision,
    SandboxPodObservation PodObservation);
