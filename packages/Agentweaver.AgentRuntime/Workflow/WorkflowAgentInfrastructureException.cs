namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Indicates that a workflow turn could not run because the execution infrastructure failed
/// (for example A2A endpoint resolution or transport), not because the model requested changes.
/// </summary>
public sealed class WorkflowAgentInfrastructureException : Exception
{
    public string Reason { get; }
    public bool? IsRetryable { get; }

    public WorkflowAgentInfrastructureException(
        string reason,
        string message,
        Exception? innerException = null,
        bool? isRetryable = null)
        : base(message, innerException)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "agent_infrastructure_failure" : reason;
        IsRetryable = isRetryable;
    }
}
