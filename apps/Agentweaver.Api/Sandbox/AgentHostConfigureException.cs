namespace Agentweaver.Api.Sandbox;

/// <summary>Typed failure returned by AgentHost's one-time <c>POST /configure</c>.</summary>
internal sealed class AgentHostConfigureException : Exception
{
    public AgentHostConfigureException(string reason, string message, int statusCode)
        : base(message)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? "agenthost_configure_failed" : reason;
        StatusCode = statusCode;
    }

    public string Reason { get; }
    public int StatusCode { get; }
}
