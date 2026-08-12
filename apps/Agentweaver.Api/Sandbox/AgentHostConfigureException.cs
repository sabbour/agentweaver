namespace Agentweaver.Api.Sandbox;

/// <summary>Typed failure returned by AgentHost's one-time <c>POST /configure</c>.</summary>
internal sealed class AgentHostConfigureException : Exception
{
    public AgentHostConfigureException(
        string reason,
        string message,
        int statusCode,
        bool retryable = false,
        string? recoveryAction = null)
        : base(message)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "agenthost_configure_failed" : reason;
        StatusCode = statusCode;
        Retryable = retryable;
        RecoveryAction = recoveryAction;
    }

    public string Reason { get; }
    public int StatusCode { get; }
    public bool Retryable { get; }
    public string? RecoveryAction { get; }
}
