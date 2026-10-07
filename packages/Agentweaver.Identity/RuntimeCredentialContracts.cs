using Agentweaver.Abstractions;
using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

public sealed record RuntimeBinding(
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string AgentId,
    string TurnId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    string EnvironmentId,
    string PlacementUid,
    long PlacementGeneration,
    string ProfileId,
    Uri ConfigureEndpoint,
    Uri ObservationEndpoint)
{
    public long EnvironmentCurrentFencingGeneration { get; init; }
    public long EnvironmentProviderFencingGeneration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelSelectionReference { get; init; }
}

public enum RuntimeRegistrationState { Active, Revoked }

public sealed record RuntimeRegistration(
    Guid RuntimeInstanceId,
    long Revision,
    RuntimeBinding Binding,
    RuntimeRegistrationState State,
    DateTimeOffset ExpiresAt);

public sealed class RuntimeActorAuthorization(SecretCredential bearer, string? tenantSelector)
{
    [JsonIgnore]
    public SecretCredential Bearer { get; } = bearer;
    public string? TenantSelector { get; } = tenantSelector;
    public override string ToString() => nameof(RuntimeActorAuthorization) + " [REDACTED]";
}

// The implementation must use fresh authenticated owner credentials, never a saved actor ID.
public interface IRuntimeRegistrationOwner
{
    Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken);
}

public sealed record RuntimeBootstrapDeliveryReceipt(
    Guid OperationId,
    Guid GrantId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string PlacementUid,
    long PlacementGeneration,
    long ExecutionFence,
    string ConfigurationHash,
    DateTimeOffset DeliveredAt)
{
    public long EnvironmentCurrentFencingGeneration { get; init; }
    public long EnvironmentProviderFencingGeneration { get; init; }
}

// Only the Environment owner may implement delivery to its exact current placement.
// Credential values are transient input, not durable request or receipt fields.
public interface IRuntimeBootstrapDelivery
{
    Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(
        RuntimeRegistration registration,
        RuntimeActorAuthorization actor,
        Guid operationId,
        Guid grantId,
        string configurationHash,
        SecretCredential credential,
        CancellationToken cancellationToken);
}

public interface IRuntimePendingBootstrapVerifier
{
    Task<RuntimeGrantReceipt> VerifyPendingBootstrapDeliveryAsync(
        RuntimeCredentialProof proof,
        Guid deliveryOperationId,
        CancellationToken cancellationToken);
}

public enum RuntimeCredentialPurpose { Configure, Observe }
public enum RuntimeCredentialState { Active, Consumed, Revoked }

public sealed record RuntimeGrantReceipt(
    Guid GrantId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long Revision,
    string Issuer,
    RuntimeCredentialPurpose Purpose,
    Uri Audience,
    RuntimeCredentialState State,
    string ConfigurationHash,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RecordedAt);

public sealed class RuntimeCredentialProof(
    Guid grantId,
    Guid runtimeInstanceId,
    long revision,
    RuntimeCredentialPurpose purpose,
    Uri audience,
    string configurationHash,
    SecretCredential credential)
{
    public Guid GrantId { get; } = grantId;
    public Guid RuntimeInstanceId { get; } = runtimeInstanceId;
    public long Revision { get; } = revision;
    public RuntimeCredentialPurpose Purpose { get; } = purpose;
    public Uri Audience { get; } = audience;
    public string ConfigurationHash { get; } = configurationHash;
    [JsonIgnore]
    public SecretCredential Credential { get; } = credential;
    public override string ToString() => nameof(RuntimeCredentialProof) + " [REDACTED]";
}

public sealed class RuntimeCredentialIssue(RuntimeGrantReceipt receipt, SecretCredential credential)
{
    public RuntimeGrantReceipt Receipt { get; } = receipt;
    [JsonIgnore]
    public SecretCredential Credential { get; } = credential;
    public override string ToString() => nameof(RuntimeCredentialIssue) + " [REDACTED]";
}

public sealed class RuntimeAuthorizationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class RuntimeCredentialExchange(
    RuntimeGrantReceipt receipt, SecretCredential? credential, bool isReplay)
{
    public RuntimeGrantReceipt Receipt { get; } = receipt;
    [JsonIgnore]
    public SecretCredential? Credential { get; } = credential;
    public bool IsReplay { get; } = isReplay;
    public override string ToString() => nameof(RuntimeCredentialExchange) + " [REDACTED]";
}
