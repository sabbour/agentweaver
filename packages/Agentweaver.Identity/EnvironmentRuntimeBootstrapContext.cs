using Agentweaver.Abstractions;
using System.Text.Json.Serialization;

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
    Uri ObservationEndpoint)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeOwnerContext? RuntimeOwnerContext { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxImageIdentity? Image { get; init; }
}
