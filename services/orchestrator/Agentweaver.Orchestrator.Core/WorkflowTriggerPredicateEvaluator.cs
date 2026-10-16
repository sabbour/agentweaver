using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public static class WorkflowTriggerPredicateEvaluator
{
    public const int MaximumPredicateDepth = 8;
    public const int MaximumPredicateCount = 128;

    public static WorkflowTriggerPredicateValidationResult ValidateAll(
        IReadOnlyList<WorkflowTriggerPredicate>? predicates,
        string? eventName)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowTriggerPredicateIssue>();
        if (predicates is null)
        {
            Add(issues, WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
                "predicates", "A predicate list is required.");
            return new WorkflowTriggerPredicateValidationResult(issues.ToImmutable());
        }

        if (predicates.Count == 0)
            return WorkflowTriggerPredicateValidationResult.Success();

        if (!TryGetGitHubEventType(eventName, out var eventType))
        {
            Add(issues, WorkflowTriggerPredicateIssueCode.InvalidEventName,
                "eventName", "Predicates require a supported github event name.");
            return new WorkflowTriggerPredicateValidationResult(issues.ToImmutable());
        }

        if (predicates.Count > MaximumPredicateCount)
        {
            Add(issues, WorkflowTriggerPredicateIssueCode.PredicateLimitExceeded,
                "predicates", $"At most {MaximumPredicateCount} predicates are supported.");
            return new WorkflowTriggerPredicateValidationResult(issues.ToImmutable());
        }

        var pending = new Stack<(WorkflowTriggerPredicate? Predicate, string Path, int Depth)>();
        for (var index = predicates.Count - 1; index >= 0; index--)
            pending.Push((predicates[index], $"predicates[{index}]", 1));

        var discoveredCount = predicates.Count;
        while (pending.TryPop(out var item))
        {
            if (item.Depth > MaximumPredicateDepth)
            {
                Add(issues, WorkflowTriggerPredicateIssueCode.PredicateLimitExceeded,
                    item.Path, $"Predicate nesting cannot exceed {MaximumPredicateDepth} levels.");
                continue;
            }

            if (item.Predicate is not { } predicate)
            {
                Add(issues, WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
                    item.Path, "A predicate is required.");
                continue;
            }

            var kindCount =
                (predicate.HasLabel is null ? 0 : 1) +
                (predicate.IsNotLabeledWith is null ? 0 : 1) +
                (predicate.BaseBranch is null ? 0 : 1) +
                (predicate.ReviewState is null ? 0 : 1) +
                (predicate.Ref is null ? 0 : 1) +
                (predicate.Category is null ? 0 : 1) +
                (predicate.CommentMatches is null ? 0 : 1) +
                (predicate.Or is null ? 0 : 1) +
                (predicate.Not is null ? 0 : 1);

            if (kindCount != 1)
            {
                Add(issues, WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
                    item.Path, "Exactly one predicate kind must be declared.");
                continue;
            }

            if (predicate.HasLabel is { } hasLabel)
            {
                RequireSupportedEvent(item.Path + ".hasLabel", eventType is "issues" or "pull_request",
                    "Label predicates are supported only for github.issues and github.pull_request events.", issues);
                RequireText(hasLabel.Label, item.Path + ".hasLabel.label", issues);
            }
            else if (predicate.IsNotLabeledWith is { } isNotLabeledWith)
            {
                RequireSupportedEvent(item.Path + ".isNotLabeledWith",
                    eventType is "issues" or "pull_request",
                    "Label predicates are supported only for github.issues and github.pull_request events.", issues);
                RequireText(isNotLabeledWith.Label, item.Path + ".isNotLabeledWith.label", issues);
            }
            else if (predicate.BaseBranch is { } baseBranch)
            {
                RequireSupportedEvent(item.Path + ".baseBranch",
                    eventType == "pull_request",
                    "Base-branch predicates are supported only for github.pull_request events.", issues);
                RequireText(baseBranch.Branch, item.Path + ".baseBranch.branch", issues);
            }
            else if (predicate.ReviewState is { } reviewState)
            {
                RequireSupportedEvent(item.Path + ".reviewState",
                    eventType == "pull_request_review",
                    "Review-state predicates are supported only for github.pull_request_review events.", issues);
                if (!Enum.IsDefined(reviewState.State))
                    Add(issues, WorkflowTriggerPredicateIssueCode.InvalidEnumValue,
                        item.Path + ".reviewState.state", "Review state is not supported.");
            }
            else if (predicate.Ref is { } refPredicate)
            {
                RequireSupportedEvent(item.Path + ".ref",
                    eventType == "push",
                    "Ref predicates are supported only for github.push events.", issues);
                RequireText(refPredicate.Branch, item.Path + ".ref.branch", issues);
                if (!Enum.IsDefined(refPredicate.MatchMode))
                    Add(issues, WorkflowTriggerPredicateIssueCode.InvalidEnumValue,
                        item.Path + ".ref.matchMode", "Ref match mode must be equals or prefix.");
            }
            else if (predicate.Category is { } category)
            {
                RequireSupportedEvent(item.Path + ".category",
                    eventType == "discussion",
                    "Category predicates are supported only for github.discussion events.", issues);
                RequireText(category.Name, item.Path + ".category.name", issues);
            }
            else if (predicate.CommentMatches is { } commentMatches)
            {
                RequireSupportedEvent(item.Path + ".commentMatches",
                    eventType == "issue_comment",
                    "Comment predicates are supported only for github.issue_comment events.", issues);
                if (!WorkflowTriggerRegexPolicy.TryValidatePattern(commentMatches.Pattern, out var regexError))
                    Add(issues, WorkflowTriggerPredicateIssueCode.InvalidRegex,
                        item.Path + ".commentMatches.pattern", regexError ?? "Regular expression is invalid.");
            }
            else if (predicate.Or is { } alternatives)
            {
                if (alternatives.Count == 0)
                {
                    Add(issues, WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
                        item.Path + ".or", "An or predicate must contain at least one child.");
                    continue;
                }

                if (alternatives.Count > MaximumPredicateCount ||
                    discoveredCount > MaximumPredicateCount - alternatives.Count)
                {
                    Add(issues, WorkflowTriggerPredicateIssueCode.PredicateLimitExceeded,
                        item.Path + ".or", $"At most {MaximumPredicateCount} predicates are supported.");
                    continue;
                }

                discoveredCount += alternatives.Count;
                for (var index = alternatives.Count - 1; index >= 0; index--)
                    pending.Push((alternatives[index], $"{item.Path}.or[{index}]", item.Depth + 1));
            }
            else if (predicate.Not is { } negated)
            {
                if (discoveredCount == MaximumPredicateCount)
                {
                    Add(issues, WorkflowTriggerPredicateIssueCode.PredicateLimitExceeded,
                        item.Path + ".not", $"At most {MaximumPredicateCount} predicates are supported.");
                    continue;
                }

                discoveredCount++;
                pending.Push((negated, item.Path + ".not", item.Depth + 1));
            }
        }

        return new WorkflowTriggerPredicateValidationResult(issues.ToImmutable());
    }

    public static WorkflowTriggerPredicateEvaluation EvaluateAll(
        IReadOnlyList<WorkflowTriggerPredicate>? predicates,
        WorkflowTriggerEventContext? context)
    {
        var validation = ValidateAll(predicates, context?.EventName);
        if (!validation.IsValid)
            return new WorkflowTriggerPredicateEvaluation(false, validation.Issues);

        if (predicates!.Count == 0)
            return new WorkflowTriggerPredicateEvaluation(true, []);
        if (context is null)
            return new WorkflowTriggerPredicateEvaluation(false, []);

        var runtimeIssues = ImmutableArray.CreateBuilder<WorkflowTriggerPredicateIssue>();
        var matches = true;
        for (var index = 0; index < predicates.Count; index++)
        {
            var predicateMatches = Evaluate(
                predicates[index],
                context,
                $"predicates[{index}]",
                runtimeIssues);
            matches = predicateMatches && matches;
        }

        if (runtimeIssues.Count > 0)
            return new WorkflowTriggerPredicateEvaluation(false, runtimeIssues.ToImmutable());

        return new WorkflowTriggerPredicateEvaluation(matches, []);
    }

    private static bool Evaluate(
        WorkflowTriggerPredicate predicate,
        WorkflowTriggerEventContext context,
        string path,
        ImmutableArray<WorkflowTriggerPredicateIssue>.Builder issues)
    {
        if (predicate.HasLabel is { } hasLabel)
            return GetEventType(context.EventName) is "issues" or "pull_request" &&
                   (context.Labels ?? []).Any(
                       label => string.Equals(label, hasLabel.Label, StringComparison.OrdinalIgnoreCase));

        if (predicate.IsNotLabeledWith is { } isNotLabeledWith)
            return GetEventType(context.EventName) is "issues" or "pull_request" &&
                   (context.Labels ?? []).All(
                       label => !string.Equals(label, isNotLabeledWith.Label, StringComparison.OrdinalIgnoreCase));

        if (predicate.BaseBranch is { } baseBranch)
            return GetEventType(context.EventName) == "pull_request" &&
                   string.Equals(context.PullRequestBaseBranch, baseBranch.Branch, StringComparison.Ordinal);

        if (predicate.ReviewState is { } reviewState)
            return GetEventType(context.EventName) == "pull_request_review" &&
                   string.Equals(context.ReviewState, ReviewStateValue(reviewState.State),
                       StringComparison.OrdinalIgnoreCase);

        if (predicate.Ref is { } refPredicate)
        {
            if (GetEventType(context.EventName) != "push" || string.IsNullOrWhiteSpace(context.Ref))
                return false;

            return refPredicate.MatchMode switch
            {
                WorkflowTriggerMatchMode.Equals =>
                    string.Equals(context.Ref, refPredicate.Branch, StringComparison.Ordinal),
                WorkflowTriggerMatchMode.Prefix =>
                    context.Ref.StartsWith(refPredicate.Branch, StringComparison.Ordinal),
                _ => false
            };
        }

        if (predicate.Category is { } category)
            return GetEventType(context.EventName) == "discussion" &&
                   string.Equals(context.DiscussionCategory, category.Name,
                       StringComparison.OrdinalIgnoreCase);

        if (predicate.CommentMatches is { } commentMatches)
        {
            if (GetEventType(context.EventName) != "issue_comment" ||
                string.IsNullOrWhiteSpace(context.CommentBody))
                return false;

            if (!WorkflowTriggerRegexPolicy.TryMatch(
                    commentMatches.Pattern,
                    context.CommentBody,
                    out var matches,
                    out var error))
            {
                Add(issues, WorkflowTriggerPredicateIssueCode.InvalidRegex,
                    path + ".commentMatches.pattern", error ?? "Regular expression matching failed.");
                return false;
            }

            return matches;
        }

        if (predicate.Or is { Count: > 0 } alternatives)
        {
            var anyMatch = false;
            for (var index = 0; index < alternatives.Count; index++)
                anyMatch = Evaluate(alternatives[index], context, $"{path}.or[{index}]", issues) || anyMatch;
            return anyMatch;
        }

        if (predicate.Not is { } negated)
            return !Evaluate(negated, context, path + ".not", issues);

        return false;
    }

    private static void RequireSupportedEvent(
        string path,
        bool supported,
        string message,
        ImmutableArray<WorkflowTriggerPredicateIssue>.Builder issues)
    {
        if (!supported)
            Add(issues, WorkflowTriggerPredicateIssueCode.UnsupportedPredicateForEvent, path, message);
    }

    private static void RequireText(
        string? value,
        string path,
        ImmutableArray<WorkflowTriggerPredicateIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            Add(issues, WorkflowTriggerPredicateIssueCode.MissingValue,
                path, "A non-empty value is required.");
    }

    private static bool TryGetGitHubEventType(string? eventName, out string eventType)
    {
        const string prefix = "github.";
        if (eventName is null || !eventName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
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

    private static string GetEventType(string eventName) =>
        TryGetGitHubEventType(eventName, out var eventType) ? eventType : string.Empty;

    private static string ReviewStateValue(WorkflowTriggerReviewState state) => state switch
    {
        WorkflowTriggerReviewState.Approved => "approved",
        WorkflowTriggerReviewState.ChangesRequested => "changes_requested",
        WorkflowTriggerReviewState.Commented => "commented",
        _ => string.Empty
    };

    private static void Add(
        ImmutableArray<WorkflowTriggerPredicateIssue>.Builder issues,
        WorkflowTriggerPredicateIssueCode code,
        string path,
        string message) =>
        issues.Add(new WorkflowTriggerPredicateIssue(code, path, message));
}
