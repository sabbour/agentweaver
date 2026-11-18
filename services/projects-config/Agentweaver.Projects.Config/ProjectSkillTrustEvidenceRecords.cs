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
