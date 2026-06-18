using Microsoft.Agents.AI.Workflows;
using Scaffolder.AgentRuntime.Workflow;

namespace Scaffolder.Api.Runs.Graph;

/// <summary>
/// A <see cref="FunctionExecutor{TInput,TOutput}"/> that ALSO carries self-describing render
/// metadata (<see cref="IWorkflowNodeMeta"/>). Used to replace the inline
/// <c>new FunctionExecutor&lt;,&gt;(...)</c> plumbing adapters/storers/terminals in
/// <c>RunWorkflowFactory.BuildWorkflow</c> so every wired executor describes the node it
/// represents, letting the descriptor be recorded as the workflow is built (no runtime
/// reflection). Most of these are hidden plumbing collapsed/dropped from the descriptor; some
/// (e.g. the scribe-path adapters, the child assemble-ready terminal) carry a visible logical id.
/// </summary>
public sealed class VisualFunctionExecutor<TInput, TOutput> : FunctionExecutor<TInput, TOutput>, IWorkflowNodeMeta
{
    /// <inheritdoc />
    public string LogicalNodeId { get; }
    /// <inheritdoc />
    public string DisplayLabel { get; }
    /// <inheritdoc />
    public string Role { get; }
    /// <inheritdoc />
    public bool Hidden { get; }
    /// <inheritdoc />
    public string NodeKind { get; }

    public VisualFunctionExecutor(
        string id,
        string logicalNodeId,
        string displayLabel,
        string role,
        bool hidden,
        Func<TInput, IWorkflowContext, CancellationToken, ValueTask<TOutput>> handler,
        string nodeKind = "live")
        : base(id, handler)
    {
        LogicalNodeId = logicalNodeId;
        DisplayLabel = displayLabel;
        Role = role;
        Hidden = hidden;
        NodeKind = nodeKind;
    }
}
