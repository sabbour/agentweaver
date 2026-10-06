using System.Text.Json;
using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

public static class CoordinationEndpoints
{
    public static IEndpointRouteBuilder MapCoordinationEndpoints(this IEndpointRouteBuilder app)
    {
        var coordination = app.MapGroup("/api/projects/{projectId}/runs/{runId}/coordination")
            .RequireAuthorization();
        coordination.MapPost("/root", AcceptRootAsync);
        coordination.MapPost("/sessions/{parentSessionId}/children", RegisterChildAsync);
        coordination.MapPost("/sessions/{sessionId}/messages", SendMessageAsync);
        coordination.MapPost("/sessions/{sessionId}/turn-boundary", AdvanceTurnBoundaryAsync);
        coordination.MapPost("/sessions/{sessionId}/turn-completion", FinishTurnAsync);
        coordination.MapGet("/sessions/{sessionId}/notifications", ReadNotificationsAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/notifications/{notificationId:guid}/acknowledge",
            AcknowledgeNotificationAsync);
        coordination.MapPost(
            "/sessions/{sessionId}/messages/{messageId:guid}/acknowledge",
            AcknowledgeMessageAsync);

        app.MapPost(
            "/internal/projects/{projectId}/runs/{runId}/coordination/message-route",
            ValidateMessageRouteAsync).RequireAuthorization();
        app.MapGet(
            "/internal/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/owner-binding",
            GetSessionBindingAsync).RequireAuthorization();
        return app;
    }

