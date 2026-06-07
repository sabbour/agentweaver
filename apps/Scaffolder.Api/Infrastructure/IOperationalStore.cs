using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

/// <summary>
/// Compliance-facing operational record for a run, written separately from the
/// append-only event log. A partial record is written at run start and updated
/// once when the run reaches a terminal state. It captures the accountable
/// user, the model source, timing, the final outcome, and the policy decisions
/// enforced during the run (Principles IX and XI).
/// </summary>
public interface IOperationalStore
{
    Task CreateAsync(
        RunId runId,
        string submittingUser,
        ModelSource modelSource,
        DateTimeOffset startedAt,
        IReadOnlyList<string> policyDecisions,
        CancellationToken ct = default);

    Task CompleteAsync(
        RunId runId,
        DateTimeOffset endedAt,
        int stepCount,
        string outcome,
        IReadOnlyList<string> policyDecisions,
        CancellationToken ct = default);
}
