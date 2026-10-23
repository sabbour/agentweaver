using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.EventsAndSessions;

/// <summary>Owner endpoint and expected token identity for live project authorization reads.</summary>
public sealed record ProjectsAuthorizationContextOptions(
    string? OwnerBaseAddress,
    string? Audience,
    string? ExpectedIssuer);

/// <summary>A permission granted at a specific role revision.</summary>
public sealed record ProjectsAuthorizationPermissionGrant(string Permission, long RoleRevision);

/// <summary>Effective permissions for one resource in the current tenant context.</summary>
public sealed record ProjectsEffectiveAuthority(
    string ResourceType,
    string ResourceId,
    ImmutableArray<ProjectsAuthorizationPermissionGrant> Permissions);

/// <summary>Current actor, tenant, bound run, and effective resource authority from the Projects owner.</summary>
public sealed record ProjectsAuthorizationContextResponse(
    int ContractVersion,
    string Issuer,
    string ActorId,
    string TenantId,
    long MembershipRevision,
    string? BoundProjectId,
    string? BoundRunId,
    ImmutableArray<ProjectsEffectiveAuthority> EffectiveAuthority);

/// <summary>Represents a rejected, unavailable, or invalid live authorization context response.</summary>
public sealed class ProjectsAuthorizationContextException(
    string code,
    HttpStatusCode statusCode,
    Exception? innerException = null)
    : Exception(code, innerException)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>Reads the caller's current authoritative project and run permissions.</summary>
public interface IProjectsAuthorizationContextClient
{
    /// <summary>Fetches and validates live authority against the authenticated caller and bound run.</summary>
    Task<ProjectsAuthorizationContextResponse> GetCurrentAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches fresh authority for a project-scoped read without widening a run-bound caller.</summary>
    Task<ProjectsAuthorizationContextResponse> GetProjectReadAsync(
        HttpContext context,
        string projectId,
        string ownerAudience,
        CancellationToken cancellationToken);
}

