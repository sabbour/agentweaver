namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Self-describing render metadata carried by a workflow executor so the per-run graph
/// descriptor can be BUILT FROM THE SAME CODE that wires the MAF workflow (no runtime
/// reflection). The descriptor builder reads this off each executor instance as edges are
/// added; reflection over the built workflow is used ONLY by the build-time drift-guard test.
/// </summary>
/// <remarks>
/// This interface lives in <c>Scaffolder.AgentRuntime</c> (not the API project) because the
/// business executors that implement it — <see cref="AgentTurnExecutor"/>,
/// <see cref="RaiTurnExecutor"/>, <see cref="MergeExecutor"/>, <see cref="ScribeTurnExecutor"/>
/// — live here, and the dependency direction is api → agentruntime. The API-side plumbing
/// adapters implement it via <c>VisualFunctionExecutor</c>.
/// </remarks>
public interface IWorkflowNodeMeta
{
    /// <summary>
    /// Logical node id used to COLLAPSE multiple executors into one rendered node. For the four
    /// business executors this EXACTLY equals the status-event step key they already emit
    /// (agent, rai, merge, scribe), so the descriptor node lines up with the live status stream.
    /// </summary>
    string LogicalNodeId { get; }

    /// <summary>Human-facing label for the rendered node (e.g. "Agent", "Human Review").</summary>
    string DisplayLabel { get; }

    /// <summary>Coarse role used for styling/grouping (e.g. "agent", "review", "merge").</summary>
    string Role { get; }

    /// <summary>
    /// Self-declared node category that drives the frontend's rendered shape/size. One of
    /// <c>agent</c> (an AI agent turn), <c>action</c> (a deterministic system op),
    /// <c>gate</c> (a human-in-the-loop decision/approval), <c>terminal</c> (a workflow
    /// endpoint/checkpoint), or <c>subtask</c> (a coordinator fan-out child reference).
    /// REQUIRED on every emitted descriptor node. Hidden plumbing still declares one (it is
    /// dropped from the descriptor regardless).
    /// </summary>
    string NodeType { get; }

    /// <summary>
    /// When true, the executor is plumbing (adapter/storer/terminal) that emits no status and is
    /// DROPPED from the descriptor; its edges are transitively re-stitched through it.
    /// </summary>
    bool Hidden { get; }

    /// <summary>
    /// "live" for all real executors wired into the running workflow. "planned" is reserved for
    /// the coordinator composer's not-yet-dispatched nodes and is never produced here.
    /// </summary>
    string NodeKind { get; }
}
