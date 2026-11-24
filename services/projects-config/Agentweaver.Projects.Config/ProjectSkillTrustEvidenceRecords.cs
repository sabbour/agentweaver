using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

public sealed class ProjectSkillTrustEvidenceRecord
{
    public Guid EvidenceId { get; set; }
    public required string ProjectId { get; set; }
    public required string SkillId { get; set; }
    public long Revision { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public required string ContentDigest { get; set; }
    public string? SourceId { get; set; }
    public string? SourceRevision { get; set; }
    public string? RequestedRef { get; set; }
    public string? ResolvedCommitSha { get; set; }
    public string? SelectedPath { get; set; }
    public required byte[] ReportSetJson { get; set; }
    public required string ReportSetSha256 { get; set; }
    public SkillTrustScoreStatus ScoreStatus { get; set; }
    public int? HeuristicScore { get; set; }
    public SkillTrustTier? TrustTier { get; set; }
    public bool SignatureVerified { get; set; }
    public bool CapabilityDeclarationValid { get; set; }
    public bool ShortDescription { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed record ProjectSkillTrustEvidenceInput(
    ProjectSkillContentRevisionRecord Revision,
    ValidatedSkillContent Content);

internal sealed record ProjectSkillTrustEvidenceSaveResult(
    ProjectSkillTrustEvidenceRecord Record,
    bool Replayed);

internal sealed record ProjectSkillTrustEvidenceReceipt(
    Guid EvidenceId,
    string ContentDigest,
    string ReportSetSha256,
    ImmutableArray<PromptContentScanReportV1> Reports,
    ProjectGovernanceDecisionReceipt Decision,
    bool Replayed);

internal sealed record ProjectSkillRefreshPolicyPin(
    string PolicyId,
    long Revision,
    string ContentSha256);

internal sealed record ProjectSkillRefreshRequest(
    string ProjectId,
    string RunId,
    string AgentId,
    string SkillId,
    long Revision,
    string ContentDigest,
    ProjectSkillRefreshPolicyPin ExpectedPlatformPolicy,
    ProjectSkillRefreshPolicyPin? ExpectedProjectPolicy,
    string IdempotencyKey,
    string Reason,
    string CorrelationId);

internal sealed record ProjectSkillRefreshReceipt(
    Guid EvidenceId,
    Guid AdmissionId,
    ProjectSkillRefreshPolicyPin PlatformPolicy,
    ProjectSkillRefreshPolicyPin? ProjectPolicy,
    DateTimeOffset ObservedAt,
    bool Replayed);

internal sealed record ProjectSkillRefreshPreparation(
    ProjectSkillRefreshRequest Request,
    string RequestFingerprint);

internal sealed record ProjectSkillRefreshStart(
    ProjectSkillRefreshPreparation? Preparation,
    ProjectSkillRefreshReceipt? Replay);

public sealed class ProjectSkillRefreshReceiptRecord
{
    public Guid ReceiptId { get; set; }
    public required string ProjectId { get; set; }
    public required string RunId { get; set; }
    public required string AgentId { get; set; }
    public required string SkillId { get; set; }
    public long Revision { get; set; }
    public required string ContentDigest { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestFingerprint { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid AdmissionId { get; set; }
    public required string PlatformPolicyId { get; set; }
    public long PlatformPolicyRevision { get; set; }
    public required string PlatformPolicySha256 { get; set; }
    public string? ProjectPolicyId { get; set; }
    public long? ProjectPolicyRevision { get; set; }
    public string? ProjectPolicySha256 { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ProjectSkillRefreshAuditIntentRecord
{
    public Guid AuditIntentId { get; set; }
    public Guid ReceiptId { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid AdmissionId { get; set; }
    public required string ProjectId { get; set; }
    public required string TenantId { get; set; }
    public required string ActorIssuer { get; set; }
    public required string ActorId { get; set; }
    public Guid MembershipId { get; set; }
    public long MembershipRevision { get; set; }
    public required string EffectiveRoleRevisionsJson { get; set; }
    public required string Reason { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
