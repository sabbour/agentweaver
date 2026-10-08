using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

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
    public override string ToString() => nameof(PendingBootstrapVerificationRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapDeliveryRequest(
    Guid RuntimeInstanceId,
    Guid OperationId,
    Guid GrantId,
    string ConfigurationHash,
    string CredentialValue,
    DateTimeOffset CredentialExpiresAt)
{
    public override string ToString() => nameof(RuntimeBootstrapDeliveryRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapRequest(
    Guid RuntimeInstanceId,
    Guid OperationId,
    string ConfigurationHash);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeModelCredentialGrantRequest(
    Guid RuntimeInstanceId,
    Guid OperationId);

public sealed record RuntimeModelCredentialGrantReceipt(
    string GrantId,
    long Revision,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    string ModelSelectionReference,
    SecretRef CredentialReference,
    string Purpose,
    DateTimeOffset ExpiresAt);

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
    public override string ToString() => nameof(RuntimeCredentialHttpRequest) + " [REDACTED]";
}

public sealed record RuntimeCredentialExchangeResponse(
    RuntimeGrantReceipt Receipt,
    string? CredentialValue,
    bool IsReplay)
{
    public override string ToString() => nameof(RuntimeCredentialExchangeResponse) + " [REDACTED]";
}
