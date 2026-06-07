namespace Scaffolder.Domain;

/// <summary>
/// Integration seam to the agent loop implemented in Scaffolder.AgentRuntime.
/// Defined in the domain package so both the API (which invokes it) and the
/// runtime (which implements it) can reference it without a circular
/// dependency. Implementations run the agent loop and emit run events through
/// the supplied publisher and durable store, returning when the run reaches a
/// terminal state (completed, bounded, or failed).
/// </summary>
public interface IAgentRunner
{
    /// <summary>
    /// Executes the agent loop for a run. Publishes events via the publisher and
    /// persists them via the store. Returns when the loop reaches a terminal
    /// state (completed, bounded, failed).
    /// </summary>
    Task ExecuteAsync(Run run, IRunEventPublisher publisher, IRunEventStore store, CancellationToken ct);
}
