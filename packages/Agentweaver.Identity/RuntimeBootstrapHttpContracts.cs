using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Transient owner-channel request for verifying one pending bootstrap grant delivery.</summary>
// Values exist only on the authenticated HTTPS owner channel, never in stored receipts.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PendingBootstrapVerificationRequest(
    Guid GrantId,
    Guid RuntimeInstanceId,
    long Revision,
    Uri Audience,
    string ConfigurationHash,
    string CredentialValue,
    DateTimeOffset CredentialExpiresAt,
    Guid DeliveryOperationId)
{
    /// <summary>Returns a redacted label rather than credential values.</summary>
    public override string ToString() => nameof(PendingBootstrapVerificationRequest) + " [REDACTED]";
}

/// <summary>Transient HTTP request carrying bootstrap credential material to the runtime receiver.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapDeliveryRequest(
    Guid RuntimeInstanceId,
    Guid OperationId,
    Guid GrantId,
    string ConfigurationHash,
    string CredentialValue,
    DateTimeOffset CredentialExpiresAt)
{
    /// <summary>Returns a redacted label rather than credential values.</summary>
    public override string ToString() => nameof(RuntimeBootstrapDeliveryRequest) + " [REDACTED]";
}

/// <summary>Credential-free request to begin a runtime bootstrap operation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapRequest(
    Guid RuntimeInstanceId,
    Guid OperationId,
    string ConfigurationHash);

/// <summary>Request to obtain an owner grant for the accepted model credential.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeModelCredentialGrantRequest(
    Guid RuntimeInstanceId,
    Guid OperationId);

/// <summary>Owner-issued, registration-bound permission to redeem an exact model credential.</summary>
public sealed record RuntimeModelCredentialGrantReceipt(
    string GrantId,
    long Revision,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string ModelSelectionReference,
    SecretRef CredentialReference,
    string Purpose,
    DateTimeOffset ExpiresAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelSourceMode? SourceMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ConnectionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConnectionRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelCredentialKind? CredentialKind { get; init; }
}

/// <summary>Kind of secret material authorized by a model credential grant.</summary>
public enum RuntimeModelCredentialKind { GitHubUserAccess, ByokKey }

/// <summary>Proof presented to redeem the model credential grant.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeModelCredentialProof(RuntimeModelCredentialGrantReceipt Receipt);

/// <summary>Transient secret response returned by an owner credential endpoint.</summary>
public sealed record RuntimeModelCredentialResponse(
    RuntimeModelCredentialGrantReceipt Receipt, DateTimeOffset ExpiresAt, string Value)
{
    /// <summary>Returns a redacted label instead of the response credential value.</summary>
    public override string ToString() => nameof(RuntimeModelCredentialResponse) + " [REDACTED]";
}

/// <summary>Pairs an owner grant receipt with an in-memory credential that is never serialized.</summary>
public sealed class RuntimeModelCredential(
    RuntimeModelCredentialGrantReceipt receipt, SecretCredential credential)
{
    public RuntimeModelCredentialGrantReceipt Receipt { get; } = receipt;
    [JsonIgnore]
    public SecretCredential Credential { get; } = credential;
    /// <summary>Returns a redacted label instead of credential material.</summary>
    public override string ToString() => nameof(RuntimeModelCredential) + " [REDACTED]";
}

/// <summary>Transient request to consume or exchange a runtime credential proof.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeCredentialHttpRequest(
    Guid GrantId,
    Guid RuntimeInstanceId,
    long Revision,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeCredentialPurpose>))]
    RuntimeCredentialPurpose Purpose,
    Uri Audience,
    string ConfigurationHash,
    string CredentialValue,
    DateTimeOffset CredentialExpiresAt,
    Guid OperationId)
{
    /// <summary>Returns a redacted label rather than the credential value.</summary>
    public override string ToString() => nameof(RuntimeCredentialHttpRequest) + " [REDACTED]";
}

/// <summary>Owner response containing the grant receipt and an optional replay-safe credential value.</summary>
public sealed record RuntimeCredentialExchangeResponse(
    RuntimeGrantReceipt Receipt,
    string? CredentialValue,
    bool IsReplay)
{
    /// <summary>Returns a redacted label rather than any returned credential.</summary>
    public override string ToString() => nameof(RuntimeCredentialExchangeResponse) + " [REDACTED]";
}
