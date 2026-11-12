using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal static class CheckpointRecoveryMemberBarrier
{
    internal static ImmutableArray<ChildRecoveryMember> RequiredMembers(
        ChildCheckpointRecoveryManifest manifest)
    {
        if (manifest.Members.Any(member => member.Disposition is "completedUnjoined" or "unresolved"))
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);
        return manifest.Members.Where(member => member.Disposition == "pendingUnsent").ToImmutableArray();
    }

    internal static void RequireAllRegistered(
        ChildCheckpointRecoveryManifest manifest,
        bool rootRegistered,
        IReadOnlySet<string> registeredAssociations)
    {
        if (!rootRegistered ||
            RequiredMembers(manifest).Any(member => !registeredAssociations.Contains(member.AssociationId)))
            throw new CoordinationException(
                "checkpoint_recovery_registration_barrier", StatusCodes.Status409Conflict);
    }
}
