using System.Collections.Immutable;
using System.Data;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>Stable client error code, message, and HTTP status for skill-content operations.</summary>
public sealed class SkillContentServiceException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    /// <summary>Creates a bad-request error for malformed skill input.</summary>
    public static SkillContentServiceException Invalid(string message) =>
        new("invalid_skill_request", message, StatusCodes.Status400BadRequest);

    /// <summary>Creates a conflict for changed content, revisions, or revocation state.</summary>
    public static SkillContentServiceException Conflict(string message) =>
        new("skill_content_conflict", message, StatusCodes.Status409Conflict);

    /// <summary>Creates a conflict when the same import key is used for different actor-scoped input.</summary>
    public static SkillContentServiceException IdempotencyConflict() =>
        new("skill_import_idempotency_conflict", "The idempotency key was used for a different skill import.",
            StatusCodes.Status409Conflict);

    /// <summary>Creates a conflict when an evidence key is reused for different content or scan results.</summary>
    public static SkillContentServiceException TrustEvidenceIdempotencyConflict() =>
        new("skill_trust_evidence_idempotency_conflict",
            "The idempotency key was used for different Skill trust evidence.",
            StatusCodes.Status409Conflict);

    /// <summary>Creates a service-unavailable response when object storage is not configured.</summary>
    public static SkillContentServiceException Unavailable() =>
        new("skill_content_unavailable", "Skill content storage is not configured.",
            StatusCodes.Status503ServiceUnavailable);

    internal static SkillContentServiceException Unavailable(string message) =>
        new("skill_content_unavailable", message, StatusCodes.Status503ServiceUnavailable);
}

