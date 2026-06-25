using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Mcp.Contracts;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class BlueprintTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "list_blueprints"), Description("List the predefined Agentweaver blueprints. Each blueprint specifies a team roster, workflow, review policy, and sandbox profile ready to apply at project creation.")]
    public async Task<string> ListBlueprintsAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<ListBlueprintsResponse>("/api/blueprints", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "validate_blueprint"), Description("Validate a blueprint object against the schema and role constraints. Returns valid:true with an empty errors array on success, or valid:false with a list of validation errors.")]
    public async Task<string> ValidateBlueprintAsync(
        [Description("Blueprint object to validate (JSON object with id, name, description, roster, workflow, review_policy, sandbox_profile)")] JsonElement blueprint,
        CancellationToken ct)
    {
        try
        {
            var bodyNode = new JsonObject
            {
                ["blueprint"] = JsonNode.Parse(blueprint.GetRawText()),
            };
            var result = await api.PostAsync<ValidateBlueprintResponse>("/api/blueprints/validate", bodyNode, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    /// <summary>
    /// Generate a project blueprint from a natural language description of the team and goals.
    /// Returns the generated blueprint including roster and workflow assignments.
    /// The agent can inspect before creating a project.
    /// </summary>
    [McpServerTool(Name = "blueprint_generate"), Description(
        "Generate a project blueprint from a natural language description of the team and goals. " +
        "Returns the generated blueprint including roster and workflow assignments. " +
        "The agent can inspect before creating a project.")]
    public async Task<string> BlueprintGenerateAsync(
        [Description("Natural language description of the team and goals")] string description,
        CancellationToken ct)
    {
        try
        {
            var body = new { description };
            var result = await api.PostAsync<GenerateBlueprintResponse>("/api/blueprints/generate", body, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException ex) when (ex.StatusCode == 400)
        {
            throw new McpApiException(400, $"Blueprint generation failed: {ex.Message}");
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
