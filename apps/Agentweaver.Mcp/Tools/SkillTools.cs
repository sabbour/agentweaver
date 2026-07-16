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

    [McpServerTool(Name = "skill_create"), Description("Create or update a manual standards-compatible SKILL.md catalog skill. The name must be a lowercase kebab-case command slug.")]
    public async Task<string> SkillCreateAsync(
        [Description("Project ID")] string project_id,
        [Description("Skill command name, lowercase kebab-case")] string name,
        [Description("Skill instructions body")] string instructions,
        [Description("Short description shown when the skill is listed")] string? description = null,
        [Description("Optional display name for clients; not stored in SKILL.md")] string? display_name = null,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_create",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills",
                new { name, displayName = display_name, description, instructions }, token),
            ct);
    }

    [McpServerTool(Name = "skill_generate"), Description("Generate an unsaved SKILL.md draft server-side from a natural language description. Review the draft, then call skill_create to persist it.")]
    public async Task<string> SkillGenerateAsync(
        [Description("Project ID")] string project_id,
        [Description("Natural language description of the skill to generate")] string description,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_generate",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skills/generate",
                new { description }, token),
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

    [McpServerTool(Name = "skill_import_preview"), Description("Preview candidate skills from owner/repo, https://github.com repo/tree/blob URLs, or raw https://raw.githubusercontent.com SKILL.md URLs, without importing.")]
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

    [McpServerTool(Name = "skill_import"), Description("Import selected skills from owner/repo, https://github.com repo/tree/blob URLs, or raw https://raw.githubusercontent.com SKILL.md URLs. Idempotent by content hash. Locations are REQUIRED when a source contains multiple skills; omitting locations works only when the source has a single skill.")]
    public async Task<string> SkillImportAsync(
        [Description("Project ID")] string project_id,
        [Description("Git repository URL to import from")] string repo_url,
        [Description("Comma-separated skill locations (as returned by skill_import_preview). Required when the source contains multiple skills; may be omitted only for a single-skill source.")] string? locations = null,
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

    [McpServerTool(Name = "skill_defaults_preview"), Description("Preview explicit bundled role-skill defaults for a confirmed project team. Returns a digest required by skill_defaults_apply; makes no changes.")]
    public async Task<string> SkillDefaultsPreviewAsync(
        [Description("Project ID")] string project_id,
        [Description("Predefined blueprint ID")] string blueprint_id,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_defaults_preview",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skill-defaults/preview",
                new { blueprintId = blueprint_id }, token),
            ct);
    }

    [McpServerTool(Name = "skill_defaults_apply"), Description("Apply a matching skill_defaults_preview atomically. A stale digest is rejected; preview again before retrying.")]
    public async Task<string> SkillDefaultsApplyAsync(
        [Description("Project ID")] string project_id,
        [Description("Predefined blueprint ID")] string blueprint_id,
        [Description("Digest returned by skill_defaults_preview")] string digest,
        CancellationToken ct = default)
    {
        return await ExecuteJsonAsync(
            "skill_defaults_apply",
            token => api.PostAsync<object>(
                $"api/projects/{Uri.EscapeDataString(project_id)}/skill-defaults/apply",
                new { blueprintId = blueprint_id, digest }, token),
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
