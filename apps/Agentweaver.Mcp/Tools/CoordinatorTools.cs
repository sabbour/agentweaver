using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class CoordinatorTools(AgentweaverApiClient api)
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

    [McpServerTool(Name = "coordinator_work_plan_get"), Description("Get the work plan for a Coordinator run: the decomposed subtasks with their assigned agent, selected model, status, child run id, and the dependency edges between subtasks. Returns null when no work plan has been drafted yet.")]
    public async Task<string> CoordinatorWorkPlanGetAsync(
        [Description("Coordinator run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/work-plan", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "coordinator_children_get"), Description("List the child runs dispatched by a Coordinator run, each paired with its subtask status, assigned agent, selected model, and child run status. Empty when nothing has been dispatched.")]
    public async Task<string> CoordinatorChildrenGetAsync(
        [Description("Coordinator run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/children", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "coordinator_steer"), Description("Steer a Coordinator run. Use 'stop' to cancel active subagents immediately; 'redirect' or 'amend' to inject guidance at the targeted subagent's next turn boundary; or a recovery verb (e.g. 'recover') to reset blocked/failed/parked subtasks and auto-resume the dispatch loop. Omit target_child_run_id to broadcast to every active child. instruction is required for redirect/amend and optional for stop/recovery verbs. Pause is not supported.")]
    public async Task<string> CoordinatorSteerAsync(
        [Description("Coordinator run ID")] string run_id,
        [Description("Steering verb: 'stop', 'redirect', 'amend', or a recovery verb such as 'recover'")] string kind,
        [Description("Direction relayed to the targeted subagent(s); required for redirect/amend, optional for stop and recovery verbs")] string? instruction,
        [Description("Target child run ID (optional); omit to broadcast to every active child")] string? target_child_run_id,
        CancellationToken ct)
    {
        try
        {
            var body = new { kind, targetChildRunId = target_child_run_id, instruction };
            var result = await api.PostAsync<JsonElement>($"/api/runs/{run_id}/steer", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "orchestration_topology"), Description("Get a one-shot topology snapshot for a Coordinator run by combining the work plan and child runs into a current view of subtasks, dependency edges, and dispatched children. For the live graph, point run_watch at the coordinator run id and consume its coordinator.topology, subtask.*, and coordinator.steering events.")]
    public async Task<string> OrchestrationTopologyAsync(
        [Description("Coordinator run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var workPlan = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/work-plan", ct);
            var children = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/children", ct);
            var snapshot = new
            {
                coordinatorRunId = run_id,
                workPlan,
                children
            };
            return JsonSerializer.Serialize(snapshot, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
