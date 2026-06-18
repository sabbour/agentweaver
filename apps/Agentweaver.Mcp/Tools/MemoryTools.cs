using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class MemoryTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string BuildQs(params (string key, string? value)[] pairs)
    {
        var parts = pairs
            .Where(p => p.value is not null)
            .Select(p => $"{p.key}={Uri.EscapeDataString(p.value!)}");
        var qs = string.Join("&", parts);
        return qs.Length > 0 ? "?" + qs : string.Empty;
    }

    // ── Decision Inbox ──────────────────────────────────────────────────────

    [McpServerTool(Name = "inbox_submit"), Description("Submit a decision or learning to the agent inbox.")]
    public async Task<string> InboxSubmitAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name submitting the entry")] string agent_name,
        [Description("Unique slug for idempotency (e.g. 'prefer-async-over-sync')")] string slug,
        [Description("Type: learning | pattern | update | architectural | scope | process | technical")] string type,
        [Description("Short title")] string title,
        [Description("Full content")] string content,
        [Description("Optional rationale")] string? rationale = null,
        CancellationToken ct = default)
    {
        var result = await api.PostAsync<object>(
            $"api/projects/{project_id}/inbox",
            new { agent_name, slug, type, title, content, rationale }, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "inbox_list"), Description("List inbox entries for a project.")]
    public async Task<string> InboxListAsync(
        [Description("Project ID")] string project_id,
        [Description("Filter by agent name")] string? agent = null,
        [Description("Filter by type")] string? type = null,
        [Description("Filter by status: pending | merged | rejected (default: pending)")] string? status = null,
        CancellationToken ct = default)
    {
        var result = await api.GetAsync<object>(
            $"api/projects/{project_id}/inbox{BuildQs(("agent", agent), ("type", type), ("status", status))}", ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "inbox_merge"), Description("Merge a pending inbox entry into team decisions.")]
    public async Task<string> InboxMergeAsync(
        [Description("Project ID")] string project_id,
        [Description("Inbox entry ID")] string entry_id,
        CancellationToken ct = default)
    {
        var result = await api.PostAsync<object>($"api/projects/{project_id}/inbox/{entry_id}/merge", null, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "inbox_reject"), Description("Reject a pending inbox entry.")]
    public async Task<string> InboxRejectAsync(
        [Description("Project ID")] string project_id,
        [Description("Inbox entry ID")] string entry_id,
        CancellationToken ct = default)
    {
        await api.PostAsync($"api/projects/{project_id}/inbox/{entry_id}/reject", null, ct);
        return "rejected";
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
        var result = await api.PostAsync<object>(
            $"api/projects/{project_id}/decisions",
            new { agent_name, type, title, content, rationale }, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "decision_list"), Description("List team decisions for a project.")]
    public async Task<string> DecisionListAsync(
        [Description("Project ID")] string project_id,
        [Description("Filter by type")] string? type = null,
        [Description("Filter by agent")] string? agent = null,
        CancellationToken ct = default)
    {
        var result = await api.GetAsync<object>(
            $"api/projects/{project_id}/decisions{BuildQs(("type", type), ("agent", agent))}", ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "decision_update"), Description("Update a decision's status or content.")]
    public async Task<string> DecisionUpdateAsync(
        [Description("Project ID")] string project_id,
        [Description("Decision ID")] string decision_id,
        [Description("New status: active | superseded | archived")] string? status = null,
        [Description("New content")] string? content = null,
        [Description("ID of the superseding decision")] string? superseded_by_id = null,
        CancellationToken ct = default)
    {
        var result = await api.PutAsync<object>(
            $"api/projects/{project_id}/decisions/{decision_id}",
            new { status, content, superseded_by_id }, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    // ── Agent Memory ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "memory_add"), Description("Add a memory entry for an agent.")]
    public async Task<string> MemoryAddAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Type: learning | pattern | core_context | update")] string type,
        [Description("Content")] string content,
        [Description("Importance: low | medium | high")] string importance = "medium",
        [Description("Comma-separated tags")] string? tags = null,
        CancellationToken ct = default)
    {
        var result = await api.PostAsync<object>(
            $"api/projects/{project_id}/agents/{agent_name}/memory",
            new { type, content, importance, tags }, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "memory_list"), Description("List memory entries for a specific agent.")]
    public async Task<string> MemoryListAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Filter by type")] string? type = null,
        [Description("Filter by importance")] string? importance = null,
        CancellationToken ct = default)
    {
        var result = await api.GetAsync<object>(
            $"api/projects/{project_id}/agents/{agent_name}/memory{BuildQs(("type", type), ("importance", importance))}", ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "memory_get"), Description("Get a single memory entry.")]
    public async Task<string> MemoryGetAsync(
        [Description("Project ID")] string project_id,
        [Description("Agent name")] string agent_name,
        [Description("Memory entry ID")] string memory_id,
        CancellationToken ct = default)
    {
        var result = await api.GetAsync<object>(
            $"api/projects/{project_id}/agents/{agent_name}/memory/{memory_id}", ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "memory_search"), Description("Cross-agent memory search across the whole project.")]
    public async Task<string> MemorySearchAsync(
        [Description("Project ID")] string project_id,
        [Description("Filter by type")] string? type = null,
        [Description("Comma-separated tags to filter by (OR semantics)")] string? tags = null,
        CancellationToken ct = default)
    {
        var result = await api.GetAsync<object>(
            $"api/projects/{project_id}/memory{BuildQs(("type", type), ("tags", tags))}", ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    // ── Sessions ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "session_start"), Description("Start a new work session for a project.")]
    public async Task<string> SessionStartAsync(
        [Description("Project ID")] string project_id,
        [Description("Unique session ID")] string session_id,
        [Description("Current focus area")] string focus_area,
        [Description("Active issues (optional)")] string? active_issues = null,
        CancellationToken ct = default)
    {
        var result = await api.PostAsync<object>(
            $"api/projects/{project_id}/sessions",
            new { session_id, focus_area, active_issues }, ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "session_current"), Description("Get the current open session for a project.")]
    public async Task<string> SessionCurrentAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        var result = await api.GetAsync<object>($"api/projects/{project_id}/sessions/current", ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "session_update"), Description("Update the current session's focus, summary, or end it.")]
    public async Task<string> SessionUpdateAsync(
        [Description("Project ID")] string project_id,
        [Description("New focus area")] string? focus_area = null,
        [Description("Active issues")] string? active_issues = null,
        [Description("Append to session summary")] string? summary = null,
        [Description("Set true to end the session")] bool end = false,
        CancellationToken ct = default)
    {
        await api.PutAsync(
            $"api/projects/{project_id}/sessions/current",
            new { focus_area, active_issues, summary, end }, ct);
        return "updated";
    }

    // ── Export / Import ───────────────────────────────────────────────────────

    [McpServerTool(Name = "memory_export"), Description("Export project memory to .squad/ and .agentweaver/context/ files.")]
    public async Task<string> MemoryExportAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        await api.PostAsync($"api/projects/{project_id}/memory/export", null, ct);
        return "exported";
    }

    [McpServerTool(Name = "memory_import"), Description("Import .squad/decisions/inbox/*.md files into the project memory DB.")]
    public async Task<string> MemoryImportAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        await api.PostAsync($"api/projects/{project_id}/memory/import", null, ct);
        return "imported";
    }
}
