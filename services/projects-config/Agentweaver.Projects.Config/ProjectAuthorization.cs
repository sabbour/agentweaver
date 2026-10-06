using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

public sealed record ProjectsConfigIdentityOptions(string Issuer);

public sealed record ProjectAuthorizationGrant(
    Guid AssignmentId,
    ProjectAuthorityResourceType ResourceType,
    string ResourceId,
    ProjectAuthorityRole Role,
    long Revision);

public enum ProjectAuthorizationPermission
{
    ReadProjects,
    WriteProjects,
    CreateProjects,
    ReadRunSelection,
    AcceptRunSelection,
    ReadPlatformRuntimeDefaults,
    WritePlatformRuntimeDefaults
}

public sealed record ProjectAuthorizationPermissionGrant(
    ProjectAuthorizationPermission Permission,
    long RoleRevision);

public sealed record EffectiveProjectAuthorization(
    ProjectAuthorityResourceType ResourceType,
    string ResourceId,
    ImmutableArray<ProjectAuthorizationPermissionGrant> Permissions);

public sealed record ProjectAuthorizationContextResponse(
    int ContractVersion,
    string Issuer,
    string ActorId,
    string TenantId,
    long MembershipRevision,
    string? BoundProjectId,
    string? BoundRunId,
    ImmutableArray<EffectiveProjectAuthorization> EffectiveAuthority);

public sealed class ProjectAuthorizationContext
{
    public const int CurrentContractVersion = 1;

    internal ProjectAuthorizationContext(
        string issuer,
        string actorId,
        string tenantId,
        Guid membershipId,
        long membershipRevision,
        ImmutableArray<ProjectAuthorizationGrant> grants,
        ImmutableHashSet<string> scopes,
        string? boundProjectId,
        string? boundRunId,
        string? purpose)
    {
        ContractVersion = CurrentContractVersion;
        Issuer = issuer;
        ActorId = actorId;
        TenantId = tenantId;
        MembershipId = membershipId;
        MembershipRevision = membershipRevision;
        Grants = grants;
        Scopes = scopes;
        BoundProjectId = boundProjectId;
        BoundRunId = boundRunId;
        Purpose = purpose;
    }

    public int ContractVersion { get; }
    public string Issuer { get; }
    public string ActorId { get; }
    public string TenantId { get; }
    public Guid MembershipId { get; }
    public long MembershipRevision { get; }
    public ImmutableArray<ProjectAuthorizationGrant> Grants { get; }
    public ImmutableHashSet<string> Scopes { get; }
    public string? BoundProjectId { get; }
    public string? BoundRunId { get; }
    public string? Purpose { get; }

    public bool HasRole(
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorityRole role) =>
        Grants.Any(grant =>
            grant.ResourceType == resourceType &&
            string.Equals(grant.ResourceId, resourceId, StringComparison.Ordinal) &&
            grant.Role == role);

    public bool HasAnyProjectRole(string projectId, params ProjectAuthorityRole[] roles) =>
        roles.Any(role => HasRole(ProjectAuthorityResourceType.Project, projectId, role));

    public bool IsTenantAdmin =>
        HasRole(ProjectAuthorityResourceType.Tenant, TenantId, ProjectAuthorityRole.TenantAdmin);

    public bool IsPlatformAdmin =>
        HasRole(
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);

    public ProjectAuthorizationGrant? FindProjectGrant(string projectId, ProjectAuthorityRole role) =>
        Grants.FirstOrDefault(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Project &&
            string.Equals(grant.ResourceId, projectId, StringComparison.Ordinal) &&
            grant.Role == role);

    public void RequireScope(string scope)
    {
        if (Purpose is not null || !Scopes.Contains(scope))
            throw ProjectConfigException.Forbidden();
    }

    public void RequireUnboundRequest()
    {
        if (Purpose is not null || BoundProjectId is not null || BoundRunId is not null)
            throw ProjectConfigException.Forbidden();
    }

    public void RequireResourceBinding(string projectId, string? runId = null)
    {
        if (Purpose is not null ||
            (BoundProjectId is not null &&
                !string.Equals(BoundProjectId, projectId, StringComparison.Ordinal)) ||
            (BoundRunId is not null &&
                !string.Equals(BoundRunId, runId, StringComparison.Ordinal)))
            throw ProjectConfigException.Forbidden();
    }

