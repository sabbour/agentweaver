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

/// <summary>Structured output for <c>run_submit</c>. Declares the output schema surfaced in tools/list.</summary>
public sealed record RunSubmitResult(
    [property: JsonPropertyName("run_id")]     string RunId,
    [property: JsonPropertyName("status")]     string Status,
    [property: JsonPropertyName("start_mode")] string StartMode);

/// <summary>
/// Structured output for <c>run_status</c>. Declares <c>status</c> in the output schema while
/// preserving the full run object at runtime via extension data (no fields are dropped).
/// </summary>
public sealed record RunStatusResult
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Additional { get; init; }
}

/// <summary>Structured output for <c>run_show_artifacts</c>: an object whose <c>artifacts</c> field is the file array.</summary>
public sealed record RunArtifactsResult(
    [property: JsonPropertyName("artifacts")] IReadOnlyList<JsonElement> Artifacts);

/// <summary>
/// Structured output for <c>run_task</c>. Covers every response variant (completed, gated,
/// failed, timed out); optional fields are omitted when null so each variant stays clean.
/// </summary>
public sealed record RunTaskResult
{
    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("artifacts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JsonElement>? Artifacts { get; init; }

    [JsonPropertyName("run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Run { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    [JsonPropertyName("review_prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewPrompt { get; init; }
}

[McpServerToolType]
public sealed class RunTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "run_submit", UseStructuredContent = true), Description("Legacy compatibility alias that starts a coordinator run directly in direct mode. Prefer run_task for the common one-call flow, or coordinator_start for full manual control.")]
    public async Task<RunSubmitResult> RunSubmitAsync(
        [Description("Project ID")] string project_id,
        [Description("Task description for the agent")] string task,
        [Description("Agent name (optional)")] string? agent_name = null,
        [Description("Branch to base the run on (optional)")] string? base_branch = null,
        [Description("Model id override (optional)")] string? model_source = null,
        CancellationToken ct = default)
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
            return new RunSubmitResult(runId, "submitted", "direct");
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_task", UseStructuredContent = true), Description("Run the common coordinator workflow in one call: start the run, poll status until it completes or hits a gate, and return the artifacts or next action.")]
    public async Task<RunTaskResult> RunTaskAsync(
        [Description("Project ID")] string project_id,
        [Description("Task or goal for the coordinator")] string task,
        [Description("Workflow id override (optional)")] string? workflow_id = null,
        [Description("Model id override (optional)")] string? model_id = null,
        [Description("Coordinator start mode: 'direct' (default) or 'defineOutcome'")] string? start_mode = null,
        [Description("Maximum seconds to wait before returning partial state (default: 600)")] int? timeout_seconds = null,
        [Description("Polling interval in seconds while waiting for completion (default: 2)")] int? poll_interval_seconds = null,
        CancellationToken ct = default)
    {
        try
        {
            var effectiveTimeout = Math.Clamp(timeout_seconds ?? 600, 1, 3600);
            var effectivePollInterval = Math.Clamp(poll_interval_seconds ?? 2, 1, 30);
            var effectiveStartMode = string.IsNullOrWhiteSpace(start_mode) ? "direct" : start_mode;

            var runId = await StartCoordinatorRunAsync(project_id, task, model_id, workflow_id, effectiveStartMode, ct);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(effectiveTimeout);
            JsonElement latestRun;

            while (true)
            {
                latestRun = await api.GetAsync<JsonElement>($"/api/runs/{Uri.EscapeDataString(runId)}", ct);

                if (TryBuildGateResponse(latestRun, runId, out var gatedResponse))
                    return gatedResponse!;

                var status = GetString(latestRun, "status");
                if (IsSuccessfulTerminalStatus(status))
                {
                    var artifacts = await api.GetAsync<IReadOnlyList<JsonElement>>($"/api/runs/{Uri.EscapeDataString(runId)}/files", ct);
                    return new RunTaskResult
                    {
                        RunId = runId,
                        Status = status!,
                        Artifacts = artifacts,
                        Run = latestRun
                    };
                }

                if (IsFailedTerminalStatus(status))
                {
                    return new RunTaskResult
                    {
                        RunId = runId,
                        Status = "failed",
                        Error = GetString(latestRun, "result") ?? $"Run ended in status '{status}'.",
                        Hint = GetFailureHint(status),
                        Run = latestRun
                    };
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return new RunTaskResult
                    {
                        RunId = runId,
                        Status = "timed_out",
                        // The required-capabilities contract expects artifacts as an array on every
                        // one-call-run response; emit an empty array rather than omitting it on timeout.
                        Artifacts = Array.Empty<JsonElement>(),
                        Hint = "Call run_status for a quick snapshot or run_watch if you want to follow the live stream.",
                        Run = latestRun
                    };
                }

                await Task.Delay(TimeSpan.FromSeconds(effectivePollInterval), ct);
            }
        }
        catch (McpApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_status", UseStructuredContent = true), Description("Get the current status of a run.")]
    public async Task<RunStatusResult> RunStatusAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct = default)
    {
        try
        {
            return await api.GetAsync<RunStatusResult>($"/api/runs/{Uri.EscapeDataString(run_id)}", ct);
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

    [McpServerTool(Name = "run_show_artifacts", UseStructuredContent = true), Description("List the files changed by a run.")]
    public async Task<RunArtifactsResult> RunShowArtifactsAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct = default)
    {
        try
        {
            var files = await api.GetAsync<IReadOnlyList<JsonElement>>($"/api/runs/{Uri.EscapeDataString(run_id)}/files", ct);
            return new RunArtifactsResult(files);
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

    private static bool TryBuildGateResponse(JsonElement run, string runId, out RunTaskResult? response)
    {
        var status = GetString(run, "status");
        var coordinatorStatus = GetString(run, "coordinator_status");

        if (string.Equals(status, "awaiting_review", StringComparison.OrdinalIgnoreCase))
        {
            response = new RunTaskResult
            {
                RunId = runId,
                Status = "awaiting_review",
                ReviewPrompt = "Run is awaiting human review. Call run_review, then rerun run_task or poll with run_status.",
                Run = run
            };
            return true;
        }

        if (string.Equals(coordinatorStatus, "awaiting_confirmation", StringComparison.OrdinalIgnoreCase))
        {
            response = new RunTaskResult
            {
                RunId = runId,
                Status = "awaiting_confirmation",
                ReviewPrompt = "Coordinator drafted an outcome spec. Call coordinator_outcome_spec_get to inspect it, then coordinator_outcome_spec_confirm or coordinator_outcome_spec_revise.",
                Run = run
            };
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

