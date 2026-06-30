using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class TeamTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "team_get"), Description("Get the current team composition for a project.")]
    public async Task<string> TeamGetAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}/team", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "team_cast"), Description(
        "Cast the team for a project. Can create a proposal, confirm an existing proposal, or create+confirm in one step. " +
        "If confirm_proposal_id is provided, confirms that proposal. Otherwise creates a new proposal with the given goal. " +
        "Set confirm=true to automatically confirm the new proposal.")]
    public async Task<string> TeamCastAsync(
        [Description("Project ID")] string project_id,
        [Description("Goal description for the new team (required unless confirm_proposal_id is set)")] string? goal,
        [Description("ID of an existing proposal to confirm (skips creation)")] string? confirm_proposal_id,
        [Description("Automatically confirm the newly created proposal (default false)")] bool confirm,
        [Description("Casting mode: 'free_text' (default), 'scenario', 'analysis', or 'manual'")] string mode = "free_text",
        [Description("Intent for confirmation: 'new' (default) replaces the team, 'merge' adds to existing")] string intent = "new",
        CancellationToken ct = default)
    {
        try
        {
            if (confirm_proposal_id is not null)
            {
                var confirmBody = new { intent };
                var confirmResult = await api.PostAsync<JsonElement>(
                    $"/api/projects/{Uri.EscapeDataString(project_id)}/casting/proposals/{Uri.EscapeDataString(confirm_proposal_id)}/confirm",
                    confirmBody, ct);
                return JsonSerializer.Serialize(confirmResult, JsonOpts);
            }

            if (string.IsNullOrWhiteSpace(goal))
                throw new McpApiException(400, "Either goal or confirm_proposal_id must be provided.");

            var body = new { goal, mode };
            var proposal = await api.PostAsync<JsonElement>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/casting/proposals", body, ct);

            if (!confirm)
                return JsonSerializer.Serialize(proposal, JsonOpts);

            // Extract proposal_id to confirm
            string? proposalId = null;
            if (proposal.TryGetProperty("proposal_id", out var pid))
                proposalId = pid.GetString();

            if (proposalId is null)
                throw new McpApiException(0, "API did not return a proposal_id in the proposal response.");

            var confirmAutoBody = new { intent };
            var confirmed = await api.PostAsync<JsonElement>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/casting/proposals/{Uri.EscapeDataString(proposalId)}/confirm",
                confirmAutoBody, ct);
            return JsonSerializer.Serialize(confirmed, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "team_member_add"), Description("Add a new member to the project team.")]
    public async Task<string> TeamMemberAddAsync(
        [Description("Project ID")] string project_id,
        [Description("Member name")] string name,
        [Description("Role ID from the catalog")] string role_id,
        [Description("Model ID override (optional)")] string? model_id,
        CancellationToken ct)
    {
        try
        {
            var body = new { name, role_id, model_id };
            var result = await api.PostAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}/team/members", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "team_member_retire"), Description("Remove (retire) a member from the project team.")]
    public async Task<string> TeamMemberRetireAsync(
        [Description("Project ID")] string project_id,
        [Description("Member name")] string member_name,
        CancellationToken ct)
    {
        try
        {
            await api.DeleteAsync($"/api/projects/{Uri.EscapeDataString(project_id)}/team/members/{Uri.EscapeDataString(member_name)}", ct);
            return $"Team member '{member_name}' retired successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "team_member_get_charter"), Description("Get the charter document for a team member.")]
    public async Task<string> TeamMemberGetCharterAsync(
        [Description("Project ID")] string project_id,
        [Description("Member name")] string member_name,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/team/members/{Uri.EscapeDataString(member_name)}/charter", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
