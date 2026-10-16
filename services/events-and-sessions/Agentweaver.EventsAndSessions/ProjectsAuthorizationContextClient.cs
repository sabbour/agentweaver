using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.EventsAndSessions;

public sealed record ProjectsAuthorizationContextOptions(
    string? OwnerBaseAddress,
    string? Audience,
    string? ExpectedIssuer);

public sealed record ProjectsAuthorizationPermissionGrant(string Permission, long RoleRevision);

public sealed record ProjectsEffectiveAuthority(
    string ResourceType,
    string ResourceId,
    ImmutableArray<ProjectsAuthorizationPermissionGrant> Permissions);

public sealed record ProjectsAuthorizationContextResponse(
    int ContractVersion,
    string Issuer,
    string ActorId,
    string TenantId,
    long MembershipRevision,
    string? BoundProjectId,
    string? BoundRunId,
    ImmutableArray<ProjectsEffectiveAuthority> EffectiveAuthority);

public sealed class ProjectsAuthorizationContextException(
    string code,
    HttpStatusCode statusCode,
    Exception? innerException = null)
    : Exception(code, innerException)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public interface IProjectsAuthorizationContextClient
{
    Task<ProjectsAuthorizationContextResponse> GetCurrentAsync(
        HttpContext context,
        CancellationToken cancellationToken = default);
}

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

    public async Task<ProjectsAuthorizationContextResponse> GetCurrentAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var ownerUri = RequireOwnerUri(options);
        var scope = RequireCaller(context, options.Audience);
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
