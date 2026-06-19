using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

internal sealed record ReadyAllResponse([property: JsonPropertyName("moved")] int Moved);

[McpServerToolType]
public sealed class BacklogTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "backlog_capture_task"), Description("Capture a new task into the project backlog.")]
    public async Task<string> BacklogCaptureTaskAsync(
        [Description("Project ID")] string project_id,
        [Description("Task title (required)")] string title,
        [Description("Optional task description")] string? description,
        CancellationToken ct)
    {
        try
        {
            var body = new { title, description };
            var result = await api.PostAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/tasks", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_edit_task"), Description("Edit the title and/or description of a backlog task.")]
    public async Task<string> BacklogEditTaskAsync(
        [Description("Project ID")] string project_id,
        [Description("Task ID")] string task_id,
        [Description("New title (required)")] string title,
        [Description("New description (optional, omit to clear)")] string? description,
        CancellationToken ct)
    {
        try
        {
            var body = new { title, description };
            var result = await api.PatchAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/tasks/{task_id}", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_delete_task"), Description("Delete a backlog task. Fails with 409 if the task has already been claimed.")]
    public async Task<string> BacklogDeleteTaskAsync(
        [Description("Project ID")] string project_id,
        [Description("Task ID")] string task_id,
        CancellationToken ct)
    {
        try
        {
            await api.DeleteAsync($"/api/projects/{project_id}/backlog/tasks/{task_id}", ct);
            return "Task deleted successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_move_to_ready"), Description("Move a task from Backlog to Ready, optionally at a specific position.")]
    public async Task<string> BacklogMoveToReadyAsync(
        [Description("Project ID")] string project_id,
        [Description("Task ID")] string task_id,
        [Description("Zero-based target position in Ready column (null appends to end)")] int? target_index,
        CancellationToken ct)
    {
        try
        {
            var body = target_index.HasValue ? (object)new { target_index } : new { };
            var result = await api.PostAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/tasks/{task_id}/ready", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_move_to_backlog"), Description("Move a task from Ready back to Backlog, optionally at a specific position.")]
    public async Task<string> BacklogMoveToBacklogAsync(
        [Description("Project ID")] string project_id,
        [Description("Task ID")] string task_id,
        [Description("Zero-based target position in Backlog column (null appends to end)")] int? target_index,
        CancellationToken ct)
    {
        try
        {
            var body = target_index.HasValue ? (object)new { target_index } : new { };
            var result = await api.PostAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/tasks/{task_id}/backlog", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_reorder_task"), Description("Reorder a task within its current bucket (Backlog or Ready) to a new zero-based position.")]
    public async Task<string> BacklogReorderTaskAsync(
        [Description("Project ID")] string project_id,
        [Description("Task ID")] string task_id,
        [Description("Zero-based target position within the task's current bucket")] int target_index,
        CancellationToken ct)
    {
        try
        {
            var body = new { target_index };
            var result = await api.PostAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/tasks/{task_id}/reorder", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_get_board"), Description("Get the full Kanban board for a project: Backlog, Ready, and coordinator workflow columns with all cards.")]
    public async Task<string> BacklogGetBoardAsync(
        [Description("Project ID")] string project_id,
        [Description("Include terminal/done history (default false)")] bool? include_terminal_history,
        CancellationToken ct)
    {
        try
        {
            var flag = include_terminal_history ?? false;
            var result = await api.GetAsync<JsonElement>(
                $"/api/projects/{project_id}/board?include_terminal_history={flag}", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_get_workflow_stages"), Description("Get the ordered coordinator workflow-stage column definitions for a project.")]
    public async Task<string> BacklogGetWorkflowStagesAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>(
                $"/api/projects/{project_id}/workflow-stages", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_get_settings"), Description("Get the per-project backlog pickup settings (max_ready_per_heartbeat, pickup_autopilot, pickup_auto_approve_tools).")]
    public async Task<string> BacklogGetSettingsAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/settings", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "send_all_backlog_to_ready"), Description("Bulk-promote all Backlog tasks to Ready in one atomic operation. Appends them after any existing Ready tasks, preserving relative order. Idempotent — safe to call on an empty backlog.")]
    public async Task<string> SendAllBacklogToReadyAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<ReadyAllResponse>(
                $"/api/projects/{project_id}/backlog/ready-all", body: null, ct);
            return result.Moved > 0
                ? $"Promoted {result.Moved} backlog task(s) to Ready."
                : "No backlog tasks to promote.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "backlog_set_settings"), Description("Set the per-project backlog pickup settings. max_ready_per_heartbeat must be between 1 and 20.")]
    public async Task<string> BacklogSetSettingsAsync(
        [Description("Project ID")] string project_id,
        [Description("Maximum number of Ready tasks the heartbeat claims per tick (1-20)")] int max_ready_per_heartbeat,
        [Description("Auto-answer child clarifying questions during unattended coordinator runs")] bool pickup_autopilot,
        [Description("Auto-approve allow-with-approval tools during unattended runs (default false; never bypasses the destructive-action safety floor)")] bool pickup_auto_approve_tools,
        CancellationToken ct)
    {
        try
        {
            var body = new { max_ready_per_heartbeat, pickup_autopilot, pickup_auto_approve_tools };
            var result = await api.PutAsync<JsonElement>(
                $"/api/projects/{project_id}/backlog/settings", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
