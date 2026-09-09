namespace Agentweaver.AgentRuntime.Workflow;

using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;

/// <summary>
/// Indicates that a workflow turn could not run because the execution infrastructure failed
/// (for example A2A endpoint resolution or transport), not because the model requested changes.
/// </summary>
public sealed class WorkflowAgentInfrastructureException : Exception
{
    public string Reason { get; }
    public bool? IsRetryable { get; }
    public bool IsModelProviderChanged =>
        string.Equals(Reason, "model_provider_changed", StringComparison.Ordinal);

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

    public AgentProviderException ToModelProviderChanged(string? modelSource)
    {
        var source = modelSource switch
        {
            "byok" or "microsoft-foundry" => ModelSource.Byok,
            _ => ModelSource.GitHubCopilot,
        };
        return new AgentProviderException(
            source,
            AgentProviderFailureKind.Configuration,
            "model_provider_changed",
            Message,
            IsRetryable ?? true,
            this);
    }
}
