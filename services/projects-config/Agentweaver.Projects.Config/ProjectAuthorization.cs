using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Projects.Config;

public sealed record ProjectsConfigIdentityOptions(string Issuer);

public sealed record ProjectAuthorizationGrant(
    Guid AssignmentId,
    ProjectAuthorityResourceType ResourceType,
    string ResourceId,
    ProjectAuthorityRole Role,
    long Revision);

public sealed class ProjectAuthorizationContext
{
    public const int CurrentContractVersion = ProjectAuthorizationContextContract.CurrentVersion;

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

    public void RequireExactRunBinding(string projectId, string runId)
    {
        if (Purpose is not null ||
            !string.Equals(BoundProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(BoundRunId, runId, StringComparison.Ordinal))
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
                            Add(grant, ProjectAuthorizationPermission.AccessPrivateKnowledge);
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
                        {
                            Add(
                                grant,
                                ProjectAuthorizationPermission.WriteProjects,
                                ProjectAuthorityResourceType.Project,
                                BoundProjectId);
                            Add(
                                grant,
                                ProjectAuthorizationPermission.AccessPrivateKnowledge,
                                ProjectAuthorityResourceType.Project,
                                BoundProjectId);
                        }
                    }
                    break;
                case (ProjectAuthorityResourceType.Project, ProjectAuthorityRole.Owner):
                    if (BoundRunId is null)
                        Add(grant, ProjectAuthorizationPermission.ReadProjects);
                    if (adminScope)
                    {
                        Add(grant, ProjectAuthorizationPermission.AccessPrivateKnowledge);
                        if (BoundRunId is null)
                            Add(grant, ProjectAuthorizationPermission.WriteProjects);
                    }
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
    public const string TenantSelectorHeader = ProjectAuthorizationContextContract.TenantSelectorHeader;
    public const string ApiReadScope = "api.read";
    public const string ProjectAdminScope = "projects.admin";
    public const string OrchestratorScope = "projects.orchestrator";
    public const string PlatformResourceId = "default";
    public const string BrokerRunBindingAuthenticationScheme = "ProjectsBrokerRunBinding";
    public const string BrokerRunBindingAuthorizationPolicy = "ProjectsBrokerRunBinding";
    public const string BrokerRunBindingTokenType = "agentweaver-run-binding-query+jwt";
    public const string BrokerRunBindingPurpose = "run-binding-query";
    public const string BrokerRunBindingServiceSubject = "identity-broker";

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

    public async Task<ProjectAuthorizationContext> ResolveBrokerRunBindingAsync(
        ClaimsPrincipal principal,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            !TryGetSingleClaim(principal, "sub", out var serviceSubject) ||
            serviceSubject != BrokerRunBindingServiceSubject ||
            !TryGetSingleClaim(principal, "actor_iss", out var actorIssuer) ||
            actorIssuer != identityOptions.Issuer ||
            !TryGetSingleClaim(principal, "actor_sub", out var actorSubject) ||
            !Guid.TryParseExact(actorSubject, "D", out var actorId) ||
            actorId == Guid.Empty ||
            actorSubject != actorId.ToString("D") ||
            !TryGetSingleClaim(principal, "project_id", out var tokenProjectId) ||
            tokenProjectId != projectId ||
            !TryGetSingleClaim(principal, "run_id", out var tokenRunId) ||
            tokenRunId != runId ||
            !TryGetSingleClaim(principal, "purpose", out var purpose) ||
            purpose != BrokerRunBindingPurpose ||
            !TryGetSingleClaim(principal, "jti", out var tokenId) ||
            !Guid.TryParseExact(tokenId, "N", out var parsedTokenId) ||
            parsedTokenId == Guid.Empty ||
            principal.Claims.Any(claim => claim.Type is "tenant_id" or "tid" or "scope" or "scp" or
                "role" or "roles" or ClaimTypes.Role))
            throw ProjectConfigException.Forbidden();

        if (!Guid.TryParseExact(projectId, "N", out _) || !IsOpaqueIdentifier(runId))
            throw ProjectConfigException.NotFound();
        var project = await db.Projects.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null || project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.NotFound();

        var actor = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", actorSubject),
            new Claim("tenant_id", project.TenantId),
            new Claim("scope", $"{ApiReadScope} {OrchestratorScope}"),
            new Claim("project_id", projectId),
            new Claim("run_id", runId),
        ], authenticationType: "BrokerRunBinding"));
        return await ResolveAsync(actor, [project.TenantId], cancellationToken).ConfigureAwait(false);
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

    private static bool TryGetSingleClaim(ClaimsPrincipal principal, string type, out string value)
    {
        var claims = principal.FindAll(type).Take(2).ToArray();
        value = claims.Length == 1 ? claims[0].Value : string.Empty;
        return claims.Length == 1;
    }

    private sealed record RequestIdentity(
        string Issuer,
        string Subject,
        ImmutableArray<string> TenantAssertions,
        ImmutableHashSet<string> Scopes,
        string? ProjectId,
        string? RunId,
        string? Purpose);
}

