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
        [Description("Blueprint object to validate (JSON object with id, name, description, roster, workflow or workflows, bespoke_roles, generated_workflow_yaml, review_policy, sandbox_profile)")] JsonElement blueprint,
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
    /// Accept a durable project Blueprint generation job.
    /// </summary>
    [McpServerTool(Name = "blueprint_generate"), Description(
        "Start durable asynchronous Blueprint generation. Returns a job id and status/result/cancel/retry URLs immediately; use the Blueprint generation job tools to follow it.")]
    public async Task<string> BlueprintGenerateAsync(
        [Description("Natural language description of the team and goals")] string description,
        [Description("Optional existing project id whose provider and generation model settings should be snapshotted")] string? project_id = null,
        [Description("Optional owner/name repository context for the generated Blueprint")] string? target_repository = null,
        [Description("Stable retry key. Reuse it only for the identical request; omit to generate a new key")] string? idempotency_key = null,
        CancellationToken ct = default)
    {
        try
        {
            var body = new { description, project_id, target_repository };
            var result = await api.PostAiAsync<BlueprintGenerationJobResponse>(
                "/api/blueprints/generate",
                body,
                "blueprint_generation",
                projectId: project_id,
                ct: ct,
                idempotencyKey: idempotency_key ?? Guid.NewGuid().ToString("N"));
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException ex) when (ex.StatusCode == 400)
        {
            throw new McpApiException(400, $"Blueprint generation failed: {ex.Message}");
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "blueprint_generation_status"), Description("Get the authorized status of a durable Blueprint generation job.")]
    public async Task<string> BlueprintGenerationStatusAsync(
        [Description("Blueprint generation job id")] string job_id,
        CancellationToken ct)
    {
        var result = await api.GetAsync<BlueprintGenerationJobResponse>(
            $"/api/blueprints/generation-jobs/{Uri.EscapeDataString(job_id)}",
            ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "blueprint_generation_result"), Description("Get the immutable Blueprint artifact for a completed generation job.")]
    public async Task<string> BlueprintGenerationResultAsync(
        [Description("Blueprint generation job id")] string job_id,
        CancellationToken ct)
    {
        var result = await api.GetAsync<BlueprintGenerationResultResponse>(
            $"/api/blueprints/generation-jobs/{Uri.EscapeDataString(job_id)}/result",
            ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "blueprint_generation_cancel"), Description("Cancel an authorized queued or running Blueprint generation job.")]
    public async Task<string> BlueprintGenerationCancelAsync(
        [Description("Blueprint generation job id")] string job_id,
        CancellationToken ct)
    {
        var result = await api.PostAsync<BlueprintGenerationJobResponse>(
            $"/api/blueprints/generation-jobs/{Uri.EscapeDataString(job_id)}/cancel",
            body: null,
            ct: ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "blueprint_generation_retry"), Description("Retry an authorized failed or cancelled Blueprint generation job without creating another artifact identity.")]
    public async Task<string> BlueprintGenerationRetryAsync(
        [Description("Blueprint generation job id")] string job_id,
        CancellationToken ct)
    {
        var result = await api.PostAsync<BlueprintGenerationJobResponse>(
            $"/api/blueprints/generation-jobs/{Uri.EscapeDataString(job_id)}/retry",
            body: null,
            ct: ct);
        return JsonSerializer.Serialize(result, JsonOpts);
    }
}
