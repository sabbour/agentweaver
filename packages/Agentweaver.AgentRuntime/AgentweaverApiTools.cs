using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Factory that produces <see cref="AIFunction"/> definitions for Agentweaver API operations.
/// These are injected into the agent's <c>SessionConfig.Tools</c> list so the model can invoke
/// them as structured tool calls rather than via bash/curl instructions in the task prompt.
/// </summary>
internal static class AgentweaverApiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static readonly ISet<string> ToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "submit_decision",
        "record_memory",
        "update_session",
        "submit_inbox_entry",
        "list_inbox",
        "list_decisions",
        "get_memory",
        "merge_inbox_entry",
        "export_memory",
        "project_get",
        "project_list_runs",
        "backlog_capture_task",
        "backlog_get_board",
        "run_status",
        "run_show_artifacts",
        "coordinator_work_plan_get",
        "coordinator_children_get",
        "orchestration_topology",
    };

    /// <summary>
    /// Builds the Agentweaver API <see cref="AIFunction"/> instances for the given project/agent
    /// context. Each function captures <paramref name="projectId"/>, <paramref name="agentName"/>,
    /// and the HTTP client in a closure so callers require no runtime arguments beyond the
    /// parameters declared in the tool schema.
    /// </summary>
    /// <param name="projectId">The project ID used in API path construction.</param>
    /// <param name="agentName">The agent name used as the submitter identity in API requests.</param>
    /// <param name="apiBaseUrl">The Agentweaver API base URL (e.g. <c>http://localhost:5000</c>).</param>
    /// <param name="apiKey">Bearer token for API authentication; may be null for unauthenticated local dev.</param>
    /// <param name="httpClientOverride">Optional pre-configured HttpClient (for testing). If null a new client is created from <paramref name="apiBaseUrl"/>/<paramref name="apiKey"/>.</param>
    internal static IEnumerable<AIFunction> Build(
        string projectId, string agentName, string apiBaseUrl, string? apiKey,
        HttpClient? httpClientOverride = null)
    {
        var http = httpClientOverride ?? CreateHttpClient(apiBaseUrl, apiKey);

        yield return AIFunctionFactory.Create(
            async (
                [Description("Unique kebab-case slug for idempotency. Namespace by topic and agent to avoid collisions with peer agents, e.g. 'prefer-async-over-sync--tank' or 'api-shape--nova'. If a collision is detected the server will de-collide automatically and return the effective slug in the response.")] string slug,
                [Description("Decision type: architectural | scope | process | pattern | technical")] string type,
                [Description("Short, descriptive title for the decision")] string title,
                [Description("Full decision content — what was decided and why")] string content,
                [Description("Optional rationale explaining the trade-offs considered")] string? rationale = null,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/decisions/inbox",
                    new { agent_name = agentName, slug, type, title, content, rationale },
                    ct).ConfigureAwait(false);
                return await HandleWriteResponseAsync(
                    "submit_decision", response,
                    successMessage: $"Decision submitted to inbox: {title}",
                    conflictMessage: $"Decision '{slug}' already recorded (already merged or rejected — no changes).",
                    ct).ConfigureAwait(false);
            },
            "submit_decision",
            "Submit an architectural, scope, or process decision to the project decision inbox for team review. " +
            "Use this whenever you make a significant design choice that the team should know about.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Memory type: learning | pattern | core_context | update")] string type,
                [Description("Importance level: low | medium | high")] string importance,
                [Description("The memory content to record")] string content,
                [Description("Comma-separated tags for future retrieval (optional)")] string? tags = null,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/agents/{agentName}/memory",
                    new { type, importance, content, tags },
                    ct).ConfigureAwait(false);
                return await HandleWriteResponseAsync(
                    "record_memory", response, "Memory recorded.", null, ct).ConfigureAwait(false);
            },
            "record_memory",
            "Record a learning, pattern, or insight into agent memory for future reference. " +
            "Use this to persist important discoveries, gotchas, or patterns encountered during the task.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Progress note or summary text to append to the current session log")] string summary,
                CancellationToken ct = default) =>
            {
                var response = await http.PutAsJsonAsync(
                    $"api/projects/{projectId}/sessions/current",
                    new { summary },
                    ct).ConfigureAwait(false);
                return await HandleWriteResponseAsync(
                    "update_session", response, "Session updated.", null, ct).ConfigureAwait(false);
            },
            "update_session",
            "Append a progress note or completion summary to the current project session log. " +
            "Use this to record what was accomplished or to flag work-in-progress state.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Unique kebab-case slug for idempotency, e.g. 'boundary-conflict-auth-scope'")] string slug,
                [Description("Entry type: learning | pattern | update | architectural | scope | process | technical")] string type,
                [Description("Short, descriptive title")] string title,
                [Description("Full entry content")] string content,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/decisions/inbox",
                    new { agent_name = agentName, slug, type, title, content },
                    ct).ConfigureAwait(false);
                return await HandleWriteResponseAsync(
                    "submit_inbox_entry", response,
                    successMessage: $"Inbox entry submitted: {title}",
                    conflictMessage: $"Inbox entry '{slug}' already recorded (already merged or rejected — no changes).",
                    ct).ConfigureAwait(false);
            },
            "submit_inbox_entry",
            "Submit a general inbox entry such as a learning, update, or boundary conflict flag. " +
            "For boundary conflicts use type 'process' and title 'Boundary conflict: [short description]'.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Filter by agent name (optional — omit to list all agents)")] string? forAgent = null,
                CancellationToken ct = default) =>
            {
                var query = forAgent is not null ? $"?agent={Uri.EscapeDataString(forAgent)}&status=pending" : "?status=pending";
                var response = await http.GetAsync(
                    $"api/projects/{projectId}/decisions/inbox{query}",
                    ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                return $"list_inbox failed: HTTP {(int)response.StatusCode} — {body}";
            },
            "list_inbox",
            "List pending decision inbox entries for this project. Returns JSON. " +
            "Use before merge_inbox_entry to get the numeric entry IDs.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("The numeric id of the inbox entry to merge (from list_inbox)")] int entryId,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/decisions/inbox/{entryId}/merge",
                    new { },
                    ct).ConfigureAwait(false);
                return await HandleWriteResponseAsync(
                    "merge_inbox_entry", response,
                    successMessage: $"Inbox entry {entryId} merged into decisions.",
                    conflictMessage: $"Inbox entry {entryId} is not pending or does not exist.",
                    ct).ConfigureAwait(false);
            },
            "merge_inbox_entry",
            "Merge a pending inbox entry into the project decision log. " +
            "A per-run worker Scribe merges only type: learning, pattern, or update. " +
            "The Coordinator also promotes architectural and scope entries it endorses during finalization.");

        yield return AIFunctionFactory.Create(
            async (CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/memory/export",
                    new { },
                    ct).ConfigureAwait(false);
                return await HandleWriteResponseAsync(
                    "export_memory", response,
                    "Memory exported to .squad/ and .agentweaver/context/.", null, ct).ConfigureAwait(false);
            },
            "export_memory",
            "Export all project decisions, inbox entries, memories, and session context to .squad/ " +
            "and .agentweaver/context/. Call this as the final step of the Scribe pass.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Filter by decision type: architectural | scope | process | pattern | technical (optional — omit for all types)")] string? type = null,
                [Description("Filter by status: active | superseded | archived (optional — defaults to active)")] string? status = null,
                CancellationToken ct = default) =>
            {
                var qs = new List<string>();
                qs.Add($"status={Uri.EscapeDataString(!string.IsNullOrWhiteSpace(status) ? status : "active")}");
                if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
                return await GetJsonAsync(http,
                    $"api/projects/{projectId}/decisions?{string.Join("&", qs)}", ct).ConfigureAwait(false);
            },
            "list_decisions",
            "Read the merged decision ledger for this project. Returns JSON with title, type, status, decided-by agent, and content. " +
            "Before committing to a notable implementation choice that other agents depend on, call list_decisions " +
            "(and get_memory + list_inbox) to check what the team has already decided — avoid re-litigating closed decisions.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Restrict to a specific agent's memory (optional — omit for all agents)")] string? agent = null,
                [Description("Comma-separated tag filter (optional — omit for all tags)")] string? tags = null,
                [Description("Filter by memory type: learning | pattern | core_context | update (optional)")] string? type = null,
                CancellationToken ct = default) =>
            {
                string path;
                if (!string.IsNullOrWhiteSpace(agent))
                {
                    var qs = new List<string>();
                    if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
                    path = $"api/projects/{projectId}/agents/{Uri.EscapeDataString(agent)}/memory"
                        + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
                }
                else
                {
                    var qs = new List<string>();
                    if (!string.IsNullOrWhiteSpace(tags)) qs.Add($"tags={Uri.EscapeDataString(tags)}");
                    if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
                    path = $"api/projects/{projectId}/memory"
                        + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
                }
                return await GetJsonAsync(http, path, ct).ConfigureAwait(false);
            },
            "get_memory",
            "Read agent memory for this project. Omit 'agent' to search across all agents (supports tag filter); " +
            "supply 'agent' to fetch one agent's memory specifically. " +
            "Before making a notable implementation choice, call get_memory " +
            "(and list_decisions + list_inbox) to surface patterns, learnings, and gotchas peers have already recorded.");

        if (!string.Equals(agentName, "Coordinator", StringComparison.OrdinalIgnoreCase))
            yield break;

        yield return AIFunctionFactory.Create(
            async (
                [Description("Project ID. Must be the current coordinator project.")] string project_id,
                CancellationToken ct = default) =>
            {
                EnsureCurrentProject(projectId, project_id);
                return await GetJsonAsync(http, $"api/projects/{projectId}", ct).ConfigureAwait(false);
            },
            "project_get",
            "MCP-equivalent Agentweaver project_get for the current project. Returns project metadata as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Project ID. Must be the current coordinator project.")] string project_id,
                CancellationToken ct = default) =>
            {
                EnsureCurrentProject(projectId, project_id);
                return await GetJsonAsync(http, $"api/projects/{projectId}/runs", ct).ConfigureAwait(false);
            },
            "project_list_runs",
            "MCP-equivalent Agentweaver project_list_runs for the current project. Returns project runs as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Project ID. Must be the current coordinator project.")] string project_id,
                [Description("Task title to capture into this project's backlog")] string title,
                [Description("Optional task description")] string? description = null,
                CancellationToken ct = default) =>
            {
                EnsureCurrentProject(projectId, project_id);
                var response = await http.PostAsJsonAsync(
                    $"api/projects/{projectId}/backlog/tasks",
                    new { title, description },
                    ct).ConfigureAwait(false);
                var body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
                return response.IsSuccessStatusCode
                    ? body
                    : $"backlog_capture_task failed: HTTP {(int)response.StatusCode} — {body}";
            },
            "backlog_capture_task",
            "MCP-equivalent Agentweaver backlog_capture_task scoped to the current project. " +
            "Use only to capture follow-up project meta work; it cannot target other projects.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Project ID. Must be the current coordinator project.")] string project_id,
                [Description("Include terminal/done history. Defaults to false.")] bool? include_terminal_history = null,
                CancellationToken ct = default) =>
            {
                EnsureCurrentProject(projectId, project_id);
                var include = include_terminal_history ?? false;
                return await GetJsonAsync(
                    http,
                    $"api/projects/{projectId}/board?include_terminal_history={include}",
                    ct).ConfigureAwait(false);
            },
            "backlog_get_board",
            "MCP-equivalent Agentweaver backlog_get_board scoped to the current project. Returns the board as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Run ID to inspect")] string run_id,
                CancellationToken ct = default) =>
            {
                var scopeError = await ValidateRunInCurrentProjectAsync(http, projectId, run_id, ct).ConfigureAwait(false);
                if (scopeError is not null) return scopeError;
                return await GetJsonAsync(http, $"api/runs/{run_id}", ct).ConfigureAwait(false);
            },
            "run_status",
            "MCP-equivalent Agentweaver run_status. Returns a run's current state as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Run ID whose changed files should be listed")] string run_id,
                CancellationToken ct = default) =>
            {
                var scopeError = await ValidateRunInCurrentProjectAsync(http, projectId, run_id, ct).ConfigureAwait(false);
                if (scopeError is not null) return scopeError;
                return await GetJsonAsync(http, $"api/runs/{run_id}/files", ct).ConfigureAwait(false);
            },
            "run_show_artifacts",
            "MCP-equivalent Agentweaver run_show_artifacts. Lists files changed by a run as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Coordinator run ID")] string run_id,
                CancellationToken ct = default) =>
            {
                var scopeError = await ValidateRunInCurrentProjectAsync(http, projectId, run_id, ct).ConfigureAwait(false);
                if (scopeError is not null) return scopeError;
                return await GetJsonAsync(http, $"api/runs/{run_id}/work-plan", ct).ConfigureAwait(false);
            },
            "coordinator_work_plan_get",
            "MCP-equivalent Agentweaver coordinator_work_plan_get. Returns the coordinator work plan as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Coordinator run ID")] string run_id,
                CancellationToken ct = default) =>
            {
                var scopeError = await ValidateRunInCurrentProjectAsync(http, projectId, run_id, ct).ConfigureAwait(false);
                if (scopeError is not null) return scopeError;
                return await GetJsonAsync(http, $"api/runs/{run_id}/children", ct).ConfigureAwait(false);
            },
            "coordinator_children_get",
            "MCP-equivalent Agentweaver coordinator_children_get. Lists coordinator child runs as JSON.");

        yield return AIFunctionFactory.Create(
            async (
                [Description("Coordinator run ID")] string run_id,
                CancellationToken ct = default) =>
            {
                var scopeError = await ValidateRunInCurrentProjectAsync(http, projectId, run_id, ct).ConfigureAwait(false);
                if (scopeError is not null) return scopeError;
                var workPlan = await GetJsonElementAsync(http, $"api/runs/{run_id}/work-plan", ct).ConfigureAwait(false);
                var children = await GetJsonElementAsync(http, $"api/runs/{run_id}/children", ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    coordinatorRunId = run_id,
                    workPlan,
                    children,
                }, JsonOptions);
            },
            "orchestration_topology",
            "MCP-equivalent Agentweaver orchestration_topology. Returns a combined work-plan and children snapshot.");
    }

    /// <summary>
    /// Handles a write response: returns the success message on 2xx, the conflict message on 409 (if provided),
    /// or a descriptive non-throwing error string for any other non-2xx status.
    /// Never throws for server-side errors — the agent sees the error as a tool result and can continue/retry.
    /// </summary>
    private static async Task<string> HandleWriteResponseAsync(
        string toolName,
        HttpResponseMessage response,
        string successMessage,
        string? conflictMessage,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return successMessage;

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict && conflictMessage is not null)
            return conflictMessage;

        var body = string.Empty;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
        return $"{toolName} failed: HTTP {(int)response.StatusCode} — {body}";
    }

    private static HttpClient CreateHttpClient(string apiBaseUrl, string? apiKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + '/') };
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    private static void EnsureCurrentProject(string currentProjectId, string requestedProjectId)
    {
        if (!string.Equals(currentProjectId, requestedProjectId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Coordinator Agentweaver meta tools are scoped to the current project and cannot target another project.");
    }

    private const string RunProjectMismatchError = "Run does not belong to current project.";

    private static async Task<string?> ValidateRunInCurrentProjectAsync(
        HttpClient http,
        string currentProjectId,
        string runId,
        CancellationToken ct)
    {
        _ = await GetJsonElementAsync(http, $"api/runs/{Uri.EscapeDataString(runId)}", ct).ConfigureAwait(false);

        var projectRuns = await GetJsonElementAsync(
            http,
            $"api/projects/{Uri.EscapeDataString(currentProjectId)}/runs?include_children=true",
            ct).ConfigureAwait(false);

        if (projectRuns.ValueKind != JsonValueKind.Array)
            return RunProjectMismatchError;

        foreach (var run in projectRuns.EnumerateArray())
        {
            if (JsonStringEquals(run, "execution_id", runId) ||
                JsonStringEquals(run, "run_id", runId) ||
                JsonStringEquals(run, "workflow_run_id", runId))
                return null;
        }

        return RunProjectMismatchError;
    }

    private static bool JsonStringEquals(JsonElement element, string propertyName, string expected) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static async Task<string> GetJsonAsync(HttpClient http, string path, CancellationToken ct)
    {
        var response = await http.GetAsync(path, ct).ConfigureAwait(false);
        var content = string.Empty;
        try { content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
        return response.IsSuccessStatusCode
            ? content
            : $"GET {path} failed: HTTP {(int)response.StatusCode} — {content}";
    }

    private static async Task<JsonElement> GetJsonElementAsync(HttpClient http, string path, CancellationToken ct)
    {
        var json = await GetJsonAsync(http, path, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // GetJsonAsync returned an error string (non-2xx path) — return a JSON string element
            // so callers can propagate the message without crashing.
            return JsonDocument.Parse(JsonSerializer.Serialize(json)).RootElement.Clone();
        }
    }
}
