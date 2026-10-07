using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeSdkSourceRequest(
    RuntimeCredentialHttpRequest Authorization, SdkSessionFacts Source)
{
    public override string ToString() => nameof(RuntimeSdkSourceRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageObservationRequest(
    RuntimeCredentialHttpRequest Authorization, SdkUsageObservation Observation)
{
    public override string ToString() => nameof(RuntimeUsageObservationRequest) + " [REDACTED]";
}

public sealed record RuntimeSdkSourceReceipt(
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    Guid SourceGrantId,
    SdkSessionFacts Source,
    string CanonicalPayloadHash,
    DateTimeOffset RecordedAt);
