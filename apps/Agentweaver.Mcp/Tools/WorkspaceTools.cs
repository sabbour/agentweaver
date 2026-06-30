using System.ComponentModel;
using System.Text.Json;
using Agentweaver.Mcp.Contracts;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class WorkspaceTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "list_project_workspace_refs"),
     Description("List the browsable git refs for a project workspace: the base branch and any active run worktrees.")]
    public async Task<string> ListProjectWorkspaceRefsAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<WorkspaceRefsResponse>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/workspace/refs", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "list_project_workspace"),
     Description("List the flat file tree for a project workspace at a given ref. Defaults to the base branch when ref is omitted.")]
    public async Task<string> ListProjectWorkspaceAsync(
        [Description("Project ID")] string project_id,
        [Description("Branch name or worktree branch to browse (optional, defaults to base branch)")] string? @ref,
        CancellationToken ct)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(@ref)
                ? $"/api/projects/{Uri.EscapeDataString(project_id)}/workspace"
                : $"/api/projects/{Uri.EscapeDataString(project_id)}/workspace?ref={Uri.EscapeDataString(@ref)}";
            var result = await api.GetAsync<IReadOnlyList<WorkspaceNode>>(path, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "get_project_workspace_file"),
     Description("Get the content of a file in a project workspace at a given ref. Defaults to the base branch when ref is omitted.")]
    public async Task<string> GetProjectWorkspaceFileAsync(
        [Description("Project ID")] string project_id,
        [Description("Relative file path within the workspace (forward slashes, e.g. src/main.cs)")] string path,
        [Description("Branch name or worktree branch (optional, defaults to base branch)")] string? @ref,
        CancellationToken ct)
    {
        try
        {
            var encodedPath = string.Join("/", path.Split('/', '\\').Select(Uri.EscapeDataString));
            var url = string.IsNullOrWhiteSpace(@ref)
                ? $"/api/projects/{Uri.EscapeDataString(project_id)}/workspace/files/{encodedPath}/content"
                : $"/api/projects/{Uri.EscapeDataString(project_id)}/workspace/files/{encodedPath}/content?ref={Uri.EscapeDataString(@ref)}";
            var result = await api.GetAsync<WorkspaceFileContent>(url, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
