using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Data;
using System.Text;
using System.Text.Json;
using AgentGovernance.Security;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Agentweaver.Policy.Acs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private const string TrustEvidenceMarker = "skill-trust-private-marker";

    [Fact]
    public async Task SkillCutoverWaitsForInFlightWriterThenBlocksOlderReplica()
    {
        await using var writerDb = CreateDbContext();
        await using var controllerDb = CreateDbContext();
        await ResetSkillAdmissionCutoverAsync(writerDb);
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(writerDb, authorityStore);
        await using var writer = await writerDb.Database
            .BeginTransactionAsync(IsolationLevel.Serializable);
        _ = await writerDb.SkillAdmissionFences
            .FromSqlRaw("""
                SELECT id, state, generation, release_inventory_revision,
                       platform_skills_policy_sha256, updated_at
                FROM projects_config.project_skill_admission_fence
                WHERE id = 1
                FOR UPDATE
                """)
            .SingleAsync();
        var cutover = new SkillAdmissionService(controllerDb, TimeProvider.System).BeginCutoverAsync(
            "drain-" + setup.Project.Suffix, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(cutover.IsCompleted);

        var original = await writerDb.SkillContentRevisions.SingleAsync(item =>
            item.ProjectId == setup.Project.Project.ProjectId &&
            item.SkillId == setup.Imported.SkillId);
        writerDb.SkillContentRevisions.Add(new ProjectSkillContentRevisionRecord
        {
            ProjectId = original.ProjectId,
            SkillId = "in-flight-writer",
            Revision = 1,
            Name = original.Name,
            Description = original.Description,
            ContentDigest = original.ContentDigest,
            ObjectKey = original.ObjectKey,
            ResourceCount = original.ResourceCount,
            TotalBytes = original.TotalBytes,
            CreatedByActorId = original.CreatedByActorId,
            CreatedAt = original.CreatedAt,
            AdmissionClassification = ProjectSkillAdmissionClassification.LegacyThirdParty,
            SourceAuthorityKind = ProjectSkillSourceAuthorityKind.CallerClaimed,
        });
        await writerDb.SaveChangesAsync();
        await writer.CommitAsync();
        await cutover;

        var blocked = await Assert.ThrowsAsync<PostgresException>(() =>
            writerDb.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO projects_config.project_skill_content_revisions
                    (project_id, skill_id, revision, name, description, content_digest,
                     object_key, resource_count, total_bytes, created_by_actor_id, created_at,
                     admission_classification, source_authority_kind)
                VALUES
                    ({original.ProjectId}, 'older-replica', 1, {original.Name},
                     {original.Description}, {original.ContentDigest}, {original.ObjectKey},
                     {original.ResourceCount}, {original.TotalBytes}, {original.CreatedByActorId},
                     {original.CreatedAt}, 'ThirdParty', 'CallerClaimed')
                """));
        Assert.Contains("skill admission fence blocks imports", blocked.MessageText);
        await ResetSkillAdmissionCutoverAsync(writerDb);
    }

    [SkillAdmissionNativeAcsFact]
    public async Task SkillAdmissionCutoverGatesWritersAndRejectsForgedExemptionWithoutARevision()
    {
        await using var db = CreateDbContext();
        await ResetSkillAdmissionCutoverAsync(db);
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        using var publisher = RSA.Create(2048);
        var admission = new SkillAdmissionService(
            db,
            TimeProvider.System,
            PublisherKeys(publisher, "agentweaver-owner"));
        await SeedSkillsPolicyAsync(
            db,
            setup.Project.Owner,
            """{"schemaVersion":1,"minimumScore":600,"requireCompletedEvidence":true}""");
        await admission.BeginCutoverAsync(
            "release-" + setup.Project.Suffix, CancellationToken.None);

        var candidate = SkillCandidate(
            "A sufficiently long approved instruction for the cutover test.",
            "# Cutover\n");
        var preview = setup.Skills.Preview(candidate);
        var request = new SkillContentImportRequest(
            "cutover-blocked-" + setup.Project.Suffix,
            preview.ContentDigest,
            "forged-built-in",
            null,
            new SkillContentSourceSelection(
                "built-in", "forged", "main", new string('b', 40), "skills/forged"),
            candidate);
        var draining = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            setup.Skills.ImportAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                request,
                CancellationToken.None));
        Assert.Equal("skill_admission_unavailable", draining.Code);
        Assert.False(await db.SkillContentRevisions.AnyAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.SkillId == "forged-built-in"));

        var source = new ProjectMarketplaceSourceRecord
        {
            ProjectId = setup.Project.Project.ProjectId,
            SourceId = Guid.NewGuid(),
            Name = "Built-in release",
            NormalizedName = "BUILT-IN RELEASE",
            Repository = "agentweaver/release-skills",
            RequestedRef = "main",
            Revision = 1,
            State = ProjectMarketplaceSourceState.Active,
            CreatedByActorId = setup.Project.Owner.ActorId,
            UpdatedByActorId = setup.Project.Owner.ActorId,
            CreatedAt = TimeProvider.System.GetUtcNow(),
            UpdatedAt = TimeProvider.System.GetUtcNow(),
        };
        _ = await admission.RegisterVerifiedInventoryAsync(
            SignedInventory(
                publisher,
                "release-" + setup.Project.Suffix,
                "agentweaver-owner",
                source.Repository,
                new string('c', 40),
                "skills/release-only",
                new string('d', 64)),
            CancellationToken.None);
        await admission.ActivateCutoverAsync(CancellationToken.None);

        var denied = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            setup.Skills.ImportAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                request with { IdempotencyKey = "cutover-denied-" + setup.Project.Suffix },
                CancellationToken.None));
        Assert.Equal("skill_admission_denied", denied.Code);
        Assert.False(await db.SkillContentRevisions.AnyAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.SkillId == "forged-built-in"));
        Assert.False(await db.SkillImportIdempotency.AnyAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.IdempotencyKey == "cutover-denied-" + setup.Project.Suffix));
        Assert.False(await db.SkillAdmissions.AnyAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.SkillId == "forged-built-in"));
        await ResetSkillAdmissionCutoverAsync(db);
    }

    [SkillAdmissionNativeAcsFact]
    public async Task SkillAdmissionExactRetryWritesOneRevisionEvidenceAndReceipt()
    {
        await using var db = CreateDbContext();
        await ResetSkillAdmissionCutoverAsync(db);
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        using var publisher = RSA.Create(2048);
        var admission = new SkillAdmissionService(
            db,
            TimeProvider.System,
            PublisherKeys(publisher, "agentweaver-owner"));
        await SeedSkillsPolicyAsync(
            db,
            setup.Project.Owner,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        await admission.BeginCutoverAsync(
            "replay-" + setup.Project.Suffix, CancellationToken.None);
        var source = new ProjectMarketplaceSourceRecord
        {
            ProjectId = setup.Project.Project.ProjectId,
            SourceId = Guid.NewGuid(),
            Name = "Release",
            NormalizedName = "RELEASE",
            Repository = "agentweaver/release-skills",
            RequestedRef = "main",
            Revision = 1,
            State = ProjectMarketplaceSourceState.Active,
            CreatedByActorId = setup.Project.Owner.ActorId,
            UpdatedByActorId = setup.Project.Owner.ActorId,
            CreatedAt = TimeProvider.System.GetUtcNow(),
            UpdatedAt = TimeProvider.System.GetUtcNow(),
        };
        var content = SkillCandidate(
            "A sufficiently long approved instruction for exact replay.",
            "# Replay\n");
        var preview = setup.Skills.Preview(content);
        _ = await admission.RegisterVerifiedInventoryAsync(
            SignedInventory(
                publisher,
                "replay-" + setup.Project.Suffix,
                "agentweaver-owner",
                source.Repository,
                new string('a', 40),
                "skills/replay",
                preview.ContentDigest),
            CancellationToken.None);
        await admission.ActivateCutoverAsync(CancellationToken.None);
        var request = new SkillContentImportRequest(
            "replay-admission-" + setup.Project.Suffix,
            preview.ContentDigest,
            "replay-skill",
            null,
            null,
            content);

        var first = await setup.Skills.ImportAsync(
            setup.Project.Owner, setup.Project.Project.ProjectId, request, CancellationToken.None);
        var replay = await setup.Skills.ImportAsync(
            setup.Project.Owner, setup.Project.Project.ProjectId, request, CancellationToken.None);

        Assert.Equal(first, replay);
        Assert.Equal(1, await db.SkillAdmissions.CountAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.SkillId == first.SkillId));
        Assert.Equal(1, await db.SkillTrustEvidence.CountAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.SkillId == first.SkillId));
        Assert.Equal(1, await db.SkillImportIdempotency.CountAsync(
            item => item.ProjectId == setup.Project.Project.ProjectId &&
                item.IdempotencyKey == request.IdempotencyKey));
        await ResetSkillAdmissionCutoverAsync(db);
    }

    public sealed class SkillAdmissionNativeAcsFactAttribute : FactAttribute
    {
        public SkillAdmissionNativeAcsFactAttribute()
        {
            var opaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
            if (!OperatingSystem.IsLinux() ||
                string.IsNullOrWhiteSpace(opaPath) ||
                !File.Exists(opaPath))
                Skip = "The Skill admission native evaluation requires the pinned Linux ACS and OPA assets.";
        }
    }

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
    public async Task EvaluatorErrorsPersistPendingEvidenceAndReuseItOnReplay()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        const string keyPrefix = "trust-evaluator-error-";
        var key = keyPrefix + setup.Project.Suffix;
        PromptDefenseReport EvaluateWithError(string input) => new()
        {
            PromptHash = Hash(Encoding.UTF8.GetBytes(input)),
            Grade = "F",
        };

        var first = await setup.Evidence.CreateAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            key,
            EvaluateWithError,
            CancellationToken.None);
        var replay = await setup.Evidence.CreateAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            key,
            EvaluateWithError,
            CancellationToken.None);

        Assert.All(first.Reports, report =>
        {
            Assert.Equal(PromptContentScanStatus.EvaluatorError, report.Status);
            Assert.Equal("unsupported_vector_set", report.ErrorCode);
        });
        Assert.Equal(first.ReportSetSha256, replay.ReportSetSha256);
        Assert.Equal(first.Decision.DecisionId, replay.Decision.DecisionId);
        var decision = await db.GovernanceDecisions.AsNoTracking()
            .SingleAsync(item => item.DecisionId == first.Decision.DecisionId);
        Assert.True(ProjectGovernanceDecisionCanonicalJson.TryDeserializeRecord(
            decision.CanonicalRecord, out var canonical));
        Assert.Equal(ProjectGovernanceOutcome.Pending, canonical!.Decision.Outcome);
        Assert.True(replay.Replayed);
        Assert.True(replay.Decision.Replayed);
    }

    [Fact]
    public async Task DecisionAppendFailureReusesCommittedEvidenceOnExactRetry()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        var suffix = setup.Project.Suffix[..12];
        var sequence = "seq_trust_decision_" + suffix;
        var triggerFunction = "tf_trust_decision_" + suffix;
        var trigger = "tr_trust_decision_" + suffix;
        var key = "trust-decision-retry-" + setup.Project.Suffix;
        await ExecuteOwnerSqlAsync($"""
            CREATE SEQUENCE projects_config.{sequence} START 1;
            CREATE FUNCTION projects_config.{triggerFunction}() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.scope_id = '{setup.Project.Project.ProjectId}' AND
                   nextval('projects_config.{sequence}') = 1 THEN
                    RAISE EXCEPTION 'injected trust decision append failure';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {trigger}
            BEFORE INSERT ON projects_config.project_governance_decisions
            FOR EACH ROW EXECUTE FUNCTION projects_config.{triggerFunction}();
            """);

        try
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                setup.Evidence.CreateAsync(
                    setup.Project.Owner,
                    setup.Project.Project.ProjectId,
                    setup.Imported.SkillId,
                    setup.Imported.Revision,
                    key,
                    CancellationToken.None));
            Assert.Equal("injected trust decision append failure", failure.MessageText);

            var committedEvidence = await db.SkillTrustEvidence.AsNoTracking()
                .SingleAsync(item =>
                    item.ProjectId == setup.Project.Project.ProjectId &&
                    item.IdempotencyKey == key);
            Assert.Equal(0, await db.GovernanceDecisions.AsNoTracking()
                .CountAsync(item =>
                    item.ScopeId == setup.Project.Project.ProjectId &&
                    item.ResourceType == "skill" &&
                    item.ResourceId == setup.Imported.SkillId));

            var retry = await setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                key,
                CancellationToken.None);

            Assert.True(retry.Replayed);
            Assert.Equal(committedEvidence.EvidenceId, retry.EvidenceId);
            Assert.Equal(1, await db.SkillTrustEvidence.AsNoTracking()
                .CountAsync(item =>
                    item.ProjectId == setup.Project.Project.ProjectId &&
                    item.IdempotencyKey == key));
            var decision = await db.GovernanceDecisions.AsNoTracking()
                .SingleAsync(item =>
                    item.ScopeId == setup.Project.Project.ProjectId &&
                    item.ResourceType == "skill" &&
                    item.ResourceId == setup.Imported.SkillId);
            Assert.Equal(retry.Decision.DecisionId, decision.DecisionId);
            Assert.True(ProjectGovernanceDecisionCanonicalJson.TryDeserializeRecord(
                decision.CanonicalRecord, out var canonical));
            Assert.Equal(ProjectGovernanceOutcome.Pending, canonical!.Decision.Outcome);
        }
        finally
        {
            await ExecuteOwnerSqlAsync(
                $"DROP TRIGGER IF EXISTS {trigger} ON projects_config.project_governance_decisions; " +
                $"DROP FUNCTION IF EXISTS projects_config.{triggerFunction}(); " +
                $"DROP SEQUENCE IF EXISTS projects_config.{sequence};");
        }
    }

    [Fact]
    public async Task TrustEvidenceReplayRechecksProjectOwnerRevocationDuringScan()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        var tenantAdmin = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == setup.Project.Owner.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Tenant &&
            item.ResourceId == setup.Project.TenantId &&
            item.Role == ProjectAuthorityRole.TenantAdmin &&
            item.State == ProjectAuthorityRecordState.Active);
        await authorityStore.RevokeRoleAssignmentAsync(
            tenantAdmin.AssignmentId, tenantAdmin.Revision, "trust-owner-revocation-test");
        var owner = await ResolveCallerAsync(
            db,
            setup.Project.Owner.ActorId,
            setup.Project.TenantId,
            ["api.read", "projects.admin"]);
        var ownerAssignment = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == owner.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Project &&
            item.ResourceId == setup.Project.Project.ProjectId &&
            item.Role == ProjectAuthorityRole.Owner &&
            item.State == ProjectAuthorityRecordState.Active);
        _ = await SeedCallerAsync(
            db,
            authorityStore,
            "trust-backup-owner-" + setup.Project.Suffix,
            setup.Project.TenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Project,
            setup.Project.Project.ProjectId,
            ProjectAuthorityRole.Owner);
        var evaluator = new PromptDefenseEvaluator();
        var key = "trust-owner-race-" + setup.Project.Suffix;
        _ = await setup.Evidence.CreateAsync(
            owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            key,
            evaluator.Evaluate,
            CancellationToken.None);
        var revoked = false;
        PromptDefenseReport EvaluateThenRevokeOwner(string input)
        {
            if (!revoked)
            {
                authorityStore.RevokeRoleAssignmentAsync(
                    ownerAssignment.AssignmentId,
                    ownerAssignment.Revision,
                    "trust-owner-revocation-during-scan").GetAwaiter().GetResult();
                revoked = true;
            }
            return evaluator.Evaluate(input);
        }

        var failure = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            setup.Evidence.CreateAsync(
                owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                key,
                EvaluateThenRevokeOwner,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, failure.StatusCode);
        Assert.Equal(1, await db.SkillTrustEvidence.AsNoTracking()
            .CountAsync(item => item.ProjectId == setup.Project.Project.ProjectId));
        Assert.Equal(1, await db.GovernanceDecisions.AsNoTracking()
            .CountAsync(item => item.ScopeId == setup.Project.Project.ProjectId));
    }

    [Fact]
    public async Task TrustEvidenceReplayRechecksRevisionRevocationDuringScan()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateTrustEvidenceSetupAsync(db, authorityStore);
        var evaluator = new PromptDefenseEvaluator();
        var key = "trust-revision-race-" + setup.Project.Suffix;
        _ = await setup.Evidence.CreateAsync(
            setup.Project.Owner,
            setup.Project.Project.ProjectId,
            setup.Imported.SkillId,
            setup.Imported.Revision,
            key,
            evaluator.Evaluate,
            CancellationToken.None);
        var revoked = false;
        PromptDefenseReport EvaluateThenRevokeRevision(string input)
        {
            if (!revoked)
            {
                setup.Skills.RevokeAsync(
                    setup.Project.Owner,
                    setup.Project.Project.ProjectId,
                    setup.Imported.SkillId,
                    setup.Imported.Revision,
                    "Revoked during the trust-evidence scan.",
                    CancellationToken.None).GetAwaiter().GetResult();
                revoked = true;
            }
            return evaluator.Evaluate(input);
        }

        var failure = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                key,
                EvaluateThenRevokeRevision,
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, failure.StatusCode);
        Assert.Equal(1, await db.SkillTrustEvidence.AsNoTracking()
            .CountAsync(item => item.ProjectId == setup.Project.Project.ProjectId));
        Assert.Equal(1, await db.GovernanceDecisions.AsNoTracking()
            .CountAsync(item => item.ScopeId == setup.Project.Project.ProjectId));
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

        var project = await db.Projects.SingleAsync(item =>
            item.ProjectId == setup.Project.Project.ProjectId);
        project.State = ProjectLifecycleState.Archived;
        await db.SaveChangesAsync();
        var archivedProject = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                "trust-archived-" + setup.Project.Suffix,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, archivedProject.StatusCode);
        project.State = ProjectLifecycleState.Active;
        await db.SaveChangesAsync();

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
        var configurations = new ProjectsConfigService(
            db, CreateProviderCatalog(), TimeProvider.System);
        var secondProject = await configurations.CreateProjectAsync(
            first.Project.Owner,
            "Second trust project " + first.Project.Suffix,
            CancellationToken.None);
        var secondPreview = first.Skills.Preview(first.Candidate);
        var secondImported = await first.Skills.ImportAsync(
            first.Project.Owner,
            secondProject.ProjectId,
            new SkillContentImportRequest(
                "trust-import-second-" + first.Project.Suffix,
                secondPreview.ContentDigest,
                "skill-alpha",
                null,
                new SkillContentSourceSelection(
                    "source-beta",
                    "source-revision-1",
                    "main",
                    new string('b', 40),
                    "skills/beta"),
                first.Candidate),
            CancellationToken.None);
        var second = new SkillTrustEvidenceTestSetup(
            first.Project with { Project = secondProject },
            first.Skills,
            first.Evidence,
            secondImported,
            first.Candidate);
        const string key = "trust-shared-project-actor-key";

        var originals = new Dictionary<string, ProjectSkillTrustEvidenceReceipt>(StringComparer.Ordinal);
        foreach (var setup in new[] { second, first })
        {
            originals[setup.Project.Project.ProjectId] = await setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                key,
                CancellationToken.None);
        }

        foreach (var setup in new[] { first, second })
        {
            var replay = await setup.Evidence.CreateAsync(
                setup.Project.Owner,
                setup.Project.Project.ProjectId,
                setup.Imported.SkillId,
                setup.Imported.Revision,
                key,
                CancellationToken.None);
            var original = originals[setup.Project.Project.ProjectId];
            Assert.True(replay.Replayed);
            Assert.Equal(original.EvidenceId, replay.EvidenceId);
            Assert.Equal(original.Decision.DecisionId, replay.Decision.DecisionId);

            var evidence = await db.SkillTrustEvidence.AsNoTracking()
                .Where(item =>
                    item.ProjectId == setup.Project.Project.ProjectId &&
                    item.ActorIssuer == setup.Project.Owner.Issuer &&
                    item.ActorId == setup.Project.Owner.ActorId &&
                    item.IdempotencyKey == key)
                .ToArrayAsync();
            Assert.Equal(original.EvidenceId, Assert.Single(evidence).EvidenceId);
            var decisions = await db.GovernanceDecisions.AsNoTracking()
                .Where(item =>
                    item.ScopeId == setup.Project.Project.ProjectId &&
                    item.ResourceType == "skill" &&
                    item.ResourceId == setup.Imported.SkillId)
                .ToArrayAsync();
            Assert.Equal(original.Decision.DecisionId, Assert.Single(decisions).DecisionId);
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

    private static async Task SeedSkillsPolicyAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext owner,
        string skillsDataJson)
    {
        var artifact = NativeAcsUnifiedPolicyProfile.CreateArtifact(
            """{"allowed_action_ids":[]}""",
            """
            {"agentweaver":{"platform_egress":{"profile":"finite-network-v1","mode":"Allowlist","approved":[{"host":"approved.example.com","port":443}],"system":{"catalog":"hosted-copilot-v1","destinations":[{"host":"api.business.githubcopilot.com","port":443},{"host":"api.enterprise.githubcopilot.com","port":443},{"host":"api.github.com","port":443},{"host":"api.githubcopilot.com","port":443},{"host":"api.individual.githubcopilot.com","port":443}]}}}}
            """,
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            skillsDataJson);
        var now = TimeProvider.System.GetUtcNow();
        var head = await db.NativeAcsPolicyHeads.SingleOrDefaultAsync(item =>
            item.ScopeType == NativeAcsPolicyScopeType.Platform &&
            item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId);
        var revision = (head?.CurrentRevision ?? 0) + 1;
        if (head is null)
        {
            db.NativeAcsPolicyHeads.Add(new NativeAcsPolicyHeadRecord
            {
                ScopeType = NativeAcsPolicyScopeType.Platform,
                ScopeId = ProjectAuthorizationOwner.PlatformResourceId,
                PolicyId = "canonical",
                CurrentRevision = revision,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            head.CurrentRevision = revision;
            head.UpdatedAt = now;
        }
        db.NativeAcsPolicyRevisions.Add(new NativeAcsPolicyRevisionRecord
        {
            ScopeType = NativeAcsPolicyScopeType.Platform,
            ScopeId = ProjectAuthorizationOwner.PlatformResourceId,
            PolicyId = "canonical",
            Revision = revision,
            ContentSha256 = artifact.ContentSha256,
            ManifestBytes = artifact.ManifestBytes.ToArray(),
            CreatedByIssuer = owner.Issuer,
            CreatedByActorId = owner.ActorId,
            MembershipId = owner.MembershipId,
            MembershipRevision = owner.MembershipRevision,
            RoleAssignmentId = Guid.NewGuid(),
            RoleAssignmentRevision = 1,
            Reason = "Skill admission test.",
            CorrelationId = Guid.NewGuid().ToString("D"),
            CreatedAt = now,
        });
        foreach (var file in artifact.Files)
        {
            db.NativeAcsPolicyArtifactFiles.Add(new NativeAcsPolicyArtifactFileRecord
            {
                ScopeType = NativeAcsPolicyScopeType.Platform,
                ScopeId = ProjectAuthorizationOwner.PlatformResourceId,
                PolicyId = "canonical",
                Revision = revision,
                RelativePath = file.RelativePath,
                Bytes = file.Bytes.ToArray(),
            });
        }
        await db.SaveChangesAsync();
    }

    private static SkillReleasePublisherKeys PublisherKeys(RSA publisher, string publisherId) =>
        new([new SkillReleasePublisherKey(publisherId, publisher.ExportSubjectPublicKeyInfoPem())]);

    private static SkillReleaseInventoryManifest SignedInventory(
        RSA publisher,
        string inventoryRevision,
        string publisherId,
        string repository,
        string commitSha,
        string path,
        string contentDigest)
    {
        var releases = ImmutableArray.Create(new SkillReleaseInventoryEntry(
            repository, commitSha, path, contentDigest));
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = SkillReleaseInventoryManifest.CurrentSchemaVersion,
                inventoryRevision,
                publisherId,
                releases,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new SkillReleaseInventoryManifest(
            SkillReleaseInventoryManifest.CurrentSchemaVersion,
            inventoryRevision,
            publisherId,
            releases,
            Convert.ToBase64String(publisher.SignData(
                payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }

    private static async Task ResetSkillAdmissionCutoverAsync(ProjectsConfigDbContext db)
    {
        var fence = await db.SkillAdmissionFences.SingleAsync(item => item.Id == 1);
        fence.State = ProjectSkillAdmissionFenceState.Open;
        fence.Generation++;
        fence.ReleaseInventoryRevision = null;
        fence.PlatformSkillsPolicySha256 = null;
        await db.SaveChangesAsync();
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
