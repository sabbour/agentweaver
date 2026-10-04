using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class MemoryTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SerializeResult<T>(T result) => JsonSerializer.Serialize(result, JsonOpts);

    private static async Task<string> ExecuteJsonAsync<T>(
        string _,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        try
        {
            var result = await action(ct);
            return SerializeResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpApiException(0, ex.Message);
        }
    }

    private static async Task<string> ExecuteMessageAsync(
        string _,
        Func<CancellationToken, Task> action,
        string successMessage,
        CancellationToken ct)
    {
        try
        {
            await action(ct);
            return successMessage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpApiException(0, ex.Message);
        }
    }

    private static string BuildQs(params (string key, string? value)[] pairs)
    {
        var parts = pairs
            .Where(p => p.value is not null)
            .Select(p => $"{p.key}={Uri.EscapeDataString(p.value!)}");
        var qs = string.Join("&", parts);
        return qs.Length > 0 ? "?" + qs : string.Empty;
    }

    // ── Decision Inbox ──────────────────────────────────────────────────────

    [McpServerTool(Name = "decision_inbox_submit"), Description("Submit a decision or learning to the agent inbox.")]
    public async Task<string> InboxSubmitAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name submitting the entry")] string agent_name,
        [Description("Unique slug for idempotency (e.g. 'prefer-async-over-sync')")] string slug,
        [Description("Type: learning | pattern | update | architectural | scope | process | technical")] string type,
        [Description("Full content")] string content,
        [Description("Short title (defaults to slug)")] string? title = null,
        [Description("Optional rationale")] string? rationale = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "decision_inbox_submit",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/inbox",
                new { agent_name, slug, type, title, content, rationale }, token),
            ct);
    }

    [McpServerTool(Name = "decision_inbox_list"), Description("List inbox entries for a project.")]
    public async Task<string> InboxListAsync(
        [Description("Project ID")] string project_id,
        [Description("Filter by agent name")] string? agent = null,
        [Description("Filter by type")] string? type = null,
        [Description("Filter by status: pending | merged | rejected (default: pending)")] string? status = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "decision_inbox_list",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/inbox{BuildQs(("agent", agent), ("type", type), ("status", status))}", token),
            ct);
    }

    [McpServerTool(Name = "decision_inbox_merge"), Description("Merge a pending inbox entry into team decisions.")]
    public async Task<string> InboxMergeAsync(
        [Description("Project ID")] string project_id,
        [Description("Inbox entry ID")] string entry_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "decision_inbox_merge",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/inbox/{Uri.EscapeDataString(entry_id)}/merge",
                null,
                token),
            ct);
    }

    [McpServerTool(Name = "decision_inbox_reject"), Description("Reject a pending inbox entry.")]
    public async Task<string> InboxRejectAsync(
        [Description("Project ID")] string project_id,
        [Description("Inbox entry ID")] string entry_id,
        CancellationToken ct = default)
    {
        return await ExecuteMessageAsync(
            "decision_inbox_reject",
            token => api.PostAsync(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/inbox/{Uri.EscapeDataString(entry_id)}/reject",
                null,
                token),
            "rejected",
            ct);
    }

    // ── Decisions ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "decision_create"), Description("Create a team decision directly (coordinator path).")]
    public async Task<string> DecisionCreateAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Type: architectural | process | scope | technical")] string type,
        [Description("Short title")] string title,
        [Description("Full content")] string content,
        [Description("Optional rationale")] string? rationale = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "decision_create",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions",
                new { agent_name, type, title, content, rationale }, token),
            ct);
    }

    [McpServerTool(Name = "squad_decide"), Description("Submit a team decision to the decision inbox from a squad agent.")]
    public async Task<string> SquadDecideAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Unique slug for idempotency (e.g. 'prefer-async-over-sync')")] string slug,
        [Description("Type: learning | pattern | update | architectural | scope | process | technical")] string type,
        [Description("Full content")] string content,
        [Description("Short title (defaults to slug)")] string? title = null,
        [Description("Optional rationale")] string? rationale = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "squad_decide",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/inbox",
                new { agent_name, slug, type, title, content, rationale }, token),
            ct);
    }

    [McpServerTool(Name = "decision_list"), Description("List team decisions for a project.")]
    public async Task<string> DecisionListAsync(
        [Description("Project ID")] string project_id,
        [Description("Filter by type")] string? type = null,
        [Description("Filter by agent")] string? agent = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "decision_list",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions{BuildQs(("type", type), ("agent", agent))}", token),
            ct);
    }

    [McpServerTool(Name = "decision_update"), Description("Update a decision's status, content, or rationale.")]
    public async Task<string> DecisionUpdateAsync(
        [Description("Project ID")] string project_id,
        [Description("Decision ID")] string decision_id,
        [Description("Current revision number; stale values are rejected")] int expected_revision,
        [Description("New status: active | superseded | archived")] string? status = null,
        [Description("New content")] string? content = null,
        [Description("New rationale")] string? rationale = null,
        [Description("Decision ID that supersedes this one")] int? superseded_by_id = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "decision_update",
            token => api.PutAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/{Uri.EscapeDataString(decision_id)}",
                new { expected_revision, status, content, rationale, superseded_by_id }, token),
            ct);
    }

    [McpServerTool(Name = "decision_history"), Description("List immutable revisions for a decision.")]
    public async Task<string> DecisionHistoryAsync(
        [Description("Project ID")] string project_id,
        [Description("Decision ID")] string decision_id,
        [Description("1-based page")] int page = 1,
        [Description("Page size, maximum 100")] int page_size = 25,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "decision_history",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/{Uri.EscapeDataString(decision_id)}/revisions{BuildQs(("page", page.ToString()), ("page_size", page_size.ToString()))}",
                token),
            ct);

    [McpServerTool(Name = "decision_compare"), Description("Retrieve two immutable decision revisions for comparison.")]
    public async Task<string> DecisionCompareAsync(
        [Description("Project ID")] string project_id,
        [Description("Decision ID")] string decision_id,
        [Description("Older revision number")] int from_revision,
        [Description("Newer revision number")] int to_revision,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "decision_compare",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/{Uri.EscapeDataString(decision_id)}/compare{BuildQs(("from_revision", from_revision.ToString()), ("to_revision", to_revision.ToString()))}",
                token),
            ct);

    [McpServerTool(Name = "decision_restore"), Description("Restore a prior decision snapshot as a new pending revision.")]
    public async Task<string> DecisionRestoreAsync(
        [Description("Project ID")] string project_id,
        [Description("Decision ID")] string decision_id,
        [Description("Current revision number")] int expected_revision,
        [Description("Historical revision number to restore")] int revision,
        [Description("Reason for restoring")] string? reason = null,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "decision_restore",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/decisions/{Uri.EscapeDataString(decision_id)}/restore",
                new { expected_revision, revision, reason },
                token),
            ct);

    // ── Agent Memory ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "memory_record"), Description("Add a memory entry for an agent.")]
    public async Task<string> MemoryAddAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Type: learning | pattern | core_context | update")] string type,
        [Description("Content")] string content,
        [Description("Importance: low | medium | high")] string importance = "medium",
        [Description("Comma-separated tags")] string? tags = null,
        [Description("Related session ID")] string? session_id = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "memory_record",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory",
                new { session_id, type, content, importance, tags }, token),
            ct);
    }

    [McpServerTool(Name = "memory_list"), Description("List memory entries for a specific agent.")]
    public async Task<string> MemoryListAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Filter by type")] string? type = null,
        [Description("Filter by importance")] string? importance = null,
        [Description("Lifecycle state: active | superseded | archived | all")] string? status = null,
        [Description("1-based page")] int page = 1,
        [Description("Page size, maximum 100")] int page_size = 25,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "memory_list",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory{BuildQs(("type", type), ("importance", importance), ("status", status), ("page", page.ToString()), ("page_size", page_size.ToString()))}", token),
            ct);
    }

    [McpServerTool(Name = "memory_get"), Description("Get a single memory entry.")]
    public async Task<string> MemoryGetAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Memory entry ID")] string memory_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "memory_get",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory/{Uri.EscapeDataString(memory_id)}", token),
            ct);
    }

    [McpServerTool(Name = "memory_search"), Description("Cross-agent memory search across the whole project.")]
    public async Task<string> MemorySearchAsync(
        [Description("Project ID")] string project_id,
        [Description("Text to find in memory content, tags, or agent name")] string? query = null,
        [Description("Filter by type")] string? type = null,
        [Description("Comma-separated tags to filter by (OR semantics)")] string? tags = null,
        [Description("Lifecycle state: active | superseded | archived | all")] string? status = null,
        [Description("1-based page")] int page = 1,
        [Description("Page size, maximum 100")] int page_size = 25,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "memory_search",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/memory{BuildQs(("q", query), ("type", type), ("tags", tags), ("status", status), ("page", page.ToString()), ("page_size", page_size.ToString()))}", token),
            ct);
    }

    [McpServerTool(Name = "memory_update"), Description("Update memory with optimistic concurrency; approved content becomes pending.")]
    public async Task<string> MemoryUpdateAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Memory entry ID")] string memory_id,
        [Description("Current revision number; stale values are rejected")] int expected_revision,
        [Description("New type")] string? type = null,
        [Description("New content")] string? content = null,
        [Description("New importance")] string? importance = null,
        [Description("New comma-separated tags")] string? tags = null,
        [Description("Lifecycle state: active | superseded | archived")] string? status = null,
        [Description("Replacement memory ID when superseding")] int? replaced_by_id = null,
        [Description("Reason for the change")] string? reason = null,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "memory_update",
            token => api.PutAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory/{Uri.EscapeDataString(memory_id)}",
                new { expected_revision, type, content, importance, tags, status, replaced_by_id, reason },
                token),
            ct);

    [McpServerTool(Name = "memory_history"), Description("List immutable revisions for a memory entry.")]
    public async Task<string> MemoryHistoryAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Memory entry ID")] string memory_id,
        [Description("1-based page")] int page = 1,
        [Description("Page size, maximum 100")] int page_size = 25,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "memory_history",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory/{Uri.EscapeDataString(memory_id)}/revisions{BuildQs(("page", page.ToString()), ("page_size", page_size.ToString()))}",
                token),
            ct);

    [McpServerTool(Name = "memory_compare"), Description("Retrieve two immutable memory revisions for comparison.")]
    public async Task<string> MemoryCompareAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Memory entry ID")] string memory_id,
        [Description("Older revision number")] int from_revision,
        [Description("Newer revision number")] int to_revision,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "memory_compare",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory/{Uri.EscapeDataString(memory_id)}/compare{BuildQs(("from_revision", from_revision.ToString()), ("to_revision", to_revision.ToString()))}",
                token),
            ct);

    [McpServerTool(Name = "memory_restore"), Description("Restore a prior memory snapshot as a new pending revision.")]
    public async Task<string> MemoryRestoreAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Memory entry ID")] string memory_id,
        [Description("Current revision number")] int expected_revision,
        [Description("Historical revision number to restore")] int revision,
        [Description("Reason for restoring")] string? reason = null,
        CancellationToken ct = default) =>
        await ExecuteJsonAsync(
            "memory_restore",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/agents/{Uri.EscapeDataString(agent_name)}/memory/{Uri.EscapeDataString(memory_id)}/restore",
                new { expected_revision, revision, reason },
                token),
            ct);

    // ── Sessions ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "session_start"), Description("Start a new work session for a project.")]
    public async Task<string> SessionStartAsync(
        [Description("Project ID")] string project_id,
        [Description("Unique session ID")] string session_id,
        [Description("Current focus area")] string focus_area,
        [Description("Active issues (optional)")] string? active_issues = null,
        [Description("Initial session summary")] string? summary = null,
        [Description("Serialized session state")] string? serialized_state = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "session_start",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/sessions",
                new { session_id, focus_area, active_issues, summary, serialized_state }, token),
            ct);
    }

    [McpServerTool(Name = "session_current"), Description("Get the current open session for a project.")]
    public async Task<string> SessionCurrentAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "session_current",
            token => api.GetAsync<object>($"api/projects/{Uri.EscapeDataString(project_id)}/sessions/current", token),
            ct);
    }

    [McpServerTool(Name = "session_update"), Description("Update the current session's focus, summary, or end it.")]
    public async Task<string> SessionUpdateAsync(
        [Description("Project ID")] string project_id,
        [Description("New focus area")] string? focus_area = null,
        [Description("Active issues")] string? active_issues = null,
        [Description("Append to session summary")] string? summary = null,
        [Description("Serialized session state")] string? serialized_state = null,
        [Description("Set true to end the session")] bool end = false,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "session_update",
            token => api.PutAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/sessions/current",
                new { focus_area, active_issues, summary, serialized_state, end }, token),
            ct);
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    [McpServerTool(Name = "memory_export"), Description("Export project memory to .squad/ and .agentweaver/context/ files and report the paths written.")]
    public async Task<string> MemoryExportAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "memory_export",
            token => api.PostAsync<JsonElement>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/memory/export", null, token),
            ct);
    }

    [McpServerTool(Name = "memory_import"), Description("Import .squad/decisions/inbox/*.md files into the project memory DB.")]
    public async Task<string> MemoryImportAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        return await ExecuteMessageAsync(
            "memory_import",
            token => api.PostAsync($"api/projects/{Uri.EscapeDataString(project_id)}/memory/import", null, token),
            "imported",
            ct);
    }
}
