using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed class SkillContentServiceException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;

    public static SkillContentServiceException Invalid(string message) =>
        new("invalid_skill_request", message, StatusCodes.Status400BadRequest);

    public static SkillContentServiceException Conflict(string message) =>
        new("skill_content_conflict", message, StatusCodes.Status409Conflict);

    public static SkillContentServiceException IdempotencyConflict() =>
        new("skill_import_idempotency_conflict", "The idempotency key was used for a different skill import.",
            StatusCodes.Status409Conflict);

    public static SkillContentServiceException Unavailable() =>
        new("skill_content_unavailable", "Skill content storage is not configured.",
            StatusCodes.Status503ServiceUnavailable);
}

public sealed class SkillContentService(
    ProjectsConfigDbContext db,
    SkillContentObjectStore? objects,
    ProjectsConfigService configurations,
    TimeProvider timeProvider) : ISkillContentService, ISkillAssignmentService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public SkillContentPreview Preview(SkillContentCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var content = candidate.Validate();
        return new SkillContentPreview(
            content.Name,
            content.Description,
            content.ContentDigest,
            content.Resources.Length,
            SemanticByteCount(content));
    }

    public async Task<SkillContentImportReceipt> ImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        SkillContentImportRequest request,
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
        var objectKey = MakeObjectKey(projectId, skillId, revision, content.ContentDigest);
        var reference = await store.WriteAsync(objectKey, content, cancellationToken).ConfigureAwait(false);
        if (reference.ContentDigest != content.ContentDigest)
            throw new SkillContentObjectStoreException(
                SkillContentObjectStoreException.IntegrityErrorCode,
                "The immutable skill content object digest changed during storage.");

        var now = timeProvider.GetUtcNow();
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        db.SkillContentRevisions.Add(new ProjectSkillContentRevisionRecord
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
        });
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
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
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
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await EnsureWriteAccessAsync(caller, projectId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

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
            current.Configuration with { Skills = settings },
            cancellationToken).ConfigureAwait(false);
    }

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
        var result = ImmutableArray.CreateBuilder<SkillRuntimeContentV1>();
        foreach (var assignment in assignments)
        {
            var revision = await FindAvailableRevisionAsync(
                projectId,
                assignment.SkillId,
                assignment.Revision!.Value,
                assignment.ContentDigest!,
                cancellationToken).ConfigureAwait(false);
            var verified = await store.ReadVerifiedAsync(
                new ObjectKey(revision.ObjectKey),
                assignment.ContentDigest!,
                cancellationToken).ConfigureAwait(false);
            if (verified.Name != revision.Name ||
                verified.Description != revision.Description ||
                verified.Resources.Length != revision.ResourceCount ||
                SemanticByteCount(verified) != revision.TotalBytes)
                throw new SkillContentObjectStoreException(
                    SkillContentObjectStoreException.IntegrityErrorCode,
                    "The immutable skill revision metadata does not match its stored content.");
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
        return result.ToImmutable();
    }

    internal static async Task<ProjectConfiguration> ValidateConfigurationAsync(
        ProjectsConfigDbContext db,
        string projectId,
        ProjectConfiguration previous,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var normalized = ProjectConfigurationValidator.ValidateTransition(previous, configuration);
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
            _ = await FindAvailableRevisionAsync(
                db,
                projectId,
                assignment.SkillId,
                assignment.Revision.Value,
                assignment.ContentDigest,
                cancellationToken).ConfigureAwait(false);
        }
        return normalized;
    }

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

    private static async Task<ProjectSkillContentRevisionRecord> FindAvailableRevisionAsync(
        ProjectsConfigDbContext db,
        string projectId,
        string skillId,
        long revision,
        string contentDigest,
        CancellationToken cancellationToken)
    {
        var stored = await db.SkillContentRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ProjectId == projectId && item.SkillId == skillId && item.Revision == revision,
                cancellationToken).ConfigureAwait(false);
        if (stored is null ||
            !string.Equals(stored.ContentDigest, contentDigest, StringComparison.Ordinal))
            throw ProjectConfigException.NotFound();
        if (await db.SkillContentRevocations.AsNoTracking().AnyAsync(item =>
            item.ProjectId == projectId && item.SkillId == skillId && item.Revision == revision,
            cancellationToken).ConfigureAwait(false))
            throw SkillContentServiceException.Conflict("The selected skill revision has been revoked.");
        return stored;
    }

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

    private static string ImportRequestDigest(SkillContentImportRequest request) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.SkillId,
            request.ExpectedRevision,
            request.ExpectedContentDigest,
            request.Source,
        }, JsonOptions));

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

    private static long SemanticByteCount(ValidatedSkillContent content)
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
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
