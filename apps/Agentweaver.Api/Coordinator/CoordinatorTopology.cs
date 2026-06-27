using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Versioned <c>coordinator.topology</c> payload shapes plus a builder. The Web topology view and
/// the MCP proxy render the graph THIN from these payloads with no client-side topology
/// computation: the snapshot carries every node and edge once, and each delta replaces the changed
/// node(s) by id. Edges never change after the snapshot.
///
/// Node id scheme: the coordinator is the single node <c>"coordinator"</c>; each subtask is
/// <c>"subtask-{id}"</c>. An edge <c>{ from, to }</c> means node <c>from</c> (a dependency) must
/// reach assemble_ready/completed before node <c>to</c> (its dependent) is dispatched.
/// </summary>
public static class CoordinatorTopology
{
    /// <summary>Bump when the payload shape changes incompatibly.</summary>
    public const int Version = 1;

    public const string CoordinatorNodeId = "coordinator";

    public static string SubtaskNodeId(int subtaskId) => $"subtask-{subtaskId}";

    /// <summary>
    /// Builds the FULL plan-time snapshot. <paramref name="seq"/> is the monotonic topology
    /// sequence number (0 for the snapshot, then incremented per delta).
    /// </summary>
    public static object BuildSnapshot(
        string coordinatorRunId,
        int workPlanId,
        string workPlanStatus,
        IReadOnlyList<Subtask> subtasks,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
        long seq,
        IPodNameRegistry? podRegistry = null)
    {
        var nodes = new List<object>(subtasks.Count + 1)
        {
            new
            {
                id = CoordinatorNodeId,
                kind = "coordinator",
                subtaskId = (int?)null,
                status = workPlanStatus,
                label = "Coordinator",
                agent = (string?)null,
                model = (string?)null,
                childRunId = (string?)null,
                phase = (string?)null,
                isolation = (string?)null,
                executionPodName = (string?)null,
            },
        };

        foreach (var s in subtasks)
        {
            nodes.Add(new
            {
                id = SubtaskNodeId(s.Id),
                kind = "subtask",
                subtaskId = (int?)s.Id,
                status = s.Status,
                label = s.Title,
                agent = (string?)s.AssignedAgent,
                model = (string?)s.SelectedModelId,
                childRunId = s.ChildRunId,
                phase = (string?)s.Phase,
                isolation = (string?)s.IsolationStrategy,
                executionPodName = string.IsNullOrEmpty(s.ChildRunId) ? null : podRegistry?.TryGet(s.ChildRunId),
            });
        }

        var edgeList = edges
            .Select(e => new { from = SubtaskNodeId(e.DependsOnSubtaskId), to = SubtaskNodeId(e.SubtaskId) })
            .ToList();

        return new
        {
            version = Version,
            kind = "snapshot",
            coordinatorRunId,
            workPlanId,
            workPlanStatus,
            seq,
            nodes,
            edges = edgeList,
            emittedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>
    /// Builds a DELTA carrying only the changed node(s). The coordinator node is included when the
    /// work-plan status itself transitioned (e.g. planned -&gt; dispatching).
    /// </summary>
    public static object BuildDelta(
        string coordinatorRunId,
        int workPlanId,
        string workPlanStatus,
        IReadOnlyList<object> changedNodes,
        long seq)
    {
        return new
        {
            version = Version,
            kind = "delta",
            coordinatorRunId,
            workPlanId,
            workPlanStatus,
            seq,
            changed = changedNodes,
            emittedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    /// <summary>Changed-node shape for a subtask delta (the coordinator applies it by id).</summary>
    public static object SubtaskNode(Subtask s, IPodNameRegistry? podRegistry = null) => new
    {
        id = SubtaskNodeId(s.Id),
        kind = "subtask",
        subtaskId = (int?)s.Id,
        status = s.Status,
        agent = (string?)s.AssignedAgent,
        model = (string?)s.SelectedModelId,
        childRunId = s.ChildRunId,
        executionPodName = string.IsNullOrEmpty(s.ChildRunId) ? null : podRegistry?.TryGet(s.ChildRunId),
    };

    /// <summary>Changed-node shape for the coordinator node when the work-plan status transitions.</summary>
    public static object CoordinatorNode(string workPlanStatus) => new
    {
        id = CoordinatorNodeId,
        kind = "coordinator",
        subtaskId = (int?)null,
        status = workPlanStatus,
        agent = (string?)null,
        model = (string?)null,
        childRunId = (string?)null,
        executionPodName = (string?)null,
    };
}