    public ProjectAuthorizationContextResponse ToEffectiveResponse()
    {
        RequireScope(ProjectAuthorizationOwner.ApiReadScope);

        var adminScope = Scopes.Contains(ProjectAuthorizationOwner.ProjectAdminScope);
        var orchestratorScope = Scopes.Contains(ProjectAuthorizationOwner.OrchestratorScope);
        var permissions = new Dictionary<
            (ProjectAuthorityResourceType ResourceType, string ResourceId),
            HashSet<ProjectAuthorizationPermissionGrant>>();

        void Add(
            ProjectAuthorizationGrant grant,
            ProjectAuthorizationPermission permission,
            ProjectAuthorityResourceType? resourceType = null,
            string? resourceId = null)
        {
            var key = (resourceType ?? grant.ResourceType, resourceId ?? grant.ResourceId);
            if (!permissions.TryGetValue(key, out var grants))
            {
                grants = [];
                permissions.Add(key, grants);
            }
            grants.Add(new ProjectAuthorizationPermissionGrant(permission, grant.Revision));
        }

        foreach (var grant in Grants)
        {
            if (grant.ResourceType == ProjectAuthorityResourceType.Project &&
                BoundProjectId is not null &&
                !string.Equals(grant.ResourceId, BoundProjectId, StringComparison.Ordinal))
                continue;

            switch (grant.ResourceType, grant.Role)
            {
                case (ProjectAuthorityResourceType.Platform, ProjectAuthorityRole.PlatformAdmin)
                    when adminScope && BoundProjectId is null && BoundRunId is null:
                    Add(grant, ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults);
                    Add(grant, ProjectAuthorizationPermission.WritePlatformRuntimeDefaults);
                    break;
                case (ProjectAuthorityResourceType.Tenant, ProjectAuthorityRole.TenantAdmin)
                    when BoundRunId is null:
                    if (BoundProjectId is null)
                    {
                        Add(grant, ProjectAuthorizationPermission.ReadProjects);
                        if (adminScope)
                        {
                            Add(grant, ProjectAuthorizationPermission.WriteProjects);
                            Add(grant, ProjectAuthorizationPermission.CreateProjects);
                        }
                    }
                    else
                    {
                        Add(
                            grant,
                            ProjectAuthorizationPermission.ReadProjects,
                            ProjectAuthorityResourceType.Project,
                            BoundProjectId);
                        if (adminScope)
                            Add(
                                grant,
                                ProjectAuthorizationPermission.WriteProjects,
                                ProjectAuthorityResourceType.Project,
                                BoundProjectId);
                    }
                    break;
                case (ProjectAuthorityResourceType.Project, ProjectAuthorityRole.Owner)
                    when BoundRunId is null:
                    Add(grant, ProjectAuthorizationPermission.ReadProjects);
                    if (adminScope)
                        Add(grant, ProjectAuthorizationPermission.WriteProjects);
                    break;
                case (ProjectAuthorityResourceType.Project,
                    ProjectAuthorityRole.Contributor or ProjectAuthorityRole.Viewer)
                    when BoundRunId is null:
                    Add(grant, ProjectAuthorizationPermission.ReadProjects);
                    break;
                case (ProjectAuthorityResourceType.Project, ProjectAuthorityRole.Orchestrator)
                    when orchestratorScope:
                    Add(grant, ProjectAuthorizationPermission.ReadRunSelection);
                    Add(grant, ProjectAuthorizationPermission.AcceptRunSelection);
                    break;
            }
        }

        var effectiveAuthority = permissions
            .OrderBy(item => item.Key.ResourceType)
            .ThenBy(item => item.Key.ResourceId, StringComparer.Ordinal)
            .Select(item => new EffectiveProjectAuthorization(
                item.Key.ResourceType,
                item.Key.ResourceId,
                item.Value
                    .OrderBy(permission => permission.Permission)
                    .ThenBy(permission => permission.RoleRevision)
                    .ToImmutableArray()))
            .ToImmutableArray();

        return new ProjectAuthorizationContextResponse(
            CurrentContractVersion,
            Issuer,
            ActorId,
            TenantId,
            MembershipRevision,
            BoundProjectId,
            BoundRunId,
            effectiveAuthority);
    }
}

