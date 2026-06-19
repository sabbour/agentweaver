namespace Agentweaver.Domain;

/// <summary>
/// Thrown by <see cref="IBacklogTaskStore"/> move/reorder operations when the order_key UNIQUE
/// constraint keeps colliding after the bounded retry budget is exhausted. Endpoints map this to
/// 409 order_conflict.
/// </summary>
public sealed class OrderKeyConflictException : Exception
{
    public OrderKeyConflictException(string message) : base(message) { }
}
