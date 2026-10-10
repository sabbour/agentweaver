using System.Net.Http.Headers;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentRuntime;

public static class RuntimeAgentHostEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeAgentHost(
        this IEndpointRouteBuilder endpoints, RuntimeAgentHost host, TimeProvider timeProvider)
    {
        var configure = host.ConfigureEndpoint;
        endpoints.MapPost(configure.AbsolutePath,
            (HttpContext context, RuntimeBootstrapDeliveryRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ReceiveAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost(configure.AbsolutePath + "/activate",
            (HttpContext context, RuntimeHostConfigureRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ConfigureAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/refresh",
            (HttpContext context, RuntimeHostRefreshRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.RefreshAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/a2a/message:send",
            (HttpContext context, RuntimeA2ASendRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.SendAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/suspend/native-evidence",
            (HttpContext context, RuntimeHostSuspendRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.SuspendAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
        endpoints.MapGet("/health/ready", async (HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                await host.ReadinessAsync(token).ConfigureAwait(false);
                return Results.Ok(new { status = "ready" });
            }
            catch (Exception failure) when (failure is RuntimeAuthorizationException or RuntimeStartupException or
                HttpRequestException or ObjectDisposedException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext context, Uri audience, TimeProvider time, Func<RuntimeActorAuthorization, Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        if (!authentication.Succeeded || authentication.Properties?.ExpiresUtc is not { } expiry ||
            expiry <= time.GetUtcNow() ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) || context.Request.Headers["X-Agentweaver-Tenant"].Count > 1)
            return Results.Unauthorized();
        if (context.Request.Scheme != audience.Scheme ||
            context.Request.Host.ToUriComponent() != audience.Authority)
            return Results.BadRequest(new { code = "runtime_configuration_audience_invalid" });
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
        catch (RuntimeStartupException failure)
        {
            return Results.Json(new { code = failure.Code, failure.Failure },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "runtime_request_invalid" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { code = "runtime_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { code = "runtime_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException &&
            !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(nameof(RuntimeAgentHostEndpoints))
                .LogError("AgentHost request failed: {FailureType}, trace {TraceId}.",
                    failure.GetType().Name, context.TraceIdentifier);
            return Results.Json(new { code = "runtime_execution_failed", traceId = context.TraceIdentifier },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            bearer.Invalidate();
        }
    }
}
