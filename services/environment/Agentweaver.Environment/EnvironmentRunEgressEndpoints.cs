using System.Globalization;
using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Agentweaver.Environment;

/// <summary>Maps the Environment read-only stored run egress endpoint.</summary>
internal static class EnvironmentRunEgressEndpoints
{
    public static IApplicationBuilder UseRunEgressNoStore(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsRunEgressPath(context.Request.Path))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
            }
            await next(context).ConfigureAwait(false);
        });

    private static bool IsRunEgressPath(PathString path)
    {
        var value = path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(value))
            return false;
        var segments = value.Split('/', StringSplitOptions.None);
        return segments.Length == 7 &&
            string.IsNullOrEmpty(segments[0]) &&
            string.Equals(segments[1], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[2], "projects", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(segments[3]) &&
            string.Equals(segments[4], "runs", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(segments[5]) &&
            string.Equals(segments[6], "egress", StringComparison.OrdinalIgnoreCase);
    }

    public static IEndpointRouteBuilder MapRunEgressEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/projects/{projectId}/runs/{runId}/egress",
            ReadRunEgressAsync).RequireAuthorization();
        return endpoints;
    }

    internal static async Task<IResult> ReadRunEgressAsync(
        string projectId,
        string runId,
        HttpContext context,
        IProjectsConfigClient projects,
        IEnvironmentEgressAuditStore auditStore,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";

        if (await HasRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false))
            return BadRequest("run_egress_query_invalid");

        if (!IsOpaqueId(projectId) || !IsOpaqueId(runId))
            return BadRequest("run_egress_query_invalid");

        if (!TryReadPageQuery(context.Request.Query, out var pageSize, out var cursor, out var queryErrorCode))
            return BadRequest(queryErrorCode!);

        if (!TryReadSingleTenantCaller(context, out var caller, out var callerErrorStatus, out var callerErrorCode))
            return Results.Json(new { code = callerErrorCode }, statusCode: callerErrorStatus);

        var subjects = context.User.FindAll("sub").Take(2).ToArray();
        if (subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out _))
            return Results.Json(new { code = "caller_invalid" }, statusCode: StatusCodes.Status403Forbidden);
        var subject = subjects[0].Value;

        var issuers = context.User.FindAll("iss").Take(2).ToArray();
        Uri? tokenIssuerUri = null;
        if (issuers.Length > 1 ||
            (issuers.Length == 1 && (!Uri.TryCreate(issuers[0].Value, UriKind.Absolute, out tokenIssuerUri) ||
                tokenIssuerUri.Scheme != Uri.UriSchemeHttps)))
            return Results.Json(new { code = "caller_invalid" }, statusCode: StatusCodes.Status403Forbidden);

        if (context.User.HasClaim(claim => claim.Type == "purpose"))
            return Forbidden("run_egress_permission_denied");

        var boundTenants = context.User.FindAll("tenant_id")
            .Concat(context.User.FindAll("tid"))
            .Concat(context.User.FindAll("http://schemas.microsoft.com/identity/claims/tenantid"))
            .Take(2)
            .ToArray();
        var boundProjects = context.User.FindAll("project_id").Take(2).ToArray();
        var boundRuns = context.User.FindAll("run_id").Take(2).ToArray();
        if (boundTenants.Length > 1 ||
            (boundTenants.Length == 1 && (!IsOpaqueId(boundTenants[0].Value) ||
                (caller!.TenantSelector is not null &&
                 !string.Equals(boundTenants[0].Value, caller.TenantSelector, StringComparison.Ordinal)))) ||
            boundProjects.Length > 1 ||
            (boundProjects.Length == 1 && (!IsOpaqueId(boundProjects[0].Value) ||
                !string.Equals(boundProjects[0].Value, projectId, StringComparison.Ordinal))) ||
            boundRuns.Length > 1 ||
            (boundRuns.Length == 1 && (boundProjects.Length != 1 ||
                !IsOpaqueId(boundRuns[0].Value) ||
                !string.Equals(boundRuns[0].Value, runId, StringComparison.Ordinal))))
            return Forbidden("run_egress_permission_denied");

        var scopes = context.User.FindAll("scope")
            .Concat(context.User.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains("api.read"))
            return Forbidden("run_egress_permission_denied");

        var boundTenantId = boundTenants.Length == 1 ? boundTenants[0].Value : null;
        var boundProjectId = boundProjects.Length == 1 ? boundProjects[0].Value : null;
        var boundRunId = boundRuns.Length == 1 ? boundRuns[0].Value : null;

        try
        {
            var authority = await projects.GetAuthorizationContextAsync(caller!, cancellationToken)
                .ConfigureAwait(false);
            if (!IsAuthorized(
                    authority,
                    subject,
                    tokenIssuerUri,
                    caller!.TenantSelector,
                    boundTenantId,
                    boundProjectId,
                    boundRunId,
                    projectId,
                    runId,
                    scopes))
                return Forbidden("run_egress_permission_denied");

            var page = await auditStore.ReadRunEgressAsync(
                authority.TenantId,
                projectId,
                runId,
                pageSize,
                cursor,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(page);
        }
        catch (EnvironmentEgressQueryException exception)
        {
            return BadRequest(exception.Code);
        }
        catch (ProjectsConfigApiException exception) when (exception.Code is
            "authorization_context_denied" or "tenant_selector_mismatch")
        {
            return Forbidden(exception.Code);
        }
        catch (ProjectsConfigApiException)
        {
            return Unavailable();
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (NpgsqlException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    private static bool IsAuthorized(
        ProjectAuthorizationContextResponse authority,
        string subject,
        Uri? tokenIssuerUri,
        string? tenantSelector,
        string? boundTenantId,
        string? boundProjectId,
        string? boundRunId,
        string projectId,
        string runId,
        HashSet<string> scopes)
    {
        if (authority.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            authority.MembershipRevision < 1 ||
            !IsOpaqueId(authority.TenantId) ||
            authority.EffectiveAuthority.IsDefault)
            return false;
        if (!string.Equals(authority.ActorId, subject, StringComparison.Ordinal))
            return false;
        if (tokenIssuerUri is not null &&
            (!Uri.TryCreate(authority.Issuer, UriKind.Absolute, out var authorityIssuerUri) ||
             authorityIssuerUri != tokenIssuerUri))
            return false;
        if (tenantSelector is not null &&
            !string.Equals(authority.TenantId, tenantSelector, StringComparison.Ordinal))
            return false;
        if (boundTenantId is not null &&
            !string.Equals(authority.TenantId, boundTenantId, StringComparison.Ordinal))
            return false;
        if (!string.Equals(authority.BoundProjectId, boundProjectId, StringComparison.Ordinal) ||
            !string.Equals(authority.BoundRunId, boundRunId, StringComparison.Ordinal))
            return false;
        if (authority.BoundProjectId is not null &&
            !string.Equals(authority.BoundProjectId, projectId, StringComparison.Ordinal))
            return false;
        if (authority.BoundRunId is not null &&
            (!string.Equals(authority.BoundRunId, runId, StringComparison.Ordinal) ||
             authority.BoundProjectId is null))
            return false;

        var isPlatformAdmin =
            scopes.Contains("api.read") &&
            scopes.Contains("projects.admin") &&
            boundTenantId is null &&
            boundProjectId is null &&
            boundRunId is null &&
            authority.BoundProjectId is null &&
            authority.BoundRunId is null &&
            authority.EffectiveAuthority.Any(resource =>
                resource.ResourceType == ProjectAuthorityResourceType.Platform &&
                string.Equals(resource.ResourceId, "default", StringComparison.Ordinal) &&
                !resource.Permissions.IsDefault &&
                resource.Permissions.Any(permission =>
                    permission.Permission == ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults &&
                    permission.RoleRevision > 0));
        if (isPlatformAdmin)
            return true;

        return scopes.Contains("api.read") &&
            authority.EffectiveAuthority.Any(resource =>
                !resource.Permissions.IsDefault &&
                ((resource.ResourceType == ProjectAuthorityResourceType.Project &&
                  string.Equals(resource.ResourceId, projectId, StringComparison.Ordinal)) ||
                 (resource.ResourceType == ProjectAuthorityResourceType.Tenant &&
                  string.Equals(resource.ResourceId, authority.TenantId, StringComparison.Ordinal))) &&
                resource.Permissions.Any(permission =>
                    permission.Permission == ProjectAuthorizationPermission.ReadProjects &&
                    permission.RoleRevision > 0));
    }

    private static bool TryReadSingleTenantCaller(
        HttpContext context,
        out CurrentCallerRequest? caller,
        out int errorStatus,
        out string errorCode)
    {
        caller = null;
        errorStatus = StatusCodes.Status401Unauthorized;
        errorCode = "caller_bearer_invalid";

        var authHeaders = context.Request.Headers.Authorization;
        if (context.User.Identity?.IsAuthenticated != true ||
            authHeaders.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authHeaders[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            return false;

        var tenantValues = context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader];
        string? tenantSelector = null;
        if (tenantValues.Count > 1)
        {
            errorStatus = StatusCodes.Status403Forbidden;
            errorCode = "tenant_selector_invalid";
            return false;
        }
        if (tenantValues.Count == 1)
        {
            if (!IsOpaqueId(tenantValues[0]))
            {
                errorStatus = StatusCodes.Status403Forbidden;
                errorCode = "tenant_selector_invalid";
                return false;
            }
            tenantSelector = tenantValues[0];
        }

        try
        {
            caller = new CurrentCallerRequest(authorization.Parameter, tenantSelector);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadPageQuery(
        IQueryCollection query,
        out int pageSize,
        out string? cursor,
        out string? errorCode)
    {
        pageSize = RunEgressContract.DefaultPageSize;
        cursor = null;
        errorCode = null;

        if (query.Keys.Any(key => key is not ("limit" or "cursor")) ||
            query.Any(pair => pair.Value.Count != 1))
        {
            errorCode = "run_egress_query_invalid";
            return false;
        }

        if (query.TryGetValue("limit", out var rawLimit))
        {
            if (!int.TryParse(rawLimit[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed is < 1 or > RunEgressContract.MaximumPageSize)
            {
                errorCode = "run_egress_query_invalid";
                return false;
            }
            pageSize = parsed;
        }

        if (query.TryGetValue("cursor", out var rawCursor))
        {
            var value = rawCursor[0];
            if (string.IsNullOrWhiteSpace(value) || value.Length > RunEgressContract.MaximumCursorLength)
            {
                errorCode = "run_egress_cursor_invalid";
                return false;
            }
            cursor = value;
        }

        return true;
    }

    private static async Task<bool> HasRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            return true;
        if (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true)
            return false;
        var probe = new byte[1];
        return await request.Body.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0;
    }

    private static bool IsOpaqueId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static IResult BadRequest(string code) =>
        Results.Json(new { code }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult Forbidden(string code) =>
        Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden);

    private static IResult Unavailable() =>
        Results.Json(new { code = "run_egress_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}
