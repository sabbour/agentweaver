using Agentweaver.Abstractions;
using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

/// <summary>
/// Immutable binding between a runtime instance and its authenticated tenant, run, placement,
/// accepted selection, execution fence, endpoints, and optional model/run limits.
/// </summary>
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowStepId { get; init; }
    public long EnvironmentCurrentFencingGeneration { get; init; }
    public long EnvironmentProviderFencingGeneration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelSelectionReference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? ModelCredentialReference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelSourceMode? ModelSourceMode { get; init; }
    /// <summary>Reasoning effort authorized for the selected runtime model.</summary>
    public string ReasoningEffort { get; init; } = "high";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ModelBindingPin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ModelConnectionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectAuthorityResourceType? ModelConnectionScope { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxModelTurns { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxToolCalls { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPromptTokens { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxRevisionAttempts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotSoftCreditLimit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotHardCreditLimit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacementProviderId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long EnvironmentLifecycleGeneration { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long EnvironmentLeaseRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxImageIdentity? Image { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ResumeOperationId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ResumeAttemptId { get; init; }
}

/// <summary>Whether a runtime registration remains authorized for use.</summary>
public enum RuntimeRegistrationState { Active, Revoked }

/// <summary>Owner-issued registration revision and expiry wrapping a complete runtime binding.</summary>
public sealed record RuntimeRegistration(
    Guid RuntimeInstanceId,
    long Revision,
    RuntimeBinding Binding,
    RuntimeRegistrationState State,
    DateTimeOffset ExpiresAt);

/// <summary>Authenticated runtime actor bearer and optional tenant selector; string output is redacted.</summary>
public sealed class RuntimeActorAuthorization(SecretCredential bearer, string? tenantSelector)
{
    [JsonIgnore]
    public SecretCredential Bearer { get; } = bearer;
    public string? TenantSelector { get; } = tenantSelector;
    /// <summary>Returns a redacted label rather than the bearer value.</summary>
    public override string ToString() => nameof(RuntimeActorAuthorization) + " [REDACTED]";
}

/// <summary>Fresh owner lookup boundary used to validate a runtime registration before privileged work.</summary>
// The implementation must use fresh authenticated owner credentials, never a saved actor ID.
public interface IRuntimeRegistrationOwner
{
    /// <summary>Reads the authoritative current registration using authenticated owner credentials.</summary>
    Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken);
}

/// <summary>Credential-free evidence that a bootstrap delivery reached the registered placement.</summary>
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
/// <summary>Environment-owner boundary for delivering transient configuration credentials to a placement.</summary>
public interface IRuntimeBootstrapDelivery
{
    /// <summary>Delivers a credential to the exact current registration and returns non-secret receipt data.</summary>
    Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(
        RuntimeRegistration registration,
        RuntimeActorAuthorization actor,
        Guid operationId,
        Guid grantId,
        string configurationHash,
        SecretCredential credential,
        CancellationToken cancellationToken);
}

/// <summary>Owner boundary for verifying that an incoming bootstrap delivery is still pending.</summary>
public interface IRuntimePendingBootstrapVerifier
{
    /// <summary>Verifies the exact proof and delivery operation against current owner state.</summary>
    Task<RuntimeGrantReceipt> VerifyPendingBootstrapDeliveryAsync(
        RuntimeCredentialProof proof,
        Guid deliveryOperationId,
        CancellationToken cancellationToken);
}

/// <summary>Operation permitted by a short-lived runtime credential grant.</summary>
public enum RuntimeCredentialPurpose { Configure, Observe }

/// <summary>Lifecycle state of an owner-issued runtime credential grant.</summary>
public enum RuntimeCredentialState { Active, Consumed, Revoked }

/// <summary>Stable secret-purpose name used by model-session redemption.</summary>
public static class RuntimeSecretPurposes
{
    public const string ModelSession = "model-session";
}

/// <summary>Non-secret receipt binding a grant to a registration, purpose, audience, and expiry.</summary>
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

/// <summary>Transient proof carrying a grant and its credential to an authenticated owner endpoint.</summary>
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
    /// <summary>Returns a redacted label rather than credential material.</summary>
    public override string ToString() => nameof(RuntimeCredentialProof) + " [REDACTED]";
}

/// <summary>Owner-issued credential receipt paired with the in-memory secret value.</summary>
public sealed class RuntimeCredentialIssue(RuntimeGrantReceipt receipt, SecretCredential credential)
{
    public RuntimeGrantReceipt Receipt { get; } = receipt;
    [JsonIgnore]
    public SecretCredential Credential { get; } = credential;
    /// <summary>Returns a redacted label rather than credential material.</summary>
    public override string ToString() => nameof(RuntimeCredentialIssue) + " [REDACTED]";
}

/// <summary>Authorization failure carrying a safe stable code and optional fixed-credit evidence.</summary>
public sealed class RuntimeAuthorizationException : Exception
{
    /// <summary>Creates an authorization failure with only a stable code.</summary>
    public RuntimeAuthorizationException(string code) : this(code, null, null) { }

    /// <summary>Creates an authorization failure with a diagnostic detail.</summary>
    public RuntimeAuthorizationException(string code, string diagnosticDetail)
        : base($"{code}: {diagnosticDetail}")
    {
        Code = code;
    }

    /// <summary>Creates an authorization failure with optional guard evidence and cause.</summary>
    public RuntimeAuthorizationException(
        string code, RuntimeFixedCreditGuardReadResult? fixedCreditGuard, Exception? innerException)
        : base(code, innerException)
    {
        Code = code;
        FixedCreditGuard = fixedCreditGuard;
    }

    public string Code { get; }
    public RuntimeFixedCreditGuardReadResult? FixedCreditGuard { get; }
}

/// <summary>Result of exchanging a grant, including whether it was an idempotent replay.</summary>
public sealed class RuntimeCredentialExchange(
    RuntimeGrantReceipt receipt, SecretCredential? credential, bool isReplay)
{
    public RuntimeGrantReceipt Receipt { get; } = receipt;
    [JsonIgnore]
    public SecretCredential? Credential { get; } = credential;
    public bool IsReplay { get; } = isReplay;
    /// <summary>Returns a redacted label instead of credential material.</summary>
    public override string ToString() => nameof(RuntimeCredentialExchange) + " [REDACTED]";
}