    private static Task<IResult> AcceptRootAsync(
        string projectId,
        string runId,
        AcceptRootRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        EventsAddressedMessageClient events,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            var selection = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var accepted = await store.AcceptRootAsync(
                actor, selection, request.SessionId, cancellationToken).ConfigureAwait(false);
            await events.EnsureSessionAsync(
                context,
                new SessionIdentity(projectId, runId, accepted.RootSessionId),
                cancellationToken).ConfigureAwait(false);
            return Results.Created(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/coordination/root",
                accepted);
        }, cancellationToken);

    private static Task<IResult> RegisterChildAsync(
        string projectId,
        string runId,
        string parentSessionId,
        RegisterChildRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        EventsAddressedMessageClient events,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var child = await store.RegisterChildAsync(
                actor,
                new SessionIdentity(projectId, runId, parentSessionId),
                request.SessionId,
                cancellationToken).ConfigureAwait(false);
            await events.EnsureSessionAsync(context, child.Identity, cancellationToken).ConfigureAwait(false);
            return Results.Created(
                $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/coordination/sessions/{Uri.EscapeDataString(child.Identity.SessionId)}",
                child);
        }, cancellationToken);

    private static Task<IResult> SendMessageAsync(
        string projectId,
        string runId,
        string sessionId,
        CoordinationMessageRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.SendMessageAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            var delivery = await store.ClaimOutboundAsync(
                actor, result.OwnerMessageId, cancellationToken).ConfigureAwait(false);
            if (delivery is null)
                return Results.Accepted(value: result);
            var outbound = await store.ReadOutboundMessageAsync(
                result.OwnerMessageId, cancellationToken).ConfigureAwait(false);
            var receipt = await events.AdmitAsync(context, outbound, cancellationToken).ConfigureAwait(false);
            await store.AdmitDeliveryAsync(
                actor,
                new DeliveryIngressRequest(result.OwnerMessageId, receipt),
                cancellationToken).ConfigureAwait(false);
            _ = await store.AcknowledgeOutboundAsync(delivery, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result with { Status = "admitted" });
        }, cancellationToken);

    private static Task<IResult> ValidateMessageRouteAsync(
        string projectId,
        string runId,
        MessageRouteValidationRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.EventsAudience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var outbound = request.Message
                ?? throw new CoordinationException("owner_message_invalid", StatusCodes.Status400BadRequest);
            var binding = await store.ValidateMessageRouteAsync(
                actor,
                new SessionIdentity(projectId, runId, outbound.Sender.SessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(binding);
        }, cancellationToken);

    private static Task<IResult> GetSessionBindingAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.EventsAudience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var binding = await store.GetSessionBindingAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(binding);
        }, cancellationToken);

    private static Task<IResult> AdvanceTurnBoundaryAsync(
        string projectId,
        string runId,
        string sessionId,
        TurnBoundaryRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var completed = await store.ReadCompletedTurnBoundaryAsync(
                actor, identity, request.ExecutionFence, request.ExpectedStateVersion, cancellationToken)
                .ConfigureAwait(false);
            if (completed is not null)
                return Results.Ok(completed);
            var boundary = await store.AdvanceTurnBoundaryAsync(
                actor, identity, request.ExecutionFence, request.ExpectedStateVersion, cancellationToken)
                .ConfigureAwait(false);
            var claim = await events.ClaimNextAsync(context, identity, cancellationToken).ConfigureAwait(false);
            if (claim is not null)
            {
                if (claim.Owner != CoordinationIdentity.ClaimOwner(actor) ||
                    claim.Message.Recipient != identity ||
                    claim.Message.RecipientFence != request.ExecutionFence)
                    throw new CoordinationException(
                        "events_claim_contract_invalid", StatusCodes.Status502BadGateway);
                var presented = claim.Message.Status == AddressedMessageStatus.Delivered
                    ? claim.Message
                    : await events.PresentAsync(
                        context,
                        identity,
                        claim.Message.MessageId,
                        claim.Message.ClaimFence,
                        cancellationToken).ConfigureAwait(false);
                await store.RecordPresentationAsync(
                    actor, identity, presented, cancellationToken).ConfigureAwait(false);
                boundary = boundary with { PresentedMessage = presented };
            }
            boundary = await store.CompleteTurnBoundaryAsync(
                actor,
                identity,
                request.ExecutionFence,
                request.ExpectedStateVersion,
                boundary.StateVersion,
                boundary.PresentedMessage,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(boundary);
        }, cancellationToken);

    private static Task<IResult> FinishTurnAsync(
        string projectId,
        string runId,
        string sessionId,
        FinishTurnRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var result = await store.FinishTurnAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }, cancellationToken);

    private static Task<IResult> ReadNotificationsAsync(
        string projectId,
        string runId,
        string sessionId,
        int? limit,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var notifications = await store.ReadParentNotificationsAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                limit ?? 50,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(notifications);
        }, cancellationToken);

    private static Task<IResult> AcknowledgeNotificationAsync(
        string projectId,
        string runId,
        string sessionId,
        Guid notificationId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            await store.AcknowledgeParentNotificationAsync(
                actor,
                new SessionIdentity(projectId, runId, sessionId),
                notificationId,
                cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }, cancellationToken);

    private static Task<IResult> AcknowledgeMessageAsync(
        string projectId,
        string runId,
        string sessionId,
        Guid messageId,
        AddressedMessageClaimFenceRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore store,
        EventsAddressedMessageClient events,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
            _ = await projects.ReadAcceptedSelectionAsync(
                context, projectId, runId, cancellationToken).ConfigureAwait(false);
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var acknowledged = await events.AcknowledgeAsync(
                context, identity, messageId, request.ClaimFence, cancellationToken).ConfigureAwait(false);
            await store.RecordAcknowledgementAsync(
                actor, identity, acknowledged, cancellationToken).ConfigureAwait(false);
            return Results.Ok(acknowledged);
        }, cancellationToken);

    private static CoordinationActor RequireOwnerActor(
        HttpContext context,
        OrchestratorOptions options,
        string projectId,
        string runId,
        string audience)
    {
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (scope.ProjectId != projectId || scope.RunId != runId ||
            !CoordinationIdentity.HasAudience(context.User, audience))
            throw new CoordinationException("caller_binding_mismatch", StatusCodes.Status403Forbidden);
        return actor;
    }

    private static async Task<IResult> ExecuteAsync(
        Func<Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "invalid_json" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
