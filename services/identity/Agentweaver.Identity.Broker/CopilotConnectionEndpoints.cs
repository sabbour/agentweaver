using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BeginCopilotConnectionRequest(ProjectAuthorityResourceType Scope, string ScopeId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompleteCopilotConnectionRequest(string State, string? Code, string? Error = null)
{
    public override string ToString() => nameof(CompleteCopilotConnectionRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeCopilotConnectionRequest(Guid ConnectionId, long ExpectedRevision);

public static class CopilotConnectionEndpoints
{
    public const string CallbackCookieName = "__Host-agentweaver-copilot-link";
    public static void MapCopilotConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/internal/connections/copilot-user")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
            });
        routes.MapPost("/begin", (HttpContext context, BeginCopilotConnectionRequest input) =>
            ExecuteAsync(context, (authority, actor, issuer, actorId) =>
                authority.BeginAsync(actor, issuer, actorId, input.Scope, input.ScopeId, context.RequestAborted)));
        routes.MapPost("/complete", (HttpContext context, CompleteCopilotConnectionRequest input) =>
            ExecuteAsync(context, (authority, actor, issuer, actorId) =>
                authority.CompleteAsync(actor, issuer, actorId, input.State, input.Code,
                    context.Request.Cookies[CallbackCookieName], input.Error, context.RequestAborted)));
        routes.MapPost("/refresh", (HttpContext context, ChangeCopilotConnectionRequest input) =>
            ExecuteAsync(context, (authority, actor, issuer, actorId) =>
                authority.RefreshAsync(actor, issuer, actorId, input.ConnectionId,
                    input.ExpectedRevision, context.RequestAborted)));
        routes.MapPost("/revoke", (HttpContext context, ChangeCopilotConnectionRequest input) =>
            ExecuteAsync(context, (authority, actor, issuer, actorId) =>
                authority.RevokeAsync(actor, issuer, actorId, input.ConnectionId,
                    input.ExpectedRevision, context.RequestAborted)));
        routes.MapGet("/{connectionId:guid}", (HttpContext context, Guid connectionId) =>
            ExecuteAsync(context, (authority, actor, issuer, actorId) =>
                authority.ReadStatusAsync(actor, issuer, actorId, connectionId, context.RequestAborted)));
    }

    private static async Task ExecuteAsync<T>(
        HttpContext context,
        Func<CopilotConnectionAuthority, RuntimeActorAuthorization, string, string, Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        SecretCredential? bearer = null;
        try
        {
            var subjectClaims = context.User.FindAll("sub").Take(2).ToArray();
            var expiry = context.User.GetExpirationDate();
            var header = context.Request.Headers.Authorization.ToString();
            var issuer = new Uri(context.RequestServices.GetRequiredService<IdentityBrokerOptions>().Issuer).AbsoluteUri;
            if (subjectClaims.Length != 1 ||
                !Guid.TryParseExact(subjectClaims[0].Value, "D", out _) || expiry is null ||
                !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                header.Length is <= 7 or > 16391 || header[7..].Any(char.IsWhiteSpace) ||
                context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader].Count > 1)
                throw new RuntimeAuthorizationException("copilot_connection_actor_invalid");
            var authority = context.RequestServices.GetService<CopilotConnectionAuthority>();
            if (authority is null)
            {
                await ErrorAsync(context, "copilot_connection_writer_unavailable", StatusCodes.Status503ServiceUnavailable);
                return;
            }
            bearer = new SecretCredential(header[7..], expiry.Value,
                context.RequestServices.GetRequiredService<TimeProvider>());
            var actor = new RuntimeActorAuthorization(bearer,
                context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader].SingleOrDefault());
            var result = await action(authority, actor, issuer, subjectClaims[0].Value);
            context.RequestAborted.ThrowIfCancellationRequested();
            if (result is CopilotConnectionStart started)
                context.Response.Cookies.Append(CallbackCookieName, started.CallbackCookie, new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/",
                    MaxAge = TimeSpan.FromMinutes(5), IsEssential = true
                });
            await Results.Json(result).ExecuteAsync(context);
        }
        catch (RuntimeAuthorizationException error)
        {
            await ErrorAsync(context, error.Code, StatusCodes.Status403Forbidden);
        }
        catch (AzureKeyVaultSecretException error)
        {
            await ErrorAsync(context,
                error.Failure == AzureKeyVaultSecretFailure.AccessDenied
                    ? "copilot_connection_writer_denied" : "copilot_connection_store_unavailable",
                error.Failure == AzureKeyVaultSecretFailure.AccessDenied
                    ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            await ErrorAsync(context, "copilot_connection_upstream_unavailable", StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException)
        {
            await ErrorAsync(context, "copilot_connection_request_invalid", StatusCodes.Status400BadRequest);
        }
        finally
        {
            bearer?.Invalidate();
        }
    }

    private static Task ErrorAsync(HttpContext context, string code, int status) =>
        Results.Json(new { error = code }, statusCode: status).ExecuteAsync(context);
}
