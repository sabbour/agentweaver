using Agentweaver.Api.Webhooks;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Evaluates the curated workflow event-trigger predicate DSL against the minimal structured GitHub
/// webhook payload shape. This is intentionally NOT a general expression engine.
/// </summary>
public static class WorkflowTriggerPredicateEvaluator
{
    public static bool EvaluateAll(
        IReadOnlyList<WorkflowTriggerPredicate> predicates,
        string eventName,
        GitHubWebhookPayload? payload)
    {
        if (predicates.Count == 0) return true;
        if (payload is null) return false;
        if (!TryGetGitHubEventType(eventName, out var eventType)) return false;

        return predicates.All(predicate => Evaluate(predicate, eventType, payload));
    }

    private static bool Evaluate(WorkflowTriggerPredicate predicate, string eventType, GitHubWebhookPayload payload)
    {
        if (predicate.HasLabel is { } hasLabel)
            return SupportsEventType(eventType, "issues", "pull_request")
                && payload.CurrentLabels.Any(x => string.Equals(x, hasLabel.Label, StringComparison.OrdinalIgnoreCase));

        if (predicate.IsNotLabeledWith is { } isNotLabeledWith)
            return SupportsEventType(eventType, "issues", "pull_request")
                && payload.CurrentLabels.All(x => !string.Equals(x, isNotLabeledWith.Label, StringComparison.OrdinalIgnoreCase));

        if (predicate.BaseBranch is { } baseBranch)
            return SupportsEventType(eventType, "pull_request")
                && string.Equals(payload.PullRequest?.Base?.Ref, baseBranch.Branch, StringComparison.Ordinal);

        if (predicate.ReviewState is { } reviewState)
            return SupportsEventType(eventType, "pull_request_review")
                && string.Equals(payload.Review?.State, ReviewState(reviewState.State), StringComparison.OrdinalIgnoreCase);

        if (predicate.Ref is { } refPredicate)
        {
            if (!SupportsEventType(eventType, "push") || string.IsNullOrWhiteSpace(payload.Ref))
                return false;

            return refPredicate.MatchMode switch
            {
                WorkflowTriggerMatchMode.Equals => string.Equals(payload.Ref, refPredicate.Branch, StringComparison.Ordinal),
                WorkflowTriggerMatchMode.Prefix => payload.Ref.StartsWith(refPredicate.Branch, StringComparison.Ordinal),
                _ => false,
            };
        }

        if (predicate.Category is { } category)
            return SupportsEventType(eventType, "discussion")
                && string.Equals(payload.Discussion?.Category?.Name, category.Name, StringComparison.OrdinalIgnoreCase);

        if (predicate.CommentMatches is { } commentMatches)
            return SupportsEventType(eventType, "issue_comment")
                && MatchesCommentPattern(commentMatches.Pattern, payload.Comment?.Body);

        if (predicate.Or.Count > 0)
            return predicate.Or.Any(child => Evaluate(child, eventType, payload));

        if (predicate.Not is { } not)
            return !Evaluate(not, eventType, payload);

        return false;
    }

    private static bool MatchesCommentPattern(string pattern, string? commentBody)
    {
        if (string.IsNullOrWhiteSpace(commentBody)) return false;
        return WorkflowTriggerRegexPolicy.IsMatch(pattern, commentBody);
    }

    private static string ReviewState(WorkflowTriggerReviewState state) => state switch
    {
        WorkflowTriggerReviewState.Approved => "approved",
        WorkflowTriggerReviewState.ChangesRequested => "changes_requested",
        WorkflowTriggerReviewState.Commented => "commented",
        _ => string.Empty,
    };

    private static bool TryGetGitHubEventType(string eventName, out string eventType)
    {
        const string prefix = "github.";
        if (!eventName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            eventType = string.Empty;
            return false;
        }

        var remainder = eventName[prefix.Length..].Trim();
        if (remainder.Length == 0)
        {
            eventType = string.Empty;
            return false;
        }

        var separator = remainder.IndexOf('.');
        eventType = (separator >= 0 ? remainder[..separator] : remainder).Trim().ToLowerInvariant();
        return eventType.Length > 0;
    }

    private static bool SupportsEventType(string actualEventType, params string[] supportedEventTypes) =>
        supportedEventTypes.Any(x => string.Equals(x, actualEventType, StringComparison.OrdinalIgnoreCase));
}
