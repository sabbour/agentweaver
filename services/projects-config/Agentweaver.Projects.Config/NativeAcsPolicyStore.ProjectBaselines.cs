using System.Data;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal sealed partial class NativeAcsPolicyStore
{
    private const string BaselinePolicyId = "canonical";

    internal async Task<NativeAcsProjectPolicyBaselineReceipt> CreateBaselineApprovalAsync(
        ProjectAuthorizationContext caller,
        NativeAcsProjectPolicyBaselineApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireHumanActor();
        ValidateBaselineWriteMetadata(request.IdempotencyKey, request.Reason, request.CorrelationId);
        if (!Guid.TryParseExact(request.ProjectId, "N", out _))
            throw ProjectConfigException.NotFound();
        ValidateProjectBaselineArtifact(request.Artifact);

        var roleGrants = GetAuthorizingGrants(caller, NativeAcsPolicyScopeType.Project, request.ProjectId);
        var scopeDigest = ComputeBaselineScopeDigest("approval", caller.TenantId, request.ProjectId, caller.Issuer, caller.ActorId, request.IdempotencyKey);
        var requestDigest = ComputeBaselineApprovalRequestDigest(caller.TenantId, request.ProjectId, request.Artifact.ContentSha256, request.Reason, request.CorrelationId);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);
        try
        {
            await SetBaselineTimeoutsAsync(db, ct).ConfigureAwait(false);
            _ = await new SkillAdmissionService(db, timeProvider).LockFenceAsync(ct).ConfigureAwait(false);
            var authorizingGrant = await LockAndCheckAuthorityAsync(
                db,
                caller,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project,
                    request.ProjectId,
                    BaselinePolicyId,
                    ExpectedRevision: null,
                    request.Artifact,
                    request.IdempotencyKey,
                    request.Reason,
                    request.CorrelationId),
                roleGrants,
                ct).ConfigureAwait(false);
            await AcquireProjectPolicyAdvisoryLockAsync(db, request.ProjectId, ct).ConfigureAwait(false);
            var installationId = await ReadCurrentInstallationIdAsync(db, ct).ConfigureAwait(false);

            var replay = await db.NativeAcsProjectPolicyBaselines.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ApprovalScopeDigest == scopeDigest, ct)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.InstallationId != installationId ||
                    replay.TenantId != caller.TenantId ||
                    replay.ProjectId != request.ProjectId ||
                    replay.ApprovalRequestDigest != requestDigest)
                    throw IdempotencyConflict();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return ToBaselineReceipt(replay, replayed: true);
            }

            if (await HasProjectPolicyHeadAsync(db, request.ProjectId, ct).ConfigureAwait(false))
                throw ProjectConfigException.Conflict("This project already has a Policy head.");

            var activeExisting = await db.NativeAcsProjectPolicyBaselines.AsNoTracking()
                .AnyAsync(item => item.ProjectId == request.ProjectId &&
                    (item.State == NativeAcsProjectPolicyBaselineState.Approved ||
                     item.State == NativeAcsProjectPolicyBaselineState.Pinned), ct)
                .ConfigureAwait(false);
            if (activeExisting)
                throw ProjectConfigException.Conflict("An active Project Policy baseline approval or pin already exists.");

            var now = timeProvider.GetUtcNow();
            var record = new NativeAcsProjectPolicyBaselineRecord
            {
                BaselineId = Guid.NewGuid(),
                InstallationId = installationId,
                TenantId = caller.TenantId,
                ProjectId = request.ProjectId,
                PolicyId = BaselinePolicyId,
                State = NativeAcsProjectPolicyBaselineState.Approved,
                ContentSha256 = request.Artifact.ContentSha256,
                ArtifactJson = JsonSerializer.Serialize(request.Artifact, JsonOptions),
                ProfileId = NativeAcsUnifiedPolicyProfile.ProfileId,
                ApproverIssuer = caller.Issuer,
                ApproverActorId = caller.ActorId,
                ApproverMembershipId = caller.MembershipId,
                ApproverMembershipRevision = caller.MembershipRevision,
                ApproverAssignmentId = authorizingGrant.AssignmentId,
                ApproverAssignmentRevision = authorizingGrant.Revision,
                ApprovalIdempotencyKey = request.IdempotencyKey,
                ApprovalRequestDigest = requestDigest,
                ApprovalScopeDigest = scopeDigest,
                ApprovalReason = request.Reason,
                ApprovalCorrelationId = request.CorrelationId,
                ApprovedAt = now,
            };
            db.NativeAcsProjectPolicyBaselines.Add(record);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ToBaselineReceipt(record, replayed: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CreateBaselineTimeoutException();
        }
        catch (Exception ex) when (MapBaselinePersistenceException(ex, out var mapped))
        {
            throw mapped;
        }
    }

    internal async Task<NativeAcsProjectPolicyBaselineReceipt> RevokeBaselineByAuthorAsync(
        ProjectAuthorizationContext caller,
        NativeAcsProjectPolicyBaselineRevokeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireHumanActor();
        ValidateBaselineReasonAndCorrelation(request.Reason, request.CorrelationId);
        if (!Guid.TryParseExact(request.ProjectId, "N", out _))
            throw ProjectConfigException.NotFound();

        var roleGrants = GetAuthorizingGrants(caller, NativeAcsPolicyScopeType.Project, request.ProjectId);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);
        try
        {
            await SetBaselineTimeoutsAsync(db, ct).ConfigureAwait(false);
            _ = await new SkillAdmissionService(db, timeProvider).LockFenceAsync(ct).ConfigureAwait(false);
            _ = await LockAndCheckAuthorityAsync(
                db,
                caller,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project,
                    request.ProjectId,
                    BaselinePolicyId,
                    ExpectedRevision: null,
                    new NativeAcsArtifact([], [], new string('0', 64)),
                    "revoke",
                    request.Reason,
                    request.CorrelationId),
                roleGrants,
                ct).ConfigureAwait(false);
            await AcquireProjectPolicyAdvisoryLockAsync(db, request.ProjectId, ct).ConfigureAwait(false);
            var installationId = await ReadCurrentInstallationIdAsync(db, ct).ConfigureAwait(false);

            var baseline = await LockBaselineRowAsync(db, request.BaselineId, ct).ConfigureAwait(false);
            if (baseline is null || baseline.ProjectId != request.ProjectId || baseline.TenantId != caller.TenantId)
                throw ProjectConfigException.NotFound();
            if (baseline.InstallationId != installationId)
                throw ProjectConfigException.Conflict("The Project Policy baseline belongs to a different installation.");

            if (baseline.State == NativeAcsProjectPolicyBaselineState.Revoked)
            {
                if (baseline.RevokedByIssuer == caller.Issuer &&
                    baseline.RevokedByActorId == caller.ActorId &&
                    baseline.RevokedReason == request.Reason &&
                    baseline.RevokedCorrelationId == request.CorrelationId)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return ToBaselineReceipt(baseline, replayed: true);
                }
                throw ProjectConfigException.Conflict("The Project Policy baseline is already revoked.");
            }
            if (baseline.State == NativeAcsProjectPolicyBaselineState.Consumed)
                throw ProjectConfigException.Conflict("A consumed Project Policy baseline cannot be revoked.");

            baseline.State = NativeAcsProjectPolicyBaselineState.Revoked;
            baseline.RevokedByIssuer = caller.Issuer;
            baseline.RevokedByActorId = caller.ActorId;
            baseline.RevokedReason = request.Reason;
            baseline.RevokedCorrelationId = request.CorrelationId;
            baseline.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ToBaselineReceipt(baseline, replayed: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CreateBaselineTimeoutException();
        }
        catch (Exception ex) when (MapBaselinePersistenceException(ex, out var mapped))
        {
            throw mapped;
        }
    }

    internal async Task<NativeAcsProjectPolicyBaselineReceipt> PinBaselineByOperatorAsync(
        ProjectAuthorizationContext operatorCaller,
        string projectId,
        Guid baselineId,
        string expectedContentSha256,
        string idempotencyKey,
        string reason,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operatorCaller);
        operatorCaller.RequireHumanActor();
        ValidateBaselineWriteMetadata(idempotencyKey, reason, correlationId);
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
        if (string.IsNullOrWhiteSpace(expectedContentSha256) || !IsSha256Hex(expectedContentSha256))
            throw ProjectConfigException.Invalid("native_acs_policy_invalid", "A valid expectedContentSha256 is required.");

        var pinScopeDigest = ComputeBaselineScopeDigest(
            "pin", operatorCaller.TenantId, projectId, operatorCaller.Issuer, operatorCaller.ActorId, idempotencyKey);
        var pinRequestDigest = ComputeBaselinePinRequestDigest(
            operatorCaller.TenantId, projectId, baselineId, expectedContentSha256, reason, correlationId);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);
        try
        {
            await SetBaselineTimeoutsAsync(db, ct).ConfigureAwait(false);
            _ = await new SkillAdmissionService(db, timeProvider).LockFenceAsync(ct).ConfigureAwait(false);

            var snapshot = await db.NativeAcsProjectPolicyBaselines.AsNoTracking()
                .Where(item => item.BaselineId == baselineId && item.ProjectId == projectId && item.TenantId == operatorCaller.TenantId)
                .Select(item => new
                {
                    item.ApproverIssuer,
                    item.ApproverActorId,
                    item.ApproverMembershipId,
                    item.ApproverMembershipRevision,
                    item.ApproverAssignmentId,
                    item.ApproverAssignmentRevision,
                })
                .SingleOrDefaultAsync(ct)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();

            await LockMembershipsForOperatorAndApproverAsync(
                db,
                operatorCaller.MembershipId,
                operatorCaller.Issuer,
                operatorCaller.ActorId,
                operatorCaller.TenantId,
                operatorCaller.MembershipRevision,
                snapshot.ApproverMembershipId,
                snapshot.ApproverIssuer,
                snapshot.ApproverActorId,
                operatorCaller.TenantId,
                snapshot.ApproverMembershipRevision,
                ct).ConfigureAwait(false);

            await LockActiveProjectForShareAsync(db, projectId, operatorCaller.TenantId, ct).ConfigureAwait(false);
            await LockApproverGrantForShareAsync(
                db,
                snapshot.ApproverMembershipId,
                snapshot.ApproverIssuer,
                snapshot.ApproverActorId,
                operatorCaller.TenantId,
                snapshot.ApproverMembershipRevision,
                projectId,
                snapshot.ApproverAssignmentId,
                snapshot.ApproverAssignmentRevision,
                ct).ConfigureAwait(false);

            await AcquireProjectPolicyAdvisoryLockAsync(db, projectId, ct).ConfigureAwait(false);
            var installationId = await ReadCurrentInstallationIdAsync(db, ct).ConfigureAwait(false);

            var replay = await db.NativeAcsProjectPolicyBaselines.AsNoTracking()
                .SingleOrDefaultAsync(item => item.PinScopeDigest == pinScopeDigest, ct)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.InstallationId != installationId ||
                    replay.TenantId != operatorCaller.TenantId ||
                    replay.ProjectId != projectId ||
                    replay.BaselineId != baselineId ||
                    replay.PinRequestDigest != pinRequestDigest)
                    throw IdempotencyConflict();
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return ToBaselineReceipt(replay, replayed: true);
            }

            if (await HasProjectPolicyHeadAsync(db, projectId, ct).ConfigureAwait(false))
                throw ProjectConfigException.Conflict("This project already has a Policy head.");

            var baseline = await LockBaselineRowAsync(db, baselineId, ct).ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            if (baseline.InstallationId != installationId)
                throw ProjectConfigException.Conflict("The Project Policy baseline belongs to a different installation.");
            if (baseline.State != NativeAcsProjectPolicyBaselineState.Approved)
                throw ProjectConfigException.Conflict($"The Project Policy baseline is in state '{baseline.State}' and cannot be pinned.");
            if (baseline.PolicyId != BaselinePolicyId ||
                !string.Equals(baseline.ContentSha256, expectedContentSha256, StringComparison.Ordinal))
                throw ProjectConfigException.Conflict("The Project Policy baseline hash does not match the expected digest.");

            var projectArtifact = JsonSerializer.Deserialize<NativeAcsArtifact>(baseline.ArtifactJson, JsonOptions)
                ?? throw new InvalidOperationException("The stored Project Policy baseline artifact could not be read.");
            if (!string.Equals(
                    NativeAcsArtifactValidator.ComputeContentSha256(projectArtifact),
                    baseline.ContentSha256,
                    StringComparison.Ordinal))
                throw new ProjectConfigException(
                    "native_acs_policy_corrupt",
                    "The stored Project Policy baseline artifact is corrupt.",
                    StatusCodes.Status500InternalServerError);

            var (platformPolicyId, platformRevision, platformArtifact) =
                await LockAndReadPlatformPolicyArtifactAsync(db, ct).ConfigureAwait(false);
            if (!NativeAcsUnifiedPolicyProfile.IsProjectBaselineCompatibleWithPlatform(
                    platformArtifact, projectArtifact, out var code, out var message))
                throw ProjectConfigException.Invalid(code ?? "native_acs_policy_incompatible",
                    message ?? "The Project Policy baseline is not compatible with the current Platform Policy.");

            var now = timeProvider.GetUtcNow();
            baseline.State = NativeAcsProjectPolicyBaselineState.Pinned;
            baseline.OperatorIssuer = operatorCaller.Issuer;
            baseline.OperatorActorId = operatorCaller.ActorId;
            baseline.OperatorMembershipId = operatorCaller.MembershipId;
            baseline.OperatorMembershipRevision = operatorCaller.MembershipRevision;
            baseline.PinIdempotencyKey = idempotencyKey;
            baseline.PinRequestDigest = pinRequestDigest;
            baseline.PinScopeDigest = pinScopeDigest;
            baseline.PlatformPolicyId = platformPolicyId;
            baseline.PlatformPolicyRevision = platformRevision;
            baseline.PlatformContentSha256 = platformArtifact.ContentSha256;
            baseline.PinReason = reason;
            baseline.PinCorrelationId = correlationId;
            baseline.PinnedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ToBaselineReceipt(baseline, replayed: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CreateBaselineTimeoutException();
        }
        catch (Exception ex) when (MapBaselinePersistenceException(ex, out var mapped))
        {
            throw mapped;
        }
    }

    internal async Task<NativeAcsProjectPolicyBaselineReceipt> RevokeBaselineByOperatorAsync(
        ProjectAuthorizationContext operatorCaller,
        string projectId,
        Guid baselineId,
        string expectedContentSha256,
        string reason,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operatorCaller);
        operatorCaller.RequireHumanActor();
        ValidateBaselineReasonAndCorrelation(reason, correlationId);
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
        if (string.IsNullOrWhiteSpace(expectedContentSha256) || !IsSha256Hex(expectedContentSha256))
            throw ProjectConfigException.Invalid("native_acs_policy_invalid", "A valid expectedContentSha256 is required.");

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);
        try
        {
            await SetBaselineTimeoutsAsync(db, ct).ConfigureAwait(false);
            _ = await new SkillAdmissionService(db, timeProvider).LockFenceAsync(ct).ConfigureAwait(false);

            await LockSingleMembershipForShareAsync(
                db,
                operatorCaller.MembershipId,
                operatorCaller.Issuer,
                operatorCaller.ActorId,
                operatorCaller.TenantId,
                operatorCaller.MembershipRevision,
                ct).ConfigureAwait(false);
            await LockActiveProjectForShareAsync(db, projectId, operatorCaller.TenantId, ct).ConfigureAwait(false);
            await AcquireProjectPolicyAdvisoryLockAsync(db, projectId, ct).ConfigureAwait(false);
            var installationId = await ReadCurrentInstallationIdAsync(db, ct).ConfigureAwait(false);

            var baseline = await LockBaselineRowAsync(db, baselineId, ct).ConfigureAwait(false);
            if (baseline is null || baseline.ProjectId != projectId || baseline.TenantId != operatorCaller.TenantId)
                throw ProjectConfigException.NotFound();
            if (baseline.InstallationId != installationId)
                throw ProjectConfigException.Conflict("The Project Policy baseline belongs to a different installation.");
            if (!string.Equals(baseline.ContentSha256, expectedContentSha256, StringComparison.Ordinal))
                throw ProjectConfigException.Conflict("The Project Policy baseline hash does not match the expected digest.");

            if (baseline.State == NativeAcsProjectPolicyBaselineState.Revoked)
            {
                if (baseline.RevokedByIssuer == operatorCaller.Issuer &&
                    baseline.RevokedByActorId == operatorCaller.ActorId &&
                    baseline.RevokedReason == reason &&
                    baseline.RevokedCorrelationId == correlationId)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return ToBaselineReceipt(baseline, replayed: true);
                }
                throw ProjectConfigException.Conflict("The Project Policy baseline is already revoked.");
            }
            if (baseline.State == NativeAcsProjectPolicyBaselineState.Consumed)
                throw ProjectConfigException.Conflict("A consumed Project Policy baseline cannot be revoked.");

            baseline.State = NativeAcsProjectPolicyBaselineState.Revoked;
            baseline.RevokedByIssuer = operatorCaller.Issuer;
            baseline.RevokedByActorId = operatorCaller.ActorId;
            baseline.RevokedReason = reason;
            baseline.RevokedCorrelationId = correlationId;
            baseline.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ToBaselineReceipt(baseline, replayed: false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw CreateBaselineTimeoutException();
        }
        catch (Exception ex) when (MapBaselinePersistenceException(ex, out var mapped))
        {
            throw mapped;
        }
    }

    private static void ValidateProjectBaselineArtifact(NativeAcsArtifact artifact)
    {
        if (artifact.ManifestBytes.IsDefaultOrEmpty ||
            artifact.ManifestBytes.Length > MaximumPolicySourceManifestBytes ||
            artifact.Files.IsDefault ||
            artifact.Files.Length > MaximumPolicySourceFiles)
            throw ProjectConfigException.Invalid("native_acs_policy_invalid", "The Project Policy baseline artifact is invalid.");

        var totalBytes = artifact.ManifestBytes.Length;
        foreach (var file in artifact.Files)
        {
            if (file is null || file.Bytes.IsDefaultOrEmpty || file.Bytes.Length > MaximumPolicySourceFileBytes)
                throw ProjectConfigException.Invalid("native_acs_policy_invalid", "The Project Policy baseline artifact contains an invalid file.");
            totalBytes += file.Bytes.Length;
            if (totalBytes > MaximumPolicySourceArtifactBytes)
                throw ProjectConfigException.Invalid("native_acs_policy_invalid", "The Project Policy baseline artifact exceeds the 1 MiB limit.");
        }

        var inspection = NativeAcsUnifiedPolicyProfile.InspectProject(artifact);
        if (!inspection.IsValid)
            throw ProjectConfigException.Invalid(
                inspection.Diagnostics.FirstOrDefault()?.Code ?? "native_acs_policy_invalid",
                inspection.Diagnostics.FirstOrDefault()?.Message ?? "The Project Policy baseline must be a valid 12-file v3 artifact.");
        if (artifact.Files.Length != 12 ||
            !artifact.Files.Any(file => string.Equals(
                file.RelativePath, SkillAdmissionService.SkillsPolicyDataPath, StringComparison.Ordinal)))
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The Project Policy baseline must be a 12-file v3 artifact.");

        var computed = NativeAcsArtifactValidator.ComputeContentSha256(artifact);
        if (!string.Equals(computed, artifact.ContentSha256, StringComparison.Ordinal))
            throw ProjectConfigException.Invalid(
                "artifact_hash",
                "The submitted artifact hash does not match the computed SHA-256.");
    }

    private static void ValidateBaselineWriteMetadata(string idempotencyKey, string reason, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw ProjectConfigException.Invalid("native_acs_policy_invalid", "A valid idempotencyKey is required.");
        ValidateBaselineReasonAndCorrelation(reason, correlationId);
    }

    private static void ValidateBaselineReasonAndCorrelation(string reason, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 512)
            throw ProjectConfigException.Invalid("native_acs_policy_invalid", "A valid reason is required.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
            throw ProjectConfigException.Invalid("native_acs_policy_invalid", "A valid correlationId is required.");
    }

    private static Task SetBaselineTimeoutsAsync(ProjectsConfigDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", ct);

    private static async Task AcquireProjectPolicyAdvisoryLockAsync(
        ProjectsConfigDbContext db,
        string projectId,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended('agentweaver.projects_config.project_policy:' || @project_id, 0))",
            connection,
            transaction);
        command.Parameters.AddWithValue("project_id", projectId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Guid> ReadCurrentInstallationIdAsync(ProjectsConfigDbContext db, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            "SELECT installation_id FROM projects_config.installation_identity WHERE singleton = true",
            connection,
            transaction);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is not Guid installationId || installationId == Guid.Empty)
            throw new ProjectConfigException(
                "native_acs_policy_source_unavailable",
                "The installation identity is unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        return installationId;
    }

    private static async Task<bool> HasProjectPolicyHeadAsync(
        ProjectsConfigDbContext db,
        string projectId,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM projects_config.native_acs_policy_heads
            WHERE scope_type = 'Project' AND scope_id = @project_id
            FOR SHARE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("project_id", projectId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task LockMembershipsForOperatorAndApproverAsync(
        ProjectsConfigDbContext db,
        Guid opMembershipId, string opIssuer, string opSubject, string opTenantId, long opRevision,
        Guid appMembershipId, string appIssuer, string appSubject, string appTenantId, long appRevision,
        CancellationToken ct)
    {
        if (opMembershipId == appMembershipId)
        {
            if (opIssuer != appIssuer || opSubject != appSubject || opTenantId != appTenantId || opRevision != appRevision)
                throw ProjectConfigException.Forbidden();
            await LockSingleMembershipForShareAsync(db, opMembershipId, opIssuer, opSubject, opTenantId, opRevision, ct).ConfigureAwait(false);
            return;
        }

        if (opMembershipId.CompareTo(appMembershipId) < 0)
        {
            await LockSingleMembershipForShareAsync(db, opMembershipId, opIssuer, opSubject, opTenantId, opRevision, ct).ConfigureAwait(false);
            await LockSingleMembershipForShareAsync(db, appMembershipId, appIssuer, appSubject, appTenantId, appRevision, ct).ConfigureAwait(false);
        }
        else
        {
            await LockSingleMembershipForShareAsync(db, appMembershipId, appIssuer, appSubject, appTenantId, appRevision, ct).ConfigureAwait(false);
            await LockSingleMembershipForShareAsync(db, opMembershipId, opIssuer, opSubject, opTenantId, opRevision, ct).ConfigureAwait(false);
        }
    }

    private static async Task LockSingleMembershipForShareAsync(
        ProjectsConfigDbContext db,
        Guid membershipId, string issuer, string subject, string tenantId, long revision,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM projects_config.tenant_memberships
            WHERE membership_id = @membership_id
              AND issuer = @issuer
              AND subject = @subject
              AND tenant_id = @tenant_id
              AND revision = @revision
              AND state = 'Active'
            FOR SHARE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("revision", revision);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull)
            throw ProjectConfigException.Forbidden();
    }

    private static async Task LockActiveProjectForShareAsync(
        ProjectsConfigDbContext db,
        string projectId,
        string tenantId,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM projects_config.projects
            WHERE project_id = @project_id
              AND tenant_id = @tenant_id
              AND state = 'Active'
            FOR SHARE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("project_id", projectId);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull)
            throw ProjectConfigException.NotFound();
    }

    private static async Task LockApproverGrantForShareAsync(
        ProjectsConfigDbContext db,
        Guid membershipId, string issuer, string subject, string tenantId, long membershipRevision,
        string projectId, Guid assignmentId, long assignmentRevision,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT authorization_result
            FROM projects_config.lock_native_acs_policy_authority(
                @membership_id, @issuer, @actor_id, @tenant_id, @membership_revision,
                'Project', @project_id, @grant_ids, @grant_resource_types, @grant_resource_ids,
                @grant_roles, @grant_revisions)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("membership_id", membershipId);
        command.Parameters.AddWithValue("issuer", issuer);
        command.Parameters.AddWithValue("actor_id", subject);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("membership_revision", membershipRevision);
        command.Parameters.AddWithValue("project_id", projectId);
        command.Parameters.AddWithValue("grant_ids", new[] { assignmentId, assignmentId });
        command.Parameters.AddWithValue("grant_resource_types", new[] { "Project", "Tenant" });
        command.Parameters.AddWithValue("grant_resource_ids", new[] { projectId, tenantId });
        command.Parameters.AddWithValue("grant_roles", new[] { "Owner", "TenantAdmin" });
        command.Parameters.AddWithValue("grant_revisions", new[] { assignmentRevision, assignmentRevision });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (result == "not_found")
            throw ProjectConfigException.NotFound();
        if (result != "authorized")
            throw ProjectConfigException.Forbidden();
    }

    private static Task<NativeAcsProjectPolicyBaselineRecord?> LockBaselineRowAsync(
        ProjectsConfigDbContext db,
        Guid baselineId,
        CancellationToken ct) =>
        db.NativeAcsProjectPolicyBaselines
            .FromSqlInterpolated($"""
                SELECT *
                FROM projects_config.native_acs_project_policy_baselines
                WHERE baseline_id = {baselineId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(ct);

    private static async Task<(string PolicyId, long Revision, NativeAcsArtifact Artifact)>
        LockAndReadPlatformPolicyArtifactAsync(ProjectsConfigDbContext db, CancellationToken ct)
    {
        var head = await db.NativeAcsPolicyHeads
            .FromSqlRaw("""
                SELECT scope_type, scope_id, policy_id, current_revision, created_at, updated_at
                FROM projects_config.native_acs_policy_heads
                WHERE scope_type = 'Platform' AND scope_id = 'default'
                FOR SHARE
                """)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (head is null)
            throw new ProjectConfigException(
                "native_acs_policy_source_unavailable",
                "The Platform Policy head is unavailable.",
                StatusCodes.Status503ServiceUnavailable);

        var revision = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision, ct)
            .ConfigureAwait(false)
            ?? throw new ProjectConfigException(
                "native_acs_policy_corrupt",
                "The Platform Policy revision is missing.",
                StatusCodes.Status500InternalServerError);

        var files = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .Where(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision)
            .OrderBy(item => item.RelativePath)
            .Select(item => new NativeAcsArtifactFile(item.RelativePath, item.Bytes.ToImmutableArray()))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var artifact = new NativeAcsArtifact(
            revision.ManifestBytes.ToImmutableArray(),
            files.ToImmutableArray(),
            revision.ContentSha256);
        if (!string.Equals(
                NativeAcsArtifactValidator.ComputeContentSha256(artifact),
                revision.ContentSha256,
                StringComparison.Ordinal))
            throw new ProjectConfigException(
                "native_acs_policy_corrupt",
                "The Platform Policy artifact hash does not match its stored bytes.",
                StatusCodes.Status500InternalServerError);
        return (head.PolicyId, head.CurrentRevision, artifact);
    }

    private static NativeAcsProjectPolicyBaselineReceipt ToBaselineReceipt(
        NativeAcsProjectPolicyBaselineRecord record,
        bool replayed) =>
        new(
            record.BaselineId,
            record.InstallationId,
            record.TenantId,
            record.ProjectId,
            record.PolicyId,
            record.State,
            record.ContentSha256,
            record.ProfileId,
            record.ApproverIssuer,
            record.ApproverActorId,
            record.ApprovedAt,
            record.OperatorIssuer,
            record.OperatorActorId,
            record.PlatformPolicyId,
            record.PlatformPolicyRevision,
            record.PlatformContentSha256,
            record.PinnedAt,
            record.RevokedByIssuer,
            record.RevokedByActorId,
            record.RevokedAt,
            replayed);

    private static string ComputeBaselineScopeDigest(
        string operation, string tenantId, string projectId, string issuer, string actorId, string idempotencyKey) =>
        HashBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            operation,
            tenantId,
            projectId,
            issuer,
            actorId,
            idempotencyKey,
        }, JsonOptions));

    private static string ComputeBaselineApprovalRequestDigest(
        string tenantId, string projectId, string contentSha256, string reason, string correlationId) =>
        HashBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenantId,
            projectId,
            contentSha256,
            reason,
            correlationId,
        }, JsonOptions));

    private static string ComputeBaselinePinRequestDigest(
        string tenantId, string projectId, Guid baselineId, string expectedContentSha256, string reason, string correlationId) =>
        HashBytes(JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenantId,
            projectId,
            baselineId,
            expectedContentSha256,
            reason,
            correlationId,
        }, JsonOptions));

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static ProjectConfigException CreateBaselineTimeoutException() =>
        new(
            "native_acs_policy_source_unavailable",
            "The Project Policy baseline operation timed out; retry shortly.",
            StatusCodes.Status503ServiceUnavailable);

    private static bool MapBaselinePersistenceException(Exception ex, out ProjectConfigException mapped)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        if (pg is not null)
        {
            if (pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                mapped = ProjectConfigException.Conflict("The baseline operation conflicted with a concurrent update.");
                return true;
            }
            if (pg.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                mapped = ProjectConfigException.Conflict("An active Project Policy baseline or idempotency key already exists.");
                return true;
            }
            if (pg.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled)
            {
                mapped = new ProjectConfigException(
                    "native_acs_policy_source_unavailable",
                    "The Project Policy baseline store is busy; retry shortly.",
                    StatusCodes.Status503ServiceUnavailable);
                return true;
            }
        }
        mapped = null!;
        return false;
    }
}
