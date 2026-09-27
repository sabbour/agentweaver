using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;

namespace Agentweaver.Api.Endpoints;

public static class AddressedMessagesEndpoints
{
    public static void MapAddressedMessagesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{id}")
            .PlatformOrMcp();

        group.MapPost("agent-messages", async (string id, SendAddressedMessage request, HttpContext http,
            IProjectStore projects, IConfiguration configuration, AddressedMessageService messages,
            IRunSubmittingUserResolver resolver, IRunAuthorshipCapabilityStore capabilities, CancellationToken ct) =>
        {
            var (project, failure) = await AuthorizeAsync(id, http, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            var (author, authorFailure) = await RunAuthorship.ResolveMessageAsync(
                http, id, resolver, capabilities, ct);
            if (authorFailure is not null) return authorFailure;
            if (author!.SourceRunId is null) return Conflict("sender_run_required");
            var reader = new SquadReader(project!.WorkingDirectory);
            try
            {
                if (reader.DetectLayout().HasConflict)
                    return Conflict("layout_conflict");
                var team = reader.ReadTeam();
                var message = await messages.SendAsync(id, author!, request,
                    name => team?.Members.Any(m => m.Status == CastMemberStatus.Active
                        && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == true, ct);
                return Results.Created($"/api/projects/{id}/agent-messages/{message.Id}", message);
            }
            catch (AddressedMessageError error) { return Conflict(error.Code); }
        });

        group.MapGet("agent-messages", async (string id, string? run_id, int? limit, HttpContext http,
            IProjectStore projects, IConfiguration configuration, AddressedMessageService messages,
            IRunSubmittingUserResolver resolver, IRunAuthorshipCapabilityStore capabilities, CancellationToken ct) =>
        {
            var (project, failure) = await AuthorizeAsync(id, http, projects, configuration, ProjectRole.Viewer, ct);
            if (failure is not null) return failure;
            var (author, authorFailure) = await RunAuthorship.ResolveMessageAsync(
                http, id, resolver, capabilities, ct);
            if (authorFailure is not null) return authorFailure;
            if (author!.SourceRunId is not null) run_id = author.SourceRunId;
            var reader = new SquadReader(project!.WorkingDirectory);
            if (reader.DetectLayout().HasConflict) return Conflict("layout_conflict");
            var team = reader.ReadTeam();
            await messages.ReconcileAsync(id, name => team?.Members.Any(m =>
                m.Status == CastMemberStatus.Active
                && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == true, ct);
            return Results.Ok(await messages.ListAsync(id, run_id, limit ?? 50, ct));
        });

        group.MapGet("agent-messages/{messageId}", async (string id, string messageId, HttpContext http,
            IProjectStore projects, IConfiguration configuration, AddressedMessageService messages,
            IRunSubmittingUserResolver resolver, IRunAuthorshipCapabilityStore capabilities, CancellationToken ct) =>
        {
            var (project, failure) = await AuthorizeAsync(id, http, projects, configuration, ProjectRole.Viewer, ct);
            if (failure is not null) return failure;
            var (author, authorFailure) = await RunAuthorship.ResolveMessageAsync(
                http, id, resolver, capabilities, ct);
            if (authorFailure is not null) return authorFailure;
            var reader = new SquadReader(project!.WorkingDirectory);
            if (reader.DetectLayout().HasConflict) return Conflict("layout_conflict");
            var team = reader.ReadTeam();
            await messages.ReconcileAsync(id, name => team?.Members.Any(m =>
                m.Status == CastMemberStatus.Active
                && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) == true, ct);
            var message = await messages.GetAsync(id, messageId, ct);
            if (message is null || author!.SourceRunId is { } run
                && message.SourceRunId != run && message.TargetRunId != run)
                return Results.NotFound();
            return Results.Ok(message);
        });

        group.MapPost("agent-messages/claim", async (string id, ClaimAddressedMessage request, HttpContext http,
            IProjectStore projects, IConfiguration configuration, AddressedMessageService messages,
            IRunSubmittingUserResolver resolver, IRunAuthorshipCapabilityStore capabilities, CancellationToken ct) =>
        {
            var (project, failure) = await AuthorizeAsync(id, http, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            var (author, authorFailure) = await RunAuthorship.ResolveMessageAsync(
                http, id, resolver, capabilities, ct);
            if (authorFailure is not null) return authorFailure;
            if (author!.SourceRunId is null) return Conflict("recipient_run_required");
            var recipientFailure = CheckRecipient(project!, author.AgentName);
            if (recipientFailure is not null) return recipientFailure;
            try
            {
                return Results.Ok(await messages.ClaimAsync(
                    id, author.SourceRunId, author.AgentName, request.Owner, ct));
            }
            catch (AddressedMessageError error) { return Conflict(error.Code); }
        });

        group.MapPost("agent-messages/{messageId}/deliver", async (string id, string messageId, DeliverAddressedMessage request,
            HttpContext http, IProjectStore projects, IConfiguration configuration, AddressedMessageService messages,
            IRunSubmittingUserResolver resolver, IRunAuthorshipCapabilityStore capabilities, CancellationToken ct) =>
        {
            var (project, failure) = await AuthorizeAsync(id, http, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            var (author, authorFailure) = await RunAuthorship.ResolveMessageAsync(
                http, id, resolver, capabilities, ct);
            if (authorFailure is not null) return authorFailure;
            if (author!.SourceRunId is null) return Conflict("recipient_run_required");
            var recipientFailure = CheckRecipient(project!, author.AgentName);
            if (recipientFailure is not null) return recipientFailure;
            try
            {
                return Results.Ok(await messages.DeliverAsync(
                    id, messageId, author.SourceRunId, author.AgentName, request.Owner, request.Fence, ct));
            }
            catch (AddressedMessageError error) { return Conflict(error.Code); }
        });

        group.MapPost("agent-messages/{messageId}/acknowledge", async (string id, string messageId, HttpContext http,
            IProjectStore projects, IConfiguration configuration, AddressedMessageService messages,
            IRunSubmittingUserResolver resolver, IRunAuthorshipCapabilityStore capabilities, CancellationToken ct) =>
        {
            var (project, failure) = await AuthorizeAsync(id, http, projects, configuration, ProjectRole.Contributor, ct);
            if (failure is not null) return failure;
            var (author, authorFailure) = await RunAuthorship.ResolveMessageAsync(
                http, id, resolver, capabilities, ct);
            if (authorFailure is not null) return authorFailure;
            if (author!.SourceRunId is null) return Conflict("recipient_run_required");
            var recipientFailure = CheckRecipient(project!, author.AgentName);
            if (recipientFailure is not null) return recipientFailure;
            try
            {
                return Results.Ok(await messages.AcknowledgeAsync(
                    id, messageId, author.SourceRunId, author.AgentName, ct));
            }
            catch (AddressedMessageError error) { return Conflict(error.Code); }
        });
    }

    private static async Task<(Project? Project, IResult? Failure)> AuthorizeAsync(
        string id, HttpContext http, IProjectStore projects, IConfiguration config,
        ProjectRole role, CancellationToken ct)
    {
        if (!ProjectId.TryParse(id, out var projectId))
            return (null, Results.BadRequest(new { error = "invalid_project_id" }));
        var project = await projects.GetAsync(projectId, ct);
        if (project is null) return (null, Results.NotFound());
        var failure = await ProjectAuthorization.RequireAccessAsync(http, project, config, role, ct);
        return (project, failure);
    }

    private static IResult Conflict(string code) => Results.Conflict(new { error = code });

    private static IResult? CheckRecipient(Project project, string agentName)
    {
        var reader = new SquadReader(project.WorkingDirectory);
        if (reader.DetectLayout().HasConflict) return Conflict("layout_conflict");
        return reader.ReadTeam()?.Members.Any(m => m.Status == CastMemberStatus.Active
            && string.Equals(m.Name, agentName, StringComparison.OrdinalIgnoreCase)) == true
            ? null : Conflict("recipient_unavailable");
    }
}

public sealed record ClaimAddressedMessage(string Owner);
public sealed record DeliverAddressedMessage(string Owner, long Fence);
