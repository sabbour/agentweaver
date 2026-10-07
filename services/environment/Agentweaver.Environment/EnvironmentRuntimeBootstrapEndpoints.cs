using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Agentweaver.Environment;

public static class EnvironmentRuntimeBootstrapEndpoints
{
    public static IEndpointConventionBuilder MapEnvironmentRuntimeBootstrap(
        this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/internal/runtime/bootstrap/deliver", async (
            RuntimeBootstrapDeliveryRequest request,
            HttpContext context,
            EnvironmentRuntimeBootstrapDelivery delivery,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiresAt ||
                !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
                !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(header.Parameter) ||
                context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
                return Results.Unauthorized();
            SecretCredential? bearer = null;
            try
            {
                bearer = new SecretCredential(header.Parameter, expiresAt, timeProvider);
                var actor = new RuntimeActorAuthorization(
                    bearer, context.Request.Headers["X-Agentweaver-Tenant"].SingleOrDefault());
                return Results.Ok(await delivery.DeliverAsync(request, actor, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (RuntimeAuthorizationException exception)
            {
                return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ProjectsConfigApiException exception)
            {
                return EnvironmentEndpoints.ToProjectAuthorizationResult(exception);
            }
            catch (EnvironmentLifecycleException exception)
            {
                return Results.Conflict(new { code = exception.Code });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { code = "runtime_delivery_invalid" });
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { code = "runtime_delivery_owner_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { code = "runtime_delivery_owner_timeout" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            finally
            {
                bearer?.Invalidate();
            }
        }).RequireAuthorization();
}
