using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed partial class ModelProviderDraftService
{
    private static readonly JsonSerializerOptions RateCardPinJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly ProjectsConfigDbContext db;
    private readonly TimeProvider timeProvider;
    private readonly ModelBindingCapabilities modelBindings;
    private readonly bool referenceReservationsEnabled;

    public ModelProviderDraftService(ProjectsConfigDbContext db, TimeProvider timeProvider)
        : this(db, timeProvider, null)
    {
    }

    internal ModelProviderDraftService(
        ProjectsConfigDbContext db,
        TimeProvider timeProvider,
        ModelBindingCapabilities? modelBindings = null,
        bool referenceReservationsEnabled = false)
    {
        this.db = db;
        this.timeProvider = timeProvider;
        this.modelBindings = modelBindings ?? ModelBindingCapabilities.Unavailable;
        this.referenceReservationsEnabled = referenceReservationsEnabled;
    }

    private static readonly ImmutableHashSet<string> SupportedReasoningEfforts =
        ImmutableHashSet.Create(StringComparer.Ordinal, "none", "minimal", "low", "medium", "high", "xhigh");

    internal async Task<ModelBindingCapabilities> ReadDeploymentBindingsAsync(
        ProjectAuthorizationContext caller, CancellationToken cancellationToken)
    {
        await AuthorizeAsync(caller, ModelProviderBindingScope.Platform, null, cancellationToken).ConfigureAwait(false);
        return modelBindings;
    }

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
                join reservation in db.ModelProviderReferenceReservations.AsNoTracking()
                    on head.ProviderId equals reservation.ProviderId into reservations
                from reservation in reservations.DefaultIfEmpty()
                where head.Scope == scope && head.ProjectId == projectId
                orderby head.DisplayName, head.ProviderId
                select new { head.State, Revision = revision, Reference = reservation == null ? null : reservation.Reference })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return drafts.Select(item => ToDraft(item.State, item.Revision, item.Reference)).ToArray();
    }

    public async Task<ModelProviderDraftPage> ListPageAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        int pageSize,
        string? afterProviderId,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(caller, scope, projectId, cancellationToken).ConfigureAwait(false);
        if (pageSize is < 1 or > 100)
            throw ModelProviderDraftException.InvalidRequest("Page size must be between 1 and 100.");
        if (afterProviderId is not null)
            ValidateProviderId(afterProviderId);
        await using var switchTransaction = scope == ModelProviderBindingScope.Platform
            ? await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false)
            : null;
        if (switchTransaction is not null)
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var transaction = (NpgsqlTransaction)switchTransaction.GetDbTransaction();
            await using var lockCommand = new NpgsqlCommand(
                "SELECT id FROM projects_config.platform_runtime_heads WHERE id = 'default' FOR SHARE",
                connection, transaction);
            _ = await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var drafts = await (
                from head in db.ModelProviderDrafts.AsNoTracking()
                join revision in db.ModelProviderDraftRevisions.AsNoTracking()
                    on new { head.ProviderId, Revision = head.CurrentRevision }
                    equals new { revision.ProviderId, Revision = revision.ProviderRevision }
                join reservation in db.ModelProviderReferenceReservations.AsNoTracking()
                    on head.ProviderId equals reservation.ProviderId into reservations
                from reservation in reservations.DefaultIfEmpty()
                where head.Scope == scope && head.ProjectId == projectId &&
                    (afterProviderId == null || head.ProviderId.CompareTo(afterProviderId) > 0)
                orderby head.ProviderId
                select new { head.ProviderId, head.State, Revision = revision, Reference = reservation == null ? null : reservation.Reference })
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasNextPage = drafts.Length > pageSize;
        var providers = drafts.Take(pageSize)
            .Select(item => ToDraft(item.State, item.Revision, item.Reference))
            .ToArray();
        var page = new ModelProviderDraftPage(
            providers,
            hasNextPage ? drafts[pageSize - 1].ProviderId : null);
        if (switchTransaction is null)
            return page;
        var settingsHead = await db.PlatformRuntimeHeads.AsNoTracking().SingleAsync(
            item => item.Id == PlatformRuntimeHeadRecord.SingletonId, cancellationToken).ConfigureAwait(false);
        if (settingsHead.CurrentRevision == 0)
        {
            await switchTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return page with
            {
                SwitchMode = "untouched",
                SettingsRevision = 0,
            };
        }
        var settingsRevision = await db.PlatformRuntimeRevisions.AsNoTracking().SingleAsync(
            item => item.Revision == settingsHead.CurrentRevision, cancellationToken).ConfigureAwait(false);
        var settings = ProjectsConfigService.Deserialize<PlatformRuntimeDefaults>(
            settingsRevision.ConfigurationJson);
        var result = page with
        {
            SwitchMode = settings.ModelDefaultSwitch.Mode switch
            {
                PlatformModelDefaultSwitchMode.Untouched => "untouched",
                PlatformModelDefaultSwitchMode.Active => "active",
                PlatformModelDefaultSwitchMode.ExplicitHosted => "explicitHosted",
                _ => throw new InvalidOperationException("The platform model switch mode is invalid."),
            },
            ActiveProviderId = settings.ModelDefaultSwitch.ActiveProviderId,
            SettingsRevision = settingsRevision.Revision,
        };
        await switchTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
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
                join reservation in db.ModelProviderReferenceReservations.AsNoTracking()
                    on head.ProviderId equals reservation.ProviderId into reservations
                from reservation in reservations.DefaultIfEmpty()
                where head.ProviderId == providerId && head.Scope == scope && head.ProjectId == projectId
                select new { head.State, Revision = revision, Reference = reservation == null ? null : reservation.Reference })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return draft is null
            ? throw ModelProviderDraftException.NotFound()
            : ToDraft(draft.State, draft.Revision, draft.Reference);
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
        if (request.PreserveRateCardLink)
        {
            var existing = await db.ModelProviderDraftRevisions.AsNoTracking()
                .Where(revision =>
                    revision.ProviderId == providerId &&
                    revision.Scope == scope &&
                    revision.ProjectId == projectId &&
                    revision.ProviderRevision == request.ExpectedRevision)
                .Select(revision => new { revision.RateCardId, revision.RateCardVersion })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
                throw ModelProviderDraftException.Conflict();
            request = request with
            {
                RateCardId = existing.RateCardId,
                RateCardVersion = existing.RateCardVersion,
                PreserveRateCardLink = false,
            };
        }
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
            metadata.PromptCapacityTokens,
            metadata.RateCardId,
            metadata.RateCardVersion);
        var requestFingerprint = Hash(new SaveFingerprint(fingerprintInput, request.ExpectedRevision));
        var configurationHash = Hash(fingerprintInput);

        if (request.ExpectedRevision == 0)
            return await CreateAsync(
                caller, scope, projectId, providerId, request, metadata, fingerprintInput,
                configurationHash, cancellationToken).ConfigureAwait(false);
        if (request.Reference is not null || request.TargetInstallationId is not null)
            throw ModelProviderDraftException.InvalidRequest("A reference and target are permitted only on platform create.");

        // Check completed operations first so a lost response can be replayed after later edits.
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
        if (current is { State: not ModelProviderDraftState.Draft })
            throw ModelProviderDraftException.Unavailable();

        var currentCredential = current is null
            ? null
            : await db.ModelProviderDraftRevisions.AsNoTracking()
                .Where(revision =>
                    revision.ProviderId == providerId &&
                    revision.ProviderRevision == current.CurrentRevision)
                .Select(revision => new
                {
                    revision.CredentialVersion,
                    revision.CredentialSlotId,
                    revision.CredentialSecretId
                })
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
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
            RateCardId = metadata.RateCardId,
            RateCardVersion = metadata.RateCardVersion,
            ConfigurationHash = configurationHash,
            CredentialVersion = currentCredential?.CredentialVersion,
            CredentialSlotId = currentCredential?.CredentialSlotId,
            CredentialSecretId = currentCredential?.CredentialSecretId,
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
                // Repeat the expected-revision check in the update to close the race after the earlier head read.
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
            if (IsProviderWriteGuardFailure(exception))
                throw ModelProviderDraftException.Unavailable();
            if (exception is ModelProviderDraftException { Code: "model_provider_draft_conflict" } ||
                IsUniqueConflict(exception))
            {
                // A concurrent identical save may have committed; its matching durable revision is the replay result.
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
        return ToDraft(ModelProviderDraftState.Draft, revisionRecord,
            await ReadReferenceAsync(providerId, cancellationToken).ConfigureAwait(false));
    }

    internal Task<ModelProviderCredentialPublicationReceipt> PublishCredentialAsync(
        ModelProviderCredentialPublicationRequest request,
        string actorId,
        CancellationToken cancellationToken) =>
        Task.FromException<ModelProviderCredentialPublicationReceipt>(
            new ModelProviderDraftException(
                "credential_publication_unavailable", "Credential publication is unavailable."));

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
        if ((request.RateCardId is null) != (request.RateCardVersion is null))
            throw ModelProviderDraftException.InvalidRequest("Rate-card ID and version must be set together.");
        if (request.RateCardId is not null)
        {
            try
            {
                RuntimeRateCardReadContract.ValidateIdentity(request.RateCardId, request.RateCardVersion!);
            }
            catch (ArgumentException)
            {
                throw ModelProviderDraftException.InvalidRequest("Rate-card ID and version are invalid.");
            }
        }

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
            request.PromptCapacityTokens,
            request.RateCardId,
            request.RateCardVersion);
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
        return ToDraft(ModelProviderDraftState.Draft, replay,
            await ReadReferenceAsync(providerId, cancellationToken).ConfigureAwait(false));
    }

    internal static ModelProviderBindingRevision ToBinding(ModelProviderDraftRevisionRecord revision) =>
        new(
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
            revision.ConfigurationHash)
        {
            CredentialIdentity = revision.CredentialSlotId is { } credentialSlotId
                ? new(credentialSlotId, revision.Scope, revision.ProjectId, revision.ProviderId)
                : null,
            CredentialReference = revision.CredentialSecretId is { } credentialSecretId &&
                                  revision.CredentialVersion is { } credentialVersion
                ? new(credentialSecretId, credentialVersion)
                : null,
            RateCardPin = ReadRateCardPin(revision)
        };

    private static ModelProviderDraft ToDraft(
        ModelProviderDraftState state,
        ModelProviderDraftRevisionRecord revision,
        string? reference = null) =>
        new(state, ToBinding(revision)) { Reference = reference };

    private static RuntimeRateCardPin? ReadRateCardPin(ModelProviderDraftRevisionRecord revision)
    {
        if (revision.RateCardPinJson is null)
            return null;
        try
        {
            var pin = JsonSerializer.Deserialize<RuntimeRateCardPin>(
                revision.RateCardPinJson, RateCardPinJsonOptions)
                ?? throw new JsonException("The rate-card pin is empty.");
            RuntimeRateCardReadContract.ValidatePin(pin);
            if (pin.RateCardId != revision.RateCardId ||
                pin.RateCardVersion != revision.RateCardVersion)
                throw new JsonException("The rate-card pin does not match the provider revision.");
            return pin;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidOperationException("The stored runtime rate-card pin is invalid.", exception);
        }
    }

    private void DetachTrackedProvider(string providerId)
    {
        // Detach the head and revision records this operation added, so a failed save cannot leave them pending in the scoped context and a successful save does not leave them tracked.
        foreach (var entry in db.ChangeTracker.Entries<ModelProviderDraftHeadRecord>()
                     .Where(entry => entry.Entity.ProviderId == providerId)
                     .ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<ModelProviderDraftRevisionRecord>()
                     .Where(entry => entry.Entity.ProviderId == providerId)
                     .ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<ModelProviderReferenceReservationRecord>()
                     .Where(entry => entry.Entity.ProviderId == providerId)
                     .ToArray())
            entry.State = EntityState.Detached;
    }

    private static bool IsUniqueConflict(Exception exception) =>
        GetPostgresException(exception) is
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "pk_model_providers" or
                "pk_model_provider_revisions" or
                "ux_model_provider_revisions_operation_id",
        };

    private static bool IsProviderWriteGuardFailure(Exception exception) =>
        GetPostgresException(exception) is
        {
            SqlState: PostgresErrorCodes.CheckViolation,
            ConstraintName: "ck_model_provider_write_guard",
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
        int? PromptCapacityTokens,
        string? RateCardId,
        string? RateCardVersion);

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
        int? PromptCapacityTokens,
        string? RateCardId,
        string? RateCardVersion);

    private sealed record SaveFingerprint(MetadataFingerprint Metadata, long ExpectedRevision);

}
