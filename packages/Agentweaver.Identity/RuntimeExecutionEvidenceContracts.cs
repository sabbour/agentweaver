using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

/// <summary>Accepted run-wide numeric capacity enforced by Orchestrator.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeNumericLimitKind>))]
public enum RuntimeNumericLimitKind
{
    ModelTurns,
    ToolCalls
}

/// <summary>
/// Immutable Orchestrator evidence that a specific request exceeded an accepted run-wide limit
/// and that the request's persisted send state was unchanged across the denial.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNumericLimitDenialReceipt(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] Guid ReceiptId,
    [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] string RootSessionId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] Guid RuntimeInstanceId,
    [property: JsonRequired] long RegistrationRevision,
    [property: JsonRequired] long SourceRegistrationRevision,
    [property: JsonRequired] Guid SourceGrantId,
    [property: JsonRequired] string SourceHash,
    [property: JsonRequired] Guid GrantId,
    [property: JsonRequired] string GrantRevision,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] string AcceptedSelectionHash,
    [property: JsonRequired] long OwnerRevision,
    [property: JsonRequired] RuntimeNumericLimitKind LimitKind,
    [property: JsonRequired] int AcceptedLimit,
    [property: JsonRequired] long ObservedCount,
    [property: JsonRequired] Guid AttemptId,
    [property: JsonRequired] string RequestHash,
    [property: JsonRequired] Guid? DispatchId,
    [property: JsonRequired] string? CheckpointId,
    [property: JsonRequired] long? CheckpointRevision,
    [property: JsonRequired] string? CheckpointHash,
    [property: JsonRequired] long? CheckpointDecisionStateVersion,
    [property: JsonRequired] string? WorkPlanId,
    [property: JsonRequired] string? AssociationId,
    [property: JsonRequired] string SendStateBeforeHash,
    [property: JsonRequired] string SendStateAfterHash,
    [property: JsonRequired] DateTimeOffset RecordedAt,
    [property: JsonRequired] string CanonicalPayloadHash)
{
    /// <summary>Checks request scope, accepted limit, checkpoint binding, no-send equality, and payload hash.</summary>
    public RuntimeNumericLimitDenialReceipt Validate()
    {
        if (ContractVersion != 1 || ReceiptId == Guid.Empty || RuntimeInstanceId == Guid.Empty ||
            RegistrationRevision < 1 || SourceRegistrationRevision != RegistrationRevision ||
            SourceGrantId == Guid.Empty || GrantId == Guid.Empty || string.IsNullOrWhiteSpace(GrantRevision) ||
            ExecutionFence < 1 || OwnerRevision < 1 || !Enum.IsDefined(LimitKind) ||
            AcceptedLimit < 1 || ObservedCount < AcceptedLimit || AttemptId == Guid.Empty ||
            DispatchId == Guid.Empty || CheckpointRevision < 1 ||
            (CheckpointId is null) != (CheckpointRevision is null) ||
            (CheckpointId is null) != (CheckpointHash is null) ||
            (CheckpointRevision is null) != (CheckpointDecisionStateVersion is null) ||
            (CheckpointId is null) != (WorkPlanId is null) ||
            (WorkPlanId is null) != (AssociationId is null) ||
            RecordedAt == default || SendStateBeforeHash != SendStateAfterHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_invalid");

        RuntimeContractValidation.ValidateIdentifier(ProjectId);
        RuntimeContractValidation.ValidateIdentifier(RunId);
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateIdentifier(SessionId);
        RuntimeContractValidation.ValidateHash(SourceHash);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        RuntimeContractValidation.ValidateHash(RequestHash);
        RuntimeContractValidation.ValidateHash(SendStateBeforeHash);
        RuntimeContractValidation.ValidateHash(SendStateAfterHash);
        RuntimeContractValidation.ValidateHash(CanonicalPayloadHash);
        if (CheckpointId is not null)
        {
            RuntimeContractValidation.ValidateIdentifier(CheckpointId);
            RuntimeContractValidation.ValidateHash(CheckpointHash!);
            RuntimeContractValidation.ValidateIdentifier(WorkPlanId!);
            RuntimeContractValidation.ValidateIdentifier(AssociationId!);
            if (CheckpointDecisionStateVersion < 1)
                throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_invalid");
        }
        var hash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            this with { CanonicalPayloadHash = string.Empty }));
        if (hash != CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_invalid");
        return this;
    }

    /// <summary>Computes the canonical payload hash and validates the sealed receipt.</summary>
    public static RuntimeNumericLimitDenialReceipt Seal(RuntimeNumericLimitDenialReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var sealedReceipt = receipt with
        {
            CanonicalPayloadHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
                receipt with { CanonicalPayloadHash = string.Empty }))
        };
        return sealedReceipt.Validate();
    }
}

