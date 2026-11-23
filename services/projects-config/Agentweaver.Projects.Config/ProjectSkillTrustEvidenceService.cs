using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentGovernance.Security;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

internal sealed class ProjectSkillTrustEvidenceService(
    SkillContentService skills,
    ProjectGovernanceDecisionStore decisions)
{
    private const int FingerprintVersion = 1;
    private const int MaximumReportSetBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly PromptDefenseEvaluator Evaluator = new();

    internal Task<ProjectSkillTrustEvidenceReceipt> CreateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        CreateAsync(
            caller, projectId, skillId, revision, idempotencyKey,
            Evaluator.Evaluate, cancellationToken);

    internal async Task<ProjectSkillTrustEvidenceReceipt> CreateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        string idempotencyKey,
        Func<string, PromptDefenseReport> evaluate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(evaluate);
        RequireIdempotencyKey(idempotencyKey);

        var input = await skills.ReadTrustEvidenceInputAsync(
            caller, projectId, skillId, revision, cancellationToken).ConfigureAwait(false);
        var scanInputs = CreateScanInputs(projectId, input.Revision, input.Content);
        var scanStartedAt = DateTimeOffset.UtcNow;
        var reports = PromptContentScanner.ScanSet(scanInputs, evaluate);
        var scanCompletedAt = DateTimeOffset.UtcNow;
        if (reports.Any(report => report.EvaluatedAt < scanStartedAt || report.EvaluatedAt > scanCompletedAt))
            throw new InvalidOperationException("The Skill scan report timestamp is outside its scan window.");

        var reportSetJson = JsonSerializer.SerializeToUtf8Bytes(reports, JsonOptions);
        if (reportSetJson.Length > MaximumReportSetBytes)
            throw new InvalidOperationException("The Skill scan report set exceeds its storage limit.");
        var reportSetSha256 = Hash(reportSetJson);
        var fingerprint = Fingerprint(
            caller, projectId, input.Revision, idempotencyKey, reports);
        var saved = await skills.PersistTrustEvidenceAsync(
            caller,
            input.Revision,
            input.Content,
            idempotencyKey,
            fingerprint,
            reportSetJson,
            reportSetSha256,
            cancellationToken).ConfigureAwait(false);
        var storedReports = ReadReports(saved.Record);
        var decision = await decisions.AppendWithCurrentHeadAsync(
            NativeAcsPolicyScopeType.Project,
            saved.Record.ProjectId,
            "skill",
            saved.Record.SkillId,
            saved.Record.EvidenceId.ToString("N"),
            new ProjectGovernanceDecisionInput(
                DecisionType: "skill.trust.evidence.v1",
                Outcome: ProjectGovernanceOutcome.Pending,
                ApprovalState: ProjectGovernanceApprovalState.Pending,
                PolicyRevision: null,
                PolicyArtifactSha256: null,
                SelectedRule: null,
                InterventionPoint: null,
                RequestId: null,
                ActionId: null,
                RunId: null,
                AgentId: null,
                RoleId: null,
                HumanEntraOid: null,
                TenantId: null,
                BrokerIssuer: caller.Issuer,
                BrokerSubject: caller.ActorId,
                EffectiveRoleRevisions: [],
                OwnerReceiptId: saved.Record.ReportSetSha256,
                OwnerReceiptPosition: null,
                CompilerGeneration: null,
                ApplicationGeneration: null,
                CorrelationId: saved.Record.EvidenceId.ToString("N"),
                DecidedAt: saved.Record.CreatedAt),
            cancellationToken).ConfigureAwait(false);

        return new ProjectSkillTrustEvidenceReceipt(
            saved.Record.EvidenceId,
            saved.Record.ContentDigest,
            saved.Record.ReportSetSha256,
            storedReports,
            decision,
            saved.Replayed);
    }

    internal static string Fingerprint(
        ProjectAuthorizationContext caller,
        string projectId,
        ProjectSkillContentRevisionRecord revision,
        string idempotencyKey,
        ImmutableArray<PromptContentScanReportV1> reports)
    {
        var payload = new RequestFingerprintV1(
            FingerprintVersion,
            projectId,
            revision.SkillId,
            revision.Revision,
            revision.ContentDigest,
            new SourceMetadataV1(
                revision.SourceId,
                revision.SourceRevision,
                revision.RequestedRef,
                revision.ResolvedCommitSha,
                revision.SelectedPath),
            caller.Issuer,
            caller.ActorId,
            idempotencyKey,
            reports.Select(report => new ReportFingerprintV1(
                report.ContractVersion,
                report.InputKind,
                report.Source,
                report.InputSha256,
                report.InputByteCount,
                report.ScannerPackage,
                report.ScannerPackageVersion,
                report.VectorSetRevision,
                report.PolicyRevision,
                report.Status,
                report.Score,
                report.Grade,
                report.CoveredVectorCount,
                report.VectorCount,
                report.MeetsRecommendedThreshold,
                report.Findings,
                report.ErrorCode)).ToImmutableArray());
        return Hash(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
    }

    private static ImmutableArray<PromptContentScanInput> CreateScanInputs(
        string projectId,
        ProjectSkillContentRevisionRecord revision,
        ValidatedSkillContent content)
    {
        var hasSource = revision.SourceId is not null;
        if (hasSource !=
            (revision.SourceRevision is not null &&
             revision.RequestedRef is not null &&
             revision.ResolvedCommitSha is not null &&
             revision.SelectedPath is not null) ||
            !hasSource &&
            (revision.SourceRevision is not null ||
             revision.RequestedRef is not null ||
             revision.ResolvedCommitSha is not null ||
             revision.SelectedPath is not null))
            throw new InvalidOperationException("The stored Skill source metadata is incomplete.");

        var source = new PromptContentScanSourceV1(
            PromptContentScanOwnerKind.SkillContent,
            Hash(Encoding.UTF8.GetBytes($"{projectId}\0{revision.SkillId}")),
            revision.Revision,
            "SKILL.md",
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? PromptContentScanProvenance.Git
                : PromptContentScanProvenance.Authored,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? revision.SourceRevision
                : null,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? revision.ResolvedCommitSha
                : null);
        var inputs = ImmutableArray.CreateBuilder<PromptContentScanInput>(content.Resources.Length + 1);
        inputs.Add(new PromptContentScanInput(
            PromptContentScanInputKind.SkillMarkdown,
            source,
            SkillContentObjectStore.CreateCanonicalSkillMarkdown(content)));
        foreach (var resource in content.Resources)
            inputs.Add(new PromptContentScanInput(
                PromptContentScanInputKind.SkillResource,
                source with { RelativePath = resource.RelativePath },
                resource.Content.ToArray()));
        return inputs.ToImmutable();
    }

    private static ImmutableArray<PromptContentScanReportV1> ReadReports(
        ProjectSkillTrustEvidenceRecord record)
    {
        if (Hash(record.ReportSetJson) != record.ReportSetSha256)
            throw new InvalidOperationException("The stored Skill trust report hash is invalid.");
        return JsonSerializer.Deserialize<ImmutableArray<PromptContentScanReportV1>>(
                record.ReportSetJson, JsonOptions)
            is { IsDefault: false } reports
            ? reports
            : throw new InvalidOperationException("The stored Skill trust report set is invalid.");
    }

    private static void RequireIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw SkillContentServiceException.Invalid("A valid Skill trust evidence idempotency key is required.");
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record RequestFingerprintV1(
        int Version,
        string ProjectId,
        string SkillId,
        long Revision,
        string ContentDigest,
        SourceMetadataV1 Source,
        string ActorIssuer,
        string ActorId,
        string IdempotencyKey,
        ImmutableArray<ReportFingerprintV1> Reports);

    private sealed record SourceMetadataV1(
        string? SourceId,
        string? SourceRevision,
        string? RequestedRef,
        string? ResolvedCommitSha,
        string? SelectedPath);

    private sealed record ReportFingerprintV1(
        int ContractVersion,
        PromptContentScanInputKind InputKind,
        PromptContentScanSourceV1 Source,
        string InputSha256,
        int InputByteCount,
        string ScannerPackage,
        string ScannerPackageVersion,
        string VectorSetRevision,
        string PolicyRevision,
        PromptContentScanStatus Status,
        int? Score,
        string? Grade,
        int? CoveredVectorCount,
        int? VectorCount,
        bool? MeetsRecommendedThreshold,
        ImmutableArray<PromptContentScanFindingV1> Findings,
        string? ErrorCode);
}
