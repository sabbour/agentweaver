using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal sealed class ModelProviderCredentialPublicationV2Exception(string code, int statusCode)
    : Exception($"Credential publication v2 failed ({code}).")
{
    internal string Code { get; } = code;
    internal int StatusCode { get; } = statusCode;

    internal static ModelProviderCredentialPublicationV2Exception InvalidRequest(string code = "credential_publication_request_invalid") =>
        new(code, 400);

    internal static ModelProviderCredentialPublicationV2Exception Forbidden(string code = "forbidden") =>
        new(code, 403);

    internal static ModelProviderCredentialPublicationV2Exception NotFound(string code = "model_provider_draft_not_found") =>
        new(code, 404);

    internal static ModelProviderCredentialPublicationV2Exception Conflict(string code = "model_provider_draft_conflict") =>
        new(code, 409);

    internal static ModelProviderCredentialPublicationV2Exception Unavailable() =>
        new("credential_publication_unavailable", 503);
}

internal sealed class ModelProviderCredentialPublicationV2Service(
    ProjectsConfigDbContext db,
    TimeProvider timeProvider,
    IRuntimeRateCardOwnerClient? rateCardOwnerClient = null)
{
    private const int MaxRetries = 3;
    private static readonly JsonSerializerOptions RateCardPinJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    internal Task<ModelProviderCredentialOperationReadResponse> ReadOperationAsync(
        ModelProviderCredentialOperationReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
            throw ModelProviderCredentialPublicationV2Exception.InvalidRequest();

        return ExecuteWithRetryAsync(async () =>
        {
            var authority = await ResolveAndLockAuthorityAsync(
                request.Issuer, request.ActorId, request.Scope, request.ProjectId,
                request.TenantSelector, request.TenantClaim, cancellationToken).ConfigureAwait(false);
            var operation = await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleOrDefaultAsync(op => op.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false);

            if (operation is null ||
                operation.Scope != request.Scope ||
                operation.ProjectId != request.ProjectId ||
                !StringComparer.Ordinal.Equals(operation.ProviderId, request.ProviderId) ||
                !StringComparer.Ordinal.Equals(operation.OriginalTenant, authority.Membership.TenantId) ||
                !StringComparer.Ordinal.Equals(operation.Issuer, request.Issuer) ||
                !StringComparer.Ordinal.Equals(operation.ActorId, request.ActorId))
            {
                return new ModelProviderCredentialOperationReadResponse(
                    request, authority.Membership.TenantId, null, null, null, null);
            }

            var outcome = operation.OperationKind switch
            {
                ModelProviderCredentialOperationKind.Publish => "published",
                ModelProviderCredentialOperationKind.Cancel => "cancelled",
                ModelProviderCredentialOperationKind.Disable => "disabled",
                _ => null,
            };
            return new ModelProviderCredentialOperationReadResponse(
                request,
                authority.Membership.TenantId,
                outcome,
                outcome == "published" ? ToPublicationReceipt(operation) : null,
                operation.ProviderRevision,
                outcome == "published" ? true : null);
        }, cancellationToken);
    }

    internal Task<ModelProviderCredentialPublicationPreflightResponse> PreflightAsync(
        ModelProviderCredentialPublicationPreflightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
            throw ModelProviderCredentialPublicationV2Exception.InvalidRequest();

        return ExecuteWithRetryAsync(async () =>
        {
            var authority = await ResolveAndLockAuthorityAsync(
                request.Issuer, request.ActorId, request.Scope, request.ProjectId,
                request.TenantSelector, request.TenantClaim, cancellationToken).ConfigureAwait(false);

            long providerRevision = 0;
            var providerState = "Draft";
            Guid? credentialSlotId = null;
            long? credentialRevision = null;

            if (request.Mode == "draft-snapshot")
            {
                var head = await LockProviderHeadAsync(
                    request.ProviderId, request.Scope, request.ProjectId, forUpdate: false, cancellationToken)
                    .ConfigureAwait(false);
                providerRevision = head.CurrentRevision;
                providerState = head.State.ToString();
                var current = await db.ModelProviderDraftRevisions.AsNoTracking()
                    .SingleAsync(r => r.ProviderId == head.ProviderId && r.ProviderRevision == head.CurrentRevision, cancellationToken)
                    .ConfigureAwait(false);
                credentialSlotId = current.CredentialSlotId;
            }

            var existingOp = await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleOrDefaultAsync(op => op.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false);
            var isCancelled = existingOp?.OperationKind == ModelProviderCredentialOperationKind.Cancel;
            var existingReceipt = existingOp?.OperationKind == ModelProviderCredentialOperationKind.Publish
                ? ToPublicationReceipt(existingOp)
                : null;

            var preflightId = Guid.NewGuid();
            db.ModelProviderCredentialPreflights.Add(new ModelProviderCredentialPreflightRecord
            {
                PreflightId = preflightId,
                OperationId = request.OperationId,
                Mode = request.Mode,
                Scope = request.Scope,
                ProjectId = request.ProjectId,
                ProviderId = request.ProviderId,
                Issuer = request.Issuer,
                ActorId = request.ActorId,
                TenantId = authority.Membership.TenantId,
                MembershipId = authority.Membership.MembershipId,
                MembershipRevision = authority.Membership.Revision,
                AssignmentId = authority.Assignment.AssignmentId,
                AssignmentRevision = authority.Assignment.Revision,
                IssuedAt = timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new ModelProviderCredentialPublicationPreflightResponse(
                request,
                preflightId,
                authority.Membership.TenantId,
                authority.Membership.MembershipId,
                authority.Membership.Revision,
                authority.Assignment.AssignmentId,
                authority.Assignment.Revision,
                providerRevision,
                providerState,
                credentialSlotId,
                credentialRevision,
                existingReceipt,
                isCancelled);
        }, cancellationToken);
    }

    internal Task<ModelProviderCredentialPublicationReceipt> PublishAsync(
        ModelProviderCredentialPublicationV2Request request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
            throw ModelProviderCredentialPublicationV2Exception.InvalidRequest();

        var fingerprint = Hash(new
        {
            kind = "publish-v2",
            request.OperationId,
            request.ExpectedProviderRevision,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.CredentialVersion,
            slotId = request.CredentialIdentity.CredentialSlotId,
            secretId = request.CredentialReference.Id,
            request.Issuer,
            request.ActorId,
            request.OriginalTenant,
        });

        return ExecuteWithRetryAsync(async () =>
        {
            var authority = await ResolveAndLockAuthorityAsync(
                request.Issuer, request.ActorId, request.Scope, request.ProjectId,
                request.TenantSelector, request.TenantClaim, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(authority.Membership.TenantId, request.OriginalTenant))
                throw ModelProviderCredentialPublicationV2Exception.Forbidden();

            var now = timeProvider.GetUtcNow();
            await ConsumePreflightAsync(
                request.PreflightId,
                ModelProviderCredentialOperationKind.Publish,
                request.OperationId,
                request.Scope,
                request.ProjectId,
                request.ProviderId,
                request.Issuer,
                request.ActorId,
                authority,
                now,
                cancellationToken).ConfigureAwait(false);

            var head = await LockProviderHeadAsync(
                request.ProviderId, request.Scope, request.ProjectId, forUpdate: true, cancellationToken)
                .ConfigureAwait(false);

            var existingOp = await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleOrDefaultAsync(op => op.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingOp is not null)
            {
                if (existingOp.OperationKind == ModelProviderCredentialOperationKind.Cancel)
                    throw ModelProviderCredentialPublicationV2Exception.Conflict("credential_operation_cancelled");
                if (existingOp.OperationKind == ModelProviderCredentialOperationKind.Publish &&
                    StringComparer.Ordinal.Equals(existingOp.RequestFingerprint, fingerprint))
                    return ToPublicationReceipt(existingOp);
                throw ModelProviderCredentialPublicationV2Exception.Conflict();
            }

            if (head.CurrentRevision != request.ExpectedProviderRevision)
                throw ModelProviderCredentialPublicationV2Exception.Conflict();

            var current = await db.ModelProviderDraftRevisions.AsNoTracking()
                .SingleAsync(r => r.ProviderId == head.ProviderId && r.ProviderRevision == head.CurrentRevision, cancellationToken)
                .ConfigureAwait(false);

            RuntimeRateCardPin? verifiedPin = null;
            if (current.RateCardId is { Length: > 0 } rateCardId &&
                current.RateCardVersion is { Length: > 0 } rateCardVersion &&
                rateCardOwnerClient is not null)
            {
                verifiedPin = await rateCardOwnerClient
                    .ReadPinAsync(rateCardId, rateCardVersion, cancellationToken)
                    .ConfigureAwait(false);
            }

            var nextState = verifiedPin is not null
                ? ModelProviderDraftState.Available
                : ModelProviderDraftState.Draft;
            var rateCardPinJson = verifiedPin is not null
                ? JsonSerializer.Serialize(verifiedPin, RateCardPinJsonOptions)
                : null;

            var nextRevision = head.CurrentRevision + 1;

            var updated = await db.ModelProviderDrafts
                .Where(h => h.ProviderId == head.ProviderId && h.CurrentRevision == head.CurrentRevision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(h => h.State, nextState)
                    .SetProperty(h => h.CurrentRevision, nextRevision)
                    .SetProperty(h => h.UpdatedByActorId, request.ActorId)
                    .SetProperty(h => h.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (updated != 1)
                throw ModelProviderCredentialPublicationV2Exception.Conflict();

            var opRecord = new ModelProviderCredentialOperationRecord
            {
                OperationId = request.OperationId,
                OperationKind = ModelProviderCredentialOperationKind.Publish,
                ProviderId = head.ProviderId,
                Scope = head.Scope,
                ProjectId = head.ProjectId,
                ProviderRevision = nextRevision,
                ExpectedProviderRevision = request.ExpectedProviderRevision,
                CredentialSlotId = request.CredentialIdentity.CredentialSlotId,
                CredentialSecretId = request.CredentialReference.Id,
                CredentialVersion = request.CredentialVersion,
                ConfigurationHash = current.ConfigurationHash,
                ProviderState = nextState.ToString(),
                RateCardId = verifiedPin?.RateCardId,
                RateCardVersion = verifiedPin?.RateCardVersion,
                RateCardPinHash = verifiedPin?.ContentHash,
                OriginalTenant = request.OriginalTenant,
                OriginalMembershipId = request.OriginalMembershipId,
                OriginalMembershipRevision = request.OriginalMembershipRevision,
                CurrentMembershipId = authority.Membership.MembershipId,
                CurrentMembershipRevision = authority.Membership.Revision,
                CurrentAssignmentId = authority.Assignment.AssignmentId,
                CurrentAssignmentRevision = authority.Assignment.Revision,
                Issuer = request.Issuer,
                ActorId = request.ActorId,
                RequestFingerprint = fingerprint,
                RecordedAt = now,
            };
            db.ModelProviderCredentialOperations.Add(opRecord);
            db.ModelProviderDraftRevisions.Add(new ModelProviderDraftRevisionRecord
            {
                ProviderId = current.ProviderId,
                Scope = current.Scope,
                ProjectId = current.ProjectId,
                ProviderRevision = nextRevision,
                OperationId = request.OperationId,
                RequestFingerprint = fingerprint,
                DisplayName = current.DisplayName,
                ProviderType = current.ProviderType,
                BaseUrl = current.BaseUrl,
                Deployment = current.Deployment,
                WireApi = current.WireApi,
                AzureApiVersion = current.AzureApiVersion,
                ReasoningEfforts = current.ReasoningEfforts,
                PromptCapacityTokens = current.PromptCapacityTokens,
                ConfigurationHash = current.ConfigurationHash,
                CredentialVersion = request.CredentialVersion,
                CredentialSlotId = request.CredentialIdentity.CredentialSlotId,
                CredentialSecretId = request.CredentialReference.Id,
                RateCardId = current.RateCardId,
                RateCardVersion = current.RateCardVersion,
                RateCardPinJson = rateCardPinJson,
                CreatedByActorId = request.ActorId,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToPublicationReceipt(opRecord);
        }, cancellationToken);
    }

    internal Task<ModelProviderCredentialCancelResponse> CancelAsync(
        ModelProviderCredentialCancelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
            throw ModelProviderCredentialPublicationV2Exception.InvalidRequest();

        var fingerprint = Hash(new
        {
            kind = "cancel-v2",
            request.OperationId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.Issuer,
            request.ActorId,
            request.OriginalTenant,
        });

        return ExecuteWithRetryAsync(async () =>
        {
            var authority = await ResolveAndLockAuthorityAsync(
                request.Issuer, request.ActorId, request.Scope, request.ProjectId,
                request.TenantSelector, request.TenantClaim, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(authority.Membership.TenantId, request.OriginalTenant))
                throw ModelProviderCredentialPublicationV2Exception.Forbidden();

            var now = timeProvider.GetUtcNow();
            await ConsumePreflightAsync(
                request.PreflightId,
                ModelProviderCredentialOperationKind.Cancel,
                request.OperationId,
                request.Scope,
                request.ProjectId,
                request.ProviderId,
                request.Issuer,
                request.ActorId,
                authority,
                now,
                cancellationToken).ConfigureAwait(false);

            _ = await LockProviderHeadAsync(
                request.ProviderId, request.Scope, request.ProjectId, forUpdate: true, cancellationToken)
                .ConfigureAwait(false);

            var existingOp = await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleOrDefaultAsync(op => op.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingOp is not null)
            {
                if (existingOp.OperationKind == ModelProviderCredentialOperationKind.Publish)
                    return new ModelProviderCredentialCancelResponse(
                        request.OperationId, "published", ToPublicationReceipt(existingOp));
                if (existingOp.OperationKind == ModelProviderCredentialOperationKind.Cancel &&
                    StringComparer.Ordinal.Equals(existingOp.RequestFingerprint, fingerprint))
                    return new ModelProviderCredentialCancelResponse(request.OperationId, "cancelled", null);
                throw ModelProviderCredentialPublicationV2Exception.Conflict();
            }

            db.ModelProviderCredentialOperations.Add(new ModelProviderCredentialOperationRecord
            {
                OperationId = request.OperationId,
                OperationKind = ModelProviderCredentialOperationKind.Cancel,
                ProviderId = request.ProviderId,
                Scope = request.Scope,
                ProjectId = request.ProjectId,
                OriginalTenant = request.OriginalTenant,
                OriginalMembershipId = request.OriginalMembershipId,
                OriginalMembershipRevision = request.OriginalMembershipRevision,
                CurrentMembershipId = authority.Membership.MembershipId,
                CurrentMembershipRevision = authority.Membership.Revision,
                CurrentAssignmentId = authority.Assignment.AssignmentId,
                CurrentAssignmentRevision = authority.Assignment.Revision,
                Issuer = request.Issuer,
                ActorId = request.ActorId,
                RequestFingerprint = fingerprint,
                RecordedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ModelProviderCredentialCancelResponse(request.OperationId, "cancelled", null);
        }, cancellationToken);
    }

    internal Task<ModelProviderCredentialDisableReceipt> DisableAsync(
        ModelProviderCredentialDisableRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid)
            throw ModelProviderCredentialPublicationV2Exception.InvalidRequest();

        var fingerprint = Hash(new
        {
            kind = "disable-v2",
            request.OperationId,
            request.ExpectedProviderRevision,
            request.OriginalCredentialRevision,
            request.ExpectedCredentialSlotId,
            request.ExpectedCredentialVersion,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.Issuer,
            request.ActorId,
        });

        return ExecuteWithRetryAsync(async () =>
        {
            var authority = await ResolveAndLockAuthorityAsync(
                request.Issuer, request.ActorId, request.Scope, request.ProjectId,
                request.TenantSelector, request.TenantClaim, cancellationToken).ConfigureAwait(false);

            // FOR UPDATE serializes with AcceptRunSelectionAsync's FOR SHARE on model_providers.
            var head = await LockProviderHeadAsync(
                request.ProviderId, request.Scope, request.ProjectId, forUpdate: true, cancellationToken)
                .ConfigureAwait(false);

            var existingOp = await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleOrDefaultAsync(op => op.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (existingOp is not null)
            {
                if (existingOp.OperationKind == ModelProviderCredentialOperationKind.Disable &&
                    StringComparer.Ordinal.Equals(existingOp.RequestFingerprint, fingerprint))
                    return ToDisableReceipt(existingOp);
                throw ModelProviderCredentialPublicationV2Exception.Conflict();
            }

            if (request.ExpectedProviderRevision is { } expected && head.CurrentRevision != expected)
                throw ModelProviderCredentialPublicationV2Exception.Conflict();

            var current = await db.ModelProviderDraftRevisions.AsNoTracking()
                .SingleAsync(r => r.ProviderId == head.ProviderId && r.ProviderRevision == head.CurrentRevision, cancellationToken)
                .ConfigureAwait(false);
            if (current.CredentialVersion is null && current.CredentialSlotId is null && current.CredentialSecretId is null)
                throw ModelProviderCredentialPublicationV2Exception.NotFound("credential_slot_not_found");
            if (current.CredentialSlotId != request.ExpectedCredentialSlotId ||
                !StringComparer.Ordinal.Equals(current.CredentialVersion, request.ExpectedCredentialVersion))
                throw ModelProviderCredentialPublicationV2Exception.Conflict();

            var now = timeProvider.GetUtcNow();
            var nextRevision = head.CurrentRevision + 1;

            var updated = await db.ModelProviderDrafts
                .Where(h => h.ProviderId == head.ProviderId && h.CurrentRevision == head.CurrentRevision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(h => h.State, ModelProviderDraftState.Draft)
                    .SetProperty(h => h.CurrentRevision, nextRevision)
                    .SetProperty(h => h.UpdatedByActorId, request.ActorId)
                    .SetProperty(h => h.UpdatedAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (updated != 1)
                throw ModelProviderCredentialPublicationV2Exception.Conflict();

            var opRecord = new ModelProviderCredentialOperationRecord
            {
                OperationId = request.OperationId,
                OperationKind = ModelProviderCredentialOperationKind.Disable,
                ProviderId = head.ProviderId,
                Scope = head.Scope,
                ProjectId = head.ProjectId,
                ProviderRevision = nextRevision,
                ExpectedProviderRevision = request.ExpectedProviderRevision,
                OriginalProviderRevision = head.CurrentRevision,
                OriginalCredentialRevision = request.OriginalCredentialRevision,
                CredentialSlotId = current.CredentialSlotId,
                CredentialSecretId = null,
                CredentialVersion = null,
                ConfigurationHash = current.ConfigurationHash,
                ProviderState = nameof(ModelProviderDraftState.Draft),
                OriginalTenant = authority.Membership.TenantId,
                OriginalMembershipId = authority.Membership.MembershipId,
                OriginalMembershipRevision = authority.Membership.Revision,
                CurrentMembershipId = authority.Membership.MembershipId,
                CurrentMembershipRevision = authority.Membership.Revision,
                CurrentAssignmentId = authority.Assignment.AssignmentId,
                CurrentAssignmentRevision = authority.Assignment.Revision,
                Issuer = request.Issuer,
                ActorId = request.ActorId,
                RequestFingerprint = fingerprint,
                RecordedAt = now,
            };
            db.ModelProviderCredentialOperations.Add(opRecord);
            db.ModelProviderDraftRevisions.Add(new ModelProviderDraftRevisionRecord
            {
                ProviderId = current.ProviderId,
                Scope = current.Scope,
                ProjectId = current.ProjectId,
                ProviderRevision = nextRevision,
                OperationId = request.OperationId,
                RequestFingerprint = fingerprint,
                DisplayName = current.DisplayName,
                ProviderType = current.ProviderType,
                BaseUrl = current.BaseUrl,
                Deployment = current.Deployment,
                WireApi = current.WireApi,
                AzureApiVersion = current.AzureApiVersion,
                ReasoningEfforts = current.ReasoningEfforts,
                PromptCapacityTokens = current.PromptCapacityTokens,
                ConfigurationHash = current.ConfigurationHash,
                CredentialVersion = null,
                CredentialSlotId = null,
                CredentialSecretId = null,
                RateCardId = current.RateCardId,
                RateCardVersion = current.RateCardVersion,
                RateCardPinJson = null,
                CreatedByActorId = request.ActorId,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToDisableReceipt(opRecord);
        }, cancellationToken);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                await using var transaction = await db.Database
                    .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                    .ConfigureAwait(false);
                var result = await action().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                DetachTracked();
                return result;
            }
            catch (ModelProviderCredentialPublicationV2Exception)
            {
                DetachTracked();
                throw;
            }
            catch (Exception ex) when (IsRetryablePostgres(ex) && attempt + 1 < MaxRetries)
            {
                DetachTracked();
            }
            catch (Exception ex) when (IsRetryablePostgres(ex))
            {
                DetachTracked();
                throw ModelProviderCredentialPublicationV2Exception.Unavailable();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                DetachTracked();
                throw ModelProviderCredentialPublicationV2Exception.Conflict();
            }
            catch
            {
                DetachTracked();
                throw;
            }
        }
        throw ModelProviderCredentialPublicationV2Exception.Unavailable();
    }

    private async Task<(ProjectTenantMembershipRecord Membership, ProjectRoleAssignmentRecord Assignment)> ResolveAndLockAuthorityAsync(
        string issuer,
        string subject,
        ModelProviderBindingScope scope,
        string? projectId,
        string? tenantSelector,
        string? tenantClaim,
        CancellationToken cancellationToken)
    {
        var memberships = await db.TenantMemberships.AsNoTracking()
            .Where(m => m.Issuer == issuer && m.Subject == subject && m.State == ProjectAuthorityRecordState.Active)
            .OrderBy(m => m.TenantId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        ProjectTenantMembershipRecord selectedMembership;
        if (tenantSelector is not null)
        {
            var matched = memberships.Where(m => m.TenantId == tenantSelector).Take(2).ToArray();
            selectedMembership = matched.Length == 1
                ? matched[0]
                : throw ModelProviderCredentialPublicationV2Exception.Forbidden();
        }
        else
        {
            selectedMembership = memberships.Length == 1
                ? memberships[0]
                : throw ModelProviderCredentialPublicationV2Exception.Forbidden();
        }

        if (tenantClaim is not null && !StringComparer.Ordinal.Equals(tenantClaim, selectedMembership.TenantId))
            throw ModelProviderCredentialPublicationV2Exception.Forbidden();

        var assignments = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.MembershipId == selectedMembership.MembershipId && a.State == ProjectAuthorityRecordState.Active)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        ProjectRoleAssignmentRecord? chosenAssignment;
        if (scope == ModelProviderBindingScope.Platform)
        {
            chosenAssignment = assignments.FirstOrDefault(a =>
                a.ResourceType == ProjectAuthorityResourceType.Platform &&
                a.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                a.Role == ProjectAuthorityRole.PlatformAdmin);
        }
        else
        {
            chosenAssignment = assignments.FirstOrDefault(a =>
                    a.ResourceType == ProjectAuthorityResourceType.Project &&
                    a.ResourceId == projectId &&
                    a.Role == ProjectAuthorityRole.Owner)
                ?? assignments.FirstOrDefault(a =>
                    a.ResourceType == ProjectAuthorityResourceType.Platform &&
                    a.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                    a.Role == ProjectAuthorityRole.PlatformAdmin);
        }

        if (chosenAssignment is null)
            throw ModelProviderCredentialPublicationV2Exception.Forbidden();

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var lockCommand = new NpgsqlCommand(
            """
            SELECT projects_config.lock_credential_publication_authority(
                @membership_id, @issuer, @subject, @tenant_id, @membership_revision,
                @assignment_id, @assignment_revision, @scope, @project_id)
            """,
            connection,
            transaction);
        lockCommand.Parameters.AddWithValue("membership_id", selectedMembership.MembershipId);
        lockCommand.Parameters.AddWithValue("issuer", issuer);
        lockCommand.Parameters.AddWithValue("subject", subject);
        lockCommand.Parameters.AddWithValue("tenant_id", selectedMembership.TenantId);
        lockCommand.Parameters.AddWithValue("membership_revision", selectedMembership.Revision);
        lockCommand.Parameters.AddWithValue("assignment_id", chosenAssignment.AssignmentId);
        lockCommand.Parameters.AddWithValue("assignment_revision", chosenAssignment.Revision);
        lockCommand.Parameters.AddWithValue("scope", scope.ToString());
        lockCommand.Parameters.AddWithValue("project_id", (object?)projectId ?? DBNull.Value);

        var lockResult = await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (lockResult == "project_missing")
            throw ModelProviderCredentialPublicationV2Exception.NotFound("project_not_found");
        if (!string.Equals(lockResult, "authorized", StringComparison.Ordinal))
            throw ModelProviderCredentialPublicationV2Exception.Forbidden();

        return (selectedMembership, chosenAssignment);
    }

    private async Task ConsumePreflightAsync(
        Guid preflightId,
        ModelProviderCredentialOperationKind kind,
        Guid operationId,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        string issuer,
        string actorId,
        (ProjectTenantMembershipRecord Membership, ProjectRoleAssignmentRecord Assignment) authority,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var preflight = await db.ModelProviderCredentialPreflights.AsNoTracking()
            .SingleOrDefaultAsync(p => p.PreflightId == preflightId, cancellationToken)
            .ConfigureAwait(false);
        if (preflight is null ||
            preflight.ConsumedAt is not null ||
            preflight.OperationId != operationId ||
            preflight.Scope != scope ||
            preflight.ProjectId != projectId ||
            !StringComparer.Ordinal.Equals(preflight.ProviderId, providerId) ||
            !StringComparer.Ordinal.Equals(preflight.Issuer, issuer) ||
            !StringComparer.Ordinal.Equals(preflight.ActorId, actorId) ||
            !StringComparer.Ordinal.Equals(preflight.TenantId, authority.Membership.TenantId) ||
            preflight.MembershipId != authority.Membership.MembershipId ||
            preflight.AssignmentId != authority.Assignment.AssignmentId)
            throw ModelProviderCredentialPublicationV2Exception.Conflict();

        var kindName = kind.ToString();
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE projects_config.model_provider_credential_preflights
            SET consumed_at = {now},
                consumed_by_kind = {kindName}
            WHERE preflight_id = {preflightId}
              AND consumed_at IS NULL
            """,
            cancellationToken).ConfigureAwait(false);
        if (updated != 1)
            throw ModelProviderCredentialPublicationV2Exception.Conflict();
    }

    private async Task<ModelProviderDraftHeadRecord> LockProviderHeadAsync(
        string providerId,
        ModelProviderBindingScope scope,
        string? projectId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var head = forUpdate
            ? await db.ModelProviderDrafts
                .FromSqlInterpolated($"SELECT * FROM projects_config.model_providers WHERE provider_id = {providerId} FOR UPDATE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : await db.ModelProviderDrafts
                .FromSqlInterpolated($"SELECT * FROM projects_config.model_providers WHERE provider_id = {providerId} FOR SHARE")
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        if (head is null || head.Scope != scope || head.ProjectId != projectId)
            throw ModelProviderCredentialPublicationV2Exception.NotFound();
        return head;
    }

    private static ModelProviderCredentialPublicationReceipt ToPublicationReceipt(ModelProviderCredentialOperationRecord op) =>
        new(
            op.OperationId,
            op.Scope,
            op.ProjectId,
            op.ProviderId,
            op.ProviderRevision!.Value,
            op.CredentialVersion!,
            op.ConfigurationHash!,
            new ModelCredentialIdentity(op.CredentialSlotId!.Value, op.Scope, op.ProjectId, op.ProviderId),
            new SecretRef(op.CredentialSecretId!, op.CredentialVersion!))
        {
            ProviderState = op.ProviderState ?? nameof(ModelProviderDraftState.Draft),
        };

    private static ModelProviderCredentialDisableReceipt ToDisableReceipt(ModelProviderCredentialOperationRecord op) =>
        new(
            op.OperationId,
            op.Scope,
            op.ProjectId,
            op.ProviderId,
            op.OriginalProviderRevision!.Value,
            op.ProviderRevision!.Value,
            op.OriginalCredentialRevision!.Value,
            op.CredentialSlotId,
            op.ConfigurationHash!,
            op.OriginalTenant);

    private static bool IsRetryablePostgres(Exception exception) =>
        exception is PostgresException pg && pg.SqlState is
            PostgresErrorCodes.SerializationFailure or
            PostgresErrorCodes.DeadlockDetected or
            PostgresErrorCodes.LockNotAvailable or
            "55P03";

    private void DetachTracked()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToArray())
            entry.State = EntityState.Detached;
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
}
