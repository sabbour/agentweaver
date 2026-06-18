namespace Agentweaver.Domain;

public sealed class WorkspaceUnavailableException : Exception
{
    public WorkspaceUnavailableException(string message) : base(message) { }
    public WorkspaceUnavailableException(string message, Exception inner) : base(message, inner) { }
}
