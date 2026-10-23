using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentRuntime;

/// <summary>Strict request body for the host's fixed-credit guard read endpoint.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeHostFixedCreditGuardReadRequest(
    RuntimeHostSessionProof? Proof, Guid RequestId, string RequestHash);

/// <summary>Strict request body for the host's native credit denial read endpoint.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RuntimeHostNativeCreditDenialReadRequest(
    RuntimeHostSessionProof? Proof, Guid DispatchId, RuntimeCreditLimitDenialStage Stage);

/// <summary>
/// Maps authenticated HTTP routes for bootstrap, runtime turns, readiness, refresh,
/// and workspace suspend/resume operations.
/// </summary>
public static class RuntimeAgentHostEndpoints
{
    /// <summary>
    /// Adds host routes. Protected routes require a live bearer matching the configure
    /// audience; health routes return only liveness or readiness.
    /// </summary>
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
        endpoints.MapPost(configure.AbsolutePath + "/resume-state",
            (HttpContext context, RuntimeResumeBootstrapRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ReadResumeBootstrapStateAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapGet("/runtime/v1/projects/{projectId}/hosted-model-reasoning-catalog",
            (string projectId, HttpContext context, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ReadHostedModelReasoningCatalogAsync(projectId, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/refresh",
            (HttpContext context, RuntimeHostRefreshRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.RefreshAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/turns/fixed-credit-denials/read",
            (HttpContext context, RuntimeHostFixedCreditGuardReadRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ReadFixedCreditGuardAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/turns/denials/read",
            (HttpContext context, RuntimeHostNativeCreditDenialReadRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ReadNativeCreditDenialAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapGet("/runtime/v1/readiness",
            (HttpContext context, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.ReadinessAsync(actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/a2a/message:send",
            (HttpContext context, RuntimeA2ASendRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.SendAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/suspend",
            (HttpContext context, RuntimeHostSuspendRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.SuspendAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapPost("/runtime/v1/workspace/resume",
            (HttpContext context, RuntimeWorkspaceResumeRequest request, CancellationToken token) =>
                ExecuteAsync(context, configure, timeProvider,
                    actor => host.VerifyWorkspaceResumeAsync(request, actor, token))).RequireAuthorization();
        endpoints.MapGet("/health/live", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { status = "live" });
        });
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

    /// <summary>Authenticates one request, invokes its host operation, and maps failures to safe status codes.</summary>
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
            var statusCode = failure.Code is
                "runtime_fixed_credit_guard_unavailable" or "runtime_fixed_credit_no_send_unavailable" or
                "runtime_usage_source_unavailable" or "runtime_owner_unavailable" or
                "runtime_owner_timeout" or "runtime_owner_contract_invalid" or
                "runtime_owner_response_contract_invalid"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status403Forbidden;
            return failure.FixedCreditGuard is { } fixedCreditGuard
                ? Results.Json(new { code = failure.Code, fixedCreditGuard }, statusCode: statusCode)
                : Results.Json(new { code = failure.Code }, statusCode: statusCode);
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
        catch (PlatformNotSupportedException)
        {
            return Results.Json(new { code = "runtime_workspace_flush_unsupported" },
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