/// <summary>Authenticated lookup identity for one Orchestrator numeric-limit denial attempt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNumericLimitDenialReadRequest(
    [property: JsonRequired] Guid RuntimeInstanceId,
    [property: JsonRequired] long RegistrationRevision,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] Guid AttemptId);

/// <summary>Optional stored numeric-limit denial for the requested attempt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNumericLimitDenialReadResult(
    [property: JsonRequired] RuntimeNumericLimitDenialReceipt? Receipt,
    RuntimeNumericLimitNativeNoSendReceipt? NativeNoSend = null);

/// <summary>Immutable evidence that a root numeric-limit denial had no native SDK events in its exact interval.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNumericLimitNativeNoSendReceipt(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] Guid ReceiptId,
    [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] string RootSessionId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] Guid RuntimeInstanceId,
    [property: JsonRequired] long RegistrationRevision,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] Guid AttemptId,
    [property: JsonRequired] Guid DispatchId,
    [property: JsonRequired] string RequestHash,
    [property: JsonRequired] Guid DenialReceiptId,
    [property: JsonRequired] string DenialPayloadHash,
    [property: JsonRequired] string RegistrationHash,
    [property: JsonRequired] string SourceHash,
    [property: JsonRequired] Guid SourceGrantId,
    [property: JsonRequired] long SourceGrantRevision,
    [property: JsonRequired] string SdkSessionIdHash,
    [property: JsonRequired] string BeforeCursorHash,
    [property: JsonRequired] string AfterCursorHash,
    [property: JsonRequired] long ObservedEventCount,
    [property: JsonRequired] bool RangeComplete,
    [property: JsonRequired] DateTimeOffset RecordedAt,
    [property: JsonRequired] string CanonicalPayloadHash)
{
    /// <summary>Validates the sealed empty-range evidence without retaining cursor or prompt contents.</summary>
    public RuntimeNumericLimitNativeNoSendReceipt Validate()
    {
        if (ContractVersion != 1 || ReceiptId == Guid.Empty || RuntimeInstanceId == Guid.Empty ||
            RegistrationRevision < 1 || ExecutionFence < 1 || AttemptId == Guid.Empty ||
            DispatchId != AttemptId || DenialReceiptId == Guid.Empty || SourceGrantId == Guid.Empty ||
            SourceGrantRevision < 1 || ObservedEventCount != 0 || !RangeComplete ||
            BeforeCursorHash != AfterCursorHash || RecordedAt == default)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_invalid");

        RuntimeContractValidation.ValidateIdentifier(ProjectId);
        RuntimeContractValidation.ValidateIdentifier(RunId);
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateIdentifier(SessionId);
        RuntimeContractValidation.ValidateHash(RequestHash);
        RuntimeContractValidation.ValidateHash(DenialPayloadHash);
        RuntimeContractValidation.ValidateHash(RegistrationHash);
        RuntimeContractValidation.ValidateHash(SourceHash);
        RuntimeContractValidation.ValidateHash(SdkSessionIdHash);
        RuntimeContractValidation.ValidateHash(BeforeCursorHash);
        RuntimeContractValidation.ValidateHash(AfterCursorHash);
        RuntimeContractValidation.ValidateHash(CanonicalPayloadHash);
        if (RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
                this with { CanonicalPayloadHash = string.Empty })) != CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_invalid");
        return this;
    }

    /// <summary>Seals all evidence fields into the canonical payload hash.</summary>
    public static RuntimeNumericLimitNativeNoSendReceipt Seal(
        RuntimeNumericLimitNativeNoSendReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return (receipt with
        {
            CanonicalPayloadHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
                receipt with { CanonicalPayloadHash = string.Empty }))
        }).Validate();
    }
}

