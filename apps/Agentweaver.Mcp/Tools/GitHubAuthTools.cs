using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class GitHubAuthTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly HashSet<string> PublicAuthorizationStatuses =
        ["pending", "completed", "failed", "expired"];

    [McpServerTool(Name = "github_repo_app_connect"), Description(
        "Begin the current human's Repo App authorization. Returns an opaque transaction ID, a browser URL, and expiry. Open browser_url in a browser, then poll github_repo_app_authorization_status. No credential, OAuth state, or callback cookie is returned.")]
    public async Task<string> GitHubRepoAppConnectAsync(CancellationToken ct)
    {
        try
        {
            var handoff = await api.PostAsync<JsonElement>(
                "/api/auth/github/repo-app/authorizations/handoff",
                new { return_route_key = "settings" },
                ct);
            return SerializeHandoff(handoff);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "github_repo_app_authorization_status"), Description(
        "Poll the current human's Repo App browser authorization transaction. Returns only pending, completed, failed, or expired.")]
    public Task<string> GitHubRepoAppAuthorizationStatusAsync(
        [Description("Opaque transaction_id returned by github_repo_app_connect.")] string transaction_id,
        CancellationToken ct) =>
        PollAsync($"/api/auth/github/repo-app/authorizations/{Uri.EscapeDataString(transaction_id)}", ct);

    [McpServerTool(Name = "github_repo_app_disconnect"), Description(
        "Disconnect the current human's Repo App authorization. This de-privileges the current human and invalidates outstanding authorization transactions.")]
    public async Task<string> GitHubRepoAppDisconnectAsync(CancellationToken ct)
    {
        try
        {
            await api.DeleteAsync("/api/auth/github/repo-app/authorization", ct);
            return Serialize(new { status = "disconnected" });
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_copilot_app_connect"), Description(
        "Begin an Owner-authorized, project-bound Copilot App connection. Returns an opaque transaction ID, browser URL, and expiry. Open browser_url, then poll project_copilot_app_authorization_status. No credential, OAuth state, callback cookie, repository, installation, or permission data is returned.")]
    public async Task<string> ProjectCopilotAppConnectAsync(
        [Description("Agentweaver project ID to bind. The backend derives and verifies current Owner authority.")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var handoff = await api.PostAsync<JsonElement>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/github/copilot/authorizations/handoff",
                null,
                ct);
            return SerializeHandoff(handoff);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_copilot_app_authorization_status"), Description(
        "Poll the initiating human's project-bound Copilot App browser authorization. Returns only pending, completed, failed, or expired.")]
    public Task<string> ProjectCopilotAppAuthorizationStatusAsync(
        [Description("Project ID originally passed to project_copilot_app_connect.")] string project_id,
        [Description("Opaque transaction_id returned by project_copilot_app_connect.")] string transaction_id,
        CancellationToken ct) =>
        PollAsync(
            $"/api/projects/{Uri.EscapeDataString(project_id)}/github/copilot/authorizations/{Uri.EscapeDataString(transaction_id)}",
            ct);

    [McpServerTool(Name = "project_copilot_app_disconnect"), Description(
        "Disconnect a project Copilot App binding. The backend allows this de-privileging operation only to an authorized human project Owner or platform administrator.")]
    public async Task<string> ProjectCopilotAppDisconnectAsync(
        [Description("Project ID whose Copilot App binding will be disconnected.")] string project_id,
        CancellationToken ct)
    {
        try
        {
            await api.DeleteAsync(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/github/copilot/binding",
                ct);
            return Serialize(new { status = "disconnected" });
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "project_github_capability_status"), Description(
        "Get the server-derived, redacted unattended GitHub capability readiness for a project. No GitHub identities, credentials, installations, repositories, or permissions are returned.")]
    public async Task<string> ProjectGitHubCapabilityStatusAsync(
        [Description("Project ID to inspect. The backend verifies current project Owner authority.")] string project_id,
        CancellationToken ct)
    {
        try
        {
            var readiness = await api.GetAsync<JsonElement>(
                $"/api/projects/{Uri.EscapeDataString(project_id)}/github/unattended-readiness",
                ct);
            return Serialize(new
            {
                status = RequiredString(readiness, "status"),
                reason_code = RequiredString(readiness, "reason_code"),
                message = RequiredString(readiness, "message"),
                repo_app_installation_connected = RequiredBoolean(readiness, "repo_app_installation_connected"),
            });
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    private async Task<string> PollAsync(string path, CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>(path, ct);
            var status = RequiredString(result, "status");
            if (!PublicAuthorizationStatuses.Contains(status))
                throw new McpApiException(0, "The authorization status response was invalid.", path);
            return Serialize(new { status });
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message, path); }
    }

    private static string SerializeHandoff(JsonElement handoff) => Serialize(new
    {
        transaction_id = RequiredString(handoff, "transaction_id"),
        browser_url = RequiredString(handoff, "browser_url"),
        expires_at = RequiredString(handoff, "expires_at"),
    });

    private static string RequiredString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) &&
        result.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(result.GetString())
            ? result.GetString()!
            : throw new InvalidOperationException($"Missing safe '{property}' response field.");

    private static bool RequiredBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) &&
        result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean()
            : throw new InvalidOperationException($"Missing safe '{property}' response field.");

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOpts);
}
