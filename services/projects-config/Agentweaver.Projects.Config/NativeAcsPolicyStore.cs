using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

internal sealed class NativeAcsPolicyStore(
    IDbContextFactory<ProjectsConfigDbContext> contextFactory,
    TimeProvider timeProvider)
{
    private const string PolicyRevisionEventType = "native_acs.policy_revision_committed.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<NativeAcsPolicyWriteReceipt> WriteAsync(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        var roleGrants = GetAuthorizingGrants(caller, request);
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
        var prior = await db.NativeAcsPolicyIdempotency.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ScopeDigest == scopeDigest, cancellationToken)
            .ConfigureAwait(false);
        if (prior is not null)
        {
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
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay with { Replayed = true };
        }

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

    private static IReadOnlyList<ProjectAuthorizationGrant> GetAuthorizingGrants(
        ProjectAuthorizationContext caller,
        NativeAcsPolicyWriteRequest request)
    {
        var effective = caller.ToEffectiveResponse().EffectiveAuthority;
        bool HasPermission(ProjectAuthorizationGrant grant, ProjectAuthorizationPermission permission) =>
            effective.Any(item =>
                item.ResourceType == grant.ResourceType &&
                item.ResourceId == grant.ResourceId &&
                item.Permissions.Any(candidate =>
                    candidate.Permission == permission && candidate.RoleRevision == grant.Revision));

        var candidates = request.ScopeType switch
        {
            NativeAcsPolicyScopeType.Platform => caller.Grants.Where(grant =>
                    grant.ResourceType == ProjectAuthorityResourceType.Platform &&
                    grant.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                    grant.Role == ProjectAuthorityRole.PlatformAdmin &&
                    HasPermission(grant, ProjectAuthorizationPermission.WritePlatformRuntimeDefaults))
                .ToArray(),
            NativeAcsPolicyScopeType.Project => caller.Grants.Where(grant =>
                    (grant.ResourceType == ProjectAuthorityResourceType.Project &&
                        grant.ResourceId == request.ScopeId &&
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
        if (request.Artifact.ManifestBytes.IsDefaultOrEmpty ||
            request.Artifact.Files.IsDefault ||
            request.Artifact.Files.Any(file =>
            file is null ||
            string.IsNullOrWhiteSpace(file.RelativePath) ||
            file.RelativePath.Length > 256 ||
            file.Bytes.IsDefault) ||
            request.Artifact.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() !=
            request.Artifact.Files.Length)
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy artifact is invalid.");

        string contentHash;
        try
        {
            contentHash = NativeAcsArtifactHash.Compute(request.Artifact);
        }
        catch (ArgumentException)
        {
            throw ProjectConfigException.Invalid(
                "native_acs_policy_invalid",
                "The native ACS policy artifact is invalid.");
        }
        if (!string.Equals(request.Artifact.ContentSha256, contentHash, StringComparison.Ordinal))
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
