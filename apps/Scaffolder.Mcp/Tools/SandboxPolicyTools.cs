using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Scaffolder.Mcp.Tools;

[McpServerToolType]
public sealed class SandboxPolicyTools(ScaffolderApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "sandbox_policy_get"), Description("Get the sandbox policy for a repository.")]
    public async Task<string> SandboxPolicyGetAsync(
        [Description("Repository path to get the policy for (optional)")] string? repository_path,
        CancellationToken ct)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(repository_path)
                ? "/api/sandbox-policy"
                : $"/api/sandbox-policy?repository_path={Uri.EscapeDataString(repository_path)}";
            var result = await api.GetAsync<JsonElement>(path, ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "sandbox_policy_set"), Description("Set the sandbox policy for a repository.")]
    public async Task<string> SandboxPolicySetAsync(
        [Description("Repository path")] string repository_path,
        [Description("Whether shell access is enabled for agent runs")] bool shell_enabled,
        CancellationToken ct)
    {
        try
        {
            var body = new { repository_path, shell_enabled };
            await api.PutAsync("/api/sandbox-policy", body, ct);
            return "Sandbox policy updated successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
