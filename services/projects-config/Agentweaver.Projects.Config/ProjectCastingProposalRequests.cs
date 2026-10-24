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
    public string? Universe { get; init; }
}

/// <summary>Request to stage a casting proposal from selected catalog role IDs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateManualProjectCastingProposalRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ImmutableArray<string> RoleIds { get; init; }
    public string? Universe { get; init; }
}

/// <summary>Request to replace a pending proposal draft using its current draft revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectCastingProposalRequest
{
    public required long ExpectedDraftRevision { get; init; }
    public required ProjectConfiguration Draft { get; init; }
}

/// <summary>Request to change one proposal member's catalog role and optional display title.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateProjectCastingProposalMemberRoleRequest
{
    public required long ExpectedDraftRevision { get; init; }
    public required string RoleId { get; init; }
    public string? CustomRoleTitle { get; init; }
}

/// <summary>Request to confirm or reject a pending proposal at the expected draft revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransitionProjectCastingProposalRequest
{
    public required long ExpectedDraftRevision { get; init; }
}

/// <summary>Starts a run-bound generation request without passing model credentials to Projects &amp; Config.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StartProjectCastingGenerationRequest
{
    public required long ExpectedConfigurationRevision { get; init; }
    public required ProjectCastingGenerationMode Mode { get; init; }
    public required string RunId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string RequestDigest { get; init; }
}

/// <summary>Completes a generation request with only the proposed charter and cast data.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteProjectCastingGenerationRequest
{
    public required string RunId { get; init; }
    public required ImmutableArray<ProjectAgentCharter> AgentCharters { get; init; }
    public required ImmutableArray<ProjectAgentCast> Casting { get; init; }
}

/// <summary>Records a safe failure code for a generation run without persisting its raw exception or output.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FailProjectCastingGenerationRequest
{
    public required string RunId { get; init; }
    public required string FailureCode { get; init; }
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