public sealed class ProjectAuthorizationOwner(
    ProjectsConfigDbContext db,
    ProjectsConfigIdentityOptions identityOptions)
{
    public const string TenantSelectorHeader = "X-Agentweaver-Tenant";
    public const string ApiReadScope = "api.read";
    public const string ProjectAdminScope = "projects.admin";
    public const string OrchestratorScope = "projects.orchestrator";
    public const string PlatformResourceId = "default";

    public async Task<ProjectAuthorizationContext> ResolveAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<string?> tenantSelectors,
        CancellationToken cancellationToken)
    {
        var request = ReadRequestIdentity(principal, identityOptions.Issuer);
        if (tenantSelectors.Count > 1)
            throw ProjectConfigException.Forbidden();
        var selector = tenantSelectors.Count == 0 ? null : tenantSelectors[0];
        if (selector is not null && !IsOpaqueIdentifier(selector))
            throw ProjectConfigException.Forbidden();

        var memberships = await db.TenantMemberships.AsNoTracking()
            .Where(membership =>
                membership.Issuer == request.Issuer &&
                membership.Subject == request.Subject &&
                membership.State == ProjectAuthorityRecordState.Active)
            .OrderBy(membership => membership.TenantId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        ProjectTenantMembershipRecord membership;
        if (selector is not null)
        {
            var selectedMemberships = memberships.Where(item => item.TenantId == selector).Take(2).ToArray();
            membership = selectedMemberships.Length == 1
                ? selectedMemberships[0]
                : throw ProjectConfigException.Forbidden();
        }
        else
        {
            membership = memberships.Length == 1
                ? memberships[0]
                : throw ProjectConfigException.Forbidden();
        }

        if (request.TenantAssertions.Any(assertion =>
            !string.Equals(assertion, membership.TenantId, StringComparison.Ordinal)))
            throw ProjectConfigException.Forbidden();

        var grants = await (
                from assignment in db.RoleAssignments.AsNoTracking()
                join activeMembership in db.TenantMemberships.AsNoTracking()
                    on assignment.MembershipId equals activeMembership.MembershipId
                where assignment.MembershipId == membership.MembershipId &&
                    assignment.State == ProjectAuthorityRecordState.Active &&
                    activeMembership.State == ProjectAuthorityRecordState.Active
                orderby assignment.ResourceType, assignment.ResourceId, assignment.Role
                select new ProjectAuthorizationGrant(
                    assignment.AssignmentId,
                    assignment.ResourceType,
                    assignment.ResourceId,
                    assignment.Role,
                    assignment.Revision))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ProjectAuthorizationContext(
            request.Issuer,
            request.Subject,
            membership.TenantId,
            membership.MembershipId,
            membership.Revision,
            grants.ToImmutableArray(),
            request.Scopes,
            request.ProjectId,
            request.RunId,
            request.Purpose);
    }

    private static RequestIdentity ReadRequestIdentity(ClaimsPrincipal principal, string configuredIssuer)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true ||
            !Uri.TryCreate(configuredIssuer, UriKind.Absolute, out var issuerUri) ||
            issuerUri.Scheme != Uri.UriSchemeHttps)
            throw ProjectConfigException.Forbidden();

        var subjects = principal.FindAll("sub").Take(2).ToArray();
        if (subjects.Length != 1 || !IsOpaqueIdentifier(subjects[0].Value))
            throw ProjectConfigException.Forbidden();

        var tenantAssertions = principal.FindAll("tenant_id")
            .Concat(principal.FindAll("tid"))
            .Concat(principal.FindAll("http://schemas.microsoft.com/identity/claims/tenantid"))
            .Take(2)
            .Select(claim => claim.Value)
            .ToArray();
        if (tenantAssertions.Length > 1 ||
            tenantAssertions.Any(assertion => !IsOpaqueIdentifier(assertion)))
            throw ProjectConfigException.Forbidden();

        var projectIds = principal.FindAll("project_id").Take(2).ToArray();
        var runIds = principal.FindAll("run_id").Take(2).ToArray();
        if (projectIds.Length > 1 || runIds.Length > 1 ||
            projectIds.Any(claim => !IsOpaqueIdentifier(claim.Value)) ||
            runIds.Any(claim => !IsOpaqueIdentifier(claim.Value)) ||
            (runIds.Length == 1 && projectIds.Length != 1))
            throw ProjectConfigException.Forbidden();

        var purposes = principal.FindAll("purpose").Take(2).ToArray();
        if (purposes.Length > 1)
            throw ProjectConfigException.Forbidden();

        var scopes = principal.FindAll("scope")
            .Concat(principal.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToImmutableHashSet(StringComparer.Ordinal);
        return new RequestIdentity(
            issuerUri.AbsoluteUri,
            subjects[0].Value,
            tenantAssertions.ToImmutableArray(),
            scopes,
            projectIds.SingleOrDefault()?.Value,
            runIds.SingleOrDefault()?.Value,
            purposes.SingleOrDefault()?.Value);
    }

    private static bool IsOpaqueIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private sealed record RequestIdentity(
        string Issuer,
        string Subject,
        ImmutableArray<string> TenantAssertions,
        ImmutableHashSet<string> Scopes,
        string? ProjectId,
        string? RunId,
        string? Purpose);
}
