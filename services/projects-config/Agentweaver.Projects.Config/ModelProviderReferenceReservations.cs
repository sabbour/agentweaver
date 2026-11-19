using System.Text;
using System.Text.Json;
using System.Data;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

public sealed partial class ModelProviderDraftService
{
    private const string ProviderCreateLock = "agentweaver.projects_config.model_provider_create";

    internal Func<int, CancellationToken, Task>? AfterReferenceAuthorityLocked { get; set; }
    internal Func<CancellationToken, Task>? AfterCreateConflict { get; set; }

    private async Task<ModelProviderDraft> CreateAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        SaveModelProviderDraftRequest request,
        NormalizedProviderMetadata metadata,
        MetadataFingerprint fingerprintInput,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        var reference = request.Reference;
        if (reference is null ? request.TargetInstallationId is not null :
            !IsValidInstallationId(request.TargetInstallationId))
            throw ModelProviderDraftException.InvalidRequest("The target installation is invalid.");
        if (reference is not null &&
            (scope != ModelProviderBindingScope.Platform ||
             providerId != "byok-" + request.OperationId.ToString("N") ||
             !IsValidReference(reference)))
            throw ModelProviderDraftException.InvalidRequest("The provider reference is invalid.");

        var fingerprint = reference is null
            ? Hash(new SaveFingerprint(fingerprintInput, 0))
            : Hash(new ReferenceCreateFingerprint("provider-reference-create-v1", fingerprintInput, 0, reference));

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await LockProviderCreateAsync(cancellationToken).ConfigureAwait(false);
            if (reference is not null)
            {
                await LockReferenceAuthorityAsync(caller, cancellationToken).ConfigureAwait(false);
                await VerifyInstallationAsync(request.TargetInstallationId!, cancellationToken).ConfigureAwait(false);
            }
            var replay = await ReadCreateReplayAsync(
                caller, scope, projectId, providerId, request.OperationId, fingerprint, reference, request.TargetInstallationId, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }

            if (await db.ModelProviderReferenceReservations.AsNoTracking().AnyAsync(
                    item => item.ReferenceFolded == FoldReference(providerId), cancellationToken).ConfigureAwait(false))
                throw reference is null ? ModelProviderDraftException.Conflict() : ModelProviderDraftException.ReferenceConflict();

            if (reference is not null)
            {
                if (!referenceReservationsEnabled || !modelBindings.IsAvailable)
                    throw ModelProviderDraftException.Unavailable();
                await CheckReferenceCollisionsAsync(reference, providerId, cancellationToken).ConfigureAwait(false);
            }

