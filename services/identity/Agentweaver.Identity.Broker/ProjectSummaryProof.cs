using System.Text.Json.Serialization;

namespace Agentweaver.Identity.Broker;

/// <summary>Strict Projects owner response used by Broker project-scope proofs.</summary>
internal sealed record ProjectSummaryProof(
    string ProjectId,
    string Name,
    string State,
    long Revision,
    long ConfigurationRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    [JsonRequired]
    public ProjectSummaryRoleProof[]? EffectiveRoles { get; init; }
}

/// <summary>A role name and its exact Projects owner resource scope.</summary>
internal sealed record ProjectSummaryRoleProof(
    [property: JsonRequired] string ResourceType,
    [property: JsonRequired] string ResourceId,
    [property: JsonRequired] string Role);
