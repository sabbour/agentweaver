using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class CoordinationIdentity
{
    public static CoordinationActor RequireActor(ClaimsPrincipal principal, string issuer)
    {
        var subjects = principal.FindAll("sub").Take(2).ToArray();
        if (principal.Identity?.IsAuthenticated != true ||
            subjects.Length != 1 || !Guid.TryParseExact(subjects[0].Value, "D", out _) ||
            !Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ||
            issuerUri.Scheme != Uri.UriSchemeHttps)
            throw new CoordinationException("caller_invalid", StatusCodes.Status403Forbidden);
        return new CoordinationActor(issuerUri.AbsoluteUri, subjects[0].Value);
    }

    public static CoordinationRunScope RequireRunScope(ClaimsPrincipal principal)
    {
        var projects = principal.FindAll("project_id").Take(2).ToArray();
        var runs = principal.FindAll("run_id").Take(2).ToArray();
        if (projects.Length != 1 || runs.Length != 1)
            throw new CoordinationException("run_binding_invalid", StatusCodes.Status403Forbidden);
        ValidateIdentity(projects[0].Value, "project_id");
        ValidateIdentity(runs[0].Value, "run_id");
        return new CoordinationRunScope(projects[0].Value, runs[0].Value);
    }

    public static void RequireScopes(ClaimsPrincipal principal)
    {
        var scopes = principal.FindAll("scope").Concat(principal.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (!scopes.Contains("api.read") || !scopes.Contains("projects.orchestrator"))
            throw new CoordinationException("caller_scope_invalid", StatusCodes.Status403Forbidden);
    }

    public static bool HasAudience(ClaimsPrincipal principal, string expectedAudience) =>
        principal.FindAll("aud").Any(claim =>
            string.Equals(claim.Value, expectedAudience, StringComparison.Ordinal));

    public static AuthenticationHeaderValue RequireBearer(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
            throw new CoordinationException("caller_bearer_invalid", StatusCodes.Status401Unauthorized);
        return authorization;
    }

    public static string? ReadTenantSelector(HttpContext context)
    {
        var selectors = context.Request.Headers["X-Agentweaver-Tenant"];
        if (selectors.Count == 0)
            return null;
        if (selectors.Count != 1 || !IsOpaqueIdentifier(selectors[0]))
            throw new CoordinationException("tenant_selector_invalid", StatusCodes.Status403Forbidden);
        return selectors[0]!;
    }

    public static string ClaimOwner(CoordinationActor actor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{actor.Issuer}\0{actor.Subject}")));

    public static void ValidateIdentity(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new CoordinationException($"{field}_invalid", StatusCodes.Status400BadRequest);
    }

    private static bool IsOpaqueIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
