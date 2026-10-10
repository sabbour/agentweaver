using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

public sealed record SessionSuspendRequest(string IdempotencyKey);

public sealed record SessionResumeRequest(string IdempotencyKey, Guid ManifestId);

public static partial class CoordinationEndpoints
{
    public static IEndpointRouteBuilder MapSuspendResumeEndpoints(this IEndpointRouteBuilder app)
    {
        var coordination = app.MapGroup("/api/projects/{projectId}/runs/{runId}/coordination")
            .RequireAuthorization();
        coordination.MapPost(
            "/sessions/{sessionId}/suspend",
            SuspendUnavailableAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/resume",
            ResumeUnavailableAsync);
        return app;
    }

    private static Task<IResult> SuspendUnavailableAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionSuspendRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ReturnUnavailableAsync(
            projectId,
            runId,
            sessionId,
            SessionSuspendResumeOperationKind.Suspend,
            request.IdempotencyKey,
            sourceManifestId: null,
            context,
            options,
            projects,
            coordinator,
            cancellationToken);

    private static Task<IResult> ResumeUnavailableAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionResumeRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ReturnUnavailableAsync(
            projectId,
            runId,
            sessionId,
            SessionSuspendResumeOperationKind.Resume,
            request.IdempotencyKey,
            request.ManifestId,
            context,
            options,
            projects,
            coordinator,
            cancellationToken);

    private static Task<IResult> ReturnUnavailableAsync(
        string projectId,
        string runId,
        string sessionId,
        SessionSuspendResumeOperationKind kind,
        string idempotencyKey,
        Guid? sourceManifestId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        SessionSuspendResumeCoordinator coordinator,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            CoordinationIdentity.ValidateIdentity(sessionId, nameof(sessionId));
            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return await coordinator.RecordUnavailableAsync(
                actor,
                selection,
                new SessionIdentity(projectId, runId, sessionId),
                kind,
                idempotencyKey,
                sourceManifestId,
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
