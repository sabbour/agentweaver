using System.Collections.Immutable;

namespace Agentweaver.Orchestrator.Core;

public static class BacklogReadinessEvaluator
{
    public static BacklogCoreResult<BacklogReadinessEvaluation> Evaluate(
        BacklogDependencyGraph? graph,
        BacklogTaskReference? taskReference,
        long expectedGraphRevision,
        long expectedTaskRevision,
        ImmutableArray<BacklogPrerequisiteExecutionSnapshot> prerequisites)
    {
        var issues = ImmutableArray.CreateBuilder<BacklogIssue>();
        if (graph is null)
        {
            Add(issues, BacklogIssueCode.InvalidTaskReference, "graph",
                "A current dependency graph is required to evaluate readiness.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (!BacklogDependencyGraph.IsValidReference(taskReference))
        {
            Add(issues, BacklogIssueCode.InvalidTaskReference, "task",
                "Task reference must have stable project and task identifiers.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (expectedGraphRevision < 0 || expectedTaskRevision < 0)
        {
            Add(issues, BacklogIssueCode.InvalidTaskRevision, "expectedRevision",
                "Expected graph and task revisions must be zero or greater.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (!string.Equals(taskReference!.ProjectId, graph.ProjectId, StringComparison.Ordinal))
        {
            Add(issues, BacklogIssueCode.CrossProjectDependency, "task.projectId",
                "Readiness can be evaluated only for a task in the graph project.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (expectedGraphRevision != graph.Revision)
        {
            Add(issues, BacklogIssueCode.StaleGraphRevision, "expectedGraphRevision",
                $"Expected graph revision {expectedGraphRevision} does not match current revision {graph.Revision}.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (!graph.TryGetTask(taskReference.TaskId, out var task))
        {
            Add(issues, BacklogIssueCode.MissingTask, "task.taskId",
                "The task does not exist in the current graph.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (expectedTaskRevision != task!.Revision)
        {
            Add(issues, BacklogIssueCode.StaleTaskRevision, "expectedTaskRevision",
                $"Expected task revision {expectedTaskRevision} does not match current revision {task.Revision}.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }
        if (prerequisites.IsDefault)
        {
            Add(issues, BacklogIssueCode.InvalidPrerequisiteCollection, "prerequisites",
                "Prerequisite execution snapshots must be initialized.");
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());
        }

        var prerequisitesById = new Dictionary<string, BacklogPrerequisiteExecutionSnapshot>(
            StringComparer.Ordinal);
        for (var index = 0; index < prerequisites.Length; index++)
        {
            var prerequisite = prerequisites[index];
            var path = $"prerequisites[{index}]";
            if (prerequisite is null ||
                !BacklogDependencyGraph.IsValidReference(prerequisite.Prerequisite) ||
                prerequisite.TaskRevision < 0 ||
                prerequisite.ExecutionRevision < 0 ||
                !Enum.IsDefined(prerequisite.State) ||
                !Enum.IsDefined(prerequisite.OutputProofState) ||
                !HasValidOutputProofReference(prerequisite))
            {
                Add(issues, BacklogIssueCode.InvalidPrerequisiteSnapshot, path,
                    "Prerequisite state and revisions must be valid and any verified output proof must have a stable reference.");
                continue;
            }
            if (!string.Equals(prerequisite.Prerequisite.ProjectId, graph.ProjectId, StringComparison.Ordinal))
            {
                Add(issues, BacklogIssueCode.CrossProjectDependency, path + ".projectId",
                    "Prerequisite evidence must belong to the graph project.");
                continue;
            }
            if (!prerequisitesById.TryAdd(prerequisite.Prerequisite.TaskId, prerequisite))
                Add(issues, BacklogIssueCode.DuplicatePrerequisiteSnapshot, path,
                    $"Prerequisite '{prerequisite.Prerequisite.TaskId}' has more than one execution snapshot.");
        }

        if (issues.Count > 0)
            return BacklogCoreResult<BacklogReadinessEvaluation>.Failure(issues.ToImmutable());

        var edges = graph.DependenciesFor(taskReference.TaskId);
        var requiredPrerequisiteIds = edges
            .Select(edge => edge.Prerequisite.TaskId)
            .ToHashSet(StringComparer.Ordinal);
        var blockers = ImmutableArray.CreateBuilder<BacklogIssue>();

        if (task.IsArchived || task.IsAutomationInvocationPending ||
            task.State == BacklogTaskState.Claimed)
            Add(blockers, BacklogIssueCode.TaskNotEligible, "task",
                "Only an unclaimed, unarchived task that is not held by an automation invocation can become ready or be claimed.");

        foreach (var extra in prerequisitesById.Keys.Except(requiredPrerequisiteIds, StringComparer.Ordinal))
            Add(blockers, BacklogIssueCode.UnexpectedPrerequisiteSnapshot, "prerequisites",
                $"Task '{extra}' is not a prerequisite of '{taskReference.TaskId}'.");

        foreach (var edge in edges)
        {
            var prerequisiteId = edge.Prerequisite.TaskId;
            if (!prerequisitesById.TryGetValue(prerequisiteId, out var prerequisite))
            {
                Add(blockers, BacklogIssueCode.MissingPrerequisiteSnapshot,
                    $"prerequisites[{prerequisiteId}]",
                    $"Current execution evidence for prerequisite '{prerequisiteId}' is required.");
                continue;
            }

            if (!graph.TryGetTask(prerequisiteId, out var prerequisiteTask) ||
                prerequisite.TaskRevision != prerequisiteTask!.Revision ||
                prerequisite.State == BacklogPrerequisiteExecutionState.Stale)
            {
                Add(blockers, BacklogIssueCode.StalePrerequisite,
                    $"prerequisites[{prerequisiteId}]",
                    $"Prerequisite '{prerequisiteId}' changed after its execution snapshot was produced.");
                continue;
            }

            if (prerequisiteTask!.IsArchived)
            {
                Add(blockers, BacklogIssueCode.PrerequisiteArchived,
                    $"prerequisites[{prerequisiteId}]",
                    $"Archived prerequisite '{prerequisiteId}' cannot satisfy readiness.");
                continue;
            }
            if (prerequisiteTask.IsAutomationInvocationPending)
            {
                Add(blockers, BacklogIssueCode.PrerequisiteUnavailable,
                    $"prerequisites[{prerequisiteId}]",
                    $"Provisional prerequisite '{prerequisiteId}' cannot satisfy readiness.");
                continue;
            }
            var prerequisiteIsCompleted = prerequisite.State is
                BacklogPrerequisiteExecutionState.Merged or BacklogPrerequisiteExecutionState.Completed;
            if (prerequisiteIsCompleted &&
                prerequisiteTask.State != BacklogTaskState.Claimed)
            {
                Add(blockers, BacklogIssueCode.StalePrerequisite,
                    $"prerequisites[{prerequisiteId}]",
                    $"Completed execution evidence for prerequisite '{prerequisiteId}' does not match its current task state.");
                continue;
            }

            if (!prerequisiteIsCompleted)
            {
                Add(blockers, BlockerFor(prerequisite.State),
                    $"prerequisites[{prerequisiteId}]",
                    $"Prerequisite '{prerequisiteId}' is not in a completed state.");
                continue;
            }

            switch (prerequisite.OutputProofState)
            {
                case BacklogOutputProofState.NotRequired
                    when prerequisite.State == BacklogPrerequisiteExecutionState.Completed:
                case BacklogOutputProofState.Verified:
                    break;
                case BacklogOutputProofState.NotRequired:
                    Add(blockers, BacklogIssueCode.OutputProofMissing,
                        $"prerequisites[{prerequisiteId}].outputProof",
                        $"Merged prerequisite '{prerequisiteId}' requires a current verified output witness.");
                    break;
                case BacklogOutputProofState.Missing:
                    Add(blockers, BacklogIssueCode.OutputProofMissing,
                        $"prerequisites[{prerequisiteId}].outputProof",
                        $"Prerequisite '{prerequisiteId}' requires verified output evidence.");
                    break;
                case BacklogOutputProofState.Indeterminate:
                    Add(blockers, BacklogIssueCode.OutputProofIndeterminate,
                        $"prerequisites[{prerequisiteId}].outputProof",
                        $"Output evidence for prerequisite '{prerequisiteId}' is indeterminate.");
                    break;
                case BacklogOutputProofState.Stale:
                    Add(blockers, BacklogIssueCode.OutputProofStale,
                        $"prerequisites[{prerequisiteId}].outputProof",
                        $"Output evidence for prerequisite '{prerequisiteId}' is stale.");
                    break;
            }
        }

        return BacklogCoreResult<BacklogReadinessEvaluation>.Success(
            new BacklogReadinessEvaluation(
                taskReference,
                graph.Revision,
                task.Revision,
                [.. prerequisitesById.Values.OrderBy(
                    prerequisite => prerequisite.Prerequisite.TaskId, StringComparer.Ordinal)],
                blockers.ToImmutable()));
    }

    private static bool HasValidOutputProofReference(
        BacklogPrerequisiteExecutionSnapshot prerequisite) =>
        prerequisite.OutputProofState switch
        {
            BacklogOutputProofState.Verified =>
                WorkflowValidationSupport.IsOpaqueReference(prerequisite.OutputProofReference),
            BacklogOutputProofState.NotRequired => prerequisite.OutputProofReference is null,
            _ => prerequisite.OutputProofReference is null
        };

    private static BacklogIssueCode BlockerFor(BacklogPrerequisiteExecutionState state) =>
        state switch
        {
            BacklogPrerequisiteExecutionState.Missing => BacklogIssueCode.PrerequisiteMissing,
            BacklogPrerequisiteExecutionState.Pending => BacklogIssueCode.PrerequisiteNotMerged,
            BacklogPrerequisiteExecutionState.Failed => BacklogIssueCode.PrerequisiteFailed,
            BacklogPrerequisiteExecutionState.Archived => BacklogIssueCode.PrerequisiteArchived,
            BacklogPrerequisiteExecutionState.Cancelled => BacklogIssueCode.PrerequisiteCancelled,
            BacklogPrerequisiteExecutionState.Delegated => BacklogIssueCode.PrerequisiteDelegated,
            BacklogPrerequisiteExecutionState.Indeterminate => BacklogIssueCode.PrerequisiteIndeterminate,
            BacklogPrerequisiteExecutionState.Stale => BacklogIssueCode.StalePrerequisite,
            BacklogPrerequisiteExecutionState.Merged => BacklogIssueCode.PrerequisiteNotMerged,
            _ => BacklogIssueCode.PrerequisiteIndeterminate
        };

    private static void Add(
        ImmutableArray<BacklogIssue>.Builder issues,
        BacklogIssueCode code,
        string path,
        string message) =>
        issues.Add(new BacklogIssue(code, path, message));
}
