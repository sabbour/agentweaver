using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

internal static class EnvironmentP1InvocationBindingVerifier
{
    public static async Task<P1InvocationBinding> ReadAndValidateAsync(
        IEnvironmentP1InvocationBindingReader reader,
        RuntimeActorAuthorization? actor,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse authorization,
        P1InvocationBindingExpectation expectation,
        CancellationToken cancellationToken)
    {
        expectation.Validate();
        if (actor is null)
            throw new RuntimeAuthorizationException("p1_invocation_binding_actor_required");
        var binding = await reader.ReadCurrentAsync(
            actor, owner.ProjectId, owner.RunId, cancellationToken).ConfigureAwait(false);
        if (binding is null)
            throw new RuntimeAuthorizationException("p1_invocation_binding_missing");

        if (binding.ContractVersion != 1 ||
            binding.CoreInvocationId != expectation.CoreInvocationId ||
            binding.SetupInvocationId != expectation.SetupInvocationId ||
            binding.SetupReceiptHash != expectation.SetupReceiptHash ||
            binding.RootSessionId != expectation.RootSessionId ||
            binding.AcceptedSelectionHash != expectation.AcceptedSelectionHash ||
            binding.ExecutionFence != expectation.ExecutionFence ||
            binding.TenantId != owner.TenantId ||
            binding.ProjectId != owner.ProjectId ||
            binding.RunId != owner.RunId ||
            binding.ActorIssuer != authorization.Issuer ||
            binding.ActorSubject != authorization.ActorId ||
            binding.MembershipRevision != authorization.MembershipRevision ||
            binding.MembershipId == Guid.Empty ||
            binding.RootStateVersion < 1 ||
            binding.AcceptedAt == default)
            throw new RuntimeAuthorizationException("p1_invocation_binding_mismatch");
        return binding;
    }
}
