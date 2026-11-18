using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    private const int ExecutionPlanMaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions ExecutionPlanJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static Task<IResult> ReadExecutionPlanAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        [FromServices] PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            CoordinationExecutionPlanSnapshot snapshot;
            try
            {
                snapshot = await store.ReadExecutionPlanAsync(
                    actor,
                    new SessionIdentity(projectId, runId, sessionId),
                    selection,
                    checkpoints,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
                throw;
            }

            byte[] response;
            try
            {
                response = SerializeExecutionPlanResponse(snapshot);
            }
            catch (CoordinationException)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (JsonException)
            {
                await RequireUnchangedAuthorizedSelectionAsync(
                    context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
                throw;
            }

            await RequireUnchangedAuthorizedSelectionAsync(
                context, projectId, runId, selection, projects, cancellationToken).ConfigureAwait(false);
            return Results.Bytes(response, "application/json");
        }, cancellationToken);

    internal static byte[] SerializeExecutionPlanResponse(CoordinationExecutionPlanSnapshot snapshot)
    {
        using var output = new BoundedExecutionPlanStream(ExecutionPlanMaximumResponseBytes);
        try
        {
            JsonSerializer.Serialize(output, snapshot, ExecutionPlanJsonOptions);
        }
        catch (ExecutionPlanResponseTooLargeException)
        {
            throw new CoordinationException(
                "execution_plan_unavailable", StatusCodes.Status409Conflict);
        }
        return output.ToArray();
    }

    private sealed class BoundedExecutionPlanStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinLimit(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinLimit(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureWithinLimit(1);
            base.WriteByte(value);
        }

        private void EnsureWithinLimit(int count)
        {
            if (count < 0 || Length > maximumBytes - count)
                throw new ExecutionPlanResponseTooLargeException();
        }
    }

    private sealed class ExecutionPlanResponseTooLargeException : Exception;
}
