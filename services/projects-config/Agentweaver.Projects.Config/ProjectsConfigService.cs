using System.Collections.Immutable;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed record ProjectSummary(
    string ProjectId,
    string Name,
    ProjectLifecycleState State,
    long Revision,
    long ConfigurationRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record VersionedProjectConfiguration(
    string ProjectId,
    long Revision,
    ProjectConfiguration Configuration,
    string UpdatedByActorId,
    DateTimeOffset CreatedAt);

public sealed record VersionedPlatformRuntimeDefaults(
    long Revision,
    PlatformRuntimeDefaults? Defaults,
    string? UpdatedByActorId,
    DateTimeOffset? CreatedAt);

public sealed record EffectiveRunSelection(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    ModelSelectionSettings ModelSelection,
    ImmutableArray<EffectiveProviderSelection> Providers,
    ImmutableArray<NetworkEgressRule> EgressAllowlist,
    CopilotRunLimits RunLimits,
    ProjectConfiguration ProjectConfiguration,
    ImmutableArray<NetworkEgressRule> EgressBaseline,
    ImmutableArray<NetworkEgressRule>? ProjectEgressNarrowing,
    ImmutableArray<NetworkEgressRule> RequiredEgress);

public sealed class ProjectsConfigService(
    ProjectsConfigDbContext db,
    ProviderCatalog providerCatalog,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal async Task<ProjectRecord> GetMarketplaceProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        bool requireWrite,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        if (requireWrite)
            caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        var project = await FindProjectAsync(
            caller,
            projectId,
            requireWrite ? ProjectAccess.Write : ProjectAccess.Read,
            cancellationToken).ConfigureAwait(false);
        if (requireWrite && project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot be reconfigured.");
        return project;
    }

    public async Task<ProjectSummary> CreateProjectAsync(
        ProjectAuthorizationContext caller,
        string name,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Tenant,
            caller.TenantId,
            ProjectAuthorityRole.TenantAdmin,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        ValidateProjectName(name);
        var now = timeProvider.GetUtcNow();
        var project = new ProjectRecord
        {
            ProjectId = Guid.NewGuid().ToString("N"),
            TenantId = caller.TenantId,
            CreatedByActorId = caller.ActorId,
            Name = name.Trim(),
            State = ProjectLifecycleState.Active,
            Revision = 1,
            ConfigurationRevision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var configuration = new ProjectConfiguration();
        db.Projects.Add(project);
        db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
        {
            ProjectId = project.ProjectId,
            Revision = 1,
            ConfigurationJson = Serialize(configuration),
            UpdatedByActorId = caller.ActorId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSummary(project);
    }

    public async Task<ImmutableArray<ProjectSummary>> ListProjectsAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        await EnsureCurrentMembershipAsync(caller, cancellationToken).ConfigureAwait(false);
        var query = db.Projects.AsNoTracking()
            .Where(project => project.TenantId == caller.TenantId);
        var isTenantAdmin = await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Tenant,
            caller.TenantId,
            ProjectAuthorityRole.TenantAdmin,
            cancellationToken).ConfigureAwait(false);
        var projects = await query.OrderBy(project => project.CreatedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (isTenantAdmin)
            return projects.Select(ToSummary).ToImmutableArray();

        var visible = ImmutableArray.CreateBuilder<ProjectSummary>();
        foreach (var project in projects)
        {
            if (await HasAnyCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Project,
                project.ProjectId,
                [
                    ProjectAuthorityRole.Owner,
                    ProjectAuthorityRole.Contributor,
                    ProjectAuthorityRole.Viewer,
                ],
                cancellationToken).ConfigureAwait(false))
                visible.Add(ToSummary(project));
        }
        return visible.ToImmutable();
    }

    public async Task<ProjectSummary> GetProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
        => await GetProjectAsync(caller, projectId, runId: null, cancellationToken).ConfigureAwait(false);

    public async Task<ProjectSummary> GetProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string? runId,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        if (runId is not null)
        {
            ValidateIdentifier(runId, "runId");
            caller.RequireExactRunBinding(projectId, runId);
        }
        var project = await FindProjectAsync(
            caller, projectId, ProjectAccess.Read, runId, cancellationToken).ConfigureAwait(false);
        return ToSummary(project);
    }

    public async Task<ProjectSummary> UpdateProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedRevision,
        string name,
        ProjectLifecycleState state,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        ValidateProjectName(name);
        if (!Enum.IsDefined(state))
            throw new ProjectConfigException("invalid_project_state", "Project lifecycle state is invalid.", StatusCodes.Status400BadRequest);
        var project = await FindProjectAsync(caller, projectId, ProjectAccess.Write, cancellationToken)
            .ConfigureAwait(false);
        if (project.Revision != expectedRevision)
            throw ProjectConfigException.Conflict("The project revision has changed.");
        project.Name = name.Trim();
        project.State = state;
        project.Revision = checked(project.Revision + 1);
        project.UpdatedAt = timeProvider.GetUtcNow();
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ProjectConfigException.Conflict("The project revision has changed.");
        }
        return ToSummary(project);
    }

    public async Task<VersionedProjectConfiguration> GetProjectConfigurationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long? revision,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        var project = await FindProjectAsync(
            caller, projectId, ProjectAccess.Read, cancellationToken).ConfigureAwait(false);
        var requestedRevision = revision ?? project.ConfigurationRevision;
        var configuration = await db.ProjectConfigurationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == project.ProjectId && item.Revision == requestedRevision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        return new VersionedProjectConfiguration(
            project.ProjectId,
            configuration.Revision,
            Deserialize<ProjectConfiguration>(configuration.ConfigurationJson),
            configuration.UpdatedByActorId,
            configuration.CreatedAt);
    }

    public async Task<VersionedProjectConfiguration> UpdateProjectConfigurationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedRevision,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        var normalized = ProjectConfigurationValidator.Validate(configuration);
        var project = await FindProjectAsync(caller, projectId, ProjectAccess.Write, cancellationToken)
            .ConfigureAwait(false);
        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot be reconfigured.");
        if (project.ConfigurationRevision != expectedRevision)
            throw ProjectConfigException.Conflict("The project configuration revision has changed.");

        await EnsureProjectNarrowingAsync(normalized, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var nextRevision = checked(project.ConfigurationRevision + 1);
        db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
        {
            ProjectId = project.ProjectId,
            Revision = nextRevision,
            ConfigurationJson = Serialize(normalized),
            UpdatedByActorId = caller.ActorId,
            CreatedAt = now,
        });
        project.ConfigurationRevision = nextRevision;
        project.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ProjectConfigException.Conflict("The project configuration revision has changed.");
        }

        return new VersionedProjectConfiguration(project.ProjectId, nextRevision, normalized, caller.ActorId, now);
    }

    public async Task<VersionedPlatformRuntimeDefaults> GetPlatformRuntimeDefaultsAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        var head = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPlatformDefaultsAsync(head, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VersionedPlatformRuntimeDefaults> UpdatePlatformRuntimeDefaultsAsync(
        ProjectAuthorizationContext caller,
        long expectedRevision,
        PlatformRuntimeDefaults defaults,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        var normalized = ProjectConfigurationValidator.Validate(defaults);
        var head = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head.CurrentRevision != expectedRevision)
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");

        var now = timeProvider.GetUtcNow();
        var nextRevision = checked(head.CurrentRevision + 1);
        db.PlatformRuntimeRevisions.Add(new PlatformRuntimeRevisionRecord
        {
            HeadId = PlatformRuntimeHeadRecord.SingletonId,
            Revision = nextRevision,
            ConfigurationJson = Serialize(normalized),
            UpdatedByActorId = caller.ActorId,
            CreatedAt = now,
        });
        head.CurrentRevision = nextRevision;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
        }

        return new VersionedPlatformRuntimeDefaults(nextRevision, normalized, caller.ActorId, now);
    }

    public async Task<EffectiveRunSelection> AcceptRunSelectionAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        AcceptRunSelectionRequest request,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireResourceBinding(projectId, runId);
        ValidateIdentifier(runId, "runId");
        ArgumentNullException.ThrowIfNull(request);
        ValidateSelectionContext(request.Context);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await LockRunSelectionInputsAsync(caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        var project = await FindTenantProjectAsync(caller, projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        var fingerprint = Fingerprint(project.ProjectId, runId, request);
        var existing = await db.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(selection => selection.RunId == runId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ReadExistingSelection(existing, project.ProjectId, fingerprint);
        }

        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot start runs.");

        if (project.ConfigurationRevision != request.ExpectedProjectConfigRevision)
            throw ProjectConfigException.Conflict("The project configuration revision has changed.");

        var projectVersion = await db.ProjectConfigurationRevisions.AsNoTracking()
            .SingleAsync(item => item.ProjectId == project.ProjectId &&
                item.Revision == project.ConfigurationRevision, cancellationToken)
            .ConfigureAwait(false);
        var projectConfiguration = Deserialize<ProjectConfiguration>(projectVersion.ConfigurationJson);

        var platformHead = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        if (platformHead.CurrentRevision == 0)
            throw new ProjectConfigException(
                "platform_runtime_unconfigured",
                "Platform runtime defaults must be configured before accepting a run.",
                StatusCodes.Status409Conflict);
        if (platformHead.CurrentRevision != request.ExpectedPlatformRuntimeRevision)
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
        var platformVersion = await db.PlatformRuntimeRevisions.AsNoTracking()
            .SingleAsync(item => item.Revision == platformHead.CurrentRevision, cancellationToken)
            .ConfigureAwait(false);
        var defaults = Deserialize<PlatformRuntimeDefaults>(platformVersion.ConfigurationJson);

        var model = ResolveModelSelection(projectConfiguration, defaults, request.Context);
        var providers = ResolveProviders(projectConfiguration, request.Context);
        var egress = ProjectConfigurationValidator.ResolveEgress(
            defaults.EgressBaseline,
            projectConfiguration.EgressNarrowing,
            request.Context.RequiredEgress);
        var limits = ProjectConfigurationValidator.ResolveLimits(defaults, projectConfiguration.RunLimits);
        var effective = new EffectiveRunSelection(
            project.ProjectId,
            runId,
            project.Revision,
            project.ConfigurationRevision,
            platformHead.CurrentRevision,
            request.Context.Revision,
            model,
            providers,
            egress,
            limits,
            projectConfiguration,
            defaults.EgressBaseline,
            projectConfiguration.EgressNarrowing,
            ProjectConfigurationValidator.ValidateEgressRules(request.Context.RequiredEgress));
        var record = new ProjectRunSelectionRecord
        {
            RunId = runId,
            ProjectId = project.ProjectId,
            ProjectRevision = project.Revision,
            ProjectConfigurationRevision = project.ConfigurationRevision,
            PlatformRuntimeRevision = platformHead.CurrentRevision,
            ContextRevision = request.Context.Revision,
            RequestFingerprint = fingerprint,
            SnapshotJson = Serialize(effective),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        db.RunSelections.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (!await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Project,
                projectId,
                ProjectAuthorityRole.Orchestrator,
                cancellationToken).ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return effective;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            db.Entry(record).State = EntityState.Detached;
            var concurrent = await db.RunSelections.AsNoTracking()
                .SingleOrDefaultAsync(selection => selection.RunId == runId, cancellationToken)
                .ConfigureAwait(false);
            if (concurrent is null) throw;
            return ReadExistingSelection(concurrent, project.ProjectId, fingerprint);
        }
    }

    private async Task LockRunSelectionInputsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using (var projectLock = new NpgsqlCommand(
            """
            SELECT project_id
            FROM projects_config.projects
            WHERE project_id = @project_id
              AND tenant_id = @tenant_id
            FOR SHARE
            """,
            connection,
            transaction))
        {
            projectLock.Parameters.AddWithValue("project_id", projectId);
            projectLock.Parameters.AddWithValue("tenant_id", caller.TenantId);
            if (await projectLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                return;
        }

        await using (var platformLock = new NpgsqlCommand(
            "SELECT id FROM projects_config.platform_runtime_heads WHERE id = 'default' FOR SHARE",
            connection,
            transaction))
            await platformLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        await using var runLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@run_id, 0))", connection, transaction);
        runLock.Parameters.AddWithValue("run_id", runId);
        await runLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EffectiveRunSelection> GetRunSelectionAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireResourceBinding(projectId, runId);
        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        var project = await FindTenantProjectAsync(caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        var selection = await db.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == project.ProjectId && item.RunId == runId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        return DeserializeRunSelection(selection.SnapshotJson);
    }

    private async Task<ProjectRecord> FindProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        ProjectAccess access,
        CancellationToken cancellationToken)
        => await FindProjectAsync(
            caller, projectId, access, runId: null, cancellationToken).ConfigureAwait(false);

    private async Task<ProjectRecord> FindProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        ProjectAccess access,
        string? runId,
        CancellationToken cancellationToken)
    {
        caller.RequireResourceBinding(projectId, runId);
        await EnsureCurrentMembershipAsync(caller, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
        var project = await db.Projects.SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
            throw ProjectConfigException.NotFound();
        if (project.TenantId != caller.TenantId)
            throw ProjectConfigException.NotFound();
        var isTenantAdmin = await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Tenant,
            caller.TenantId,
            ProjectAuthorityRole.TenantAdmin,
            cancellationToken).ConfigureAwait(false);
        var hasProjectRole = access == ProjectAccess.Read
            ? await HasAnyCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Project,
                projectId,
                [
                    ProjectAuthorityRole.Owner,
                    ProjectAuthorityRole.Contributor,
                    ProjectAuthorityRole.Viewer,
                ],
                cancellationToken).ConfigureAwait(false)
            : await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Project,
                projectId,
                ProjectAuthorityRole.Owner,
                cancellationToken).ConfigureAwait(false);
        if (isTenantAdmin || hasProjectRole)
            return project;
        throw ProjectConfigException.NotFound();
    }

    private async Task<ProjectRecord> FindTenantProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string? runId,
        CancellationToken cancellationToken)
    {
        caller.RequireResourceBinding(projectId, runId);
        await EnsureCurrentMembershipAsync(caller, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null || project.TenantId != caller.TenantId)
            throw ProjectConfigException.NotFound();
        return project;
    }

    private async Task EnsureProjectNarrowingAsync(
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var limits = configuration.RunLimits;
        if (configuration.EgressNarrowing is null &&
            limits.MaxModelTurns is null &&
            limits.MaxToolCalls is null &&
            limits.MaxChildren is null &&
            limits.MaxConcurrentChildren is null &&
            limits.MaxWallTimeSeconds is null &&
            limits.MaxPromptTokens is null)
            return;
        var head = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head.CurrentRevision == 0)
            throw new ProjectConfigException(
                "platform_runtime_unconfigured",
                "Platform runtime defaults must be configured before project runtime overrides.",
                StatusCodes.Status409Conflict);
        var defaults = await ReadPlatformDefaultsAsync(head, cancellationToken).ConfigureAwait(false);
        _ = ProjectConfigurationValidator.ResolveEgress(
            defaults.Defaults!.EgressBaseline,
            configuration.EgressNarrowing,
            []);
        _ = ProjectConfigurationValidator.ResolveLimits(defaults.Defaults, limits);
    }

    private async Task<PlatformRuntimeHeadRecord> GetPlatformHeadAsync(CancellationToken cancellationToken) =>
        await db.PlatformRuntimeHeads.SingleOrDefaultAsync(
            head => head.Id == PlatformRuntimeHeadRecord.SingletonId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The platform runtime head row is missing from the Projects & Config schema.");

    private async Task<VersionedPlatformRuntimeDefaults> ReadPlatformDefaultsAsync(
        PlatformRuntimeHeadRecord head,
        CancellationToken cancellationToken)
    {
        if (head.CurrentRevision == 0)
            return new VersionedPlatformRuntimeDefaults(0, null, null, null);
        var revision = await db.PlatformRuntimeRevisions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Revision == head.CurrentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The platform runtime head references a missing immutable revision.");
        return new VersionedPlatformRuntimeDefaults(
            revision.Revision,
            Deserialize<PlatformRuntimeDefaults>(revision.ConfigurationJson),
            revision.UpdatedByActorId,
            revision.CreatedAt);
    }

    private static ModelSelectionSettings ResolveModelSelection(
        ProjectConfiguration project,
        PlatformRuntimeDefaults platform,
        RunSelectionContext context)
    {
        var selected = project.ModelSelection ?? platform.ModelSelection;
        if (selected is null)
            throw new ProjectConfigException(
                "model_selection_unavailable",
                "No project or platform model selection is configured.",
                StatusCodes.Status422UnprocessableEntity);
        if (!context.AvailableModelSelectionReferences.Contains(selected.Reference))
            throw new ProjectConfigException(
                "model_selection_unavailable",
                $"The configured model selection '{selected.Reference}' is unavailable in this run's immutable selection context.",
                StatusCodes.Status422UnprocessableEntity);
        return selected;
    }

    private ImmutableArray<EffectiveProviderSelection> ResolveProviders(
        ProjectConfiguration project,
        RunSelectionContext context)
    {
        var resolver = new ProviderResolver(providerCatalog);
        var selections = ImmutableArray.CreateBuilder<EffectiveProviderSelection>();
        foreach (var requirement in context.ProviderRequirements.OrderBy(item => item.Seam)
            .ThenBy(item => item.MeterSource, StringComparer.Ordinal))
        {
            var version = Version.TryParse(requirement.RequiredAdapterVersion, out var parsed)
                ? parsed
                : throw new ProjectConfigException(
                    "invalid_run_selection_context",
                    $"Provider adapter version for '{requirement.Seam}' is invalid.",
                    StatusCodes.Status400BadRequest);
            var cardinality = ProviderSeams.Cardinality(requirement.Seam);
            switch (cardinality)
            {
                case ProviderCardinality.Exclusive:
                case ProviderCardinality.PlatformSingleton:
                {
                    var providerOverride = project.ProviderOverrides
                        .SingleOrDefault(item => item.Seam == requirement.Seam)?.ProviderId;
                    var result = resolver.Resolve(new ProviderResolutionRequest(
                        requirement.Seam,
                        providerOverride,
                        version,
                        requirement.RequiredOptionsSchemaVersion,
                        requirement.RequiredCapabilities));
                    var resolution = RequireProviderResult(result);
                    if (resolution.Candidate is { } candidate)
                        selections.Add(new EffectiveProviderSelection(cardinality, requirement.Seam, [ToEffective(candidate)]));
                    break;
                }
                case ProviderCardinality.OrderedComposite:
                {
                    var projectIds = project.OrderedProviderOverrides
                        .SingleOrDefault(item => item.Seam == requirement.Seam)?.ProviderIds;
                    var result = resolver.ResolveOrdered(new OrderedProviderResolutionRequest(
                        requirement.Seam,
                        projectIds,
                        version,
                        requirement.RequiredOptionsSchemaVersion,
                        requirement.RequiredCapabilities));
                    var resolution = RequireProviderResult(result);
                    selections.Add(new EffectiveProviderSelection(
                        cardinality,
                        requirement.Seam,
                        resolution.Candidates.Select(candidate => ToEffective(candidate)).ToImmutableArray()));
                    break;
                }
                case ProviderCardinality.Layered:
                {
                    var result = resolver.ResolveNetworkPolicy(new NetworkPolicyResolutionRequest(
                        version,
                        requirement.RequiredOptionsSchemaVersion,
                        requirement.RequiredL3L4Capabilities,
                        requirement.RequiredL7Capabilities));
                    var resolution = RequireProviderResult(result);
                    selections.Add(new EffectiveProviderSelection(
                        cardinality,
                        requirement.Seam,
                        resolution.Layers
                            .Select(layer => ToEffective(layer.Candidate, layer.Layer))
                            .ToImmutableArray()));
                    break;
                }
                case ProviderCardinality.KeyedByMeterSource:
                {
                    var resolution = RequireProviderResult(resolver.ResolveCost(
                        new CostProviderResolutionRequest(
                            requirement.MeterSource!, version, requirement.RequiredOptionsSchemaVersion,
                            requirement.RequiredCapabilities)));
                    selections.Add(new EffectiveProviderSelection(
                        cardinality, requirement.Seam, [ToEffective(resolution.Candidate)],
                        resolution.MeterSource));
                    break;
                }
                case ProviderCardinality.PerApplication:
                {
                    var result = resolver.Resolve(new ProviderResolutionRequest(
                        requirement.Seam,
                        null,
                        version,
                        requirement.RequiredOptionsSchemaVersion,
                        requirement.RequiredCapabilities));
                    _ = RequireProviderResult(result);
                    throw new InvalidOperationException("Unsupported provider cardinality unexpectedly resolved.");
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(requirement.Seam));
            }
        }
        return selections.ToImmutable();
    }

    private static T RequireProviderResult<T>(ProviderResult<T> result) where T : class
    {
        if (result.IsSuccess)
            return result.Value!;
        var error = result.Error!;
        throw new ProjectConfigException(
            "provider_resolution_failed",
            $"{error.Code}: {error.Message}",
            StatusCodes.Status422UnprocessableEntity);
    }

    private static EffectiveProviderCandidate ToEffective(
        ProviderCandidate candidate,
        NetworkPolicyLayer? layer = null) =>
        new(
            candidate.Seam,
            candidate.ProviderId,
            candidate.AdapterVersion.ToString(),
            candidate.OptionsSchemaVersion,
            candidate.OptionsRevision,
            candidate.Hosting,
            candidate.AdvertisedCapabilities.Order(StringComparer.Ordinal).ToImmutableArray(),
            candidate.RequiredCapabilities.Order(StringComparer.Ordinal).ToImmutableArray(),
            layer);

    private static void ValidateSelectionContext(RunSelectionContext context)
    {
        if (context is null)
            throw new ProjectConfigException(
                "invalid_run_selection_context",
                "Run selection context must be present.",
                StatusCodes.Status400BadRequest);
        ValidateIdentifier(context.Revision, "context.revision");
        if (context.AvailableModelSelectionReferences is null ||
            context.ProviderRequirements.IsDefault ||
            context.RequiredEgress.IsDefault)
            throw new ProjectConfigException(
                "invalid_run_selection_context",
                "Run selection context collections and revision must be present.",
                StatusCodes.Status400BadRequest);
        foreach (var reference in context.AvailableModelSelectionReferences)
            ValidateIdentifier(reference, "context.availableModelSelectionReferences");
        if (context.ProviderRequirements.Any(requirement => requirement is null) ||
            context.ProviderRequirements.Select(item => (item.Seam, item.MeterSource)).Distinct().Count() !=
                context.ProviderRequirements.Length)
            throw new ProjectConfigException(
                "invalid_run_selection_context",
                "Run selection context contains null or duplicate provider requirements.",
                StatusCodes.Status400BadRequest);
        foreach (var requirement in context.ProviderRequirements)
        {
            if (requirement is null || !Enum.IsDefined(requirement.Seam) ||
                string.IsNullOrWhiteSpace(requirement.RequiredAdapterVersion) ||
                requirement.RequiredOptionsSchemaVersion < 1 ||
                requirement.RequiredCapabilities is null ||
                requirement.RequiredL3L4Capabilities is null ||
                requirement.RequiredL7Capabilities is null ||
                requirement.RequiredCapabilities.Any(string.IsNullOrWhiteSpace) ||
                requirement.RequiredL3L4Capabilities.Any(string.IsNullOrWhiteSpace) ||
                requirement.RequiredL7Capabilities.Any(string.IsNullOrWhiteSpace))
                throw new ProjectConfigException(
                    "invalid_run_selection_context",
                    "Run selection provider requirements are invalid.",
                    StatusCodes.Status400BadRequest);
            if (requirement.Seam == ProviderSeam.Cost)
            {
                if (requirement.MeterSource is null)
                    throw new ProjectConfigException(
                        "invalid_run_selection_context", "Cost requirements need an explicit meter source.",
                        StatusCodes.Status400BadRequest);
                ValidateIdentifier(requirement.MeterSource, "context.providerRequirements.meterSource");
            }
            else if (requirement.MeterSource is not null)
                throw new ProjectConfigException(
                    "invalid_run_selection_context", "Only Cost requirements accept a meter source.",
                    StatusCodes.Status400BadRequest);
            var layered = ProviderSeams.Cardinality(requirement.Seam) == ProviderCardinality.Layered;
            if (layered
                    ? requirement.RequiredCapabilities.Count != 0
                    : requirement.RequiredL3L4Capabilities.Count != 0 ||
                      requirement.RequiredL7Capabilities.Count != 0)
                throw new ProjectConfigException(
                    "invalid_run_selection_context",
                    $"Provider requirement for '{requirement.Seam}' uses capability fields that do not apply to its cardinality.",
                    StatusCodes.Status400BadRequest);
        }
        _ = ProjectConfigurationValidator.ValidateEgressRules(context.RequiredEgress);
    }

    private static EffectiveRunSelection ReadExistingSelection(
        ProjectRunSelectionRecord existing,
        string projectId,
        string fingerprint)
    {
        if (existing.ProjectId != projectId || existing.RequestFingerprint != fingerprint)
            throw ProjectConfigException.IdempotencyConflict();
        return DeserializeRunSelection(existing.SnapshotJson);
    }

    internal static EffectiveRunSelection DeserializeRunSelection(string value)
    {
        var selection = Deserialize<EffectiveRunSelection>(value);
        if (!selection.EgressBaseline.IsDefault &&
            !selection.RequiredEgress.IsDefault &&
            selection.ProjectEgressNarrowing is not { IsDefault: true })
            return selection;

        if (!selection.EgressBaseline.IsDefault ||
            !selection.RequiredEgress.IsDefault ||
            selection.ProjectEgressNarrowing is not null)
            throw new JsonException("Stored run selection has an incomplete egress snapshot.");

        var effective = selection.EgressAllowlist.IsDefault
            ? ImmutableArray<NetworkEgressRule>.Empty
            : NetworkEgressRuleSemantics.NormalizeSet(selection.EgressAllowlist, "legacy run selection");
        var legacyProjectConfiguration = selection.ProjectConfiguration with
        {
            EgressNarrowing = selection.ProjectConfiguration.EgressNarrowing is { } narrowing
                ? NetworkEgressRuleSemantics.NormalizeSet(narrowing, "legacy project egress narrowing")
                : null,
        };
        return selection with
        {
            EgressAllowlist = effective,
            EgressBaseline = effective,
            ProjectEgressNarrowing = effective,
            RequiredEgress = effective,
            ProjectConfiguration = legacyProjectConfiguration,
        };
    }

    private static string Fingerprint(string projectId, string runId, AcceptRunSelectionRequest request)
    {
        var requiredEgress = ProjectConfigurationValidator
            .ValidateEgressRules(request.Context.RequiredEgress);
        var modelReferences = request.Context.AvailableModelSelectionReferences
            .Order(StringComparer.Ordinal)
            .ToArray();
        var providerRequirements = request.Context.ProviderRequirements
            .OrderBy(item => item.Seam)
            .ThenBy(item => item.MeterSource, StringComparer.Ordinal)
            .Select(item => new ProviderRequirementFingerprint(
                item.Seam, item.RequiredAdapterVersion, item.RequiredOptionsSchemaVersion,
                item.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray(),
                item.RequiredL3L4Capabilities.Order(StringComparer.Ordinal).ToArray(),
                item.RequiredL7Capabilities.Order(StringComparer.Ordinal).ToArray(),
                item.MeterSource)).ToArray();

        if (requiredEgress.All(rule =>
            rule.Purpose == NetworkEgressPurpose.PublicHttps &&
            rule.DestinationKind == NetworkEgressDestinationKind.Fqdn))
        {
            var legacyCanonical = new
            {
                projectId,
                runId,
                request.ExpectedProjectConfigRevision,
                request.ExpectedPlatformRuntimeRevision,
                ContextRevision = request.Context.Revision,
                ModelReferences = modelReferences,
                ProviderRequirements = providerRequirements,
                RequiredEgress = requiredEgress
                    .Select(rule => new LegacyEgressFingerprint(rule.Destination, rule.Port, rule.Protocol))
                    .OrderBy(rule => rule.Host, StringComparer.Ordinal)
                    .ThenBy(rule => rule.Port)
                    .ThenBy(rule => rule.Protocol)
                    .ToArray(),
            };
            return HashFingerprint(legacyCanonical);
        }

        var canonical = new
        {
            projectId,
            runId,
            request.ExpectedProjectConfigRevision,
            request.ExpectedPlatformRuntimeRevision,
            ContextRevision = request.Context.Revision,
            ModelReferences = modelReferences,
            ProviderRequirements = providerRequirements,
            RequiredEgress = requiredEgress
                .OrderBy(rule => rule.Purpose)
                .ThenBy(rule => rule.DestinationKind)
                .ThenBy(rule => rule.Destination, StringComparer.Ordinal)
                .ThenBy(rule => rule.Port)
                .ThenBy(rule => rule.Protocol)
                .ToArray(),
        };
        return HashFingerprint(canonical);
    }

    private static string HashFingerprint<TCanonical>(TCanonical canonical)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record LegacyEgressFingerprint(string Host, int Port, EgressProtocol Protocol);

    private sealed record ProviderRequirementFingerprint(
        ProviderSeam Seam,
        string RequiredAdapterVersion,
        int RequiredOptionsSchemaVersion,
        string[] RequiredCapabilities,
        string[] RequiredL3L4Capabilities,
        string[] RequiredL7Capabilities,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MeterSource);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private async Task EnsurePlatformAdminAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
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

    private async Task<bool> HasAnyCurrentRoleAsync(
        ProjectAuthorizationContext caller,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        IReadOnlyCollection<ProjectAuthorityRole> roles,
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
                roles.Contains(assignment.Role) &&
                assignment.State == ProjectAuthorityRecordState.Active
            select assignment.AssignmentId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

    private enum ProjectAccess
    {
        Read,
        Write,
    }

    private static void ValidateProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 160 ||
            name.Any(char.IsControl))
            throw new ProjectConfigException(
                "invalid_project_name",
                "Project name must be nonempty, contain no control characters, and be at most 160 characters.",
                StatusCodes.Status400BadRequest);
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ProjectConfigException(
                "invalid_run_selection_context",
                $"'{name}' must be a nonempty opaque identifier of at most 256 ASCII characters.",
                StatusCodes.Status400BadRequest);
    }

    private static ProjectSummary ToSummary(ProjectRecord project) =>
        new(project.ProjectId, project.Name, project.State, project.Revision,
            project.ConfigurationRevision, project.CreatedAt, project.UpdatedAt);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidOperationException($"Stored Projects & Config JSON could not be read as {typeof(T).Name}.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new LegacyProjectEgressRuleConverter());
        return options;
    }

    private sealed class LegacyProjectEgressRuleConverter : JsonConverter<NetworkEgressRule>
    {
        private static readonly string[] CurrentFields =
            ["purpose", "destinationKind", "destination", "port", "protocol"];
        private static readonly string[] LegacyFields = ["host", "port", "protocol"];

        public override NetworkEgressRule Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("An egress rule must be an object.");

            var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.TryAdd(property.Name, property.Value))
                    throw new JsonException("An egress rule contains a duplicate property.");

            if (properties.ContainsKey("host"))
            {
                RequireExactFields(properties, LegacyFields);
                return new(
                    NetworkEgressPurpose.PublicHttps,
                    NetworkEgressDestinationKind.Fqdn,
                    ReadString(properties, "host"),
                    ReadInt32(properties, "port"),
                    ReadEnum<EgressProtocol>(properties, "protocol", options));
            }

            RequireExactFields(properties, CurrentFields);
            return new(
                ReadEnum<NetworkEgressPurpose>(properties, "purpose", options),
                ReadEnum<NetworkEgressDestinationKind>(properties, "destinationKind", options),
                ReadString(properties, "destination"),
                ReadInt32(properties, "port"),
                ReadEnum<EgressProtocol>(properties, "protocol", options));
        }

        public override void Write(
            Utf8JsonWriter writer,
            NetworkEgressRule value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("purpose");
            JsonSerializer.Serialize(writer, value.Purpose, options);
            writer.WritePropertyName("destinationKind");
            JsonSerializer.Serialize(writer, value.DestinationKind, options);
            writer.WriteString("destination", value.Destination);
            writer.WriteNumber("port", value.Port);
            writer.WritePropertyName("protocol");
            JsonSerializer.Serialize(writer, value.Protocol, options);
            writer.WriteEndObject();
        }

        private static void RequireExactFields(
            IReadOnlyDictionary<string, JsonElement> properties,
            IReadOnlyCollection<string> expected)
        {
            if (properties.Count != expected.Count ||
                expected.Any(field => !properties.ContainsKey(field)))
                throw new JsonException("An egress rule does not match a supported contract.");
        }

        private static string ReadString(
            IReadOnlyDictionary<string, JsonElement> properties,
            string name) =>
            properties[name].ValueKind == JsonValueKind.String
                ? properties[name].GetString() ?? throw new JsonException($"Egress field '{name}' is null.")
                : throw new JsonException($"Egress field '{name}' must be a string.");

        private static int ReadInt32(
            IReadOnlyDictionary<string, JsonElement> properties,
            string name) =>
            properties[name].ValueKind == JsonValueKind.Number &&
            properties[name].TryGetInt32(out var value)
                ? value
                : throw new JsonException($"Egress field '{name}' must be an integer.");

        private static T ReadEnum<T>(
            IReadOnlyDictionary<string, JsonElement> properties,
            string name,
            JsonSerializerOptions options) where T : struct, Enum =>
            JsonSerializer.Deserialize<T>(properties[name], options);
    }
}
