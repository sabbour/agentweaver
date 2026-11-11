using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed class ModelProviderDraftService(
    ProjectsConfigDbContext db,
    TimeProvider timeProvider)
{
    private static readonly ImmutableHashSet<string> SupportedReasoningEfforts =
        ImmutableHashSet.Create(StringComparer.Ordinal, "none", "minimal", "low", "medium", "high", "xhigh");

    public async Task<IReadOnlyList<ModelProviderDraft>> ListAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(caller, scope, projectId, cancellationToken).ConfigureAwait(false);
        var drafts = await (
                from head in db.ModelProviderDrafts.AsNoTracking()
                join revision in db.ModelProviderDraftRevisions.AsNoTracking()
                    on new { head.ProviderId, Revision = head.CurrentRevision }
                    equals new { revision.ProviderId, Revision = revision.ProviderRevision }
                where head.Scope == scope && head.ProjectId == projectId
                orderby head.DisplayName, head.ProviderId
                select new { head.State, Revision = revision })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return drafts.Select(item => ToDraft(item.State, item.Revision)).ToArray();
    }

    public async Task<ModelProviderDraft> ReadAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(caller, scope, projectId, cancellationToken).ConfigureAwait(false);
        ValidateProviderId(providerId);
        var draft = await (
                from head in db.ModelProviderDrafts.AsNoTracking()
                join revision in db.ModelProviderDraftRevisions.AsNoTracking()
                    on new { head.ProviderId, Revision = head.CurrentRevision }
                    equals new { revision.ProviderId, Revision = revision.ProviderRevision }
                where head.ProviderId == providerId && head.Scope == scope && head.ProjectId == projectId
                select new { head.State, Revision = revision })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return draft is null
            ? throw ModelProviderDraftException.NotFound()
            : ToDraft(draft.State, draft.Revision);
    }

    public async Task<ModelProviderDraft> SaveAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        SaveModelProviderDraftRequest request,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(caller, scope, projectId, cancellationToken).ConfigureAwait(false);
        ValidateProviderId(providerId);
        ArgumentNullException.ThrowIfNull(request);
        var metadata = ValidateAndNormalize(request);
        var fingerprintInput = new MetadataFingerprint(
            providerId,
            scope.ToString(),
            projectId,
            metadata.DisplayName,
            metadata.ProviderType,
            metadata.BaseUrl,
            metadata.Deployment,
            metadata.WireApi,
            metadata.AzureApiVersion,
            metadata.ReasoningEfforts,
            metadata.PromptCapacityTokens);
        var requestFingerprint = Hash(new SaveFingerprint(fingerprintInput, request.ExpectedRevision));
        var configurationHash = Hash(fingerprintInput);

        var replay = await ReadReplayAsync(
                request.OperationId,
                providerId,
                scope,
                projectId,
                requestFingerprint,
                cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
            return replay;

        var current = await db.ModelProviderDrafts.AsNoTracking()
            .SingleOrDefaultAsync(head => head.ProviderId == providerId, cancellationToken)
            .ConfigureAwait(false);
        if ((current is not null &&
                (current.Scope != scope || current.ProjectId != projectId ||
                 current.CurrentRevision != request.ExpectedRevision)) ||
            (current is null && request.ExpectedRevision != 0))
        {
            var concurrentReplay = await ReadReplayAsync(
                    request.OperationId,
                    providerId,
                    scope,
                    projectId,
                    requestFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (concurrentReplay is not null)
                return concurrentReplay;
            throw ModelProviderDraftException.Conflict();
        }

        var nextRevision = request.ExpectedRevision + 1;
        var now = timeProvider.GetUtcNow();
        var revisionRecord = new ModelProviderDraftRevisionRecord
        {
            ProviderId = providerId,
            Scope = scope,
            ProjectId = projectId,
            ProviderRevision = nextRevision,
            OperationId = request.OperationId,
            RequestFingerprint = requestFingerprint,
            DisplayName = metadata.DisplayName,
            ProviderType = metadata.ProviderType,
            BaseUrl = metadata.BaseUrl,
            Deployment = metadata.Deployment,
            WireApi = metadata.WireApi,
            AzureApiVersion = metadata.AzureApiVersion,
            ReasoningEfforts = metadata.ReasoningEfforts,
            PromptCapacityTokens = metadata.PromptCapacityTokens,
            ConfigurationHash = configurationHash,
            CreatedByActorId = caller.ActorId,
            CreatedAt = now,
        };

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                db.ModelProviderDrafts.Add(new ModelProviderDraftHeadRecord
                {
                    ProviderId = providerId,
                    Scope = scope,
                    ProjectId = projectId,
                    DisplayName = metadata.DisplayName,
                    ProviderType = metadata.ProviderType,
                    BaseUrl = metadata.BaseUrl,
                    Deployment = metadata.Deployment,
                    WireApi = metadata.WireApi,
                    AzureApiVersion = metadata.AzureApiVersion,
                    ReasoningEfforts = metadata.ReasoningEfforts,
                    PromptCapacityTokens = metadata.PromptCapacityTokens,
                    State = ModelProviderDraftState.Draft,
                    CurrentRevision = nextRevision,
                    CreatedByActorId = caller.ActorId,
                    CreatedAt = now,
                    UpdatedByActorId = caller.ActorId,
                    UpdatedAt = now,
                });
            }
            else
            {
                var updated = await db.ModelProviderDrafts
                    .Where(head =>
                        head.ProviderId == providerId &&
                        head.Scope == scope &&
                        head.ProjectId == projectId &&
                        head.State == ModelProviderDraftState.Draft &&
                        head.CurrentRevision == request.ExpectedRevision)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(head => head.DisplayName, metadata.DisplayName)
                            .SetProperty(head => head.ProviderType, metadata.ProviderType)
                            .SetProperty(head => head.BaseUrl, metadata.BaseUrl)
                            .SetProperty(head => head.Deployment, metadata.Deployment)
                            .SetProperty(head => head.WireApi, metadata.WireApi)
                            .SetProperty(head => head.AzureApiVersion, metadata.AzureApiVersion)
                            .SetProperty(head => head.ReasoningEfforts, metadata.ReasoningEfforts)
                            .SetProperty(head => head.PromptCapacityTokens, metadata.PromptCapacityTokens)
                            .SetProperty(head => head.CurrentRevision, nextRevision)
                            .SetProperty(head => head.UpdatedByActorId, caller.ActorId)
                            .SetProperty(head => head.UpdatedAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (updated != 1)
                    throw ModelProviderDraftException.Conflict();
            }

            db.ModelProviderDraftRevisions.Add(revisionRecord);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DetachTrackedProvider(providerId);
            if (exception is ModelProviderDraftException { Code: "model_provider_draft_conflict" } ||
                IsUniqueConflict(exception))
            {
                var concurrentReplay = await ReadReplayAsync(
                        request.OperationId,
                        providerId,
                        scope,
                        projectId,
                        requestFingerprint,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (concurrentReplay is not null)
                    return concurrentReplay;
            }
            if (IsUniqueConflict(exception))
                throw ModelProviderDraftException.Conflict();
            throw;
        }

        DetachTrackedProvider(providerId);
        return ToDraft(ModelProviderDraftState.Draft, revisionRecord);
    }

    private async Task AuthorizeAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);

        if (scope == ModelProviderBindingScope.Platform)
        {
            if (projectId is not null)
                throw ModelProviderDraftException.InvalidRequest("Platform drafts cannot name a project.");
            caller.RequireUnboundRequest();
            await EnsureCurrentMembershipAsync(caller, cancellationToken).ConfigureAwait(false);
            if (!await HasCurrentRoleAsync(
                    caller,
                    ProjectAuthorityResourceType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId,
                    ProjectAuthorityRole.PlatformAdmin,
                    cancellationToken)
                .ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();
            return;
        }

        if (scope != ModelProviderBindingScope.Project ||
            string.IsNullOrWhiteSpace(projectId) ||
            projectId.Length > ModelProviderDraftLimits.ProjectId)
            throw ModelProviderDraftException.InvalidRequest("A valid project ID is required.");

        caller.RequireResourceBinding(projectId);
        if (!string.Equals(caller.BoundProjectId, projectId, StringComparison.Ordinal) ||
            caller.BoundRunId is not null)
            throw ProjectConfigException.Forbidden();

        await EnsureCurrentMembershipAsync(caller, cancellationToken).ConfigureAwait(false);
        var isPlatformAdmin = await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin,
                cancellationToken)
            .ConfigureAwait(false);
        var isProjectOwner = await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Project,
                projectId,
                ProjectAuthorityRole.Owner,
                cancellationToken)
            .ConfigureAwait(false);
        if (!isPlatformAdmin && !isProjectOwner)
            throw ProjectConfigException.Forbidden();

        await EnsureProjectActiveAsync(caller, projectId, isPlatformAdmin, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureProjectActiveAsync(
        ProjectAuthorizationContext caller,
        string? projectId,
        bool isPlatformAdmin,
        CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null ||
            project.State != ProjectLifecycleState.Active ||
            (!isPlatformAdmin && project.TenantId != caller.TenantId))
            throw ModelProviderDraftException.NotFound();
    }

    private async Task EnsureCurrentMembershipAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        var isCurrent = await db.TenantMemberships.AsNoTracking().AnyAsync(
            membership =>
                membership.MembershipId == caller.MembershipId &&
                membership.Issuer == caller.Issuer &&
                membership.Subject == caller.ActorId &&
                membership.TenantId == caller.TenantId &&
                membership.Revision == caller.MembershipRevision &&
                membership.State == ProjectAuthorityRecordState.Active,
            cancellationToken).ConfigureAwait(false);
        if (!isCurrent)
            throw ProjectConfigException.Forbidden();
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

    private static NormalizedProviderMetadata ValidateAndNormalize(SaveModelProviderDraftRequest request)
    {
        if (request.OperationId == Guid.Empty)
            throw ModelProviderDraftException.InvalidRequest("An operation ID is required.");
        if (request.ExpectedRevision < 0 || request.ExpectedRevision == long.MaxValue)
            throw ModelProviderDraftException.InvalidRequest("Expected revision must be between zero and Int64.MaxValue - 1.");
        if (string.IsNullOrWhiteSpace(request.DisplayName) ||
            request.DisplayName.Length > ModelProviderDraftLimits.DisplayName)
            throw ModelProviderDraftException.InvalidRequest("Display name must contain 1 to 128 characters.");
        if (request.ProviderType is not ("openai" or "azure" or "anthropic"))
            throw ModelProviderDraftException.InvalidRequest("Provider type must be openai, azure, or anthropic.");
        if (string.IsNullOrWhiteSpace(request.BaseUrl) ||
            request.BaseUrl.Length > ModelProviderDraftLimits.BaseUrl ||
            !Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(baseUri.Host) ||
            baseUri.UserInfo.Length != 0 ||
            baseUri.Fragment.Length != 0)
            throw ModelProviderDraftException.InvalidRequest("Provider base URL must be a valid HTTPS URL of at most 2048 characters.");
        if (request.ProviderType == "azure" &&
            (!string.IsNullOrEmpty(baseUri.PathAndQuery.Trim('/')) ||
             !string.IsNullOrEmpty(baseUri.Fragment)))
            throw ModelProviderDraftException.InvalidRequest("Azure provider base URL must be its HTTPS host without an API path.");
        if (string.IsNullOrWhiteSpace(request.Deployment) ||
            request.Deployment.Length > ModelProviderDraftLimits.Deployment)
            throw ModelProviderDraftException.InvalidRequest("Deployment must contain 1 to 256 characters.");
        if (request.WireApi is not (null or "completions" or "responses"))
            throw ModelProviderDraftException.InvalidRequest("Wire API must be completions or responses.");
        if (request.WireApi?.Length > ModelProviderDraftLimits.WireApi)
            throw ModelProviderDraftException.InvalidRequest("Wire API is too long.");
        if (request.AzureApiVersion?.Length > ModelProviderDraftLimits.AzureApiVersion)
            throw ModelProviderDraftException.InvalidRequest("Azure API version is too long.");
        if (request.PromptCapacityTokens is <= 0)
            throw ModelProviderDraftException.InvalidRequest("Prompt capacity must be positive.");

        var reasoningEfforts = request.ReasoningEfforts.IsDefault
            ? []
            : request.ReasoningEfforts.ToArray();
        if (reasoningEfforts.Length > ModelProviderDraftLimits.ReasoningEffortCount ||
            reasoningEfforts.Any(effort =>
                string.IsNullOrWhiteSpace(effort) ||
                effort.Length > ModelProviderDraftLimits.ReasoningEffort ||
                !SupportedReasoningEfforts.Contains(effort)) ||
            reasoningEfforts.Distinct(StringComparer.Ordinal).Count() != reasoningEfforts.Length)
            throw ModelProviderDraftException.InvalidRequest("Reasoning efforts contain an unsupported or duplicate value.");

        return new NormalizedProviderMetadata(
            request.DisplayName,
            request.ProviderType,
            request.BaseUrl,
            request.Deployment,
            request.WireApi,
            request.AzureApiVersion,
            reasoningEfforts.Order(StringComparer.Ordinal).ToArray(),
            request.PromptCapacityTokens);
    }

    private static void ValidateProviderId(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) ||
            providerId.Length > ModelProviderDraftLimits.ProviderId ||
            providerId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw ModelProviderDraftException.InvalidRequest("Provider ID must be an opaque identifier of at most 128 characters.");
    }

    private async Task<ModelProviderDraft?> ReadReplayAsync(
        Guid operationId,
        string providerId,
        ModelProviderBindingScope scope,
        string? projectId,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var replay = await db.ModelProviderDraftRevisions.AsNoTracking()
            .SingleOrDefaultAsync(revision => revision.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (replay is null)
            return null;
        if (replay.ProviderId != providerId ||
            replay.Scope != scope ||
            replay.ProjectId != projectId ||
            replay.RequestFingerprint != requestFingerprint)
            throw ModelProviderDraftException.Conflict();
        return ToDraft(ModelProviderDraftState.Draft, replay);
    }

    private static ModelProviderDraft ToDraft(
        ModelProviderDraftState state,
        ModelProviderDraftRevisionRecord revision) =>
        new(state, new ModelProviderBindingRevision(
            ModelProviderBindingRevision.CurrentContractVersion,
            revision.ProviderId,
            revision.Scope,
            revision.ProjectId,
            revision.DisplayName,
            revision.ProviderType,
            revision.BaseUrl,
            revision.Deployment,
            revision.WireApi,
            revision.AzureApiVersion,
            ImmutableArray.CreateRange(revision.ReasoningEfforts),
            revision.PromptCapacityTokens,
            revision.ProviderRevision,
            revision.CredentialVersion,
            revision.RateCardId,
            revision.RateCardVersion,
            revision.ConfigurationHash));

    private void DetachTrackedProvider(string providerId)
    {
        foreach (var entry in db.ChangeTracker.Entries<ModelProviderDraftHeadRecord>()
                     .Where(entry => entry.Entity.ProviderId == providerId)
                     .ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<ModelProviderDraftRevisionRecord>()
                     .Where(entry => entry.Entity.ProviderId == providerId)
                     .ToArray())
            entry.State = EntityState.Detached;
    }

    private static bool IsUniqueConflict(Exception exception) =>
        exception is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "pk_model_providers" or
                    "pk_model_provider_revisions" or
                    "ux_model_provider_revisions_operation_id",
            },
        };

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();

    private sealed record NormalizedProviderMetadata(
        string DisplayName,
        string ProviderType,
        string BaseUrl,
        string Deployment,
        string? WireApi,
        string? AzureApiVersion,
        string[] ReasoningEfforts,
        int? PromptCapacityTokens);

    private sealed record MetadataFingerprint(
        string ProviderId,
        string Scope,
        string? ProjectId,
        string DisplayName,
        string ProviderType,
        string BaseUrl,
        string Deployment,
        string? WireApi,
        string? AzureApiVersion,
        string[] ReasoningEfforts,
        int? PromptCapacityTokens);

    private sealed record SaveFingerprint(MetadataFingerprint Metadata, long ExpectedRevision);
}
