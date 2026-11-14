using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Agentweaver.Environment;

/// <summary>Maps authenticated internal runtime-bootstrap and resume operations to the Environment services.</summary>
public static class EnvironmentRuntimeBootstrapEndpoints
{
    /// <summary>Registers the runtime bootstrap, resume, suspend, and workspace verification routes.</summary>
    public static IEndpointRouteBuilder MapEnvironmentRuntimeBootstrap(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/runtime/resume/bootstrap", async (
            RuntimeResumeBootstrapRequest request, HttpContext context,
            EnvironmentRuntimeBootstrapDelivery delivery, TimeProvider timeProvider, CancellationToken token) =>
            await ExecuteAsync(context, timeProvider, async actor =>
            {
                try
                {
                    var receipt = await delivery.BootstrapResumeAsync(request, actor, token).ConfigureAwait(false);
                    return new RuntimeResumeBootstrapOutcome(receipt, null).ValidateFor(request);
                }
                catch (RuntimeResumeBootstrapStateLostException loss)
                {
                    return new RuntimeResumeBootstrapOutcome(null, loss.Receipt).ValidateFor(request);
                }
            }, token).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/v1/activate", async (
            RuntimeHostConfigureRequest request, HttpContext context,
            EnvironmentRuntimeBootstrapDelivery delivery, TimeProvider timeProvider, CancellationToken token) =>
            await ExecuteAsync(context, timeProvider,
                actor => delivery.ActivateAsync(request, actor, token), token).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/run-start/placement", async (
            RuntimeRunStartPlacementRequest request,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeRunStartManager manager,
            TimeProvider timeProvider,
            CancellationToken token) =>
            await ExecuteAsync(context, timeProvider,
                async actor =>
                {
                    if (request.ContractVersion != 1)
                        throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
                    return request.Cleanup is null
                        ? (object)await manager.ProvisionAsync(request, actor, token).ConfigureAwait(false)
                        : await manager.CleanupAsync(request, actor, token).ConfigureAwait(false);
                },
                token).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/run-start/terminal-cleanup", async (
            RuntimeRunStartPlacementRequest request,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeRunStartManager manager,
            TimeProvider timeProvider,
            CancellationToken token) =>
            await ExecuteAsync(context, timeProvider, actor =>
            {
                if (request.ContractVersion != 2 || request.Cleanup is null)
                    throw new RuntimeAuthorizationException("runtime_start_cleanup_request_invalid");
                return manager.CleanupAsync(request, actor, token);
            }, token).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/run-start/child-placement", async (
            RuntimeRunStartPlacementRequest request,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeRunStartManager manager,
            TimeProvider timeProvider,
            CancellationToken token) =>
            await ExecuteAsync(context, timeProvider, actor =>
            {
                if (request.ContractVersion != 2 || request.Owner is null || request.Cleanup is not null)
                    throw new RuntimeAuthorizationException("runtime_start_owner_context_stale");
                return manager.ProvisionAsync(request, actor, token);
            }, token).ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/run-start/retire-failed", async (
            RuntimeRunStartRetirementRequest request,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeRunStartManager manager,
            TimeProvider timeProvider,
            CancellationToken token) =>
            await ExecuteAsync(context, timeProvider,
                actor => manager.RetireFailedBootstrapAsync(request, actor, token), token)
                .ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/run-start/replacement-placement", async (
            RuntimeReplacementPlacementRequest request,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeRunStartManager manager,
            TimeProvider timeProvider,
            CancellationToken token) =>
            await ExecuteAsync(context, timeProvider,
                actor => manager.ProvisionReplacementAsync(request, actor, token), token)
                .ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/run-start/replacement-retire", async (
            RuntimeReplacementRetirementRequest request,
            HttpContext context,
            [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeRunStartManager manager,
            TimeProvider timeProvider,
            CancellationToken token) =>
            await ExecuteAsync(context, timeProvider,
                actor => manager.RetireReplacementAsync(request, actor, token), token)
                .ConfigureAwait(false))
            .RequireAuthorization();
        endpoints.MapPost(
            "/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}" +
            "/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}/resolve",
            async (
                string projectId,
                string runId,
                string environmentId,
                string sessionId,
                string profileId,
                RuntimeBootstrapProfileResolveRequest request,
                HttpContext context,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimePlacementReader placements,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeBootstrapProfileRegistry profiles,
                [Microsoft.AspNetCore.Mvc.FromServices] IEnvironmentRuntimeBootstrapProfileReplacementStore replacements,
                [Microsoft.AspNetCore.Mvc.FromServices] EnvironmentRuntimeEndpointVerifier endpointVerifier,
                TimeProvider timeProvider,
                Guid? checkpointRecoveryOperationId,
                CancellationToken token) =>
                await ExecuteAsync(context, timeProvider, actor =>
                {
                    request.Validate();
                    var recoveryQuery = context.Request.Query["checkpointRecoveryOperationId"];
                    if (recoveryQuery.Count > 1 ||
                        recoveryQuery.Count == 1 &&
                        (!Guid.TryParse(recoveryQuery[0], out var recoveryId) || recoveryId == Guid.Empty ||
                         checkpointRecoveryOperationId != recoveryId))
                        throw new RuntimeAuthorizationException("checkpoint_recovery_registration_stale");
                    return placements.ResolveCurrentProfileAsync(
                        actor, projectId, runId, sessionId, environmentId, profileId,
                        profiles, replacements, endpointVerifier, token, checkpointRecoveryOperationId);
                }, token, "runtime_bootstrap_profile_invalid").ConfigureAwait(false))
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
        CancellationToken cancellationToken, string invalidCode = "runtime_activation_invalid")
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
            when (failure.Code is "upstream_unavailable" or "upstream_timeout")
        {
            return Results.Json(new { code = failure.Code }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (RuntimeAuthorizationException failure)
        {
            return Results.Json(new { code = failure.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (EnvironmentLifecycleException failure)
        {
            return Results.Conflict(new { code = failure.Code });
        }
        catch (SandboxProviderException failure)
        {
            return Results.Conflict(new { code = failure.Code });
        }
        catch (ProjectsConfigApiException failure)
        {
            return EnvironmentEndpoints.ToProjectAuthorizationResult(failure);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = invalidCode });
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
