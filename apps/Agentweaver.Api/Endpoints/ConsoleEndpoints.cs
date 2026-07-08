using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.ConsoleFacade;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Endpoints;

public static class ConsoleEndpoints
{
    public static void MapConsoleEndpoints(this WebApplication app)
    {
        async Task<IResult> HandleTurnAsync(
            HttpContext httpContext,
            ConsoleTurnRequest request,
            IConsoleTurnService console,
            ILogger<Program> logger,
            CancellationToken ct)
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var authorizationHeader = httpContext.Request.Headers.Authorization.ToString();

            try
            {
                return Results.Json(await console.HandleAsync(request, caller, authorizationHeader, ct).ConfigureAwait(false));
            }
            catch (ConsoleTurnHttpException ex)
            {
                return Results.Json(
                    new
                    {
                        error = ex.Error,
                        message = ex.Message,
                    },
                    statusCode: ex.StatusCode);
            }
            catch (SteeringValidationException ex)
            {
                return Results.Json(
                    new
                    {
                        error = "steering_invalid",
                        message = ex.Message,
                    },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (SteeringRecoveryExhaustedException ex)
            {
                return Results.Json(
                    new
                    {
                        error = "steering_recovery_exhausted",
                        message = ex.Message,
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (AgentProviderException ex)
            {
                return Results.Json(
                    new
                    {
                        error = ex.ErrorCode,
                        message = ex.UserMessage,
                        kind = ex.FailureKind.ToString(),
                        retryable = ex.IsRetryable,
                    },
                    statusCode: ProviderFailureStatus(ex.FailureKind));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Console turn failed.");
                return Results.Problem("Console turn failed.", statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        app.MapPost("/api/console/messages", HandleTurnAsync);
        app.MapPost("/api/console/turn", HandleTurnAsync);
    }

    private static int ProviderFailureStatus(AgentProviderFailureKind kind) =>
        kind switch
        {
            AgentProviderFailureKind.Authorization => StatusCodes.Status401Unauthorized,
            AgentProviderFailureKind.RateLimited => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
}
