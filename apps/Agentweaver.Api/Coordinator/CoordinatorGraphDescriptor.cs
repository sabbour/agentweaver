using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Builds the UNIFIED coordinator view as a <see cref="GraphDescriptor"/> (variant
/// <c>"coordinator"</c>) so the frontend's generic renderer draws the coordinator, its fan-out
/// subtask children, and the PLANNED Phase 3 collective-assembly stage with the SAME contract used
/// for per-run graphs. Built from domain state (the work plan), never reflection — consistent with
/// the per-run drift-guard philosophy.
///
/// Shape-only: runtime status is deliberately NOT baked into the descriptor (just like the per-run
/// <see cref="GraphDescriptor"/>); status is projected separately via the <c>subtask.*</c> /
/// <c>coordinator.topology</c> event streams. The subtask nodes carry their rich display metadata
/// (agent, model, phase, isolation, child_run_id) on the optional <see cref="GraphNode"/> fields,
/// and a <c>child_graph_ref</c> of <c>run:{childRunId}</c> once a child run has been dispatched so
/// the frontend can fetch/expand the child's own graph via <c>GET /api/runs/{childRunId}/graph</c>.
/// </summary>
public static class CoordinatorGraphDescriptor
{
    public const string Variant = "coordinator";
    public const string CoordinatorNodeId = "coordinator";

    // Planned collective-assembly node ids (Phase 3, not yet built — kind == "planned").
    public const string AssemblyRaiNodeId = "planned:assembly-rai";
    public const string AssemblyReviewNodeId = "planned:assembly-review";
    public const string AssemblyMergeNodeId = "planned:assembly-merge";
    public const string AssemblyScribeNodeId = "planned:assembly-scribe";

    public static string SubtaskNodeId(int subtaskId) => $"plan:subtask-{subtaskId}";

    public static string GraphId(string coordinatorRunId) => $"coordinator:{coordinatorRunId}";

    /// <summary>Builds the descriptor from EF <see cref="Subtask"/> rows + dependency edges.</summary>
    public static GraphDescriptor Build(
        string coordinatorRunId,
        IReadOnlyList<Subtask> subtasks,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> dependencies,
        string? assemblyStage = null)
    {
        var projected = subtasks
            .Select(s => new SubtaskNode(
                s.Id, s.Title, s.AssignedAgent, s.SelectedModelId, s.Phase, s.IsolationStrategy, s.ChildRunId))
            .ToList();
        return BuildCore(coordinatorRunId, projected, dependencies, assemblyStage);
    }

    /// <summary>Builds the descriptor from the <see cref="CoordinatorWorkPlanView"/> projection.</summary>
    public static GraphDescriptor Build(CoordinatorWorkPlanView plan)
    {
        var projected = plan.Subtasks
            .Select(s => new SubtaskNode(
                s.SubtaskId, s.Title, s.AssignedAgent, s.SelectedModelId, s.Phase, s.Isolation, s.ChildRunId))
            .ToList();
        var deps = plan.Dependencies
            .Select(d => (d.SubtaskId, d.DependsOnSubtaskId))
            .ToList();
        return BuildCore(plan.CoordinatorRunId, projected, deps, plan.AssemblyStage);
    }

    private static GraphDescriptor BuildCore(
        string coordinatorRunId,
        IReadOnlyList<SubtaskNode> subtasks,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> dependencies,
        string? assemblyStage)
    {
        var nodes = new List<GraphNode>(subtasks.Count + 5)
        {
            new(CoordinatorNodeId, "Coordinator", "coordinator", "live", "agent", ChildGraphRef: null),
        };

        foreach (var s in subtasks)
        {
            var childGraphRef = string.IsNullOrEmpty(s.ChildRunId) ? null : $"run:{s.ChildRunId}";
            var label = string.IsNullOrWhiteSpace(s.Title) ? s.Agent : s.Title;
            nodes.Add(new GraphNode(
                Id: SubtaskNodeId(s.Id),
                Label: label,
                Role: "subtask",
                Kind: "live",
                NodeType: "subtask",
                ChildGraphRef: childGraphRef,
                Agent: s.Agent,
                Model: s.Model,
                Phase: s.Phase,
                Isolation: s.Isolation,
                ChildRunId: s.ChildRunId));
        }

        // Collective-assembly stage (Phase 3). Each node flips planned -> live once its stage has
        // started, computed from the persisted work-plan AssemblyStage (sticky/forward-only): a node
        // renders "live" when its stage ordinal is <= the current stage ordinal, else "planned".
        var stageOrd = AssemblyStage.Ordinal(assemblyStage);
        string Kind(int nodeStageOrd) => stageOrd >= nodeStageOrd ? "live" : "planned";
        nodes.Add(new GraphNode(AssemblyRaiNodeId, "RAI", "rai", Kind(AssemblyStage.Ordinal(AssemblyStage.Rai)), "agent", ChildGraphRef: null));
        nodes.Add(new GraphNode(AssemblyReviewNodeId, "Human Review", "review", Kind(AssemblyStage.Ordinal(AssemblyStage.Review)), "gate", ChildGraphRef: null));
        nodes.Add(new GraphNode(AssemblyMergeNodeId, "Merge", "merge", Kind(AssemblyStage.Ordinal(AssemblyStage.Merge)), "action", ChildGraphRef: null));
        nodes.Add(new GraphNode(AssemblyScribeNodeId, "Scribe", "scribe", Kind(AssemblyStage.Ordinal(AssemblyStage.Scribe)), "agent", ChildGraphRef: null));

        // ── Edges ────────────────────────────────────────────────────────────
        var subtaskIds = subtasks.Select(s => s.Id).ToHashSet();
        // dependsOn -> dependent (a dependency must finish before its dependent).
        var depEdges = dependencies
            .Where(d => subtaskIds.Contains(d.SubtaskId) && subtaskIds.Contains(d.DependsOnSubtaskId))
            .Select(d => (From: d.DependsOnSubtaskId, To: d.SubtaskId))
            .ToList();

        var hasIncoming = depEdges.Select(e => e.To).ToHashSet();   // dependents (have a prerequisite)
        var hasOutgoing = depEdges.Select(e => e.From).ToHashSet(); // prerequisites (something depends on them)

        var edges = new List<(string From, string To)>();

        // coordinator -> roots (subtasks with no dependency). Isolated subtasks are roots too.
        foreach (var s in subtasks)
            if (!hasIncoming.Contains(s.Id))
                edges.Add((CoordinatorNodeId, SubtaskNodeId(s.Id)));

        // dependency edges between subtasks.
        foreach (var (from, to) in depEdges)
            edges.Add((SubtaskNodeId(from), SubtaskNodeId(to)));

        // terminal subtasks (leaves: nothing depends on them) -> planned assembly RAI.
        foreach (var s in subtasks)
            if (!hasOutgoing.Contains(s.Id))
                edges.Add((SubtaskNodeId(s.Id), AssemblyRaiNodeId));

        // planned assembly chain.
        edges.Add((AssemblyRaiNodeId, AssemblyReviewNodeId));
        edges.Add((AssemblyReviewNodeId, AssemblyMergeNodeId));
        edges.Add((AssemblyMergeNodeId, AssemblyScribeNodeId));

        // Cardinality by forward degree (the coordinator graph is a DAG; loopback always false).
        var outDeg = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Count());
        var inDeg = edges.GroupBy(e => e.To).ToDictionary(g => g.Key, g => g.Count());

        string Cardinality((string From, string To) e)
        {
            if (outDeg.GetValueOrDefault(e.From, 0) > 1) return "fanout";
            if (inDeg.GetValueOrDefault(e.To, 0) > 1) return "fanin";
            return "direct";
        }

        var edgeArr = edges
            .Select(e => new GraphEdge(e.From, e.To, Cardinality(e), Loopback: false))
            .ToArray();

        return new GraphDescriptor(
            GraphId(coordinatorRunId), Variant, CoordinatorNodeId, nodes.ToArray(), edgeArr);
    }

    private readonly record struct SubtaskNode(
        int Id, string Title, string Agent, string Model, string Phase, string Isolation, string? ChildRunId);
}
