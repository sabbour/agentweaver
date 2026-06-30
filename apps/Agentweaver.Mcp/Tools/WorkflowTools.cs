using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

/// <summary>Response from POST /api/projects/{id}/workflows/generate.</summary>
internal sealed record GenerateWorkflowResponse(
    [property: JsonPropertyName("yaml")] string Yaml,
    [property: JsonPropertyName("workflow_id")] string WorkflowId,
    [property: JsonPropertyName("was_corrected")] bool WasCorrected);

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
            var result = await api.GetAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}/workflows", ct);
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
            var result = await api.GetAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}/workflows/{Uri.EscapeDataString(workflow_id)}", ct);
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
            var result = await api.PostAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}/workflows/sync", body: null, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    /// <summary>
    /// Generate a new workflow definition from a natural language description.
    /// Returns YAML draft — not yet saved. Use workflow_save to persist. The agent can inspect the YAML before saving.
    /// </summary>
    [McpServerTool(Name = "workflow_generate"), Description(
        "Generate a new workflow definition from a natural language description. " +
        "Returns YAML draft — not yet saved. Use workflow_save to persist. " +
        "The agent can inspect the YAML before saving.")]
    public async Task<string> WorkflowGenerateAsync(
        [Description("Project ID")] string project_id,
        [Description("Natural language description of the workflow to generate")] string description,
        CancellationToken ct)
    {
        try
        {
            var body = new { description };
            var result = await api.PostAsync<GenerateWorkflowResponse>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/workflows/generate", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException ex) when (ex.StatusCode == 404)
        {
            throw new McpApiException(404, $"Project {Uri.EscapeDataString(project_id)} not found");
        }
        catch (McpApiException ex) when (ex.StatusCode == 400)
        {
            throw new McpApiException(400, $"Workflow generation failed: {ex.Message}");
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    /// <summary>
    /// Save a workflow YAML to the project workspace.
    /// Validates and dry-run binds before saving. Returns the parsed workflow definition.
    /// </summary>
    [McpServerTool(Name = "workflow_save"), Description(
        "Save a workflow YAML to the project workspace. " +
        "Validates and dry-run binds before saving. " +
        "Returns the parsed workflow definition.")]
    public async Task<string> WorkflowSaveAsync(
        [Description("Project ID")] string project_id,
        [Description("Workflow ID (must match the 'id' field declared in the YAML)")] string workflow_id,
        [Description("Full YAML content of the workflow definition")] string yaml,
        CancellationToken ct)
    {
        try
        {
            var body = new { yaml };
            var result = await api.PutAsync<JsonElement>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/workflows/{Uri.EscapeDataString(workflow_id)}", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException ex) when (ex.StatusCode == 404)
        {
            throw new McpApiException(404, $"Project {Uri.EscapeDataString(project_id)} not found");
        }
        catch (McpApiException ex) when (ex.StatusCode == 400)
        {
            throw new McpApiException(400, $"Workflow validation failed: {ex.Message}");
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
