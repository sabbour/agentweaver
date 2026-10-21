using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

    /// <summary>Cancels project runs using fresh unbound Contributor, Owner, or tenant-admin authority.</summary>
    private static Task<IResult> CancelProjectRunAsync(
        string projectId, string runId, CancelProjectRunRequest request, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects, CoordinationOwnerStore store,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
    {
        RequireProjectRunRequestShape(context);
        context.Response.Headers.CacheControl = "no-store";
        var key = RequireProjectRunIdempotencyKey(context);
        var selection = await projects.ReadSelectionForCancelAsync(context, projectId, runId, cancellationToken);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        return Results.Ok(await store.CancelProjectRunAsync(actor, selection, key, request,
            async token =>
            {
                var refreshed = await projects.ReadSelectionForCancelAsync(context, projectId, runId, token);
                if (refreshed.Authorization.TenantId != selection.Authorization.TenantId ||
                    CoordinationOwnerStore.HashSelection(refreshed.Selection) !=
                        CoordinationOwnerStore.HashSelection(selection.Selection))
                    throw new CoordinationException("accepted_run_conflict", StatusCodes.Status409Conflict);
            },
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
    internal static string RequireProjectRunIdempotencyKey(HttpContext context)
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

/// <summary>
/// Maps project-scoped run reads that do not require a run-bound token.
/// </summary>
public static class ProjectRunEndpoints
{
    private static readonly HashSet<string> ListQueryNames =
        new(["state", "cursor", "pageSize"], StringComparer.Ordinal);

    /// <summary>
    /// Registers <c>GET /api/projects/{projectId}/runs</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapProjectRunEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId}/runs", ListRunsAsync).RequireAuthorization();
        // Metrics use the same fresh authority and run-bound narrowing as the run list.
        app.MapGet("/api/projects/{projectId}/metrics", ReadMetricsAsync).RequireAuthorization();
        return app;
    }

    /// <summary>Returns stable query failures and reads bounded, uncached UTC project metrics.</summary>
    internal static Task<IResult> ReadMetricsAsync(
        string projectId, HttpContext context, ProjectsRunSelectionClient projects,
        ProjectRunReadStore store, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectMetricsRange range;
            try
            {
                var query = ReadSingleValuedQuery(context.Request.Query, new(["from", "to", "bucket"], StringComparer.Ordinal));
                range = ProjectMetricsRange.Parse(query.GetValueOrDefault("from"), query.GetValueOrDefault("to"),
                    query.GetValueOrDefault("bucket"), timeProvider.GetUtcNow());
            }
            catch (Exception exception) when (exception is ArgumentException ||
                exception is CoordinationException { Code: "query_invalid" })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "query_invalid",
                    extensions: new Dictionary<string, object?> { ["code"] = "query_invalid" });
            }
            var authority = await projects.ReadProjectReadAuthorityAsync(context, projectId, cancellationToken);
            return Results.Ok(await store.ReadMetricsAsync(
                authority.TenantId, projectId, authority.BoundRunId, range, cancellationToken));
        }, cancellationToken);

    /// <summary>
    /// Lists the project's runs newest first after a fresh Projects read-authority check.
    /// </summary>
    /// <remarks>
    /// An unbound or project-bound caller sees every run in the project. A run-bound caller sees only its run.
    /// Unknown or repeated query parameters return 400.
    /// </remarks>
    private static Task<IResult> ListRunsAsync(
        string projectId,
        HttpContext context,
        ProjectsRunSelectionClient projects,
        ProjectRunReadStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = ReadSingleValuedQuery(context.Request.Query, ListQueryNames);
            var states = ProjectRunReadStore.ParseStateFilter(query.GetValueOrDefault("state"));
            var pageSize = ParsePageSize(query.GetValueOrDefault("pageSize"));
            var authorization = await projects.ReadProjectReadAuthorityAsync(
                context, projectId, cancellationToken).ConfigureAwait(false);
            var page = await store.ListAsync(
                authorization.TenantId,
                projectId,
                authorization.BoundRunId,
                states,
                query.GetValueOrDefault("cursor"),
                pageSize,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(page);
        }, cancellationToken);

    /// <summary>
    /// Reads query parameters, rejecting names outside <paramref name="allowed"/> and repeated values.
    /// </summary>
    /// <exception cref="CoordinationException">A parameter is unknown or repeated.</exception>
    internal static Dictionary<string, string> ReadSingleValuedQuery(IQueryCollection query, HashSet<string> allowed)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in query)
        {
            if (!allowed.Contains(name) || value.Count != 1 || value[0] is null)
                throw new CoordinationException("query_invalid", StatusCodes.Status400BadRequest);
            values[name] = value[0]!;
        }
        return values;
    }

    /// <summary>
    /// Parses an optional page size, defaulting to <see cref="ProjectRunReadStore.DefaultPageSize"/>.
    /// </summary>
    /// <exception cref="CoordinationException">The value is not an integer from 1 to the maximum.</exception>
    internal static int ParsePageSize(string? value)
    {
        if (value is null)
            return ProjectRunReadStore.DefaultPageSize;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pageSize) ||
            pageSize is < 1 or > ProjectRunReadStore.MaximumPageSize)
            throw new CoordinationException("page_size_invalid", StatusCodes.Status400BadRequest);
        return pageSize;
    }
}
