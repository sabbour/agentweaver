using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Knowledge;

public sealed class ProjectsConfigClient(
    HttpClient client,
    IHttpContextAccessor contextAccessor,
    KnowledgeRuntimeOptions options)
{
    private const string TenantSelectorHeader = "X-Agentweaver-Tenant";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<ProjectAuthorizationContextResponse> GetCurrentAuthorityAsync(
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/api/authorization/context",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Unauthorized)
            throw MissingAuthority();
        if (!response.IsSuccessStatusCode)
            throw MapOwnerFailure(response, "The current Projects authority could not be read.");

        ProjectAuthorizationContextResponse? authority;
        try
        {
            authority = await response.Content.ReadFromJsonAsync<ProjectAuthorizationContextResponse>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new KnowledgeApiException(
                "invalid_authority_response",
                "Projects & Config returned an invalid authorization-context contract.",
                StatusCodes.Status502BadGateway);
        }
        if (authority is null)
            throw new KnowledgeApiException(
                "invalid_authority_response",
                "Projects & Config returned an empty authorization-context contract.",
                StatusCodes.Status502BadGateway);

        ValidateAuthority(authority, contextAccessor.HttpContext?.User, projectId, runId, options);
        return authority;
    }

    public async Task<ProjectRunSelectionResponse> GetRunSelectionAsync(
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/selection",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw MissingRunSelectionAuthority();
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KnowledgeApiException(
                "run_selection_not_found",
                "The requested project run has no accepted Projects & Config selection.",
                StatusCodes.Status404NotFound);
        if (!response.IsSuccessStatusCode)
            throw MapOwnerFailure(response, "The current run provider selection could not be read.");

        ProjectRunSelectionResponse? selection;
        try
        {
            selection = await response.Content.ReadFromJsonAsync<ProjectRunSelectionResponse>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new KnowledgeApiException(
                "invalid_run_selection",
                "Projects & Config returned an invalid run-selection contract.",
                StatusCodes.Status502BadGateway);
        }
        if (selection is null ||
            !string.Equals(selection.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(selection.RunId, runId, StringComparison.Ordinal) ||
            selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 ||
            string.IsNullOrWhiteSpace(selection.ContextRevision) ||
            selection.Providers.IsDefault ||
            selection.RunLimits is null ||
            selection.RunLimits.MaxPromptTokens < 1)
            throw new KnowledgeApiException(
                "invalid_run_selection",
                "Projects & Config returned a mismatched or incomplete run-selection contract.",
                StatusCodes.Status502BadGateway);
        return selection;
    }

    public static void RequireProjectPermission(
        ProjectAuthorizationContextResponse authority,
        string projectId,
        ProjectAuthorizationPermission permission)
    {
        if (HasPermission(authority, ProjectAuthorityResourceType.Project, projectId, permission) ||
            HasPermission(authority, ProjectAuthorityResourceType.Tenant, authority.TenantId, permission))
            return;
        var action = permission == ProjectAuthorizationPermission.ReadProjects ? "read" : "write";
        throw new KnowledgeApiException(
            $"missing_effective_{permission.ToString().ToLowerInvariant()}",
            $"The caller has no current effective {permission} permission for this project and cannot {action} Knowledge records.",
            StatusCodes.Status403Forbidden);
    }

    public static (ProjectAuthorityResourceType ResourceType, string ResourceId, long RoleRevision)
        GetEffectiveProjectPermission(
            ProjectAuthorizationContextResponse authority,
            string projectId,
            ProjectAuthorizationPermission permission)
    {
        var projectGrants = FindGrants(
            authority, ProjectAuthorityResourceType.Project, projectId, permission);
        var grants = projectGrants.Length > 0
            ? projectGrants
            : FindGrants(authority, ProjectAuthorityResourceType.Tenant, authority.TenantId, permission);
        if (grants.Length != 1)
            throw new KnowledgeApiException(
                "invalid_authority_response",
                "Projects & Config returned ambiguous effective project authorization.",
                StatusCodes.Status502BadGateway);
        return (grants[0].ResourceType, grants[0].ResourceId, grants[0].Grant.RoleRevision);
    }

    public static void RequireRunSelectionPermission(
        ProjectAuthorizationContextResponse authority,
        string projectId)
    {
        if (HasPermission(
            authority,
            ProjectAuthorityResourceType.Project,
            projectId,
            ProjectAuthorizationPermission.ReadRunSelection))
            return;
        throw MissingRunSelectionAuthority();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var context = contextAccessor.HttpContext
            ?? throw new KnowledgeApiException(
                "caller_context_missing",
                "A validated caller context is required to contact Projects & Config.",
                StatusCodes.Status401Unauthorized);
        var authorizationValues = context.Request.Headers.Authorization;
        if (authorizationValues.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorizationValues[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            throw new KnowledgeApiException(
                "caller_token_missing",
                "A single validated bearer token is required to contact Projects & Config.",
                StatusCodes.Status401Unauthorized);

        var tenantValues = context.Request.Headers[TenantSelectorHeader];
        if (tenantValues.Count > 1 ||
            (tenantValues.Count == 1 && !IsOpaqueIdentifier(tenantValues[0])))
            throw new KnowledgeApiException(
                "invalid_tenant_selector",
                "The tenant selector must contain at most one valid tenant identifier.",
                StatusCodes.Status403Forbidden);

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorization.Parameter);
        if (tenantValues.Count == 1)
            request.Headers.TryAddWithoutValidation(TenantSelectorHeader, tenantValues[0]);

        try
        {
            return await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new KnowledgeApiException(
                "projects_authority_unavailable",
                "Projects & Config could not be reached to recheck current authority and provider selection.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static void ValidateAuthority(
        ProjectAuthorizationContextResponse authority,
        ClaimsPrincipal? principal,
        string projectId,
        string runId,
        KnowledgeRuntimeOptions options)
    {
        var identities = principal?.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray() ?? [];
        if (identities.Length != 1)
            throw InvalidAuthority();

        var claims = identities[0].Claims.ToArray();
        var subjects = claims.Where(claim => claim.Type == "sub").ToArray();
        var issuers = claims.Where(claim => claim.Type == "iss").ToArray();
        var projectClaims = claims.Where(claim => claim.Type == "project_id").ToArray();
        var runClaims = claims.Where(claim => claim.Type == "run_id").ToArray();
        var purposes = claims.Where(claim => claim.Type == "purpose").ToArray();
        var tenantAssertions = claims.Where(claim =>
                claim.Type is "tenant_id" or "tid" or "http://schemas.microsoft.com/identity/claims/tenantid")
            .Take(2)
            .Select(claim => claim.Value)
            .ToArray();
        if (subjects.Length != 1 || !IsOpaqueIdentifier(subjects[0].Value) ||
            !TryNormalizeIssuer(subjects[0].Issuer, out var callerIssuer) ||
            !string.Equals(callerIssuer, options.IdentityAuthority.AbsoluteUri, StringComparison.Ordinal) ||
            issuers.Length > 1 ||
            (issuers.Length == 1 && !HasIssuerValue(issuers[0].Value, callerIssuer)) ||
            projectClaims.Any(claim => !HasClaimIssuer(claim, callerIssuer)) ||
            runClaims.Any(claim => !HasClaimIssuer(claim, callerIssuer)) ||
            authority.ContractVersion != 1 ||
            !string.Equals(authority.Issuer, callerIssuer, StringComparison.Ordinal) ||
            !string.Equals(authority.ActorId, subjects[0].Value, StringComparison.Ordinal) ||
            projectClaims.Length > 1 ||
            runClaims.Length > 1 ||
            purposes.Length > 0 ||
            (runClaims.Length == 1 && projectClaims.Length != 1) ||
            projectClaims.Any(claim => !IsOpaqueIdentifier(claim.Value)) ||
            runClaims.Any(claim => !IsOpaqueIdentifier(claim.Value)) ||
            (projectClaims.Length == 0) != (authority.BoundProjectId is null) ||
            (runClaims.Length == 0) != (authority.BoundRunId is null) ||
            (projectClaims.Length == 1 &&
                !string.Equals(projectClaims[0].Value, authority.BoundProjectId, StringComparison.Ordinal)) ||
            (runClaims.Length == 1 &&
                !string.Equals(runClaims[0].Value, authority.BoundRunId, StringComparison.Ordinal)) ||
            !IsOpaqueIdentifier(authority.TenantId) ||
            authority.MembershipRevision < 1 ||
            (tenantAssertions.Length == 1 &&
                !string.Equals(tenantAssertions[0], authority.TenantId, StringComparison.Ordinal)) ||
            tenantAssertions.Length > 1 ||
            (authority.BoundProjectId is not null && !IsOpaqueIdentifier(authority.BoundProjectId)) ||
            (authority.BoundRunId is not null && !IsOpaqueIdentifier(authority.BoundRunId)) ||
            (authority.BoundRunId is not null && authority.BoundProjectId is null) ||
            (authority.BoundProjectId is not null &&
                !string.Equals(authority.BoundProjectId, projectId, StringComparison.Ordinal)) ||
            (authority.BoundRunId is not null &&
                !string.Equals(authority.BoundRunId, runId, StringComparison.Ordinal)) ||
            authority.EffectiveAuthority.IsDefault ||
            authority.EffectiveAuthority.Any(resource =>
                !Enum.IsDefined(resource.ResourceType) ||
                !IsOpaqueIdentifier(resource.ResourceId) ||
                resource.Permissions.IsDefault ||
                resource.Permissions.Any(permission =>
                    !Enum.IsDefined(permission.Permission) || permission.RoleRevision < 1)))
            throw InvalidAuthority();
    }

    private static KnowledgeApiException InvalidAuthority() =>
        new(
                "invalid_authority_response",
                "Projects & Config returned an authorization context that does not match the validated caller or requested resource.",
                StatusCodes.Status502BadGateway);

    private static bool HasPermission(
        ProjectAuthorizationContextResponse authority,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorizationPermission permission) =>
        FindGrants(authority, resourceType, resourceId, permission).Length > 0;

    private static (ProjectAuthorityResourceType ResourceType, string ResourceId,
        ProjectAuthorizationPermissionGrant Grant)[] FindGrants(
        ProjectAuthorizationContextResponse authority,
        ProjectAuthorityResourceType resourceType,
        string resourceId,
        ProjectAuthorizationPermission permission) =>
        authority.EffectiveAuthority
            .Where(resource =>
                resource.ResourceType == resourceType &&
                string.Equals(resource.ResourceId, resourceId, StringComparison.Ordinal))
            .SelectMany(resource => resource.Permissions
                .Where(grant => grant.Permission == permission)
                .Select(grant => (resource.ResourceType, resource.ResourceId, grant)))
            .ToArray();

    private static KnowledgeApiException MapOwnerFailure(HttpResponseMessage response, string message)
    {
        if ((int)response.StatusCode is >= 500 or 0)
            return new KnowledgeApiException(
                "projects_authority_unavailable", message, StatusCodes.Status503ServiceUnavailable);
        if ((int)response.StatusCode is >= 300 and < 400)
            return new KnowledgeApiException(
                "projects_redirect_rejected",
                "Projects & Config returned a redirect, which Knowledge will not follow.",
                StatusCodes.Status502BadGateway);
        return new KnowledgeApiException(
            "projects_request_rejected",
            "Projects & Config rejected the current caller or project/run selection.",
            (int)response.StatusCode);
    }

    private static KnowledgeApiException MissingAuthority() =>
        new(
            "missing_current_project_authority",
            "Projects & Config did not confirm a current membership and effective project permission for this caller.",
            StatusCodes.Status403Forbidden);

    private static KnowledgeApiException MissingRunSelectionAuthority() =>
        new(
            "missing_effective_read_run_selection",
            "The caller has no current effective ReadRunSelection permission for this project/run.",
            StatusCodes.Status403Forbidden);

    private static bool IsOpaqueIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool HasClaimIssuer(Claim claim, string expectedIssuer) =>
        TryNormalizeIssuer(claim.Issuer, out var issuer) &&
        string.Equals(issuer, expectedIssuer, StringComparison.Ordinal);

    private static bool HasIssuerValue(string? value, string expectedIssuer) =>
        TryNormalizeIssuer(value, out var issuer) &&
        string.Equals(issuer, expectedIssuer, StringComparison.Ordinal);

    private static bool TryNormalizeIssuer(string? value, out string issuer)
    {
        issuer = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        issuer = uri.AbsoluteUri;
        return true;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
