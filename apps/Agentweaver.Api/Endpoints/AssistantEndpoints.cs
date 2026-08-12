using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Endpoints for the MCP-driven operator assistant (#346). A "run" here is a lightweight operator
/// chat conversation persisted in the run store (AgentName == "Operator"); its transcript streams
/// over the existing GET /api/runs/{id}/stream and /events endpoints. Auth is enforced globally by
/// <see cref="GitHubTokenAuthMiddleware"/> (these routes live under /api), so an unauthenticated
/// request never reaches these handlers.
///
/// Additive: the legacy /api/console/turn facade path is untouched.
/// </summary>
public static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this WebApplication app)
    {
        // POST /api/assistant/runs — start a new operator chat run. An optional initial message runs
        // the opening turn immediately. Returns the run id used to stream and to post further turns.
        app.MapPost("/api/assistant/runs", async (
            HttpContext httpContext,
            StartAssistantRunRequest request,
            IAssistantRunService assistant,
            IProjectStore projectStore,
            IConfiguration configuration,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var bearer = ExtractBearer(httpContext);

            try
            {
                if (!string.IsNullOrWhiteSpace(request.ProjectId))
                {
                    if (!ProjectId.TryParse(request.ProjectId, out var projectId))
                        return Results.Json(
                            new { error = "invalid_project_id", message = "The project id is invalid." },
                            statusCode: StatusCodes.Status400BadRequest);

                    var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
                    if (project is null)
                        return Results.NotFound();

                    if (await ProjectAuthorization
                        .RequireAccessAsync(httpContext, project, configuration, ProjectRole.Viewer, ct)
                        .ConfigureAwait(false) is { } forbid)
                    {
                        return forbid;
                    }
                }

                var result = await assistant.StartRunAsync(
                    caller,
                    bearer,
                    request.Message,
                    request.ProjectId,
                    request.RunId,
                    request.ModelId,
                    ct,
                    resumeFromRunId: request.ResumeFromRunId).ConfigureAwait(false);

                return Results.Json(new StartAssistantRunResponse
                {
                    RunId = result.RunId.ToString(),
                    Status = result.Status.ToApiString(),
                    Message = result.FirstTurn?.Message,
                    ToolsInvoked = result.FirstTurn?.ToolNamesInvoked,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (AssistantConcurrencyLimitException ex)
            {
                return Results.Json(
                    new { error = "operator_run_limit", message = ex.Message, limit = ex.Limit },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (AssistantRunHttpException ex)
            {
                return Results.Json(new { error = ex.Error, message = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (AgentProviderException ex)
            {
                return Results.Json(
                    new { error = ex.ErrorCode, message = ex.UserMessage, kind = ex.FailureKind.ToString(), retryable = ex.IsRetryable },
                    statusCode: ProviderFailureStatus(ex.FailureKind));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to start operator assistant run.");
                return Results.Problem("Failed to start operator assistant run.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // GET /api/assistant/runs — list the caller's own operator conversations, newest-first. Scoped
        // to the authenticated caller (never leaks other users' runs). Optional ?limit= caps the count.
        app.MapGet("/api/assistant/runs", async (
            HttpContext httpContext,
            IAssistantRunService assistant,
            ILogger<Program> logger,
            CancellationToken ct,
            int? limit) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            try
            {
                var runs = await assistant.ListRunsAsync(caller, limit ?? 50, ct).ConfigureAwait(false);
                return Results.Json(new AssistantRunListResponse
                {
                    Runs = runs.Select(r => new AssistantRunSummaryDto
                    {
                        RunId = r.RunId,
                        Status = r.Status.ToApiString(),
                        Title = r.Title,
                        CreatedAt = r.CreatedAt.ToString("O"),
                    }).ToList(),
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to list operator assistant runs.");
                return Results.Problem("Failed to list operator assistant runs.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // POST /api/assistant/runs/{id}/messages — send the next user turn into a running conversation.
        app.MapPost("/api/assistant/runs/{id}/messages", async (
            HttpContext httpContext,
            string id,
            AssistantMessageRequest request,
            IAssistantRunService assistant,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var bearer = ExtractBearer(httpContext);
            var message = request.Message?.Trim();
            if (string.IsNullOrWhiteSpace(message))
                return Results.Json(new { error = "message_required", message = "message is required." }, statusCode: StatusCodes.Status400BadRequest);

            try
            {
                var response = await assistant.SendMessageAsync(caller, bearer, id, message, ct).ConfigureAwait(false);
                return Results.Json(new AssistantMessageResponse
                {
                    RunId = id,
                    Message = response.Message,
                    Status = Domain.RunStatus.InProgress.ToApiString(),
                    ToolsInvoked = response.ToolNamesInvoked,
                });
            }
            catch (AssistantRunHttpException ex)
            {
                return Results.Json(new { error = ex.Error, message = ex.Message }, statusCode: ex.StatusCode);
            }
            catch (AgentProviderException ex)
            {
                return Results.Json(
                    new { error = ex.ErrorCode, message = ex.UserMessage, kind = ex.FailureKind.ToString(), retryable = ex.IsRetryable },
                    statusCode: ProviderFailureStatus(ex.FailureKind));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to run operator assistant turn.");
                return Results.Problem("Failed to run operator assistant turn.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    /// <summary>Extracts the raw bearer token (without the scheme) to thread through to the MCP
    /// provider as the caller's per-call identity. Returns empty when the header is absent (the auth
    /// middleware guarantees a valid credential reached the endpoint).</summary>
    private static string ExtractBearer(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : header.Trim();
    }

    private static int ProviderFailureStatus(AgentProviderFailureKind kind) =>
        kind switch
        {
            AgentProviderFailureKind.Authorization => StatusCodes.Status401Unauthorized,
            AgentProviderFailureKind.RateLimited => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
}
