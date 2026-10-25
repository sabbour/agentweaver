using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>Checks current Environment-owner egress evidence before creating or delivering a MAF child intent.</summary>
internal static class MafExecutionEgressGuard
{
    /// <summary>Maps expected Environment and registration authorization failures to controlled endpoint statuses.</summary>
    internal static CoordinationException MapVerificationFailure(RuntimeAuthorizationException failure)
    {
        // Expired credentials and explicit owner authorization changes are caller failures, not transient egress state.
        var statusCode = failure.Code switch
        {
            "runtime_actor_expired" => StatusCodes.Status401Unauthorized,
            RuntimeEgressVerificationFailureCodes.AuthorizationChanged => StatusCodes.Status403Forbidden,
            RuntimeEgressVerificationFailureCodes.StaleFence or
            RuntimeEgressVerificationFailureCodes.GenerationUnverified or
            RuntimeEgressVerificationFailureCodes.SelectionChanged or
            "runtime_owner_context_stale" or
            "runtime_registration_stale" => StatusCodes.Status409Conflict,
            // Missing endpoints, malformed upstream evidence and unknown owner failures must remain unavailable.
            _ => StatusCodes.Status503ServiceUnavailable
        };
        return new CoordinationException(failure.Code, statusCode, failure);
    }

    /// <summary>Derives the only valid verification request from current session registration and owner readiness.</summary>
    internal static RuntimeEgressVerificationRequest RequireReadiness(
        RuntimeRegistration registration,
        EnvironmentRuntimeReadinessContext readiness,
        string projectId,
        string runId,
        string sessionId,
        string contextRevision,
        string selectionHash,
        long executionFence,
        DateTimeOffset now)
    {
        var binding = registration.Binding;
        var placement = readiness.Placement;
        var observation = readiness.Observation;
        // Neither a caller-provided policy generation nor an unbound provider observation can authorize dispatch.
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= now ||
            binding.ProjectId != projectId || binding.RunId != runId ||
            binding.SessionId != sessionId || binding.ExecutionFence != executionFence ||
            binding.AcceptedSelectionHash != selectionHash ||
            binding.ContextRevision != contextRevision ||
            readiness.ContractVersion != 1 || placement.ContractVersion != 1 ||
            placement.TenantId != binding.TenantId ||
            placement.ProjectId != projectId || placement.RunId != runId ||
            placement.EnvironmentId != binding.EnvironmentId ||
            placement.ProfileId != binding.ProfileId ||
            placement.LifecycleGeneration != binding.EnvironmentLifecycleGeneration ||
            placement.LeaseRevision != binding.EnvironmentLeaseRevision ||
            placement.LeaseExpiresAt <= now ||
            placement.CurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            placement.ProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            placement.CurrentFencingGeneration < 1 ||
            placement.CurrentFencingGeneration != placement.ProviderFencingGeneration ||
            placement.Resource.ResourceId != binding.PlacementUid ||
            placement.Resource.Generation != binding.PlacementGeneration ||
            observation.Resource != placement.Resource ||
            observation.FencingGeneration != placement.CurrentFencingGeneration ||
            observation.State != SandboxObservedState.Ready ||
            observation.VerifiedNetworkGeneration is not > 0 ||
            placement.RuntimeOwnerContext is not { } owner ||
            owner.ContextRevision != contextRevision ||
            owner.AcceptedSelectionHash != selectionHash ||
            owner.ExecutionFence != executionFence ||
            owner.SessionId != sessionId)
            throw new CoordinationException("maf_execution_egress_stale", StatusCodes.Status409Conflict);

        // The Environment endpoint verifies this exact owner lifecycle, recorded policy generation,
        // and accepted selection again after Cilium readback; observation alone is never the proof.
        return new RuntimeEgressVerificationRequest(
            1,
            new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity(binding.TenantId, projectId, runId, binding.EnvironmentId),
                placement.LifecycleGeneration),
            observation.VerifiedNetworkGeneration.Value,
            contextRevision);
    }

    /// <summary>Rejects a retry if the live verified generation or intent differs from its immutable intent.</summary>
    internal static void RequireSameReceipt(
        RuntimeEgressVerificationReceipt? pinned,
        RuntimeEgressVerificationReceipt current)
    {
        // Legacy pending intents without an Environment proof cannot be upgraded on replay.
        if (pinned is null ||
            pinned.Fence != current.Fence ||
            pinned.PolicyGeneration != current.PolicyGeneration ||
            !string.Equals(pinned.ContextRevision, current.ContextRevision, StringComparison.Ordinal) ||
            !string.Equals(pinned.IntentHash, current.IntentHash, StringComparison.Ordinal))
            throw new CoordinationException("maf_execution_egress_stale", StatusCodes.Status409Conflict);
    }
}
