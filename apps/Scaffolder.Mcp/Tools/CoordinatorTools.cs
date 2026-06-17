using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Scaffolder.Mcp.Tools;

[McpServerToolType]
public sealed class CoordinatorTools(ScaffolderApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "coordinator_start"), Description("Start a Coordinator orchestration for a project from a plain-language goal. The coordinator drafts a confirmable outcome spec and suspends at the confirmation gate; no work is dispatched until the spec is confirmed.")]
    public async Task<string> CoordinatorStartAsync(
        [Description("Project ID")] string project_id,
        [Description("The outcome the coordinator should draft a spec for")] string goal,
        [Description("Model id override (optional); falls back to the project default, then the role default")] string? model_id,
        CancellationToken ct)
    {
        try
        {
            var body = new { goal, modelId = model_id };
            var result = await api.PostAsync<JsonElement>($"/api/projects/{project_id}/orchestrations", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "coordinator_outcome_spec_get"), Description("Get the current persisted outcome spec for a Coordinator run.")]
    public async Task<string> CoordinatorOutcomeSpecGetAsync(
        [Description("Coordinator run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/outcome-spec", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "coordinator_outcome_spec_confirm"), Description("Confirm the drafted outcome spec for a Coordinator run, resuming the suspended run past the confirmation gate.")]
    public async Task<string> CoordinatorOutcomeSpecConfirmAsync(
        [Description("Coordinator run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<JsonElement>($"/api/runs/{run_id}/outcome-spec/confirm", null, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "coordinator_outcome_spec_revise"), Description("Request a revision of the drafted outcome spec for a Coordinator run. The coordinator re-drafts using the feedback and re-suspends at the confirmation gate.")]
    public async Task<string> CoordinatorOutcomeSpecReviseAsync(
        [Description("Coordinator run ID")] string run_id,
        [Description("Revision guidance for the coordinator")] string feedback,
        CancellationToken ct)
    {
        try
        {
            var body = new { feedback };
            var result = await api.PostAsync<JsonElement>($"/api/runs/{run_id}/outcome-spec/revise", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