/// <summary>Calls the Projects owner without redirects and validates its uncached authorization contract.</summary>
public sealed class ProjectsAuthorizationContextClient(
    HttpClient httpClient,
    ProjectsAuthorizationContextOptions options) : IProjectsAuthorizationContextClient
{
    private const string TenantSelectorHeader = "X-Agentweaver-Tenant";
    private const int CurrentContractVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Forwards the caller token and tenant selector, then checks the owner context's identity and scope.</summary>
    public Task<ProjectsAuthorizationContextResponse> GetCurrentAsync(
        HttpContext context,
        CancellationToken cancellationToken = default) =>
        GetContextAsync(context, null, cancellationToken);

    /// <summary>Reads fresh project authority for unbound, project-bound, or run-bound api.read callers.</summary>
    public async Task<ProjectsAuthorizationContextResponse> GetProjectReadAsync(
        HttpContext context, string projectId, string ownerAudience, CancellationToken cancellationToken)
    {
        if (!IsOpaqueIdentifier(projectId))
            throw new ArgumentException("project_id_invalid");
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains("api.read", StringComparer.Ordinal) ||
            !context.User.FindAll("aud").Any(claim => claim.Value == ownerAudience))
            throw new ProjectsAuthorizationContextException("caller_scope_invalid", HttpStatusCode.Forbidden);
        var current = await GetContextAsync(context, projectId, cancellationToken).ConfigureAwait(false);
        if (!current.EffectiveAuthority.Any(authority => !authority.Permissions.IsDefault &&
                authority.Permissions.Any(permission => permission.RoleRevision > 0 &&
                    (authority.ResourceType == "project" && authority.ResourceId == projectId &&
                        permission.Permission is "readProjects" or "readRunSelection" ||
                     authority.ResourceType == "tenant" && authority.ResourceId == current.TenantId &&
                        permission.Permission == "readProjects"))))
            throw new ProjectsAuthorizationContextException("project_read_permission_denied", HttpStatusCode.Forbidden);
        if (current.EffectiveAuthority.Any(authority =>
                authority.ResourceType == "tenant" && authority.ResourceId == current.TenantId &&
                !authority.Permissions.IsDefault && authority.Permissions.Any(permission =>
                    permission.RoleRevision > 0 && permission.Permission == "readProjects")))
            await RequireProjectInTenantAsync(context, projectId, current.TenantId, cancellationToken)
                .ConfigureAwait(false);
        return current;
    }

    private async Task RequireProjectInTenantAsync(
        HttpContext context,
        string projectId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        // Tenant-wide read permission is not proof that the requested project belongs to this tenant.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(RequireOwnerUri(options), $"/api/projects/{Uri.EscapeDataString(projectId)}"));
        request.Headers.Authorization = RequireBearer(context);
        request.Headers.TryAddWithoutValidation(TenantSelectorHeader, tenantId);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ProjectsAuthorizationContextException(
                "projects_authorization_unavailable", HttpStatusCode.BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw new ProjectsAuthorizationContextException(
                    "project_read_permission_denied", HttpStatusCode.Forbidden);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ProjectsAuthorizationContextException(
                    response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest
                        ? "projects_authorization_redirect_rejected"
                        : "projects_authorization_unavailable",
                    HttpStatusCode.BadGateway);
            if (response.Headers.CacheControl?.NoStore != true)
                throw new ProjectsAuthorizationContextException(
                    "projects_authorization_cache_policy_invalid", HttpStatusCode.BadGateway);

            try
            {
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("projectId", out var project) ||
                    project.ValueKind != JsonValueKind.String ||
                    !string.Equals(project.GetString(), projectId, StringComparison.Ordinal))
                    throw new ProjectsAuthorizationContextException(
                        "projects_authorization_contract_invalid", HttpStatusCode.BadGateway);
            }
            catch (JsonException exception)
            {
                throw new ProjectsAuthorizationContextException(
                    "projects_authorization_contract_invalid", HttpStatusCode.BadGateway, exception);
            }
        }
    }

    /// <summary>Validates the uncached Projects response against either exact run scope or optional read bindings.</summary>
    private async Task<ProjectsAuthorizationContextResponse> GetContextAsync(
        HttpContext context, string? readProjectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var ownerUri = RequireOwnerUri(options);
        (string? ProjectId, string? RunId) scope;
        if (readProjectId is null)
        {
            var runScope = RequireCaller(context, options.Audience);
            scope = (runScope.ProjectId, runScope.RunId);
        }
        else
            scope = RequireProjectReadCaller(context, readProjectId, options.Audience);
        var bearer = RequireBearer(context);
        var tenant = ReadTenantSelector(context);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(ownerUri, "/api/authorization/context"));
        request.Headers.Authorization = bearer;
        if (tenant is not null)
            request.Headers.TryAddWithoutValidation(TenantSelectorHeader, tenant);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new ProjectsAuthorizationContextException(
                "projects_authorization_unavailable", HttpStatusCode.BadGateway, exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProjectsAuthorizationContextException(
                    "projects_authorization_denied", HttpStatusCode.Forbidden);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ProjectsAuthorizationContextException(
                    response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest
                        ? "projects_authorization_redirect_rejected"
                        : "projects_authorization_unavailable",
                    HttpStatusCode.BadGateway);
            if (response.Headers.CacheControl?.NoStore != true)
                throw new ProjectsAuthorizationContextException(
                    "projects_authorization_cache_policy_invalid", HttpStatusCode.BadGateway);

            ProjectsAuthorizationContextResponse? ownerContext;
            try
            {
                ownerContext = await response.Content.ReadFromJsonAsync<ProjectsAuthorizationContextResponse>(
                    JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new ProjectsAuthorizationContextException(
                    "projects_authorization_contract_invalid", HttpStatusCode.BadGateway, exception);
            }

            if (ownerContext is null ||
                ownerContext.ContractVersion != CurrentContractVersion ||
                !string.Equals(ownerContext.Issuer, options.ExpectedIssuer, StringComparison.Ordinal) ||
                !string.Equals(ownerContext.ActorId, RequireSubject(context.User), StringComparison.Ordinal) ||
                !string.Equals(ownerContext.BoundProjectId, scope.ProjectId, StringComparison.Ordinal) ||
                !string.Equals(ownerContext.BoundRunId, scope.RunId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(ownerContext.TenantId) ||
                ownerContext.MembershipRevision < 1 ||
                ownerContext.EffectiveAuthority.IsDefault ||
                (tenant is not null && !string.Equals(ownerContext.TenantId, tenant, StringComparison.Ordinal)))
                throw new ProjectsAuthorizationContextException(
                    "projects_authorization_context_mismatch", HttpStatusCode.Forbidden);

            return ownerContext;
        }
    }

    private static Uri RequireOwnerUri(ProjectsAuthorizationContextOptions options)
    {
        if (options is null ||
            !Uri.TryCreate(options.OwnerBaseAddress, UriKind.Absolute, out var ownerUri) ||
            ownerUri.Scheme != Uri.UriSchemeHttps ||
            ownerUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(ownerUri.Query) ||
            !string.IsNullOrEmpty(ownerUri.Fragment) ||
            !string.IsNullOrEmpty(ownerUri.UserInfo) ||
            string.IsNullOrWhiteSpace(options.Audience) ||
            string.IsNullOrWhiteSpace(options.ExpectedIssuer) ||
            !Uri.TryCreate(options.ExpectedIssuer, UriKind.Absolute, out var issuerUri) ||
            issuerUri.Scheme != Uri.UriSchemeHttps)
            throw new ProjectsAuthorizationContextException(
                "projects_authorization_configuration_invalid", HttpStatusCode.ServiceUnavailable);
        return ownerUri;
    }

    private static SessionRunScope RequireCaller(HttpContext context, string? expectedAudience)
    {
        if (!SessionIdentityClaims.TryGetScope(context.User, out var scope) || scope is null ||
            string.IsNullOrWhiteSpace(expectedAudience) ||
            !context.User.FindAll("aud").Any(claim =>
                string.Equals(claim.Value, expectedAudience, StringComparison.Ordinal)))
            throw new ProjectsAuthorizationContextException(
                "projects_authorization_caller_invalid", HttpStatusCode.Forbidden);
        return scope.Value;
    }

    /// <summary>Validates optional project/run bindings without relaxing the existing run-only caller path.</summary>
    private static (string? ProjectId, string? RunId) RequireProjectReadCaller(
        HttpContext context, string projectId, string? audience)
    {
        var subjects = context.User.FindAll("sub").Take(2).ToArray();
        var projects = context.User.FindAll("project_id").Take(2).ToArray();
        var runs = context.User.FindAll("run_id").Take(2).ToArray();
        if (context.User.Identity?.IsAuthenticated != true ||
            subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out _) ||
            !context.User.FindAll("aud").Any(claim => claim.Value == audience) ||
            projects.Length > 1 || runs.Length > 1 ||
            projects.Length == 1 && projects[0].Value != projectId ||
            runs.Length == 1 && (projects.Length == 0 || !IsOpaqueIdentifier(runs[0].Value)))
            throw new ProjectsAuthorizationContextException("caller_binding_mismatch", HttpStatusCode.Forbidden);
        return (projects.SingleOrDefault()?.Value, runs.SingleOrDefault()?.Value);
    }

    private static AuthenticationHeaderValue RequireBearer(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            throw new ProjectsAuthorizationContextException(
                "projects_authorization_caller_invalid", HttpStatusCode.Unauthorized);
        return authorization;
    }

    private static string? ReadTenantSelector(HttpContext context)
    {
        var selectors = context.Request.Headers[TenantSelectorHeader];
        if (selectors.Count == 0)
            return null;
        if (selectors.Count != 1 || !IsOpaqueIdentifier(selectors[0]))
            throw new ProjectsAuthorizationContextException(
                "projects_authorization_tenant_invalid", HttpStatusCode.Forbidden);
        return selectors[0];
    }

    private static string RequireSubject(System.Security.Claims.ClaimsPrincipal principal)
    {
        var subjects = principal.FindAll("sub").Take(2).ToArray();
        return subjects.Length == 1
            ? subjects[0].Value
            : throw new ProjectsAuthorizationContextException(
                "projects_authorization_caller_invalid", HttpStatusCode.Forbidden);
    }

    private static bool IsOpaqueIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
