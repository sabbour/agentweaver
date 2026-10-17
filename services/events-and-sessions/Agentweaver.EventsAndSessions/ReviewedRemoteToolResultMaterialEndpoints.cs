using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Azure;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Agentweaver.EventsAndSessions;

/// <summary>Exposes private remote-tool result transfer only on the configured direct mutual-TLS listener.</summary>
public static class ReviewedRemoteToolResultMaterialEndpoints
{
    /// <summary>Maps private result write/read routes and requires the dedicated HTTPS peer certificate.</summary>
    public static IEndpointRouteBuilder MapReviewedRemoteToolResultMaterialEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/sessions/{sessionId}/remote-tool-results",
            (HttpContext context, string sessionId, CancellationToken token) =>
                ExecuteAsync(context, async () =>
                {
                    var request = await context.Request.ReadFromJsonAsync<SessionMaterialWriteRequest>(
                        cancellationToken: token).ConfigureAwait(false)
                        ?? throw new ArgumentException("A remote tool result material request is required.");
                    return await context.RequestServices.GetRequiredService<ReviewedRemoteToolResultMaterialService>()
                        .WriteAsync(context, sessionId, request, token).ConfigureAwait(false);
                }))
            .RequireAuthorization();
        endpoints.MapPost(
            "/internal/sessions/{sessionId}/remote-tool-results/read",
            (HttpContext context, string sessionId, CancellationToken token) =>
                ExecuteAsync(context, async () =>
                {
                    var proof = await context.Request.ReadFromJsonAsync<RemoteToolResultProof>(
                        cancellationToken: token).ConfigureAwait(false)
                        ?? throw new ArgumentException("A remote tool result proof is required.");
                    return await context.RequestServices.GetRequiredService<ReviewedRemoteToolResultMaterialService>()
                        .ReadAsync(context, sessionId, proof, token).ConfigureAwait(false);
                }))
            .RequireAuthorization();
        return endpoints;
    }

    /// <summary>Applies transport checks before body reads, returns no-store responses, and maps owner failures.</summary>
    private static async Task<IResult> ExecuteAsync<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps ||
            context.RequestServices.GetService<RuntimeServiceTlsOptions>() is not { } tlsOptions)
        {
            var error = context.Request.IsHttps
                ? "runtime_remote_tool_result_route_unavailable"
                : "runtime_remote_tool_result_https_required";
            return Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            var protocol = context.Features.Get<ITlsHandshakeFeature>()?.Protocol;
            RuntimeServiceTlsIdentity.RequireDirectTls(
                context.Request.IsHttps, protocol, context.Connection.LocalPort, tlsOptions.ListenerPort);
            context.RequestServices.GetRequiredService<RuntimeServiceTlsIdentity>().RequirePeerConnection(
                context.Request.IsHttps, protocol, context.Connection.LocalPort,
                context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = 4 * SessionMaterialValidation.MaximumBytes;
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (RuntimeAuthorizationException failure)
        {
            return Results.Json(new { error = failure.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ProjectsAuthorizationContextException failure)
        {
            return Results.Json(new { error = failure.Code }, statusCode: (int)failure.StatusCode);
        }
        catch (SessionEventConflictException)
        {
            return Results.Conflict(new { error = "runtime_remote_tool_result_event_conflict" });
        }
        catch (SessionNotFoundException)
        {
            return Results.NotFound(new { error = "runtime_remote_tool_result_not_found" });
        }
        catch (SessionAuthenticationException)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_actor_denied" },
                statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (SessionAccessDeniedException)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_access_denied" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (SessionPinnedProviderUnavailableException)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SessionMaterialIntegrityException failure)
        {
            return Results.Json(new { error = failure.Code }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "runtime_remote_tool_result_invalid" });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "runtime_remote_tool_result_invalid" });
        }
        catch (BadHttpRequestException failure)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_request_invalid" },
                statusCode: failure.StatusCode);
        }
        catch (RequestFailedException)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_store_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
