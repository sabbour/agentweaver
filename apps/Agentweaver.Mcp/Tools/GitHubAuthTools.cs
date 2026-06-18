using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class GitHubAuthTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "github_status"), Description("Check the current GitHub authentication status.")]
    public async Task<string> GitHubStatusAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>("/api/auth/github", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "github_signout"), Description("Sign out of GitHub authentication.")]
    public async Task<string> GitHubSignOutAsync(CancellationToken ct)
    {
        try
        {
            await api.PostAsync("/api/auth/github/sign-out", null, ct);
            return "Signed out of GitHub successfully.";
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "github_signin"), Description(
        "Sign in to GitHub using the device flow. Returns a user code and verification URL. " +
        "The user must visit the URL and enter the code. Polls until authentication completes or times out.")]
    public async Task<string> GitHubSignInAsync(
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        try
        {
            // Step 1: Start device flow
            var deviceFlow = await api.PostAsync<JsonElement>("/api/auth/github/device", null, ct);

            string? userCode = null;
            string? verificationUri = null;
            int expiresIn = 900;
            int interval = 5;

            if (deviceFlow.TryGetProperty("user_code", out var uc)) userCode = uc.GetString();
            if (deviceFlow.TryGetProperty("verification_uri", out var vu)) verificationUri = vu.GetString();
            if (deviceFlow.TryGetProperty("expires_in", out var ei)) expiresIn = ei.GetInt32();
            if (deviceFlow.TryGetProperty("interval", out var iv)) interval = iv.GetInt32();

            progress.Report(new ProgressNotificationValue
            {
                Message = $"Open {verificationUri} and enter code: {userCode}",
                Progress = 0
            });

            // Step 2: Poll
            var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(interval, 5)), ct);

                var poll = await api.PostAsync<JsonElement>("/api/auth/github/poll", null, ct);
                string? status = poll.TryGetProperty("status", out var s) ? s.GetString() : null;

                if (status == "success" || status == "complete")
                {
                    string? login = poll.TryGetProperty("login", out var l) ? l.GetString() : null;
                    return $"Authenticated as {login ?? "unknown"}";
                }

                if (status == "expired" || status == "error")
                    throw new McpApiException(0, $"GitHub authentication failed with status: {status}");

                progress.Report(new ProgressNotificationValue
                {
                    Message = "Waiting for browser authentication...",
                    Progress = 0
                });
            }

            throw new McpApiException(0, "Authentication timed out. Please try again.");
        }
        catch (McpApiException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
