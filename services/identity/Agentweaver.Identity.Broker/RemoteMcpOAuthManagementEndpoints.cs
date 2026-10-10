using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker;

public static class RemoteMcpOAuthManagementEndpoints
{
    public static void MapRemoteMcpOAuthManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/internal/connections/remote-mcp")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
            });
        routes.MapGet("/{connectionId:guid}", (HttpContext context, Guid connectionId) =>
            ExecuteAsync(context, (service, actor, issuer, actorId) =>
                service.ReadStatusAsync(actor, issuer, actorId, connectionId, context.RequestAborted)));
        routes.MapPost("/{connectionId:guid}/disconnect",
            (HttpContext context, Guid connectionId, RemoteMcpOAuthDisconnectRequest input) =>
                ExecuteAsync(context, (service, actor, issuer, actorId) =>
                    service.DisconnectAsync(actor, issuer, actorId, connectionId, input,
                        context.RequestAborted)));
    }

    private static async Task ExecuteAsync<T>(
        HttpContext context,
        Func<RemoteMcpOAuthManagementService, RuntimeActorAuthorization, string, string, Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        SecretCredential? bearer = null;
        try
        {
            var subjectClaims = context.User.FindAll("sub").Take(2).ToArray();
            var expiry = context.User.GetExpirationDate();
            var authorization = context.Request.Headers.Authorization;
            var header = authorization.ToString();
            var issuer = context.RequestServices.GetRequiredService<IdentityBrokerOptions>().Issuer;
            if (subjectClaims.Length != 1 ||
                !Guid.TryParseExact(subjectClaims[0].Value, "D", out _) || expiry is null ||
                authorization.Count != 1 ||
                !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                header.Length is <= 7 or > 16391 || header[7..].Any(char.IsWhiteSpace) ||
                context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader].Count > 1)
            {
                await ErrorAsync(context, "remote_mcp_actor_invalid", StatusCodes.Status403Forbidden)
                    .ConfigureAwait(false);
                return;
            }

            var service = context.RequestServices.GetService<RemoteMcpOAuthManagementService>();
            if (service is null)
            {
                await ErrorAsync(context, "remote_mcp_management_unavailable",
                    StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
                return;
            }

            bearer = new SecretCredential(header[7..], expiry.Value,
                context.RequestServices.GetRequiredService<TimeProvider>());
            var actor = new RuntimeActorAuthorization(
                bearer,
                context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader]
                    .SingleOrDefault());
            var result = await action(service, actor, issuer, subjectClaims[0].Value).ConfigureAwait(false);
            context.RequestAborted.ThrowIfCancellationRequested();
            await Results.Json(result).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (RemoteMcpOAuthManagementException error)
        {
            await ErrorAsync(context, error.Code, error.StatusCode).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException error)
        {
            var status = error.Code == "runtime_owner_denied"
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status503ServiceUnavailable;
            await ErrorAsync(context, "remote_mcp_owner_unavailable", status).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await ErrorAsync(context, "remote_mcp_owner_unavailable",
                StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await ErrorAsync(context, "remote_mcp_request_invalid",
                StatusCodes.Status400BadRequest).ConfigureAwait(false);
        }
        finally
        {
            bearer?.Invalidate();
        }
    }

    private static Task ErrorAsync(HttpContext context, string code, int status) =>
        Results.Json(new { error = code }, statusCode: status).ExecuteAsync(context);
}