/// <summary>
/// Owns project skill revision metadata, idempotent imports, assignments, revocation, and accepted-run content reads.
/// </summary>
public sealed class SkillContentService(
    ProjectsConfigDbContext db,
    SkillContentObjectStore? objects,
    ProjectsConfigService configurations,
    TimeProvider timeProvider,
    SkillAdmissionService? admission = null,
    SkillTrustProjectionService? trustProjection = null) : ISkillContentService, ISkillAssignmentService
{
    private readonly SkillAdmissionService _admission = admission ?? new SkillAdmissionService(db, timeProvider);
    public const int DefaultInventoryPageSize = SkillMarketplaceBrowseService.DefaultPageSize;
    public const int MaxInventoryPageSize = SkillMarketplaceBrowseService.MaxPageSize;

    private const int InventoryCursorVersion = 1;
    private static readonly TimeSpan RefreshPersistenceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumSkillEvidenceAge = TimeSpan.FromDays(30);
    private const string InventoryCursorOrder = "skillId-ascending,revision-descending";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    internal Func<
        string,
        SkillAdmissionPolicySnapshot,
        SkillTrustScore,
        bool,
        bool,
        CancellationToken,
        Task>? EvaluateSkillsPolicyOverride { get; set; }
    internal Func<CancellationToken, Task>? AfterSkillRefreshStagedOverride { get; set; }

    /// <summary>Validates candidate bytes and returns metadata without writing storage or database state.</summary>
    public SkillContentPreview Preview(SkillContentCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var content = candidate.Validate();
        SkillContentScanAdmission.Scan(content, content.ContentDigest, 1, timeProvider);
        return new SkillContentPreview(
            content.Name,
            content.Description,
            content.ContentDigest,
            content.Resources.Length,
            SemanticByteCount(content));
    }

    /// <summary>
    /// Returns a stable, bounded revision page and current assignments after rechecking project-owner authority.
    /// </summary>
    public async Task<SkillContentInventoryPage> ListInventoryAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RequireProjectId(projectId);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        if (pageSize is < 1 or > MaxInventoryPageSize)
            throw SkillContentServiceException.Invalid(
                $"Skill inventory page size must be between 1 and {MaxInventoryPageSize}.");
        var after = cursor is null ? null : DecodeInventoryCursor(projectId, pageSize, cursor);

        await EnsureInventoryAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);

        VersionedProjectConfiguration current;
        (ProjectSkillContentRevisionRecord Revision, ProjectSkillContentRevocationRecord? Revocation)[] rows;
        // Read configuration and revisions from one snapshot, then recheck authority before returning the page.
        await using (var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false))
        {
            current = await configurations.GetProjectConfigurationAsync(
                caller, projectId, revision: null, cancellationToken).ConfigureAwait(false);

            var revisionQuery = db.SkillContentRevisions.AsNoTracking()
                .Where(item => item.ProjectId == projectId);
            if (after is not null)
            {
                var afterSkillId = after.SkillId;
                var afterRevision = after.Revision;
                revisionQuery = revisionQuery.Where(item =>
                    EF.Functions.Collate(item.SkillId, "C").CompareTo(afterSkillId) > 0 ||
                    (EF.Functions.Collate(item.SkillId, "C") == afterSkillId &&
                        item.Revision < afterRevision));
            }

            rows = await (
                from revision in revisionQuery
                join revocation in db.SkillContentRevocations.AsNoTracking()
                    on new { revision.ProjectId, revision.SkillId, revision.Revision }
                    equals new { revocation.ProjectId, revocation.SkillId, revocation.Revision }
                    into revocations
                from revocation in revocations.DefaultIfEmpty()
                orderby EF.Functions.Collate(revision.SkillId, "C"), revision.Revision descending
                select new { Revision = revision, Revocation = revocation })
                .Take(pageSize + 1)
                .Select(row => new ValueTuple<ProjectSkillContentRevisionRecord, ProjectSkillContentRevocationRecord?>(
                    row.Revision, row.Revocation))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureInventoryAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);

        var settings = current.Configuration.Skills.IsDefault
            ? ImmutableArray<SkillCatalogSetting>.Empty
            : current.Configuration.Skills;
        var assignments = settings.Select(setting => new SkillContentAssignmentSummary(
            setting.SkillId,
            setting.Enabled,
            setting.Order,
            setting.Revision,
            setting.ContentDigest,
            setting.AgentIds)).ToImmutableArray();
        var revisions = rows.Take(pageSize)
            .Select(row => new SkillContentRevisionSummary(
                row.Revision.SkillId,
                row.Revision.Revision,
                row.Revision.Name,
                row.Revision.Description,
                row.Revision.ContentDigest,
                row.Revision.ResourceCount,
                row.Revision.TotalBytes,
                row.Revision.CreatedByActorId,
                row.Revision.CreatedAt,
                row.Revision.SourceId,
                row.Revision.SourceRevision,
                row.Revision.RequestedRef,
                row.Revision.ResolvedCommitSha,
                row.Revision.SelectedPath,
                row.Revocation is not null,
                row.Revocation?.RevokedByActorId,
                row.Revocation?.RevokedAt))
            .ToImmutableArray();

        var nextCursor = rows.Length > pageSize && revisions.Length > 0
            ? EncodeInventoryCursor(projectId, pageSize, revisions[^1].SkillId, revisions[^1].Revision)
            : null;
        return new SkillContentInventoryPage(
            current.Revision,
            assignments,
            revisions,
            pageSize,
            nextCursor);
    }

    /// <summary>Projects the current admission decision for one exact project Skill revision.</summary>
    public async Task<SkillTrustProjection> ReadTrustProjectionAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RequireProjectId(projectId);
        RequireIdentifier(skillId, nameof(skillId));
        if (revision <= 0)
            throw SkillContentServiceException.Invalid("The Skill revision must be positive.");
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        await EnsureInventoryAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);

        var content = await db.SkillContentRevisions.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == projectId &&
                    item.SkillId == skillId &&
                    item.Revision == revision,
                cancellationToken)
            .ConfigureAwait(false) ?? throw ProjectConfigException.NotFound();
        var revoked = await db.SkillContentRevocations.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId &&
                    item.SkillId == skillId &&
                    item.Revision == revision,
                cancellationToken).ConfigureAwait(false);
        var fence = await db.SkillAdmissionFences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        var admission = await db.SkillAdmissions.AsNoTracking()
            .Where(item => item.ProjectId == projectId &&
                item.SkillId == skillId &&
                item.Revision == revision)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var evidence = admission is null
            ? null
            : await db.SkillTrustEvidence.AsNoTracking()
                .SingleOrDefaultAsync(item => item.EvidenceId == admission.EvidenceId, cancellationToken)
                .ConfigureAwait(false);

        await EnsureInventoryAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        var projection = trustProjection ?? new SkillTrustProjectionService(db, timeProvider);
        if (revoked)
        {
            return projection.Error(
                projectId,
                content,
                evidence?.CreatedAt,
                true,
                SkillContentServiceException.Conflict("The Skill revision was revoked."));
        }
        if (fence?.State != ProjectSkillAdmissionFenceState.Active)
        {
            return new SkillTrustProjection(
                projectId,
                content.SkillId,
                content.Revision,
                content.ContentDigest,
                content.SourceRevision,
                content.ResolvedCommitSha,
                content.SelectedPath,
                "unknown",
                admission?.Classification.ToString() ?? content.AdmissionClassification.ToString(),
                evidence?.CreatedAt,
                evidence is null ? null : Math.Max(
                    0, checked((long)(timeProvider.GetUtcNow() - evidence.CreatedAt).TotalSeconds)),
                false,
                null,
                "Unknown — the revision has no active admission decision.");
        }
        if (admission is null)
        {
            return projection.Error(
                projectId,
                content,
                evidenceCreatedAt: null,
                revoked: false,
                SkillAdmissionService.AdmissionDenied(
                    "The Skill revision has no active admission binding."));
        }
        if (evidence is null)
        {
            return projection.Error(
                projectId,
                content,
                null,
                false,
                SkillAdmissionService.AdmissionUnavailable(
                    "The required Skill admission evidence is unavailable."));
        }

        var errorEvidence = evidence;
        try
        {
            var policy = await _admission.ReadPolicySnapshotAsync(
                projectId, lockHeads: false, cancellationToken).ConfigureAwait(false);
            ProjectMarketplaceSourceRecord? source = null;
            if (admission.VerifiedSourceId is not null &&
                Guid.TryParse(admission.VerifiedSourceId, out var sourceId))
            {
                source = await db.MarketplaceSources.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == projectId &&
                        item.SourceId == sourceId &&
                        item.State == ProjectMarketplaceSourceState.Active,
                    cancellationToken).ConfigureAwait(false);
            }
            var release = admission.ReleaseId is { } releaseId
                ? await db.SkillBuiltInReleases.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.ReleaseId == releaseId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            SkillAdmissionService.RequirePassingAdmission(
                admission, evidence, policy, source, release, isRevoked: false);
            var verified = await SkillContentScanAdmission.ReadVerifiedContentAsync(
                    objects, content, cancellationToken)
                .ConfigureAwait(false);
            ValidateRuntimeEvidence(
                admission, evidence, content, verified, timeProvider.GetUtcNow());
            await EvaluateSkillsPolicyAsync(
                projectId,
                policy,
                SkillTrustScore.Compute(
                    verified.Description,
                    evidence.SignatureVerified,
                    evidence.CapabilityDeclarationValid),
                evidenceComplete: evidence.ReportSetJson.Length > 0,
                builtInExempt: admission.Classification ==
                    ProjectSkillAdmissionClassification.BuiltInExempt,
                cancellationToken).ConfigureAwait(false);
            SkillTrustProjection positiveProjection;
            var finalEvidence = evidence;
            await using (var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false))
            {
                var finalFence = await _admission.LockFenceForReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                var evaluatedAt = timeProvider.GetUtcNow();
                var finalPolicy = await _admission.ReadPolicySnapshotAsync(
                    projectId, lockHeads: true, cancellationToken).ConfigureAwait(false);
                await LockSkillRevisionForReadAsync(
                    projectId, skillId, revision, cancellationToken).ConfigureAwait(false);
                var lockedContent = await db.SkillContentRevisions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.ProjectId == projectId &&
                        item.SkillId == skillId &&
                        item.Revision == revision, cancellationToken)
                    .ConfigureAwait(false) ?? throw ProjectConfigException.NotFound();
                var finalAdmission = await db.SkillAdmissions.AsNoTracking()
                    .Where(item => item.ProjectId == projectId &&
                        item.SkillId == skillId &&
                        item.Revision == revision)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false) ??
                    throw SkillAdmissionService.AdmissionDenied(
                        "The Skill revision has no active admission binding.");
                finalEvidence = await db.SkillTrustEvidence.AsNoTracking()
                    .SingleOrDefaultAsync(
                        item => item.EvidenceId == finalAdmission.EvidenceId,
                        cancellationToken)
                    .ConfigureAwait(false) ??
                    throw SkillAdmissionService.AdmissionUnavailable(
                        "The required Skill admission evidence is unavailable.");
                errorEvidence = finalEvidence;
                var finalSource = finalAdmission.VerifiedSourceId is not null &&
                    Guid.TryParse(finalAdmission.VerifiedSourceId, out var finalSourceId)
                    ? await LockMarketplaceSourceAsync(projectId, finalSourceId, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                var finalRelease = finalAdmission.ReleaseId is { } finalReleaseId
                    ? await LockBuiltInReleaseAsync(finalReleaseId, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                if (finalFence.State != ProjectSkillAdmissionFenceState.Active ||
                    finalAdmission.CutoverGeneration != finalFence.CutoverGeneration ||
                    (finalRelease is not null &&
                     finalRelease.InventoryRevision != finalFence.ReleaseInventoryRevision))
                    throw SkillAdmissionService.AdmissionUnavailable(
                        "The Skill admission fence changed during projection.");
                var finalRevoked = await db.SkillContentRevocations.AsNoTracking().AnyAsync(
                    item => item.ProjectId == projectId &&
                        item.SkillId == skillId &&
                        item.Revision == revision,
                    cancellationToken).ConfigureAwait(false);

                SkillAdmissionService.RequirePassingAdmission(
                    finalAdmission, finalEvidence, finalPolicy, finalSource, finalRelease, finalRevoked);
                ValidateRuntimeEvidence(
                    finalAdmission, finalEvidence, lockedContent, verified, evaluatedAt);
                await EvaluateSkillsPolicyAsync(
                    projectId,
                    finalPolicy,
                    SkillTrustScore.Compute(
                        verified.Description,
                        finalEvidence.SignatureVerified,
                        finalEvidence.CapabilityDeclarationValid),
                    evidenceComplete: finalEvidence.ReportSetJson.Length > 0,
                    builtInExempt: finalAdmission.Classification ==
                        ProjectSkillAdmissionClassification.BuiltInExempt,
                    cancellationToken).ConfigureAwait(false);
                RequireFreshRuntimeEvidence(finalEvidence.CreatedAt, timeProvider.GetUtcNow());
                await EnsureInventoryAccessAsync(caller, projectId, cancellationToken)
                    .ConfigureAwait(false);
                if (await db.SkillContentRevocations.AsNoTracking().AnyAsync(
                    item => item.ProjectId == projectId &&
                        item.SkillId == skillId &&
                        item.Revision == revision,
                    cancellationToken).ConfigureAwait(false))
                    throw SkillContentServiceException.Conflict(
                        "The Skill revision was revoked during trust projection.");
                RequireFreshRuntimeEvidence(finalEvidence.CreatedAt, timeProvider.GetUtcNow());

                var finalBuiltIn = finalAdmission.Classification ==
                    ProjectSkillAdmissionClassification.BuiltInExempt;
                positiveProjection = new SkillTrustProjection(
                    projectId,
                    lockedContent.SkillId,
                    lockedContent.Revision,
                    lockedContent.ContentDigest,
                    lockedContent.SourceRevision,
                    lockedContent.ResolvedCommitSha,
                    lockedContent.SelectedPath,
                    finalBuiltIn ? "builtInExempt" : "admitted",
                    finalAdmission.Classification.ToString(),
                    finalEvidence.CreatedAt,
                    Math.Max(
                        0, checked((long)(timeProvider.GetUtcNow() - finalEvidence.CreatedAt).TotalSeconds)),
                    false,
                    null,
                    finalBuiltIn
                        ? "Built-in — exempt"
                        : "The current policy admits this exact revision.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return positiveProjection;
        }
        catch (SkillContentServiceException exception)
        {
            var currentlyRevoked = await db.SkillContentRevocations.AsNoTracking().AnyAsync(
                item => item.ProjectId == projectId &&
                    item.SkillId == skillId &&
                    item.Revision == revision,
                cancellationToken).ConfigureAwait(false);
            return projection.Error(
                projectId, content, errorEvidence.CreatedAt, currentlyRevoked, exception);
        }
        catch (SkillContentObjectStoreException exception)
        {
            return projection.Error(
                projectId,
                content,
                errorEvidence.CreatedAt,
                false,
                SkillAdmissionService.AdmissionUnavailable(exception.Message));
        }
    }

    /// <summary>
    /// Imports an exact-digest candidate into immutable object storage and persists its revision and scoped replay receipt.
    /// </summary>
    /// <exception cref="SkillContentServiceException">Input, expected digest/revision, authority, or idempotency key conflicts.</exception>
    public async Task<SkillContentImportReceipt> ImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillContentImportRequest request,
        CancellationToken cancellationToken) =>
        await ImportCoreAsync(
            caller, projectId, request, ownerProvenance: null, cancellationToken)
            .ConfigureAwait(false);

    internal async Task<SkillContentImportReceipt> ImportOwnerVerifiedAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillContentImportRequest request,
        SkillContentOwnerProvenance ownerProvenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownerProvenance);
        return await ImportCoreAsync(
            caller, projectId, request, ownerProvenance, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SkillContentImportReceipt> ImportCoreAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillContentImportRequest request,
        SkillContentOwnerProvenance? ownerProvenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireProjectId(projectId);
        RequireIdempotencyKey(request.IdempotencyKey);
        RequireHash(request.ExpectedContentDigest);
        if (request.SkillId is not null)
            RequireIdentifier(request.SkillId, nameof(request.SkillId));
        if (request.ExpectedRevision is <= 0)
            throw SkillContentServiceException.Invalid("Expected skill revision must be positive.");
        ValidateSource(request.Source);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        var content = request.Candidate.Validate();
        if (!string.Equals(content.ContentDigest, request.ExpectedContentDigest, StringComparison.Ordinal))
            throw SkillContentServiceException.Conflict("The previewed skill content digest has changed.");
        var requestDigest = ImportRequestDigest(request);
        var scopeDigest = IdempotencyScopeDigest(caller, projectId, request.IdempotencyKey);

        var store = objects ?? throw SkillContentServiceException.Unavailable();
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var fence = await _admission.LockFenceAsync(cancellationToken).ConfigureAwait(false);
        if (fence.State == ProjectSkillAdmissionFenceState.Draining)
            throw SkillAdmissionService.AdmissionUnavailable(
                "Skill content imports are draining for admission cutover.");
        if (ownerProvenance is not null)
        {
            await ownerProvenance.VerifyCurrentSourceAsync(cancellationToken).ConfigureAwait(false);
            var lockedSource = await LockMarketplaceSourceAsync(
                    projectId, ownerProvenance.Source.SourceId, cancellationToken)
                .ConfigureAwait(false);
            if (lockedSource != ownerProvenance.Source)
                throw MarketplaceSourceException.RevisionConflict();
            if (!string.Equals(
                    ownerProvenance.ResolvedCommitSha,
                    request.Source?.ResolvedCommitSha,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ownerProvenance.SelectedPath,
                    request.Source?.SelectedPath,
                    StringComparison.Ordinal))
                throw SkillContentServiceException.Conflict(
                    "The verified marketplace source binding changed during import.");
        }

        // The digest scope includes project, actor, and key; an existing row is replayed only for identical request data.
        var prior = await db.SkillImportIdempotency.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.ScopeDigest == scopeDigest,
                cancellationToken)
            .ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ReadIdempotentReceipt(prior, caller, request, requestDigest);
        }

        var skillId = request.SkillId ?? Guid.NewGuid().ToString("N");
        var current = await db.SkillContentRevisions.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.SkillId == skillId)
            .OrderByDescending(item => item.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null && request.ExpectedRevision is not null ||
            current is not null && request.ExpectedRevision != current.Revision)
            throw SkillContentServiceException.Conflict("The expected skill revision is no longer current.");

        var revision = checked((current?.Revision ?? 0) + 1);
        var policy = fence.State == ProjectSkillAdmissionFenceState.Active
            ? await _admission.ReadPolicySnapshotAsync(projectId, lockHeads: true, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var reports = SkillContentScanAdmission.ScanReports(
            content,
            SkillContentScanAdmission.StoredOwnerId(projectId, skillId),
            revision,
            timeProvider,
            ownerProvenance is null
                ? PromptContentScanProvenance.Authored
                : PromptContentScanProvenance.Git,
            ownerProvenance is null
                ? null
                : ownerProvenance.Source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ownerProvenance?.ResolvedCommitSha);
        var objectKey = MakeObjectKey(projectId, skillId, revision, content.ContentDigest);
        // The deterministic revision key lets an exact retry verify/reuse a blob if its database transaction did not commit.
        var reference = await store.WriteAsync(objectKey, content, cancellationToken).ConfigureAwait(false);
        if (reference.ContentDigest != content.ContentDigest)
            throw new SkillContentObjectStoreException(
                SkillContentObjectStoreException.IntegrityErrorCode,
                "The immutable skill content object digest changed during storage.");

        var now = timeProvider.GetUtcNow();
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        var revisionRecord = new ProjectSkillContentRevisionRecord
        {
            ProjectId = projectId,
            SkillId = skillId,
            Revision = revision,
            Name = content.Name,
            Description = content.Description,
            ContentDigest = content.ContentDigest,
            ObjectKey = objectKey.Value,
            ResourceCount = content.Resources.Length,
            TotalBytes = SemanticByteCount(content),
            CreatedByActorId = caller.ActorId,
            CreatedAt = now,
            SourceId = request.Source?.SourceId,
            SourceRevision = request.Source?.SourceRevision,
            RequestedRef = request.Source?.RequestedRef,
            ResolvedCommitSha = request.Source?.ResolvedCommitSha,
            SelectedPath = request.Source?.SelectedPath,
            AdmissionClassification = policy is null
                ? ProjectSkillAdmissionClassification.LegacyThirdParty
                : ownerProvenance is null
                    ? ProjectSkillAdmissionClassification.ThirdParty
                    : ProjectSkillAdmissionClassification.ThirdParty,
            SourceAuthorityKind = ownerProvenance is null
                ? ProjectSkillSourceAuthorityKind.CallerClaimed
                : ProjectSkillSourceAuthorityKind.MarketplaceVerified,
        };
        db.SkillContentRevisions.Add(revisionRecord);
        if (fence.State == ProjectSkillAdmissionFenceState.Active)
        {
            var release = ownerProvenance is null
                ? null
                : await _admission.FindReleaseAsync(
                    ownerProvenance.Source.Repository,
                    ownerProvenance.ResolvedCommitSha,
                    ownerProvenance.SelectedPath,
                    content.ContentDigest,
                    fence.ReleaseInventoryRevision!,
                    cancellationToken).ConfigureAwait(false);
            if (release is not null)
                revisionRecord.AdmissionClassification = ProjectSkillAdmissionClassification.BuiltInExempt;
            var score = SkillTrustScore.Compute(
                content.Description,
                release?.SignatureSha256 is not null,
                !content.CapabilityRequests.IsEmpty);
            await EvaluateSkillsPolicyAsync(
                    projectId,
                    policy!,
                    score,
                    evidenceComplete: reports.All(report =>
                        report.Status == PromptContentScanStatus.Completed),
                    builtInExempt: release?.SignatureSha256 is not null,
                    cancellationToken)
                .ConfigureAwait(false);
            var reportSetJson = JsonSerializer.SerializeToUtf8Bytes(reports, JsonOptions);
            var evidence = new ProjectSkillTrustEvidenceRecord
            {
                EvidenceId = Guid.NewGuid(),
                ProjectId = projectId,
                SkillId = skillId,
                Revision = revision,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = requestDigest,
                ContentDigest = content.ContentDigest,
                SourceId = request.Source?.SourceId,
                SourceRevision = request.Source?.SourceRevision,
                RequestedRef = request.Source?.RequestedRef,
                ResolvedCommitSha = request.Source?.ResolvedCommitSha,
                SelectedPath = request.Source?.SelectedPath,
                ReportSetJson = reportSetJson,
                ReportSetSha256 = Hash(reportSetJson),
                ScoreStatus = score.Status,
                HeuristicScore = score.Value,
                TrustTier = score.Tier,
                SignatureVerified = score.SignatureVerified,
                CapabilityDeclarationValid = score.CapabilityDeclarationValid,
                ShortDescription = score.ShortDescription,
                CreatedAt = now,
            };
            db.SkillTrustEvidence.Add(evidence);
            db.SkillAdmissions.Add(new ProjectSkillAdmissionRecord
            {
                AdmissionId = Guid.NewGuid(),
                ProjectId = projectId,
                SkillId = skillId,
                Revision = revision,
                EvidenceId = evidence.EvidenceId,
                CutoverGeneration = fence.CutoverGeneration,
                Classification = revisionRecord.AdmissionClassification,
                SourceAuthorityKind = revisionRecord.SourceAuthorityKind,
                VerifiedSourceId = ownerProvenance?.Source.SourceId.ToString("D"),
                VerifiedSourceRevision = ownerProvenance?.Source.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                VerifiedRequestedRef = ownerProvenance?.Source.RequestedRef,
                VerifiedResolvedCommitSha = ownerProvenance?.ResolvedCommitSha,
                VerifiedSelectedPath = ownerProvenance?.SelectedPath,
                ReleaseId = release?.ReleaseId,
                TrustedPublisherId = release?.TrustedPublisherId,
                SignatureSha256 = release?.SignatureSha256,
                PlatformPolicyId = policy!.PlatformPolicyId,
                PlatformPolicyRevision = policy.PlatformPolicyRevision,
                PlatformPolicySha256 = policy.PlatformPolicySha256,
                ProjectPolicyId = policy.ProjectPolicyId,
                ProjectPolicyRevision = policy.ProjectPolicyRevision,
                ProjectPolicySha256 = policy.ProjectPolicySha256,
                CreatedAt = now,
            });
            if (release is null && score.Value < policy.MinimumScore)
                throw SkillAdmissionService.AdmissionDenied(
                    "The Skill trust score does not meet the current policy.");
        }
        var receipt = new SkillContentImportReceipt(
            skillId,
            revision,
            content.Name,
            content.Description,
            content.ContentDigest,
            content.Resources.Length,
            SemanticByteCount(content));
        db.SkillImportIdempotency.Add(new ProjectSkillImportIdempotencyRecord
        {
            ProjectId = projectId,
            ScopeDigest = scopeDigest,
            ActorIssuer = caller.Issuer,
            ActorId = caller.ActorId,
            IdempotencyKey = request.IdempotencyKey,
            RequestDigest = requestDigest,
            ReceiptJson = JsonSerializer.Serialize(receipt, JsonOptions),
            CreatedAt = now,
        });
        try
        {
            if (ownerProvenance is not null)
            {
                await ownerProvenance.VerifyCurrentSourceAsync(cancellationToken).ConfigureAwait(false);
                var lockedSource = await LockMarketplaceSourceAsync(
                        projectId, ownerProvenance.Source.SourceId, cancellationToken)
                    .ConfigureAwait(false);
                if (lockedSource != ownerProvenance.Source)
                    throw MarketplaceSourceException.RevisionConflict();
                await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
            }
            if (fence.State != ProjectSkillAdmissionFenceState.Open)
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('agentweaver.skill_admission_v1', '1', true)",
                    cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw SkillContentServiceException.Conflict("The skill revision changed during import.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw SkillContentServiceException.Conflict("The skill revision or import idempotency key changed concurrently.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw SkillContentServiceException.Conflict("The skill import conflicted with a concurrent update.");
        }
    }

    internal async Task<ProjectSkillTrustEvidenceInput> ReadTrustEvidenceInputAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RequireProjectId(projectId);
        RequireIdentifier(skillId, nameof(skillId));
        if (revision <= 0)
            throw SkillContentServiceException.Invalid("Skill revision must be positive.");
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);

        var stored = await FindAvailableRevisionAsync(
            projectId, skillId, revision, cancellationToken).ConfigureAwait(false);
        var store = objects ?? throw SkillContentServiceException.Unavailable();
        var content = await store.ReadVerifiedAsync(
            new ObjectKey(stored.ObjectKey), stored.ContentDigest, cancellationToken).ConfigureAwait(false);
        if (content.Name != stored.Name ||
            content.Description != stored.Description ||
            content.Resources.Length != stored.ResourceCount ||
            SemanticByteCount(content) != stored.TotalBytes)
            throw new SkillContentObjectStoreException(
                SkillContentObjectStoreException.IntegrityErrorCode,
                "The immutable skill revision metadata does not match its stored content.");

        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        _ = await FindAvailableRevisionAsync(
            projectId, skillId, revision, stored.ContentDigest, cancellationToken).ConfigureAwait(false);
        return new ProjectSkillTrustEvidenceInput(stored, content);
    }

    internal async Task<ProjectSkillRefreshStart> BeginRefreshAsync(
        ProjectAuthorizationContext caller,
        ProjectSkillRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRefreshRequest(caller, request);
        await EnsureWriteAccessAsync(caller, request.ProjectId, cancellationToken).ConfigureAwait(false);
        await RequireAcceptedRefreshAssignmentAsync(request, cancellationToken).ConfigureAwait(false);
        var fingerprint = RefreshRequestFingerprint(caller, request);
        var evidence = await db.SkillTrustEvidence.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ProjectId == request.ProjectId &&
                item.ActorIssuer == caller.Issuer &&
                item.ActorId == caller.ActorId &&
                item.IdempotencyKey == request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (evidence is not null)
        {
            var requestCancellationToken = cancellationToken;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
            deadline.CancelAfter(RefreshPersistenceTimeout);
            cancellationToken = deadline.Token;
            IDbContextTransaction? transaction = null;
            try
            {
                transaction = await db.Database
                    .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(
                        "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'",
                        cancellationToken)
                    .ConfigureAwait(false);
                await LockCurrentSkillWriteAuthorityAsync(caller, request.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
                var activeProject = await db.Projects
                    .FromSqlInterpolated($"""
                        SELECT project_id, tenant_id, created_by_actor_id, name, state, revision,
                               configuration_revision, created_at, updated_at
                        FROM projects_config.projects
                        WHERE project_id = {request.ProjectId}
                          AND tenant_id = {caller.TenantId}
                        FOR SHARE
                        """)
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (activeProject is null)
                    throw ProjectConfigException.NotFound();
                if (activeProject.State != ProjectLifecycleState.Active)
                    throw ProjectConfigException.Conflict("Archived projects cannot refresh Skill evidence.");
                await RequireAcceptedRefreshAssignmentAsync(request, cancellationToken).ConfigureAwait(false);
                var lockedEvidence = await db.SkillTrustEvidence.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.EvidenceId == evidence.EvidenceId, cancellationToken)
                    .ConfigureAwait(false);
                var lockedReceipt = lockedEvidence is null
                    ? null
                    : await db.SkillRefreshReceipts.AsNoTracking()
                        .SingleOrDefaultAsync(
                            item => item.EvidenceId == lockedEvidence.EvidenceId,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (lockedReceipt is null)
                    throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
                var replay = ReadRefreshReceipt(lockedReceipt, caller, request, fingerprint);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ProjectSkillRefreshStart(null, replay);
            }
            catch (Exception exception) when (IsRefreshSerializationFailure(exception))
            {
                await RollbackRefreshAsync(transaction).ConfigureAwait(false);
                throw SkillContentServiceException.Conflict(
                    "The Skill refresh replay conflicted with a concurrent update.");
            }
            catch (Exception exception) when (
                !requestCancellationToken.IsCancellationRequested &&
                (IsRefreshUnavailableFailure(exception) || deadline.IsCancellationRequested))
            {
                await RollbackRefreshAsync(transaction).ConfigureAwait(false);
                throw SkillContentServiceException.Unavailable(
                    "The Skill refresh replay timed out or could not obtain a required lock.");
            }
            finally
            {
                if (transaction is not null)
                    await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new ProjectSkillRefreshStart(
            new ProjectSkillRefreshPreparation(request, fingerprint),
            null);
    }

    internal async Task<ProjectSkillRefreshReceipt> PersistRefreshAsync(
        ProjectAuthorizationContext caller,
        ProjectSkillRefreshPreparation preparation,
        ProjectSkillContentRevisionRecord revisionSnapshot,
        ValidatedSkillContent content,
        byte[] reportSetJson,
        string reportSetSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(revisionSnapshot);
        var request = preparation.Request;
        ValidateRefreshRequest(caller, request);
        if (preparation.RequestFingerprint != RefreshRequestFingerprint(caller, request) ||
            reportSetJson is null || reportSetJson.Length is 0 or > 1_048_576 ||
            Hash(reportSetJson) != reportSetSha256)
            throw SkillContentServiceException.Invalid("The prepared Skill refresh is invalid.");

        var requestCancellationToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        deadline.CancelAfter(RefreshPersistenceTimeout);
        cancellationToken = deadline.Token;
        IDbContextTransaction? transaction = null;
        try
        {
            transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                    "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'",
                    cancellationToken)
                .ConfigureAwait(false);
            var fence = await _admission.LockFenceAsync(cancellationToken).ConfigureAwait(false);
            await LockCurrentSkillWriteAuthorityAsync(caller, request.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            var project = await db.Projects
                .FromSqlInterpolated($"""
                    SELECT project_id, tenant_id, created_by_actor_id, name, state, revision,
                           configuration_revision, created_at, updated_at
                    FROM projects_config.projects
                    WHERE project_id = {request.ProjectId}
                      AND tenant_id = {caller.TenantId}
                    FOR SHARE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            if (project.State != ProjectLifecycleState.Active)
                throw ProjectConfigException.Conflict("Archived projects cannot refresh Skill evidence.");
            await RequireAcceptedRefreshAssignmentAsync(request, cancellationToken).ConfigureAwait(false);

            // Read Committed needs this fresh statement after the fence wait; the tracked preparation is not reused.
            var keyEvidence = await db.SkillTrustEvidence.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ProjectId == request.ProjectId &&
                    item.ActorIssuer == caller.Issuer &&
                    item.ActorId == caller.ActorId &&
                    item.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var keyReceipt = keyEvidence is null
                ? null
                : await db.SkillRefreshReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.EvidenceId == keyEvidence.EvidenceId, cancellationToken)
                    .ConfigureAwait(false);
            if (keyEvidence is not null && keyReceipt is null)
                throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
            if (keyReceipt is not null)
            {
                var replay = ReadRefreshReceipt(
                    keyReceipt, caller, request, preparation.RequestFingerprint);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            if (fence.State != ProjectSkillAdmissionFenceState.Active)
                throw SkillAdmissionService.AdmissionUnavailable(
                    "Skill admission is not active for evidence refresh.");
            var policy = await _admission.ReadPolicySnapshotAsync(
                request.ProjectId, lockHeads: true, cancellationToken).ConfigureAwait(false);
            RequireExpectedPolicy(request, policy);

            await LockSkillRevisionAsync(
                request.ProjectId, request.SkillId, request.Revision, cancellationToken)
                .ConfigureAwait(false);
            var current = await db.SkillContentRevisions.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ProjectId == request.ProjectId &&
                    item.SkillId == request.SkillId &&
                    item.Revision == request.Revision &&
                    item.ContentDigest == request.ContentDigest,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            if (!SameSourceMetadata(revisionSnapshot, current))
                throw SkillContentServiceException.Conflict(
                    "The stored Skill source metadata changed during scanning.");
            if (await db.SkillContentRevocations.AsNoTracking().AnyAsync(item =>
                    item.ProjectId == request.ProjectId &&
                    item.SkillId == request.SkillId &&
                    item.Revision == request.Revision,
                    cancellationToken).ConfigureAwait(false))
                throw SkillContentServiceException.Conflict("The selected Skill revision was revoked.");

            var priorAdmission = await db.SkillAdmissions.AsNoTracking()
                .Where(item =>
                    item.ProjectId == request.ProjectId &&
                    item.SkillId == request.SkillId &&
                    item.Revision == request.Revision)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.AdmissionId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw SkillAdmissionService.AdmissionDenied(
                    "The Skill revision has no prior admission classification.");
            if (priorAdmission.Classification == ProjectSkillAdmissionClassification.LegacyThirdParty)
                throw SkillAdmissionService.AdmissionDenied(
                    "The legacy Skill classification is not eligible for refresh.");

            var source = priorAdmission.VerifiedSourceId is not null &&
                Guid.TryParse(priorAdmission.VerifiedSourceId, out var sourceId)
                ? await LockMarketplaceSourceAsync(request.ProjectId, sourceId, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var release = priorAdmission.ReleaseId is { } releaseId
                ? await LockBuiltInReleaseAsync(releaseId, cancellationToken).ConfigureAwait(false)
                : null;
            RequireMatchingRefreshSource(current, priorAdmission, source, release, fence);

            var observedAt = ToPostgresTimestampPrecision(timeProvider.GetUtcNow());
            if (observedAt < priorAdmission.CreatedAt)
                throw SkillContentServiceException.Conflict(
                    "The Skill refresh clock moved backward before persistence.");
            var persistedAt = priorAdmission.CreatedAt >= observedAt
                ? priorAdmission.CreatedAt.AddTicks(10)
                : observedAt;
            var reports = ReadRefreshReports(reportSetJson, reportSetSha256);
            SkillContentScanAdmission.RequirePassingReports(
                CreateRefreshScanInputs(current, content),
                reports,
                DateTimeOffset.MinValue,
                observedAt.AddMicroseconds(1));
            var score = SkillTrustScore.Compute(
                content.Description,
                priorAdmission.Classification == ProjectSkillAdmissionClassification.BuiltInExempt
                    ? release?.SignatureSha256 is not null
                    : false,
                !content.CapabilityRequests.IsEmpty);
            await EvaluateSkillsPolicyAsync(
                    request.ProjectId,
                    policy,
                    score,
                    evidenceComplete: reports.All(item => item.Status == PromptContentScanStatus.Completed),
                    builtInExempt: priorAdmission.Classification ==
                        ProjectSkillAdmissionClassification.BuiltInExempt,
                    cancellationToken)
                .ConfigureAwait(false);

            var evidence = new ProjectSkillTrustEvidenceRecord
            {
                EvidenceId = Guid.NewGuid(),
                ProjectId = current.ProjectId,
                SkillId = current.SkillId,
                Revision = current.Revision,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = preparation.RequestFingerprint,
                ContentDigest = current.ContentDigest,
                SourceId = current.SourceId,
                SourceRevision = current.SourceRevision,
                RequestedRef = current.RequestedRef,
                ResolvedCommitSha = current.ResolvedCommitSha,
                SelectedPath = current.SelectedPath,
                ReportSetJson = reportSetJson,
                ReportSetSha256 = reportSetSha256,
                ScoreStatus = score.Status,
                HeuristicScore = score.Value,
                TrustTier = score.Tier,
                SignatureVerified = score.SignatureVerified,
                CapabilityDeclarationValid = score.CapabilityDeclarationValid,
                ShortDescription = score.ShortDescription,
                CreatedAt = observedAt,
            };
            var admission = new ProjectSkillAdmissionRecord
            {
                AdmissionId = Guid.NewGuid(),
                ProjectId = current.ProjectId,
                SkillId = current.SkillId,
                Revision = current.Revision,
                EvidenceId = evidence.EvidenceId,
                CutoverGeneration = fence.CutoverGeneration,
                Classification = priorAdmission.Classification,
                SourceAuthorityKind = priorAdmission.SourceAuthorityKind,
                VerifiedSourceId = priorAdmission.VerifiedSourceId,
                VerifiedSourceRevision = priorAdmission.VerifiedSourceRevision,
                VerifiedRequestedRef = priorAdmission.VerifiedRequestedRef,
                VerifiedResolvedCommitSha = priorAdmission.VerifiedResolvedCommitSha,
                VerifiedSelectedPath = priorAdmission.VerifiedSelectedPath,
                ReleaseId = release?.ReleaseId,
                TrustedPublisherId = release?.TrustedPublisherId,
                SignatureSha256 = release?.SignatureSha256,
                PlatformPolicyId = policy.PlatformPolicyId,
                PlatformPolicyRevision = policy.PlatformPolicyRevision,
                PlatformPolicySha256 = policy.PlatformPolicySha256,
                ProjectPolicyId = policy.ProjectPolicyId,
                ProjectPolicyRevision = policy.ProjectPolicyRevision,
                ProjectPolicySha256 = policy.ProjectPolicySha256,
                CreatedAt = persistedAt,
            };
            var receipt = new ProjectSkillRefreshReceiptRecord
            {
                ReceiptId = Guid.NewGuid(),
                ProjectId = request.ProjectId,
                RunId = request.RunId,
                AgentId = request.AgentId,
                SkillId = request.SkillId,
                Revision = request.Revision,
                ContentDigest = request.ContentDigest,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = preparation.RequestFingerprint,
                EvidenceId = evidence.EvidenceId,
                AdmissionId = admission.AdmissionId,
                PlatformPolicyId = policy.PlatformPolicyId,
                PlatformPolicyRevision = policy.PlatformPolicyRevision,
                PlatformPolicySha256 = policy.PlatformPolicySha256,
                ProjectPolicyId = policy.ProjectPolicyId,
                ProjectPolicyRevision = policy.ProjectPolicyRevision,
                ProjectPolicySha256 = policy.ProjectPolicySha256,
                ObservedAt = observedAt,
                CreatedAt = observedAt,
            };
            var eligibleRoleGrants = caller.Grants
                .Where(grant =>
                    grant.ResourceType == ProjectAuthorityResourceType.Project &&
                    grant.ResourceId == request.ProjectId &&
                    grant.Role == ProjectAuthorityRole.Owner ||
                    grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
                    grant.ResourceId == caller.TenantId &&
                    grant.Role == ProjectAuthorityRole.TenantAdmin)
                .ToArray();
            var eligibleRoleGrantIds = eligibleRoleGrants
                .Select(grant => grant.AssignmentId)
                .ToArray();
            var activeRoleAssignments = await db.RoleAssignments.AsNoTracking()
                .Where(current =>
                    eligibleRoleGrantIds.Contains(current.AssignmentId) &&
                    current.MembershipId == caller.MembershipId &&
                    current.State == ProjectAuthorityRecordState.Active)
                .Select(current => new
                {
                    current.AssignmentId,
                    current.Revision,
                    current.ResourceType,
                    current.ResourceId,
                    current.Role,
                })
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var checkedRoleRevisions = eligibleRoleGrants
                .Where(grant => activeRoleAssignments.Any(current =>
                    current.AssignmentId == grant.AssignmentId &&
                    current.Revision == grant.Revision &&
                    current.ResourceType == grant.ResourceType &&
                    current.ResourceId == grant.ResourceId &&
                    current.Role == grant.Role))
                .Select(grant => new { grant.AssignmentId, grant.Revision })
                .ToArray();
            if (checkedRoleRevisions.Length == 0)
                throw ProjectConfigException.Forbidden();
            var audit = new ProjectSkillRefreshAuditIntentRecord
            {
                AuditIntentId = Guid.NewGuid(),
                ReceiptId = receipt.ReceiptId,
                EvidenceId = evidence.EvidenceId,
                AdmissionId = admission.AdmissionId,
                ProjectId = request.ProjectId,
                TenantId = caller.TenantId,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                MembershipId = caller.MembershipId,
                MembershipRevision = caller.MembershipRevision,
                EffectiveRoleRevisionsJson = JsonSerializer.Serialize(
                    checkedRoleRevisions,
                    JsonOptions),
                Reason = request.Reason.Trim(),
                CorrelationId = request.CorrelationId,
                CreatedAt = observedAt,
            };
            db.SkillTrustEvidence.Add(evidence);
            db.SkillAdmissions.Add(admission);
            db.SkillRefreshReceipts.Add(receipt);
            db.SkillRefreshAuditIntents.Add(audit);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (AfterSkillRefreshStagedOverride is { } afterStaged)
                await afterStaged(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToRefreshReceipt(receipt, replayed: false);
        }
        catch (Exception exception) when (IsRefreshSerializationFailure(exception))
        {
            await RollbackRefreshAsync(transaction).ConfigureAwait(false);
            throw SkillContentServiceException.Conflict(
                "The Skill refresh conflicted with a concurrent update.");
        }
        catch (DbUpdateException exception) when (IsRefreshKeyViolation(exception))
        {
            await RollbackRefreshAsync(transaction).ConfigureAwait(false);
            throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
        }
        catch (Exception exception) when (
            !requestCancellationToken.IsCancellationRequested &&
            (IsRefreshUnavailableFailure(exception) || deadline.IsCancellationRequested))
        {
            await RollbackRefreshAsync(transaction).ConfigureAwait(false);
            throw SkillContentServiceException.Unavailable(
                "The Skill refresh database operation timed out or could not obtain a required lock.");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<ProjectSkillRefreshReceipt> ResolveRefreshPreparationFailureAsync(
        ProjectAuthorizationContext caller,
        ProjectSkillRefreshPreparation preparation,
        Exception preparationFailure,
        CancellationToken cancellationToken)
    {
        var request = preparation.Request;
        var requestCancellationToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken);
        deadline.CancelAfter(RefreshPersistenceTimeout);
        cancellationToken = deadline.Token;
        IDbContextTransaction? transaction = null;
        Exception? preparationFailureToRethrow = null;
        try
        {
            transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                    "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'",
                    cancellationToken)
                .ConfigureAwait(false);
            var fence = await _admission.LockFenceAsync(cancellationToken).ConfigureAwait(false);
            await LockCurrentSkillWriteAuthorityAsync(caller, request.ProjectId, cancellationToken)
                .ConfigureAwait(false);
            await RequireAcceptedRefreshAssignmentAsync(request, cancellationToken).ConfigureAwait(false);
            var evidence = await db.SkillTrustEvidence.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ProjectId == request.ProjectId &&
                    item.ActorIssuer == caller.Issuer &&
                    item.ActorId == caller.ActorId &&
                    item.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            var receipt = evidence is null
                ? null
                : await db.SkillRefreshReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.EvidenceId == evidence.EvidenceId, cancellationToken)
                    .ConfigureAwait(false);
            if (evidence is not null && receipt is null)
                throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
            if (receipt is not null)
            {
                var replay = ReadRefreshReceipt(
                    receipt, caller, request, preparation.RequestFingerprint);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            transaction = null;
            preparationFailureToRethrow = preparationFailure;
        }
        catch (Exception exception) when (IsRefreshSerializationFailure(exception))
        {
            await RollbackRefreshAsync(transaction).ConfigureAwait(false);
            throw SkillContentServiceException.Conflict(
                "The Skill refresh preparation recovery conflicted with a concurrent update.");
        }
        catch (Exception exception) when (
            !requestCancellationToken.IsCancellationRequested &&
            (IsRefreshUnavailableFailure(exception) || deadline.IsCancellationRequested))
        {
            await RollbackRefreshAsync(transaction).ConfigureAwait(false);
            throw SkillContentServiceException.Unavailable(
                "The Skill refresh preparation recovery timed out or could not obtain a required lock.");
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync().ConfigureAwait(false);
        }
        if (preparationFailureToRethrow is not null)
            ExceptionDispatchInfo.Capture(preparationFailureToRethrow).Throw();
        throw new InvalidOperationException();
    }

    internal async Task<ProjectSkillTrustEvidenceSaveResult> PersistTrustEvidenceAsync(
        ProjectAuthorizationContext caller,
        ProjectSkillContentRevisionRecord revisionSnapshot,
        ValidatedSkillContent content,
        string idempotencyKey,
        string requestFingerprint,
        byte[] reportSetJson,
        string reportSetSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(revisionSnapshot);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        RequireIdempotencyKey(idempotencyKey);
        RequireHash(requestFingerprint);
        RequireHash(reportSetSha256);
        if (reportSetJson is null || reportSetJson.Length is 0 or > 1_048_576 ||
            Hash(reportSetJson) != reportSetSha256)
            throw SkillContentServiceException.Invalid("The Skill trust report set is invalid.");
        await EnsureWriteAccessAsync(caller, revisionSnapshot.ProjectId, cancellationToken).ConfigureAwait(false);

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        var fence = await _admission.LockFenceAsync(cancellationToken).ConfigureAwait(false);
        await LockCurrentSkillWriteAuthorityAsync(
            caller, revisionSnapshot.ProjectId, cancellationToken).ConfigureAwait(false);
        var current = await FindAvailableRevisionAsync(
            revisionSnapshot.ProjectId,
            revisionSnapshot.SkillId,
            revisionSnapshot.Revision,
            revisionSnapshot.ContentDigest,
            cancellationToken).ConfigureAwait(false);
        if (!SameSourceMetadata(revisionSnapshot, current))
            throw SkillContentServiceException.Conflict("The stored Skill source metadata changed during scanning.");

        var existing = await db.SkillTrustEvidence.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ProjectId == current.ProjectId &&
                item.ActorIssuer == caller.Issuer &&
                item.ActorId == caller.ActorId &&
                item.IdempotencyKey == idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (await db.SkillRefreshReceipts.AsNoTracking()
                    .AnyAsync(item => item.EvidenceId == existing.EvidenceId, cancellationToken)
                    .ConfigureAwait(false))
                throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
            if (existing.SkillId != current.SkillId ||
                existing.Revision != current.Revision ||
                existing.ContentDigest != current.ContentDigest ||
                existing.SourceId != current.SourceId ||
                existing.SourceRevision != current.SourceRevision ||
                existing.RequestedRef != current.RequestedRef ||
                existing.ResolvedCommitSha != current.ResolvedCommitSha ||
                existing.SelectedPath != current.SelectedPath ||
                existing.RequestFingerprint != requestFingerprint)
                throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectSkillTrustEvidenceSaveResult(existing, Replayed: true);
        }

        var policy = fence.State == ProjectSkillAdmissionFenceState.Active
            ? await _admission.ReadPolicySnapshotAsync(
                    revisionSnapshot.ProjectId, lockHeads: true, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var release = fence.State == ProjectSkillAdmissionFenceState.Active &&
            current.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified &&
            current.ResolvedCommitSha is not null &&
            current.SelectedPath is not null &&
            current.SourceId is not null &&
            Guid.TryParse(current.SourceId, out var sourceId)
            ? await _admission.FindReleaseAsync(
                    await db.MarketplaceSources
                        .Where(source => source.ProjectId == current.ProjectId &&
                            source.SourceId == sourceId)
                        .Select(source => source.Repository)
                        .SingleAsync(cancellationToken)
                        .ConfigureAwait(false),
                    current.ResolvedCommitSha,
                    current.SelectedPath,
                    current.ContentDigest,
                    fence.ReleaseInventoryRevision!,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var score = SkillTrustScore.Compute(
            content.Description,
            release?.SignatureSha256 is not null,
            !content.CapabilityRequests.IsEmpty);
        if (policy is not null)
            await EvaluateSkillsPolicyAsync(
                    current.ProjectId,
                    policy,
                    score,
                    evidenceComplete: true,
                    builtInExempt: release?.SignatureSha256 is not null,
                    cancellationToken)
                .ConfigureAwait(false);
        var record = new ProjectSkillTrustEvidenceRecord
        {
            EvidenceId = Guid.NewGuid(),
            ProjectId = current.ProjectId,
            SkillId = current.SkillId,
            Revision = current.Revision,
            ActorIssuer = caller.Issuer,
            ActorId = caller.ActorId,
            IdempotencyKey = idempotencyKey,
            RequestFingerprint = requestFingerprint,
            ContentDigest = current.ContentDigest,
            SourceId = current.SourceId,
            SourceRevision = current.SourceRevision,
            RequestedRef = current.RequestedRef,
            ResolvedCommitSha = current.ResolvedCommitSha,
            SelectedPath = current.SelectedPath,
            ReportSetJson = reportSetJson.ToArray(),
            ReportSetSha256 = reportSetSha256,
            ScoreStatus = score.Status,
            HeuristicScore = score.Value,
            TrustTier = score.Tier,
            SignatureVerified = score.SignatureVerified,
            CapabilityDeclarationValid = score.CapabilityDeclarationValid,
            ShortDescription = score.ShortDescription,
            // PostgreSQL stores timestamps at microsecond precision, so retries use one stable decision time.
            CreatedAt = ToPostgresTimestampPrecision(timeProvider.GetUtcNow()),
        };
        if (policy is not null)
        {
            try
            {
                var refreshedReports =
                    JsonSerializer.Deserialize<ImmutableArray<PromptContentScanReportV1>>(
                        reportSetJson, JsonOptions);
                SkillContentScanAdmission.RequirePassingReports(
                    SkillContentScanAdmission.CreateInputs(
                        content,
                        SkillContentScanAdmission.StoredOwnerId(current.ProjectId, current.SkillId),
                        current.Revision,
                        current.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                            ? PromptContentScanProvenance.Git
                            : PromptContentScanProvenance.Authored,
                        current.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                            ? current.SourceRevision
                            : null,
                        current.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                            ? current.ResolvedCommitSha
                            : null),
                    refreshedReports,
                    DateTimeOffset.MinValue,
                    record.CreatedAt.AddMicroseconds(1));
            }
            catch (JsonException)
            {
                throw SkillAdmissionService.AdmissionUnavailable(
                    "The required Skill admission evidence is corrupt.");
            }
        }
        db.SkillTrustEvidence.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (policy is not null)
            {
                var priorAdmission = await db.SkillAdmissions
                    .Where(item => item.ProjectId == current.ProjectId &&
                        item.SkillId == current.SkillId &&
                        item.Revision == current.Revision)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstAsync(cancellationToken).ConfigureAwait(false);
                db.SkillAdmissions.Add(new ProjectSkillAdmissionRecord
                {
                    AdmissionId = Guid.NewGuid(),
                    ProjectId = priorAdmission.ProjectId,
                    SkillId = priorAdmission.SkillId,
                    Revision = priorAdmission.Revision,
                    EvidenceId = record.EvidenceId,
                    CutoverGeneration = fence.CutoverGeneration,
                    Classification = priorAdmission.Classification,
                    SourceAuthorityKind = priorAdmission.SourceAuthorityKind,
                    VerifiedSourceId = priorAdmission.VerifiedSourceId,
                    VerifiedSourceRevision = priorAdmission.VerifiedSourceRevision,
                    VerifiedRequestedRef = priorAdmission.VerifiedRequestedRef,
                    VerifiedResolvedCommitSha = priorAdmission.VerifiedResolvedCommitSha,
                    VerifiedSelectedPath = priorAdmission.VerifiedSelectedPath,
                    ReleaseId = priorAdmission.ReleaseId,
                    TrustedPublisherId = priorAdmission.TrustedPublisherId,
                    SignatureSha256 = priorAdmission.SignatureSha256,
                    PlatformPolicyId = policy.PlatformPolicyId,
                    PlatformPolicyRevision = policy.PlatformPolicyRevision,
                    PlatformPolicySha256 = policy.PlatformPolicySha256,
                    ProjectPolicyId = policy.ProjectPolicyId,
                    ProjectPolicyRevision = policy.ProjectPolicyRevision,
                    ProjectPolicySha256 = policy.ProjectPolicySha256,
                    CreatedAt = priorAdmission.CreatedAt >= record.CreatedAt
                        ? priorAdmission.CreatedAt.AddTicks(10)
                        : record.CreatedAt,
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            await EnsureWriteAccessAsync(caller, current.ProjectId, cancellationToken).ConfigureAwait(false);
            _ = await FindAvailableRevisionAsync(
                current.ProjectId,
                current.SkillId,
                current.Revision,
                current.ContentDigest,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectSkillTrustEvidenceSaveResult(record, Replayed: false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw SkillContentServiceException.Conflict("The Skill trust evidence changed concurrently.");
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw SkillContentServiceException.Conflict("The Skill trust evidence conflicted with a concurrent update.");
        }
    }

    /// <summary>Appends a revocation marker for an existing immutable revision; an already-revoked revision is unchanged.</summary>
    public async Task RevokeAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string skillId,
        long revision,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RequireProjectId(projectId);
        RequireIdentifier(skillId, nameof(skillId));
        if (revision <= 0)
            throw SkillContentServiceException.Invalid("Skill revision must be positive.");
        ValidateReason(reason);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        var fence = await _admission.LockFenceAsync(cancellationToken).ConfigureAwait(false);
        await LockCurrentSkillWriteAuthorityAsync(caller, projectId, cancellationToken)
            .ConfigureAwait(false);
        await LockSkillRevisionAsync(projectId, skillId, revision, cancellationToken)
            .ConfigureAwait(false);
        var content = await db.SkillContentRevisions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.SkillId == skillId && item.Revision == revision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        var existing = await db.SkillContentRevocations.AsNoTracking()
            .AnyAsync(item =>
                item.ProjectId == projectId && item.SkillId == skillId && item.Revision == revision,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        db.SkillContentRevocations.Add(new ProjectSkillContentRevocationRecord
        {
            ProjectId = content.ProjectId,
            SkillId = content.SkillId,
            Revision = content.Revision,
            RevokedByActorId = caller.ActorId,
            Reason = reason.Trim(),
            RevokedAt = timeProvider.GetUtcNow(),
        });
        fence.Generation = checked(fence.Generation + 1);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates one skill assignment by appending the next project configuration revision.</summary>
    public async Task<VersionedProjectConfiguration> UpdateAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillAssignmentUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireProjectId(projectId);
        RequireIdentifier(request.SkillId, nameof(request.SkillId));
        RequireHash(request.ContentDigest);
        if (request.Revision <= 0 || request.Order < 0 || request.AgentIds.IsDefault ||
            request.AgentIds.Length > SkillRuntimeContentContract.MaxSkillCount)
            throw SkillContentServiceException.Invalid("The skill assignment is invalid.");

        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        var current = await configurations.GetProjectConfigurationAsync(
            caller, projectId, revision: null, cancellationToken).ConfigureAwait(false);
        if (current.Revision != request.ExpectedProjectConfigurationRevision)
            throw ProjectConfigException.Conflict("The project configuration revision has changed.");

        var content = await FindAvailableRevisionAsync(
            projectId, request.SkillId, request.Revision, request.ContentDigest, cancellationToken)
            .ConfigureAwait(false);
        var activeAgentIds = current.Configuration.Casting
            .Select(agent => agent.AgentId)
            .ToHashSet(StringComparer.Ordinal);
        if (request.AgentIds.Any(agentId => !activeAgentIds.Contains(agentId)) ||
            request.Enabled && request.AgentIds.IsEmpty)
            throw SkillContentServiceException.Invalid(
                "Enabled skill assignments must target current project agents.");
        var settings = current.Configuration.Skills
            .Where(setting => setting.SkillId != request.SkillId)
            .Append(new SkillCatalogSetting(
                request.SkillId, request.Enabled, request.Order)
            {
                Revision = content.Revision,
                ContentDigest = content.ContentDigest,
                AgentIds = request.AgentIds,
            })
            .OrderBy(setting => setting.Order)
            .ThenBy(setting => setting.SkillId, StringComparer.Ordinal)
            .ToImmutableArray();
        return await configurations.UpdateProjectConfigurationAsync(
            caller,
            projectId,
            request.ExpectedProjectConfigurationRevision,
            // Stored provider overrides are carried forward by the owner, so submit none.
            current.Configuration with { Skills = settings, ProviderOverrides = [], OrderedProviderOverrides = [] },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns only content pinned by the accepted run and assigned to the named active agent, rechecking authority and revocation.
    /// </summary>
    /// <exception cref="SkillContentServiceException">The accepted binding is inconsistent, content is revoked, or storage is unavailable.</exception>
    public async Task<ImmutableArray<SkillRuntimeContentV1>> ReadAcceptedRunSkillsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        string agentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        RequireProjectId(projectId);
        RequireIdentifier(runId, nameof(runId));
        RequireIdentifier(agentId, nameof(agentId));
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireExactRunBinding(projectId, runId);
        await EnsureCurrentRunAuthorityAsync(caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        var selection = await db.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId && item.RunId == runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        var accepted = Deserialize<EffectiveRunSelection>(selection.SnapshotJson);
        if (accepted.ProjectId != projectId || accepted.RunId != runId ||
            accepted.ProjectConfigurationRevision != selection.ProjectConfigurationRevision)
            throw SkillContentServiceException.Conflict("The accepted skill configuration binding is inconsistent.");
        if (!accepted.ProjectConfiguration.Skills.IsDefault &&
            accepted.ProjectConfiguration.Skills.Any(item =>
                item is { Enabled: true } &&
                (item.Revision is null || item.ContentDigest is null || item.AgentIds is null)))
            throw SkillContentServiceException.Conflict(
                "The accepted run contains an enabled skill without an imported revision and agent assignment.");
        var configuration = ProjectConfigurationValidator.Validate(accepted.ProjectConfiguration);
        var activeAgents = configuration.Casting.Select(item => item.AgentId)
            .ToHashSet(StringComparer.Ordinal);
        if (!activeAgents.Contains(agentId))
            throw ProjectConfigException.NotFound();

        var assignments = configuration.Skills
            .Where(item => item.Enabled && item.AgentIds!.Value.Contains(agentId, StringComparer.Ordinal))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.SkillId, StringComparer.Ordinal)
            .ToArray();
        if (assignments.Length == 0)
        {
            await EnsureCurrentRunAuthorityAsync(caller, projectId, runId, cancellationToken)
                .ConfigureAwait(false);
            return [];
        }

        var store = objects ?? throw SkillContentServiceException.Unavailable();
        await using var admissionRead = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        var fence = await _admission.LockFenceAsync(cancellationToken).ConfigureAwait(false);
        var evaluatedAt = timeProvider.GetUtcNow();
        if (fence.State == ProjectSkillAdmissionFenceState.Draining)
            throw SkillAdmissionService.AdmissionUnavailable(
                "Skill runtime admission is draining for cutover.");
        var policy = fence.State == ProjectSkillAdmissionFenceState.Active
            ? await _admission.ReadPolicySnapshotAsync(projectId, lockHeads: true, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var result = ImmutableArray.CreateBuilder<SkillRuntimeContentV1>();
        var admitted = new List<(
            ProjectSkillAdmissionRecord Admission,
            ProjectSkillTrustEvidenceRecord Evidence,
            ValidatedSkillContent Content)>();
        foreach (var assignment in assignments)
        {
            var revision = await FindAvailableRevisionAsync(
                projectId,
                assignment.SkillId,
                assignment.Revision!.Value,
                assignment.ContentDigest!,
                cancellationToken).ConfigureAwait(false);
            var verified = await SkillContentScanAdmission.ReadVerifiedContentAsync(
                store, revision, cancellationToken).ConfigureAwait(false);
            if (policy is null)
            {
                SkillContentScanAdmission.Scan(
                    verified,
                    SkillContentScanAdmission.StoredOwnerId(projectId, revision.SkillId),
                    revision.Revision,
                    timeProvider);
            }
            else
            {
                var admission = await db.SkillAdmissions.AsNoTracking()
                    .Where(item =>
                        item.ProjectId == projectId &&
                        item.SkillId == revision.SkillId &&
                        item.Revision == revision.Revision)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false) ??
                    throw SkillAdmissionService.AdmissionDenied(
                        "The Skill revision has no cutover admission binding.");
                if (admission.CutoverGeneration != fence.CutoverGeneration)
                    throw SkillAdmissionService.AdmissionUnavailable(
                        "The Skill admission fence does not match the current cutover.");
                var evidence = await db.SkillTrustEvidence.AsNoTracking().SingleOrDefaultAsync(
                        item => item.EvidenceId == admission.EvidenceId,
                        cancellationToken)
                    .ConfigureAwait(false) ??
                    throw SkillAdmissionService.AdmissionUnavailable(
                        "The required Skill admission evidence is unavailable.");
                ValidateRuntimeEvidence(admission, evidence, revision, verified, evaluatedAt);
                ProjectMarketplaceSourceRecord? source = null;
                if (admission.VerifiedSourceId is not null &&
                    Guid.TryParse(admission.VerifiedSourceId, out var sourceId))
                    source = await LockMarketplaceSourceAsync(
                            projectId, sourceId, cancellationToken)
                        .ConfigureAwait(false);
                var release = admission.ReleaseId is { } releaseId
                    ? await LockBuiltInReleaseAsync(releaseId, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                var revoked = await db.SkillContentRevocations.AsNoTracking().AnyAsync(item =>
                    item.ProjectId == projectId &&
                    item.SkillId == revision.SkillId &&
                    item.Revision == revision.Revision,
                    cancellationToken).ConfigureAwait(false);
                SkillAdmissionService.RequirePassingAdmission(
                    admission, evidence, policy, source, release, revoked);
                await EvaluateSkillsPolicyAsync(
                        projectId,
                        policy,
                        SkillTrustScore.Compute(
                            verified.Description,
                            evidence.SignatureVerified,
                            evidence.CapabilityDeclarationValid),
                        evidenceComplete: evidence.ReportSetJson.Length > 0,
                        builtInExempt: admission.Classification ==
                            ProjectSkillAdmissionClassification.BuiltInExempt,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireFreshRuntimeEvidence(evidence.CreatedAt, timeProvider.GetUtcNow());
                admitted.Add((admission, evidence, verified));
            }
            await EnsureCurrentRunAuthorityAsync(caller, projectId, runId, cancellationToken)
                .ConfigureAwait(false);
            if (await db.SkillContentRevocations.AsNoTracking().AnyAsync(item =>
                item.ProjectId == projectId && item.SkillId == revision.SkillId &&
                item.Revision == revision.Revision, cancellationToken).ConfigureAwait(false))
                throw SkillContentServiceException.Conflict("An assigned skill revision has been revoked.");
            result.Add(new SkillRuntimeContentV1(
                revision.SkillId,
                revision.Revision,
                verified.Name,
                verified.Description,
                verified.Instructions,
                verified.ContentDigest,
                verified.Resources.Select(resource => new SkillRuntimeContentResourceV1(
                    resource.RelativePath, resource.Content, resource.Sha256)).ToImmutableArray()));
        }

        await EnsureCurrentRunAuthorityAsync(caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (policy is not null)
        {
            var finalPolicy = await _admission.ReadPolicySnapshotAsync(
                    projectId, lockHeads: true, cancellationToken)
                .ConfigureAwait(false);
            if (!SamePolicy(policy, finalPolicy))
                throw SkillContentServiceException.Conflict(
                    "The Skill admission policy changed while projecting the run.");
            foreach (var (admission, evidence, content) in admitted)
            {
                ProjectMarketplaceSourceRecord? source = null;
                if (admission.VerifiedSourceId is not null &&
                    Guid.TryParse(admission.VerifiedSourceId, out var sourceId))
                    source = await LockMarketplaceSourceAsync(
                            projectId, sourceId, cancellationToken)
                        .ConfigureAwait(false);
                var release = admission.ReleaseId is { } releaseId
                    ? await LockBuiltInReleaseAsync(releaseId, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                var revoked = await db.SkillContentRevocations.AsNoTracking().AnyAsync(item =>
                    item.ProjectId == projectId &&
                    item.SkillId == admission.SkillId &&
                    item.Revision == admission.Revision,
                    cancellationToken).ConfigureAwait(false);
                SkillAdmissionService.RequirePassingAdmission(
                    admission, evidence, finalPolicy, source, release, revoked);
                await EvaluateSkillsPolicyAsync(
                        projectId,
                        finalPolicy,
                        SkillTrustScore.Compute(
                            content.Description,
                            evidence.SignatureVerified,
                            evidence.CapabilityDeclarationValid),
                        evidenceComplete: evidence.ReportSetJson.Length > 0,
                        builtInExempt: admission.Classification ==
                            ProjectSkillAdmissionClassification.BuiltInExempt,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireFreshRuntimeEvidence(evidence.CreatedAt, timeProvider.GetUtcNow());
            }
        }
        foreach (var (_, evidence, _) in admitted)
            RequireFreshRuntimeEvidence(evidence.CreatedAt, timeProvider.GetUtcNow());
        await admissionRead.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.ToImmutable();
    }

    private Task<ProjectMarketplaceSourceRecord?> LockMarketplaceSourceAsync(
        string projectId,
        Guid sourceId,
        CancellationToken cancellationToken) =>
        db.MarketplaceSources.FromSqlInterpolated($"""
            SELECT project_id, source_id, name, normalized_name, repository,
                   requested_ref, subpath, revision, state, created_by_actor_id,
                   updated_by_actor_id, created_at, updated_at
            FROM projects_config.marketplace_sources
            WHERE project_id = {projectId}
              AND source_id = {sourceId}
              AND state = 'Active'
            FOR SHARE
            """).SingleOrDefaultAsync(cancellationToken);

    private Task LockSkillRevisionAsync(
        string projectId,
        string skillId,
        long revision,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_catalog.pg_advisory_xact_lock(
                pg_catalog.hashtextextended(
                    {"skill-revision:" + projectId + ":" + skillId + ":" +
                        revision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    0))
            """, cancellationToken);

    private Task LockSkillRevisionForReadAsync(
        string projectId,
        string skillId,
        long revision,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_catalog.pg_advisory_xact_lock_shared(
                pg_catalog.hashtextextended(
                    {"skill-revision:" + projectId + ":" + skillId + ":" +
                        revision.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                    0))
            """, cancellationToken);

    private Task<ProjectSkillBuiltInReleaseRecord?> LockBuiltInReleaseAsync(
        Guid releaseId,
        CancellationToken cancellationToken) =>
        db.SkillBuiltInReleases.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ReleaseId == releaseId, cancellationToken);

    private Task EvaluateSkillsPolicyAsync(
        string projectId,
        SkillAdmissionPolicySnapshot policy,
        SkillTrustScore score,
        bool evidenceComplete,
        bool builtInExempt,
        CancellationToken cancellationToken) =>
        EvaluateSkillsPolicyOverride?.Invoke(
            projectId,
            policy,
            score,
            evidenceComplete,
            builtInExempt,
            cancellationToken) ??
        _admission.EvaluateAsync(
            projectId,
            policy,
            score,
            evidenceComplete,
            builtInExempt,
            cancellationToken);

    private static void ValidateRuntimeEvidence(
        ProjectSkillAdmissionRecord admission,
        ProjectSkillTrustEvidenceRecord evidence,
        ProjectSkillContentRevisionRecord revision,
        ValidatedSkillContent content,
        DateTimeOffset evaluatedAt)
    {
        RequireFreshRuntimeEvidence(evidence.CreatedAt, evaluatedAt);
        if (admission.ProjectId != revision.ProjectId ||
            admission.SkillId != revision.SkillId ||
            admission.Revision != revision.Revision ||
            admission.Classification != revision.AdmissionClassification ||
            admission.SourceAuthorityKind != revision.SourceAuthorityKind ||
            admission.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified &&
            (admission.VerifiedSourceId != revision.SourceId ||
             admission.VerifiedSourceRevision != revision.SourceRevision ||
             admission.VerifiedRequestedRef != revision.RequestedRef ||
             admission.VerifiedResolvedCommitSha != revision.ResolvedCommitSha ||
             admission.VerifiedSelectedPath != revision.SelectedPath) ||
            evidence.ProjectId != revision.ProjectId ||
            evidence.SkillId != revision.SkillId ||
            evidence.Revision != revision.Revision ||
            evidence.ContentDigest != revision.ContentDigest ||
            evidence.SourceId != revision.SourceId ||
            evidence.SourceRevision != revision.SourceRevision ||
            evidence.RequestedRef != revision.RequestedRef ||
            evidence.ResolvedCommitSha != revision.ResolvedCommitSha ||
            evidence.SelectedPath != revision.SelectedPath ||
            evidence.ReportSetSha256 != Hash(evidence.ReportSetJson))
            throw SkillAdmissionService.AdmissionUnavailable(
                "The required Skill admission evidence is corrupt or stale.");
        var recomputedScore = SkillTrustScore.Compute(
            content.Description,
            evidence.SignatureVerified,
            !content.CapabilityRequests.IsEmpty);
        if (evidence.ScoreStatus != recomputedScore.Status ||
            evidence.HeuristicScore != recomputedScore.Value ||
            evidence.TrustTier != recomputedScore.Tier ||
            evidence.CapabilityDeclarationValid != recomputedScore.CapabilityDeclarationValid ||
            evidence.ShortDescription != recomputedScore.ShortDescription)
            throw SkillAdmissionService.AdmissionUnavailable(
                "The required Skill admission score is corrupt or stale.");
        ImmutableArray<PromptContentScanReportV1> reports;
        try
        {
            reports = JsonSerializer.Deserialize<ImmutableArray<PromptContentScanReportV1>>(
                evidence.ReportSetJson, JsonOptions);
        }
        catch (JsonException)
        {
            throw SkillAdmissionService.AdmissionUnavailable(
                "The required Skill admission evidence is corrupt.");
        }
        var inputs = SkillContentScanAdmission.CreateInputs(
            content,
            SkillContentScanAdmission.StoredOwnerId(revision.ProjectId, revision.SkillId),
            revision.Revision,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? PromptContentScanProvenance.Git
                : PromptContentScanProvenance.Authored,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? revision.SourceRevision
                : null,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? revision.ResolvedCommitSha
                : null);
        try
        {
            SkillContentScanAdmission.RequirePassingReports(
                inputs, reports, DateTimeOffset.MinValue, evidence.CreatedAt.AddMicroseconds(1));
        }
        catch (SkillContentServiceException)
        {
            throw SkillAdmissionService.AdmissionUnavailable(
                "The required Skill admission evidence is incomplete or invalid.");
        }
    }

    internal static void RequireFreshRuntimeEvidence(
        DateTimeOffset createdAt,
        DateTimeOffset evaluatedAt)
    {
        if (createdAt > evaluatedAt ||
            evaluatedAt - createdAt > MaximumSkillEvidenceAge)
            throw SkillAdmissionService.AdmissionUnavailable(
                "The required Skill admission evidence is stale.");
    }

    private static bool SamePolicy(
        SkillAdmissionPolicySnapshot left,
        SkillAdmissionPolicySnapshot right) =>
        left.PlatformPolicyId == right.PlatformPolicyId &&
        left.PlatformPolicyRevision == right.PlatformPolicyRevision &&
        left.PlatformPolicySha256 == right.PlatformPolicySha256 &&
        left.ProjectPolicyId == right.ProjectPolicyId &&
        left.ProjectPolicyRevision == right.ProjectPolicyRevision &&
        left.ProjectPolicySha256 == right.ProjectPolicySha256 &&
        left.MinimumScore == right.MinimumScore &&
        left.RequireCompletedEvidence == right.RequireCompletedEvidence;

    /// <summary>Validates enabled skill pins and assignments against the active cast and non-revoked stored revisions.</summary>
    internal static async Task<ProjectConfiguration> ValidateConfigurationAsync(
        ProjectsConfigDbContext db,
        string projectId,
        ProjectConfiguration previous,
        ProjectConfiguration configuration,
        SkillContentObjectStore? skillContentObjects,
        TimeProvider timeProvider,
        Agentweaver.Policy.Acs.NativeAcsPolicyEvaluator? policyEvaluator,
        CancellationToken cancellationToken)
    {
        var normalized = ProjectConfigurationValidator.ValidateTransition(previous, configuration);
        var admissionService = new SkillAdmissionService(
            db, timeProvider, evaluator: policyEvaluator);
        ProjectSkillAdmissionFenceRecord? fence = null;
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fenceExists = new NpgsqlCommand(
                "SELECT to_regclass('projects_config.project_skill_admission_fence') IS NOT NULL",
                connection,
                transaction);
            if (await fenceExists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
                fence = await admissionService.LockFenceAsync(cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            if (opened)
                await connection.CloseAsync().ConfigureAwait(false);
        }
        if (fence?.State == ProjectSkillAdmissionFenceState.Draining)
            throw SkillAdmissionService.AdmissionUnavailable(
                "Skill runtime admission is draining for cutover.");
        var policy = fence?.State == ProjectSkillAdmissionFenceState.Active
            ? await admissionService.ReadPolicySnapshotAsync(
                    projectId, lockHeads: true, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var activeAgentIds = normalized.Casting
            .Select(item => item.AgentId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var assignment in normalized.Skills.Where(item => item.Enabled))
        {
            if (assignment.Revision is null)
                continue;
            if (assignment.ContentDigest is null || assignment.AgentIds is null)
                throw SkillContentServiceException.Invalid(
                    "Enabled skills must pin an imported revision, content digest, and agent assignment.");
            if (assignment.AgentIds.Value.IsEmpty ||
                assignment.AgentIds.Value.Any(agentId => !activeAgentIds.Contains(agentId)))
                throw SkillContentServiceException.Invalid(
                    "Enabled skill assignments must target current project agents.");
            var revision = await FindAvailableRevisionAsync(
                db,
                projectId,
                assignment.SkillId,
                assignment.Revision.Value,
                assignment.ContentDigest,
                cancellationToken).ConfigureAwait(false);
            ValidatedSkillContent verified;
            try
            {
                verified = await SkillContentScanAdmission.ReadVerifiedContentAsync(
                    skillContentObjects, revision, cancellationToken).ConfigureAwait(false);
            }
            catch (SkillContentObjectStoreException)
            {
                throw SkillContentScanAdmission.UnavailableException();
            }
            SkillContentScanAdmission.Scan(
                verified,
                SkillContentScanAdmission.StoredOwnerId(projectId, revision.SkillId),
                revision.Revision,
                timeProvider);
            if (policy is not null)
            {
                var admission = await db.SkillAdmissions.AsNoTracking()
                    .Where(item => item.ProjectId == projectId &&
                        item.SkillId == revision.SkillId &&
                        item.Revision == revision.Revision)
                    .OrderByDescending(item => item.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false) ??
                    throw SkillAdmissionService.AdmissionDenied(
                        "The Skill revision has no cutover admission binding.");
                var evidence = await db.SkillTrustEvidence.AsNoTracking().SingleOrDefaultAsync(
                        item => item.EvidenceId == admission.EvidenceId,
                        cancellationToken)
                    .ConfigureAwait(false) ??
                    throw SkillAdmissionService.AdmissionUnavailable(
                        "The required Skill admission evidence is unavailable.");
                ValidateRuntimeEvidence(
                    admission, evidence, revision, verified, timeProvider.GetUtcNow());
                ProjectMarketplaceSourceRecord? source = null;
                if (admission.VerifiedSourceId is not null &&
                    Guid.TryParse(admission.VerifiedSourceId, out var sourceId))
                    source = await db.MarketplaceSources.AsNoTracking().SingleOrDefaultAsync(
                        item => item.ProjectId == projectId &&
                            item.SourceId == sourceId &&
                            item.State == ProjectMarketplaceSourceState.Active,
                        cancellationToken).ConfigureAwait(false);
                var release = admission.ReleaseId is { } releaseId
                    ? await db.SkillBuiltInReleases.AsNoTracking().SingleOrDefaultAsync(
                        item => item.ReleaseId == releaseId,
                        cancellationToken).ConfigureAwait(false)
                    : null;
                var revoked = await db.SkillContentRevocations.AsNoTracking().AnyAsync(
                    item => item.ProjectId == projectId &&
                        item.SkillId == revision.SkillId &&
                        item.Revision == revision.Revision,
                    cancellationToken).ConfigureAwait(false);
                SkillAdmissionService.RequirePassingAdmission(
                    admission, evidence, policy, source, release, revoked);
                await admissionService.EvaluateAsync(
                        projectId,
                        policy,
                        SkillTrustScore.Compute(
                            verified.Description,
                            evidence.SignatureVerified,
                            evidence.CapabilityDeclarationValid),
                        evidenceComplete: true,
                        builtInExempt: admission.Classification ==
                            ProjectSkillAdmissionClassification.BuiltInExempt,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireFreshRuntimeEvidence(evidence.CreatedAt, timeProvider.GetUtcNow());
            }
        }
        return normalized;
    }

    /// <summary>Requires current membership, active project state, and an active Owner or tenant-admin assignment.</summary>
    private async Task EnsureWriteAccessAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        caller.RequireResourceBinding(projectId);
        var currentMembership = await db.TenantMemberships.AsNoTracking().AnyAsync(
            membership =>
                membership.MembershipId == caller.MembershipId &&
                membership.Issuer == caller.Issuer &&
                membership.Subject == caller.ActorId &&
                membership.TenantId == caller.TenantId &&
                membership.Revision == caller.MembershipRevision &&
                membership.State == ProjectAuthorityRecordState.Active,
            cancellationToken).ConfigureAwait(false);
        if (!currentMembership)
            throw ProjectConfigException.Forbidden();
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null || project.TenantId != caller.TenantId)
            throw ProjectConfigException.NotFound();
        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot change skill content.");
        if (!await HasCurrentRoleAsync(
            caller, ProjectAuthorityResourceType.Tenant, caller.TenantId,
            ProjectAuthorityRole.TenantAdmin, cancellationToken).ConfigureAwait(false) &&
            !await HasCurrentRoleAsync(
                caller, ProjectAuthorityResourceType.Project, projectId,
                ProjectAuthorityRole.Owner, cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
    }

    /// <summary>Requires current membership and Owner or tenant-admin authority while allowing archived-project inventory reads.</summary>
    private async Task EnsureInventoryAccessAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        caller.RequireResourceBinding(projectId);
        var currentMembership = await db.TenantMemberships.AsNoTracking().AnyAsync(
            membership =>
                membership.MembershipId == caller.MembershipId &&
                membership.Issuer == caller.Issuer &&
                membership.Subject == caller.ActorId &&
                membership.TenantId == caller.TenantId &&
                membership.Revision == caller.MembershipRevision &&
                membership.State == ProjectAuthorityRecordState.Active,
            cancellationToken).ConfigureAwait(false);
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (!currentMembership || project is null || project.TenantId != caller.TenantId)
            throw ProjectConfigException.NotFound();
        if (!await HasCurrentRoleAsync(
            caller, ProjectAuthorityResourceType.Tenant, caller.TenantId,
            ProjectAuthorityRole.TenantAdmin, cancellationToken).ConfigureAwait(false) &&
            !await HasCurrentRoleAsync(
                caller, ProjectAuthorityResourceType.Project, projectId,
                ProjectAuthorityRole.Owner, cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
    }

    /// <summary>Rechecks current Orchestrator role and exact accepted run existence before returning runtime bytes.</summary>
    private async Task EnsureCurrentRunAuthorityAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var currentMembership = await db.TenantMemberships.AsNoTracking().AnyAsync(
            membership =>
                membership.MembershipId == caller.MembershipId &&
                membership.Issuer == caller.Issuer &&
                membership.Subject == caller.ActorId &&
                membership.TenantId == caller.TenantId &&
                membership.Revision == caller.MembershipRevision &&
                membership.State == ProjectAuthorityRecordState.Active,
            cancellationToken).ConfigureAwait(false);
        var currentProject = await db.Projects.AsNoTracking().AnyAsync(
            project => project.ProjectId == projectId && project.TenantId == caller.TenantId,
            cancellationToken).ConfigureAwait(false);
        if (!currentMembership || !currentProject ||
            !await HasCurrentRoleAsync(
                caller, ProjectAuthorityResourceType.Project, projectId,
                ProjectAuthorityRole.Orchestrator, cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        if (!await db.RunSelections.AsNoTracking().AnyAsync(
            selection => selection.ProjectId == projectId && selection.RunId == runId,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.NotFound();
    }

    private async Task<bool> HasCurrentRoleAsync(
        ProjectAuthorizationContext caller,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role,
        CancellationToken cancellationToken) =>
        await (
            from membership in db.TenantMemberships.AsNoTracking()
            join assignment in db.RoleAssignments.AsNoTracking()
                on membership.MembershipId equals assignment.MembershipId
            where membership.MembershipId == caller.MembershipId &&
                membership.Issuer == caller.Issuer &&
                membership.Subject == caller.ActorId &&
                membership.TenantId == caller.TenantId &&
                membership.Revision == caller.MembershipRevision &&
                membership.State == ProjectAuthorityRecordState.Active &&
                assignment.ResourceType == resourceType &&
                assignment.ResourceId == resourceId &&
                assignment.Role == role &&
                assignment.State == ProjectAuthorityRecordState.Active
            select assignment.AssignmentId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

    private async Task<ProjectSkillContentRevisionRecord> FindAvailableRevisionAsync(
        string projectId,
        string skillId,
        long revision,
        string contentDigest,
        CancellationToken cancellationToken) =>
        await FindAvailableRevisionAsync(db, projectId, skillId, revision, contentDigest, cancellationToken)
            .ConfigureAwait(false);

    private async Task<ProjectSkillContentRevisionRecord> FindAvailableRevisionAsync(
        string projectId,
        string skillId,
        long revision,
        CancellationToken cancellationToken) =>
        await FindAvailableRevisionAsync(db, projectId, skillId, revision, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Loads the exact digest-pinned revision and rejects it if a revocation marker exists.</summary>
    private static async Task<ProjectSkillContentRevisionRecord> FindAvailableRevisionAsync(
        ProjectsConfigDbContext db,
        string projectId,
        string skillId,
        long revision,
        string contentDigest,
        CancellationToken cancellationToken)
    {
        var stored = await FindAvailableRevisionAsync(db, projectId, skillId, revision, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(stored.ContentDigest, contentDigest, StringComparison.Ordinal))
            throw ProjectConfigException.NotFound();
        return stored;
    }

    private static async Task<ProjectSkillContentRevisionRecord> FindAvailableRevisionAsync(
        ProjectsConfigDbContext db,
        string projectId,
        string skillId,
        long revision,
        CancellationToken cancellationToken)
    {
        var stored = await db.SkillContentRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ProjectId == projectId && item.SkillId == skillId && item.Revision == revision,
                cancellationToken).ConfigureAwait(false);
        if (stored is null)
            throw ProjectConfigException.NotFound();
        if (await db.SkillContentRevocations.AsNoTracking().AnyAsync(item =>
            item.ProjectId == projectId && item.SkillId == skillId && item.Revision == revision,
            cancellationToken).ConfigureAwait(false))
            throw SkillContentServiceException.Conflict("The selected skill revision has been revoked.");
        return stored;
    }

    private static bool SameSourceMetadata(
        ProjectSkillContentRevisionRecord left,
        ProjectSkillContentRevisionRecord right) =>
        left.ProjectId == right.ProjectId &&
        left.SkillId == right.SkillId &&
        left.Revision == right.Revision &&
        left.ContentDigest == right.ContentDigest &&
        left.SourceId == right.SourceId &&
        left.SourceRevision == right.SourceRevision &&
        left.RequestedRef == right.RequestedRef &&
        left.ResolvedCommitSha == right.ResolvedCommitSha &&
        left.SelectedPath == right.SelectedPath;

    private static bool IsRefreshKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        postgres.ConstraintName is
            "ux_project_skill_trust_evidence_idempotency" or
            "ux_project_skill_refresh_receipts_idempotency";

    private static bool IsRefreshSerializationFailure(Exception exception) =>
        FindPostgresException(exception) is { } postgres &&
        postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private static bool IsRefreshUnavailableFailure(Exception exception) =>
        HasTimeoutException(exception) ||
        exception is OperationCanceledException ||
        FindPostgresException(exception) is
        {
            SqlState: PostgresErrorCodes.LockNotAvailable or
                PostgresErrorCodes.QueryCanceled or
                "55P03"
        };

    private static bool HasTimeoutException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is TimeoutException)
                return true;
        return false;
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres)
                return postgres;
        return null;
    }

    private static async Task RollbackRefreshAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null)
            return;
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>Returns a saved import receipt only when actor, key, and complete request digest all match.</summary>
    private static SkillContentImportReceipt ReadIdempotentReceipt(
        ProjectSkillImportIdempotencyRecord existing,
        ProjectAuthorizationContext caller,
        SkillContentImportRequest request,
        string requestDigest)
    {
        if (existing.ActorIssuer != caller.Issuer || existing.ActorId != caller.ActorId ||
            existing.IdempotencyKey != request.IdempotencyKey ||
            existing.RequestDigest != requestDigest)
            throw SkillContentServiceException.IdempotencyConflict();
        return JsonSerializer.Deserialize<SkillContentImportReceipt>(existing.ReceiptJson, JsonOptions)
            ?? throw new InvalidOperationException("The stored skill import receipt could not be read.");
    }

    // Bind retries to the imported content pin and source, not only to the idempotency key.
    private static string ImportRequestDigest(SkillContentImportRequest request) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.SkillId,
            request.ExpectedRevision,
            request.ExpectedContentDigest,
            request.Source,
        }, JsonOptions));

    // Scope keys to one project and actor so a receipt cannot be replayed across either boundary.
    private static string IdempotencyScopeDigest(
        ProjectAuthorizationContext caller,
        string projectId,
        string key) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            projectId,
            caller.Issuer,
            caller.ActorId,
            key,
        }));

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static DateTimeOffset ToPostgresTimestampPrecision(DateTimeOffset value)
    {
        var utcTicks = value.UtcDateTime.Ticks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }

    /// <summary>Encodes versioned page-order, project, size, and last-row values into an opaque URL-safe cursor.</summary>
    private static string EncodeInventoryCursor(string projectId, int pageSize, string skillId, long revision)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new SkillInventoryCursor(
                InventoryCursorVersion, InventoryCursorOrder, projectId, pageSize, skillId, revision),
            JsonOptions);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Rejects malformed or cross-project/page-size cursors before applying the stable keyset query.</summary>
    private static SkillInventoryCursor DecodeInventoryCursor(string projectId, int pageSize, string value)
    {
        if (value.Length is 0 or > 2048)
            throw SkillContentServiceException.Invalid("The skill inventory cursor is invalid.");
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw SkillContentServiceException.Invalid("The skill inventory cursor is invalid."),
            };
            var cursor = JsonSerializer.Deserialize<SkillInventoryCursor>(
                Convert.FromBase64String(base64), JsonOptions);
            if (cursor is null ||
                cursor.Version != InventoryCursorVersion ||
                cursor.Order != InventoryCursorOrder ||
                cursor.ProjectId != projectId ||
                cursor.PageSize != pageSize ||
                cursor.Revision <= 0)
                throw SkillContentServiceException.Invalid("The skill inventory cursor is invalid.");
            RequireIdentifier(cursor.SkillId, nameof(cursor.SkillId));
            return cursor;
        }
        catch (FormatException)
        {
            throw SkillContentServiceException.Invalid("The skill inventory cursor is invalid.");
        }
        catch (JsonException)
        {
            throw SkillContentServiceException.Invalid("The skill inventory cursor is invalid.");
        }
    }

    internal static long SemanticByteCount(ValidatedSkillContent content)
    {
        var utf8 = new UTF8Encoding(false, true);
        return checked((long)utf8.GetByteCount(content.Name) +
            utf8.GetByteCount(content.Description) +
            utf8.GetByteCount(content.Instructions) +
            content.Resources.Sum(resource => (long)resource.Content.Length));
    }

    private static ObjectKey MakeObjectKey(string projectId, string skillId, long revision, string digest) =>
        new($"projects/{projectId}/skills/{skillId}/revisions/{revision}/{digest}.json");

    private static void ValidateSource(SkillContentSourceSelection? source)
    {
        if (source is null)
            return;
        ValidateBoundedText(source.SourceId, 256, "SourceId");
        ValidateBoundedText(source.SourceRevision, 256, "SourceRevision");
        ValidateBoundedText(source.RequestedRef, 256, "RequestedRef");
        if (source.ResolvedCommitSha is not { Length: 40 or 64 } commit ||
            commit.Any(character => !Uri.IsHexDigit(character)))
            throw SkillContentServiceException.Invalid("A resolved source commit SHA is required.");
        if (source.SelectedPath != string.Empty)
        {
            try
            {
                if (SkillRuntimeContentContract.NormalizeResourcePath(source.SelectedPath) != source.SelectedPath)
                    throw SkillContentServiceException.Invalid("The selected source path is not normalized.");
            }
            catch (ArgumentException exception)
            {
                throw SkillContentServiceException.Invalid(exception.Message);
            }
        }
    }

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000 ||
            reason.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw SkillContentServiceException.Invalid("A bounded human-readable revocation reason is required.");
    }

    private static void ValidateRefreshRequest(
        ProjectAuthorizationContext caller,
        ProjectSkillRefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        RequireProjectId(request.ProjectId);
        RequireIdentifier(request.RunId, nameof(request.RunId));
        RequireIdentifier(request.AgentId, nameof(request.AgentId));
        RequireIdentifier(request.SkillId, nameof(request.SkillId));
        if (request.Revision <= 0)
            throw SkillContentServiceException.Invalid("Skill revision must be positive.");
        RequireHash(request.ContentDigest);
        RequireIdempotencyKey(request.IdempotencyKey);
        ValidateReason(request.Reason);
        RequireIdentifier(request.CorrelationId, nameof(request.CorrelationId));
        if (request.CorrelationId.Length > 128)
            throw SkillContentServiceException.Invalid("The correlation identifier is too long.");
        ValidatePolicyPin(request.ExpectedPlatformPolicy, nameof(request.ExpectedPlatformPolicy));
        if (request.ExpectedProjectPolicy is not null)
            ValidatePolicyPin(request.ExpectedProjectPolicy, nameof(request.ExpectedProjectPolicy));
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        caller.RequireResourceBinding(request.ProjectId);
    }

    private static void ValidatePolicyPin(ProjectSkillRefreshPolicyPin pin, string name)
    {
        if (pin is null || string.IsNullOrWhiteSpace(pin.PolicyId) || pin.PolicyId.Length > 256 ||
            pin.Revision <= 0)
            throw SkillContentServiceException.Invalid($"'{name}' is invalid.");
        RequireHash(pin.ContentSha256);
    }

    private string RefreshRequestFingerprint(
        ProjectAuthorizationContext caller,
        ProjectSkillRefreshRequest request) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                Operation = "project-skill-refresh",
                Version = 1,
                TenantId = caller.TenantId,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                request.ProjectId,
                request.RunId,
                request.AgentId,
                request.SkillId,
                request.Revision,
                request.ContentDigest,
                request.ExpectedPlatformPolicy,
                request.ExpectedProjectPolicy,
                request.IdempotencyKey,
                request.Reason,
                request.CorrelationId,
            },
            JsonOptions));

    private async Task RequireAcceptedRefreshAssignmentAsync(
        ProjectSkillRefreshRequest request,
        CancellationToken cancellationToken)
    {
        var selection = await db.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ProjectId == request.ProjectId && item.RunId == request.RunId,
                cancellationToken)
            .ConfigureAwait(false);
        if (selection is null)
            throw ProjectConfigException.NotFound();
        var accepted = Deserialize<EffectiveRunSelection>(selection.SnapshotJson);
        if (accepted.ProjectId != request.ProjectId ||
            accepted.RunId != request.RunId ||
            accepted.ProjectConfigurationRevision != selection.ProjectConfigurationRevision)
            throw SkillContentServiceException.Conflict(
                "The accepted Skill configuration binding is inconsistent.");
        var configuration = ProjectConfigurationValidator.Validate(accepted.ProjectConfiguration);
        if (!configuration.Casting.Any(agent => agent.AgentId == request.AgentId))
            throw ProjectConfigException.NotFound();
        var assignments = configuration.Skills
            .Where(item =>
                item.Enabled &&
                item.AgentIds is { } agentIds &&
                agentIds.Contains(request.AgentId, StringComparer.Ordinal))
            .OrderBy(item => item.Order)
            .ThenBy(item => item.SkillId, StringComparer.Ordinal)
            .ToArray();
        if (assignments.Length > SkillRuntimeContentContract.MaxSkillCount ||
            assignments.Count(item =>
                item.SkillId == request.SkillId &&
                item.Revision == request.Revision &&
                item.ContentDigest == request.ContentDigest) != 1)
            throw ProjectConfigException.NotFound();
    }

    private async Task LockCurrentSkillWriteAuthorityAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using (var command = new NpgsqlCommand(
            """
            SELECT projects_config.lock_casting_authority(
                @membership_id, @issuer, @subject, @tenant_id, @membership_revision, @project_id, true)
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("membership_id", caller.MembershipId);
            command.Parameters.AddWithValue("issuer", caller.Issuer);
            command.Parameters.AddWithValue("subject", caller.ActorId);
            command.Parameters.AddWithValue("tenant_id", caller.TenantId);
            command.Parameters.AddWithValue("membership_revision", caller.MembershipRevision);
            command.Parameters.AddWithValue("project_id", projectId);
            if (!string.Equals(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string,
                    "authorized",
                    StringComparison.Ordinal))
                throw ProjectConfigException.Forbidden();
        }
        var eligibleGrantIds = caller.Grants
            .Where(grant =>
                grant.ResourceType == ProjectAuthorityResourceType.Project &&
                grant.ResourceId == projectId &&
                grant.Role == ProjectAuthorityRole.Owner ||
                grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
                grant.ResourceId == caller.TenantId &&
                grant.Role == ProjectAuthorityRole.TenantAdmin)
            .Select(grant => grant.AssignmentId)
            .ToArray();
        var locked = await db.RoleAssignments.AsNoTracking()
            .Where(grant =>
                eligibleGrantIds.Contains(grant.AssignmentId) &&
                grant.MembershipId == caller.MembershipId &&
                grant.State == ProjectAuthorityRecordState.Active)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!caller.Grants.Any(grant =>
                eligibleGrantIds.Contains(grant.AssignmentId) &&
                locked.Any(current =>
                    current.AssignmentId == grant.AssignmentId &&
                    current.Revision == grant.Revision &&
                    current.ResourceType == grant.ResourceType &&
                    current.ResourceId == grant.ResourceId &&
                    current.Role == grant.Role)))
            throw ProjectConfigException.Forbidden();
    }

    private static ProjectSkillRefreshReceipt ReadRefreshReceipt(
        ProjectSkillRefreshReceiptRecord receipt,
        ProjectAuthorizationContext caller,
        ProjectSkillRefreshRequest request,
        string fingerprint)
    {
        if (receipt.ActorIssuer != caller.Issuer ||
            receipt.ActorId != caller.ActorId ||
            receipt.ProjectId != request.ProjectId ||
            receipt.RunId != request.RunId ||
            receipt.AgentId != request.AgentId ||
            receipt.SkillId != request.SkillId ||
            receipt.Revision != request.Revision ||
            receipt.ContentDigest != request.ContentDigest ||
            receipt.IdempotencyKey != request.IdempotencyKey ||
            receipt.RequestFingerprint != fingerprint)
            throw SkillContentServiceException.TrustEvidenceIdempotencyConflict();
        return ToRefreshReceipt(receipt, replayed: true);
    }

    private static ProjectSkillRefreshReceipt ToRefreshReceipt(
        ProjectSkillRefreshReceiptRecord receipt,
        bool replayed) =>
        new(
            receipt.EvidenceId,
            receipt.AdmissionId,
            new ProjectSkillRefreshPolicyPin(
                receipt.PlatformPolicyId,
                receipt.PlatformPolicyRevision,
                receipt.PlatformPolicySha256),
            receipt.ProjectPolicyId is null
                ? null
                : new ProjectSkillRefreshPolicyPin(
                    receipt.ProjectPolicyId,
                    receipt.ProjectPolicyRevision!.Value,
                    receipt.ProjectPolicySha256!),
            receipt.ObservedAt,
            replayed);

    private static void RequireExpectedPolicy(
        ProjectSkillRefreshRequest request,
        SkillAdmissionPolicySnapshot policy)
    {
        if (request.ExpectedPlatformPolicy.PolicyId != policy.PlatformPolicyId ||
            request.ExpectedPlatformPolicy.Revision != policy.PlatformPolicyRevision ||
            request.ExpectedPlatformPolicy.ContentSha256 != policy.PlatformPolicySha256 ||
            !EqualsPolicyPin(request.ExpectedProjectPolicy, policy))
            throw SkillContentServiceException.Conflict("The expected Skills policy pin changed.");
    }

    private static bool EqualsPolicyPin(
        ProjectSkillRefreshPolicyPin? expected,
        SkillAdmissionPolicySnapshot policy) =>
        expected is null
            ? policy.ProjectPolicyId is null &&
              policy.ProjectPolicyRevision is null &&
              policy.ProjectPolicySha256 is null
            : expected.PolicyId == policy.ProjectPolicyId &&
              expected.Revision == policy.ProjectPolicyRevision &&
              expected.ContentSha256 == policy.ProjectPolicySha256;

    private static void RequireMatchingRefreshSource(
        ProjectSkillContentRevisionRecord current,
        ProjectSkillAdmissionRecord prior,
        ProjectMarketplaceSourceRecord? source,
        ProjectSkillBuiltInReleaseRecord? release,
        ProjectSkillAdmissionFenceRecord fence)
    {
        if (prior.SourceAuthorityKind != current.SourceAuthorityKind ||
            current.AdmissionClassification != prior.Classification)
            throw SkillContentServiceException.Conflict("The stored Skill source binding changed.");
        if (current.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.CallerClaimed)
        {
            if (source is not null || release is not null)
                throw SkillContentServiceException.Conflict("The caller-claimed Skill source binding changed.");
            return;
        }
        if (prior.VerifiedSourceId != current.SourceId ||
            prior.VerifiedSourceRevision != current.SourceRevision ||
            prior.VerifiedRequestedRef != current.RequestedRef ||
            prior.VerifiedResolvedCommitSha != current.ResolvedCommitSha ||
            prior.VerifiedSelectedPath != current.SelectedPath)
            throw SkillContentServiceException.Conflict("The stored Skill source proof changed.");
        if (source is null ||
            source.SourceId.ToString("D") != current.SourceId ||
            source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) !=
                current.SourceRevision ||
            source.State != ProjectMarketplaceSourceState.Active ||
            source.RequestedRef != current.RequestedRef)
            throw SkillContentServiceException.Conflict("The verified Skill source is no longer active.");
        if (prior.Classification == ProjectSkillAdmissionClassification.BuiltInExempt)
        {
            if (release is null ||
                !release.IsActive ||
                release.InventoryRevision != fence.ReleaseInventoryRevision ||
                release.Repository != source.Repository ||
                release.ResolvedCommitSha != current.ResolvedCommitSha ||
                release.SelectedPath != current.SelectedPath ||
                release.ContentDigest != current.ContentDigest ||
                release.TrustedPublisherId != prior.TrustedPublisherId ||
                release.SignatureSha256 != prior.SignatureSha256 ||
                string.IsNullOrWhiteSpace(release.SignatureSha256))
                throw SkillContentServiceException.Conflict(
                    "The verified Skill release proof is no longer active.");
        }
    }

    private static ImmutableArray<PromptContentScanInput> CreateRefreshScanInputs(
        ProjectSkillContentRevisionRecord revision,
        ValidatedSkillContent content) =>
        SkillContentScanAdmission.CreateInputs(
            content,
            SkillContentScanAdmission.StoredOwnerId(revision.ProjectId, revision.SkillId),
            revision.Revision,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? PromptContentScanProvenance.Git
                : PromptContentScanProvenance.Authored,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? revision.SourceRevision
                : null,
            revision.SourceAuthorityKind == ProjectSkillSourceAuthorityKind.MarketplaceVerified
                ? revision.ResolvedCommitSha
                : null);

    private static ImmutableArray<PromptContentScanReportV1> ReadRefreshReports(
        byte[] reportSetJson,
        string reportSetSha256)
    {
        if (Hash(reportSetJson) != reportSetSha256)
            throw SkillAdmissionService.AdmissionUnavailable(
                "The prepared Skill admission evidence hash is invalid.");
        try
        {
            return JsonSerializer.Deserialize<ImmutableArray<PromptContentScanReportV1>>(
                reportSetJson, JsonOptions);
        }
        catch (JsonException)
        {
            throw SkillAdmissionService.AdmissionUnavailable(
                "The prepared Skill admission evidence is corrupt.");
        }
    }

    private static void RequireProjectId(string projectId)
    {
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
    }

    private static void RequireIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw SkillContentServiceException.Invalid($"'{name}' must be an opaque identifier.");
    }

    private static void RequireIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw SkillContentServiceException.Invalid("A valid import idempotency key is required.");
    }

    private static void RequireHash(string value)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw SkillContentServiceException.Invalid("A lowercase SHA-256 digest is required.");
    }

    private static void ValidateBoundedText(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength ||
            value.Any(char.IsControl))
            throw SkillContentServiceException.Invalid($"'{name}' must be bounded text without control characters.");
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation;

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Stored Projects & Config JSON could not be read as {typeof(T).Name}.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        // Strict parsing makes mismatched stored contracts fail instead of silently dropping unknown fields.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record SkillInventoryCursor(
        int Version,
        string Order,
        string ProjectId,
        int PageSize,
        string SkillId,
        long Revision);
}
