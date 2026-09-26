using System.ComponentModel.DataAnnotations;

namespace Agentweaver.Api.Memory;

public static class KnowledgeLifecycleStates
{
    public const string Active = "active";
    public const string Superseded = "superseded";
    public const string Archived = "archived";

    public static bool IsValid(string value) =>
        value is Active or Superseded or Archived;
}

public sealed class AgentMemoryRevision
{
    [Key] public required string RevisionId { get; set; }
    public int MemoryId { get; set; }
    public AgentMemory? Memory { get; set; }
    public required string ProjectId { get; set; }
    public int Revision { get; set; }
    public string? PreviousRevisionId { get; set; }
    public required string Actor { get; set; }
    public string? SourceRunId { get; set; }
    public required string Reason { get; set; }
    public required string AgentName { get; set; }
    public string? SessionId { get; set; }
    public required string Type { get; set; }
    public required string Importance { get; set; }
    public required string Content { get; set; }
    public string? Tags { get; set; }
    public required string Status { get; set; }
    public int? ReplacedById { get; set; }
    public required string SourceKind { get; set; }
    public string? SourceIdentityFingerprint { get; set; }
    public string? SourceRunReference { get; set; }
    public required string TrustState { get; set; }
    public string? ApprovedByFingerprint { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DecisionRevision
{
    [Key] public required string RevisionId { get; set; }
    public int DecisionId { get; set; }
    public Decision? Decision { get; set; }
    public required string ProjectId { get; set; }
    public int Revision { get; set; }
    public string? PreviousRevisionId { get; set; }
    public required string Actor { get; set; }
    public string? SourceRunId { get; set; }
    public required string Reason { get; set; }
    public required string AgentName { get; set; }
    public required string Type { get; set; }
    public required string Status { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? Rationale { get; set; }
    public string? Tags { get; set; }
    public int? SupersededById { get; set; }
    public required string SourceKind { get; set; }
    public string? SourceIdentityFingerprint { get; set; }
    public string? SourceRunReference { get; set; }
    public required string TrustState { get; set; }
    public string? ApprovedByFingerprint { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
