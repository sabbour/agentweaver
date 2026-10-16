using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Agentweaver.AgentRuntime;

public static class RuntimeBootstrapReceiverEndpoints
{
    public static IEndpointConventionBuilder MapRuntimeBootstrapReceiver(
        this IEndpointRouteBuilder endpoints,
        Uri configureEndpoint,
        RuntimeBootstrapReceiver receiver)
    {
        if (!RuntimeContractValidation.IsHttpsEndpoint(configureEndpoint))
            throw new ArgumentException("The Runtime configure endpoint must be fixed HTTPS.", nameof(configureEndpoint));
        return endpoints.MapPost(configureEndpoint.AbsolutePath,
            async (HttpContext context, RuntimeBootstrapDeliveryRequest request, CancellationToken token) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
                if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
                    !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
                    !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(header.Parameter) ||
                    context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
                    return Results.Unauthorized();
                if (context.Request.Scheme != configureEndpoint.Scheme ||
                    context.Request.Host.ToUriComponent() != configureEndpoint.Authority)
                    return Results.BadRequest(new { code = "runtime_configuration_audience_invalid" });
                SecretCredential? bearer = null;
                try
                {
                    bearer = new SecretCredential(header.Parameter, expiresAt);
                    var actor = new RuntimeActorAuthorization(
                        bearer, context.Request.Headers["X-Agentweaver-Tenant"].SingleOrDefault());
                    return Results.Ok(await receiver.ReceiveAsync(request, actor, token).ConfigureAwait(false));
                }
                catch (RuntimeAuthorizationException exception)
                {
                    return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { code = "runtime_delivery_invalid" });
                }
                finally
                {
                    bearer?.Invalidate();
                }
            }).RequireAuthorization();
    }
}
