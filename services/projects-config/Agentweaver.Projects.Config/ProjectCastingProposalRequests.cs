using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Agentweaver.Projects.Config;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ProjectConfiguration Draft { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateScenarioProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required string TemplateId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateManualProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ImmutableArray<string> RoleIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectCastingProposalRequest
{
    public required long ExpectedDraftRevision { get; init; }
    public required ProjectConfiguration Draft { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionProjectCastingProposalRequest
{
    public required long ExpectedDraftRevision { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImportProjectCastingTransferRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ProjectCastingTransfer Transfer { get; init; }
}

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

public sealed record ProjectCastingTransferProvenance(
    int FormatVersion,
    string SourceProjectId,
    long SourceConfigurationRevision,
    string ContentDigest);
