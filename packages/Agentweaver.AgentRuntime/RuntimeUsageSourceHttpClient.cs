using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Sends SDK source, usage, native-turn, and credit-denial evidence to the Orchestrator,
/// validating each owner receipt against the current runtime session.
/// </summary>
public sealed class RuntimeUsageSourceHttpClient(
    HttpClient client, Uri orchestratorAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    /// <summary>Registers the exact SDK source and validates its canonical payload hash.</summary>
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

    /// <summary>Appends one SDK usage observation and requires the returned observation to match it.</summary>
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

    /// <summary>Starts owner-side admission for a workflow turn and validates its result.</summary>
    internal async Task<RuntimeNativeTurnBeginResult> BeginNativeTurnAsync(
        AuthorizedRuntimeSession session, RuntimeA2ASendRequest message, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            session.Registration.RuntimeInstanceId, session.Registration.Revision, proof.GrantId,
            session.Facts, RuntimeUsageSourceReceiptContract.HashSource(session.Registration, session.Facts),
            DateTimeOffset.UtcNow);
        var result = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeTurnBeginResult>(
            client, _address, "/internal/runtime/turns/begin", actor,
            new RuntimeNativeTurnBeginRequest(Request(proof), message, session.Facts), cancellationToken)
            .ConfigureAwait(false);
        RuntimeNativeDenialContract.ValidateBeginResult(result, session.Registration, sourceReceipt, message);
        return result;
    }

    /// <summary>Reads denial evidence for a native dispatch at the requested stage.</summary>
    internal async Task<RuntimeNativeCreditDenialReadResult> ReadNativeCreditDenialAsync(
        AuthorizedRuntimeSession session, Guid dispatchId, RuntimeCreditLimitDenialStage stage,
        CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            session.Registration.RuntimeInstanceId, session.Registration.Revision, proof.GrantId,
            session.Facts, RuntimeUsageSourceReceiptContract.HashSource(session.Registration, session.Facts),
            DateTimeOffset.UtcNow);
        var readRequest = new RuntimeNativeCreditDenialReadRequest(
            Request(proof), dispatchId, stage);
        RuntimeNativeDenialContract.ValidateReadRequest(readRequest);
        var response = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeCreditDenialReadResult>(
            client, _address, "/internal/runtime/turns/denials/read", actor,
            readRequest, cancellationToken)
            .ConfigureAwait(false);
        RuntimeNativeDenialContract.ValidateReadResult(
            response, session.Registration, sourceReceipt, dispatchId, stage);
        return response;
    }

    /// <summary>Reads one persisted run-wide numeric-limit denial for the exact current registration.</summary>
    internal async Task<RuntimeNumericLimitDenialReceipt?> ReadNumericLimitDenialAsync(
        AuthorizedRuntimeSession session, Guid attemptId, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var registration = session.Registration;
        var request = new RuntimeNumericLimitDenialReadRequest(
            registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, attemptId);
        RuntimeNumericLimitDenialReadContract.ValidateRequest(request);
        var result = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNumericLimitDenialReadResult>(
            client, _address, "/internal/runtime/actions/numeric-denials/read", actor,
            request, cancellationToken).ConfigureAwait(false);
        RuntimeNumericLimitDenialReadContract.ValidateResult(result, registration, request);
        if (result.Receipt is { } receipt &&
            (receipt.SourceRegistrationRevision != registration.Revision ||
             receipt.SourceGrantId != session.SourceGrant.GrantId ||
             receipt.SourceHash != RuntimeUsageSourceReceiptContract.HashSource(registration, session.Facts)))
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        return result.Receipt;
    }

    /// <summary>Records evidence that an admitted native turn was denied without sending it.</summary>
    internal async Task<RuntimeNativeTurnNoSendReceipt> RecordNativeTurnNoSendAsync(
        AuthorizedRuntimeSession session, RuntimeNativeCreditDenialReceipt denial,
        RuntimeNativeTurnNoSendReceipt evidence, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var sourceReceipt = new RuntimeSdkSourceReceipt(
            session.Registration.RuntimeInstanceId, session.Registration.Revision, proof.GrantId,
            session.Facts, RuntimeUsageSourceReceiptContract.HashSource(session.Registration, session.Facts),
            DateTimeOffset.UtcNow);
        RuntimeNativeDenialContract.ValidateNoSend(evidence, denial, session.Registration, sourceReceipt);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeNativeTurnNoSendReceipt>(
            client, _address, "/internal/runtime/turns/no-send", actor,
            new RuntimeNativeTurnNoSendEvidenceRequest(Request(proof), denial, evidence), cancellationToken)
            .ConfigureAwait(false);
        RuntimeNativeDenialContract.ValidateNoSend(receipt, denial, session.Registration, sourceReceipt);
        return receipt;
    }

    /// <summary>Records the fixed-credit guard receipt under the current source grant.</summary>
    internal async Task<RuntimeFixedCreditGuardReceipt> RecordFixedCreditGuardAsync(
        AuthorizedRuntimeSession session, RuntimeHostSessionProof expectedProof,
        RuntimeFixedCreditGuardReceipt receipt, CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var grant = session.SourceGrant;
        RequireFixedSourceProof(session, expectedProof, proof, grant);
        RuntimeFixedCreditGuardContract.ValidateReceipt(receipt);
        var sourceReceipt = CurrentSourceReceipt(session, proof);
        RuntimeFixedCreditGuardContract.ValidateSourceTuple(
            receipt, session.Registration, grant, sourceReceipt);
        var stored = await RuntimeOwnerHttpTransport.SendAsync<RuntimeFixedCreditGuardReceipt>(
            client, _address, "/internal/runtime/turns/fixed-credit-denials/record", actor,
            new RuntimeFixedCreditGuardRecordRequest(Request(proof), receipt), cancellationToken)
            .ConfigureAwait(false);
        RuntimeFixedCreditGuardContract.ValidateReceipt(stored);
        if (stored.Registration != receipt.Registration || stored.Stage != receipt.Stage ||
            stored.RequestId != receipt.RequestId || stored.RequestHash != receipt.RequestHash)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        RuntimeFixedCreditGuardContract.ValidateSourceTuple(
            stored, session.Registration, grant, sourceReceipt);
        return stored;
    }

    /// <summary>Records no-send evidence for a fixed-credit denial.</summary>
    internal async Task<RuntimeFixedCreditNoSendReceipt> RecordFixedCreditNoSendAsync(
        AuthorizedRuntimeSession session, RuntimeHostSessionProof expectedProof,
        RuntimeFixedCreditGuardReceipt receipt, RuntimeFixedCreditNoSendReceipt evidence,
        CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var grant = session.SourceGrant;
        RequireFixedSourceProof(session, expectedProof, proof, grant);
        var sourceReceipt = CurrentSourceReceipt(session, proof);
        RuntimeFixedCreditGuardContract.ValidateEvidence(
            evidence, receipt, session.Registration, grant, sourceReceipt);
        var stored = await RuntimeOwnerHttpTransport.SendAsync<RuntimeFixedCreditNoSendReceipt>(
            client, _address, "/internal/runtime/turns/fixed-credit-denials/no-send", actor,
            new RuntimeFixedCreditNoSendEvidenceRequest(Request(proof), receipt, evidence), cancellationToken)
            .ConfigureAwait(false);
        RuntimeFixedCreditGuardContract.ValidateEvidence(
            stored, receipt, session.Registration, grant, sourceReceipt);
        return stored;
    }

    /// <summary>Reads and validates host pre-queue fixed-credit evidence.</summary>
    internal async Task<RuntimeFixedCreditGuardReadResult> ReadFixedCreditGuardAsync(
        AuthorizedRuntimeSession session, Guid requestId, string requestHash,
        CancellationToken cancellationToken)
    {
        RequireAudience(session);
        var proof = session.Proof();
        var grant = session.SourceGrant;
        if (grant.GrantId != proof.GrantId || grant.Revision != proof.Revision)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
        var sourceReceipt = CurrentSourceReceipt(session, proof);
        var request = new RuntimeFixedCreditGuardReadRequest(
            Request(proof), session.Registration.RuntimeInstanceId, session.Registration.Revision,
            session.Registration.Binding.ExecutionFence, RuntimeFixedCreditGuardStage.HostPreQueue,
            requestId, requestHash);
        RuntimeFixedCreditGuardContract.ValidateReadRequest(request);
        var result = await RuntimeOwnerHttpTransport.SendAsync<RuntimeFixedCreditGuardReadResult>(
            client, _address, "/internal/runtime/turns/fixed-credit-denials/read", actor,
            request, cancellationToken).ConfigureAwait(false);
        RuntimeFixedCreditGuardContract.ValidateReadResult(
            result, session.Registration, request, grant, sourceReceipt);
        if (result.Receipt is { SourceGrantId: not null } receipt)
            RuntimeFixedCreditGuardContract.ValidateSourceTuple(
                receipt, session.Registration, grant, sourceReceipt);
        return result;
    }

    /// <summary>Stores the observed native turn and verifies its canonical observation hash.</summary>
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

    /// <summary>Completes owner-side accounting using the exact usage references for the recorded turn.</summary>
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

    /// <summary>Requires the session's observation endpoint to match this client's owner address and route.</summary>
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

    private static RuntimeSdkSourceReceipt CurrentSourceReceipt(
        AuthorizedRuntimeSession session, RuntimeCredentialProof proof) =>
        new(session.Registration.RuntimeInstanceId, session.Registration.Revision, proof.GrantId,
            session.Facts, RuntimeUsageSourceReceiptContract.HashSource(session.Registration, session.Facts),
            DateTimeOffset.UtcNow);

    private static void RequireFixedSourceProof(
        AuthorizedRuntimeSession session, RuntimeHostSessionProof expected,
        RuntimeCredentialProof proof, RuntimeGrantReceipt grant)
    {
        if (expected.ContractVersion != 1 || expected.Registration != session.Registration ||
            expected.Purpose != RuntimeCredentialPurpose.Observe ||
            expected.SourceGrantId != proof.GrantId || expected.SourceGrantRevision != proof.Revision ||
            proof.Purpose != RuntimeCredentialPurpose.Observe ||
            grant.GrantId != proof.GrantId || grant.Revision != proof.Revision ||
            grant.RuntimeInstanceId != session.Registration.RuntimeInstanceId ||
            grant.RegistrationRevision != session.Registration.Revision ||
            !proof.Credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
    }
}

/// <summary>Validates owner cost-preflight identity while preserving an explicit Unpriced result.</summary>
internal sealed class RuntimeUsageCostPreflightHttpClient(
    HttpClient client, Uri eventsAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(eventsAddress);

    /// <summary>Validates cost-preflight evidence without making a missing rate a dispatch gate.</summary>
    internal async Task ValidateAsync(
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
    }
}
