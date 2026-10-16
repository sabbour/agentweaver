using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Holds prerequisite snapshots prepared before a backlog claim transaction begins.
/// </summary>
internal record BacklogPrerequisiteEvidencePreparation(
    ImmutableArray<BacklogPrerequisiteExecutionSnapshot> Prerequisites);

/// <summary>
/// Reads owner-verified prerequisite evidence while the Orchestrator claims a backlog task.
/// </summary>
internal interface IBacklogPrerequisiteEvidenceReader
{
    // May resolve immutable source evidence here, before the claim transaction starts.
    /// <summary>
    /// Resolves evidence that may require calls outside the claim transaction.
    /// </summary>
    /// <param name="actor">The authenticated caller that owns the claim.</param>
    /// <param name="selection">The accepted project and run selection.</param>
    /// <param name="graph">The backlog graph used for the preflight readiness check.</param>
    /// <param name="task">The task being claimed.</param>
    /// <param name="cancellationToken">Cancels the preparation work.</param>
    /// <returns>Prepared evidence for the transaction-time re-read.</returns>
    Task<BacklogPrerequisiteEvidencePreparation> PrepareForClaimAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        BacklogDependencyGraph graph,
        BacklogTaskReference task,
        CancellationToken cancellationToken);

    // This method runs in the claim transaction and must only re-read local owner state.
    /// <summary>
    /// Re-reads local prerequisite state inside the atomic task-claim transaction.
    /// </summary>
    /// <param name="connection">The connection holding the claim transaction.</param>
    /// <param name="transaction">The transaction that will persist the claim.</param>
    /// <param name="actor">The authenticated caller that owns the claim.</param>
    /// <param name="selection">The accepted project and run selection.</param>
    /// <param name="graph">The graph locked for the claim.</param>
    /// <param name="task">The task being claimed.</param>
    /// <param name="prepared">Evidence resolved before the transaction started.</param>
    /// <param name="decisionStateVersion">The initialized coordinator decision version.</param>
    /// <param name="executionFence">The accepted run's current execution fence.</param>
    /// <param name="cancellationToken">Cancels the transaction-time read.</param>
    /// <returns>Current prerequisite snapshots used by readiness validation.</returns>
    /// <remarks>This method must use only local owner state; remote evidence is not fetched while holding the claim transaction.</remarks>
    Task<ImmutableArray<BacklogPrerequisiteExecutionSnapshot>> ReadCurrentForClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        BacklogDependencyGraph graph,
        BacklogTaskReference task,
        BacklogPrerequisiteEvidencePreparation prepared,
        long decisionStateVersion,
        long executionFence,
        CancellationToken cancellationToken);
}
