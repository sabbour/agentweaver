using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class WorkflowTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "workflows_list"), Description("List all discovered workflow definitions for a project, including their validation status and which one is the effective default.")]
    public async Task<string> WorkflowsListAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/projects/{project_id}/workflows", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "workflow_get"), Description("Get the full definition of a single workflow by ID, including its nodes, edges, and trigger.")]
    public async Task<string> WorkflowGetAsync(
        [Description("Project ID")] string project_id,
        [Description("Workflow ID")] string workflow_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/projects/{project_id}/workflows/{Uri.EscapeDataString(workflow_id)}", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "workflows_sync"), Description("Re-read the project's workflow definitions from disk, refreshing the in-memory registry. Returns the updated workflow list.")]
    public async Task<string> WorkflowsSyncAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.PostAsync<JsonElement>($"/api/projects/{project_id}/workflows/sync", body: null, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
