using Agentweaver.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Orchestrator;

public static class RuntimeUsageSourceEndpoints
{
    public static bool AddRuntimeUsageSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        var address = configuration["Orchestrator:RuntimeUsage:BrokerOwnerAddress"];
        if (address is null)
            return false;
        if (configuration["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] is null ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Native usage requires the current runtime registration and HTTPS Broker owners.");
        services.AddSingleton(new RuntimeUsageSourceOptions(RuntimeOwnerHttpTransport.RequireOwnerAddress(uri)));
        services.AddSingleton<RuntimeUsageSourceStore>();
        services.AddScoped<RuntimeUsageSourceOwner>();
        services.AddHttpClient<RuntimeUsageBrokerClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        return true;
    }

    public static IEndpointRouteBuilder MapRuntimeUsageSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/runtime/sources/{runtimeInstanceId:guid}",
            (Guid runtimeInstanceId, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadCurrentSourceAsync(
                    context, runtimeInstanceId, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/sources/{runtimeInstanceId:guid}",
            (Guid runtimeInstanceId, RuntimeSdkSourceRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.RegisterAsync(context, runtimeInstanceId, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/observations",
            (RuntimeUsageObservationRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.AppendAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/begin",
            (RuntimeNativeTurnBeginRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.BeginNativeTurnAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/observations",
            (RuntimeNativeTurnObservationRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.RecordNativeTurnAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/accounting",
            (RuntimeNativeTurnAccountingRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.CompleteNativeTurnAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/usage-receipts/{receiptId:guid}",
            (string projectId, string runId, string sessionId, Guid receiptId, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadReceiptAsync(
                    context, projectId, runId, sessionId, receiptId, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/usage-dispatch-completions/{dispatchId:guid}",
            (string projectId, string runId, string sessionId, Guid dispatchId, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadSourceCompletionAsync(
                    context, projectId, runId, sessionId, dispatchId, cancellationToken)))
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (CoordinationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (RuntimeAuthorizationException exception)
        {
            var status = exception.Code switch
            {
                "runtime_native_turn_admission_unavailable" => StatusCodes.Status503ServiceUnavailable,
                "runtime_native_turn_indeterminate" => StatusCodes.Status409Conflict,
                _ when exception.Code.EndsWith("_conflict", StringComparison.Ordinal) =>
                    StatusCodes.Status409Conflict,
                _ => StatusCodes.Status403Forbidden
            };
            return Results.Json(new { error = exception.Code }, statusCode: status);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "runtime_usage_request_invalid" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_usage_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_usage_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
