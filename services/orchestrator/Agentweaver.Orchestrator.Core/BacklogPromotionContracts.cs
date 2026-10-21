using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Orchestrator.Core;

/// <summary>
/// Records where a promoted backlog task came from: the confirmed work plan item and the coordinator run.
/// </summary>
/// <remarks>Tasks that are captured directly have no origin record.</remarks>
public sealed record BacklogTaskOrigin(
    BacklogTaskReference Task,
    Guid PromotionId,
    string RunId,
    string RootSessionId,
    string WorkPlanId,
    string WorkItemId);

/// <summary>Maps one work plan item to the backlog task created for it.</summary>
public sealed record BacklogPromotedTask(
    string TaskId,
    string WorkItemId);

/// <summary>
/// Durable record of one promotion of a confirmed coordinator work plan into backlog tasks.
/// </summary>
/// <remarks>
/// A plan is promoted at most once per run. <see cref="GraphRevision"/> is the project graph revision
/// after the promotion committed, and <see cref="DecisionStateVersion"/> is the decision version that held the confirmed plan.
/// </remarks>
public sealed record BacklogPlanPromotion(
    Guid PromotionId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string WorkPlanId,
    string WorkflowId,
    string WorkflowRevision,
    long DecisionStateVersion,
    long ExecutionFence,
    long GraphRevision,
    DateTimeOffset PromotedAt,
    ImmutableArray<BacklogPromotedTask> Tasks);

/// <summary>Deterministic identifiers for tasks created from work plan items.</summary>
public static class BacklogPlanPromotionIds
{
    /// <summary>
    /// Returns the backlog task ID for one work plan item in one run.
    /// </summary>
    /// <remarks>The ID is <c>plan-</c> followed by 32 lowercase hexadecimal characters of a SHA-256 hash over the length-prefixed values, so a replay creates the same task ID.</remarks>
    public static string CreateTaskId(string projectId, string runId, string workPlanId, string workItemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workPlanId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItemId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(
            new[] { projectId, runId, workPlanId, workItemId }.Select(value => $"{value.Length}:{value}"))));
        return "plan-" + Convert.ToHexStringLower(hash)[..32];
    }
}
