using Agentweaver.Abstractions;
using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

/// <summary>Names an HTTPS MCP server endpoint made available during runtime bootstrap.</summary>
public sealed record RuntimeMcpServerEndpoint(string Name, Uri HttpsUrl)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name) &&
        Name.Length <= 256 &&
        Name.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':') &&
        RuntimeContractValidation.IsHttpsEndpoint(HttpsUrl);

    /// <summary>Returns this endpoint when its name and HTTPS URL satisfy the contract.</summary>
    public RuntimeMcpServerEndpoint Validate() =>
        IsValid ? this : throw new ArgumentException("The Runtime MCP server endpoint is invalid.");
}

/// <summary>
/// Environment owner's authenticated runtime bootstrap context, including lifecycle and
/// fencing generations, resource placement, and fixed configure and observation endpoints.
/// </summary>
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxRuntimeHttpsEndpoint? RuntimeEndpoint { get; init; }
}
