namespace Agentweaver.Api.Webhooks;

using Agentweaver.Domain;

public sealed record GitHubWebhookProvisioningResult(
    long HookId,
    bool Created,
    string Repository,
    string PayloadUrl);

public sealed class GitHubWebhookProvisioningException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IGitHubWebhookProvisioningService
{
    Task<GitHubWebhookProvisioningResult> ProvisionAsync(
        Project project,
        GitHubTokenScope tokenScope,
        Uri payloadUrl,
        CancellationToken ct = default);
}

/// <summary>
/// Compatibility registration retained until the final legacy cleanup wave. Per-project webhook
/// provisioning is deliberately unavailable: the Repo App owns one App-level webhook.
/// </summary>
public sealed class GitHubWebhookProvisioningService : IGitHubWebhookProvisioningService
{
    public Task<GitHubWebhookProvisioningResult> ProvisionAsync(
        Project project, GitHubTokenScope tokenScope, Uri payloadUrl, CancellationToken ct = default) =>
        Task.FromException<GitHubWebhookProvisioningResult>(new GitHubWebhookProvisioningException(
            StatusCodes.Status410Gone, "Per-project webhook provisioning is unavailable; configure the Repo App webhook."));
}
