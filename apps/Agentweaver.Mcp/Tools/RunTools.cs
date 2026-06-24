using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

internal sealed record RetryRunResponse(
    [property: JsonPropertyName("run_id")]      string RunId,
    [property: JsonPropertyName("retried_from")] string RetriedFrom,
    [property: JsonPropertyName("status")]      string Status);

[McpServerToolType]
public sealed class RunTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "run_submit"), Description("Submit a new agent run for a project.")]
    public async Task<string> RunSubmitAsync(
        [Description("Project ID")] string project_id,
        [Description("Task description for the agent")] string task,
        [Description("Agent name (optional)")] string? agent_name,
        [Description("Branch to base the run on (optional)")] string? base_branch,
        [Description("Model source override (optional)")] string? model_source,
        CancellationToken ct)
    {
        try
        {
            var body = new { task, agent_name, base_branch, model_source };
            var result = await api.PostAsync<JsonElement>($"/api/projects/{project_id}/runs", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "run_status"), Description("Get the current status of a run.")]
    public async Task<string> RunStatusAsync(
        [Description("Run ID")] string run_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/runs/{run_id}", ct);
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
            await foreach (var evt in api.StreamSseAsync($"/api/runs/{run_id}/stream", ct))
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

            var finalState = await api.GetAsync<JsonElement>($"/api/runs/{run_id}", ct);
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
            var result = await api.PostAsync<JsonElement>($"/api/runs/{run_id}/review", body, ct);
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
            var result = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/files", ct);
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
            var result = await api.GetAsync<JsonElement>($"/api/runs/{run_id}/files/{encodedPath}", ct);
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
                $"/api/runs/{run_id}/retry", body: null, ct);
            return $"Retried run {run_id} -> new run {result.RunId}.";
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
        var result = await api.PostAsync<JsonElement>($"/api/runs/{run_id}/archive", body: null, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
