using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Agentweaver.Environment;

public static class EnvironmentRuntimeBootstrapEndpoints
{
    public static IEndpointRouteBuilder MapEnvironmentRuntimeBootstrap(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/runtime/v1/activate", async (
            RuntimeHostConfigureRequest request, HttpContext context,
            EnvironmentRuntimeBootstrapDelivery delivery, TimeProvider timeProvider, CancellationToken token) =>
            await ExecuteAsync(context, timeProvider,
                actor => delivery.ActivateAsync(request, actor, token), token).ConfigureAwait(false))
            .RequireAuthorization();
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
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext context, TimeProvider time, Func<RuntimeActorAuthorization, Task<T>> action,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiry ||
            expiry <= time.GetUtcNow() ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) || context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
            return Results.Unauthorized();
        var bearer = new SecretCredential(header.Parameter, expiry, time);
        try
        {
            return Results.Ok(await action(new(bearer,
                context.Request.Headers["X-Agentweaver-Tenant"].SingleOrDefault())).ConfigureAwait(false));
        }
        catch (RuntimeAuthorizationException failure)
        {
            return Results.Json(new { code = failure.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (EnvironmentLifecycleException failure)
        {
            return Results.Conflict(new { code = failure.Code });
        }
        catch (ProjectsConfigApiException failure)
        {
            return EnvironmentEndpoints.ToProjectAuthorizationResult(failure);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "runtime_activation_invalid" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { code = "runtime_activation_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new { code = "runtime_activation_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            bearer.Invalidate();
        }
    }
}
