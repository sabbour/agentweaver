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
            var result = await api.GetAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_create"), Description("Create a new Agentweaver project. When origin is 'github', repository_selection_code is required; obtain it from the repository-selection endpoints after the caller chooses an authorized repository. Supply blueprint_id to apply a predefined blueprint, or supply blueprint to apply an inline blueprint; the two options are mutually exclusive.")]
    public async Task<string> ProjectCreateAsync(
        [Description("Project name")] string name,
        [Description("Local working directory path")] string working_directory,
        [Description("Inline blueprint object to apply at creation, as a JSON-encoded string (optional; exclusive with blueprint_id)")] string? blueprint = null,
        [Description("Project origin: 'blank' (default) or 'github'")] string? origin = null,
        [Description("Short-lived opaque code returned by POST /api/github/repository-selections; required when origin is 'github'. Do not supply a repository URL or identifier.")] string? repository_selection_code = null,
        [Description("Predefined blueprint ID to apply (optional; exclusive with blueprint)")] string? blueprint_id = null,
        [Description("Generated workflow YAML returned by blueprint_generate (optional; forwarded as generated_workflow_yaml)")] string? generated_workflow_yaml = null,
        CancellationToken ct = default)
    {
        try
        {
            var bodyNode = new JsonObject
            {
                ["name"] = name,
                ["working_directory"] = working_directory,
            };
            if (origin is not null) bodyNode["origin"] = origin;
            if (repository_selection_code is not null) bodyNode["repository_selection_code"] = repository_selection_code;
            if (blueprint_id is not null) bodyNode["blueprint_id"] = blueprint_id;
            if (!string.IsNullOrWhiteSpace(blueprint))
            {
                try
                {
                    bodyNode["blueprint"] = JsonNode.Parse(blueprint);
                }
                catch (JsonException ex)
                {
                    throw new McpApiException(0, $"blueprint is not valid JSON: {ex.Message}");
                }
            }
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
            var result = await api.PatchAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}", body, ct);
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
            await api.DeleteAsync($"/api/projects/{Uri.EscapeDataString(project_id)}?confirm=true", ct);
            return "Project deleted successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_configure"), Description("Configure the AI model provider settings for a project.")]
    public async Task<string> ProjectConfigureAsync(
        [Description("Project ID")] string project_id,
        [Description("Default model provider (e.g. github_copilot or microsoft_foundry)")] string default_provider,
        [Description("Model ID for GitHub Copilot provider (optional)")] string? default_model_github_copilot = null,
        [Description("Model ID for Microsoft Foundry provider (optional)")] string? default_model_microsoft_foundry = null,
        CancellationToken ct = default)
    {
        try
        {
            var body = new { default_provider, default_model_github_copilot, default_model_microsoft_foundry };
            await api.PutAsync($"/api/projects/{Uri.EscapeDataString(project_id)}/provider-settings", body, ct);
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
            var result = await api.GetAsync<JsonElement>($"/api/projects/{Uri.EscapeDataString(project_id)}/runs", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

}
