using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

/// <summary>Request to stage a complete project configuration as a casting proposal.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ProjectConfiguration Draft { get; init; }
}

/// <summary>Request to stage a casting proposal from a built-in team template at a known configuration revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateScenarioProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required string TemplateId { get; init; }
}

/// <summary>Request to stage a casting proposal from selected catalog role IDs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateManualProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ImmutableArray<string> RoleIds { get; init; }
}

/// <summary>Request to replace a pending proposal draft using its current draft revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectCastingProposalRequest
{
    public required long ExpectedDraftRevision { get; init; }
    public required ProjectConfiguration Draft { get; init; }
}

/// <summary>Request to confirm or reject a pending proposal at the expected draft revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionProjectCastingProposalRequest
{
    public required long ExpectedDraftRevision { get; init; }
}

/// <summary>Request to stage an explicit, digest-checked team transfer for review in the target project.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportProjectCastingTransferRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ProjectCastingTransfer Transfer { get; init; }
}

/// <summary>
/// Versioned transfer payload containing only project agent charters and casting, plus source provenance and a digest.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProjectCastingTransfer
{
    public const int CurrentFormatVersion = 1;

    public required int FormatVersion { get; init; }
    public required string SourceProjectId { get; init; }
    public required long SourceConfigurationRevision { get; init; }
    public required ImmutableArray<ProjectAgentCharter> AgentCharters { get; init; }
    public required ImmutableArray<ProjectAgentCast> Casting { get; init; }
    public required string ContentDigest { get; init; }
}

/// <summary>Identifies the source revision and verified payload digest retained on an imported proposal.</summary>
public sealed record ProjectCastingTransferProvenance(
    int FormatVersion,
    string SourceProjectId,
    long SourceConfigurationRevision,
    string ContentDigest);