            var now = timeProvider.GetUtcNow();
            var head = new ModelProviderDraftHeadRecord
            {
                ProviderId = providerId, Scope = scope, ProjectId = projectId,
                DisplayName = metadata.DisplayName, ProviderType = metadata.ProviderType,
                BaseUrl = metadata.BaseUrl, Deployment = metadata.Deployment, WireApi = metadata.WireApi,
                AzureApiVersion = metadata.AzureApiVersion, ReasoningEfforts = metadata.ReasoningEfforts,
                PromptCapacityTokens = metadata.PromptCapacityTokens, State = ModelProviderDraftState.Draft,
                CurrentRevision = 1, CreatedByActorId = caller.ActorId, CreatedAt = now,
                UpdatedByActorId = caller.ActorId, UpdatedAt = now,
            };
            var revision = new ModelProviderDraftRevisionRecord
            {
                ProviderId = providerId, Scope = scope, ProjectId = projectId, ProviderRevision = 1,
                OperationId = request.OperationId, RequestFingerprint = fingerprint,
                DisplayName = metadata.DisplayName, ProviderType = metadata.ProviderType,
                BaseUrl = metadata.BaseUrl, Deployment = metadata.Deployment, WireApi = metadata.WireApi,
                AzureApiVersion = metadata.AzureApiVersion, ReasoningEfforts = metadata.ReasoningEfforts,
                PromptCapacityTokens = metadata.PromptCapacityTokens, RateCardId = metadata.RateCardId,
                RateCardVersion = metadata.RateCardVersion, ConfigurationHash = configurationHash,
                CreatedByActorId = caller.ActorId, CreatedAt = now,
            };
            db.ModelProviderDrafts.Add(head);
            db.ModelProviderDraftRevisions.Add(revision);
            if (reference is not null)
                db.ModelProviderReferenceReservations.Add(new ModelProviderReferenceReservationRecord
                {
                    Reference = reference, ReferenceFolded = FoldReference(reference), ProviderId = providerId,
                    CreationRevision = 1, OperationId = request.OperationId, Issuer = caller.Issuer,
                    InstallationId = Guid.ParseExact(request.TargetInstallationId!, "D"),
                    TenantId = caller.TenantId, Subject = caller.ActorId, CreatedAt = now,
                });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            DetachTrackedProvider(providerId);
            return ToDraft(ModelProviderDraftState.Draft, revision, reference);
        }
        catch (Exception exception) when (reference is not null &&
            GetPostgresException(exception) is { SqlState: PostgresErrorCodes.CheckViolation,
                ConstraintName: "ck_provider_reference_installation_id" })
        {
            DetachTrackedProvider(providerId);
            throw ModelProviderDraftException.TargetInstallationMismatch();
        }
        catch (Exception exception) when (IsCreateConflict(exception))
        {
            DetachTrackedProvider(providerId);
            if (AfterCreateConflict is not null)
                await AfterCreateConflict(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            await LockProviderCreateAsync(cancellationToken).ConfigureAwait(false);
            if (reference is not null)
            {
                await LockReferenceAuthorityAsync(caller, cancellationToken).ConfigureAwait(false);
                await VerifyInstallationAsync(request.TargetInstallationId!, cancellationToken).ConfigureAwait(false);
            }
            var replay = await ReadCreateReplayAsync(
                caller, scope, projectId, providerId, request.OperationId, fingerprint, reference, request.TargetInstallationId, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }
            if (reference is not null &&
                (IsReferenceCollision(exception) ||
                 await db.ModelProviderReferenceReservations.AsNoTracking().AnyAsync(
                     item => item.ReferenceFolded == FoldReference(reference) ||
                             item.ReferenceFolded == FoldReference(providerId), cancellationToken).ConfigureAwait(false)))
                throw ModelProviderDraftException.ReferenceConflict();
            throw ModelProviderDraftException.Conflict();
        }
        catch (Exception exception) when (IsProviderWriteGuardFailure(exception))
        {
            DetachTrackedProvider(providerId);
            throw ModelProviderDraftException.Unavailable();
        }
        catch
        {
            DetachTrackedProvider(providerId);
            throw;
        }
    }

    private async Task<ModelProviderDraft?> ReadCreateReplayAsync(
        ProjectAuthorizationContext caller,
        ModelProviderBindingScope scope,
        string? projectId,
        string providerId,
        Guid operationId,
        string fingerprint,
        string? reference,
        string? targetInstallationId,
        CancellationToken cancellationToken)
    {
        var reservation = await db.ModelProviderReferenceReservations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (reference is not null && reservation is not null &&
            (reservation.Issuer != caller.Issuer || reservation.TenantId != caller.TenantId ||
             reservation.Subject != caller.ActorId))
            throw ModelProviderDraftException.Conflict();
        if (reference is not null && reservation is not null &&
            reservation.InstallationId != Guid.ParseExact(targetInstallationId!, "D"))
            throw ModelProviderDraftException.ReferenceReconciliationRequired();
        var revision = await db.ModelProviderDraftRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            if (reservation is not null)
                throw ModelProviderDraftException.Conflict();
            return null;
        }
        if (revision.ProviderId != providerId || revision.Scope != scope || revision.ProjectId != projectId ||
            revision.ProviderRevision != 1 || revision.RequestFingerprint != fingerprint ||
            (reference is null ? reservation is not null :
                reservation is null || reservation.Reference != reference || reservation.ProviderId != providerId ||
                reservation.Issuer != caller.Issuer || reservation.TenantId != caller.TenantId ||
                reservation.Subject != caller.ActorId))
            throw ModelProviderDraftException.Conflict();
        return ToDraft(ModelProviderDraftState.Draft, revision, reservation?.Reference);
    }

    private async Task LockProviderCreateAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended({ProviderCreateLock}, 0))",
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsValidInstallationId(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty &&
        value == id.ToString("D");

    private async Task VerifyInstallationAsync(string target, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton = true",
                connection, transaction);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not Guid id || id == Guid.Empty)
                throw ModelProviderDraftException.Unavailable();
            if (id != Guid.ParseExact(target, "D"))
                throw ModelProviderDraftException.TargetInstallationMismatch();
        }
        catch (NpgsqlException)
        {
            throw ModelProviderDraftException.Unavailable();
        }
    }

    private async Task LockReferenceAuthorityAsync(ProjectAuthorizationContext caller, CancellationToken cancellationToken)
    {
        var grants = caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Platform &&
            grant.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
            grant.Role == ProjectAuthorityRole.PlatformAdmin).ToArray();
        if (grants.Length == 0)
            throw ProjectConfigException.Forbidden();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand("""
            SELECT assignment_id, revision, authorization_result
            FROM projects_config.lock_native_acs_policy_authority(
                @membership_id, @issuer, @actor, @tenant, @membership_revision,
                'Platform', 'default', @ids, @types, @resources, @roles, @revisions)
            """, connection, transaction);
        command.Parameters.AddWithValue("membership_id", caller.MembershipId);
        command.Parameters.AddWithValue("issuer", caller.Issuer);
        command.Parameters.AddWithValue("actor", caller.ActorId);
        command.Parameters.AddWithValue("tenant", caller.TenantId);
        command.Parameters.AddWithValue("membership_revision", caller.MembershipRevision);
        command.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            grants.Select(item => item.AssignmentId).ToArray());
        command.Parameters.AddWithValue("types", NpgsqlDbType.Array | NpgsqlDbType.Text,
            grants.Select(item => item.ResourceType.ToString()).ToArray());
        command.Parameters.AddWithValue("resources", NpgsqlDbType.Array | NpgsqlDbType.Text,
            grants.Select(item => item.ResourceId).ToArray());
        command.Parameters.AddWithValue("roles", NpgsqlDbType.Array | NpgsqlDbType.Text,
            grants.Select(item => item.Role.ToString()).ToArray());
        command.Parameters.AddWithValue("revisions", NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            grants.Select(item => item.Revision).ToArray());
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(2) != "authorized" ||
                !grants.Any(item => item.AssignmentId == reader.GetGuid(0) && item.Revision == reader.GetInt64(1)))
                throw ProjectConfigException.Forbidden();
        }
        if (AfterReferenceAuthorityLocked is not null)
            await AfterReferenceAuthorityLocked(connection.ProcessID, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadReferenceAsync(string providerId, CancellationToken cancellationToken) =>
        await db.ModelProviderReferenceReservations.AsNoTracking()
            .Where(item => item.ProviderId == providerId).Select(item => item.Reference)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private async Task CheckReferenceCollisionsAsync(
        string reference, string providerId, CancellationToken cancellationToken)
    {
        var folded = FoldReference(reference);
        var foldedId = FoldReference(providerId);
        if (folded == foldedId ||
            await db.ModelProviderReferenceReservations.AsNoTracking()
                .AnyAsync(item => item.ReferenceFolded == folded, cancellationToken).ConfigureAwait(false))
            throw ModelProviderDraftException.ReferenceConflict();

        var providerIds = await db.ModelProviderDrafts.AsNoTracking()
            .Select(item => item.ProviderId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (providerIds.Any(item => FoldReference(item) == folded || FoldReference(item) == foldedId))
            throw ModelProviderDraftException.ReferenceConflict();

        var matches = modelBindings.Bindings.Where(item => FoldReference(item.Reference) == folded).ToArray();
        if (matches.Any(item => item.SourceMode != "Byok" || item.Reference != reference) ||
            modelBindings.Bindings.Any(item => FoldReference(item.Reference) == foldedId))
            throw ModelProviderDraftException.ReferenceConflict();

        // S1 checks a creation-time snapshot; S2 must fence later saves and pending casting proposals.
        var savedReferences = await ReadCurrentSelectorReferencesAsync(cancellationToken).ConfigureAwait(false);
        if (savedReferences.Any(item => FoldReference(item) == foldedId) ||
            savedReferences.Any(item => FoldReference(item) == folded &&
                (item != reference || !matches.Any(binding =>
                    binding.SourceMode == "Byok" && binding.Reference == reference))))
            throw ModelProviderDraftException.ReferenceConflict();
    }

    private async Task<List<string>> ReadCurrentSelectorReferencesAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand("""
            SELECT 'platform', head.current_revision, revision.revision, revision.configuration::text
            FROM (SELECT 1) AS singleton
            LEFT JOIN projects_config.platform_runtime_heads AS head ON head.id = 'default'
            LEFT JOIN projects_config.platform_runtime_revisions AS revision
                ON revision.head_id = head.id AND revision.revision = head.current_revision
            UNION ALL
            SELECT 'project', project.configuration_revision, revision.revision, revision.configuration::text
            FROM projects_config.projects AS project
            LEFT JOIN projects_config.project_configuration_revisions AS revision
                ON revision.project_id = project.project_id AND revision.revision = project.configuration_revision
            """, connection, transaction);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1))
                throw ModelProviderDraftException.Unavailable();
            var revision = reader.GetInt64(1);
            if (reader.GetString(0) == "platform" && revision == 0)
                continue;
            if (revision <= 0 || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.GetInt64(2) != revision)
                throw ModelProviderDraftException.Unavailable();
            if (reader.GetString(0) == "platform")
            {
                var defaults = ReadStoredSelectors<PlatformRuntimeDefaults>(reader.GetString(3));
                if (defaults.ModelTaskDefaults.IsDefault || defaults.ModelRoleDefaults.IsDefault ||
                    defaults.ModelAgentDefaults.IsDefault)
                    throw ModelProviderDraftException.Unavailable();
                AddPrimary(result, defaults.ModelSelection);
                foreach (var item in defaults.ModelTaskDefaults)
                    AddOverride(result, item?.Selection);
                foreach (var item in defaults.ModelRoleDefaults)
                    AddOverride(result, item?.Selection);
                foreach (var item in defaults.ModelAgentDefaults)
                    AddOverride(result, item?.Selection);
            }
            else
            {
                var project = ReadStoredSelectors<ProjectConfiguration>(reader.GetString(3));
                if (project.ModelTaskOverrides.IsDefault || project.ModelRoleOverrides.IsDefault ||
                    project.ModelAgentOverrides.IsDefault)
                    throw ModelProviderDraftException.Unavailable();
                AddPrimary(result, project.ModelSelection);
                foreach (var item in project.ModelTaskOverrides)
                    AddOverride(result, item?.Selection);
                foreach (var item in project.ModelRoleOverrides)
                    AddOverride(result, item?.Selection);
                foreach (var item in project.ModelAgentOverrides)
                    AddOverride(result, item?.Selection);
            }
        }
        return result;
    }

    private static T ReadStoredSelectors<T>(string json)
    {
        try
        {
            return ProjectsConfigService.Deserialize<T>(json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw ModelProviderDraftException.Unavailable();
        }
    }

    private static void AddPrimary(List<string> result, ModelSelectionSettings? selection)
    {
        if (selection is null)
            return;
        if (!IsValidSavedReference(selection.Reference))
            throw ModelProviderDraftException.Unavailable();
        result.Add(selection.Reference);
    }

    private static void AddOverride(List<string> result, ModelSelectionOverride? selection)
    {
        if (selection is null)
            throw ModelProviderDraftException.Unavailable();
        if (selection.Reference is { } reference)
        {
            if (!IsValidSavedReference(reference))
                throw ModelProviderDraftException.Unavailable();
            result.Add(reference);
        }
    }

    private static bool IsValidSavedReference(string? reference) =>
        !string.IsNullOrWhiteSpace(reference) && reference.Length <= 256 &&
        reference.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    internal static bool IsValidReference(string value) =>
        value.Length is >= 1 and <= 256 && value[0] != '_' && value[^1] != '_' &&
        !value.Contains("__", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static string FoldReference(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            result.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        return result.ToString();
    }

    private static bool IsReferenceCollision(Exception exception) =>
        GetPostgresException(exception) is { } postgres &&
        ((postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
          postgres.ConstraintName is "pk_model_provider_reference_reservations" or
              "ux_model_provider_reference_reservations_provider_id" or
              "ux_model_provider_reference_reservations_operation_id") ||
         (postgres.SqlState == PostgresErrorCodes.CheckViolation &&
          postgres.ConstraintName == "ck_model_provider_reference_cross_table"));

    private static bool IsCreateConflict(Exception exception) =>
        IsUniqueConflict(exception) || IsReferenceCollision(exception);

    private static PostgresException? GetPostgresException(Exception exception) =>
        exception as PostgresException ?? (exception as DbUpdateException)?.InnerException as PostgresException;

    private sealed record ReferenceCreateFingerprint(
        string Kind, MetadataFingerprint Metadata, long ExpectedRevision, string Reference);
}
