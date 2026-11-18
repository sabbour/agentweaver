using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentGovernance.Security;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private const string TrustEvidenceMarker = "skill-trust-private-marker";

    [Fact]
    public async Task PersistsExactSkillScanEvidenceAndPendingDecision()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);

        var invalidPath = new SkillContentCandidate(
            setup.Candidate.SkillMarkdown,
            [new SkillContentResourceInput("../outside.md", Encoding.UTF8.GetBytes("outside"))]);
        Assert.Throws<SkillContentValidationException>(() => setup.Skills.Preview(invalidPath));

        var result = await setup.Evidence.CreateAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            "trust-evidence-" + setup.Project.Suffix,
            CancellationToken.None);

        Assert.False(result.Replayed);
        Assert.Equal(setup.Imported.ContentDigest, result.ContentDigest);
        Assert.Equal(2, result.Reports.Length);
        Assert.DoesNotContain(
            TrustEvidenceMarker,
            JsonSerializer.Serialize(result.Reports),
            StringComparison.Ordinal);
        var markdown = Assert.Single(result.Reports.Where(
            report => report.InputKind == PromptContentScanInputKind.SkillMarkdown));
        var reference = Assert.Single(result.Reports.Where(
            report => report.InputKind == PromptContentScanInputKind.SkillResource));
        var content = setup.Candidate.Validate();
        Assert.Equal(
            Hash(SkillContentObjectStore.CreateCanonicalSkillMarkdown(content)),
            markdown.InputSha256);
        Assert.Equal(Hash(content.Resources[0].Content.AsSpan()), reference.InputSha256);
        Assert.Equal("docs/reference.md", reference.Source.RelativePath);
        Assert.Equal(PromptContentScanProvenance.Authored, markdown.Source.Provenance);
        Assert.Null(markdown.Source.SourceRevision);
        Assert.Null(markdown.Source.ResolvedCommitSha);

        var evidence = await db.SkillTrustEvidence.AsNoTracking()
            .Where(item =>
                item.ProjectId == setup.Project.Project.ProjectId &&
                item.ActorIssuer == setup.Project.Owner.Issuer &&
                item.ActorId == setup.Project.Owner.ActorId &&
                item.IdempotencyKey == "trust-evidence-" + setup.Project.Suffix)
            .ToArrayAsync();
        var saved = Assert.Single(evidence);
        Assert.Equal(setup.Imported.ContentDigest, saved.ContentDigest);
        Assert.Equal("source-alpha", saved.SourceId);
        Assert.Equal("source-revision-1", saved.SourceRevision);
        Assert.Equal("main", saved.RequestedRef);
        Assert.Equal(new string('a', 40), saved.ResolvedCommitSha);
        Assert.Equal("skills/alpha", saved.SelectedPath);
        Assert.Equal(result.ReportSetSha256, saved.ReportSetSha256);
        Assert.Equal(Hash(saved.ReportSetJson), saved.ReportSetSha256);
        await AssertImmutableAsync(
            """
            UPDATE projects_config.project_skill_trust_evidence
            SET report_set_sha256 = repeat('0', 64)
            WHERE evidence_id = @evidence_id
            """,
            ("evidence_id", saved.EvidenceId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.project_skill_trust_evidence WHERE evidence_id = '" +
            saved.EvidenceId.ToString("D") + "'");
        await AssertImmutableAsync("TRUNCATE TABLE projects_config.project_skill_trust_evidence");

        var decisions = await db.GovernanceDecisions.AsNoTracking()
            .Where(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Project &&
                item.ScopeId == setup.Project.Project.ProjectId &&
                item.ResourceType == "skill" &&
                item.ResourceId == setup.Imported.SkillId)
            .ToArrayAsync();
        var decision = Assert.Single(decisions);
        Assert.True(ProjectGovernanceDecisionCanonicalJson.TryDeserializeRecord(
            decision.CanonicalRecord, out var canonical));
        Assert.Equal(ProjectGovernanceOutcome.Pending, canonical!.Decision.Outcome);
        Assert.Equal(ProjectGovernanceApprovalState.Pending, canonical.Decision.ApprovalState);
        Assert.Equal(result.ReportSetSha256, canonical.Decision.OwnerReceiptId);
    }

    [Fact]
    public async Task ExactReplayIgnoresEvaluationTimeButChangedReportConflicts()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        var key = "trust-replay-" + setup.Project.Suffix;
        var evaluator = new PromptDefenseEvaluator();
        var firstTimes = new List<DateTime>();
        PromptDefenseReport EvaluateFirst(string input)
        {
            var report = evaluator.Evaluate(input);
            firstTimes.Add(report.EvaluatedAt.ToUniversalTime());
            return report;
        }

        var first = await setup.Evidence.CreateAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            key,
            EvaluateFirst,
            CancellationToken.None);

        var replayTimes = new List<DateTime>();
        PromptDefenseReport EvaluateReplay(string input)
        {
            var report = evaluator.Evaluate(input);
            replayTimes.Add(report.EvaluatedAt.ToUniversalTime());
            return report;
        }

        await Task.Delay(50);
        var replay = await setup.Evidence.CreateAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            key,
            EvaluateReplay,
            CancellationToken.None);

        Assert.All(firstTimes, time =>
            Assert.True(time < replayTimes.Min(), "The replay scan must have a later EvaluatedAt."));
        Assert.True(replay.Replayed);
        Assert.Equal(first.EvidenceId, replay.EvidenceId);
        Assert.Equal(first.ReportSetSha256, replay.ReportSetSha256);
        Assert.Equal(JsonSerializer.Serialize(first.Reports), JsonSerializer.Serialize(replay.Reports));
        Assert.Equal(first.Decision.DecisionId, replay.Decision.DecisionId);

        var changedReport = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                key,
                input => new PromptDefenseReport
                {
                    PromptHash = Hash(Encoding.UTF8.GetBytes(input)),
                    Grade = "F",
                },
                CancellationToken.None));
        Assert.Equal("skill_trust_evidence_idempotency_conflict", changedReport.Code);

        var savedCount = await db.SkillTrustEvidence.AsNoTracking()
            .CountAsync(item =>
                item.ProjectId == setup.Project.Project.ProjectId &&
                item.ActorIssuer == setup.Project.Owner.Issuer &&
                item.ActorId == setup.Project.Owner.ActorId &&
                item.IdempotencyKey == key);
        var decisionCount = await db.GovernanceDecisions.AsNoTracking()
            .CountAsync(item =>
                item.ScopeId == setup.Project.Project.ProjectId &&
                item.ResourceType == "skill" &&
                item.ResourceId == setup.Imported.SkillId);
        Assert.Equal(1, savedCount);
        Assert.Equal(1, decisionCount);
    }

    [Fact]
    public async Task TrustEvidenceRequiresCurrentProjectOwnerAndUnrevokedRevision()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        var missingRevision = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision + 1,
                "trust-missing-" + setup.Project.Suffix,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, missingRevision.StatusCode);

        await setup.Skills.RevokeAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            "Revoked for the trust-evidence test.",
            CancellationToken.None);

        var revokedRevision = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                "trust-revoked-" + setup.Project.Suffix,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, revokedRevision.StatusCode);

        var revokedOwner = await SeedCallerAsync(
            db,
            authorityStore,
            "trust-revoked-owner-" + setup.Project.Suffix,
            setup.Project.TenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            setup.Project.TenantId,
            ProjectAuthorityRole.TenantAdmin);
        var assignment = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == revokedOwner.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Tenant &&
            item.ResourceId == setup.Project.TenantId &&
            item.Role == ProjectAuthorityRole.TenantAdmin &&
            item.State == ProjectAuthorityRecordState.Active);
        await authorityStore.RevokeRoleAssignmentAsync(
            assignment.AssignmentId, assignment.Revision, "trust-evidence-test");

        var revokedAuthority = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            setup.Evidence.CreateAsync(
                revokedOwner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                "trust-role-revoked-" + setup.Project.Suffix,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedAuthority.StatusCode);

        var saved = await db.SkillTrustEvidence.AsNoTracking()
            .CountAsync(item => item.ProjectId == setup.Project.Project.ProjectId);
        var decisions = await db.GovernanceDecisions.AsNoTracking()
            .CountAsync(item => item.ScopeId == setup.Project.Project.ProjectId);
        Assert.Equal(0, saved);
        Assert.Equal(0, decisions);
    }

    [Fact]
    public async Task TrustEvidenceReadsOnlyItsProjectWhenProjectsRunInReverseOrder()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var first = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        var second = await CreateTrustEvidenceSetupAsync(db, authorityStore);

        foreach (var setup in new[] { second, first })
        {
            _ = await setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                "trust-order-" + setup.Project.Suffix,
                CancellationToken.None);
        }

        foreach (var setup in new[] { first, second })
        {
            var evidence = await db.SkillTrustEvidence.AsNoTracking()
                .Where(item =>
                    item.ProjectId == setup.Project.Project.ProjectId &&
                    item.ActorIssuer == setup.Project.Owner.Issuer &&
                    item.ActorId == setup.Project.Owner.ActorId &&
                    item.IdempotencyKey == "trust-order-" + setup.Project.Suffix)
                .ToArrayAsync();
            Assert.Single(evidence);
            var decisions = await db.GovernanceDecisions.AsNoTracking()
                .Where(item =>
                    item.ScopeId == setup.Project.Project.ProjectId &&
                    item.ResourceType == "skill" &&
                    item.ResourceId == setup.Imported.SkillId)
                .ToArrayAsync();
            Assert.Single(decisions);
        }
    }

    private async Task<SkillTrustEvidenceTestSetup> CreateTrustEvidenceSetupAsync(
        ProjectsConfigDbContext db,
        ProjectsConfigPrivilegedAuthorityStore authorityStore)
    {
        var project = await CreateSkillInventoryProjectAsync(db, authorityStore);
        var objects = new SkillTestObjectStore();
        var configurations = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var skills = new SkillContentService(
            db,
            new SkillContentObjectStore(objects),
            configurations,
            TimeProvider.System);
        var candidate = SkillCandidate(
            $"Instructions include {TrustEvidenceMarker}.",
            $"# Reference\n\n{TrustEvidenceMarker}\n");
        var preview = skills.Preview(candidate);
        var imported = await skills.ImportAsync(
            project.Owner,
            project.Project.ProjectId,
            new SkillContentImportRequest(
                "trust-import-" + project.Suffix,
                preview.ContentDigest,
                "skill-alpha",
                null,
                new SkillContentSourceSelection(
                    "source-alpha",
                    "source-revision-1",
                    "main",
                    new string('a', 40),
                    "skills/alpha"),
                candidate),
            CancellationToken.None);
        var evidence = new ProjectSkillTrustEvidenceService(
            skills,
            new ProjectGovernanceDecisionStore(
                new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()),
                TimeProvider.System));
        return new SkillTrustEvidenceTestSetup(project, skills, evidence, imported, candidate);
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record SkillTrustEvidenceTestSetup(
        SkillInventoryTestProject Project,
        SkillContentService Skills,
        ProjectSkillTrustEvidenceService Evidence,
        SkillContentImportReceipt Imported,
        SkillContentCandidate Candidate);
}
