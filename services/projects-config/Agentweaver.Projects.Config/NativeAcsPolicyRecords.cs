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
    bool Replayed,
    Guid? GovernanceDecisionId = null,
    string? GovernanceDecisionHash = null);

public sealed record NativeAcsPolicyReadResult(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string PolicyId,
    long Revision,
    string ContentSha256,
    NativeAcsArtifact Artifact,
    DateTimeOffset UpdatedAt);

public sealed record NativeAcsPlatformPolicyExpectedHead(
    string PolicyId,
    long Revision,
    string ContentSha256);

public sealed record NativeAcsPlatformPolicyPreparationReadResult(
    NativeAcsArtifact Artifact,
    string ProposedContentSha256,
    NativeAcsPlatformPolicyExpectedHead? ExpectedHead);

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
    public Guid? GovernanceDecisionId { get; set; }
    public string? GovernanceDecisionHash { get; set; }
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

public enum NativeAcsProjectPolicyBaselineState
{
    Approved,
    Pinned,
    Consumed,
    Revoked,
}

public sealed record NativeAcsProjectPolicyBaselineApprovalRequest(
    string ProjectId,
    NativeAcsArtifact Artifact,
    string IdempotencyKey,
    string Reason,
    string CorrelationId);

public sealed record NativeAcsProjectPolicyBaselineRevokeRequest(
    string ProjectId,
    Guid BaselineId,
    string Reason,
    string CorrelationId);

public sealed record NativeAcsProjectPolicyBaselineReceipt(
    Guid BaselineId,
    Guid InstallationId,
    string TenantId,
    string ProjectId,
    string PolicyId,
    NativeAcsProjectPolicyBaselineState State,
    string ContentSha256,
    string ProfileId,
    string ApproverIssuer,
    string ApproverActorId,
    DateTimeOffset ApprovedAt,
    string? OperatorIssuer,
    string? OperatorActorId,
    string? PlatformPolicyId,
    long? PlatformPolicyRevision,
    string? PlatformContentSha256,
    DateTimeOffset? PinnedAt,
    string? RevokedByIssuer,
    string? RevokedByActorId,
    DateTimeOffset? RevokedAt,
    bool Replayed);

public sealed class NativeAcsProjectPolicyBaselineRecord
{
    public Guid BaselineId { get; set; }
    public Guid InstallationId { get; set; }
    public required string TenantId { get; set; }
    public required string ProjectId { get; set; }
    public required string PolicyId { get; set; }
    public NativeAcsProjectPolicyBaselineState State { get; set; }
    public required string ContentSha256 { get; set; }
    public required string ArtifactJson { get; set; }
    public required string ProfileId { get; set; }
    public required string ApproverIssuer { get; set; }
    public required string ApproverActorId { get; set; }
    public Guid ApproverMembershipId { get; set; }
    public long ApproverMembershipRevision { get; set; }
    public Guid ApproverAssignmentId { get; set; }
    public long ApproverAssignmentRevision { get; set; }
    public required string ApprovalIdempotencyKey { get; set; }
    public required string ApprovalRequestDigest { get; set; }
    public required string ApprovalScopeDigest { get; set; }
    public required string ApprovalReason { get; set; }
    public required string ApprovalCorrelationId { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
    public string? OperatorIssuer { get; set; }
    public string? OperatorActorId { get; set; }
    public Guid? OperatorMembershipId { get; set; }
    public long? OperatorMembershipRevision { get; set; }
    public string? PinIdempotencyKey { get; set; }
    public string? PinRequestDigest { get; set; }
    public string? PinScopeDigest { get; set; }
    public string? PlatformPolicyId { get; set; }
    public long? PlatformPolicyRevision { get; set; }
    public string? PlatformContentSha256 { get; set; }
    public string? PinReason { get; set; }
    public string? PinCorrelationId { get; set; }
    public DateTimeOffset? PinnedAt { get; set; }
    public string? RevokedByIssuer { get; set; }
    public string? RevokedByActorId { get; set; }
    public string? RevokedReason { get; set; }
    public string? RevokedCorrelationId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public long? ConsumedRevision { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
