using Agentweaver.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Registers the runtime usage broker and maps authenticated source, observation, native-turn, and accounting routes.
/// </summary>
public static class RuntimeUsageSourceEndpoints
{
    /// <summary>
    /// Registers usage-source services when the Broker owner is configured alongside runtime registration.
    /// </summary>
    /// <param name="services">The service collection receiving usage stores and clients.</param>
    /// <param name="configuration">Configuration containing Broker and Environment owner addresses.</param>
    /// <returns><see langword="true"/> when native usage support is enabled, or <see langword="false"/> when unconfigured.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the Broker or runtime-registration configuration is invalid.</exception>
    public static bool AddRuntimeUsageSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        var address = configuration["Orchestrator:RuntimeUsage:BrokerOwnerAddress"];
        if (address is null)
            return false;
        if (configuration["Orchestrator:RuntimeRegistration:EnvironmentOwnerAddress"] is null ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Native usage requires the current runtime registration and HTTPS Broker owners.");
        services.AddSingleton<RuntimeUsageSourceStore>();
        services.AddSingleton(new RuntimeUsageSourceOptions(RuntimeOwnerHttpTransport.RequireOwnerAddress(uri)));
        services.AddScoped<RuntimeUsageSourceOwner>();
        services.AddHttpClient<RuntimeUsageBrokerClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(RuntimeOwnerHttpTransport.CreateHandler);
        return true;
    }

    /// <summary>
    /// Maps protected runtime usage APIs for source receipts, native turns, credit guards, and dispatch completions.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same route builder after endpoint registration.</returns>
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
        endpoints.MapPost("/internal/runtime/turns/denials/read",
            (RuntimeNativeCreditDenialReadRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadNativeCreditDenialAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/fixed-credit-denials/record",
            (RuntimeFixedCreditGuardRecordRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.RecordFixedCreditGuardAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/fixed-credit-denials/read",
            (RuntimeFixedCreditGuardReadRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadFixedCreditGuardAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/credit-guards/read",
            (RuntimeCreditGuardReadRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadCreditGuardAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/fixed-credit-denials/no-send",
            (RuntimeFixedCreditNoSendEvidenceRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.RecordFixedCreditNoSendAsync(context, request, cancellationToken)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/turns/no-send",
            (RuntimeNativeTurnNoSendEvidenceRequest request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.RecordNativeTurnNoSendAsync(context, request, cancellationToken)))
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
        endpoints.MapPost("/internal/runtime/turns/recorded",
            (RuntimeNativeTurnRecordedReceipt request, HttpContext context,
                [FromServices] RuntimeUsageSourceOwner owner, CancellationToken cancellationToken) =>
                ExecuteAsync(context, () => owner.ReadNativeTurnAsync(context, request, cancellationToken)))
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

    /// <summary>
    /// Converts owner results and expected runtime failures into no-store HTTP responses.
    /// </summary>
    /// <typeparam name="T">The successful response type.</typeparam>
    /// <param name="context">The current request context used to set response headers and distinguish client cancellation.</param>
    /// <param name="action">The owner operation to execute.</param>
    /// <returns>An OK response or a structured error response with a mapped HTTP status.</returns>
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
