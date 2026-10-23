using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Live hosted reasoning choices returned by AgentHost for one project configuration revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostedModelReasoningCatalogV1(
    int ContractVersion,
    string ProjectId,
    long ProjectConfigurationRevision,
    string BindingsRevision,
    string State,
    string? UnavailableReason,
    ImmutableDictionary<string, HostedModelReasoningModelV1> Models);

/// <summary>Live reasoning choices or an explicit unavailable reason for one model reference.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HostedModelReasoningModelV1(
    string State,
    string? UnavailableReason,
    ImmutableArray<string> ReasoningEfforts);
