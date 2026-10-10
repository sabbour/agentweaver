using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class WorkflowTriggerPredicateEvaluatorTests
{
    [Fact]
    public void EmptyPredicateListMatchesWithoutAnEventPayload()
    {
        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll(
            [],
            context: null);

        Assert.True(result.IsValid);
        Assert.True(result.Matches);
    }

    [Fact]
    public void LabelPredicatesMatchCaseInsensitivelyAndAllTopLevelPredicatesAreAnded()
    {
        var predicates = new[]
        {
            new WorkflowTriggerPredicate
            {
                HasLabel = new WorkflowTriggerLabelPredicate { Label = "roadmap" }
            },
            new WorkflowTriggerPredicate
            {
                IsNotLabeledWith = new WorkflowTriggerLabelPredicate { Label = "blocked" }
            }
        };
        var context = new WorkflowTriggerEventContext
        {
            EventName = "github.issues.labeled",
            Labels = ["RoadMap", "priority"]
        };

        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll(predicates, context);

        Assert.True(result.IsValid);
        Assert.True(result.Matches);
    }

    [Fact]
    public void BooleanCompositionSupportsOrAndNot()
    {
        var predicate = new WorkflowTriggerPredicate
        {
            Or =
            [
                new WorkflowTriggerPredicate
                {
                    BaseBranch = new WorkflowTriggerBaseBranchPredicate { Branch = "main" }
                },
                new WorkflowTriggerPredicate
                {
                    Not = new WorkflowTriggerPredicate
                    {
                        BaseBranch = new WorkflowTriggerBaseBranchPredicate { Branch = "release" }
                    }
                }
            ]
        };
        var context = new WorkflowTriggerEventContext
        {
            EventName = "github.pull_request.opened",
            PullRequestBaseBranch = "develop"
        };

        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll([predicate], context);

        Assert.True(result.IsValid);
        Assert.True(result.Matches);
    }

    [Theory]
    [InlineData("github.pull_request_review.submitted", "APPROVED", WorkflowTriggerReviewState.Approved, true)]
    [InlineData("github.pull_request_review.submitted", "COMMENTED", WorkflowTriggerReviewState.Commented, true)]
    [InlineData("github.pull_request_review.submitted", "changes_requested", WorkflowTriggerReviewState.ChangesRequested, true)]
    [InlineData("github.pull_request.opened", "approved", WorkflowTriggerReviewState.Approved, false)]
    public void ReviewStateIsEventBoundAndCaseInsensitive(
        string eventName,
        string actualState,
        WorkflowTriggerReviewState expectedState,
        bool expectedMatch)
    {
        var predicate = new WorkflowTriggerPredicate
        {
            ReviewState = new WorkflowTriggerReviewStatePredicate { State = expectedState }
        };
        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll(
            [predicate],
            new WorkflowTriggerEventContext { EventName = eventName, ReviewState = actualState });

        Assert.Equal(expectedMatch, result.IsValid && result.Matches);
    }

    [Theory]
    [InlineData(WorkflowTriggerMatchMode.Equals, "refs/heads/main", true)]
    [InlineData(WorkflowTriggerMatchMode.Equals, "refs/heads/main/extra", false)]
    [InlineData(WorkflowTriggerMatchMode.Prefix, "refs/heads/main/extra", true)]
    [InlineData(WorkflowTriggerMatchMode.Prefix, "refs/heads/Main", false)]
    public void RefMatchModesUseOrdinalMatching(
        WorkflowTriggerMatchMode matchMode,
        string actualRef,
        bool expectedMatch)
    {
        var predicate = new WorkflowTriggerPredicate
        {
            Ref = new WorkflowTriggerRefPredicate
            {
                Branch = "refs/heads/main",
                MatchMode = matchMode
            }
        };
        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll(
            [predicate],
            new WorkflowTriggerEventContext { EventName = "github.push", Ref = actualRef });

        Assert.True(result.IsValid);
        Assert.Equal(expectedMatch, result.Matches);
    }

    [Fact]
    public void DiscussionCategoryMatchesCaseInsensitively()
    {
        var predicate = new WorkflowTriggerPredicate
        {
            Category = new WorkflowTriggerCategoryPredicate { Name = "Announcements" }
        };
        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll(
            [predicate],
            new WorkflowTriggerEventContext
            {
                EventName = "github.discussion.created",
                DiscussionCategory = "announcements"
            });

        Assert.True(result.IsValid);
        Assert.True(result.Matches);
    }

    [Fact]
    public void CommentRegexIsLimitedToIssueCommentEvents()
    {
        var predicate = new WorkflowTriggerPredicate
        {
            CommentMatches = new WorkflowTriggerCommentMatchesPredicate { Pattern = "^please review$" }
        };
        var comment = new WorkflowTriggerEventContext
        {
            EventName = "github.issue_comment.created",
            CommentBody = "please review"
        };
        var push = comment with { EventName = "github.push", Ref = "refs/heads/main" };

        Assert.True(WorkflowTriggerPredicateEvaluator.EvaluateAll([predicate], comment).Matches);
        var unsupported = WorkflowTriggerPredicateEvaluator.EvaluateAll([predicate], push);
        Assert.False(unsupported.IsValid);
        Assert.False(unsupported.Matches);
    }

    [Fact]
    public void RejectsMalformedPredicateShapesAndUnsupportedEventNames()
    {
        var noKind = new WorkflowTriggerPredicate();
        var multipleKinds = new WorkflowTriggerPredicate
        {
            HasLabel = new WorkflowTriggerLabelPredicate { Label = "bug" },
            BaseBranch = new WorkflowTriggerBaseBranchPredicate { Branch = "main" }
        };

        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
            WorkflowTriggerPredicateEvaluator.ValidateAll([noKind], "github.issues.opened")
                .Issues.Select(issue => issue.Code));
        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
            WorkflowTriggerPredicateEvaluator.ValidateAll([multipleKinds], "github.issues.opened")
                .Issues.Select(issue => issue.Code));
        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.InvalidEventName,
            WorkflowTriggerPredicateEvaluator.ValidateAll(
                [Label("bug")],
                "issues.opened").Issues.Select(issue => issue.Code));
    }

    [Fact]
    public void RejectsMissingValuesEmptyOrInvalidEnumsAndExcessiveComposition()
    {
        var missingValue = Label(" ");
        var emptyOr = new WorkflowTriggerPredicate { Or = [] };
        var invalidEnum = new WorkflowTriggerPredicate
        {
            Ref = new WorkflowTriggerRefPredicate
            {
                Branch = "refs/heads/main",
                MatchMode = (WorkflowTriggerMatchMode)19
            }
        };
        var tooDeep = Label("target");
        for (var depth = 0; depth < WorkflowTriggerPredicateEvaluator.MaximumPredicateDepth; depth++)
            tooDeep = new WorkflowTriggerPredicate { Not = tooDeep };

        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.MissingValue,
            WorkflowTriggerPredicateEvaluator.ValidateAll([missingValue], "github.issues.opened")
                .Issues.Select(issue => issue.Code));
        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.InvalidPredicateShape,
            WorkflowTriggerPredicateEvaluator.ValidateAll([emptyOr], "github.issues.opened")
                .Issues.Select(issue => issue.Code));
        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.InvalidEnumValue,
            WorkflowTriggerPredicateEvaluator.ValidateAll([invalidEnum], "github.push")
                .Issues.Select(issue => issue.Code));
        Assert.Contains(
            WorkflowTriggerPredicateIssueCode.PredicateLimitExceeded,
            WorkflowTriggerPredicateEvaluator.ValidateAll([tooDeep], "github.issues.opened")
                .Issues.Select(issue => issue.Code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("^(a+)+$")]
    [InlineData("(a)\\1")]
    [InlineData("(?=a)a")]
    [InlineData("\\Gfoo")]
    [InlineData("[abc")]
    public void RegexPolicyRejectsEmptyOrUnsafePatterns(string pattern)
    {
        Assert.False(WorkflowTriggerRegexPolicy.TryValidatePattern(pattern, out _));
    }

    [Fact]
    public void RegexPolicyAcceptsSafePatternWithBoundedMatching()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), WorkflowTriggerRegexPolicy.MatchTimeout);
        Assert.True(WorkflowTriggerRegexPolicy.TryValidatePattern(
            "^(?:bug|feature)-[0-9]{1,4}$",
            out _));
        Assert.True(WorkflowTriggerRegexPolicy.IsMatch(
            "^(?:bug|feature)-[0-9]{1,4}$",
            "feature-42"));
        Assert.False(WorkflowTriggerRegexPolicy.IsMatch(
            "^(?:bug|feature)-[0-9]{1,4}$",
            "feature-12345"));
    }

    [Fact]
    public void RegexMatchingInsideNotDistinguishesCompletionFromFailure()
    {
        var predicate = new WorkflowTriggerPredicate
        {
            Not = new WorkflowTriggerPredicate
            {
                CommentMatches = new WorkflowTriggerCommentMatchesPredicate { Pattern = "^a*b$" }
            }
        };
        var result = WorkflowTriggerPredicateEvaluator.EvaluateAll(
            [predicate],
            new WorkflowTriggerEventContext
            {
                EventName = "github.issue_comment.created",
                CommentBody = new string('a', 1_000_000)
            });

        if (result.IsValid)
        {
            Assert.True(result.Matches);
            Assert.Empty(result.Issues);
        }
        else
        {
            Assert.False(result.Matches);
            Assert.Contains(result.Issues, issue =>
                issue.Code == WorkflowTriggerPredicateIssueCode.InvalidRegex);
        }
    }

    private static WorkflowTriggerPredicate Label(string label) => new()
    {
        HasLabel = new WorkflowTriggerLabelPredicate { Label = label }
    };
}
