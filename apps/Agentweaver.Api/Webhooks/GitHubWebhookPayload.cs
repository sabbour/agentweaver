using System.Text.Json.Serialization;

namespace Agentweaver.Api.Webhooks;

/// <summary>
/// Minimal subset of the GitHub webhook payload shape needed by the event-trigger pipeline: repository
/// identity plus the small, structured fields used by the trigger predicate DSL. The only raw user
/// text admitted is <c>comment.body</c>, and ONLY so <c>commentMatches</c> can compute a boolean
/// fire/no-fire decision; that body must never be forwarded, logged, or stored anywhere downstream.
/// </summary>
public sealed record GitHubWebhookPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("repository")]
    public GitHubWebhookRepository? Repository { get; init; }

    [JsonPropertyName("issue")]
    public GitHubWebhookIssueLike? Issue { get; init; }

    [JsonPropertyName("pull_request")]
    public GitHubWebhookPullRequest? PullRequest { get; init; }

    [JsonPropertyName("review")]
    public GitHubWebhookReview? Review { get; init; }

    [JsonPropertyName("discussion")]
    public GitHubWebhookDiscussion? Discussion { get; init; }

    [JsonPropertyName("comment")]
    public GitHubWebhookComment? Comment { get; init; }

    [JsonPropertyName("ref")]
    public string? Ref { get; init; }

    public IReadOnlyList<string> CurrentLabels =>
        Issue?.Labels?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
        ?? PullRequest?.Labels?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList()
        ?? [];
}

public sealed record GitHubWebhookRepository
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; init; }
}

public sealed record GitHubWebhookIssueLike
{
    [JsonPropertyName("labels")]
    public IReadOnlyList<GitHubWebhookLabel>? Labels { get; init; }
}

public sealed record GitHubWebhookPullRequest
{
    [JsonPropertyName("labels")]
    public IReadOnlyList<GitHubWebhookLabel>? Labels { get; init; }

    [JsonPropertyName("base")]
    public GitHubWebhookBranchRef? Base { get; init; }
}

public sealed record GitHubWebhookBranchRef
{
    [JsonPropertyName("ref")]
    public string? Ref { get; init; }
}

public sealed record GitHubWebhookReview
{
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

public sealed record GitHubWebhookDiscussion
{
    [JsonPropertyName("category")]
    public GitHubWebhookCategory? Category { get; init; }
}

public sealed record GitHubWebhookCategory
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record GitHubWebhookComment
{
    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

public sealed record GitHubWebhookLabel
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
