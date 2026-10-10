using System.Collections.Immutable;
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

    internal async Task<RuntimeNativeTurnAdmissionReceipt> BeginNativeTurnAsync(
        AuthorizedRuntimeSession session, RuntimeA2ASendRequest message, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeTurnAdmissionReceipt>(
            client, _address, "/internal/runtime/turns/begin", actor,
            new RuntimeNativeTurnBeginRequest(Request(session.Proof()), message, session.Facts), cancellationToken)
            .ConfigureAwait(false);
        RuntimeNativeTurnContract.ValidateAdmission(receipt, session.Registration, session.Facts, message);
        return receipt;
    }

    internal async Task<RuntimeNativeTurnRecordedReceipt> RecordNativeTurnAsync(
        AuthorizedRuntimeSession session, RuntimeNativeTurnAdmissionReceipt admission,
        RuntimeNativeTurnObservation observation, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        RuntimeNativeTurnContract.ValidateObservation(admission, observation);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeTurnRecordedReceipt>(
            client, _address, "/internal/runtime/turns/observations", actor,
            new RuntimeNativeTurnObservationRequest(Request(session.Proof()), admission, observation), cancellationToken)
            .ConfigureAwait(false);
        RuntimeNativeTurnContract.ValidateRecorded(receipt);
        if (receipt.Admission != admission ||
            receipt.CanonicalPayloadHash != RuntimeNativeTurnContract.ObservationHash(observation))
            throw new RuntimeAuthorizationException("runtime_native_turn_receipt_invalid");
        return receipt;
    }

    internal async Task<RuntimeNativeTurnAccountingReceipt> CompleteNativeTurnAsync(
        AuthorizedRuntimeSession session, RuntimeNativeTurnRecordedReceipt recorded,
        ImmutableArray<RuntimeUsageCostReceiptReference> references, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, references);
        await session.RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeTurnAccountingReceipt>(
            client, _address, "/internal/runtime/turns/accounting", actor,
            new RuntimeNativeTurnAccountingRequest(Request(session.Proof()), recorded, references), cancellationToken)
            .ConfigureAwait(false);
        RuntimeNativeTurnContract.ValidateAccounted(receipt, recorded, references);
        await session.RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
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
