using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Knowledge;

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RestoreKnowledgeRecordRequest
{
    public required int ExpectedRevision { get; init; }
    public required int Revision { get; init; }
    public string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApproveKnowledgeDecisionRequest
{
    public required int ExpectedRevision { get; init; }
    public string? Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PromoteKnowledgeProposalRequest
{
    public required int ExpectedRevision { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RejectKnowledgeProposalRequest
{
    public required int ExpectedRevision { get; init; }
}

public sealed record KnowledgeRevisionReference(
    string Kind,
    Guid RecordId,
    int Revision,
    Guid RevisionId);

public sealed record MemoryContextCompilation(
    string? Text,
    int OmittedMemoryCount,
    int OmittedSessionCount,
    ImmutableArray<string> OmissionCauses,
    ImmutableArray<KnowledgeRevisionReference> RevisionReferences);
