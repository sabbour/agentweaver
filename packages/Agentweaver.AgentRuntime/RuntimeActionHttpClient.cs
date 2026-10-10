using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeActionHttpClient(HttpClient client, Uri orchestratorAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    internal async Task RequireAsync(
        RuntimeRegistration registration, string actionId, ReadOnlyMemory<byte> input,
        Func<CancellationToken, Task> requireCurrentAuthority, CancellationToken cancellationToken,
        bool isToolInvocation = false, Guid? dispatchId = null)
    {
        var request = new RuntimeActionRequest(1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, Guid.NewGuid(), actionId, RuntimeContractValidation.Hash(input.Span))
        {
            IsToolInvocation = isToolInvocation, DispatchId = dispatchId
        };
        RuntimeActionContract.Validate(request);
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var admitted = await SendAdmissionAsync(
            "/internal/runtime/actions/authorize", request, requireCurrentAuthority, cancellationToken)
            .ConfigureAwait(false);
        if (admitted.ContractVersion != 1 || admitted.Request != request ||
            admitted.PolicyReceiptId != request.EventId || admitted.GrantId != request.EventId.ToString("N") ||
            admitted.GrantRevision != "1")
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        if (admitted.Outcome != PolicyEvaluationOutcome.Allow ||
            admitted.ReasonCode != PolicyEvaluationReasonCode.Allowed)
            throw new RuntimeAuthorizationException("runtime_action_denied");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var verified = await SendAdmissionAsync(
            "/internal/runtime/actions/verify", admitted, requireCurrentAuthority, cancellationToken)
            .ConfigureAwait(false);
        if (verified != admitted)
            throw new RuntimeAuthorizationException("runtime_action_admission_changed");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<RuntimeActionAdmission> SendAdmissionAsync(
        string path, object request, Func<CancellationToken, Task> requireCurrentAuthority,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RuntimeOwnerHttpTransport.SendAsync<RuntimeActionAdmission>(
                client, _address, path, actor, request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
            return await RuntimeOwnerHttpTransport.SendAsync<RuntimeActionAdmission>(
                client, _address, path, actor, request, cancellationToken).ConfigureAwait(false);
        }
    }
}
