using Microsoft.AspNetCore.Hosting;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// <see cref="ProjectsWebApplicationFactory"/> variant that additionally configures a
/// <c>GitHubWebhook:Secret</c> so <see cref="Agentweaver.Api.Endpoints.GitHubWebhookEndpoints"/> tests
/// can compute a matching HMAC-SHA256 signature (issue #53 follow-up: GitHub webhook receiver).
/// </summary>
public sealed class GitHubWebhookWebApplicationFactory : ProjectsWebApplicationFactory
{
    public const string WebhookSecret = "webhook-test-secret-99999";

    protected override IDictionary<string, string?> GetAdditionalConfiguration() =>
        new Dictionary<string, string?>
        {
            ["GitHubWebhook:Secret"] = WebhookSecret,
        };
}
