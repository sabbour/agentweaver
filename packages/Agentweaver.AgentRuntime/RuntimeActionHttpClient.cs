using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeActionHttpClient(HttpClient client, Uri orchestratorAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    internal async Task RequireAsync(
        RuntimeRegistration registration, string actionId, ReadOnlyMemory<byte> input,
        Func<CancellationToken, Task> requireCurrentAuthority, CancellationToken cancellationToken)
    {
        var request = new RuntimeActionRequest(1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, Guid.NewGuid(), actionId, RuntimeContractValidation.Hash(input.Span));
        RuntimeActionContract.Validate(request);
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var admitted = await RuntimeOwnerHttpTransport.SendAsync<RuntimeActionAdmission>(
            client, _address, "/internal/runtime/actions/authorize", actor, request, cancellationToken)
            .ConfigureAwait(false);
        if (admitted.ContractVersion != 1 || admitted.Request != request ||
            admitted.PolicyReceiptId != request.EventId || admitted.GrantId != request.EventId.ToString("N") ||
            admitted.GrantRevision != "1")
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        if (admitted.Outcome != PolicyEvaluationOutcome.Allow ||
            admitted.ReasonCode != PolicyEvaluationReasonCode.Allowed)
            throw new RuntimeAuthorizationException("runtime_action_denied");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var verified = await RuntimeOwnerHttpTransport.SendAsync<RuntimeActionAdmission>(
            client, _address, "/internal/runtime/actions/verify", actor, admitted, cancellationToken)
            .ConfigureAwait(false);
        if (verified != admitted)
            throw new RuntimeAuthorizationException("runtime_action_admission_changed");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
