using System.Text.Json.Serialization;

namespace Agentweaver.Api.Webhooks;

/// <summary>
/// Minimal subset of the GitHub webhook payload shape shared by <c>push</c>, <c>pull_request</c>, and
/// <c>issues</c> events (and, in practice, every other repository-scoped GitHub event): the repository
/// full name (used to match against <c>Project.Origin.SourceRepository</c>, "owner/repo") and the
/// optional <c>action</c> field (present on <c>pull_request</c>/<c>issues</c>-style events, absent on
/// <c>push</c>). Deliberately does not model the full GitHub webhook schema — the receiver only needs
/// enough to route to <c>WorkflowEventTriggerService.FireEventAsync</c>, not to interpret the event.
/// </summary>
public sealed record GitHubWebhookPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("repository")]
    public GitHubWebhookRepository? Repository { get; init; }
}

public sealed record GitHubWebhookRepository
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }
}
