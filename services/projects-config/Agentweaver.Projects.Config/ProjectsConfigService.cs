using System.Collections.Immutable;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentGovernance.Security;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Agentweaver.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Projects.Config;

/// <summary>Project metadata and current configuration revision returned to authorized callers.</summary>
public sealed record ProjectSummary(
    string ProjectId,
    string Name,
    ProjectLifecycleState State,
    long Revision,
    long ConfigurationRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Current roles for the caller on this project or its tenant, for display only.</summary>
    public IReadOnlyList<EffectiveProjectRole> EffectiveRoles { get; init; } = [];
}

/// <summary>A current caller role and the exact resource scope that owns it.</summary>
public sealed record EffectiveProjectRole(
    ProjectAuthorityResourceType ResourceType,
    string ResourceId,
    ProjectAuthorityRole Role);

/// <summary>One immutable project configuration revision with its author and creation time.</summary>
public sealed record VersionedProjectConfiguration(
    string ProjectId,
    long Revision,
    ProjectConfiguration Configuration,
    string UpdatedByActorId,
    DateTimeOffset CreatedAt);

/// <summary>One committed project configuration revision with an optional post-commit charter scan.</summary>
public sealed record UpdateProjectConfigurationResponse(
    string ProjectId,
    long Revision,
    ProjectConfiguration Configuration,
    string UpdatedByActorId,
    DateTimeOffset CreatedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectCharterScanResultV1? CharterScan { get; init; }
}

/// <summary>Read-only eligibility choices for model references and platform-selected provider components.</summary>
public sealed record ProjectConfigurationChoices(
    string ProjectId,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    ProjectModelChoices Models,
    ProjectProviderChoices Providers,
    ImmutableArray<string> EffectiveFallbackReferences,
    ImmutableArray<ProjectAvailablePlatformProvider> AvailablePlatformProviders);

/// <summary>Read-only projection of one platform BYOK provider in Available state.</summary>
public sealed record ProjectAvailablePlatformProvider(
    string Id,
    string Name,
    string Type,
    string Model,
    ImmutableArray<string> ReasoningEfforts,
    bool IsActive,
    string? Reference);

/// <summary>
/// Model references and reasoning choices that may be selected for future runs. <see cref="Choices"/> lists only
/// enabled references from the deployment's model-binding copy; it is empty with an explicit unavailable state when
/// no copy is configured. <see cref="SavedReferences"/> are references already saved in platform or project
/// configuration and are never eligible solely because they are saved.
/// </summary>
public sealed record ProjectModelChoices(
    string State,
    string? UnavailableReason,
    string RuntimeEligibility,
    string? BindingsRevision,
    string DefaultReasoningEffort,
    ImmutableArray<ProjectModelChoice> Choices,
    ImmutableArray<ProjectModelReferenceChoice> SavedReferences);

/// <summary>
/// One enabled model reference. BYOK references list their configured reasoning efforts
/// (<c>reasoningEffortSource</c> <c>modelBinding</c>); hosted Copilot references list levels previously verified
/// by AgentHost when available, and otherwise retain the run-start verification fallback.
/// </summary>
public sealed record ProjectModelChoice(
    string Reference,
    string SourceMode,
    string ReasoningEffortSource,
    ImmutableArray<string> ReasoningEfforts);

/// <summary>A model reference and the owner-held configuration sources that contain it.</summary>
public sealed record ProjectModelReferenceChoice(
    string Reference,
    ImmutableArray<string> Sources);

/// <summary>Platform provider selections and project overrides that are retained but ignored.</summary>
public sealed record ProjectProviderChoices(
    bool ProjectOverridesSupported,
    ImmutableArray<ProjectProviderSeamChoice> Seams,
    ImmutableArray<string> IgnoredProjectOverrides);

/// <summary>Selected provider IDs for one platform seam, including explicit upstream availability.</summary>
public sealed record ProjectProviderSeamChoice(
    string Seam,
    string Cardinality,
    string State,
    string? UnavailableReason,
    ImmutableArray<string> ProviderIds);

/// <summary>Current platform runtime defaults, or an empty revision-zero head before the first configuration.</summary>
public sealed record VersionedPlatformRuntimeDefaults(
    long Revision,
    PlatformRuntimeDefaults? Defaults,
    string? UpdatedByActorId,
    DateTimeOffset? CreatedAt);

/// <summary>Saved platform model-switch result.</summary>
public sealed record PlatformModelDefaultSwitchResult(
    string Mode,
    string? ActiveProviderId,
    long SettingsRevision);

/// <summary>Effective run configuration resolved from immutable project, platform, and owner-provided context.</summary>
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
    ImmutableArray<NetworkEgressRule> RequiredEgress)
{
    /// <summary>Resolved settings for every model reference available to this run.</summary>
    public ImmutableArray<EffectiveModelSelection> ModelSelections { get; init; } = [];

    /// <summary>Default model reference for each task, role, and agent combination.</summary>
    public ImmutableArray<ModelTaskSelectionReference> ModelTaskSelections { get; init; } = [];
}

