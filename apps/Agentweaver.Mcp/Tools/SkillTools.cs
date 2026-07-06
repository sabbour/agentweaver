using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

/// <summary>
/// Project-scoped skill catalog tools (issues #51/#56). These mirror the REST surface in
/// <c>SkillEndpoints</c> so the MCP and Web UI expose identical behavior (constitution IV):
/// acquire skills (connected-repo sync / repo import) and assign catalog skills to agents.
/// File/folder/archive upload is intentionally omitted here (multipart is a Web-only surface).
/// </summary>
[McpServerToolType]
public sealed class SkillTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string FormatFailure(string toolName, Exception ex) =>
        ex is McpApiException apiEx
            ? $"{toolName} failed: HTTP {apiEx.StatusCode} — {apiEx.Message}"
            : $"{toolName} failed: {ex.Message}";

    private static string SerializeResult<T>(T result) => JsonSerializer.Serialize(result, JsonOpts);

    private static async Task<string> ExecuteJsonAsync<T>(
        string toolName,
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
        catch (Exception ex)
        {
            return FormatFailure(toolName, ex);
        }
    }

    private static async Task<string> ExecuteMessageAsync(
        string toolName,
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
        catch (Exception ex)
        {
            return FormatFailure(toolName, ex);
        }
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "skill_list"), Description("List catalog skills for a project with their agent assignments and status.")]
    public async Task<string> SkillListAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_list",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills", token),
            ct);
    }

    [McpServerTool(Name = "skill_get"), Description("Get a single catalog skill including SKILL.md instructions and bundled resources.")]
    public async Task<string> SkillGetAsync(
        [Description("Project ID")] string project_id,
        [Description("Skill ID")] string skill_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_get",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/{Uri.EscapeDataString(skill_id)}", token),
            ct);
    }

    [McpServerTool(Name = "skill_delete"), Description("Delete a catalog skill and all of its agent assignments.")]
    public async Task<string> SkillDeleteAsync(
        [Description("Project ID")] string project_id,
        [Description("Skill ID")] string skill_id,
        CancellationToken ct = default)
    {
        return await ExecuteMessageAsync(
            "skill_delete",
            token => api.DeleteAsync(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/{Uri.EscapeDataString(skill_id)}", token),
            "deleted",
            ct);
    }

    // ── Acquisition ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "skill_sync"), Description("Discover and sync skills already present in the project's connected repository (.github/skills, .copilot/skills, .claude/skills, .agents/skills). Idempotent; marks vanished skills as missing.")]
    public async Task<string> SkillSyncAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_sync",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/sync", null, token),
            ct);
    }

    [McpServerTool(Name = "skill_import_preview"), Description("Clone a Git repo and list candidate skills found in recognized skill locations, without importing.")]
    public async Task<string> SkillImportPreviewAsync(
        [Description("Project ID")] string project_id,
        [Description("Git repository URL to inspect")] string repo_url,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_import_preview",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/import/preview",
                new { repoUrl = repo_url }, token),
            ct);
    }

    [McpServerTool(Name = "skill_import"), Description("Import selected skills from a Git repo into the project catalog. Idempotent by content hash. Omit locations to import all discovered candidates.")]
    public async Task<string> SkillImportAsync(
        [Description("Project ID")] string project_id,
        [Description("Git repository URL to import from")] string repo_url,
        [Description("Optional comma-separated skill locations (as returned by skill_import_preview). Omit to import all candidates.")] string? locations = null,
        CancellationToken ct = default)
    {
        var locs = string.IsNullOrWhiteSpace(locations)
            ? null
            : locations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await ExecuteJsonAsync(
            "skill_import",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/import",
                new { repoUrl = repo_url, locations = locs }, token),
            ct);
    }

    // ── Assignment ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "skill_assignments_list"), Description("List all skill→agent assignments in a project.")]
    public async Task<string> SkillAssignmentsListAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_assignments_list",
            token => api.GetAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/assignments", token),
            ct);
    }

    [McpServerTool(Name = "skill_assign"), Description("Assign a catalog skill to an agent. At prompt-assembly time only assigned skills appear for that agent (progressive disclosure).")]
    public async Task<string> SkillAssignAsync(
        [Description("Project ID")] string project_id,
        [Description("Skill ID")] string skill_id,
        [Description("Agent name to assign the skill to")] string agent_name,
        CancellationToken ct = default)
    {
        return await ExecuteMessageAsync(
            "skill_assign",
            token => api.PutAsync(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/{Uri.EscapeDataString(skill_id)}/assignments/{Uri.EscapeDataString(agent_name)}",
                null, token),
            "assigned",
            ct);
    }

    [McpServerTool(Name = "skill_unassign"), Description("Remove a skill assignment from an agent.")]
    public async Task<string> SkillUnassignAsync(
        [Description("Project ID")] string project_id,
        [Description("Skill ID")] string skill_id,
        [Description("Agent name to unassign the skill from")] string agent_name,
        CancellationToken ct = default)
    {
        return await ExecuteMessageAsync(
            "skill_unassign",
            token => api.DeleteAsync(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/{Uri.EscapeDataString(skill_id)}/assignments/{Uri.EscapeDataString(agent_name)}", token),
            "unassigned",
            ct);
    }
}
