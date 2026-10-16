using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.EventsAndSessions;

public static class AddressedMessageEndpoints
{
    public static IEndpointRouteBuilder MapAddressedMessageEndpoints(this IEndpointRouteBuilder app)
    {
        var endpoints = app.MapGroup("/internal/addressed-messages").RequireAuthorization();
        endpoints.MapPost("/admit", AdmitAsync);
        endpoints.MapPost(
            "/projects/{projectId}/runs/{runId}/sessions/{sessionId}/claim", ClaimAsync);
        endpoints.MapPost(
            "/projects/{projectId}/runs/{runId}/sessions/{sessionId}/messages/{messageId:guid}/present",
            PresentAsync);
        endpoints.MapPost(
            "/projects/{projectId}/runs/{runId}/sessions/{sessionId}/messages/{messageId:guid}/acknowledge",
            AcknowledgeAsync);
        return app;
    }

    private static async Task<IResult> ClaimAsync(
        string projectId,
        string runId,
        string sessionId,
        HttpContext context,
        ICoordinationOwnerClient owner,
        PostgresAddressedMessageStore messages,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var binding = await owner.GetSessionBindingAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            RequireTurnBoundaryBinding(binding, identity);
            var claim = await messages.ClaimNextAtTurnBoundaryAsync(
                identity, binding.ExecutionFence, binding.ClaimOwner, cancellationToken).ConfigureAwait(false);
            return Results.Json(new AddressedMessageClaimResult(claim));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationOwnerClientException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (AddressedMessageException exception)
        {
            return ToError(exception);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> PresentAsync(
        string projectId,
        string runId,
        string sessionId,
        Guid messageId,
        AddressedMessageClaimFenceRequest request,
        HttpContext context,
        ICoordinationOwnerClient owner,
        PostgresAddressedMessageStore messages,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var binding = await owner.GetSessionBindingAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            RequireTurnBoundaryBinding(binding, identity);
            var envelope = await messages.PresentAsync(
                identity,
                binding.ExecutionFence,
                messageId,
                binding.ClaimOwner,
                request.ClaimFence,
                cancellationToken).ConfigureAwait(false);
            return Results.Json(envelope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationOwnerClientException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (AddressedMessageException exception)
        {
            return ToError(exception);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> AcknowledgeAsync(
        string projectId,
        string runId,
        string sessionId,
        Guid messageId,
        AddressedMessageClaimFenceRequest request,
        HttpContext context,
        ICoordinationOwnerClient owner,
        PostgresAddressedMessageStore messages,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var identity = new SessionIdentity(projectId, runId, sessionId);
            var binding = await owner.GetSessionBindingAsync(
                context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
            RequireSessionBinding(binding, identity);
            var envelope = await messages.AcknowledgeAsync(
                identity,
                messageId,
                binding.ClaimOwner,
                request.ClaimFence,
                cancellationToken,
                currentFence: binding.ExecutionFence).ConfigureAwait(false);
            return Results.Json(envelope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationOwnerClientException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (AddressedMessageException exception)
        {
            return ToError(exception);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> AdmitAsync(
        OwnerOutboundMessage request,
        HttpContext context,
        ICoordinationOwnerClient owner,
        PostgresAddressedMessageStore messages,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            if (request.OwnerMessageId == Guid.Empty)
                throw new AddressedMessageException("owner_message_id_invalid");
            var route = await owner.ValidateMessageRouteAsync(
                context,
                request.Sender.ProjectId,
                request.Sender.RunId,
                new MessageRouteValidationRequest(request),
                cancellationToken).ConfigureAwait(false);
            if (route.Sender != request.Sender || route.Recipient != request.Recipient ||
                request.ReplyToId.HasValue != route.ReplyToMessageId.HasValue)
                throw new AddressedMessageException("owner_route_mismatch");

            var stored = await messages.SendAsync(
                context.User,
                new AddressedMessageDraft(
                    route.Sender,
                    route.Recipient,
                    request.IdempotencyKey,
                    request.DeliveryMode,
                    request.Purpose,
                    request.Kind,
                    request.Payload,
                    route.SenderFence,
                    route.RecipientFence,
                    request.ThreadId,
                    route.ReplyToMessageId,
                    request.RequestId,
                    request.ReplyCorrelationId,
                    request.UserQuote,
                    request.CoordinatorInstructions),
                cancellationToken).ConfigureAwait(false);
            if (stored.Message.Status is not (AddressedMessageStatus.Accepted or
                AddressedMessageStatus.Claimed or AddressedMessageStatus.Delivered))
                throw new AddressedMessageException("message_not_admissible");

            var receipt = new MessageAdmissionReceipt(
                request.OwnerMessageId,
                stored.Message.MessageId,
                stored.Message.ThreadId,
                stored.Message.ThreadSequence,
                AddressedMessageStatus.Accepted,
                stored.Message.RequestId,
                stored.Message.Purpose,
                stored.Message.Sender,
                stored.Message.Recipient,
                stored.Message.SenderFence,
                stored.Message.RecipientFence);
            return Results.Json(
                receipt,
                statusCode: stored.IsDuplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationOwnerClientException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (AddressedMessageException exception)
        {
            var status = exception.Code switch
            {
                "invalid_run_claims" => StatusCodes.Status401Unauthorized,
                "sender_session_mismatch" or "sender_session_not_found" or "session_unavailable" =>
                    StatusCodes.Status403Forbidden,
                "message_not_admissible" or "owner_route_mismatch" => StatusCodes.Status409Conflict,
                "message_idempotency_conflict" => StatusCodes.Status409Conflict,
                "invalid_expiry" or "invalid_request_id" or "invalid_reply_correlation" =>
                    StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status400BadRequest
            };
            return Results.Json(new { error = exception.Code }, statusCode: status);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult ToError(AddressedMessageException exception)
    {
        var status = exception.Code switch
        {
            "invalid_run_claims" => StatusCodes.Status401Unauthorized,
            "message_unavailable" or "message_not_delivered" or "claim_lost" or "turn_boundary_required" =>
                StatusCodes.Status409Conflict,
            "invalid_claim" or "invalid_claim_owner" or "invalid_acknowledgment" =>
                StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(new { error = exception.Code }, statusCode: status);
    }

    private static void RequireSessionBinding(
        CoordinationSessionBinding binding,
        SessionIdentity expected)
    {
        if (binding.Identity != expected || binding.ExecutionFence < 1 ||
            string.IsNullOrWhiteSpace(binding.ClaimOwner))
            throw new CoordinationOwnerClientException(
                "coordination_owner_contract_invalid", StatusCodes.Status502BadGateway);
    }

    private static void RequireTurnBoundaryBinding(
        CoordinationSessionBinding binding,
        SessionIdentity expected)
    {
        RequireSessionBinding(binding, expected);
        if (binding.TurnState != "presenting")
            throw new AddressedMessageException("turn_boundary_required");
    }
}
