using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Npgsql;

namespace Agentweaver.Orchestrator;

internal record BacklogPrerequisiteEvidencePreparation(
    ImmutableArray<BacklogPrerequisiteExecutionSnapshot> Prerequisites);

internal interface IBacklogPrerequisiteEvidenceReader
{
    // May resolve immutable source evidence here, before the claim transaction starts.
    Task<BacklogPrerequisiteEvidencePreparation> PrepareForClaimAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        BacklogDependencyGraph graph,
        BacklogTaskReference task,
        CancellationToken cancellationToken);

    // This method runs in the claim transaction and must only re-read local owner state.
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
