using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class ProjectTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "project_list"), Description("List all Agentweaver projects.")]
    public async Task<string> ProjectListAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>("/api/projects", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_get"), Description("Get a project by ID.")]
    public async Task<string> ProjectGetAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/projects/{project_id}", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_create"), Description("Create a new Agentweaver project. Supply blueprint_id to apply a predefined blueprint, or supply blueprint to apply an inline blueprint; the two options are mutually exclusive.")]
    public async Task<string> ProjectCreateAsync(
        [Description("Project name")] string name,
        [Description("Local repository path")] string repository_path,
        [Description("Model source (optional, e.g. github_copilot or microsoft_foundry)")] string? model_source,
        [Description("Predefined blueprint ID to apply (optional; exclusive with blueprint)")] string? blueprint_id,
        [Description("Inline blueprint object to apply at creation (optional JSON object; exclusive with blueprint_id)")] JsonElement? blueprint,
        CancellationToken ct)
    {
        try
        {
            var bodyNode = new JsonObject
            {
                ["name"] = name,
                ["repository_path"] = repository_path,
            };
            if (model_source is not null) bodyNode["model_source"] = model_source;
            if (blueprint_id is not null) bodyNode["blueprint_id"] = blueprint_id;
            if (blueprint.HasValue) bodyNode["blueprint"] = JsonNode.Parse(blueprint.Value.GetRawText());

            var result = await api.PostAsync<JsonElement>("/api/projects", bodyNode, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_rename"), Description("Rename an existing project.")]
    public async Task<string> ProjectRenameAsync(
        [Description("Project ID")] string project_id,
        [Description("New name")] string name,
        CancellationToken ct)
    {
        try
        {
            var body = new { name };
            var result = await api.PatchAsync<JsonElement>($"/api/projects/{project_id}", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_relink"), Description("Relink a project to a new local repository path.")]
    public async Task<string> ProjectRelinkAsync(
        [Description("Project ID")] string project_id,
        [Description("New repository path")] string repository_path,
        CancellationToken ct)
    {
        try
        {
            var body = new { repository_path };
            var result = await api.PostAsync<JsonElement>($"/api/projects/{project_id}/relink", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_delete"), Description("Delete a project by ID.")]
    public async Task<string> ProjectDeleteAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            await api.DeleteAsync($"/api/projects/{project_id}", ct);
            return "Project deleted successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_configure"), Description("Configure the AI model provider settings for a project.")]
    public async Task<string> ProjectConfigureAsync(
        [Description("Project ID")] string project_id,
        [Description("Model source (e.g. github_copilot or microsoft_foundry)")] string model_source,
        [Description("Specific model ID (optional)")] string? model,
        CancellationToken ct)
    {
        try
        {
            var body = new { model_source, model };
            await api.PutAsync($"/api/projects/{project_id}/provider-settings", body, ct);
            return "Project provider settings updated successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_list_runs"), Description("List all runs for a project.")]
    public async Task<string> ProjectListRunsAsync(
        [Description("Project ID")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>($"/api/projects/{project_id}/runs", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
