using System.Text.Json;
using System.Globalization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    internal static void MapCheckpointRecoveryEventRoute(RouteGroupBuilder coordination) =>
        coordination.MapGet(
            "/sessions/{sessionId}/checkpoint-recoveries/{operationId}/events",
            ReadCheckpointRecoveryEventsAsync);

    internal static Task<IResult> ReadCheckpointRecoveryEventsAsync(
        string projectId, string runId, string sessionId, string operationId, string? cursor, string? limit,
        HttpContext context, OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            byte[] response;
            try
            {
                if (!Guid.TryParseExact(operationId, "D", out var id) || id == Guid.Empty)
                    throw new CoordinationException("checkpoint_recovery_events_invalid", StatusCodes.Status400BadRequest);
                var pageLimit = 100;
                if (limit is not null && !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out pageLimit))
                    throw new CoordinationException("checkpoint_recovery_events_invalid", StatusCodes.Status400BadRequest);
                var page = await store.ReadCheckpointRecoveryEventsAsync(
                    actor, new SessionIdentity(projectId, runId, sessionId), id,
                    selection, pageLimit, cursor, cancellationToken).ConfigureAwait(false);
                response = SerializeCheckpointRecoveryEvents(page);
            }
            catch (Exception exception) when (exception is CoordinationException or JsonException)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
                throw CheckpointRecoveryEventProjection.Unavailable();
            }
            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            return Results.Bytes(response, "application/json");
        }, cancellationToken);

    internal static byte[] SerializeCheckpointRecoveryEvents(CheckpointRecoveryEventPage page)
    {
        using var output = new BoundedExecutionPlanStream(ExecutionPlanMaximumResponseBytes);
        try
        {
            JsonSerializer.Serialize(output, page, JsonOptions);
        }
        catch (ExecutionPlanResponseTooLargeException)
        {
            throw CheckpointRecoveryEventProjection.Unavailable();
        }
        return output.ToArray();
    }
}
