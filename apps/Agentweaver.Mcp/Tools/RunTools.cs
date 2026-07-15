using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

internal sealed record RetryRunResponse(
    [property: JsonPropertyName("run_id")]      string RunId,
    [property: JsonPropertyName("retried_from")] string RetriedFrom,
    [property: JsonPropertyName("status")]      string Status);

internal sealed record StartCoordinatorRunResponse(
    [property: JsonPropertyName("runId")] string RunId);

[McpServerToolType]
public sealed class RunTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "run_submit"), Description("Legacy compatibility alias that starts a coordinator run directly in direct mode. Prefer run_task for the common one-call flow, or coordinator_start for full manual control.")]
    public async Task<string> RunSubmitAsync(
        [Description("Project ID")] string project_id,
        [Description("Task description for the agent")] string task,
        [Description("Agent name (optional)")] string? agent_name,
        [Description("Branch to base the run on (optional)")] string? base_branch,
        [Description("Model id override (optional)")] string? model_source,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(agent_name) || !string.IsNullOrWhiteSpace(base_branch))
            {
                throw new McpApiException(
                    400,
                    "agent_name and base_branch are not supported by coordinator runs.",
                    $"/api/projects/{Uri.EscapeDataString(project_id)}/runs",
                    hint: "Call coordinator_start for manual control, or remove the legacy fields and use run_task.");
            }

            var runId = await StartCoordinatorRunAsync(project_id, task, model_source, workflow_id: null, start_mode: "direct", ct);
            var result = new JsonObject
            {
                ["run_id"] = runId,
                ["status"] = "submitted",
                ["start_mode"] = "direct"
            };
            return result.ToJsonString(JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_task"), Description("Run the common coordinator workflow in one call: start the run, poll status until it completes or hits a gate, and return the artifacts or next action.")]
    public async Task<string> RunTaskAsync(
        [Description("Project ID")] string project_id,
        [Description("Task or goal for the coordinator")] string task,
        [Description("Workflow id override (optional)")] string? workflow_id,
        [Description("Model id override (optional)")] string? model_id,
        [Description("Coordinator start mode: 'direct' (default) or 'defineOutcome'")] string? start_mode,
        [Description("Maximum seconds to wait before returning partial state (default: 600)")] int? timeout_seconds,
        [Description("Polling interval in seconds while waiting for completion (default: 2)")] int? poll_interval_seconds,
        CancellationToken ct)
    {
        try
        {
            var effectiveTimeout = Math.Clamp(timeout_seconds ?? 600, 1, 3600);
            var effectivePollInterval = Math.Clamp(poll_interval_seconds ?? 2, 1, 30);
            var effectiveStartMode = string.IsNullOrWhiteSpace(start_mode) ? "direct" : start_mode;

            var runId = await StartCoordinatorRunAsync(project_id, task, model_id, workflow_id, effectiveStartMode, ct);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(effectiveTimeout);
            JsonElement latestRun = default;

            while (true)
            {
                latestRun = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(runId)}", ct);

                if (TryBuildGateResponse(latestRun, runId, out var gatedResponse))
                    return gatedResponse!;

                var status = GetString(latestRun, "status");
                if (IsSuccessfulTerminalStatus(status))
                {
                    var artifacts = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(runId)}/files", ct);
                    var response = new JsonObject
                    {
                        ["run_id"] = runId,
                        ["status"] = status,
                        ["artifacts"] = JsonNode.Parse(artifacts.GetRawText()),
                        ["run"] = JsonNode.Parse(latestRun.GetRawText())
                    };
                    return response.ToJsonString(JsonOpts);
                }

                if (IsFailedTerminalStatus(status))
                {
                    var response = new JsonObject
                    {
                        ["run_id"] = runId,
                        ["status"] = "failed",
                        ["error"] = GetString(latestRun, "result") ?? $"Run ended in status '{status}'.",
                        ["hint"] = GetFailureHint(status),
                        ["run"] = JsonNode.Parse(latestRun.GetRawText())
                    };
                    return response.ToJsonString(JsonOpts);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    var response = new JsonObject
                    {
                        ["run_id"] = runId,
                        ["status"] = "timed_out",
                        ["hint"] = "Call run_status for a quick snapshot or run_watch if you want to follow the live stream.",
                        ["run"] = JsonNode.Parse(latestRun.GetRawText())
                    };
                    return response.ToJsonString(JsonOpts);
                }

                await Task.Delay(TimeSpan.FromSeconds(effectivePollInterval), ct);
            }
        }
        catch (McpApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_status"), Description("Get the current status of a run.")]
    public async Task<string> RunStatusAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_watch"), Description("Watch a run live, streaming progress until completion.")]
    public async Task<string> RunWatchAsync(
        [Description("Run ID")] string run_id,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        try
        {
            await foreach (var evt in api.StreamSseAsync($"/api/runs/{Uri.EscapeDataString(run_id)}/stream", ct))
            {
                var eventType = evt.EventType;
                if (eventType == "done") break;
                try
                {
                    var doc = JsonDocument.Parse(evt.Data);
                    var payload = doc.RootElement.TryGetProperty("payload", out var p) ? p : doc.RootElement;

                    string? notification = eventType switch
                    {
                        "agent.message" or "agent.message.delta" =>
                            payload.TryGetProperty("content", out var c) ? c.GetString() ?? "" : evt.Data,
                        "tool.call" =>
                            payload.TryGetProperty("name", out var n) ? $"Tool call: {n.GetString()}" : "Tool call",
                        "tool.result" => "Tool result received",
                        "run.status" =>
                            payload.TryGetProperty("status", out var s) ? $"Run status: {s.GetString()}" : null,
                        "run.completed" => "Run completed",
                        "review.requested" => "Run awaiting review",
                        _ => null
                    };

                    if (notification is not null)
                    {
                        progress.Report(new ProgressNotificationValue { Message = notification, Progress = 0 });
                    }
                }
                catch { /* skip unparseable events */ }
            }

            var finalState = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}", ct);
            return JsonSerializer.Serialize(finalState, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_review"), Description("Approve or reject a run that is awaiting review.")]
    public async Task<string> RunReviewAsync(
        [Description("Run ID")] string run_id,
        [Description("Whether to approve (true) or reject (false) the run")] bool approved,
        CancellationToken ct)
    {
        try
        {
            var body = new { approved };
            var result = await api.PostAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}/review", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "start_preview"), Description("Register a live browser preview for a web server the agent has ALREADY started and verified inside a run's sandbox pod. Call this AFTER your server is running and responding (e.g. you confirmed `curl http://localhost:PORT/` succeeds) — pass the exact port it listens on (e.g. 3000). You MUST call this whenever you start any server so the user gets a live preview link. Routes through a human-in-the-loop approval gate; returns the public HTTPS preview_url once approved. Do not finish the task without registering the preview for any server you started.")]
    public async Task<string> StartPreviewAsync(
        [Description("Run ID whose sandbox pod hosts the server to expose")] string run_id,
        [Description("Port the server is listening on inside the sandbox pod, e.g. 3000")] int port,
        CancellationToken ct)
    {
        try
        {
            var body = new { target_port = port };
            var result = await api.PostAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}/sandbox/preview", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_show_artifacts"), Description("List the files changed by a run.")]
    public async Task<string> RunShowArtifactsAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}/files", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_get_file"), Description("Get the content or diff of a specific file changed by a run.")]
    public async Task<string> RunGetFileAsync(
        [Description("Run ID")] string run_id,
        [Description("File path within the run workspace")] string path,
        CancellationToken ct)
    {
        try
        {
            var encodedPath = string.Join("/", path.TrimStart('/').Split('/', '\\').Select(Uri.EscapeDataString));
            var result = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}/files/{encodedPath}", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_retry"), Description("Retry a failed run by creating a fresh run from its original inputs.")]
    public async Task<string> RunRetryAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<RetryRunResponse>(
                $"/api/runs/{Uri.EscapeDataString(run_id)}/retry", body: null, ct);
            return $"Retried run {Uri.EscapeDataString(run_id)} -> new run {result.RunId}.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_archive"), Description("Archive a run off active project board/list projections.")]
    public async Task<string> RunArchiveAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
        var result = await api.PostAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(run_id)}/archive", body: null, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    private async Task<string> StartCoordinatorRunAsync(
        string project_id,
        string task,
        string? model_id,
        string? workflow_id,
        string start_mode,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["goal"] = task,
            ["start_mode"] = start_mode
        };

        if (!string.IsNullOrWhiteSpace(model_id))
            body["modelId"] = model_id;
        if (!string.IsNullOrWhiteSpace(workflow_id))
            body["workflow_override_id"] = workflow_id;

        var result = await api.PostAsync<StartCoordinatorRunResponse>(
            $"/api/projects/{Uri.EscapeDataString(project_id)}/orchestrations",
            body,
            ct);

        return result.RunId;
    }

    private static bool TryBuildGateResponse(JsonElement run, string runId, out string? response)
    {
        var status = GetString(run, "status");
        var coordinatorStatus = GetString(run, "coordinator_status");

        if (string.Equals(status, "awaiting_review", StringComparison.OrdinalIgnoreCase))
        {
            var payload = new JsonObject
            {
                ["run_id"] = runId,
                ["status"] = "awaiting_review",
                ["review_prompt"] = "Run is awaiting human review. Call run_review, then rerun run_task or poll with run_status.",
                ["run"] = JsonNode.Parse(run.GetRawText())
            };
            response = payload.ToJsonString(JsonOpts);
            return true;
        }

        if (string.Equals(coordinatorStatus, "awaiting_confirmation", StringComparison.OrdinalIgnoreCase))
        {
            var payload = new JsonObject
            {
                ["run_id"] = runId,
                ["status"] = "awaiting_confirmation",
                ["review_prompt"] = "Coordinator drafted an outcome spec. Call coordinator_outcome_spec_get to inspect it, then coordinator_outcome_spec_confirm or coordinator_outcome_spec_revise.",
                ["run"] = JsonNode.Parse(run.GetRawText())
            };
            response = payload.ToJsonString(JsonOpts);
            return true;
        }

        response = null;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsSuccessfulTerminalStatus(string? status) =>
        status is "completed" or "merged" or "assemble_ready";

    private static bool IsFailedTerminalStatus(string? status) =>
        status is "failed" or "declined" or "merge_failed";

    private static string GetFailureHint(string? status) =>
        status switch
        {
            "failed" => "Call run_status for the failure detail, then use run_retry if you want a fresh attempt.",
            "declined" => "A reviewer declined this run. Inspect run_status or run_show_artifacts, revise the task, then retry.",
            "merge_failed" => "Inspect run_status for merge_conflicts, resolve the blocking issue, then retry or resubmit.",
            _ => "Call run_status for details before retrying."
        };
}
