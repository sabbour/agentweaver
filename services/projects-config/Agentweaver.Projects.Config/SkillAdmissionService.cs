using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed class SkillAdmissionService(
    ProjectsConfigDbContext db,
    TimeProvider timeProvider,
    SkillReleasePublisherKeys? publisherKeys = null,
    NativeAcsPolicyEvaluator? evaluator = null)
{
    internal const string SkillsPolicyDataPath = "policy/skills/data.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly IReadOnlyDictionary<string, string> _publisherKeys =
        publisherKeys?.Pinned ?? new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly NativeAcsPolicyEvaluator? _evaluator =
        evaluator ?? CreateEvaluatorFromEnvironment();

    private static NativeAcsPolicyEvaluator? CreateEvaluatorFromEnvironment()
    {
        var opaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        return !string.IsNullOrWhiteSpace(opaPath) && File.Exists(opaPath)
            ? new NativeAcsPolicyEvaluator(
                new NativeAcsArtifactValidator(opaPath),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "acs-evaluation")))
            : null;
    }

    internal async Task<ProjectSkillAdmissionFenceRecord> LockFenceAsync(
        CancellationToken cancellationToken)
    {
        return await db.SkillAdmissionFences
            .FromSqlRaw("""
                SELECT id, state, generation, cutover_generation, release_inventory_revision,
                       platform_skills_policy_sha256, updated_at
                FROM projects_config.project_skill_admission_fence
                WHERE id = 1
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<ProjectSkillAdmissionFenceRecord> LockFenceForReadAsync(
        CancellationToken cancellationToken)
    {
        return await db.SkillAdmissionFences
            .FromSqlRaw("""
                SELECT id, state, generation, cutover_generation, release_inventory_revision,
                       platform_skills_policy_sha256, updated_at
                FROM projects_config.project_skill_admission_fence
                WHERE id = 1
                FOR SHARE
                """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task BeginCutoverAsync(
        string releaseInventoryRevision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseInventoryRevision) || releaseInventoryRevision.Length > 128)
            throw SkillContentServiceException.Invalid("A valid release inventory revision is required.");
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var fence = await LockFenceAsync(cancellationToken).ConfigureAwait(false);
        if (fence.State == ProjectSkillAdmissionFenceState.Active)
            throw SkillContentServiceException.Conflict("The Skill admission cutover is already active.");
        fence.State = ProjectSkillAdmissionFenceState.Draining;
        fence.ReleaseInventoryRevision = releaseInventoryRevision;
        fence.Generation = checked(fence.Generation + 1);
        fence.CutoverGeneration = checked(fence.CutoverGeneration + 1);
        fence.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateAsync(
        ProjectAuthorizationContext caller,
        SkillReleaseInventoryManifest inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(inventory);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        if (!caller.IsPlatformAdmin)
            throw ProjectConfigException.Forbidden();

        await BeginCutoverAsync(inventory.InventoryRevision, cancellationToken).ConfigureAwait(false);
        _ = await RegisterVerifiedInventoryAsync(inventory, cancellationToken).ConfigureAwait(false);
        _ = await ReadPolicySnapshotAsync(
                ProjectAuthorizationOwner.PlatformResourceId,
                lockHeads: true,
                cancellationToken)
            .ConfigureAwait(false);
        await ActivateCutoverAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<ProjectSkillBuiltInReleaseRecord>> RegisterVerifiedInventoryAsync(
        SkillReleaseInventoryManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != SkillReleaseInventoryManifest.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.InventoryRevision) ||
            manifest.InventoryRevision.Length > 128 ||
            string.IsNullOrWhiteSpace(manifest.PublisherId) ||
            manifest.PublisherId.Length > 256 ||
            manifest.Releases.IsDefaultOrEmpty ||
            manifest.Releases.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Repository) ||
                entry.Repository.Length > 256 ||
                entry.ResolvedCommitSha is not { Length: 40 or 64 } ||
                !entry.ResolvedCommitSha.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                string.IsNullOrWhiteSpace(entry.SelectedPath) ||
                entry.SelectedPath.Length > 1024 ||
                entry.ContentDigest is not { Length: 64 } ||
                !entry.ContentDigest.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !_publisherKeys.TryGetValue(manifest.PublisherId, out var publicKeyPem))
            throw SkillContentServiceException.Invalid("The signed release inventory is invalid.");

        var unsignedPayload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                manifest.SchemaVersion,
                manifest.InventoryRevision,
                manifest.PublisherId,
                manifest.Releases,
            },
            JsonOptions);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.SignatureBase64);
        }
        catch (FormatException)
        {
            throw SkillContentServiceException.Invalid("The release inventory signature is invalid.");
        }
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (CryptographicException)
        {
            throw SkillAdmissionService.AdmissionUnavailable(
                "The pinned Skill publisher key is invalid.");
        }
        if (!rsa.VerifyData(
                unsignedPayload,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
            throw AdmissionDenied("The release inventory signature is not valid.");

        var signatureSha256 = Convert.ToHexString(SHA256.HashData(signature)).ToLowerInvariant();
        var releases = manifest.Releases.Select(entry =>
            new ProjectSkillBuiltInReleaseRecord
            {
                ReleaseId = Guid.NewGuid(),
                InventoryRevision = manifest.InventoryRevision,
                Repository = entry.Repository,
                ResolvedCommitSha = entry.ResolvedCommitSha,
                SelectedPath = entry.SelectedPath,
                ContentDigest = entry.ContentDigest,
                TrustedPublisherId = manifest.PublisherId,
                SignatureSha256 = signatureSha256,
                IsActive = true,
                CreatedAt = timeProvider.GetUtcNow(),
            }).ToArray();
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var fence = await LockFenceAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('agentweaver.skill_release_management_v1', '1', true)",
            cancellationToken).ConfigureAwait(false);
        foreach (var release in releases)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT projects_config.register_verified_skill_release(
                    {release.ReleaseId},
                    {release.InventoryRevision},
                    {release.Repository},
                    {release.ResolvedCommitSha},
                    {release.SelectedPath},
                    {release.ContentDigest},
                    {release.TrustedPublisherId},
                    {release.SignatureSha256},
                    {release.CreatedAt})
                """, cancellationToken).ConfigureAwait(false);
        fence.Generation = checked(fence.Generation + 1);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return releases;
    }

    internal async Task ActivateCutoverAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var fence = await LockFenceAsync(cancellationToken).ConfigureAwait(false);
        if (fence.State != ProjectSkillAdmissionFenceState.Draining ||
            string.IsNullOrWhiteSpace(fence.ReleaseInventoryRevision))
            throw SkillContentServiceException.Conflict("Start the Skill admission cutover before activation.");
        var releaseReady = await db.SkillBuiltInReleases.AsNoTracking().AnyAsync(
            release => release.IsActive &&
                release.InventoryRevision == fence.ReleaseInventoryRevision,
            cancellationToken).ConfigureAwait(false);
        if (!releaseReady)
            throw SkillContentServiceException.Unavailable(
                "The owner-verified built-in release inventory is not ready.");
        var policy = await ReadPolicySnapshotAsync(
            ProjectAuthorizationOwner.PlatformResourceId, lockHeads: true, cancellationToken)
            .ConfigureAwait(false);
        fence.PlatformSkillsPolicySha256 = policy.PlatformPolicySha256;
        fence.State = ProjectSkillAdmissionFenceState.Active;
        fence.Generation = checked(fence.Generation + 1);
        fence.CutoverGeneration = checked(fence.CutoverGeneration + 1);
        fence.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SkillAdmissionPolicySnapshot> ReadPolicySnapshotAsync(
        string projectId,
        bool lockHeads,
        CancellationToken cancellationToken)
    {
        if (lockHeads)
        {
            await LockPolicyScopeAnchorAsync(db, NativeAcsPolicyScopeType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId, cancellationToken)
                .ConfigureAwait(false);
            if (projectId != ProjectAuthorizationOwner.PlatformResourceId)
                await LockPolicyScopeAnchorAsync(db, NativeAcsPolicyScopeType.Project,
                        projectId, cancellationToken)
                    .ConfigureAwait(false);
        }
        var platform = await ReadPolicyHeadAsync(
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                lockHeads,
                cancellationToken)
            .ConfigureAwait(false) ??
            throw AdmissionUnavailable("The explicit platform Skills policy is not available.");
        var project = projectId == ProjectAuthorizationOwner.PlatformResourceId
            ? null
            : await ReadPolicyHeadAsync(
                NativeAcsPolicyScopeType.Project, projectId, lockHeads, cancellationToken)
                .ConfigureAwait(false);
        var platformData = ReadSkillsPolicyData(platform);
        var projectData = project is null ? null : ReadSkillsPolicyData(project);
        var requireCompleted = platformData.RequireCompletedEvidence ||
            projectData?.RequireCompletedEvidence == true;
        return new SkillAdmissionPolicySnapshot(
            platform.PolicyId,
            platform.Revision,
            platform.ContentSha256,
            project?.PolicyId,
            project?.Revision,
            project?.ContentSha256,
            Math.Max(platformData.MinimumScore, projectData?.MinimumScore ?? 0),
            requireCompleted,
            platform.Artifact,
            project?.Artifact);
    }

    internal async Task EvaluateAsync(
        string projectId,
        SkillAdmissionPolicySnapshot policy,
        SkillTrustScore score,
        bool evidenceComplete,
        bool builtInExempt,
        CancellationToken cancellationToken)
    {
        var evaluator = _evaluator ??
            throw AdmissionUnavailable("The canonical Skills policy evaluator is unavailable.");
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false) ??
            throw AdmissionUnavailable("The Skills admission project is unavailable.");
        var snapshot = new NativeAcsEvaluationSnapshot(
            new SessionIdentity(projectId, "skill-admission", "skill-admission"),
            project.TenantId,
            "skill-admission",
            "skill.admit",
            Skills: new NativeAcsSkillsEvaluationFacts(
                score.Value ?? -1,
                evidenceComplete,
                builtInExempt));
        await RequireAllowAsync(policy.PlatformArtifact, snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (policy.ProjectArtifact is { } projectArtifact)
            await RequireAllowAsync(projectArtifact, snapshot, cancellationToken)
                .ConfigureAwait(false);

        async Task RequireAllowAsync(
            NativeAcsArtifact artifact,
            NativeAcsEvaluationSnapshot facts,
            CancellationToken token)
        {
            var result = await evaluator.EvaluateComposedAsync(
                    artifact,
                    NativeAcsDomainId.Skills,
                    AgentControlSpecification.InterventionPoint.PreModelCall,
                    facts,
                    token)
                .ConfigureAwait(false);
            if (result.Outcome == PolicyEvaluationOutcome.Deny)
                throw AdmissionDenied("The canonical Skills policy denied the revision.");
            if (result.Outcome != PolicyEvaluationOutcome.Allow ||
                !string.Equals(result.ArtifactSha256, artifact.ContentSha256, StringComparison.Ordinal))
                throw AdmissionUnavailable("The canonical Skills policy evaluation is unavailable.");
        }
    }

    internal static async Task LockPolicyScopeAnchorAsync(
        ProjectsConfigDbContext db,
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        CancellationToken cancellationToken)
    {
        if (scopeType == NativeAcsPolicyScopeType.Platform)
        {
            _ = await db.SkillAdmissionFences.FromSqlRaw("""
                    SELECT id, state, generation, cutover_generation, release_inventory_revision,
                           platform_skills_policy_sha256, updated_at
                    FROM projects_config.project_skill_admission_fence
                    WHERE id = 1
                    FOR SHARE
                    """).SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var project = await db.Projects.FromSqlInterpolated($"""
            SELECT project_id, tenant_id, created_by_actor_id, name, state,
                   revision, configuration_revision, created_at, updated_at
            FROM projects_config.projects
            WHERE project_id = {scopeId}
            FOR SHARE
            """).SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
            throw AdmissionUnavailable("The Skills policy project anchor is unavailable.");
    }

    internal static SkillContentServiceException AdmissionDenied(string message) =>
        new("skill_admission_denied", message, StatusCodes.Status422UnprocessableEntity);

    internal static SkillContentServiceException AdmissionUnavailable(string message) =>
        new("skill_admission_unavailable", message, StatusCodes.Status503ServiceUnavailable);

    internal static void RequirePassingAdmission(
        ProjectSkillAdmissionRecord admission,
        ProjectSkillTrustEvidenceRecord evidence,
        SkillAdmissionPolicySnapshot currentPolicy,
        ProjectMarketplaceSourceRecord? currentSource,
        ProjectSkillBuiltInReleaseRecord? currentRelease,
        bool? isRevoked)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        if (isRevoked == true)
            throw SkillContentServiceException.Conflict("The assigned Skill revision was revoked.");
        if (admission.EvidenceId != evidence.EvidenceId ||
            admission.PlatformPolicyId != currentPolicy.PlatformPolicyId ||
            admission.PlatformPolicyRevision != currentPolicy.PlatformPolicyRevision ||
            admission.PlatformPolicySha256 != currentPolicy.PlatformPolicySha256 ||
            admission.ProjectPolicyId != currentPolicy.ProjectPolicyId ||
            admission.ProjectPolicyRevision != currentPolicy.ProjectPolicyRevision ||
            admission.ProjectPolicySha256 != currentPolicy.ProjectPolicySha256)
            throw SkillContentServiceException.Conflict(
                "The Skill admission policy changed; retry with the current policy.");
        if (admission.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified &&
            (currentSource is null ||
             currentSource.SourceId.ToString("D") != admission.VerifiedSourceId ||
             currentSource.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) !=
                 admission.VerifiedSourceRevision ||
             currentSource.RequestedRef != admission.VerifiedRequestedRef))
            throw AdmissionUnavailable("The verified Skill source authority is no longer available.");
        if (admission.Classification == ProjectSkillAdmissionClassification.LegacyThirdParty)
            throw AdmissionDenied("The pre-cutover Skill revision has no current admission evidence.");
        if (admission.Classification == ProjectSkillAdmissionClassification.BuiltInExempt)
        {
            if (currentRelease?.IsActive != true ||
                admission.ReleaseId != currentRelease.ReleaseId ||
                admission.TrustedPublisherId != currentRelease.TrustedPublisherId ||
                string.IsNullOrWhiteSpace(admission.SignatureSha256) ||
                admission.SignatureSha256 != currentRelease.SignatureSha256 ||
                admission.SourceAuthorityKind != ProjectSkillSourceAuthorityKind.MarketplaceVerified)
                throw AdmissionUnavailable("The owner-verified built-in release is no longer available.");
            return;
        }
        if (evidence.SignatureVerified &&
            (currentRelease?.IsActive != true ||
             admission.ReleaseId != currentRelease.ReleaseId ||
             admission.SignatureSha256 != currentRelease.SignatureSha256))
            throw AdmissionUnavailable("The verified Skill signature authority is unavailable.");
        if (admission.VerifiedResolvedCommitSha is null ||
            admission.VerifiedSelectedPath is null)
        {
            if (admission.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified)
                throw AdmissionUnavailable("The verified Skill source binding is incomplete.");
        }
        if (evidence.ScoreStatus != SkillTrustScoreStatus.Available ||
            evidence.HeuristicScore is not { } score ||
            evidence.TrustTier is null)
            throw AdmissionUnavailable("The required Skill score evidence is unavailable.");
        if (score < currentPolicy.MinimumScore)
            throw AdmissionDenied("The Skill trust score does not meet the current policy.");
    }

    internal async Task<ProjectSkillBuiltInReleaseRecord?> FindReleaseAsync(
        string repository,
        string resolvedCommitSha,
        string selectedPath,
        string contentDigest,
        string inventoryRevision,
        CancellationToken cancellationToken) =>
        await db.SkillBuiltInReleases.FromSqlInterpolated($"""
            SELECT release_id, inventory_revision, repository, resolved_commit_sha,
                   selected_path, content_digest, trusted_publisher_id,
                   signature_sha256, is_active, created_at
            FROM projects_config.project_skill_builtin_releases
            WHERE is_active = true
              AND inventory_revision = {inventoryRevision}
              AND repository = {repository}
              AND resolved_commit_sha = {resolvedCommitSha}
              AND selected_path = {selectedPath}
              AND content_digest = {contentDigest}
            """).SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<NativeAcsPolicyReadResult?> ReadPolicyHeadAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        bool lockHeads,
        CancellationToken cancellationToken)
    {
        var head = lockHeads
            ? await db.NativeAcsPolicyHeads.FromSqlInterpolated($"""
                SELECT scope_type, scope_id, policy_id, current_revision, created_at, updated_at
                FROM projects_config.native_acs_policy_heads
                WHERE scope_type = {(scopeType == NativeAcsPolicyScopeType.Platform ? "Platform" : "Project")}
                  AND scope_id = {scopeId}
                FOR SHARE
                """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.NativeAcsPolicyHeads.AsNoTracking().SingleOrDefaultAsync(
                item => item.ScopeType == scopeType && item.ScopeId == scopeId,
                cancellationToken).ConfigureAwait(false);
        if (head is null)
            return null;

        var revision = await db.NativeAcsPolicyRevisions.AsNoTracking().SingleOrDefaultAsync(
                item =>
                    item.ScopeType == scopeType &&
                    item.ScopeId == scopeId &&
                    item.PolicyId == head.PolicyId &&
                    item.Revision == head.CurrentRevision,
                cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
            throw AdmissionUnavailable("The current Skills policy revision is unavailable.");
        var files = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .Where(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision)
            .Select(item => new NativeAcsArtifactFile(
                item.RelativePath, item.Bytes.ToImmutableArray()))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var artifact = new NativeAcsArtifact(
            revision.ManifestBytes.ToImmutableArray(),
            files.ToImmutableArray(),
            revision.ContentSha256);
        if (NativeAcsArtifactHash.Compute(artifact) != revision.ContentSha256)
            throw AdmissionUnavailable("The current Skills policy artifact is corrupt.");
        var inspection = scopeType == NativeAcsPolicyScopeType.Platform
            ? NativeAcsUnifiedPolicyProfile.Inspect(artifact)
            : NativeAcsUnifiedPolicyProfile.InspectProject(artifact);
        if (!inspection.IsValid || !inspection.Domains.Contains(NativeAcsDomainId.Skills))
            throw AdmissionUnavailable("The current Skills policy artifact is outside the canonical profile.");
        return new NativeAcsPolicyReadResult(
            scopeType,
            scopeId,
            head.PolicyId,
            head.CurrentRevision,
            revision.ContentSha256,
            artifact,
            head.UpdatedAt);
    }

    private static SkillAdmissionPolicyDataV1 ReadSkillsPolicyData(NativeAcsPolicyReadResult policy)
    {
        var file = policy.Artifact.Files.SingleOrDefault(item =>
            item.RelativePath == SkillsPolicyDataPath);
        if (file is null)
        {
            throw AdmissionUnavailable("The canonical Skills policy domain is unavailable.");
        }

        SkillAdmissionPolicyDataV1? data;
        try
        {
            data = JsonSerializer.Deserialize<SkillAdmissionPolicyDataV1>(file.Bytes.AsSpan(), JsonOptions);
        }
        catch (JsonException)
        {
            throw AdmissionUnavailable("The canonical Skills policy is corrupt.");
        }
        if (data is null ||
            data.SchemaVersion != SkillAdmissionPolicyDataV1.CurrentSchemaVersion ||
            data.MinimumScore is < 0 or > 1000)
            throw AdmissionUnavailable("The canonical Skills policy is invalid.");
        return data;
    }
}