internal static class BrokerRunBindingTokenValidation
{
    private const long MaximumLifetimeSeconds = 30;
    private const long ClockSkewSeconds = 5;
    private const long MaximumAgeSeconds = MaximumLifetimeSeconds + ClockSkewSeconds;

    public static JwtBearerEvents CreateEvents(TimeProvider timeProvider) => new()
    {
        OnTokenValidated = context =>
        {
            var now = timeProvider.GetUtcNow();
            var valid = context.SecurityToken switch
            {
                JwtSecurityToken token => HasValidTimeProfile(token.Payload, now),
                JsonWebToken token => HasValidTimeProfile(token.EncodedPayload, now),
                _ => false,
            };
            if (!valid)
                context.Fail("The Broker run-binding ticket lifetime is invalid.");
            return Task.CompletedTask;
        },
    };

    internal static bool HasValidTimeProfile(JwtPayload payload, DateTimeOffset now)
    {
        if (!TryReadNumericDate(payload, "iat", out var issuedAt) ||
            !TryReadNumericDate(payload, "nbf", out var notBefore) ||
            !TryReadNumericDate(payload, "exp", out var expires))
            return false;

        return HasValidTimeProfile(issuedAt, notBefore, expires, now);
    }

    private static bool HasValidTimeProfile(string encodedPayload, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(encodedPayload));
        var payload = document.RootElement;
        if (!TryReadNumericDate(payload, "iat", out var issuedAt) ||
            !TryReadNumericDate(payload, "nbf", out var notBefore) ||
            !TryReadNumericDate(payload, "exp", out var expires))
            return false;

        return HasValidTimeProfile(issuedAt, notBefore, expires, now);
    }

    private static bool HasValidTimeProfile(
        long issuedAt,
        long notBefore,
        long expires,
        DateTimeOffset now)
    {
        var nowSeconds = now.ToUnixTimeSeconds();
        return issuedAt <= nowSeconds + ClockSkewSeconds &&
            issuedAt >= nowSeconds - MaximumAgeSeconds &&
            notBefore <= issuedAt &&
            notBefore >= issuedAt - ClockSkewSeconds &&
            expires > issuedAt &&
            expires <= issuedAt + MaximumLifetimeSeconds &&
            expires > nowSeconds - ClockSkewSeconds;
    }

    private static bool TryReadNumericDate(JsonElement payload, string name, out long value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var raw) &&
            raw.ValueKind == JsonValueKind.Number &&
            raw.TryGetInt64(out value) &&
            value >= 0;
    }

    private static bool TryReadNumericDate(JwtPayload payload, string name, out long value)
    {
        value = 0;
        if (!payload.TryGetValue(name, out var raw))
            return false;
        switch (raw)
        {
            case long number when number >= 0:
                value = number;
                return true;
            case int number when number >= 0:
                value = number;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number):
                value = number;
                return number >= 0;
            default:
                return false;
        }
    }

}
