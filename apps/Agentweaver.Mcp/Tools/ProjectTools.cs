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
        [Description("Local working directory path")] string working_directory,
        [Description("Project origin: 'blank' (default) or 'github'")] string? origin,
        [Description("Predefined blueprint ID to apply (optional; exclusive with blueprint)")] string? blueprint_id,
        [Description("Inline blueprint object to apply at creation (optional JSON object; exclusive with blueprint_id)")] JsonElement? blueprint,
        [Description("Generated workflow YAML returned by blueprint_generate (optional; forwarded as generated_workflow_yaml)")] string? generated_workflow_yaml,
        CancellationToken ct)
    {
        try
        {
            var bodyNode = new JsonObject
            {
                ["name"] = name,
                ["working_directory"] = working_directory,
            };
            if (origin is not null) bodyNode["origin"] = origin;
            if (blueprint_id is not null) bodyNode["blueprint_id"] = blueprint_id;
            if (blueprint.HasValue) bodyNode["blueprint"] = JsonNode.Parse(blueprint.Value.GetRawText());
            if (!string.IsNullOrWhiteSpace(generated_workflow_yaml)) bodyNode["generated_workflow_yaml"] = generated_workflow_yaml;

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

    [McpServerTool(Name = "project_relink"), Description("Relink a project to a new local working directory path.")]
    public async Task<string> ProjectRelinkAsync(
        [Description("Project ID")] string project_id,
        [Description("New working directory path")] string working_directory,
        CancellationToken ct)
    {
        try
        {
            var body = new { working_directory };
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
            await api.DeleteAsync($"/api/projects/{project_id}?confirm=true", ct);
            return "Project deleted successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_configure"), Description("Configure the AI model provider settings for a project.")]
    public async Task<string> ProjectConfigureAsync(
        [Description("Project ID")] string project_id,
        [Description("Default model provider (e.g. github_copilot or microsoft_foundry)")] string default_provider,
        [Description("Model ID for GitHub Copilot provider (optional)")] string? default_model_github_copilot,
        [Description("Model ID for Microsoft Foundry provider (optional)")] string? default_model_microsoft_foundry,
        CancellationToken ct)
    {
        try
        {
            var body = new { default_provider, default_model_github_copilot, default_model_microsoft_foundry };
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

    [McpServerTool(Name = "get_project_usage"), Description("Get token and AI Credit usage for a project over a time range (default: last 30 days).")]
    public async Task<string> GetProjectUsageAsync(
        [Description("Project ID")] string project_id,
        [Description("Start of the time range in ISO 8601 format (optional, default: 30 days ago)")] string? from,
        [Description("End of the time range in ISO 8601 format (optional, default: now)")] string? to,
        CancellationToken ct)
    {
        try
        {
            var query = new System.Text.StringBuilder($"/api/projects/{project_id}/usage");
            var sep = '?';
            if (!string.IsNullOrWhiteSpace(from))  { query.Append($"{sep}from={Uri.EscapeDataString(from)}");  sep = '&'; }
            if (!string.IsNullOrWhiteSpace(to))    { query.Append($"{sep}to={Uri.EscapeDataString(to)}");      sep = '&'; }
            var result = await api.GetAsync<JsonElement>(query.ToString(), ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
