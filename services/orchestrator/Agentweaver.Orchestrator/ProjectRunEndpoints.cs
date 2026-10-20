using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>Maps durable new-chat admission, first-message delivery, and fenced run cancellation.</summary>
public static partial class CoordinationEndpoints
{
    /// <summary>Persists browser input before creating the selection needed by the existing Broker binding flow.</summary>
    private static Task<IResult> CreateProjectRunAsync(
        string projectId, CreateProjectRunRequest request, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects, CoordinationOwnerStore store,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        RequireProjectRunRequestShape(context);
        context.Response.Headers.CacheControl = "no-store";
        var key = RequireProjectRunIdempotencyKey(context);
        CoordinationOwnerStore.ValidateProjectRunRequest(request);
        var authority = await projects.ReadProjectRunAuthorityAsync(context, projectId, cancellationToken);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var row = await store.CreateProjectRunAsync(actor, authority.TenantId, projectId, key, request, cancellationToken);
        await projects.CreateInteractiveSelectionAsync(
            context, projectId, row.RunId, row.ExpectedProjectConfigRevision, cancellationToken);
        var refreshed = await projects.ReadProjectRunAuthorityAsync(context, projectId, cancellationToken);
        if (refreshed.TenantId != authority.TenantId)
            throw new CoordinationException("caller_tenant_changed", StatusCodes.Status403Forbidden);
        return Results.Created($"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{row.RunId}",
            new CreateProjectRunResponse(row.RunId, row.RootSessionId, "awaiting_binding",
                new ProjectRunBinding(projectId, row.RunId)));
    }, cancellationToken);

    /// <summary>Accepts the saved root and admits one first message using a stable addressed-message key.</summary>
    private static Task<IResult> StartProjectRunAsync(
        string projectId, string runId, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, EventsAddressedMessageClient events,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        RequireProjectRunRequestShape(context, noBody: true);
        context.Response.Headers.CacheControl = "no-store";
        var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(context, projectId, runId, cancellationToken);
        var result = await store.StartProjectRunAsync(actor, selection.Authorization.TenantId, projectId, runId,
            async (row, token) =>
            {
                await AcceptInteractiveRootAsync(projectId, runId, row.RootSessionId,
                    context, options, projects, events, store, decisions, token);
                var message = await store.SendMessageAsync(actor,
                    new SessionIdentity(projectId, runId, row.RootSessionId),
                    new CoordinationMessageRequest(row.RootSessionId, $"{runId}:first",
                        AddressedMessageDeliveryMode.Immediate, AddressedMessagePurpose.Steering,
                        AddressedMessageKind.Text,
                        JsonSerializer.SerializeToElement(new { text = row.FirstMessage, workflowId = row.WorkflowId })), token);
                var delivery = await store.ClaimOutboundAsync(actor, message.OwnerMessageId, token);
                if (delivery is null)
                {
                    if (message.Status is not ("admitted" or "presented" or "acknowledged"))
                        throw new CoordinationException("first_message_delivery_pending", StatusCodes.Status503ServiceUnavailable);
                    return;
                }
                var outbound = await store.ReadOutboundMessageAsync(message.OwnerMessageId, token);
                var receipt = await events.AdmitAsync(context, outbound, token);
                await store.AdmitDeliveryAsync(actor, new DeliveryIngressRequest(message.OwnerMessageId, receipt), token);
                _ = await store.AcknowledgeOutboundAsync(delivery, token);
            },
            token => RequireUnchangedAuthorizedSelectionAsync(context, projectId, runId, selection, projects, token),
            cancellationToken);
        return Results.Ok(result);
    }, cancellationToken);

    /// <summary>Cancels accepted runs and bound pending requests without delivering the saved first message.</summary>
    private static Task<IResult> CancelProjectRunAsync(
        string projectId, string runId, CancelProjectRunRequest request, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects, EventsAddressedMessageClient events,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        RequireProjectRunRequestShape(context);
        context.Response.Headers.CacheControl = "no-store";
        var key = RequireProjectRunIdempotencyKey(context);
        var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(context, projectId, runId, cancellationToken);
        return Results.Ok(await store.CancelProjectRunAsync(actor, selection, key, request,
            async (row, token) =>
            {
                await AcceptInteractiveRootAsync(projectId, runId, row.RootSessionId,
                    context, options, projects, events, store, decisions, token);
            },
            token => RequireUnchangedAuthorizedSelectionAsync(context, projectId, runId, selection, projects, token),
            cancellationToken));
    }, cancellationToken);

    /// <summary>Shares root admission, budget checks, decision initialization and Events registration with the legacy root route.</summary>
    private static async Task<AcceptedRoot> AcceptInteractiveRootAsync(
        string projectId, string runId, string sessionId, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, EventsAddressedMessageClient events,
        CoordinationOwnerStore store, CoordinatorDecisionOwnerStore decisions, CancellationToken cancellationToken)
    {
        var actor = RequireOwnerActor(context, options, projectId, runId, options.Audience);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(context, projectId, runId, cancellationToken);
        await RequireUnchangedAuthorizedSelectionAsync(context, projectId, runId, selection, projects, cancellationToken);
        var admission = context.RequestServices.GetService<RuntimeRunAdmissionClient>();
        var accepted = await store.AcceptRootAsync(actor, selection, sessionId, cancellationToken,
            admission is null ? null : token => admission.ReadAsync(context, selection, token),
            token => RequireUnchangedAuthorizedSelectionAsync(context, projectId, runId, selection, projects, token));
        var identity = new SessionIdentity(projectId, runId, accepted.RootSessionId);
        await decisions.InitializeRootAsync(actor, identity, selection, cancellationToken);
        await RequireUnchangedAuthorizedSelectionAsync(context, projectId, runId, selection, projects, cancellationToken);
        await events.EnsureSessionAsync(context, identity, cancellationToken);
        return accepted;
    }

    /// <summary>Rejects ambiguous retry headers using the same bounded key syntax as the Gateway contract.</summary>
    private static string RequireProjectRunIdempotencyKey(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || values[0] is not { Length: >= 1 and <= 128 } key ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or ':' or '-')))
            throw new CoordinationException("idempotency_key_invalid", StatusCodes.Status400BadRequest);
        return key;
    }

    /// <summary>Interactive operations accept no query options; start takes no body.</summary>
    private static void RequireProjectRunRequestShape(HttpContext context, bool noBody = false)
    {
        if (context.Request.Query.Count != 0 ||
            noBody && (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count != 0))
            throw new CoordinationException("run_request_invalid", StatusCodes.Status400BadRequest);
    }
}
