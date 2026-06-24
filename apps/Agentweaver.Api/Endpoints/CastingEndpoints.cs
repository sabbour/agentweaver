using System.Text.Encodings.Web;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;
using Agentweaver.Squad.Squad;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Sync;

namespace Agentweaver.Api.Endpoints;

public static class CastingEndpoints
{
    public static void MapCastingEndpoints(this WebApplication app)
    {
// GET /api/casting/templates — list all team templates from the catalog
app.MapGet("/api/casting/templates", (CastingService castingService, CatalogReader catalog) =>
{
    var templates = catalog.LoadTemplates();
    return Results.Ok(templates.Select(CastingMappings.ToDto));
});

// GET /api/projects/{id}/casting/universes — list allowed universe names for a project
app.MapGet("/api/projects/{id}/casting/universes", async (
    string id,
    CastingService castingService,
    CancellationToken ct) =>
{
    try
    {
        var universes = await castingService.GetAllowlistUniversesAsync(id, ct);
        return Results.Ok(new { universes });
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// GET /api/catalog/roles — list all available role archetypes
app.MapGet("/api/catalog/roles", (CastingService castingService) =>
{
    var roles = castingService.GetAllRoles();
    return Results.Ok(roles);
});

// POST /api/projects/{id}/casting/proposals — create a new proposal
app.MapPost("/api/projects/{id}/casting/proposals", async (
    string id,
    CreateProposalRequest request,
    CastingService castingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var mode = (request.Mode ?? string.Empty).ToLowerInvariant();

    if (mode is not ("scenario" or "free_text" or "analysis" or "manual"))
        return Results.BadRequest(new { error = "mode must be scenario, free_text, analysis, or manual." });

    try
    {
        switch (mode)
        {
            case "scenario":
            {
                if (string.IsNullOrWhiteSpace(request.TemplateId))
                    return Results.BadRequest(new { error = "template_id is required for scenario mode." });

                var (proposal, _) = await castingService.ProposeScenarioCastAsync(
                    id, request.TemplateId, request.Universe, ct);
                return Results.Ok(CastingMappings.ToDto(proposal));
            }
            case "free_text":
            {
                var (proposal, _) = await castingService.ProposeFreetextCastAsync(
                    id, request.Goal ?? "", request.Universe, request.ModelId, ct, request.TeamSize);
                return Results.Ok(CastingMappings.ToDto(proposal));
            }
            case "analysis":
            {
                var (proposal, _) = await castingService.ProposeAnalysisCastAsync(
                    id, request.Universe, request.ModelId, ct, request.TeamSize);
                return Results.Ok(CastingMappings.ToDto(proposal));
            }
            case "manual":
            {
                if (request.RoleIds is null || request.RoleIds.Count == 0)
                    return Results.BadRequest(new { error = "role_ids is required for manual mode." });

                var (proposal, _) = await castingService.ProposeManualCastAsync(
                    id, request.RoleIds, request.Universe, ct);
                return Results.Ok(CastingMappings.ToDto(proposal));
            }
            default:
                return Results.BadRequest(new { error = "mode must be scenario, free_text, analysis, or manual." });
        }
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (SquadLayoutConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "layout_conflict" });
    }
    catch (ModelRunFailedException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "model_run_failed" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to create proposal for project {ProjectId}", id);
        return Results.Problem("Failed to create proposal.", statusCode: 500);
    }
});

// GET /api/projects/{id}/casting/proposals — list active proposals for a project
app.MapGet("/api/projects/{id}/casting/proposals", (
    string id,
    CastProposalStore proposalStore) =>
{
    var proposals = proposalStore.ListByProject(id);
    return Results.Ok(proposals.Select(p => CastingMappings.ToDto(p.Proposal)));
});

// GET /api/projects/{id}/casting/proposals/{proposalId} — get proposal
app.MapGet("/api/projects/{id}/casting/proposals/{proposalId}", (
    string id,
    string proposalId,
    CastProposalStore proposalStore) =>
{
    var (proposal, _) = proposalStore.Get(id, proposalId);
    if (proposal is null) return Results.NotFound();
    return Results.Ok(CastingMappings.ToDto(proposal));
});

// PATCH /api/projects/{id}/casting/proposals/{proposalId} — amend proposal
app.MapMethods("/api/projects/{id}/casting/proposals/{proposalId}", ["PATCH"], async (
    string id,
    string proposalId,
    AmendProposalRequest request,
    CastingService castingService,
    CatalogReader catalog,
    CancellationToken ct) =>
{
    IReadOnlyList<Agentweaver.Squad.Model.ProposedMember>? members = null;
    if (request.Members is not null)
    {
        var converted = new List<Agentweaver.Squad.Model.ProposedMember>();
        foreach (var m in request.Members)
        {
            var role = new Agentweaver.Squad.Model.Role(
                Id: m.Role.Id,
                Title: m.Role.Title,
                Summary: m.Role.Summary,
                DefaultModel: m.Role.DefaultModel,
                Capabilities: [],
                Responsibilities: [],
                Boundaries: []);
            converted.Add(new Agentweaver.Squad.Model.ProposedMember(
                ProposedName: m.ProposedName,
                Role: role,
                CharterMarkdown: m.CharterMarkdown,
                IsNamed: m.IsNamed,
                DefaultModel: m.DefaultModel,
                Justification: m.Justification));
        }
        members = converted;
    }

    try
    {
        var updated = await castingService.AmendProposalAsync(id, proposalId, members, request.Universe, ct);
        return Results.Ok(CastingMappings.ToDto(updated));
    }
    catch (ProposalNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "project_unavailable" });
    }
    catch (ArgumentException ex)
    {
        return Results.UnprocessableEntity(new { error = ex.Message });
    }
});

// POST /api/projects/{id}/casting/proposals/{proposalId}/confirm — confirm proposal
app.MapPost("/api/projects/{id}/casting/proposals/{proposalId}/confirm", async (
    string id,
    string proposalId,
    ConfirmProposalRequest request,
    CastingService castingService,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    try
    {
        var team = await castingService.ConfirmProposalAsync(id, proposalId, request.Intent, ct);
        var teamDto = new TeamDto
        {
            ProjectName = team.ProjectName,
            Universe = team.Universe,
            Members = team.Members.Select(m => CastingMappings.ToDto(m)).ToList(),
            Layout = "canonical",
            MigrationAvailable = false,
        };
        return Results.Ok(teamDto);
    }
    catch (ProposalNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectNotFoundException)
    {
        return Results.NotFound();
    }
    catch (ProjectUnavailableException)
    {
        return Results.Conflict(new { error = "project_unavailable", code = "project_unavailable" });
    }
    catch (RequiresChoiceException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "requires_choice" });
    }
    catch (SquadLayoutConflictException ex)
    {
        return Results.Conflict(new { error = ex.Message, code = "layout_conflict" });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to confirm proposal {ProposalId} for project {ProjectId}", proposalId, id);
        return Results.Problem("Failed to confirm proposal.", statusCode: 500);
    }
});

// DELETE /api/projects/{id}/casting/proposals/{proposalId} — reject proposal
app.MapDelete("/api/projects/{id}/casting/proposals/{proposalId}", (
    string id,
    string proposalId,
    CastingService castingService) =>
{
    try
    {
        castingService.RejectProposal(id, proposalId);
        return Results.NoContent();
    }
    catch (ProposalNotFoundException)
    {
        return Results.NotFound();
    }
});
    }
}
