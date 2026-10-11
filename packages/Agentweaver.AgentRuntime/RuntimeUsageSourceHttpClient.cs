using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeUsageSourceHttpClient(
    HttpClient client, Uri orchestratorAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    internal async Task<RuntimeSdkSourceReceipt> RegisterAsync(
        AuthorizedRuntimeSession session, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeSdkSourceReceipt>(
            client, _address, $"/internal/runtime/sources/{session.Registration.RuntimeInstanceId:D}",
            actor, new RuntimeSdkSourceRequest(Request(proof), session.Facts), cancellationToken)
            .ConfigureAwait(false);
        if (receipt.RuntimeInstanceId != session.Registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != session.Registration.Revision ||
            receipt.SourceGrantId != proof.GrantId || receipt.Source != session.Facts ||
            receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                session.Registration, session.Facts))
            throw new RuntimeAuthorizationException("runtime_sdk_source_receipt_invalid");
        RuntimeContractValidation.ValidateHash(receipt.CanonicalPayloadHash);
        return receipt;
    }

    internal async Task<RuntimeUsageSourceReceipt> AppendAsync(
        AuthorizedRuntimeSession session, SdkUsageObservation observation, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeUsageSourceReceipt>(
            client, _address, session.Registration.Binding.ObservationEndpoint.AbsolutePath, actor,
            new RuntimeUsageObservationRequest(Request(session.Proof()), observation), cancellationToken)
            .ConfigureAwait(false);
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        var expected = RuntimeUsageSourceReceiptContract.CreateUsage(
            session.Registration, session.Facts, observation);
        if (receipt.Registration != session.Registration || receipt.Usage != expected)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_mismatch");
        return receipt;
    }

    private void RequireAudience(AuthorizedRuntimeSession session)
    {
        var endpoint = session.Registration.Binding.ObservationEndpoint;
        if (endpoint.Scheme != _address.Scheme || endpoint.Authority != _address.Authority ||
            endpoint.AbsolutePath != "/internal/runtime/observations")
            throw new RuntimeAuthorizationException("runtime_usage_audience_invalid");
    }

    private static RuntimeCredentialHttpRequest Request(RuntimeCredentialProof proof) =>
        new(proof.GrantId, proof.RuntimeInstanceId, proof.Revision, proof.Purpose, proof.Audience,
            proof.ConfigurationHash, proof.Credential.GetValue(), proof.Credential.ExpiresAt, Guid.Empty);
}

internal sealed class RuntimeUsageCostPreflightHttpClient(
    HttpClient client, Uri eventsAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(eventsAddress);

    internal async Task EnsurePricedAsync(
        AuthorizedRuntimeSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var registration = session.Registration;
        var facts = session.Facts;
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeUsageCostPreflightReceipt>(
            client,
            _address,
            $"/internal/runtime/sources/{registration.RuntimeInstanceId:D}/cost-preflight",
            actor,
            new RuntimeUsageCostPreflightRequest(),
            cancellationToken).ConfigureAwait(false);
        RuntimeUsageCostPreflightContract.Validate(receipt);
        if (receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.ExecutionFence != registration.Binding.ExecutionFence ||
            receipt.ModelSelectionReference != registration.Binding.ModelSelectionReference ||
            receipt.ModelSelectionReference != facts.ModelSelectionReference ||
            receipt.ModelId != facts.ModelId ||
            receipt.MeterSource != facts.MeterSource ||
            receipt.AcceptedSelectionHash != registration.Binding.AcceptedSelectionHash ||
            receipt.AcceptedSelectionHash != facts.AcceptedSelectionHash ||
            receipt.SourceReceiptHash != RuntimeUsageSourceReceiptContract.HashSource(registration, facts))
            throw new RuntimeAuthorizationException("runtime_cost_preflight_source_mismatch");
        if (!receipt.IsPriced)
            throw new RuntimeAuthorizationException("runtime_cost_preflight_unpriced");
    }
}
