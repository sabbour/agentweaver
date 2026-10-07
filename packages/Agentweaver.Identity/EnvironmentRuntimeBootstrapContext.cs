using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed record EnvironmentRuntimeBootstrapContext(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string EnvironmentId,
    long LifecycleGeneration,
    long CurrentFencingGeneration,
    long ProviderFencingGeneration,
    long LeaseRevision,
    DateTimeOffset LeaseExpiresAt,
    ProviderResourceRef Resource,
    SandboxEndpointReference Endpoint,
    SandboxPlacementReference Placement,
    string ProfileId,
    Uri ConfigureEndpoint,
    Uri ObservationEndpoint);