/// <summary>Observe-authenticated request to persist one root numeric-limit no-send companion.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNumericLimitNativeNoSendRecordRequest(
    [property: JsonRequired] RuntimeCredentialHttpRequest Authorization,
    [property: JsonRequired] RuntimeActionRequest Action,
    [property: JsonRequired] RuntimeNumericLimitNativeNoSendReceipt Evidence)
{
    public override string ToString() => nameof(RuntimeNumericLimitNativeNoSendRecordRequest) + " [REDACTED]";
}

/// <summary>Validation for the immutable root numeric-limit denial and no-send pair.</summary>
public static class RuntimeNumericLimitNativeNoSendContract
{
    /// <summary>Checks companion bindings against the stored denial and current registration.</summary>
    public static RuntimeNumericLimitNativeNoSendReceipt ValidatePair(
        RuntimeNumericLimitNativeNoSendReceipt evidence,
        RuntimeNumericLimitDenialReceipt denial,
        RuntimeRegistration registration,
        RuntimeActionRequest? action = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(denial);
        ArgumentNullException.ThrowIfNull(registration);
        evidence.Validate();
        denial.Validate();
        var binding = registration.Binding;
        if (denial.LimitKind != RuntimeNumericLimitKind.ModelTurns ||
            binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            binding.WorkflowStepId is not null || binding.MaxModelTurns is null ||
            denial.RootSessionId != binding.SessionId || denial.SessionId != binding.SessionId ||
            denial.AcceptedLimit != binding.MaxModelTurns ||
            denial.GrantId != denial.AttemptId || denial.GrantRevision != "1" ||
            denial.DispatchId != denial.AttemptId ||
            evidence.ProjectId != denial.ProjectId || evidence.RunId != denial.RunId ||
            evidence.RootSessionId != binding.SessionId || evidence.SessionId != binding.SessionId ||
            evidence.RuntimeInstanceId != registration.RuntimeInstanceId ||
            evidence.RegistrationRevision != registration.Revision ||
            evidence.ExecutionFence != registration.Binding.ExecutionFence ||
            evidence.AttemptId != denial.AttemptId || evidence.DispatchId != denial.DispatchId ||
            evidence.RequestHash != denial.RequestHash ||
            evidence.DenialReceiptId != denial.ReceiptId ||
            evidence.DenialPayloadHash != denial.CanonicalPayloadHash ||
            evidence.ReceiptId != RuntimeNumericLimitNativeNoSendIdentity.ReceiptId(
                denial.ProjectId, denial.RunId, denial.AttemptId, denial.ReceiptId) ||
            evidence.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration) ||
            evidence.SourceGrantId != denial.SourceGrantId ||
            evidence.SourceHash != denial.SourceHash ||
            denial.RegistrationRevision != registration.Revision ||
            denial.ExecutionFence != registration.Binding.ExecutionFence ||
            denial.AcceptedSelectionHash != registration.Binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        if (action is not null)
        {
            RuntimeActionContract.Validate(action);
            if (action.ActionId != "model.turn" || action.RuntimeInstanceId != registration.RuntimeInstanceId ||
                action.RegistrationRevision != registration.Revision ||
                action.ExecutionFence != registration.Binding.ExecutionFence ||
                action.EventId != evidence.AttemptId || action.DispatchId != evidence.DispatchId ||
                RuntimeActionContract.Hash(action) != evidence.RequestHash)
                throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        }
        return evidence;
    }

