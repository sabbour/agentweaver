using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>
/// Publishes only reasoning levels verified from a hosted model catalog. The payload never contains a model ID,
/// hosted credential, or caller token; caller identity is resolved by the receiving owner from its authorization token.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostedModelReasoningCatalogPublicationV1(
    string BindingsRevision,
    string Reference,
    Guid ConnectionId,
    ImmutableArray<string> SupportedReasoningEfforts,
    DateTimeOffset VerifiedAt);
