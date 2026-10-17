using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.EventsAndSessions;

/// <summary>Maps the authenticated endpoint that records owner-accepted project effects.</summary>
public static class AcceptedEffectEndpoints
{
    /// <summary>
    /// Adds the receipt-delivery route; conflicts stay explicit and unexpected failures return a stable 503 code.
    /// </summary>
    public static void MapAcceptedEffectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/internal/project-facts/accepted-effects",
                async (
                    HttpContext context,
                    AcceptedEffectDeliveryRequest request,
                    AcceptedEffectApplicationService service,
                    ILogger<Program> logger,
                    CancellationToken cancellationToken) =>
                {
                    try
                    {
                        var acknowledgment = await service.AppendAsync(
                            context.User, request, cancellationToken).ConfigureAwait(false);
                        return Results.Ok(acknowledgment);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (ProjectFactConflictException)
                    {
                        return Error("accepted_effect_identity_conflict", StatusCodes.Status409Conflict);
                    }
                    catch (ProjectFactApiException exception)
                    {
                        return Error(exception.Code, exception.StatusCode);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        logger.LogError(
                            "Failed to append accepted-effect receipt. FailureCode={FailureCode}; FailureType={FailureType}",
                            "project_fact_unavailable",
                            exception.GetType().Name);
                        return Error("project_fact_unavailable", StatusCodes.Status503ServiceUnavailable);
                    }
                })
            .RequireAuthorization();
    }

    /// <summary>Creates the stable problem response used when a receipt cannot be admitted.</summary>
    private static IResult Error(string code, int statusCode) =>
        Results.Problem(
            title: code,
            detail: "The accepted-effect fact could not be admitted.",
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