    /// <summary>Rejects missing originals and validates any companion carried by a read response.</summary>
    public static RuntimeNumericLimitDenialReadResult ValidateResult(
        RuntimeNumericLimitDenialReadResult result,
        RuntimeRegistration registration,
        RuntimeNumericLimitDenialReadRequest request)
    {
        RuntimeNumericLimitDenialReadContract.ValidateResult(
            result with { NativeNoSend = null }, registration, request);
        if (result.NativeNoSend is { } evidence)
        {
            var denial = result.Receipt
                ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
            ValidatePair(evidence, denial, registration);
            if (evidence.AttemptId != request.AttemptId)
                throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        }
        return result;
    }
}

/// <summary>Derives one stable companion receipt ID from its denial attempt and original receipt.</summary>
public static class RuntimeNumericLimitNativeNoSendIdentity
{
    public static Guid ReceiptId(string projectId, string runId, Guid attemptId, Guid denialReceiptId)
    {
        RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateIdentifier(runId);
        if (attemptId == Guid.Empty || denialReceiptId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_invalid");
        var digest = RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(
            $"agentweaver:numeric-limit-native-no-send:v1\0{projectId}\0{runId}\0{attemptId:D}\0{denialReceiptId:D}"));
        var receiptId = Guid.ParseExact(digest[..32], "N");
        if (receiptId == denialReceiptId)
        {
            var bytes = receiptId.ToByteArray();
            bytes[0] ^= 0x80;
            receiptId = new Guid(bytes);
        }
        return receiptId;
    }
}

/// <summary>Validation for attempt-addressed Orchestrator numeric-limit denial reads.</summary>
public static class RuntimeNumericLimitDenialReadContract
{
    /// <summary>Rejects malformed request identities before authorization or storage access.</summary>
    public static void ValidateRequest(RuntimeNumericLimitDenialReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RuntimeInstanceId == Guid.Empty || request.RegistrationRevision < 1 ||
            request.ExecutionFence < 1 || request.AttemptId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_request_invalid");
    }

    /// <summary>Checks a stored receipt against the current registration and requested attempt.</summary>
    public static RuntimeNumericLimitDenialReadResult ValidateResult(
        RuntimeNumericLimitDenialReadResult result,
        RuntimeRegistration registration,
        RuntimeNumericLimitDenialReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateRequest(request);
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != registration.Binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        if (result.Receipt is { } receipt)
        {
            receipt.Validate();
            if (receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
                receipt.RegistrationRevision != registration.Revision ||
                receipt.ExecutionFence != registration.Binding.ExecutionFence ||
                receipt.ProjectId != registration.Binding.ProjectId ||
                receipt.RunId != registration.Binding.RunId ||
                receipt.SessionId != registration.Binding.SessionId ||
                receipt.AttemptId != request.AttemptId ||
                receipt.AcceptedSelectionHash != registration.Binding.AcceptedSelectionHash)
                throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        }
        if (result.NativeNoSend is { } evidence)
        {
            var denial = result.Receipt
                ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
            RuntimeNumericLimitNativeNoSendContract.ValidatePair(evidence, denial, registration);
            if (evidence.AttemptId != request.AttemptId)
                throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        }
        return result;
    }
}

/// <summary>
/// Authenticated current-owner identity used to request a recovery comparison. InvocationId must be
/// the accepted P1 setup invocation or its Core invocation; caller-supplied baselines are not evidence.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeReplacementComparisonRequest(
    [property: JsonRequired] Guid RuntimeInstanceId,
    [property: JsonRequired] long RegistrationRevision,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] Guid DispatchId,
    [property: JsonRequired] string InvocationId);