/// <summary>
/// Owns project configuration revisions and the immutable run-selection snapshot stored in the Projects schema.
/// </summary>
public sealed partial class ProjectsConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly ProjectsConfigDbContext db;
    private readonly ProviderCatalog providerCatalog;
    private readonly TimeProvider timeProvider;
    private readonly IDbContextFactory<ProjectsConfigDbContext>? dbContextFactory;
    private readonly ModelBindingCapabilities modelBindings;
    private readonly SkillContentObjectStore? skillContentObjects;
    private readonly Agentweaver.Policy.Acs.NativeAcsPolicyEvaluator? skillPolicyEvaluator;

    /// <summary>
    /// Creates the service over the request context, provider catalog, clock, optional transaction factory, and
    /// optional model-binding copy and skill content store. Without a model-binding copy, model choices are unavailable.
    /// </summary>
    public ProjectsConfigService(
        ProjectsConfigDbContext db,
        ProviderCatalog providerCatalog,
        TimeProvider timeProvider,
        IDbContextFactory<ProjectsConfigDbContext>? dbContextFactory = null,
        ModelBindingCapabilities? modelBindings = null,
        SkillContentObjectStore? skillContentObjects = null,
        Agentweaver.Policy.Acs.NativeAcsPolicyEvaluator? skillPolicyEvaluator = null)
    {
        this.db = db;
        this.providerCatalog = providerCatalog;
        this.timeProvider = timeProvider;
        this.dbContextFactory = dbContextFactory;
        this.modelBindings = modelBindings ?? ModelBindingCapabilities.Unavailable;
        this.skillContentObjects = skillContentObjects;
        this.skillPolicyEvaluator = skillPolicyEvaluator;
    }

    /// <summary>
    /// Reads current-tenant project model references and platform provider selections for display and selection.
    /// These choices describe eligibility only: they provision nothing and never alter accepted immutable run pins.
    /// </summary>
    public async Task<ProjectConfigurationChoices> GetProjectConfigurationChoicesAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        var project = await FindProjectAsync(
            caller, projectId, ProjectAccess.Read, cancellationToken).ConfigureAwait(false);
        var configurationRevision = await db.ProjectConfigurationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == project.ProjectId && item.Revision == project.ConfigurationRevision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        var configuration = Deserialize<ProjectConfiguration>(configurationRevision.ConfigurationJson);
        var platformHead = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        var platform = await ReadPlatformDefaultsAsync(platformHead, cancellationToken).ConfigureAwait(false);
        var connectionId = configuration.ModelSelection is { } projectSelection
            ? projectSelection.ConnectionId
            : platform.Defaults?.ModelSelection?.ConnectionId;
        IReadOnlyDictionary<string, ImmutableArray<string>>? hostedReasoningEfforts = null;
        // A publication is visible only in its original actor/project scope and for the currently selected binding.
        if (connectionId is { } selectedConnection && modelBindings.Revision is { } bindingsRevision)
        {
            var catalogCutoff = timeProvider.GetUtcNow() - HostedReasoningCatalogMaxAge;
            var publications = await db.HostedModelReasoningCatalog.AsNoTracking()
                .Where(catalog =>
                    catalog.TenantId == caller.TenantId &&
                    catalog.ProjectId == project.ProjectId &&
                    catalog.ActorId == caller.ActorId &&
                    catalog.ConnectionId == selectedConnection &&
                    catalog.BindingsRevision == bindingsRevision &&
                    catalog.VerifiedAt >= catalogCutoff)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            hostedReasoningEfforts = publications.ToDictionary(
                catalog => catalog.Reference,
                catalog => catalog.SupportedReasoningEfforts.ToImmutableArray(),
                StringComparer.Ordinal);
        }
        var modelChoices = BuildModelChoices(configuration, platform, modelBindings, hostedReasoningEfforts);
        var activePlatformProviderId =
            platform.Defaults?.ModelDefaultSwitch.Mode == PlatformModelDefaultSwitchMode.Active
                ? platform.Defaults.ModelDefaultSwitch.ActiveProviderId
                : null;
        var availablePlatformProviders = (await (
                from head in db.ModelProviderDrafts.AsNoTracking()
                join reservation in db.ModelProviderReferenceReservations.AsNoTracking()
                    on head.ProviderId equals reservation.ProviderId into reservations
                from reservation in reservations.DefaultIfEmpty()
                where head.Scope == ModelProviderBindingScope.Platform &&
                    head.ProjectId == null &&
                    head.State == ModelProviderDraftState.Available
                orderby head.ProviderId
                select new
                {
                    head.ProviderId,
                    head.DisplayName,
                    head.ProviderType,
                    head.Deployment,
                    head.ReasoningEfforts,
                    Reference = reservation == null ? null : reservation.Reference,
                })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(item => new ProjectAvailablePlatformProvider(
                item.ProviderId,
                item.DisplayName,
                item.ProviderType,
                item.Deployment,
                [.. item.ReasoningEfforts],
                string.Equals(item.ProviderId, activePlatformProviderId, StringComparison.Ordinal),
                item.Reference))
            .ToImmutableArray();

        return new ProjectConfigurationChoices(
            project.ProjectId,
            configurationRevision.Revision,
            platform.Revision,
            modelChoices,
            BuildProviderChoices(configuration),
            BuildEffectiveFallbackReferences(configuration, platform.Defaults),
            availablePlatformProviders);
    }

    /// <summary>Loads a project after checking current read or write authority for marketplace operations.</summary>
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

    /// <summary>
    /// Serializes a source write with membership, project, and role locks, then rechecks active project authority before commit.
    /// </summary>
    /// <exception cref="MarketplaceSourceException">A serialization race becomes a source revision conflict.</exception>
    internal async Task<T> ExecuteMarketplaceWriteAsync<T>(
        ProjectAuthorizationContext caller,
        string projectId,
        Func<ProjectRecord, Task<T>> writeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireResourceBinding(projectId);

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await LockMarketplaceWriteInputsAsync(caller, projectId, cancellationToken)
                .ConfigureAwait(false);
            var project = await FindProjectAsync(
                    caller, projectId, ProjectAccess.Write, cancellationToken)
                .ConfigureAwait(false);
            if (project.State != ProjectLifecycleState.Active)
                throw ProjectConfigException.Conflict("Archived projects cannot be reconfigured.");

            var result = await writeAsync(project).ConfigureAwait(false);

            project = await FindProjectAsync(
                    caller, projectId, ProjectAccess.Write, cancellationToken)
                .ConfigureAwait(false);
            if (project.State != ProjectLifecycleState.Active)
                throw ProjectConfigException.Conflict("Archived projects cannot be reconfigured.");

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            db.ChangeTracker.Clear();
            _ = await GetMarketplaceProjectAsync(
                    caller, projectId, requireWrite: true, cancellationToken)
                .ConfigureAwait(false);
            throw MarketplaceSourceException.RevisionConflict();
        }
    }

    /// <summary>Locks membership, project, and eligible role rows so a concurrent revoke cannot cross the source write.</summary>
    private async Task LockMarketplaceWriteInputsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        await using (var membershipLock = new NpgsqlCommand(
            """
            SELECT membership_id
            FROM projects_config.tenant_memberships
            WHERE membership_id = @membership_id
            FOR SHARE
            """,
            connection,
            transaction))
        {
            membershipLock.Parameters.AddWithValue("membership_id", caller.MembershipId);
            await membershipLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

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
            await projectLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var roleLock = new NpgsqlCommand(
            """
            SELECT assignment_id
            FROM projects_config.project_role_assignments
            WHERE membership_id = @membership_id
              AND state = 'Active'
              AND (
                (resource_type = 'Tenant' AND resource_id = @tenant_id AND role = 'TenantAdmin')
                OR
                (resource_type = 'Project' AND resource_id = @project_id AND role = 'Owner')
              )
            FOR SHARE
            """,
            connection,
            transaction);
        roleLock.Parameters.AddWithValue("membership_id", caller.MembershipId);
        roleLock.Parameters.AddWithValue("tenant_id", caller.TenantId);
        roleLock.Parameters.AddWithValue("project_id", projectId);
        await using var roleReader = await roleLock.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await roleReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    /// <summary>Creates a project, its initial configuration revision, and the creator's active Owner assignment atomically.</summary>
    /// <exception cref="ProjectConfigException">The caller lacks current scope/role, the project name is invalid, or role-write grants are unavailable.</exception>
    public async Task<ProjectSummary> CreateProjectAsync(
        ProjectAuthorizationContext caller,
        string name,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        var adminGrant = caller.Grants.FirstOrDefault(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            string.Equals(grant.ResourceId, caller.TenantId, StringComparison.Ordinal) &&
            grant.Role == ProjectAuthorityRole.TenantAdmin);
        if (adminGrant is null || !await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Tenant,
            caller.TenantId,
            ProjectAuthorityRole.TenantAdmin,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        name = NormalizeProjectName(name);
        var now = timeProvider.GetUtcNow();
        var projectId = Guid.NewGuid().ToString("N");
        var project = new ProjectRecord
        {
            ProjectId = projectId,
            TenantId = caller.TenantId,
            CreatedByActorId = caller.ActorId,
            Name = name,
            State = ProjectLifecycleState.Active,
            Revision = 1,
            ConfigurationRevision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var configuration = new ProjectConfiguration();
        await using (var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _ = await new SkillAdmissionService(db, timeProvider)
                    .LockFenceAsync(cancellationToken).ConfigureAwait(false);
                await ProjectRoleAssignmentService.LockCurrentTenantAdminAsync(
                    db, caller, adminGrant, projectId, cancellationToken).ConfigureAwait(false);
                var membership = await db.TenantMemberships.AsNoTracking()
                    .SingleOrDefaultAsync(item =>
                        item.MembershipId == caller.MembershipId &&
                        item.Issuer == caller.Issuer &&
                        item.Subject == caller.ActorId &&
                        item.TenantId == caller.TenantId &&
                        item.State == ProjectAuthorityRecordState.Active &&
                        item.Revision == caller.MembershipRevision,
                        cancellationToken).ConfigureAwait(false)
                    ?? throw ProjectConfigException.Forbidden();
                var owner = new ProjectRoleAssignmentRecord
                {
                    AssignmentId = Guid.NewGuid(),
                    MembershipId = membership.MembershipId,
                    ResourceType = ProjectAuthorityResourceType.Project,
                    ResourceId = projectId,
                    Role = ProjectAuthorityRole.Owner,
                    State = ProjectAuthorityRecordState.Active,
                    Revision = 1,
                    GrantedBy = caller.ActorId,
                    GrantedAt = now,
                };
                db.Projects.Add(project);
                db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
                {
                    ProjectId = projectId,
                    Revision = 1,
                    ConfigurationJson = Serialize(configuration),
                    UpdatedByActorId = caller.ActorId,
                    CreatedAt = now,
                });
                db.RoleAssignments.Add(owner);
                db.AuthorityAudit.Add(ProjectAuthorityRules.CreateRoleAudit(
                    membership,
                    owner,
                    "role_assigned",
                    caller.ActorId,
                    now,
                    ProjectAuthorityRules.P1SetupInvocationId(name)));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ProjectAuthorityRules.IsPermissionDenied(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw new ProjectConfigException(
                    "role_assignment_unavailable",
                    "Project role assignment is not enabled for this deployment.",
                    StatusCodes.Status503ServiceUnavailable);
            }
        }
        return await ToSummaryAsync(project, caller, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists projects in the active tenant, filtering non-admin results to projects with a current read role.</summary>
    public async Task<ImmutableArray<ProjectSummary>> ListProjectsAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        var projects = await ListReadableProjectsAsync(caller, cancellationToken).ConfigureAwait(false);
        var visible = ImmutableArray.CreateBuilder<ProjectSummary>(projects.Length);
        foreach (var project in projects)
            visible.Add(await ToSummaryAsync(project, caller, cancellationToken).ConfigureAwait(false));
        return visible.ToImmutable();
    }

    internal async Task EnsureProjectPolicyReaderAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        _ = await FindProjectAsync(caller, projectId, ProjectAccess.Read, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task EnsureProjectPolicyAuthorAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireUnboundRequest();
        var project = await FindProjectAsync(caller, projectId, ProjectAccess.Read, cancellationToken)
            .ConfigureAwait(false);
        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.NotFound();
        var grants = NativeAcsPolicyStore.EnsureWriteAuthorized(
            caller, NativeAcsPolicyScopeType.Project, projectId);
        var ids = grants.Select(grant => grant.AssignmentId).ToArray();
        var current = await (
                from assignment in db.RoleAssignments.AsNoTracking()
                join membership in db.TenantMemberships.AsNoTracking()
                    on assignment.MembershipId equals membership.MembershipId
                where membership.MembershipId == caller.MembershipId &&
                    membership.Issuer == caller.Issuer &&
                    membership.Subject == caller.ActorId &&
                    membership.TenantId == caller.TenantId &&
                    membership.Revision == caller.MembershipRevision &&
                    membership.State == ProjectAuthorityRecordState.Active &&
                    assignment.State == ProjectAuthorityRecordState.Active &&
                    ids.Contains(assignment.AssignmentId) &&
                    db.Projects.AsNoTracking().Any(project =>
                        project.ProjectId == projectId && project.TenantId == caller.TenantId)
                select new
                {
                    assignment.AssignmentId,
                    assignment.Revision,
                    assignment.ResourceType,
                    assignment.ResourceId,
                    assignment.Role
                })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!grants.Any(grant => current.Any(assignment =>
                assignment.AssignmentId == grant.AssignmentId &&
                assignment.Revision == grant.Revision &&
                assignment.ResourceType == grant.ResourceType &&
                assignment.ResourceId == grant.ResourceId &&
                assignment.Role == grant.Role)))
            throw ProjectConfigException.Forbidden();
    }

    private async Task<ImmutableArray<ProjectRecord>> ListReadableProjectsAsync(
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
        var visible = ImmutableArray.CreateBuilder<ProjectRecord>();
        foreach (var project in projects)
        {
            // Tenant admins see every project in their selected tenant; other callers need a current project read role.
            if (!isTenantAdmin && !await HasAnyCurrentRoleAsync(
                    caller,
                    ProjectAuthorityResourceType.Project,
                    project.ProjectId,
                    [
                        ProjectAuthorityRole.Owner,
                        ProjectAuthorityRole.Contributor,
                        ProjectAuthorityRole.Viewer,
                    ],
                    cancellationToken).ConfigureAwait(false))
                continue;

            visible.Add(project);
        }
        return visible.ToImmutable();
    }

    /// <summary>Returns an authorized project summary without a run binding.</summary>
    public async Task<ProjectSummary> GetProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        CancellationToken cancellationToken)
        => await GetProjectAsync(caller, projectId, runId: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns an authorized project summary and, when supplied, requires the exact signed run binding.</summary>
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
        return await ToSummaryAsync(project, caller, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates project metadata at an expected revision and advances the mutable project head.</summary>
    /// <exception cref="ProjectConfigException">The caller lacks write authority, state is invalid, or the revision is stale.</exception>
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
        return await ToSummaryAsync(project, caller, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the current or a retained immutable configuration revision for an authorized project.</summary>
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

    /// <summary>
    /// Validates and appends a project configuration revision after checking skill pins and platform narrowing.
    /// </summary>
    /// <exception cref="ProjectConfigException">The project is archived, the expected revision is stale, or configuration is invalid.</exception>
    public async Task<VersionedProjectConfiguration> UpdateProjectConfigurationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedRevision,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var updated = await UpdateProjectConfigurationAsync(
            caller,
            projectId,
            expectedRevision,
            configuration,
            scanCharterAgentId: null,
            evaluateCharter: null,
            cancellationToken).ConfigureAwait(false);
        return new VersionedProjectConfiguration(
            updated.ProjectId,
            updated.Revision,
            updated.Configuration,
            updated.UpdatedByActorId,
            updated.CreatedAt);
    }

    /// <summary>
    /// Validates and appends a project configuration revision, then runs an optional local scan of one selected charter.
    /// </summary>
    /// <exception cref="ProjectConfigException">The project is archived, the expected revision is stale, the configuration is invalid, or the charter selector does not match.</exception>
    public Task<UpdateProjectConfigurationResponse> UpdateProjectConfigurationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedRevision,
        ProjectConfiguration configuration,
        string? scanCharterAgentId,
        CancellationToken cancellationToken) =>
        UpdateProjectConfigurationAsync(
            caller,
            projectId,
            expectedRevision,
            configuration,
            scanCharterAgentId,
            evaluateCharter: null,
            cancellationToken);

    internal async Task<UpdateProjectConfigurationResponse> UpdateProjectConfigurationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedRevision,
        ProjectConfiguration configuration,
        string? scanCharterAgentId,
        Func<string, PromptDefenseReport>? evaluateCharter,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        _ = await FindProjectAsync(caller, projectId, ProjectAccess.Write, cancellationToken)
            .ConfigureAwait(false);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        string committedProjectId;
        long nextRevision;
        string serializedConfiguration;
        ProjectConfiguration normalized;
        DateTimeOffset now;
        ProjectAgentCharter? selectedCharter = null;
        try
        {
            _ = await new SkillAdmissionService(db, timeProvider)
                .LockFenceAsync(cancellationToken).ConfigureAwait(false);
            var project = await LockCastingProjectAndAuthorityAsync(
                db, caller, projectId, write: true, cancellationToken).ConfigureAwait(false);
            if (project.State != ProjectLifecycleState.Active)
                throw ProjectConfigException.Conflict("Archived projects cannot be reconfigured.");
            if (project.ConfigurationRevision != expectedRevision)
                throw ProjectConfigException.Conflict("The project configuration revision has changed.");
            var currentRevision = await db.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.ProjectId == project.ProjectId &&
                        record.Revision == project.ConfigurationRevision,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var previous = Deserialize<ProjectConfiguration>(currentRevision.ConfigurationJson);
            ArgumentNullException.ThrowIfNull(configuration);
            RejectSubmittedProviderOverrides(configuration);
            configuration = KeepStoredProviderOverrides(previous, configuration);
            normalized = await SkillContentService.ValidateConfigurationAsync(
                db,
                project.ProjectId,
                previous,
                configuration,
                skillContentObjects,
                timeProvider,
                skillPolicyEvaluator,
                cancellationToken).ConfigureAwait(false);
            if (normalized.ReviewedRemoteToolSnapshots is { } reviewedSnapshots &&
                reviewedSnapshots.Any(snapshot => snapshot.ProjectId != project.ProjectId))
                throw new ProjectConfigException(
                    "invalid_configuration",
                    "Reviewed remote tool snapshot references must belong to this project.",
                    (int)HttpStatusCode.BadRequest);

            await EnsureProjectNarrowingAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (scanCharterAgentId is not null)
                selectedCharter = SelectCharterForScan(normalized, scanCharterAgentId);

            now = timeProvider.GetUtcNow();
            nextRevision = checked(project.ConfigurationRevision + 1);
            committedProjectId = project.ProjectId;
            serializedConfiguration = Serialize(normalized);
            db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
            {
                ProjectId = committedProjectId,
                Revision = nextRevision,
                ConfigurationJson = serializedConfiguration,
                UpdatedByActorId = caller.ActorId,
                CreatedAt = now,
            });
            project.ConfigurationRevision = nextRevision;
            project.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ProjectConfigException.Conflict("The project configuration revision has changed.");
        }

        var charterScan = selectedCharter is null
            ? null
            : ScanCommittedCharter(
                committedProjectId,
                nextRevision,
                serializedConfiguration,
                selectedCharter,
                evaluateCharter);
        return new UpdateProjectConfigurationResponse(
            committedProjectId, nextRevision, normalized, caller.ActorId, now)
        {
            CharterScan = charterScan
        };
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static ProjectAgentCharter SelectCharterForScan(
        ProjectConfiguration configuration,
        string scanCharterAgentId)
    {
        if (!string.IsNullOrWhiteSpace(scanCharterAgentId))
        {
            foreach (var charter in configuration.AgentCharters)
            {
                if (string.Equals(charter.AgentId, scanCharterAgentId, StringComparison.Ordinal))
                    return charter;
            }
        }

        throw new ProjectConfigException(
            "invalid_charter_selection",
            "The requested agent charter was not found in the submitted configuration.",
            (int)HttpStatusCode.BadRequest);
    }

    internal static ProjectCharterScanResultV1 ScanCommittedCharter(
        string projectId,
        long committedRevision,
        string serializedConfiguration,
        ProjectAgentCharter selectedCharter,
        Func<string, PromptDefenseReport>? evaluateCharter = null)
    {
        try
        {
            var savedConfiguration = Deserialize<ProjectConfiguration>(serializedConfiguration);
            var savedCharter = savedConfiguration.AgentCharters.FirstOrDefault(item =>
                string.Equals(item.AgentId, selectedCharter.AgentId, StringComparison.Ordinal));
            if (savedCharter is null ||
                !string.Equals(savedCharter.Charter, selectedCharter.Charter, StringComparison.Ordinal))
            {
                return ProjectCharterScanResultV1.Unavailable(selectedCharter.AgentId);
            }

            var agentIdBytes = StrictUtf8.GetBytes(savedCharter.AgentId);
            var logicalPath = $"agent-charters/by-id-sha256/{PromptContentScanContract.ComputeInputSha256(agentIdBytes)}/charter";
            var source = new PromptContentScanSourceV1(
                PromptContentScanOwnerKind.ProjectConfiguration,
                projectId,
                committedRevision,
                logicalPath,
                PromptContentScanProvenance.Authored,
                SourceRevision: null,
                ResolvedCommitSha: null);
            var charterBytes = StrictUtf8.GetBytes(savedCharter.Charter);
            var report = evaluateCharter is null
                ? PromptContentScanner.Scan(PromptContentScanInputKind.ProjectCharter, source, charterBytes)
                : PromptContentScanner.Scan(PromptContentScanInputKind.ProjectCharter, source, charterBytes, evaluateCharter);
            var validated = PromptContentScanContract.ValidateReport(
                report, PromptContentScanInputKind.ProjectCharter, source, charterBytes);
            return validated.Status == PromptContentScanStatus.Completed
                ? ProjectCharterScanResultV1.Completed(selectedCharter.AgentId, validated)
                : ProjectCharterScanResultV1.Unavailable(selectedCharter.AgentId);
        }
        catch
        {
            return ProjectCharterScanResultV1.Unavailable(selectedCharter.AgentId);
        }
    }

    /// <summary>Returns the current platform defaults to a caller with a current platform-admin role.</summary>
    public async Task<VersionedPlatformRuntimeDefaults> GetPlatformRuntimeDefaultsAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        var head = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        return await ReadPlatformDefaultsAsync(head, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates and appends platform runtime defaults only when the supplied revision is current.</summary>
    /// <exception cref="ProjectConfigException">The caller is not a platform administrator or the head revision changed.</exception>
    public async Task<VersionedPlatformRuntimeDefaults> UpdatePlatformRuntimeDefaultsAsync(
        ProjectAuthorizationContext caller,
        long expectedRevision,
        PlatformRuntimeDefaults defaults,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        var head = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        if (head.CurrentRevision != expectedRevision)
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
        var current = expectedRevision == 0
            ? null
            : await ReadPlatformDefaultsAsync(head, cancellationToken).ConfigureAwait(false);
        var normalized = ProjectConfigurationValidator.Validate(
            PreservePlatformModelSwitch(current?.Defaults, defaults));

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

    /// <summary>Switches installation model fallbacks to one exact Available platform provider revision.</summary>
    public async Task<PlatformModelDefaultSwitchResult> ActivatePlatformModelProviderAsync(
        ProjectAuthorizationContext caller,
        string providerId,
        long expectedSettingsRevision,
        long expectedProviderRevision,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        if (expectedSettingsRevision < 1 || expectedProviderRevision < 1)
            throw ProjectConfigException.Invalid(
                "invalid_expected_revision", "Activation requires initialized positive revisions.");
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await LockPlatformAdminAuthorityAsync(db, caller, cancellationToken).ConfigureAwait(false);
            var head = await LockPlatformHeadForSwitchAsync(db, cancellationToken).ConfigureAwait(false);
            if (head.CurrentRevision != expectedSettingsRevision)
                throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
            if (!await HasCurrentRoleAsync(
                db, caller, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin, cancellationToken).ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();

            var provider = await LockAndReadPlatformProviderAsync(
                db, providerId, cancellationToken).ConfigureAwait(false);
            if (provider is null || provider.Head.State != ModelProviderDraftState.Available ||
                provider.Head.CurrentRevision != expectedProviderRevision)
                throw ProjectConfigException.Conflict("The provider revision changed or is unavailable.");
            var binding = ModelProviderDraftService.ToBinding(provider.Revision);
            var switched = ApplyPlatformProviderSwitch(
                await ReadPlatformDefaultsRevisionAsync(
                    db, head, cancellationToken).ConfigureAwait(false),
                binding);
            var next = await AppendPlatformDefaultsAsync(
                db, head, caller.ActorId, switched, cancellationToken).ConfigureAwait(false);
            if (!await HasCurrentRoleAsync(
                db, caller, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin, cancellationToken).ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformModelDefaultSwitchResult(
                ToSwitchMode(PlatformModelDefaultSwitchMode.Active), providerId, next);
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
        }
    }

    /// <summary>Returns new-run installation fallbacks to the shipped hosted Copilot choices.</summary>
    public async Task<PlatformModelDefaultSwitchResult> DeactivatePlatformModelProviderAsync(
        ProjectAuthorizationContext caller,
        long expectedSettingsRevision,
        CancellationToken cancellationToken)
    {
        await EnsurePlatformAdminAsync(caller, cancellationToken).ConfigureAwait(false);
        if (expectedSettingsRevision < 1)
            throw ProjectConfigException.Invalid(
                "invalid_expected_revision", "Deactivation requires initialized positive settings.");
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await LockPlatformAdminAuthorityAsync(db, caller, cancellationToken).ConfigureAwait(false);
            var head = await LockPlatformHeadForSwitchAsync(db, cancellationToken).ConfigureAwait(false);
            if (head.CurrentRevision != expectedSettingsRevision)
                throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
            if (!await HasCurrentRoleAsync(
                db, caller, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin, cancellationToken).ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();
            var defaults = await ReadPlatformDefaultsRevisionAsync(db, head, cancellationToken)
                .ConfigureAwait(false);
            var hosted = RestoreHostedPlatformFallbacks(defaults);
            var next = await AppendPlatformDefaultsAsync(
                db, head, caller.ActorId, hosted, cancellationToken).ConfigureAwait(false);
            if (!await HasCurrentRoleAsync(
                db, caller, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin, cancellationToken).ConfigureAwait(false))
                throw ProjectConfigException.Forbidden();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformModelDefaultSwitchResult(
                ToSwitchMode(PlatformModelDefaultSwitchMode.ExplicitHosted), null, next);
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
        }
    }

    /// <summary>
    /// Resolves and persists an immutable selection once per run ID; exact retries replay, while changed requests conflict.
    /// </summary>
    /// <exception cref="ProjectConfigException">Authority, project state, expected revisions, or selection requirements are invalid.</exception>
    public async Task<EffectiveRunSelection> AcceptRunSelectionAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        AcceptRunSelectionRequest request,
        CancellationToken cancellationToken) =>
        await AcceptRunSelectionCoreAsync(caller, projectId, runId, request, false, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Accepts an interactive selection using owner-held platform revisions and configured provider choices.</summary>
    public Task<EffectiveRunSelection> AcceptInteractiveRunSelectionAsync(
        ProjectAuthorizationContext caller, string projectId, string runId,
        InteractiveRunSelectionRequest request, CancellationToken cancellationToken) =>
        AcceptRunSelectionCoreAsync(caller, projectId, runId, new AcceptRunSelectionRequest
        {
            ExpectedProjectConfigRevision = request.ExpectedProjectConfigRevision,
            ExpectedPlatformRuntimeRevision = 0,
            Context = new RunSelectionContext { Revision = "interactive-v1" }
        }, true, cancellationToken);

    private const int MaxRunSelectionAttempts = 3;

    /// <summary>Shares the same authority locks, replay checks and selection transaction for both admission contracts.</summary>
    private async Task<EffectiveRunSelection> AcceptRunSelectionCoreAsync(
        ProjectAuthorizationContext caller, string projectId, string runId,
        AcceptRunSelectionRequest request, bool interactive, CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireResourceBinding(projectId, runId);
        ValidateIdentifier(runId, "runId");
        ArgumentNullException.ThrowIfNull(request);
        ValidateSelectionContext(request.Context);
        var originalRequest = request;
        _ = await FindTenantProjectAsync(caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        db.ChangeTracker.Clear();

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (dbContextFactory is null)
                    return await AcceptRunSelectionAttemptAsync(
                        db, caller, projectId, runId, originalRequest, interactive, cancellationToken)
                        .ConfigureAwait(false);

                await using var attemptDb = await dbContextFactory.CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await AcceptRunSelectionAttemptAsync(
                    attemptDb, caller, projectId, runId, originalRequest, interactive, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                exception is not OperationCanceledException &&
                (IsSerializationFailure(exception) || IsRunSelectionPrimaryKeyViolation(exception)))
            {
                if (dbContextFactory is null || attempt + 1 >= MaxRunSelectionAttempts)
                    throw ProjectConfigException.Conflict(
                        "The run selection conflicted with a concurrent update. Retry the request.");
            }
        }
    }

    private async Task<EffectiveRunSelection> AcceptRunSelectionAttemptAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        AcceptRunSelectionRequest originalRequest,
        bool interactive,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        _ = await new SkillAdmissionService(context, timeProvider)
            .LockFenceAsync(cancellationToken).ConfigureAwait(false);
        // Lock mutable authority and revision heads before computing the snapshot, and serialize all writers for this run ID.
        await LockRunSelectionInputsAsync(context, caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (!await HasCurrentRoleAsync(
            context,
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        var project = await FindTenantProjectAsync(context, caller, projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        var fingerprint = Fingerprint(project.ProjectId, runId, originalRequest);
        var existing = await context.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(selection => selection.RunId == runId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // The fingerprint makes an identical request replay-safe without reopening mutable configuration inputs.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ReadExistingSelection(existing, project.ProjectId, fingerprint);
        }

        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot start runs.");

        if (project.ConfigurationRevision != originalRequest.ExpectedProjectConfigRevision)
            throw ProjectConfigException.Conflict("The project configuration revision has changed.");

        var projectVersion = await context.ProjectConfigurationRevisions.AsNoTracking()
            .SingleAsync(item => item.ProjectId == project.ProjectId &&
                item.Revision == project.ConfigurationRevision, cancellationToken)
            .ConfigureAwait(false);
        var projectConfiguration = ProjectConfigurationValidator.Validate(
            Deserialize<ProjectConfiguration>(projectVersion.ConfigurationJson));
        await SkillContentService.ValidateConfigurationAsync(
            context,
            project.ProjectId,
            projectConfiguration,
            projectConfiguration,
            skillContentObjects,
            timeProvider,
            skillPolicyEvaluator,
            cancellationToken)
            .ConfigureAwait(false);

        var platformHead = await GetPlatformHeadAsync(context, cancellationToken).ConfigureAwait(false);
        if (platformHead.CurrentRevision == 0)
            throw new ProjectConfigException(
                "platform_runtime_unconfigured",
                "Platform runtime defaults must be configured before accepting a run.",
                StatusCodes.Status409Conflict);
        if (!interactive && platformHead.CurrentRevision != originalRequest.ExpectedPlatformRuntimeRevision)
            throw ProjectConfigException.Conflict("The platform runtime revision has changed.");
        var platformVersion = await context.PlatformRuntimeRevisions.AsNoTracking()
            .SingleAsync(item => item.Revision == platformHead.CurrentRevision, cancellationToken)
            .ConfigureAwait(false);
        var defaults = Deserialize<PlatformRuntimeDefaults>(platformVersion.ConfigurationJson);
        var effectiveRequest = interactive
            ? originalRequest with { Context = CreateInteractiveRunContext(projectConfiguration, defaults) }
            : originalRequest;

        var modelSelections = ResolveModelSelections(
            projectConfiguration, defaults, effectiveRequest.Context, project.ProjectId);
        modelSelections = await BindProviderBindingRevisionsAsync(
            context, project.ProjectId, modelSelections, cancellationToken).ConfigureAwait(false);
        var providers = ResolveProviders(effectiveRequest.Context);
        var egress = ProjectConfigurationValidator.ResolveEgress(
            defaults.EgressBaseline,
            projectConfiguration.EgressNarrowing,
            effectiveRequest.Context.RequiredEgress);
        var limits = ProjectConfigurationValidator.ResolveLimits(defaults, projectConfiguration.RunLimits);
        var effective = new EffectiveRunSelection(
            project.ProjectId,
            runId,
            project.Revision,
            project.ConfigurationRevision,
            platformHead.CurrentRevision,
            effectiveRequest.Context.Revision,
            modelSelections.PrimarySelection,
            providers,
            egress,
            limits,
            projectConfiguration,
            defaults.EgressBaseline,
            projectConfiguration.EgressNarrowing,
            ProjectConfigurationValidator.ValidateEgressRules(effectiveRequest.Context.RequiredEgress))
        {
            ModelSelections = modelSelections.EffectiveSelections,
            ModelTaskSelections = modelSelections.TaskSelections
        };
        var record = new ProjectRunSelectionRecord
        {
            RunId = runId,
            ProjectId = project.ProjectId,
            ProjectRevision = project.Revision,
            ProjectConfigurationRevision = project.ConfigurationRevision,
            PlatformRuntimeRevision = platformHead.CurrentRevision,
            ContextRevision = effectiveRequest.Context.Revision,
            RequestFingerprint = fingerprint,
            SnapshotJson = Serialize(effective),
            CreatedAt = timeProvider.GetUtcNow(),
        };
        context.RunSelections.Add(record);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasCurrentRoleAsync(
            context,
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return effective;
    }

    /// <summary>Locks project and platform heads and obtains a transaction-scoped advisory lock for the run ID.</summary>
    private static async Task LockRunSelectionInputsAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();

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

    /// <summary>Reads the saved immutable selection after current Orchestrator authority and exact resource binding checks.</summary>
    public async Task<EffectiveRunSelection> GetRunSelectionAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken,
        bool forCancellation = false)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireResourceBinding(projectId, runId);
        // Only the cancellation owner route permits project-access roles without run binding.
        var canCancel = forCancellation && caller.BoundRunId is null &&
            (await HasCurrentRoleAsync(caller, ProjectAuthorityResourceType.Tenant, caller.TenantId,
                ProjectAuthorityRole.TenantAdmin, cancellationToken).ConfigureAwait(false) ||
             await HasCurrentRoleAsync(caller, ProjectAuthorityResourceType.Project, projectId,
                ProjectAuthorityRole.Owner, cancellationToken).ConfigureAwait(false) ||
             await HasCurrentRoleAsync(caller, ProjectAuthorityResourceType.Project, projectId,
                ProjectAuthorityRole.Contributor, cancellationToken).ConfigureAwait(false));
        if (forCancellation ? !canCancel : !await HasCurrentRoleAsync(
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

    /// <summary>Reads only a run's Memory selection for a caller with current private-Knowledge access.</summary>
    public async Task<ProjectRunSelectionResponse> GetPrivateKnowledgeRunSelectionAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        // This read is project-scoped; a run-bound token cannot use it to inspect another run's selection.
        var authority = caller.ToEffectiveResponse();
        var project = await FindTenantProjectAsync(
            caller, projectId, runId: null, cancellationToken).ConfigureAwait(false);
        var hasPrivateKnowledgeAccess = authority.EffectiveAuthority.Any(resource =>
            (resource.ResourceType == ProjectAuthorityResourceType.Project &&
                string.Equals(resource.ResourceId, projectId, StringComparison.Ordinal) ||
             resource.ResourceType == ProjectAuthorityResourceType.Tenant &&
                string.Equals(resource.ResourceId, caller.TenantId, StringComparison.Ordinal)) &&
            resource.Permissions.Any(permission =>
                permission.Permission == ProjectAuthorizationPermission.AccessPrivateKnowledge));
        if (!hasPrivateKnowledgeAccess)
            throw ProjectConfigException.Forbidden();

        var selection = await db.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == project.ProjectId && item.RunId == runId,
                cancellationToken).ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        var effective = DeserializeRunSelection(selection.SnapshotJson);
        // Knowledge needs only the Memory candidate; do not disclose model or other provider selections.
        return new ProjectRunSelectionResponse(
            effective.ProjectId,
            effective.RunId,
            effective.ProjectRevision,
            effective.ProjectConfigurationRevision,
            effective.PlatformRuntimeRevision,
            effective.ContextRevision,
            effective.Providers
                .Where(provider => provider.Seam == ProviderSeam.Memory)
                .ToImmutableArray(),
            new ProjectRunLimitSnapshot(effective.RunLimits.MaxPromptTokens));
    }

    /// <summary>
    /// Returns the project-scope spend budgets from the current project and platform configuration, not from the
    /// run's stored snapshot. Orchestrator calls this for each new dispatch, so a stricter project budget applies
    /// to the next dispatch of every run that is already accepted.
    /// </summary>
    /// <exception cref="ProjectConfigException">The caller, project, run selection, or current configuration is not valid.</exception>
    public async Task<CurrentProjectSpendBudgets> GetCurrentProjectSpendBudgetsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        // Only Orchestrator can read budgets, and only for the project and run that its token is bound to.
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireResourceBinding(projectId, runId);
        // Check the current role assignment as well as the token, so a revoked role takes effect at once.
        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
        var project = await FindTenantProjectAsync(caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        // The run must have been accepted for this project. An unknown run returns 404.
        if (!await db.RunSelections.AsNoTracking()
            .AnyAsync(item => item.ProjectId == project.ProjectId && item.RunId == runId, cancellationToken)
            .ConfigureAwait(false))
            throw ProjectConfigException.NotFound();

        // Load and validate the project's current configuration revision.
        var projectVersion = await db.ProjectConfigurationRevisions.AsNoTracking()
            .SingleAsync(item => item.ProjectId == project.ProjectId &&
                item.Revision == project.ConfigurationRevision, cancellationToken)
            .ConfigureAwait(false);
        var projectConfiguration = ProjectConfigurationValidator.Validate(
            Deserialize<ProjectConfiguration>(projectVersion.ConfigurationJson));
        // Platform defaults are the ceiling for project budgets. Without them, budgets can't be resolved,
        // so return 409 instead of an empty list that would look like "no budgets".
        var platformHead = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        if (platformHead.CurrentRevision == 0)
            throw new ProjectConfigException(
                "platform_runtime_unconfigured",
                "Platform runtime defaults must be configured before reading spend budgets.",
                StatusCodes.Status409Conflict);
        var platformVersion = await db.PlatformRuntimeRevisions.AsNoTracking()
            .SingleAsync(item => item.Revision == platformHead.CurrentRevision, cancellationToken)
            .ConfigureAwait(false);
        var defaults = Deserialize<PlatformRuntimeDefaults>(platformVersion.ConfigurationJson);
        // Merge the two layers with the same rules used at configuration time, so the project can't widen
        // a platform budget here either.
        var limits = ProjectConfigurationValidator.ResolveLimits(defaults, projectConfiguration.RunLimits);
        // Return only project-scope budgets. Run and agent budgets come from the run's stored snapshot.
        // Both revisions are included so the caller can tell which configuration produced the result.
        return new CurrentProjectSpendBudgets(
            project.ProjectId,
            project.ConfigurationRevision,
            platformHead.CurrentRevision,
            [.. (limits.SpendBudgets ?? []).Where(budget => budget.Scope == SpendBudgetScopes.Project)]);
    }

    /// <summary>
    /// Builds a bounded proof for the exact signed Broker request from current authority and the stored selection hash.
    /// </summary>
    /// <exception cref="ProjectConfigException">The caller, active project, assignment, or selection binding is no longer valid.</exception>
    public async Task<BrokerRunBindingProofResponse> GetBrokerRunBindingProofAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.OrchestratorScope);
        caller.RequireExactRunBinding(projectId, runId);
        var project = await FindTenantProjectAsync(caller, projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.NotFound();

        var hasProjectVisibility =
            await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin,
                cancellationToken).ConfigureAwait(false) ||
            await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Tenant,
                caller.TenantId,
                ProjectAuthorityRole.TenantAdmin,
                cancellationToken).ConfigureAwait(false) ||
            await HasAnyCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Project,
                projectId,
                [
                    ProjectAuthorityRole.Owner,
                    ProjectAuthorityRole.Contributor,
                    ProjectAuthorityRole.Viewer,
                    ProjectAuthorityRole.Orchestrator,
                ],
                cancellationToken).ConfigureAwait(false);
        if (!hasProjectVisibility)
            throw ProjectConfigException.NotFound();

        if (!await HasCurrentRoleAsync(
            caller,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorityRole.Orchestrator,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden("orchestrator_role_required");

        var assignments = caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Project &&
            grant.ResourceId == projectId &&
            grant.Role == ProjectAuthorityRole.Orchestrator).Take(2).ToArray();
        if (assignments.Length != 1 ||
            assignments[0].AssignmentId == Guid.Empty ||
            assignments[0].Revision <= 0 ||
            caller.MembershipId == Guid.Empty ||
            caller.MembershipRevision <= 0)
            throw ProjectConfigException.Forbidden();
        if (!await db.RoleAssignments.AsNoTracking().AnyAsync(
            assignment =>
                assignment.AssignmentId == assignments[0].AssignmentId &&
                assignment.MembershipId == caller.MembershipId &&
                assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                assignment.ResourceId == projectId &&
                assignment.Role == ProjectAuthorityRole.Orchestrator &&
                assignment.Revision == assignments[0].Revision &&
                assignment.State == ProjectAuthorityRecordState.Active,
            cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();

        var record = await db.RunSelections.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.RunId == runId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound("run_selection_not_found");
        var selection = DeserializeRunSelection(record.SnapshotJson);
        if (selection.ProjectId != projectId ||
            selection.RunId != runId ||
            selection.ProjectRevision != record.ProjectRevision ||
            selection.ProjectConfigurationRevision != record.ProjectConfigurationRevision ||
            selection.PlatformRuntimeRevision != record.PlatformRuntimeRevision ||
            selection.ContextRevision != record.ContextRevision)
            throw new InvalidOperationException("The run-selection snapshot does not match its recorded binding.");

        return new BrokerRunBindingProofResponse(
            caller.Issuer,
            caller.ActorId,
            caller.TenantId,
            caller.MembershipId,
            caller.MembershipRevision,
            projectId,
            runId,
            assignments[0].AssignmentId,
            assignments[0].Revision,
            record.ProjectRevision,
            record.ProjectConfigurationRevision,
            record.PlatformRuntimeRevision,
            record.ContextRevision,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.SnapshotJson))));
    }

    /// <summary>Enforces current membership, tenant ownership, token bindings, and the requested project access level.</summary>
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

    private Task<ProjectRecord> FindTenantProjectAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string? runId,
        CancellationToken cancellationToken) =>
        FindTenantProjectAsync(db, caller, projectId, runId, cancellationToken);

    /// <summary>Loads a tenant-owned project without disclosing foreign-project existence.</summary>
    private static async Task<ProjectRecord> FindTenantProjectAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        string projectId,
        string? runId,
        CancellationToken cancellationToken)
    {
        caller.RequireResourceBinding(projectId, runId);
        await EnsureCurrentMembershipAsync(context, caller, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
        var project = await context.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null || project.TenantId != caller.TenantId)
            throw ProjectConfigException.NotFound();
        return project;
    }

    /// <summary>Checks project egress and run-limit overrides against the current configured platform defaults.</summary>
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

    private Task<PlatformRuntimeHeadRecord> GetPlatformHeadAsync(CancellationToken cancellationToken) =>
        GetPlatformHeadAsync(db, cancellationToken);

    private static async Task<PlatformRuntimeHeadRecord> GetPlatformHeadAsync(
        ProjectsConfigDbContext context,
        CancellationToken cancellationToken) =>
        await context.PlatformRuntimeHeads.SingleOrDefaultAsync(
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

    private static PlatformRuntimeDefaults PreservePlatformModelSwitch(
        PlatformRuntimeDefaults? current,
        PlatformRuntimeDefaults submitted)
    {
        if (current is null)
            return submitted with { ModelDefaultSwitch = new PlatformModelDefaultSwitch() };
        var switchState = current.ModelDefaultSwitch;
        if (switchState.Mode != PlatformModelDefaultSwitchMode.Active)
            return submitted with { ModelDefaultSwitch = switchState };

        var tasks = submitted.ModelTaskDefaults
            .Where(item => !switchState.ManagedTaskKinds.Contains(item.TaskKind))
            .Concat(current.ModelTaskDefaults.Where(item =>
                switchState.ManagedTaskKinds.Contains(item.TaskKind)))
            .GroupBy(item => item.TaskKind)
            .Select(group => group.First())
            .OrderBy(item => item.TaskKind)
            .ToImmutableArray();
        return submitted with
        {
            ModelSelection = current.ModelSelection,
            ModelTaskDefaults = tasks,
            ModelDefaultSwitch = switchState,
        };
    }

    private static PlatformRuntimeDefaults ApplyPlatformProviderSwitch(
        PlatformRuntimeDefaults current,
        ModelProviderBindingRevision binding)
    {
        if (binding.Scope != ModelProviderBindingScope.Platform ||
            binding.ProjectId is not null ||
            binding.CredentialIdentity is not { } identity ||
            binding.CredentialReference is not { } credential ||
            binding.RateCardPin is null)
            throw new ProjectConfigException(
                "model_provider_binding_unavailable",
                "The selected platform provider does not have the required credential and pricing evidence.",
                StatusCodes.Status422UnprocessableEntity);

        ModelSelectionSettings CreateSelection(string effort)
        {
            if (!binding.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                throw ProviderBindingUnavailable();
            RuntimeModelBindingPin pin;
            try
            {
                pin = RuntimeModelBindingsResolver.Pin(binding, effort);
            }
            catch (RuntimeAuthorizationException)
            {
                throw ProviderBindingUnavailable();
            }
            return new ModelSelectionSettings(
                binding.ProviderId, credential, ModelSourceMode.Byok)
            {
                ReasoningEffort = effort,
                CredentialIdentity = identity,
                ModelBindingPin = pin,
            };
        }

        var shipped = ModelTaskSelectionDefaults.Create()
            .ToDictionary(item => item.TaskKind);
        var isSameProvider =
            current.ModelDefaultSwitch.Mode == PlatformModelDefaultSwitchMode.Active &&
            string.Equals(
                current.ModelDefaultSwitch.ActiveProviderId,
                binding.ProviderId,
                StringComparison.Ordinal);
        var previousManaged = current.ModelDefaultSwitch.Mode == PlatformModelDefaultSwitchMode.Active
            ? current.ModelDefaultSwitch.ManagedTaskKinds
            : [];
        var managed = isSameProvider
            ? previousManaged.ToImmutableHashSet()
            : current.ModelTaskDefaults
                .Where(item => previousManaged.Contains(item.TaskKind) ||
                    shipped.TryGetValue(item.TaskKind, out var installation) &&
                    item.Selection == installation.Selection)
                .Select(item => item.TaskKind)
                .ToImmutableHashSet();
        var taskDefaults = current.ModelTaskDefaults
            .Select(item =>
            {
                if (!managed.Contains(item.TaskKind))
                    return item;
                var effort = item.Selection.ReasoningEffort ??
                             item.Selection.ModelBindingPin?.ReasoningEffort ??
                             shipped[item.TaskKind].Selection.ReasoningEffort ??
                             "high";
                var switched = CreateSelection(effort);
                return item with
                {
                    Selection = new ModelSelectionOverride(
                        switched.Reference, switched.ReasoningEffort, switched.ModelBindingPin)
                    {
                        CredentialReference = switched.CredentialReference,
                        CredentialIdentity = switched.CredentialIdentity,
                    },
                };
            })
            .ToImmutableArray();
        return ProjectConfigurationValidator.Validate(current with
        {
            ModelSelection = CreateSelection("high"),
            ModelTaskDefaults = taskDefaults,
            ModelDefaultSwitch = new PlatformModelDefaultSwitch
            {
                Mode = PlatformModelDefaultSwitchMode.Active,
                ActiveProviderId = binding.ProviderId,
                ProviderRevision = binding.ProviderRevision,
                ManagedTaskKinds = managed.OrderBy(kind => kind).ToImmutableArray(),
            },
        });
    }

    private static PlatformRuntimeDefaults RestoreHostedPlatformFallbacks(PlatformRuntimeDefaults current)
    {
        var shipped = ModelTaskSelectionDefaults.Create()
            .ToDictionary(item => item.TaskKind);
        var existing = current.ModelTaskDefaults
            .ToDictionary(item => item.TaskKind);
        var managed = current.ModelDefaultSwitch.ManagedTaskKinds.ToImmutableHashSet();
        var tasks = Enum.GetValues<ModelTaskKind>()
            .Select(taskKind =>
            {
                var fallback = shipped[taskKind];
                if (!existing.TryGetValue(taskKind, out var currentTask) || managed.Contains(taskKind))
                    return fallback;
                if (currentTask.Selection.Reference is null)
                {
                    var effort = currentTask.Selection.ReasoningEffort ??
                                 fallback.Selection.ReasoningEffort ??
                                 "high";
                    return new ModelTaskSelectionOverride(
                        taskKind,
                        new ModelSelectionOverride(fallback.Selection.Reference, effort));
                }
                return currentTask;
            })
            .ToImmutableArray();
        return ProjectConfigurationValidator.Validate(current with
        {
            ModelSelection = null,
            ModelTaskDefaults = tasks,
            ModelDefaultSwitch = new PlatformModelDefaultSwitch
            {
                Mode = PlatformModelDefaultSwitchMode.ExplicitHosted,
            },
        });
    }

    private static string ToSwitchMode(PlatformModelDefaultSwitchMode mode) => mode switch
    {
        PlatformModelDefaultSwitchMode.Untouched => "untouched",
        PlatformModelDefaultSwitchMode.Active => "active",
        PlatformModelDefaultSwitchMode.ExplicitHosted => "explicitHosted",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static async Task<PlatformRuntimeHeadRecord> LockPlatformHeadForSwitchAsync(
        ProjectsConfigDbContext context,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM projects_config.platform_runtime_heads WHERE id = 'default' FOR UPDATE",
            connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("The platform runtime head row is missing.");
        return await context.PlatformRuntimeHeads.SingleAsync(
            item => item.Id == PlatformRuntimeHeadRecord.SingletonId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockPlatformAdminAuthorityAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        var grants = caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Platform &&
            grant.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
            grant.Role == ProjectAuthorityRole.PlatformAdmin).ToArray();
        if (grants.Length == 0)
            throw ProjectConfigException.Forbidden();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT assignment_id, revision, authorization_result
            FROM projects_config.lock_native_acs_policy_authority(
                @membership_id, @issuer, @actor, @tenant, @membership_revision,
                'Platform', 'default', @ids, @types, @resources, @roles, @revisions)
            """,
            connection, transaction);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(2) != "authorized" ||
            !grants.Any(item => item.AssignmentId == reader.GetGuid(0) && item.Revision == reader.GetInt64(1)))
            throw ProjectConfigException.Forbidden();
    }

    private static async Task<LockedPlatformProvider?> LockAndReadPlatformProviderAsync(
        ProjectsConfigDbContext context,
        string providerId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using var lockCommand = new NpgsqlCommand(
            """
            SELECT provider_id
            FROM projects_config.model_providers
            WHERE provider_id = @provider_id AND scope = 'Platform' AND project_id IS NULL
            FOR UPDATE
            """,
            connection, transaction);
        lockCommand.Parameters.AddWithValue("provider_id", providerId);
        if (await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            return null;
        var head = await context.ModelProviderDrafts.SingleAsync(
            item => item.ProviderId == providerId && item.Scope == ModelProviderBindingScope.Platform &&
                item.ProjectId == null, cancellationToken).ConfigureAwait(false);
        var revision = await context.ModelProviderDraftRevisions.AsNoTracking().SingleAsync(
            item => item.ProviderId == providerId && item.ProviderRevision == head.CurrentRevision,
            cancellationToken).ConfigureAwait(false);
        return new LockedPlatformProvider(head, revision);
    }

    private static async Task<PlatformRuntimeDefaults> ReadPlatformDefaultsRevisionAsync(
        ProjectsConfigDbContext context,
        PlatformRuntimeHeadRecord head,
        CancellationToken cancellationToken)
    {
        if (head.CurrentRevision == 0)
            throw new ProjectConfigException(
                "platform_runtime_unconfigured",
                "Platform runtime defaults must be initialized before switching the provider.",
                StatusCodes.Status409Conflict);
        var revision = await context.PlatformRuntimeRevisions.AsNoTracking().SingleAsync(
            item => item.Revision == head.CurrentRevision, cancellationToken).ConfigureAwait(false);
        return ProjectConfigurationValidator.Validate(
            Deserialize<PlatformRuntimeDefaults>(revision.ConfigurationJson));
    }

    private async Task<long> AppendPlatformDefaultsAsync(
        ProjectsConfigDbContext context,
        PlatformRuntimeHeadRecord head,
        string actorId,
        PlatformRuntimeDefaults defaults,
        CancellationToken cancellationToken)
    {
        var revision = checked(head.CurrentRevision + 1);
        var now = timeProvider.GetUtcNow();
        context.PlatformRuntimeRevisions.Add(new PlatformRuntimeRevisionRecord
        {
            HeadId = PlatformRuntimeHeadRecord.SingletonId,
            Revision = revision,
            ConfigurationJson = Serialize(defaults),
            UpdatedByActorId = actorId,
            CreatedAt = now,
        });
        head.CurrentRevision = revision;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return revision;
    }

    private sealed record LockedPlatformProvider(
        ModelProviderDraftHeadRecord Head,
        ModelProviderDraftRevisionRecord Revision);

    /// <summary>Resolves global task routes and configured overrides for eligible cast assignments.</summary>
    internal static ResolvedModelSelections ResolveModelSelections(
        ProjectConfiguration project,
        PlatformRuntimeDefaults platform,
        RunSelectionContext context,
        string projectId)
    {
        var targets = new HashSet<(string? RoleId, string? AgentId)> { (null, null) };
        var castingByAgent = project.Casting.ToDictionary(item => item.AgentId, StringComparer.Ordinal);
        var eligibleRoles = project.Casting.Select(item => item.Role).ToHashSet(StringComparer.Ordinal);
        foreach (var roleId in project.ModelRoleOverrides.Select(item => item.RoleId)
                     .Concat(platform.ModelRoleDefaults.Select(item => item.RoleId)))
            if (eligibleRoles.Contains(roleId))
                targets.Add((roleId, null));
        foreach (var agentId in project.ModelAgentOverrides.Select(item => item.AgentId)
                     .Concat(platform.ModelAgentDefaults.Select(item => item.AgentId)))
            if (castingByAgent.TryGetValue(agentId, out var cast))
                targets.Add((cast.Role, cast.AgentId));

        var taskKinds = Enum.GetValues<ModelTaskKind>();
        if (targets.Count > ModelTaskSelectionLimits.MaximumRoutes / taskKinds.Length)
            throw new ProjectConfigException(
                "model_selection_limit_exceeded",
                $"A run may contain at most {ModelTaskSelectionLimits.MaximumRoutes} task model routes.",
                StatusCodes.Status422UnprocessableEntity);
        var taskSelections = ImmutableArray.CreateBuilder<ModelTaskSelectionReference>();
        var effectiveByIdentity = new Dictionary<(string Reference, string Effort), EffectiveModelSelection>();
        var connectionScope = project.ModelSelection is not null
            ? ProjectAuthorityResourceType.Project
            : ProjectAuthorityResourceType.Platform;
        foreach (var taskKind in taskKinds)
        foreach (var target in targets
                     .OrderBy(item => item.RoleId, StringComparer.Ordinal)
                     .ThenBy(item => item.AgentId, StringComparer.Ordinal))
        {
            var selection = ResolveTaskModelSelection(project, platform, taskKind, target.RoleId, target.AgentId);
            if (!context.AvailableModelSelectionReferences.Contains(selection.Reference))
                throw new ProjectConfigException(
                    "model_selection_unavailable",
                    $"The configured model selection '{selection.Reference}' is unavailable in this run's immutable selection context.",
                    StatusCodes.Status422UnprocessableEntity);

            var effort = selection.ReasoningEffort ?? "high";
            var normalized = selection with { ReasoningEffort = effort };
            ProjectConfigurationValidator.ValidateResolvedCredentialSlotBinding(
                normalized, "modelSelection");
            if (normalized.CredentialIdentity is
                { Scope: ModelProviderBindingScope.Project } identity &&
                identity.ProjectId != projectId)
                throw new ProjectConfigException(
                    "model_credential_identity_invalid",
                    "The model credential slot belongs to a different project.",
                    StatusCodes.Status422UnprocessableEntity);
            var selectionConnectionScope = normalized.CredentialIdentity is { } slotIdentity
                ? (slotIdentity.Scope == ModelProviderBindingScope.Project
                    ? ProjectAuthorityResourceType.Project
                    : ProjectAuthorityResourceType.Platform)
                : connectionScope;
            var key = (normalized.Reference, effort);
            if (effectiveByIdentity.TryGetValue(key, out var existing))
                effectiveByIdentity[key] = MergeEffectiveSelection(existing, normalized, selectionConnectionScope);
            else
                effectiveByIdentity.Add(key, new EffectiveModelSelection(normalized, selectionConnectionScope));
            if (effectiveByIdentity.Count > ModelTaskSelectionLimits.MaximumAcceptedSelections)
                throw new ProjectConfigException(
                    "model_selection_limit_exceeded",
                    $"A run may contain at most {ModelTaskSelectionLimits.MaximumAcceptedSelections} distinct model selections.",
                    StatusCodes.Status422UnprocessableEntity);
            taskSelections.Add(new ModelTaskSelectionReference(
                taskKind, target.RoleId, target.AgentId, normalized.Reference, effort));
        }

        var resolvedTasks = taskSelections
            .OrderBy(item => item.TaskKind)
            .ThenBy(item => item.RoleId, StringComparer.Ordinal)
            .ThenBy(item => item.AgentId, StringComparer.Ordinal)
            .ToImmutableArray();
        var effectiveSelections = effectiveByIdentity
            .OrderBy(item => item.Key.Reference, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Effort, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToImmutableArray();
        var primary = project.ModelSelection is { } projectSelection
            ? NormalizeModelSelection(projectSelection) ?? throw new InvalidOperationException(
                "The explicit project model selection is missing.")
            : resolvedTasks.FirstOrDefault(item =>
                  item.TaskKind == ModelTaskKind.Planning && item.RoleId is null && item.AgentId is null) is { } planning
                ? effectiveByIdentity[(planning.ModelSelectionReference, planning.ReasoningEffort)].Selection
                : throw new ProjectConfigException(
                    "model_selection_unavailable",
                    "No task-based model selection is configured for this run.",
                    StatusCodes.Status422UnprocessableEntity);
        return new ResolvedModelSelections(primary, effectiveSelections, resolvedTasks);
    }

    /// <summary>Applies project and platform model layers, preserving explicit project-wide selection precedence.</summary>
    private static ModelSelectionSettings ResolveTaskModelSelection(
        ProjectConfiguration project,
        PlatformRuntimeDefaults platform,
        ModelTaskKind taskKind,
        string? roleId,
        string? agentId)
    {
        var selected = NormalizeModelSelection(project.ModelSelection ?? platform.ModelSelection);
        if (project.ModelSelection is not null)
            return selected ?? throw new InvalidOperationException(
                "The explicit project model selection is missing.");

        // Platform task, role, and agent defaults apply first; project overrides then apply in the same order.
        selected = ApplyModelOverride(
            selected,
            platform.ModelTaskDefaults.FirstOrDefault(item => item.TaskKind == taskKind)?.Selection);
        if (roleId is not null)
            selected = ApplyModelOverride(
                selected,
                platform.ModelRoleDefaults.FirstOrDefault(item => item.RoleId == roleId)?.Selection);
        if (agentId is not null)
            selected = ApplyModelOverride(
                selected,
                platform.ModelAgentDefaults.FirstOrDefault(item => item.AgentId == agentId)?.Selection);
        selected = ApplyModelOverride(
            selected,
            project.ModelTaskOverrides.FirstOrDefault(item => item.TaskKind == taskKind)?.Selection);
        if (roleId is not null)
            selected = ApplyModelOverride(
                selected,
                project.ModelRoleOverrides.FirstOrDefault(item => item.RoleId == roleId)?.Selection);
        if (agentId is not null)
            selected = ApplyModelOverride(
                selected,
                project.ModelAgentOverrides.FirstOrDefault(item => item.AgentId == agentId)?.Selection);
        return selected ?? throw new ProjectConfigException(
            "model_selection_unavailable",
            $"No model selection is configured for task '{taskKind}'.",
            StatusCodes.Status422UnprocessableEntity);
    }

    /// <summary>Applies a partial model override and clears stale pins and credential links when the binding changes.</summary>
    private static ModelSelectionSettings? ApplyModelOverride(
        ModelSelectionSettings? current,
        ModelSelectionOverride? modelOverride)
    {
        if (modelOverride is null)
            return current;
        if (modelOverride.Reference is { } explicitReference &&
            current?.Reference != explicitReference)
        {
            var explicitEffort = modelOverride.ReasoningEffort ??
                                 modelOverride.ModelBindingPin?.ReasoningEffort ??
                                 "high";
            var explicitSourceMode = modelOverride.ModelBindingPin?.SourceMode ??
                (modelOverride.CredentialIdentity is not null || modelOverride.CredentialReference is not null
                    ? ModelSourceMode.Byok
                    : current?.SourceMode == ModelSourceMode.HostedCopilot
                        ? ModelSourceMode.HostedCopilot
                        : (ModelSourceMode?)null);
            return new ModelSelectionSettings(
                explicitReference,
                modelOverride.CredentialReference,
                explicitSourceMode,
                explicitSourceMode == ModelSourceMode.HostedCopilot ? current?.ConnectionId : null)
            {
                ReasoningEffort = explicitEffort,
                ModelBindingPin = modelOverride.ModelBindingPin,
                CredentialIdentity = modelOverride.CredentialIdentity,
            };
        }

        var reference = modelOverride.Reference ?? current?.Reference ?? throw new ProjectConfigException(
            "model_selection_unavailable",
            "A model selection override requires a base model reference.",
            StatusCodes.Status422UnprocessableEntity);
        var effort = modelOverride.ReasoningEffort ??
                     modelOverride.ModelBindingPin?.ReasoningEffort ??
                     current?.ReasoningEffort ?? "high";
        var sourceMode = modelOverride.ModelBindingPin?.SourceMode ?? current?.SourceMode;
        var sourceChanged = current?.SourceMode != sourceMode;
        var effortChanged = (current?.ReasoningEffort ?? "high") != effort;
        var slotBacked = modelOverride.CredentialIdentity is not null ||
                         modelOverride.ModelBindingPin?.ProviderId is not null;
        var credentialBindingChanged = current is null || sourceChanged ||
                                       modelOverride.CredentialIdentity is not null &&
                                       current.CredentialIdentity != modelOverride.CredentialIdentity ||
                                       slotBacked && current.CredentialIdentity is null ||
                                       modelOverride.ModelBindingPin?.ProviderId is { } providerId &&
                                       current.CredentialIdentity?.ProviderId != providerId;
        var retainedPin = modelOverride.ModelBindingPin ??
                          (modelOverride.Reference is null && !sourceChanged && current?.ModelBindingPin is not null
                              ? current.ModelBindingPin with { ReasoningEffort = effort }
                              : !sourceChanged && !effortChanged
                                  ? current?.ModelBindingPin
                                  : null);
        return new ModelSelectionSettings(
            reference,
            modelOverride.CredentialReference ??
                (credentialBindingChanged ? null : current?.CredentialReference),
            sourceMode,
            sourceMode == ModelSourceMode.HostedCopilot ? current?.ConnectionId : null)
        {
            ReasoningEffort = effort,
            ModelBindingPin = retainedPin,
            CredentialIdentity = modelOverride.CredentialIdentity ??
                                 (credentialBindingChanged ? null : current?.CredentialIdentity)
        };
    }

    /// <summary>Normalizes omitted reasoning effort to the platform's High default.</summary>
    private static ModelSelectionSettings? NormalizeModelSelection(ModelSelectionSettings? selection) =>
        selection is null
            ? null
            : selection with
            {
                ReasoningEffort = selection.ReasoningEffort ?? selection.ModelBindingPin?.ReasoningEffort ?? "high"
            };

    /// <summary>Coalesces identical routes without dropping a pin or merging conflicting bindings.</summary>
    private static EffectiveModelSelection MergeEffectiveSelection(
        EffectiveModelSelection existing,
        ModelSelectionSettings incoming,
        ProjectAuthorityResourceType incomingConnectionScope)
    {
        var current = existing.Selection;
        if (existing.ConnectionScope != incomingConnectionScope ||
            current.CredentialReference is not null && incoming.CredentialReference is not null &&
                current.CredentialReference != incoming.CredentialReference ||
            current.SourceMode is not null && incoming.SourceMode is not null &&
                current.SourceMode != incoming.SourceMode ||
            current.ConnectionId is not null && incoming.ConnectionId is not null &&
                current.ConnectionId != incoming.ConnectionId ||
            current.CredentialIdentity != incoming.CredentialIdentity ||
            current.ModelBindingPin is { } currentPin &&
                incoming.ModelBindingPin is { } incomingPin && currentPin != incomingPin)
            throw new ProjectConfigException(
                "model_selection_conflict",
                "Routes with the same model reference and reasoning effort must use one compatible model binding.",
                StatusCodes.Status422UnprocessableEntity);

        return existing with
        {
            Selection = current with
            {
                CredentialReference = current.CredentialReference ?? incoming.CredentialReference,
                SourceMode = current.SourceMode ?? incoming.SourceMode,
                ConnectionId = current.ConnectionId ?? incoming.ConnectionId,
                CredentialIdentity = current.CredentialIdentity,
                ModelBindingPin = current.ModelBindingPin ?? incoming.ModelBindingPin
            }
        };
    }

    /// <summary>Resolved base selection, immutable model bindings, and route matrix for one accepted run.</summary>
    internal sealed record ResolvedModelSelections(
        ModelSelectionSettings PrimarySelection,
        ImmutableArray<EffectiveModelSelection> EffectiveSelections,
        ImmutableArray<ModelTaskSelectionReference> TaskSelections);

    private static async Task<ResolvedModelSelections> BindProviderBindingRevisionsAsync(
        ProjectsConfigDbContext context,
        string projectId,
        ResolvedModelSelections selections,
        CancellationToken cancellationToken)
    {
        var providerIds = CollectOrderedByokProviderIds(projectId, selections);
        if (providerIds.Length > 0)
            await LockModelProviderHeadsAsync(context, providerIds, cancellationToken).ConfigureAwait(false);

        async Task<ModelSelectionSettings> BindAsync(ModelSelectionSettings selection)
        {
            if (selection.SourceMode != ModelSourceMode.Byok || selection.CredentialIdentity is not { } identity)
                return selection;
            if (selection.CredentialReference is not { } credential ||
                selection.ModelBindingPin is not { } requestedPin ||
                identity.ProviderId != selection.Reference ||
                identity.Scope == ModelProviderBindingScope.Project && identity.ProjectId != projectId)
                throw ProviderBindingUnavailable();

            var current = await (
                    from head in context.ModelProviderDrafts.AsNoTracking()
                    join revision in context.ModelProviderDraftRevisions.AsNoTracking()
                        on new { head.ProviderId, Revision = head.CurrentRevision }
                        equals new { revision.ProviderId, Revision = revision.ProviderRevision }
                    where head.ProviderId == identity.ProviderId &&
                        head.Scope == identity.Scope &&
                        head.ProjectId == identity.ProjectId &&
                        head.State == ModelProviderDraftState.Available
                    select revision)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
                throw ProviderBindingUnavailable();
            var binding = ModelProviderDraftService.ToBinding(current);
            if (binding.CredentialVersion != credential.Version ||
                binding.CredentialReference != credential ||
                binding.CredentialIdentity != identity)
                throw new ProjectConfigException(
                    "model_credential_unavailable",
                    "The selected model credential slot or exact secret reference is no longer available.",
                    StatusCodes.Status422UnprocessableEntity);

            RuntimeModelBindingPin expectedPin;
            try
            {
                expectedPin = RuntimeModelBindingsResolver.Pin(
                    binding, selection.ReasoningEffort ?? "high");
            }
            catch (RuntimeAuthorizationException)
            {
                throw ProviderBindingUnavailable();
            }
            if (requestedPin with { ReasoningEffort = selection.ReasoningEffort ?? "high" } != expectedPin)
                throw new ProjectConfigException(
                    "model_provider_binding_stale",
                    "The selected provider or rate-card revision has changed.",
                    StatusCodes.Status422UnprocessableEntity);
            return selection with
            {
                ModelBindingPin = expectedPin,
                ProviderBindingRevision = binding,
            };
        }

        var primary = await BindAsync(selections.PrimarySelection).ConfigureAwait(false);
        var effective = ImmutableArray.CreateBuilder<EffectiveModelSelection>(selections.EffectiveSelections.Length);
        foreach (var item in selections.EffectiveSelections)
            effective.Add(item with { Selection = await BindAsync(item.Selection).ConfigureAwait(false) });
        return selections with
        {
            PrimarySelection = primary,
            EffectiveSelections = effective.MoveToImmutable()
        };
    }

    private static string[] CollectOrderedByokProviderIds(
        string projectId,
        ResolvedModelSelections selections)
    {
        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in new[] { selections.PrimarySelection }
                     .Concat(selections.EffectiveSelections.Select(item => item.Selection)))
        {
            if (selection.SourceMode != ModelSourceMode.Byok || selection.CredentialIdentity is not { } identity)
                continue;
            if (selection.CredentialReference is null ||
                selection.ModelBindingPin is null ||
                identity.ProviderId != selection.Reference ||
                identity.Scope == ModelProviderBindingScope.Project && identity.ProjectId != projectId)
                throw ProviderBindingUnavailable();
            providerIds.Add(identity.ProviderId);
        }

        return providerIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private static async Task LockModelProviderHeadsAsync(
        ProjectsConfigDbContext context,
        string[] providerIds,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            SELECT provider_id
            FROM projects_config.model_providers
            WHERE provider_id = ANY(@provider_ids)
            ORDER BY provider_id COLLATE "C"
            FOR SHARE
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("provider_ids", providerIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    private static ProjectConfigException ProviderBindingUnavailable() =>
        new(
            "model_provider_binding_unavailable",
            "The selected BYOK provider or its Events rate card is unavailable.",
            StatusCodes.Status422UnprocessableEntity);

    /// <summary>Resolves provider candidates by seam cardinality and preserves all selected candidate/version metadata.</summary>
    private ImmutableArray<EffectiveProviderSelection> ResolveProviders(RunSelectionContext context)
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
                    var result = resolver.Resolve(new ProviderResolutionRequest(
                        requirement.Seam,
                        null,
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
                    var result = resolver.ResolveOrdered(new OrderedProviderResolutionRequest(
                        requirement.Seam,
                        null,
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

    /// <summary>Rejects malformed, duplicated, or cardinality-inconsistent owner selection requirements before resolution.</summary>
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

    /// <summary>Allows an existing run ID only when its stored project and request fingerprint exactly match.</summary>
    private static EffectiveRunSelection ReadExistingSelection(
        ProjectRunSelectionRecord existing,
        string projectId,
        string fingerprint)
    {
        if (existing.ProjectId != projectId || existing.RequestFingerprint != fingerprint)
            throw ProjectConfigException.IdempotencyConflict();
        return DeserializeRunSelection(existing.SnapshotJson);
    }

    /// <summary>Reads current snapshots and safely upgrades the supported legacy egress shape in memory.</summary>
    internal static EffectiveRunSelection DeserializeRunSelection(string value)
    {
        var selection = Deserialize<EffectiveRunSelection>(value);
        if (!selection.EgressBaseline.IsDefault &&
            !selection.RequiredEgress.IsDefault &&
            selection.ProjectEgressNarrowing is not { IsDefault: true })
            return selection;

        // Legacy run snapshots stored one effective allowlist; use it for the fields added by the layered egress contract.
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

    /// <summary>Builds a stable digest from sorted request inputs so set ordering does not change idempotency identity.</summary>
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

        // Keep the earlier host/port/protocol shape so retries match fingerprints already stored for legacy rules.
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

    /// <summary>
    /// Lists enabled model references from the model-binding copy and the references already saved in platform and
    /// project configuration. Saved references are reported separately and never become choices.
    /// </summary>
    internal static ProjectModelChoices BuildModelChoices(
        ProjectConfiguration project,
        VersionedPlatformRuntimeDefaults platform,
        ModelBindingCapabilities modelBindings,
        IReadOnlyDictionary<string, ImmutableArray<string>>? hostedReasoningEfforts = null)
    {
        var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string? reference, string source)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return;
            if (!references.TryGetValue(reference, out var sources))
            {
                sources = new HashSet<string>(StringComparer.Ordinal);
                references.Add(reference, sources);
            }
            sources.Add(source);
        }

        if (platform.Defaults is { } defaults)
        {
            Add(defaults.ModelSelection?.Reference, "platformDefault");
            foreach (var item in defaults.ModelTaskDefaults)
                Add(item.Selection.Reference, "platformTask");
            foreach (var item in defaults.ModelRoleDefaults)
                Add(item.Selection.Reference, "platformRole");
            foreach (var item in defaults.ModelAgentDefaults)
                Add(item.Selection.Reference, "platformAgent");
        }

        Add(project.ModelSelection?.Reference, "projectSaved");
        foreach (var item in project.ModelTaskOverrides)
            Add(item.Selection.Reference, "projectSaved");
        foreach (var item in project.ModelRoleOverrides)
            Add(item.Selection.Reference, "projectSaved");
        foreach (var item in project.ModelAgentOverrides)
            Add(item.Selection.Reference, "projectSaved");

        string[] sourceOrder = ["platformDefault", "platformTask", "platformRole", "platformAgent", "projectSaved"];
        var savedReferences = references.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new ProjectModelReferenceChoice(
                item.Key,
                sourceOrder.Where(item.Value.Contains).ToImmutableArray()))
            .ToImmutableArray();
        // Without a deployment-supplied model-binding copy there is no capability source, so report that plainly
        // instead of treating saved references as eligible.
        if (!modelBindings.IsAvailable)
        {
            return new ProjectModelChoices(
                "unavailable",
                "model_capability_catalog_unavailable",
                "verifiedAtRunStart",
                null,
                "high",
                [],
                savedReferences);
        }

        // Only enabled bindings are selectable; disabled ones are omitted because AgentHost refuses them at run start.
        // BYOK efforts come from the binding, while hosted efforts come only from a fresh AgentHost publication.
        // The runtime still checks the selected effort against its live SDK catalog before starting a run.
        var choices = modelBindings.Bindings
            .Where(binding => binding.Enabled &&
                (binding.SourceMode != "Byok" || !binding.SupportedReasoningEfforts.IsEmpty))
            .Select(binding => binding.SourceMode == "Byok"
                ? new ProjectModelChoice(binding.Reference, "byok", "modelBinding", binding.SupportedReasoningEfforts)
                : hostedReasoningEfforts is not null &&
                    hostedReasoningEfforts.TryGetValue(binding.Reference, out var efforts)
                    ? new ProjectModelChoice(binding.Reference, "hostedCopilot", "hostedModelCatalog", efforts)
                    : new ProjectModelChoice(binding.Reference, "hostedCopilot", "verifiedAtRunStart", []))
            .ToImmutableArray();
        return new ProjectModelChoices(
            "available",
            null,
            "verifiedAtRunStart",
            modelBindings.Revision,
            "high",
            choices,
            savedReferences);
    }

    internal static ImmutableArray<string> BuildEffectiveFallbackReferences(
        ProjectConfiguration project,
        PlatformRuntimeDefaults? defaults)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? reference)
        {
            if (!string.IsNullOrWhiteSpace(reference))
                references.Add(reference);
        }

        if (project.ModelSelection is null)
        {
            Add(defaults?.ModelSelection?.Reference);
            foreach (var item in defaults?.ModelTaskDefaults ?? [])
                Add(item.Selection.Reference);
            foreach (var item in defaults?.ModelRoleDefaults ?? [])
                Add(item.Selection.Reference);
            foreach (var item in defaults?.ModelAgentDefaults ?? [])
                Add(item.Selection.Reference);
        }
        return references.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private ProjectProviderChoices BuildProviderChoices(ProjectConfiguration project)
    {
        var seams = ImmutableArray.CreateBuilder<ProjectProviderSeamChoice>();
        foreach (var seam in Enum.GetValues<ProviderSeam>())
        {
            var providerIds = GetPlatformProviderIds(seam);
            if (providerIds.IsEmpty)
                continue;

            string? unavailableReason = null;
            foreach (var id in providerIds)
            {
                if (!providerCatalog.TryGetProvider(id, out var registration) || registration is null)
                {
                    unavailableReason = "provider_not_registered";
                    break;
                }
                if (!registration.Enabled)
                {
                    unavailableReason = "provider_disabled";
                    break;
                }
            }

            seams.Add(new ProjectProviderSeamChoice(
                ToWireLabel(seam),
                ToWireLabel(ProviderSeams.Cardinality(seam)),
                unavailableReason is null ? "selected" : "unavailable",
                unavailableReason,
                providerIds));
        }

        var ignoredOverrides = project.ProviderOverrides.Select(item => ToWireLabel(item.Seam))
            .Concat(project.OrderedProviderOverrides.Select(item => ToWireLabel(item.Seam)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToImmutableArray();
        return new ProjectProviderChoices(false, seams.ToImmutable(), ignoredOverrides);
    }

    private ImmutableArray<string> GetPlatformProviderIds(ProviderSeam seam)
    {
        var providerIds = ImmutableArray.CreateBuilder<string>();
        if (providerCatalog.TryGetDefault(seam, out var defaultId) && defaultId is not null)
            providerIds.Add(defaultId);
        if (providerCatalog.TryGetOrdered(seam, out var ordered))
            providerIds.AddRange(ordered);
        if (seam == ProviderSeam.NetworkPolicy)
            foreach (var layer in Enum.GetValues<NetworkPolicyLayer>())
                if (providerCatalog.TryGetLayer(layer, out var layerId) && layerId is not null)
                    providerIds.Add(layerId);
        if (seam == ProviderSeam.Cost)
            providerIds.AddRange(providerCatalog.MeterSourceSelections.Select(item => item.ProviderId));
        return providerIds.ToImmutable();
    }

    private static string ToWireLabel<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>
    /// Rejects caller-submitted project provider overrides. Any non-empty <c>providerOverrides</c> or
    /// <c>orderedProviderOverrides</c> is refused, including a copy of legacy stored values; callers send empty
    /// arrays and the owner carries stored values forward with <see cref="KeepStoredProviderOverrides"/>.
    /// </summary>
    internal static void RejectSubmittedProviderOverrides(ProjectConfiguration? submitted)
    {
        if (submitted is null)
            return;
        if (!submitted.ProviderOverrides.IsDefaultOrEmpty || !submitted.OrderedProviderOverrides.IsDefaultOrEmpty)
            throw ProviderOverrideUnsupported();
    }

    /// <summary>
    /// Copies the stored provider overrides onto a new revision so legacy values are never deleted by a write.
    /// Run selection ignores them; a default (missing) array is left for validation to reject.
    /// </summary>
    internal static ProjectConfiguration KeepStoredProviderOverrides(
        ProjectConfiguration stored, ProjectConfiguration configuration) => configuration with
    {
        ProviderOverrides = configuration.ProviderOverrides.IsDefault || stored.ProviderOverrides.IsDefault
            ? configuration.ProviderOverrides
            : stored.ProviderOverrides,
        OrderedProviderOverrides = configuration.OrderedProviderOverrides.IsDefault || stored.OrderedProviderOverrides.IsDefault
            ? configuration.OrderedProviderOverrides
            : stored.OrderedProviderOverrides,
    };

    /// <summary>
    /// Rejects an owner-stored proposal draft whose provider overrides differ from the stored configuration,
    /// such as a proposal drafted before project provider overrides were refused. An empty draft array is
    /// accepted because confirmation restores the stored values rather than clearing them.
    /// </summary>
    internal static void RequireStoredProviderOverridesUnchanged(
        ProjectConfiguration stored, ProjectConfiguration draft)
    {
        if ((!draft.ProviderOverrides.IsDefaultOrEmpty &&
                !SameProviderOverrides(draft.ProviderOverrides, stored.ProviderOverrides)) ||
            (!draft.OrderedProviderOverrides.IsDefaultOrEmpty &&
                !SameOrderedProviderOverrides(draft.OrderedProviderOverrides, stored.OrderedProviderOverrides)))
            throw ProviderOverrideUnsupported();
    }

    private static ProjectConfigException ProviderOverrideUnsupported() => ProjectConfigException.Invalid(
        "project_provider_override_unsupported",
        "Platform components other than models are selected by the platform and cannot be overridden by a project. " +
        "Send empty providerOverrides and orderedProviderOverrides; stored values are kept and ignored.");

    private static bool SameProviderOverrides(
        ImmutableArray<ProjectProviderOverride> incoming,
        ImmutableArray<ProjectProviderOverride> previous) =>
        !previous.IsDefault &&
        incoming.Length == previous.Length &&
        incoming.All(item => item is not null) &&
        incoming.SequenceEqual(previous);

    private static bool SameOrderedProviderOverrides(
        ImmutableArray<ProjectOrderedProviderOverride> incoming,
        ImmutableArray<ProjectOrderedProviderOverride> previous)
    {
        if (previous.IsDefault || incoming.Length != previous.Length)
            return false;
        for (var index = 0; index < incoming.Length; index++)
        {
            var current = incoming[index];
            var prior = previous[index];
            if (current is null || prior is null || current.ProviderIds.IsDefault || prior.ProviderIds.IsDefault ||
                current.Seam != prior.Seam ||
                !current.ProviderIds.SequenceEqual(prior.ProviderIds, StringComparer.Ordinal))
                return false;
        }
        return true;
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

    private static bool IsRunSelectionPrimaryKeyViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException postgres &&
                postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(postgres.ConstraintName, "PK_project_run_selections", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Requires ordinary API/admin scopes and a current platform-admin assignment.</summary>
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

    /// <summary>
    /// Requires API read scope, an unbound request, and a current TenantAdmin assignment for the
    /// caller's tenant or a current PlatformAdmin assignment before the platform components report.
    /// </summary>
    /// <exception cref="ProjectConfigException">The caller lacks a current qualifying assignment.</exception>
    public async Task EnsurePlatformComponentReaderAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        if (await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Tenant,
                caller.TenantId,
                ProjectAuthorityRole.TenantAdmin,
                cancellationToken).ConfigureAwait(false) ||
            await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin,
                cancellationToken).ConfigureAwait(false))
            return;
        throw ProjectConfigException.Forbidden();
    }

    public async Task EnsurePlatformPolicyReaderAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireUnboundRequest();
        if (!await HasCurrentRoleAsync(
                caller,
                ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin,
                cancellationToken).ConfigureAwait(false))
            throw ProjectConfigException.Forbidden();
    }

    private Task EnsureCurrentMembershipAsync(
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken) =>
        EnsureCurrentMembershipAsync(db, caller, cancellationToken);

    /// <summary>Rejects stale or revoked membership revisions before project operations.</summary>
    private static async Task EnsureCurrentMembershipAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        var isCurrent = await context.TenantMemberships.AsNoTracking().AnyAsync(
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

    private Task<bool> HasCurrentRoleAsync(
        ProjectAuthorizationContext caller,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role,
        CancellationToken cancellationToken) =>
        HasCurrentRoleAsync(db, caller, resourceType, resourceId, role, cancellationToken);

    private static async Task<bool> HasCurrentRoleAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role,
        CancellationToken cancellationToken) =>
        await (
            from membership in context.TenantMemberships.AsNoTracking()
            join assignment in context.RoleAssignments.AsNoTracking()
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

    /// <summary>Validates and trims a project name before persistence.</summary>
    internal static string NormalizeProjectName(string name)
    {
        ValidateProjectName(name);
        return name.Trim();
    }

    /// <summary>Serializes project configuration with the same strict wire settings used for stored snapshots.</summary>
    internal static string SerializeProjectConfiguration(ProjectConfiguration configuration) =>
        Serialize(configuration);

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

    private async Task<ProjectSummary> ToSummaryAsync(
        ProjectRecord project,
        ProjectAuthorizationContext caller,
        CancellationToken cancellationToken)
    {
        // Read assignments from live owner state instead of caller.Grants, which can predate a grant or revocation.
        // Pin the query to this active actor membership so another actor's or tenant's assignments cannot appear.
        var currentRoles = await (
            from membership in db.TenantMemberships.AsNoTracking()
            join assignment in db.RoleAssignments.AsNoTracking()
                on membership.MembershipId equals assignment.MembershipId
            where membership.MembershipId == caller.MembershipId &&
                membership.Issuer == caller.Issuer &&
                membership.Subject == caller.ActorId &&
                membership.TenantId == caller.TenantId &&
                membership.Revision == caller.MembershipRevision &&
                membership.State == ProjectAuthorityRecordState.Active &&
                assignment.State == ProjectAuthorityRecordState.Active &&
                ((assignment.ResourceType == ProjectAuthorityResourceType.Project &&
                    assignment.ResourceId == project.ProjectId) ||
                 (assignment.ResourceType == ProjectAuthorityResourceType.Tenant &&
                    assignment.ResourceId == caller.TenantId))
            select new EffectiveProjectRole(
                assignment.ResourceType, assignment.ResourceId, assignment.Role))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var summary = new ProjectSummary(
            project.ProjectId, project.Name, project.State, project.Revision,
            project.ConfigurationRevision, project.CreatedAt, project.UpdatedAt);
        return summary with
        {
            EffectiveRoles = currentRoles
                .OrderBy(role => role.ResourceType)
                .ThenBy(role => role.ResourceId, StringComparer.Ordinal)
                .ThenBy(role => role.Role)
                .ToArray(),
        };
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    internal static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidOperationException($"Stored Projects & Config JSON could not be read as {typeof(T).Name}.");

    internal static string SerializeRunSelection(EffectiveRunSelection selection) =>
        Serialize(selection);

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

            // Older stored rules have only host, port, and protocol; read that exact form as public HTTPS.
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
            // Read legacy rules only; always write the current canonical shape.
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
