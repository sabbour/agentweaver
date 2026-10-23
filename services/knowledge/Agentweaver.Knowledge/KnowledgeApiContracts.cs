using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Knowledge;

/// <summary>Request body for creating a Memory, SessionContext, or Proposal record.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateKnowledgeRecordRequest
{
    public required KnowledgeRecordKind Kind { get; init; }
    public required string Type { get; init; }
    public string? Title { get; init; }
    public required string Content { get; init; }
    public string? Rationale { get; init; }
    public required string Importance { get; init; }
    public required ImmutableArray<string> Tags { get; init; }
}

/// <summary>Request body for a revision-checked update of an existing Knowledge record.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateKnowledgeRecordRequest
{
    public required int ExpectedRevision { get; init; }
    public required string Type { get; init; }
    public string? Title { get; init; }
    public required string Content { get; init; }
    public string? Rationale { get; init; }
    public required string Importance { get; init; }
    public required ImmutableArray<string> Tags { get; init; }
    public required KnowledgeRecordState State { get; init; }
    public string? Reason { get; init; }
    public Guid? SupersededByRecordId { get; init; }
}

/// <summary>Request body for restoring a record from an immutable historical revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RestoreKnowledgeRecordRequest
{
    public required int ExpectedRevision { get; init; }
    public required int Revision { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Request body for approving a pending Decision at the supplied current revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveKnowledgeDecisionRequest
{
    public required int ExpectedRevision { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Request body for promoting a Proposal only if its expected revision is still current.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PromoteKnowledgeProposalRequest
{
    public required int ExpectedRevision { get; init; }
}

/// <summary>Request body for rejecting a Proposal only if its expected revision is still current.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RejectKnowledgeProposalRequest
{
    public required int ExpectedRevision { get; init; }
}

/// <summary>Identifies the immutable revision of a Knowledge item included in compiled context.</summary>
public sealed record KnowledgeRevisionReference(
    string Kind,
    Guid RecordId,
    int Revision,
    Guid RevisionId);

/// <summary>Compiled context text together with omissions and the source revisions used to build it.</summary>
public sealed record MemoryContextCompilation(
    string? Text,
    int OmittedMemoryCount,
    int OmittedSessionCount,
    ImmutableArray<string> OmissionCauses,
    ImmutableArray<KnowledgeRevisionReference> RevisionReferences);

/// <summary>A Knowledge record and the run binding used to authorize access to its history.</summary>
/// <param name="AccessRunId">The authorizing run, not the record's source run.</param>
public sealed record ProjectKnowledgeRecord(string AccessRunId, KnowledgeRecord Record);

/// <summary>A globally ordered page of private Knowledge records discovered across accessible project runs.</summary>
public sealed record ProjectKnowledgeRecordPage(
    ImmutableArray<ProjectKnowledgeRecord> Items,
    int TotalCount,
    int Page,
    int PageSize);