/// <summary>Blocked result; upstream receipt claims are never accepted as comparison evidence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeReplacementComparisonBlocker(
    [property: JsonRequired] string Status,
    [property: JsonRequired] string InvocationId,
    [property: JsonRequired] string CheckpointId,
    [property: JsonRequired] long CheckpointRevision,
    [property: JsonRequired] string WorkPlanId,
    [property: JsonRequired] string AssociationId,
    [property: JsonRequired] Guid DispatchId,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] Guid OriginalRuntimeInstanceId,
    [property: JsonRequired] long OriginalRegistrationRevision,
    [property: JsonRequired] Guid CurrentRuntimeInstanceId,
    [property: JsonRequired] long CurrentRegistrationRevision,
    [property: JsonRequired] string? ResultCheckpointId,
    [property: JsonRequired] long? ResultCheckpointRevision,
    [property: JsonRequired] RuntimeExecutionCheckpointParent? CheckpointParent,
    [property: JsonRequired] RuntimeExecutionResultDigest? ResultDigest,
    [property: JsonRequired] IReadOnlyList<string> MissingProvenance)
{
    /// <summary>Validates correlation identity and explicit missing-provenance reporting.</summary>
    public RuntimeReplacementComparisonBlocker Validate()
    {
        if (Status != "blocked" || DispatchId == Guid.Empty || CheckpointRevision < 1 || ExecutionFence < 1 ||
            OriginalRuntimeInstanceId == Guid.Empty || OriginalRegistrationRevision < 1 ||
            CurrentRuntimeInstanceId == Guid.Empty || CurrentRegistrationRevision < 1 ||
            MissingProvenance is null || MissingProvenance.Count == 0 ||
            MissingProvenance.Distinct(StringComparer.Ordinal).Count() != MissingProvenance.Count)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_invalid");
        RuntimeContractValidation.ValidateIdentifier(InvocationId);
        RuntimeContractValidation.ValidateIdentifier(CheckpointId);
        RuntimeContractValidation.ValidateIdentifier(WorkPlanId);
        RuntimeContractValidation.ValidateIdentifier(AssociationId);
        if (ResultCheckpointId is null != (ResultCheckpointRevision is null) ||
            ResultCheckpointRevision is < 1 ||
            ResultCheckpointId is not null && CheckpointParent is null ||
            ResultCheckpointId is null && CheckpointParent is not null ||
            ResultCheckpointId is null &&
                !MissingProvenance.Contains("orchestrator.invocation-checkpoint-binding", StringComparer.Ordinal) ||
            ResultDigest is not null && ResultCheckpointId is null ||
            ResultDigest is null &&
                !MissingProvenance.Contains("orchestrator.result-hash-binding", StringComparer.Ordinal) ||
            ResultDigest is { } digest &&
                (digest.Utf8ByteLength < 0 || digest.Sha256 is not { Length: 64 } ||
                 !digest.Sha256.All(Uri.IsHexDigit)))
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_invalid");
        if (CheckpointParent is not null)
            _ = CheckpointParent.Validate();
        foreach (var missing in MissingProvenance)
            RuntimeContractValidation.ValidateIdentifier(missing);
        return this;
    }
}

/// <summary>Validation for blocked replacement comparison requests.</summary>
public static class RuntimeReplacementComparisonContract
{
    /// <summary>Rejects malformed request identities before authorization or storage access.</summary>
    public static void ValidateRequest(RuntimeReplacementComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RuntimeInstanceId == Guid.Empty || request.RegistrationRevision < 1 ||
            request.ExecutionFence < 1 || request.DispatchId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_invalid");
        RuntimeContractValidation.ValidateIdentifier(request.InvocationId);
    }

    /// <summary>Checks that a blocked result is bound to the exact requested dispatch and current fence.</summary>
    public static RuntimeReplacementComparisonBlocker ValidateResult(
        RuntimeReplacementComparisonBlocker result,
        RuntimeRegistration registration,
        RuntimeReplacementComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateRequest(request);
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != registration.Binding.ExecutionFence ||
            result.InvocationId != request.InvocationId ||
            result.DispatchId != request.DispatchId ||
            result.ExecutionFence != request.ExecutionFence ||
            result.CurrentRuntimeInstanceId != registration.RuntimeInstanceId ||
            result.CurrentRegistrationRevision != registration.Revision)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_conflict");
        return result.Validate();
    }
}
