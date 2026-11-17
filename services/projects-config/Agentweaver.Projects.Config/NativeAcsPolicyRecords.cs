using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

public enum NativeAcsPolicyScopeType
{
    Platform,
    Project,
}

public sealed record NativeAcsPolicyWriteRequest(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    long? ExpectedRevision,
    NativeAcsArtifact Artifact,
    string IdempotencyKey,
    string Reason,
    string CorrelationId);

public sealed record NativeAcsPolicyWriteReceipt(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256,
    Guid AuditId,
    Guid OutboxId,
    bool Replayed);

public sealed record NativeAcsPolicyPreparationReceipt(
    Guid PreparationId,
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    string? ExpectedHeadPolicyId,
    long? ExpectedHeadRevision,
    string? ExpectedHeadContentSha256,
    string ProposedContentSha256,
    string State,
    string? Checkpoint,
    DateTimeOffset CreatedAt,
    bool Replayed);

public sealed record NativeAcsPolicyReadResult(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256,
    NativeAcsArtifact Artifact,
    DateTimeOffset UpdatedAt);

public sealed record NativeAcsPolicyRevisionSummary(
    long Revision,
    string ContentSha256,
    DateTimeOffset CreatedAt);

public sealed record NativeAcsPolicyRevisionPage(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string? PolicyId,
    long? SnapshotRevision,
    ImmutableArray<NativeAcsPolicyRevisionSummary> Items,
    string? NextCursor);

public sealed record NativeAcsPolicyRevisionReadResult(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256,
    NativeAcsArtifact Artifact,
    DateTimeOffset CreatedAt);

public sealed record NativeAcsPolicyScopeSummary(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256,
    DateTimeOffset UpdatedAt);

public sealed record NativeAcsPolicyScopePage(
    ImmutableArray<NativeAcsPolicyScopeSummary> Items,
    string? NextCursor);

public sealed class NativeAcsPolicyHeadRecord
{
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string PolicyId { get; set; }
    public long CurrentRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class NativeAcsPolicyPreparationRecord
{
    public Guid PreparationId { get; set; }
    public required string ScopeDigest { get; set; }
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string PolicyId { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipRevision { get; set; }
    public Guid RoleAssignmentId { get; set; }
    public long RoleAssignmentRevision { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestDigest { get; set; }
    public string? ExpectedHeadPolicyId { get; set; }
    public long? ExpectedHeadRevision { get; set; }
    public string? ExpectedHeadContentSha256 { get; set; }
    public required string ProposedContentSha256 { get; set; }
    public required string ArtifactJson { get; set; }
    public required string Reason { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NativeAcsPolicyRevisionRecord
{
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string PolicyId { get; set; }
    public long Revision { get; set; }
    public required string ContentSha256 { get; set; }
    public required byte[] ManifestBytes { get; set; }
    public string WriteTransactionId { get; set; } = null!;
    public required string CreatedByIssuer { get; set; }
    public required string CreatedByActorId { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipRevision { get; set; }
    public Guid RoleAssignmentId { get; set; }
    public long RoleAssignmentRevision { get; set; }
    public required string Reason { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NativeAcsPolicyArtifactFileRecord
{
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string PolicyId { get; set; }
    public long Revision { get; set; }
    public required string RelativePath { get; set; }
    public required byte[] Bytes { get; set; }
    public string WriteTransactionId { get; set; } = null!;
}

public sealed class NativeAcsPolicyIdempotencyRecord
{
    public required string ScopeDigest { get; set; }
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string PolicyId { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestDigest { get; set; }
    public required string ReceiptJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NativeAcsPolicyChangeAuditRecord
{
    public Guid AuditId { get; set; }
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string PolicyId { get; set; }
    public long Revision { get; set; }
    public long? PreviousRevision { get; set; }
    public required string ChangeType { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipRevision { get; set; }
    public Guid RoleAssignmentId { get; set; }
    public long RoleAssignmentRevision { get; set; }
    public string? PreviousContentSha256 { get; set; }
    public required string ContentSha256 { get; set; }
    public required string Reason { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NativeAcsPolicyOutboxRecord
{
    public Guid OutboxId { get; set; }
    public required string EventType { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
