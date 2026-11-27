using System.Data;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

internal sealed partial class NativeAcsPolicyStore(
    IDbContextFactory<ProjectsConfigDbContext> contextFactory,
    TimeProvider timeProvider,
    IDataProtectionProvider dataProtectionProvider,
    ProjectGovernanceDecisionStore? governanceDecisions = null)
{
    private const string PolicyRevisionEventType = "native_acs.policy_revision_committed.v1";
    private const string PlatformPolicyPreparationResourceType = "platform_policy";
    private const string PlatformPolicyPreparationDecisionType = "platform.policy.preparation.v1";
    private const int MaximumPolicySourceResponseBytes = 5_242_880;
    // A valid 1 MiB artifact with 3-digit byte arrays and PostgreSQL jsonb::text spacing can reach ~5.35 MB.
    private const int MaximumPreparationStorageJsonBytes = 5_500_000;
    private const int MaximumPolicySourceManifestBytes = 65_536;
    private const int MaximumPolicySourceFiles = 64;
    private const int MaximumPolicySourceFileBytes = 262_144;
    private const int MaximumPolicySourceArtifactBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ProjectGovernanceDecisionStore _governanceDecisions =
        governanceDecisions ?? new ProjectGovernanceDecisionStore(contextFactory, timeProvider);
    private readonly IDataProtector _scopeListCursorProtector =
        dataProtectionProvider.CreateProtector("Agentweaver.Projects.Config.ProjectPolicyScopeListCursor.v2");
    private const int HistoryCursorVersion = 1;
    private const string HistoryCursorOrder = "revision-descending";
    private const int ScopeListCursorVersion = 2;
    private const string ScopeListCursorOrder = "scope-id-ascending";

    internal async Task<NativeAcsPolicyWriteReceipt> WriteAsync(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var roleGrants = EnsureWriteAuthorized(caller, request);
        var requestDigest = RequestDigest(request);
        var scopeDigest = ScopeDigest(caller, request);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Keep the database authority check, head advancement, audit, and outbox writes in one serializable commit.
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
        var skillAdmissionFence = request.Artifact.Files.Any(file =>
            string.Equals(file.RelativePath, SkillAdmissionService.SkillsPolicyDataPath,
                StringComparison.Ordinal))
            ? await new SkillAdmissionService(db, timeProvider)
                .LockFenceAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var authorizingGrant = await LockAndCheckAuthorityAsync(
            db, caller, request, roleGrants, cancellationToken).ConfigureAwait(false);
        // Check current database authority before returning a saved receipt for a retry.
        var replay = await FindReceiptAsync(db, caller, request, scopeDigest, requestDigest, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay;
        }

        await SkillAdmissionService.LockPolicyScopeAnchorAsync(
                db, request.ScopeType, request.ScopeId, cancellationToken)
            .ConfigureAwait(false);
        var currentRevision = await LockHeadAsync(db, request, cancellationToken).ConfigureAwait(false);
        if (currentRevision != request.ExpectedRevision)
            throw ProjectConfigException.Conflict("The native ACS policy head has changed.");
        var revision = checked((currentRevision ?? 0) + 1);
        var now = timeProvider.GetUtcNow();
        var auditId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var receipt = new NativeAcsPolicyWriteReceipt(
            request.ScopeType,
            request.ScopeId,
            request.PolicyId,
            revision,
            request.Artifact.ContentSha256,
            auditId,
            outboxId,
            Replayed: false);

        db.NativeAcsPolicyRevisions.Add(new NativeAcsPolicyRevisionRecord
        {
            ScopeType = request.ScopeType,
            ScopeId = request.ScopeId,
            PolicyId = request.PolicyId,
            Revision = revision,
            ContentSha256 = request.Artifact.ContentSha256,
            ManifestBytes = request.Artifact.ManifestBytes.ToArray(),
            CreatedByIssuer = caller.Issuer,
            CreatedByActorId = caller.ActorId,
            MembershipId = caller.MembershipId,
            MembershipRevision = caller.MembershipRevision,
            RoleAssignmentId = authorizingGrant.AssignmentId,
            RoleAssignmentRevision = authorizingGrant.Revision,
            Reason = request.Reason,
            CorrelationId = request.CorrelationId,
            CreatedAt = now,
        });
        foreach (var file in request.Artifact.Files)
        {
            db.NativeAcsPolicyArtifactFiles.Add(new NativeAcsPolicyArtifactFileRecord
            {
                ScopeType = request.ScopeType,
                ScopeId = request.ScopeId,
                PolicyId = request.PolicyId,
                Revision = revision,
                RelativePath = file.RelativePath,
                Bytes = file.Bytes.ToArray(),
            });
        }

        if (currentRevision is null)
        {
            db.NativeAcsPolicyHeads.Add(new NativeAcsPolicyHeadRecord
            {
                ScopeType = request.ScopeType,
                ScopeId = request.ScopeId,
                PolicyId = request.PolicyId,
                CurrentRevision = revision,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            var head = await db.NativeAcsPolicyHeads.SingleAsync(
                item => item.ScopeType == request.ScopeType &&
                    item.ScopeId == request.ScopeId &&
                    item.PolicyId == request.PolicyId,
                cancellationToken).ConfigureAwait(false);
            head.CurrentRevision = revision;
            head.UpdatedAt = now;
        }

        db.NativeAcsPolicyIdempotency.Add(new NativeAcsPolicyIdempotencyRecord
        {
            ScopeDigest = scopeDigest,
            ScopeType = request.ScopeType,
            ScopeId = request.ScopeId,
            PolicyId = request.PolicyId,
            ActorIssuer = caller.Issuer,
            ActorId = caller.ActorId,
            IdempotencyKey = request.IdempotencyKey,
            RequestDigest = requestDigest,
            ReceiptJson = JsonSerializer.Serialize(receipt, JsonOptions),
            CreatedAt = now,
        });
        db.NativeAcsPolicyChangeAudit.Add(new NativeAcsPolicyChangeAuditRecord
        {
            AuditId = auditId,
            ScopeType = request.ScopeType,
            ScopeId = request.ScopeId,
            PolicyId = request.PolicyId,
            Revision = revision,
            PreviousRevision = currentRevision,
            ChangeType = currentRevision is null ? "created" : "updated",
            ActorIssuer = caller.Issuer,
            ActorId = caller.ActorId,
            MembershipId = caller.MembershipId,
            MembershipRevision = caller.MembershipRevision,
            RoleAssignmentId = authorizingGrant.AssignmentId,
            RoleAssignmentRevision = authorizingGrant.Revision,
            PreviousContentSha256 = currentRevision is null
                ? null
                : await db.NativeAcsPolicyRevisions.AsNoTracking()
                    .Where(item => item.ScopeType == request.ScopeType &&
                        item.ScopeId == request.ScopeId &&
                        item.PolicyId == request.PolicyId &&
                        item.Revision == currentRevision)
                    .Select(item => item.ContentSha256)
                    .SingleAsync(cancellationToken)
                    .ConfigureAwait(false),
            ContentSha256 = request.Artifact.ContentSha256,
            Reason = request.Reason,
            CorrelationId = request.CorrelationId,
            CreatedAt = now,
        });
        if (skillAdmissionFence is not null)
            skillAdmissionFence.Generation = checked(skillAdmissionFence.Generation + 1);
        db.NativeAcsPolicyOutbox.Add(new NativeAcsPolicyOutboxRecord
        {
            OutboxId = outboxId,
            EventType = PolicyRevisionEventType,
            PayloadJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                scopeType = request.ScopeType.ToString(),
                scopeId = request.ScopeId,
                policyId = request.PolicyId,
                revision,
                request.Artifact.ContentSha256,
                actorIssuer = caller.Issuer,
                actorId = caller.ActorId,
                createdAt = now,
            }, JsonOptions),
            CreatedAt = now,
        });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // Use current database authority again after the writes and before commit.
            var rechecked = await LockAndCheckAuthorityAsync(
                db, caller, request, roleGrants, cancellationToken).ConfigureAwait(false);
            if (rechecked.AssignmentId != authorizingGrant.AssignmentId ||
                rechecked.Revision != authorizingGrant.Revision)
                throw ProjectConfigException.Forbidden();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ProjectConfigException.Conflict("The native ACS policy head changed during the write.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            throw ProjectConfigException.Conflict("The native ACS policy write conflicted with another request.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.SerializationFailure,
        })
        {
            throw ProjectConfigException.Conflict("The native ACS policy write conflicted with a concurrent update.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw ProjectConfigException.Conflict("The native ACS policy write conflicted with a concurrent update.");
        }
    }

    /// <summary>
    /// Returns the durable receipt for an identical earlier request after a fresh authority check.
    /// It does not validate the artifact again, so a lost response can be recovered while the validator is down.
    /// </summary>
    internal async Task<NativeAcsPolicyWriteReceipt?> FindReplayAsync(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var roleGrants = EnsureWriteAuthorized(caller, request);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        await LockAndCheckAuthorityAsync(db, caller, request, roleGrants, cancellationToken).ConfigureAwait(false);
        var receipt = await FindReceiptAsync(
            db, caller, request, ScopeDigest(caller, request), RequestDigest(request), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    internal async Task<NativeAcsPolicyPreparationReceipt?> FindPreparationReplayAsync(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ValidatePreparationRequest(request);
        EnsurePlatformPreparationRequest(request);
        var roleGrants = EnsureWriteAuthorized(caller, request);
        var requestDigest = RequestDigest(request);
        var scopeDigest = ScopeDigest(caller, request);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        await LockAndCheckAuthorityAsync(db, caller, request, roleGrants, cancellationToken).ConfigureAwait(false);
        var receipt = await FindPreparationReceiptAsync(
            db, caller, request, scopeDigest, requestDigest, cancellationToken).ConfigureAwait(false);
        var currentHead = await LockPreparationHeadAsync(db, request, cancellationToken).ConfigureAwait(false);
        EnsureExpectedHead(request, currentHead);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    internal async Task<NativeAcsPolicyPreparationReceipt> PrepareAsync(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ValidatePreparationRequest(request);
        EnsurePlatformPreparationRequest(request);
        var roleGrants = EnsureWriteAuthorized(caller, request);
        var requestDigest = RequestDigest(request);
        var scopeDigest = ScopeDigest(caller, request);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var authorizingGrant = await LockAndCheckAuthorityAsync(
                db, caller, request, roleGrants, cancellationToken).ConfigureAwait(false);
            var replay = await FindPreparationReceiptAsync(
                db, caller, request, scopeDigest, requestDigest, cancellationToken).ConfigureAwait(false);
            var currentHead = await LockPreparationHeadAsync(db, request, cancellationToken).ConfigureAwait(false);
            EnsureExpectedHead(request, currentHead);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            var preparationId = Guid.NewGuid();
            var now = timeProvider.GetUtcNow();
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var npgsqlTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();
            var decisionReceipt = await _governanceDecisions.AppendWithCurrentHeadInTransactionAsync(
                connection,
                npgsqlTransaction,
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                PlatformPolicyPreparationResourceType,
                request.PolicyId,
                scopeDigest,
                new ProjectGovernanceDecisionInput(
                    DecisionType: PlatformPolicyPreparationDecisionType,
                    Outcome: ProjectGovernanceOutcome.Pending,
                    ApprovalState: ProjectGovernanceApprovalState.Pending,
                    PolicyRevision: currentHead?.Revision,
                    PolicyArtifactSha256: request.Artifact.ContentSha256,
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
                    EffectiveRoleRevisions:
                    [
                        new ProjectGovernanceRoleRevision(
                            authorizingGrant.AssignmentId,
                            authorizingGrant.Revision),
                    ],
                    OwnerReceiptId: preparationId.ToString("N"),
                    OwnerReceiptPosition: null,
                    CompilerGeneration: null,
                    ApplicationGeneration: null,
                    CorrelationId: request.CorrelationId,
                    DecidedAt: now),
                cancellationToken).ConfigureAwait(false);

            var record = new NativeAcsPolicyPreparationRecord
            {
                PreparationId = preparationId,
                ScopeDigest = scopeDigest,
                ScopeType = request.ScopeType,
                ScopeId = request.ScopeId,
                PolicyId = request.PolicyId,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                MembershipId = caller.MembershipId,
                MembershipRevision = caller.MembershipRevision,
                RoleAssignmentId = authorizingGrant.AssignmentId,
                RoleAssignmentRevision = authorizingGrant.Revision,
                IdempotencyKey = request.IdempotencyKey,
                RequestDigest = requestDigest,
                ExpectedHeadPolicyId = currentHead?.PolicyId,
                ExpectedHeadRevision = currentHead?.Revision,
                ExpectedHeadContentSha256 = currentHead?.ContentSha256,
                ProposedContentSha256 = request.Artifact.ContentSha256,
                ArtifactJson = JsonSerializer.Serialize(request.Artifact, JsonOptions),
                Reason = request.Reason,
                CorrelationId = request.CorrelationId,
                GovernanceDecisionId = decisionReceipt.DecisionId,
                GovernanceDecisionHash = decisionReceipt.RecordHash,
                CreatedAt = now,
            };
            db.NativeAcsPolicyPreparations.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            record.CreatedAt = await db.NativeAcsPolicyPreparations.AsNoTracking()
                .Where(item => item.PreparationId == record.PreparationId)
                .Select(item => item.CreatedAt)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            var rechecked = await LockAndCheckAuthorityAsync(
                db, caller, request, roleGrants, cancellationToken).ConfigureAwait(false);
            if (rechecked.AssignmentId != authorizingGrant.AssignmentId ||
                rechecked.Revision != authorizingGrant.Revision)
                throw ProjectConfigException.Forbidden();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToPreparationReceipt(record, replayed: false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            throw ProjectConfigException.Conflict("The native ACS policy preparation conflicted with another request.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected,
        })
        {
            throw ProjectConfigException.Conflict("The native ACS policy preparation conflicted with a concurrent update.");
        }
        catch (PostgresException exception) when (exception.SqlState is
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation)
        {
            throw ProjectConfigException.Conflict("The native ACS policy preparation conflicted with a concurrent update.");
        }
        catch (InvalidOperationException exception) when (exception.InnerException is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.UniqueViolation,
            },
        })
        {
            throw ProjectConfigException.Conflict("The native ACS policy preparation conflicted with a concurrent update.");
        }
    }

    /// <summary>Returns the stable policy ID of the one head for a scope, or null when the scope has no head.</summary>
    internal async Task<string?> FindPolicyIdAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.NativeAcsPolicyHeads.AsNoTracking()
            .Where(item => item.ScopeType == scopeType && item.ScopeId == scopeId)
            .Select(item => item.PolicyId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<NativeAcsPolicyReadResult?> ReadAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReadHeadAsync(db, scopeType, scopeId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<NativeAcsPolicyReadResult?> ReadPlatformHeadAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        NativeAcsPolicyReadResult? result;
        try
        {
            result = await ReadHeadAsync(
                db, NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            throw Corrupt("The current native ACS policy artifact failed its integrity check.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<NativeAcsPolicyReadResult?> ReadHeadAsync(
        ProjectsConfigDbContext db,
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        CancellationToken cancellationToken)
    {
        var head = await db.NativeAcsPolicyHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (head is null)
            return null;
        var policyId = head.PolicyId;

        var revision = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.PolicyId == policyId &&
                item.Revision == head.CurrentRevision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Corrupt("The current native ACS policy revision is unavailable.");
        var storedFiles = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .Where(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.PolicyId == policyId &&
                item.Revision == head.CurrentRevision)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var artifact = ReconstructArtifact(
            revision,
            storedFiles,
            "The current native ACS policy artifact failed its integrity check.");

        return new NativeAcsPolicyReadResult(
            head.ScopeType,
            head.ScopeId,
            head.PolicyId,
            head.CurrentRevision,
            revision.ContentSha256,
            artifact,
            head.UpdatedAt);
    }

    internal async Task<NativeAcsPolicyReadResult?> ReadPlatformPolicySourceAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken)
            .ConfigureAwait(false);

        var head = await db.NativeAcsPolicyHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (head is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var revisionMetadata = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item =>
                item.ScopeType == head.ScopeType &&
                item.ScopeId == head.ScopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision)
            .Select(item => new
            {
                item.ContentSha256,
                item.CreatedAt,
                ManifestLength = item.ManifestBytes.Length,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Corrupt("The current native ACS policy revision is unavailable.");
        var fileMetadata = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .Where(item =>
                item.ScopeType == head.ScopeType &&
                item.ScopeId == head.ScopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision)
            .OrderBy(item => item.RelativePath)
            .Select(item => new
            {
                item.RelativePath,
                Length = item.Bytes.Length,
            })
            .Take(MaximumPolicySourceFiles + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var totalBytes = (long)revisionMetadata.ManifestLength;
        if (revisionMetadata.ManifestLength is < 1 or > MaximumPolicySourceManifestBytes ||
            fileMetadata.Length > MaximumPolicySourceFiles ||
            fileMetadata.Any(file =>
                string.IsNullOrWhiteSpace(file.RelativePath) ||
                file.RelativePath.Length > 256 ||
                file.Length > MaximumPolicySourceFileBytes))
            throw Corrupt("The current native ACS policy artifact exceeds its storage limits.");
        foreach (var file in fileMetadata)
            totalBytes += file.Length;
        if (totalBytes > MaximumPolicySourceArtifactBytes)
            throw Corrupt("The current native ACS policy artifact exceeds its total size limit.");

        var revision = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == head.ScopeType &&
                item.ScopeId == head.ScopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Corrupt("The current native ACS policy revision is unavailable.");
        var storedFiles = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .Where(item =>
                item.ScopeType == head.ScopeType &&
                item.ScopeId == head.ScopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == head.CurrentRevision)
            .OrderBy(item => item.RelativePath)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (storedFiles.Length != fileMetadata.Length ||
            storedFiles.Where((file, index) =>
                file.RelativePath != fileMetadata[index].RelativePath ||
                file.Bytes.Length != fileMetadata[index].Length).Any())
            throw Corrupt("The current native ACS policy artifact changed during its bounded read.");

        NativeAcsArtifact artifact;
        try
        {
            artifact = ReconstructArtifact(
                revision,
                storedFiles,
                "The current native ACS policy artifact failed its integrity check.");
        }
        catch (ArgumentException)
        {
            throw Corrupt("The current native ACS policy artifact failed its integrity check.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NativeAcsPolicyReadResult(
            head.ScopeType,
            head.ScopeId,
            head.PolicyId,
            head.CurrentRevision,
            revision.ContentSha256,
            artifact,
            head.UpdatedAt);
    }

    internal async Task<NativeAcsPlatformPolicyPreparationReadResult?> ReadPlatformPolicyPreparationAsync(
        Guid preparationId,
        CancellationToken cancellationToken)
    {
        if (preparationId == Guid.Empty)
            throw ReadRequestInvalid();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken)
            .ConfigureAwait(false);

        var metadata = await db.Database
            .SqlQuery<PlatformPolicyPreparationMetadataRow>($"""
                SELECT expected_head_policy_id AS "ExpectedHeadPolicyId",
                       expected_head_revision AS "ExpectedHeadRevision",
                       expected_head_content_sha256 AS "ExpectedHeadContentSha256",
                       proposed_content_sha256 AS "ProposedContentSha256",
                       octet_length(artifact::text)::bigint AS "ArtifactJsonBytes"
                FROM projects_config.native_acs_policy_preparations
                WHERE preparation_id = {preparationId}
                  AND scope_type = 'Platform'
                  AND scope_id = 'default'
                """)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (metadata.ArtifactJsonBytes is < 2 or > MaximumPreparationStorageJsonBytes)
            throw Corrupt("The stored native ACS policy preparation artifact exceeds its storage limits.");

        var artifactJson = await db.NativeAcsPolicyPreparations.AsNoTracking()
            .Where(item =>
                item.PreparationId == preparationId &&
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId)
            .Select(item => item.ArtifactJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw Corrupt("The stored native ACS policy preparation is unavailable.");

        if (System.Text.Encoding.UTF8.GetByteCount(artifactJson) > MaximumPreparationStorageJsonBytes)
            throw Corrupt("The stored native ACS policy preparation artifact exceeds its storage limits.");

        NativeAcsPlatformPolicyExpectedHead? expectedHead;
        if (metadata.ExpectedHeadPolicyId is null &&
            metadata.ExpectedHeadRevision is null &&
            metadata.ExpectedHeadContentSha256 is null)
        {
            expectedHead = null;
        }
        else if (string.IsNullOrWhiteSpace(metadata.ExpectedHeadPolicyId) ||
            metadata.ExpectedHeadRevision is not > 0 ||
            metadata.ExpectedHeadContentSha256 is not { Length: 64 } ||
            !metadata.ExpectedHeadContentSha256.All(c => char.IsAsciiHexDigitLower(c)))
        {
            throw Corrupt("The stored native ACS policy preparation expected head is invalid.");
        }
        else
        {
            expectedHead = new NativeAcsPlatformPolicyExpectedHead(
                metadata.ExpectedHeadPolicyId,
                metadata.ExpectedHeadRevision.Value,
                metadata.ExpectedHeadContentSha256);
        }

        NativeAcsArtifact? artifact;
        try
        {
            artifact = JsonSerializer.Deserialize<NativeAcsArtifact>(artifactJson, JsonOptions);
        }
        catch (JsonException)
        {
            throw Corrupt("The stored native ACS policy preparation artifact is invalid.");
        }

        if (artifact is null ||
            artifact.ManifestBytes.IsDefaultOrEmpty ||
            artifact.Files.IsDefault ||
            artifact.Files.Any(file =>
                file is null ||
                string.IsNullOrWhiteSpace(file.RelativePath) ||
                file.RelativePath.Length > 256 ||
                file.Bytes.IsDefault) ||
            artifact.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
            artifact.Files.Length)
            throw Corrupt("The stored native ACS policy preparation artifact is invalid.");

        if (artifact.ManifestBytes.Length is < 1 or > MaximumPolicySourceManifestBytes ||
            artifact.Files.Length > MaximumPolicySourceFiles ||
            artifact.Files.Any(file => file.Bytes.Length > MaximumPolicySourceFileBytes))
            throw Corrupt("The stored native ACS policy preparation artifact exceeds its storage limits.");

        var totalBytes = (long)artifact.ManifestBytes.Length;
        foreach (var file in artifact.Files)
            totalBytes += file.Bytes.Length;
        if (totalBytes > MaximumPolicySourceArtifactBytes)
            throw Corrupt("The stored native ACS policy preparation artifact exceeds its total size limit.");

        string computedDigest;
        try
        {
            computedDigest = NativeAcsArtifactHash.Compute(artifact);
        }
        catch (ArgumentException)
        {
            throw Corrupt("The stored native ACS policy preparation artifact failed its integrity check.");
        }

        if (!string.Equals(computedDigest, artifact.ContentSha256, StringComparison.Ordinal) ||
            !string.Equals(computedDigest, metadata.ProposedContentSha256, StringComparison.Ordinal))
            throw Corrupt("The stored native ACS policy preparation artifact failed its integrity check.");

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new NativeAcsPlatformPolicyPreparationReadResult(
            artifact,
            metadata.ProposedContentSha256,
            expectedHead);
    }

    internal Task<NativeAcsPolicyRevisionPage> ReadHistoryAsync(
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken) =>
        ReadHistoryAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            pageSize,
            cursor,
            cancellationToken);

    internal async Task<NativeAcsPolicyRevisionPage> ReadHistoryAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var scopeName = PolicyDisplayName(scopeType);
        if (pageSize is < 1 or > 100)
            throw HistoryRequestInvalid(scopeName);
        ValidatePolicyScope(scopeType, scopeId, () => HistoryRequestInvalid(scopeName));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var head = await db.NativeAcsPolicyHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (head is null)
        {
            if (cursor is not null)
                throw HistoryRequestInvalid(scopeName);
            return new NativeAcsPolicyRevisionPage(scopeType, scopeId, null, null, [], null);
        }

        var snapshotRevision = head.CurrentRevision;
        long? beforeRevision = null;
        if (cursor is not null)
        {
            var decoded = DecodeHistoryCursor(cursor, pageSize, scopeType, scopeId);
            if (decoded.PolicyId != head.PolicyId || decoded.SnapshotRevision > head.CurrentRevision)
                throw HistoryRequestInvalid(scopeName);
            snapshotRevision = decoded.SnapshotRevision;
            beforeRevision = decoded.LastRevision;
        }

        var query = db.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision <= snapshotRevision);
        if (beforeRevision is { } lastRevision)
            query = query.Where(item => item.Revision < lastRevision);

        var rows = await query.OrderByDescending(item => item.Revision)
            .Select(item => new NativeAcsPolicyRevisionSummary(
                item.Revision, item.ContentSha256, item.CreatedAt))
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = rows.Take(pageSize).ToImmutableArray();
        var nextCursor = rows.Length > pageSize
            ? EncodeHistoryCursor(
                scopeType,
                scopeId,
                head.PolicyId,
                snapshotRevision,
                pageSize,
                items[^1].Revision)
            : null;
        return new NativeAcsPolicyRevisionPage(
            scopeType,
            scopeId,
            head.PolicyId,
            snapshotRevision,
            items,
            nextCursor);
    }

    internal async Task<NativeAcsPolicyScopePage> ReadProjectPolicyScopesAsync(
        ProjectAuthorizationContext caller,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        if (pageSize is < 1 or > 100 || string.IsNullOrWhiteSpace(caller.TenantId))
            throw ScopeListRequestInvalid();
        var authorizationFingerprint = ScopeListAuthorizationFingerprint(caller);
        var afterScopeId = cursor is null
            ? null
            : DecodeScopeListCursor(cursor, caller, pageSize, authorizationFingerprint).LastScopeId;
        var isTenantAdmin = caller.IsTenantAdmin;
        var hasProjectReaderRole = caller.Grants.Any(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Project &&
            (grant.Role is ProjectAuthorityRole.Owner or
                ProjectAuthorityRole.Contributor or
                ProjectAuthorityRole.Viewer));
        if (!isTenantAdmin && !hasProjectReaderRole)
            return new NativeAcsPolicyScopePage([], null);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        ProjectAuthorityRole[] projectReaderRoles =
        [
            ProjectAuthorityRole.Owner,
            ProjectAuthorityRole.Contributor,
            ProjectAuthorityRole.Viewer,
        ];
        var query =
            from head in db.NativeAcsPolicyHeads.AsNoTracking()
            join project in db.Projects.AsNoTracking()
                on head.ScopeId equals project.ProjectId
            join revision in db.NativeAcsPolicyRevisions.AsNoTracking()
                on new
                {
                    head.ScopeType,
                    head.ScopeId,
                    head.PolicyId,
                    Revision = head.CurrentRevision,
                }
                equals new
                {
                    revision.ScopeType,
                    revision.ScopeId,
                    revision.PolicyId,
                    revision.Revision,
                }
                into matchingRevisions
            from revision in matchingRevisions.DefaultIfEmpty()
            where head.ScopeType == NativeAcsPolicyScopeType.Project &&
                project.TenantId == caller.TenantId &&
                db.TenantMemberships.AsNoTracking().Any(membership =>
                    membership.MembershipId == caller.MembershipId &&
                    membership.Issuer == caller.Issuer &&
                    membership.Subject == caller.ActorId &&
                    membership.TenantId == caller.TenantId &&
                    membership.Revision == caller.MembershipRevision &&
                    membership.State == ProjectAuthorityRecordState.Active) &&
                ((isTenantAdmin &&
                    db.RoleAssignments.AsNoTracking().Any(assignment =>
                        assignment.MembershipId == caller.MembershipId &&
                        assignment.ResourceType == ProjectAuthorityResourceType.Tenant &&
                        assignment.ResourceId == caller.TenantId &&
                        assignment.Role == ProjectAuthorityRole.TenantAdmin &&
                        assignment.State == ProjectAuthorityRecordState.Active)) ||
                 (hasProjectReaderRole &&
                    db.RoleAssignments.AsNoTracking().Any(assignment =>
                        assignment.MembershipId == caller.MembershipId &&
                        assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                        assignment.ResourceId == project.ProjectId &&
                        projectReaderRoles.Contains(assignment.Role) &&
                        assignment.State == ProjectAuthorityRecordState.Active)))
            select new
            {
                head.ScopeType,
                head.ScopeId,
                head.PolicyId,
                Revision = head.CurrentRevision,
                ContentSha256 = revision == null ? null : revision.ContentSha256,
                head.UpdatedAt,
            };
        if (afterScopeId is not null)
            query = query.Where(item => item.ScopeId.CompareTo(afterScopeId) > 0);

        var rows = await query.OrderBy(item => item.ScopeId)
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Any(item => item.ContentSha256 is null))
            throw Corrupt("A current Project Policy revision is unavailable.");

        var items = rows.Take(pageSize)
            .Select(item => new NativeAcsPolicyScopeSummary(
                item.ScopeType,
                item.ScopeId,
                item.PolicyId,
                item.Revision,
                item.ContentSha256!,
                item.UpdatedAt))
            .ToImmutableArray();
        var nextCursor = rows.Length > pageSize
            ? EncodeScopeListCursor(caller, pageSize, items[^1].ScopeId, authorizationFingerprint)
            : null;
        return new NativeAcsPolicyScopePage(items, nextCursor);
    }

    internal Task<NativeAcsPolicyRevisionReadResult?> ReadRevisionAsync(
        long requestedRevision,
        CancellationToken cancellationToken) =>
        ReadRevisionAsync(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            requestedRevision,
            cancellationToken);

    internal async Task<NativeAcsPolicyRevisionReadResult?> ReadRevisionAsync(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        long requestedRevision,
        CancellationToken cancellationToken)
    {
        if (requestedRevision <= 0)
            throw RevisionRequestInvalid(PolicyDisplayName(scopeType));
        ValidatePolicyScope(scopeType, scopeId, () => RevisionRequestInvalid(PolicyDisplayName(scopeType)));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var head = await db.NativeAcsPolicyHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (head is null || requestedRevision > head.CurrentRevision)
            return null;

        var revision = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == requestedRevision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Corrupt("The requested native ACS policy revision is unavailable.");
        var storedFiles = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .Where(item =>
                item.ScopeType == scopeType &&
                item.ScopeId == scopeId &&
                item.PolicyId == head.PolicyId &&
                item.Revision == requestedRevision)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var artifact = ReconstructArtifact(
            revision,
            storedFiles,
            "The requested native ACS policy artifact failed its integrity check.");
        return new NativeAcsPolicyRevisionReadResult(
            head.ScopeType,
            head.ScopeId,
            head.PolicyId,
            revision.Revision,
            revision.ContentSha256,
            artifact,
            revision.CreatedAt);
    }

    internal static int ParseHistoryPageSize(string? value, string scope = "Platform")
    {
        if (value is null)
            return 20;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize) ||
            pageSize is < 1 or > 100)
            throw HistoryRequestInvalid(scope);
        return pageSize;
    }

    internal static int ParseScopeListPageSize(string? value)
    {
        if (value is null)
            return 20;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pageSize) ||
            pageSize is < 1 or > 100)
            throw ScopeListRequestInvalid();
        return pageSize;
    }

    internal static long ParseRevision(string value, string scope = "Platform")
    {
        if (value.Length == 0 ||
            value.Length > 1 && value[0] == '0' ||
            value.Any(character => character is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision <= 0)
            throw RevisionRequestInvalid(scope);
        return revision;
    }

    internal static HistoryCursor DecodeHistoryCursor(string value, int pageSize) =>
        DecodeHistoryCursor(
            value,
            pageSize,
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId);

    internal static HistoryCursor DecodeHistoryCursor(
        string value,
        int pageSize,
        NativeAcsPolicyScopeType scopeType,
        string scopeId)
    {
        var invalid = () => HistoryRequestInvalid(PolicyDisplayName(scopeType));
        if (value.Length is 0 or > 2048 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw invalid();
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw invalid(),
            };
            var payload = Convert.FromBase64String(base64);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    throw invalid();
            if (names.Count != 8 ||
                !names.SetEquals(["version", "order", "scopeType", "scopeId", "policyId",
                    "snapshotRevision", "pageSize", "lastRevision"]))
                throw invalid();
            var cursor = JsonSerializer.Deserialize<HistoryCursor>(payload, HistoryCursorJsonOptions);
            if (cursor is null ||
                cursor.Version != HistoryCursorVersion ||
                cursor.Order != HistoryCursorOrder ||
                cursor.ScopeType != PolicyScopeName(scopeType) ||
                cursor.ScopeId != scopeId ||
                string.IsNullOrEmpty(cursor.PolicyId) ||
                cursor.SnapshotRevision <= 0 ||
                cursor.PageSize != pageSize ||
                cursor.LastRevision <= 0 ||
                cursor.LastRevision > cursor.SnapshotRevision)
                throw invalid();
            return cursor;
        }
        catch (FormatException)
        {
            throw invalid();
        }
        catch (JsonException)
        {
            throw invalid();
        }
    }

    private static readonly JsonSerializerOptions HistoryCursorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static string EncodeHistoryCursor(string policyId, long snapshotRevision, int pageSize, long lastRevision) =>
        EncodeHistoryCursor(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            policyId,
            snapshotRevision,
            pageSize,
            lastRevision);

    internal static string EncodeHistoryCursor(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        string policyId,
        long snapshotRevision,
        int pageSize,
        long lastRevision)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new HistoryCursor(
                HistoryCursorVersion,
                HistoryCursorOrder,
                PolicyScopeName(scopeType),
                scopeId,
                policyId,
                snapshotRevision,
                pageSize,
                lastRevision),
            HistoryCursorJsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private ScopeListCursor DecodeScopeListCursor(
        string value,
        ProjectAuthorizationContext caller,
        int pageSize,
        string authorizationFingerprint)
    {
        if (value.Length is 0 or > 4096 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw ScopeListRequestInvalid();
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw ScopeListRequestInvalid(),
            };
            var payload = _scopeListCursorProtector.Unprotect(Convert.FromBase64String(base64));
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw ScopeListRequestInvalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    throw ScopeListRequestInvalid();
            if (names.Count != 10 ||
                !names.SetEquals([
                    "version", "order", "tenantId", "membershipId", "membershipRevision",
                    "actorIssuer", "actorId", "authorizationFingerprint", "pageSize", "lastScopeId",
                ]))
                throw ScopeListRequestInvalid();
            var cursor = JsonSerializer.Deserialize<ScopeListCursor>(payload, HistoryCursorJsonOptions);
            if (cursor is null ||
                cursor.Version != ScopeListCursorVersion ||
                cursor.Order != ScopeListCursorOrder ||
                cursor.TenantId != caller.TenantId ||
                cursor.MembershipId != caller.MembershipId ||
                cursor.MembershipRevision != caller.MembershipRevision ||
                cursor.ActorIssuer != caller.Issuer ||
                cursor.ActorId != caller.ActorId ||
                cursor.AuthorizationFingerprint != authorizationFingerprint ||
                cursor.PageSize != pageSize ||
                !Guid.TryParseExact(cursor.LastScopeId, "N", out _))
                throw ScopeListRequestInvalid();
            return cursor;
        }
        catch (FormatException)
        {
            throw ScopeListRequestInvalid();
        }
        catch (JsonException)
        {
            throw ScopeListRequestInvalid();
        }
        catch (CryptographicException)
        {
            throw ScopeListRequestInvalid();
        }
    }

    private string EncodeScopeListCursor(
        ProjectAuthorizationContext caller,
        int pageSize,
        string lastScopeId,
        string authorizationFingerprint)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ScopeListCursor(
                ScopeListCursorVersion,
                ScopeListCursorOrder,
                caller.TenantId,
                caller.MembershipId,
                caller.MembershipRevision,
                caller.Issuer,
                caller.ActorId,
                authorizationFingerprint,
                pageSize,
                lastScopeId),
            HistoryCursorJsonOptions);
        return Convert.ToBase64String(_scopeListCursorProtector.Protect(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ScopeListAuthorizationFingerprint(ProjectAuthorizationContext caller)
    {
        var grants = caller.Grants
            .Where(grant =>
                (grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
                    grant.ResourceId == caller.TenantId &&
                    grant.Role == ProjectAuthorityRole.TenantAdmin) ||
                (grant.ResourceType == ProjectAuthorityResourceType.Project &&
                    (grant.Role is ProjectAuthorityRole.Owner or
                        ProjectAuthorityRole.Contributor or
                        ProjectAuthorityRole.Viewer)))
            .OrderBy(grant => grant.ResourceType)
            .ThenBy(grant => grant.ResourceId, StringComparer.Ordinal)
            .ThenBy(grant => grant.Role)
            .ThenBy(grant => grant.AssignmentId)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(grants, HistoryCursorJsonOptions)));
    }

    private static string PolicyScopeName(NativeAcsPolicyScopeType scopeType) =>
        scopeType switch
        {
            NativeAcsPolicyScopeType.Platform => "platform",
            NativeAcsPolicyScopeType.Project => "project",
            _ => throw new ArgumentOutOfRangeException(nameof(scopeType)),
        };

    private static string PolicyDisplayName(NativeAcsPolicyScopeType scopeType) =>
        scopeType switch
        {
            NativeAcsPolicyScopeType.Platform => "Platform",
            NativeAcsPolicyScopeType.Project => "Project",
            _ => throw new ArgumentOutOfRangeException(nameof(scopeType)),
        };

    private static void ValidatePolicyScope(
        NativeAcsPolicyScopeType scopeType,
        string scopeId,
        Func<ProjectConfigException> invalidRequest)
    {
        if (!Enum.IsDefined(scopeType) ||
            scopeType == NativeAcsPolicyScopeType.Platform &&
            scopeId != ProjectAuthorizationOwner.PlatformResourceId ||
            scopeType == NativeAcsPolicyScopeType.Project &&
            !Guid.TryParseExact(scopeId, "N", out _))
            throw invalidRequest();
    }

    private static NativeAcsArtifact ReconstructArtifact(
        NativeAcsPolicyRevisionRecord revision,
        NativeAcsPolicyArtifactFileRecord[] storedFiles,
        string integrityFailure)
    {
        var artifact = new NativeAcsArtifact(
            revision.ManifestBytes.ToImmutableArray(),
            storedFiles
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .Select(item => new NativeAcsArtifactFile(item.RelativePath, item.Bytes.ToImmutableArray()))
                .ToImmutableArray(),
            revision.ContentSha256);
        if (!string.Equals(
                NativeAcsArtifactHash.Compute(artifact),
                revision.ContentSha256,
                StringComparison.Ordinal))
            throw Corrupt(integrityFailure);
        return artifact;
    }

    private static ProjectConfigException Corrupt(string message) =>
        new("native_acs_policy_corrupt", message, StatusCodes.Status500InternalServerError);

    internal static ProjectConfigException HistoryRequestInvalid(string scope = "Platform") =>
        ProjectConfigException.Invalid(
            "native_acs_policy_history_request_invalid",
            $"The {scope} Policy history request is invalid.");

    internal static ProjectConfigException RevisionRequestInvalid(string scope = "Platform") =>
        ProjectConfigException.Invalid(
            "native_acs_policy_revision_request_invalid",
            $"The {scope} Policy revision request is invalid.");

    internal static ProjectConfigException ScopeListRequestInvalid() =>
        ProjectConfigException.Invalid(
            "native_acs_policy_scope_list_request_invalid",
            "The Project Policy list request is invalid.");

    internal static ProjectConfigException ReadRequestInvalid(string scope = "Platform") =>
        ProjectConfigException.Invalid(
            "native_acs_policy_read_request_invalid",
            $"The {scope} Policy read request is invalid.");

    private sealed record PlatformPolicyPreparationMetadataRow(
        string? ExpectedHeadPolicyId,
        long? ExpectedHeadRevision,
        string? ExpectedHeadContentSha256,
        string ProposedContentSha256,
        long ArtifactJsonBytes);

    internal sealed record HistoryCursor(
        int Version,
        string Order,
        string ScopeType,
        string ScopeId,
        string PolicyId,
        long SnapshotRevision,
        int PageSize,
        long LastRevision);

    private sealed record ScopeListCursor(
        int Version,
        string Order,
        string TenantId,
        Guid MembershipId,
        long MembershipRevision,
        string ActorIssuer,
        string ActorId,
        string AuthorizationFingerprint,
        int PageSize,
        string LastScopeId);

    private sealed record NativeAcsPolicyHeadSnapshot(
        string PolicyId,
        long Revision,
        string ContentSha256);

    internal static IReadOnlyList<ProjectAuthorizationGrant> EnsureWriteAuthorized(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        return EnsureWriteAuthorized(caller, request.ScopeType, request.ScopeId);
    }

    internal static IReadOnlyList<ProjectAuthorizationGrant> EnsureWriteAuthorized(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyScopeType scopeType,
        string scopeId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        return GetAuthorizingGrants(caller, scopeType, scopeId);
    }

    private static IReadOnlyList<ProjectAuthorizationGrant> GetAuthorizingGrants(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyScopeType scopeType,
        string scopeId)
    {
        var effective = caller.ToEffectiveResponse().EffectiveAuthority;
        bool HasPermission(ProjectAuthorizationGrant grant, ProjectAuthorizationPermission permission) =>
            effective.Any(item =>
                item.ResourceType == grant.ResourceType &&
                item.ResourceId == grant.ResourceId &&
                item.Permissions.Any(candidate =>
                    candidate.Permission == permission && candidate.RoleRevision == grant.Revision));

        var candidates = scopeType switch
        {
            NativeAcsPolicyScopeType.Platform => caller.Grants.Where(grant =>
                    grant.ResourceType == ProjectAuthorityResourceType.Platform &&
                    grant.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                    grant.Role == ProjectAuthorityRole.PlatformAdmin &&
                    HasPermission(grant, ProjectAuthorizationPermission.WritePlatformRuntimeDefaults))
                .ToArray(),
            NativeAcsPolicyScopeType.Project => caller.Grants.Where(grant =>
                    (grant.ResourceType == ProjectAuthorityResourceType.Project &&
                        grant.ResourceId == scopeId &&
                        grant.Role == ProjectAuthorityRole.Owner &&
                        HasPermission(grant, ProjectAuthorizationPermission.WriteProjects)) ||
                    (grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
                        grant.ResourceId == caller.TenantId &&
                        grant.Role == ProjectAuthorityRole.TenantAdmin &&
                        HasPermission(grant, ProjectAuthorizationPermission.WriteProjects)))
                .OrderBy(grant => grant.ResourceType)
                .ThenBy(grant => grant.AssignmentId)
                .ToArray(),
            _ => [],
        };
        if (candidates.Length == 0)
            throw ProjectConfigException.Forbidden();
        return candidates;
    }

    private static async Task<ProjectAuthorizationGrant> LockAndCheckAuthorityAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        IReadOnlyList<ProjectAuthorizationGrant> roleGrants,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT assignment_id, revision, authorization_result
            FROM projects_config.lock_native_acs_policy_authority(
                @membership_id, @issuer, @actor_id, @tenant_id, @membership_revision,
                @scope_type, @scope_id, @grant_ids, @grant_resource_types, @grant_resource_ids,
                @grant_roles, @grant_revisions)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("membership_id", caller.MembershipId);
        command.Parameters.AddWithValue("issuer", caller.Issuer);
        command.Parameters.AddWithValue("actor_id", caller.ActorId);
        command.Parameters.AddWithValue("tenant_id", caller.TenantId);
        command.Parameters.AddWithValue("membership_revision", caller.MembershipRevision);
        command.Parameters.AddWithValue("scope_type", request.ScopeType.ToString());
        command.Parameters.AddWithValue("scope_id", request.ScopeId);
        command.Parameters.AddWithValue(
            "grant_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            roleGrants.Select(grant => grant.AssignmentId).ToArray());
        command.Parameters.AddWithValue(
            "grant_resource_types",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            roleGrants.Select(grant => grant.ResourceType.ToString()).ToArray());
        command.Parameters.AddWithValue(
            "grant_resource_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            roleGrants.Select(grant => grant.ResourceId).ToArray());
        command.Parameters.AddWithValue(
            "grant_roles",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            roleGrants.Select(grant => grant.Role.ToString()).ToArray());
        command.Parameters.AddWithValue(
            "grant_revisions",
            NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            roleGrants.Select(grant => grant.Revision).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        if (reader.GetString(2) == "not_found")
            throw ProjectConfigException.NotFound();
        if (reader.GetString(2) != "authorized")
            throw ProjectConfigException.Forbidden();
        var assignmentId = reader.GetGuid(0);
        var revision = reader.GetInt64(1);
        return roleGrants.Single(grant =>
            grant.AssignmentId == assignmentId && grant.Revision == revision);
    }

    private static async Task<NativeAcsPolicyWriteReceipt?> FindReceiptAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        string scopeDigest,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        var prior = await db.NativeAcsPolicyIdempotency.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScopeDigest == scopeDigest, cancellationToken)
            .ConfigureAwait(false);
        if (prior is null)
            return null;
        if (prior.ScopeType != request.ScopeType ||
            prior.ScopeId != request.ScopeId ||
            prior.PolicyId != request.PolicyId ||
            prior.ActorIssuer != caller.Issuer ||
            prior.ActorId != caller.ActorId ||
            prior.IdempotencyKey != request.IdempotencyKey ||
            prior.RequestDigest != requestDigest)
            throw IdempotencyConflict();

        var replay = JsonSerializer.Deserialize<NativeAcsPolicyWriteReceipt>(prior.ReceiptJson, JsonOptions)
            ?? throw new InvalidOperationException("The saved native ACS policy receipt could not be read.");
        return replay with { Replayed = true };
    }

    private static async Task<NativeAcsPolicyPreparationReceipt?> FindPreparationReceiptAsync(
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        string scopeDigest,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        var prior = await db.NativeAcsPolicyPreparations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScopeDigest == scopeDigest, cancellationToken)
            .ConfigureAwait(false);
        if (prior is null)
            return null;
        if (prior.ScopeType != request.ScopeType ||
            prior.ScopeId != request.ScopeId ||
            prior.PolicyId != request.PolicyId ||
            prior.ActorIssuer != caller.Issuer ||
            prior.ActorId != caller.ActorId ||
            prior.IdempotencyKey != request.IdempotencyKey ||
            prior.RequestDigest != requestDigest)
            throw IdempotencyConflict();

        return ToPreparationReceipt(prior, replayed: true);
    }

    private static NativeAcsPolicyPreparationReceipt ToPreparationReceipt(
        NativeAcsPolicyPreparationRecord record,
        bool replayed) =>
        new(
            record.PreparationId,
            record.ScopeType,
            record.ScopeId,
            record.PolicyId,
            record.ExpectedHeadPolicyId,
            record.ExpectedHeadRevision,
            record.ExpectedHeadContentSha256,
            record.ProposedContentSha256,
            "pending",
            Checkpoint: null,
            record.CreatedAt,
            replayed,
            record.GovernanceDecisionId,
            record.GovernanceDecisionHash);

    private static async Task<NativeAcsPolicyHeadSnapshot?> LockPreparationHeadAsync(
        ProjectsConfigDbContext db,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT head.policy_id, head.current_revision, revision.content_sha256
            FROM projects_config.native_acs_policy_heads AS head
            LEFT JOIN projects_config.native_acs_policy_revisions AS revision
              ON revision.scope_type = head.scope_type
             AND revision.scope_id = head.scope_id
             AND revision.policy_id = head.policy_id
             AND revision.revision = head.current_revision
            WHERE head.scope_type = @scope_type
              AND head.scope_id = @scope_id
            FOR UPDATE OF head
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_type", request.ScopeType.ToString());
        command.Parameters.AddWithValue("scope_id", request.ScopeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.IsDBNull(2))
            throw Corrupt("The native ACS policy head points to a missing revision.");
        var result = new NativeAcsPolicyHeadSnapshot(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw Corrupt("The native ACS policy scope has more than one head.");
        return result;
    }

    private static void EnsureExpectedHead(
        NativeAcsPolicyWriteRequest request,
        NativeAcsPolicyHeadSnapshot? currentHead)
    {
        if (currentHead?.Revision != request.ExpectedRevision ||
            currentHead is not null && currentHead.PolicyId != request.PolicyId)
            throw ProjectConfigException.Conflict("The native ACS policy head has changed.");
    }

    private static void EnsurePlatformPreparationRequest(NativeAcsPolicyWriteRequest request)
    {
        if (request.ScopeType != NativeAcsPolicyScopeType.Platform ||
            request.ScopeId != ProjectAuthorizationOwner.PlatformResourceId)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "Only the Platform Policy can be prepared.");
    }

    private static async Task<long?> LockHeadAsync(
        ProjectsConfigDbContext db,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT current_revision
            FROM projects_config.native_acs_policy_heads
            WHERE scope_type = @scope_type
              AND scope_id = @scope_id
              AND policy_id = @policy_id
            FOR UPDATE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_type", request.ScopeType.ToString());
        command.Parameters.AddWithValue("scope_id", request.ScopeId);
        command.Parameters.AddWithValue("policy_id", request.PolicyId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // The scope digest locates one caller's retry slot.
    internal static string ComputeScopeDigest(ProjectAuthorizationContext caller, NativeAcsPolicyWriteRequest request) =>
        ScopeDigest(caller, request);

    internal static string ComputeRequestDigest(NativeAcsPolicyWriteRequest request) =>
        RequestDigest(request);

    private static string ScopeDigest(ProjectAuthorizationContext caller, NativeAcsPolicyWriteRequest request) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ScopeType,
            request.ScopeId,
            request.PolicyId,
            caller.Issuer,
            caller.ActorId,
            request.IdempotencyKey,
        }, JsonOptions));

    // The request digest binds that slot to the expected revision and request metadata.
    private static string RequestDigest(NativeAcsPolicyWriteRequest request) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ScopeType,
            request.ScopeId,
            request.PolicyId,
            request.ExpectedRevision,
            request.Artifact.ContentSha256,
            request.Reason,
            request.CorrelationId,
        }, JsonOptions));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateRequest(NativeAcsPolicyWriteRequest request)
    {
        ValidateWriteMetadata(request);
        ValidateArtifact(request.Artifact);
    }

    private static void ValidatePreparationRequest(NativeAcsPolicyWriteRequest request)
    {
        ValidateRequest(request);
        var artifact = request.Artifact;
        if (artifact.ManifestBytes.Length > MaximumPolicySourceManifestBytes ||
            artifact.Files.Length > MaximumPolicySourceFiles ||
            artifact.Files.Any(file => file.Bytes.Length > MaximumPolicySourceFileBytes) ||
            artifact.ManifestBytes.Length + artifact.Files.Sum(file => (long)file.Bytes.Length) >
                MaximumPolicySourceArtifactBytes)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy artifact exceeds its storage limits.");
    }

    internal static void ValidateWriteMetadata(NativeAcsPolicyWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.ScopeType) ||
            string.IsNullOrWhiteSpace(request.ScopeId) ||
            request.ScopeId.Length > 256 ||
            !IsIdentifier(request.PolicyId, 128) ||
            request.ExpectedRevision is <= 0 ||
            !IsIdentifier(request.IdempotencyKey, 128) ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > 1024 ||
            !IsIdentifier(request.CorrelationId, 128) ||
            request.Artifact is null)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy write request is invalid.");

        if (request.ScopeType == NativeAcsPolicyScopeType.Platform &&
            request.ScopeId != ProjectAuthorizationOwner.PlatformResourceId)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The platform policy scope ID is invalid.");
    }

    private static void ValidateArtifact(NativeAcsArtifact artifact)
    {
        if (artifact.ManifestBytes.IsDefaultOrEmpty ||
            artifact.Files.IsDefault ||
            artifact.Files.Any(file =>
            file is null ||
            string.IsNullOrWhiteSpace(file.RelativePath) ||
            file.RelativePath.Length > 256 ||
            file.Bytes.IsDefault) ||
            artifact.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
            artifact.Files.Length)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy artifact is invalid.");

        string contentHash;
        try
        {
            contentHash = NativeAcsArtifactHash.Compute(artifact);
        }
        catch (ArgumentException)
        {
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy artifact is invalid.");
        }
        if (!string.Equals(artifact.ContentSha256, contentHash, StringComparison.Ordinal))
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy artifact is invalid.");
    }

    private static bool IsIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static ProjectConfigException IdempotencyConflict() =>
        ProjectConfigException.Conflict(
            "idempotency_conflict",
            "The idempotency key was already used for a different native ACS policy request.");
}
