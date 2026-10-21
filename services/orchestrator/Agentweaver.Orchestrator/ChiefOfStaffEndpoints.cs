using System.Collections.Immutable;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Maps the private Chief of Staff conversation and Priority routes under <c>/api/chief-of-staff</c>.
/// </summary>
/// <remarks>
/// Every route reads fresh, unbound caller authority from Projects &amp; Config. Rows are scoped to the caller.
/// Content that belongs to a project the caller can no longer read is removed from every response.
/// These routes never start, cancel, or answer anything: run creation stays with
/// <c>POST /api/projects/{projectId}/runs</c>, and gates stay with the coordination decision routes.
/// </remarks>
public static class ChiefOfStaffEndpoints
{
    private static readonly HashSet<string> ListQueryNames = new(["cursor", "pageSize"], StringComparer.Ordinal);
    private static readonly HashSet<string> NoQueryNames = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RemoveLinkQueryNames = new(["expectedRevision"], StringComparer.Ordinal);

    /// <summary>Registers the Chief of Staff routes.</summary>
    public static IEndpointRouteBuilder MapChiefOfStaffEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chief-of-staff").RequireAuthorization();
        group.MapGet("/conversations", ListConversationsAsync);
        group.MapPost("/conversations", BindConversationAsync);
        group.MapGet("/conversations/{conversationId}", GetConversationAsync);
        group.MapGet("/priorities", ListPrioritiesAsync);
        group.MapPost("/priorities", CreatePriorityAsync);
        group.MapPut("/priorities/order", ReorderPrioritiesAsync);
        group.MapGet("/priorities/{priorityId}", GetPriorityAsync);
        group.MapPatch("/priorities/{priorityId}", UpdatePriorityAsync);
        group.MapPost("/priorities/{priorityId}/links", AddLinkAsync);
        group.MapDelete("/priorities/{priorityId}/links/{linkId}", RemoveLinkAsync);
        return app;
    }

    /// <summary>Lists the caller's conversations newest first. Entries in unreadable projects are redacted.</summary>
    private static Task<IResult> ListConversationsAsync(
        HttpContext context, OrchestratorOptions options, ProjectsRunSelectionClient projects,
        ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, ListQueryNames);
            var pageSize = ProjectRunEndpoints.ParsePageSize(query.GetValueOrDefault("pageSize"));
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            var page = await store.ListConversationsAsync(
                actor, authority.TenantId, query.GetValueOrDefault("cursor"), pageSize, cancellationToken);
            return Results.Ok(page with { Items = [.. page.Items.Select(item => Redact(item, authority))] });
        }, cancellationToken);

    /// <summary>Binds a run the caller started to the caller's conversation list. Repeats return the same entry.</summary>
    private static Task<IResult> BindConversationAsync(
        BindChiefOfStaffConversationRequest request, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            RequireProjectRead(authority, request.ProjectId);
            return Results.Ok(await store.BindConversationAsync(
                actor, authority.TenantId, request.ProjectId, request.RunId, cancellationToken));
        }, cancellationToken);

    /// <summary>Reads one of the caller's conversations. An entry in an unreadable project is redacted.</summary>
    private static Task<IResult> GetConversationAsync(
        string conversationId, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            var conversation = await store.GetConversationAsync(actor, authority.TenantId, conversationId, cancellationToken)
                ?? throw new CoordinationException("conversation_not_found", StatusCodes.Status404NotFound);
            return Results.Ok(Redact(conversation, authority));
        }, cancellationToken);

    /// <summary>Lists the caller's Priorities in order with links resolved against current access.</summary>
    private static Task<IResult> ListPrioritiesAsync(
        HttpContext context, OrchestratorOptions options, ProjectsRunSelectionClient projects,
        ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            var list = await store.ListPrioritiesAsync(actor, authority.TenantId, cancellationToken);
            return Results.Ok(list with { Items = [.. list.Items.Select(item => Redact(item, authority))] });
        }, cancellationToken);

    /// <summary>Creates a Priority. Requires one <c>Idempotency-Key</c>; a replay returns 200 with the same Priority.</summary>
    private static Task<IResult> CreatePriorityAsync(
        CreateChiefOfStaffPriorityRequest request, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var key = CoordinationEndpoints.RequireProjectRunIdempotencyKey(context);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            var (priority, created) = await store.CreatePriorityAsync(
                actor, authority.TenantId, key, request, cancellationToken);
            var body = Redact(priority, authority);
            return created
                ? Results.Created($"/api/chief-of-staff/priorities/{priority.PriorityId}", body)
                : Results.Ok(body);
        }, cancellationToken);

    /// <summary>Reads one of the caller's Priorities.</summary>
    private static Task<IResult> GetPriorityAsync(
        string priorityId, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            var priority = await store.GetPriorityAsync(actor, authority.TenantId, priorityId, cancellationToken)
                ?? throw new CoordinationException("priority_not_found", StatusCodes.Status404NotFound);
            return Results.Ok(Redact(priority, authority));
        }, cancellationToken);

    /// <summary>Changes Priority content at the expected revision. Never changes execution state.</summary>
    private static Task<IResult> UpdatePriorityAsync(
        string priorityId, UpdateChiefOfStaffPriorityRequest request, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects, ChiefOfStaffStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            return Results.Ok(Redact(
                await store.UpdatePriorityAsync(actor, authority.TenantId, priorityId, request, cancellationToken),
                authority));
        }, cancellationToken);

    /// <summary>Replaces the order of every Priority at the expected order revision.</summary>
    private static Task<IResult> ReorderPrioritiesAsync(
        ReorderChiefOfStaffPrioritiesRequest request, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            var list = await store.ReorderPrioritiesAsync(actor, authority.TenantId, request, cancellationToken);
            return Results.Ok(list with { Items = [.. list.Items.Select(item => Redact(item, authority))] });
        }, cancellationToken);

    /// <summary>Adds a typed link. The caller must currently read the linked project.</summary>
    private static Task<IResult> AddLinkAsync(
        string priorityId, AddChiefOfStaffPriorityLinkRequest request, HttpContext context,
        OrchestratorOptions options, ProjectsRunSelectionClient projects, ChiefOfStaffStore store,
        CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, NoQueryNames);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            RequireProjectRead(authority, request.ProjectId);
            return Results.Ok(Redact(
                await store.AddLinkAsync(actor, authority.TenantId, priorityId, request, cancellationToken),
                authority));
        }, cancellationToken);

    /// <summary>Removes a link at the expected revision named by the required <c>expectedRevision</c> query.</summary>
    /// <remarks>Removal does not need project access, so a user can clean up links to projects they lost.</remarks>
    private static Task<IResult> RemoveLinkAsync(
        string priorityId, string linkId, HttpContext context, OrchestratorOptions options,
        ProjectsRunSelectionClient projects, ChiefOfStaffStore store, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = ProjectRunEndpoints.ReadSingleValuedQuery(context.Request.Query, RemoveLinkQueryNames);
            if (!long.TryParse(query.GetValueOrDefault("expectedRevision"), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var expectedRevision) || expectedRevision < 1)
                throw new CoordinationException("expected_revision_invalid", StatusCodes.Status400BadRequest);
            var (actor, authority) = await ReadCallerAsync(context, options, projects, cancellationToken);
            return Results.Ok(Redact(
                await store.RemoveLinkAsync(actor, authority.TenantId, priorityId, linkId, expectedRevision,
                    cancellationToken),
                authority));
        }, cancellationToken);

    private static async Task<(CoordinationActor Actor, ProjectsAuthorizationContext Authority)> ReadCallerAsync(
        HttpContext context, OrchestratorOptions options, ProjectsRunSelectionClient projects,
        CancellationToken cancellationToken)
    {
        var authority = await projects.ReadCallerAuthorityAsync(context, cancellationToken).ConfigureAwait(false);
        return (CoordinationIdentity.RequireActor(context.User, options.Issuer), authority);
    }

    private static void RequireProjectRead(ProjectsAuthorizationContext authority, string? projectId)
    {
        if (projectId is null)
            throw new CoordinationException("project_id_invalid", StatusCodes.Status400BadRequest);
        CoordinationIdentity.ValidateIdentity(projectId, "project_id");
        if (!ProjectsRunSelectionClient.CanReadProject(authority, projectId))
            throw new CoordinationException("project_read_permission_denied", StatusCodes.Status403Forbidden);
    }

    /// <summary>Removes project-owned fields from a conversation in a project the caller can no longer read.</summary>
    internal static ChiefOfStaffConversation Redact(
        ChiefOfStaffConversation conversation, ProjectsAuthorizationContext authority) =>
        conversation.ProjectId is not null && ProjectsRunSelectionClient.CanReadProject(authority, conversation.ProjectId)
            ? conversation
            : new ChiefOfStaffConversation(
                conversation.ConversationId, "unavailable", null, null, null, null, null, null, conversation.CreatedAt);

    /// <summary>Removes every link field except identity and kind from links in unreadable projects.</summary>
    internal static ChiefOfStaffPriority Redact(ChiefOfStaffPriority priority, ProjectsAuthorizationContext authority) =>
        priority with
        {
            Links = [.. priority.Links.Select(link =>
                link.ProjectId is not null && ProjectsRunSelectionClient.CanReadProject(authority, link.ProjectId)
                    ? link
                    : new ChiefOfStaffPriorityLink(link.LinkId, link.Kind, "unavailable",
                        null, null, null, null, null, null, null))]
        };
}
