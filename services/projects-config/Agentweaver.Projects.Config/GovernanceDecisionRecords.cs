using System.Collections.Immutable;

namespace Agentweaver.Projects.Config;

public enum ProjectGovernanceOutcome
{
    Allow,
    Deny,
    Approve,
    Reject,
    Pending,
}

public enum ProjectGovernanceApprovalState
{
    NotRequired,
    Pending,
    Approved,
    Rejected,
}

public sealed record ProjectGovernanceRoleRevision(Guid AssignmentId, long Revision);

internal sealed record ProjectGovernanceDecisionInput(
    string DecisionType,
    ProjectGovernanceOutcome Outcome,
    ProjectGovernanceApprovalState ApprovalState,
    long? PolicyRevision,
    string? PolicyArtifactSha256,
    string? SelectedRule,
    string? InterventionPoint,
    string? RequestId,
    string? ActionId,
    string? RunId,
    string? AgentId,
    string? RoleId,
    Guid? HumanEntraOid,
    Guid? TenantId,
    string? BrokerIssuer,
    string? BrokerSubject,
    ImmutableArray<ProjectGovernanceRoleRevision> EffectiveRoleRevisions,
    string? OwnerReceiptId,
    long? OwnerReceiptPosition,
    string? CompilerGeneration,
    string? ApplicationGeneration,
    string CorrelationId,
    DateTimeOffset DecidedAt);

internal sealed record ProjectGovernanceDecisionV1(
    int SchemaVersion,
    Guid DecisionId,
    string ScopeType,
    string ScopeId,
    string ResourceType,
    string ResourceId,
    long Sequence,
    string PreviousHash,
    DateTimeOffset RecordedAt,
    ProjectGovernanceDecisionInput Decision);

public sealed class ProjectGovernanceDecisionStreamHeadRecord
{
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public long Sequence { get; set; }
    public long FenceGeneration { get; set; }
    public required string HeadHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ProjectGovernanceDecisionRecord
{
    public NativeAcsPolicyScopeType ScopeType { get; set; }
    public required string ScopeId { get; set; }
    public required string ResourceType { get; set; }
    public required string ResourceId { get; set; }
    public long Sequence { get; set; }
    public Guid DecisionId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestDigest { get; set; }
    public required string PreviousHash { get; set; }
    public required string RecordHash { get; set; }
    public required byte[] CanonicalRecord { get; set; }
    public string WriteTransactionId { get; set; } = null!;
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ProjectGovernanceDecisionOutboxRecord
{
    public Guid OutboxId { get; set; }
    public Guid DecisionId { get; set; }
    public required string EventType { get; set; }
    public long Sequence { get; set; }
    public required string RecordHash { get; set; }
    public required string PayloadJson { get; set; }
    public string WriteTransactionId { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed record ProjectGovernanceDecisionAppendRequest(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string ResourceType,
    string ResourceId,
    long ExpectedSequence,
    long ExpectedFenceGeneration,
    string ExpectedHeadHash,
    string IdempotencyKey,
    ProjectGovernanceDecisionInput Decision);

internal sealed record ProjectGovernanceDecisionReceipt(
    Guid DecisionId,
    Guid OutboxId,
    long Sequence,
    long FenceGeneration,
    string PreviousHash,
    string RecordHash,
    bool Replayed);

internal sealed record ProjectGovernanceDecisionStreamHeadSnapshot(
    long Sequence,
    long FenceGeneration,
    string HeadHash);

public sealed record ProjectGovernanceDecisionDetailView(
    string DecisionType,
    ProjectGovernanceOutcome Outcome,
    ProjectGovernanceApprovalState ApprovalState,
    long? PolicyRevision,
    string? PolicyArtifactSha256,
    string? SelectedRule,
    string? InterventionPoint,
    string? RequestId,
    string? ActionId,
    string? RunId,
    string? AgentId,
    string? RoleId,
    Guid? HumanEntraOid,
    Guid? TenantId,
    string? BrokerIssuer,
    string? BrokerSubject,
    ImmutableArray<ProjectGovernanceRoleRevision> EffectiveRoleRevisions,
    string? OwnerReceiptId,
    long? OwnerReceiptPosition,
    string? CompilerGeneration,
    string? ApplicationGeneration,
    string CorrelationId,
    DateTimeOffset DecidedAt);

public sealed record ProjectGovernanceDecisionHistoryItem(
    Guid DecisionId,
    long Sequence,
    string PreviousHash,
    string RecordHash,
    DateTimeOffset RecordedAt,
    ProjectGovernanceDecisionDetailView Decision);

public sealed record ProjectGovernanceDecisionHistoryPage(
    NativeAcsPolicyScopeType ScopeType,
    string ScopeId,
    string ResourceType,
    string ResourceId,
    ImmutableArray<ProjectGovernanceDecisionHistoryItem> Items,
    long? NextBeforeSequence);
