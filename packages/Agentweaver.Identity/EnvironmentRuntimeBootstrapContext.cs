using Agentweaver.Abstractions;
using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

public sealed record RuntimeMcpServerEndpoint(string Name, Uri HttpsUrl)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name) &&
        Name.Length <= 256 &&
        Name.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':') &&
        RuntimeContractValidation.IsHttpsEndpoint(HttpsUrl);

    public RuntimeMcpServerEndpoint Validate() =>
        IsValid ? this : throw new ArgumentException("The Runtime MCP server endpoint is invalid.");
}

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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeMcpServerEndpoint? McpServer { get; init; }
}
