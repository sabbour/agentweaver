namespace Agentweaver.Api.Workflows;

/// <summary>
/// Thrown when the <see cref="RunWorkflowGraphBinder"/> cannot bind a workflow node or edge onto the
/// live MAF executor graph (Feature 015, US1). The binder FAILS CLOSED: rather than silently skipping,
/// mis-wiring, or partially executing a graph, an unbindable node aborts the whole build with a
/// node-scoped message naming the offending node and the reason. This is the drift/governance guard —
/// a definition that diverges from the executor wiring fails loudly at build time, never at first run.
/// </summary>
public sealed class WorkflowBindException : Exception
{
    /// <summary>The id of the node the binder could not resolve/wire, when known.</summary>
    public string? NodeId { get; }

    public WorkflowBindException(string message, string? nodeId = null)
        : base(message)
    {
        NodeId = nodeId;
    }

    public WorkflowBindException(string message, Exception innerException, string? nodeId = null)
        : base(message, innerException)
    {
        NodeId = nodeId;
    }
}
