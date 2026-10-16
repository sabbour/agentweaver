using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Stage at which a native model turn was denied by a hard credit limit.</summary>
public enum RuntimeCreditLimitDenialStage { Preparation, NativeTurnBegin }

/// <summary>Stable reason categories for native credit denials.</summary>
public enum RuntimeCreditLimitDenialCode { HardLimitReached }

/// <summary>Point at which a fixed zero-credit guard denied a runtime request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeFixedCreditGuardStage>))]
public enum RuntimeFixedCreditGuardStage { ActionAuthorize, ActionVerify, HostPreQueue }

/// <summary>Stable reason recorded by the fixed credit guard.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeFixedCreditGuardCode>))]
public enum RuntimeFixedCreditGuardCode { HardLimitZero }

/// <summary>Canonical owner evidence that a zero hard-credit limit denied a request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeFixedCreditGuardReceipt(
    int ContractVersion,
    RuntimeFixedCreditGuardStage Stage,
    RuntimeFixedCreditGuardCode Code,
    RuntimeRegistration Registration,
    string RegistrationHash,
    Guid RequestId,
    string RequestHash,
    string? SourceHash,
    Guid? SourceGrantId,
    long? SourceGrantRevision,
    DateTimeOffset RecordedAt,
    string CanonicalPayloadHash);

/// <summary>Evidence that a denied fixed-credit request did not send a native model turn.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeFixedCreditNoSendReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    Guid RequestId,
    RuntimeFixedCreditGuardStage Stage,
    string GuardReceiptHash,
    string RegistrationHash,
    string SourceHash,
    Guid SourceGrantId,
    long SourceGrantRevision,
    string NativeSessionHash,
    string BeforeCursorHash,
    string AfterCursorHash,
    int ObservedEventCount,
    bool RangeComplete,
    DateTimeOffset VerifiedAt,
    string CanonicalPayloadHash);

/// <summary>Authenticated request to persist a fixed-credit denial receipt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeFixedCreditGuardRecordRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeFixedCreditGuardReceipt Receipt)
{
    public override string ToString() => nameof(RuntimeFixedCreditGuardRecordRequest) + " [REDACTED]";
}

/// <summary>Authenticated request to persist no-send evidence for a fixed-credit denial.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeFixedCreditNoSendEvidenceRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeFixedCreditGuardReceipt Receipt,
    RuntimeFixedCreditNoSendReceipt Evidence)
{
    public override string ToString() => nameof(RuntimeFixedCreditNoSendEvidenceRequest) + " [REDACTED]";
}

/// <summary>Request for one fixed-credit guard receipt and optional no-send evidence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeFixedCreditGuardReadRequest(
    RuntimeCredentialHttpRequest? Authorization,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    RuntimeFixedCreditGuardStage Stage,
    Guid RequestId,
    string RequestHash)
{
    public override string ToString() => nameof(RuntimeFixedCreditGuardReadRequest) + " [REDACTED]";
}

/// <summary>Stored fixed-credit receipt and optional proof that the native call was not sent.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeFixedCreditGuardReadResult(
    RuntimeFixedCreditGuardReceipt? Receipt,
    RuntimeFixedCreditNoSendReceipt? Evidence);

/// <summary>Owner receipt explaining a native turn hard-limit denial and the dispatch state transition.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeCreditDenialReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    Guid SourceGrantId,
    Guid DispatchId,
    RuntimeCreditLimitDenialStage Stage,
    RuntimeCreditLimitDenialCode Code,
    string RegistrationHash,
    string SourceHash,
    string DispatchHash,
    string CheckpointHash,
    decimal HardCreditLimit,
    decimal ObservedCredits,
    long LedgerEventCount,
    string LedgerDigest,
    string CostIdentity,
    long GuardRevisionBefore,
    long GuardRevisionAfter,
    string DispatchStateBefore,
    long DispatchRevisionBefore,
    string DispatchStateAfter,
    long DispatchRevisionAfter,
    DateTimeOffset RecordedAt,
    string CanonicalPayloadHash);

/// <summary>Exclusive result of beginning a native turn: either an admission or a denial.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnBeginResult(
    RuntimeNativeTurnAdmissionReceipt? Admission,
    RuntimeNativeCreditDenialReceipt? Denial);

/// <summary>Authenticated request to read a native-turn credit denial by dispatch and stage.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeCreditDenialReadRequest(
    RuntimeCredentialHttpRequest Authorization,
    Guid DispatchId,
    RuntimeCreditLimitDenialStage Stage)
{
    public override string ToString() => nameof(RuntimeNativeCreditDenialReadRequest) + " [REDACTED]";
}

/// <summary>Native-turn denial and optional evidence that no SDK turn was sent.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeCreditDenialReadResult(
    RuntimeNativeCreditDenialReceipt? Receipt,
    RuntimeNativeTurnNoSendReceipt? NoSend = null);

/// <summary>Evidence that a hard-limit-denied native turn emitted no session events.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnNoSendReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    Guid DispatchId,
    string DenialReceiptHash,
    string RegistrationHash,
    string SourceHash,
    string NativeSessionHash,
    string BeforeCursorHash,
    string AfterCursorHash,
    int ObservedEventCount,
    bool RangeComplete,
    DateTimeOffset VerifiedAt,
    string CanonicalPayloadHash);

/// <summary>Authenticated request to record no-send evidence for a native turn denial.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnNoSendEvidenceRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeNativeCreditDenialReceipt Denial,
    RuntimeNativeTurnNoSendReceipt Evidence)
{
    public override string ToString() => nameof(RuntimeNativeTurnNoSendEvidenceRequest) + " [REDACTED]";
}

/// <summary>Seals and validates owner receipts for hard-limit native-turn denial and no-send behavior.</summary>
public static class RuntimeNativeDenialContract
{
    /// <summary>Returns a denial receipt with a canonical hash over its content.</summary>
    public static RuntimeNativeCreditDenialReceipt SealDenial(RuntimeNativeCreditDenialReceipt receipt) =>
        receipt with { CanonicalPayloadHash = DenialPayloadHash(receipt) };

    /// <summary>Returns no-send evidence with a canonical hash over its content.</summary>
    public static RuntimeNativeTurnNoSendReceipt SealNoSend(RuntimeNativeTurnNoSendReceipt receipt) =>
        receipt with { CanonicalPayloadHash = NoSendPayloadHash(receipt) };

    /// <summary>Checks denial scope, source accounting, hard limit, dispatch revision, and canonical hashes.</summary>
    public static void ValidateDenial(
        RuntimeNativeCreditDenialReceipt receipt,
        RuntimeRegistration registration,
        RuntimeSdkSourceReceipt sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(sourceReceipt);
        RuntimeContractValidation.Validate(registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, sourceReceipt.Source);
        var binding = registration.Binding;
        var validDispatch = receipt.Stage switch
        {
            RuntimeCreditLimitDenialStage.Preparation =>
                receipt.DispatchStateBefore == receipt.DispatchStateAfter &&
                (receipt.DispatchStateBefore == "absent" &&
                    receipt.DispatchRevisionBefore == 0 && receipt.DispatchRevisionAfter == 0 ||
                 receipt.DispatchStateBefore == "prepared" &&
                    receipt.DispatchRevisionBefore >= 1 &&
                    receipt.DispatchRevisionAfter == receipt.DispatchRevisionBefore),
            RuntimeCreditLimitDenialStage.NativeTurnBegin =>
                receipt.DispatchStateBefore == "prepared" &&
                receipt.DispatchRevisionBefore >= 1 &&
                receipt.DispatchStateAfter == receipt.DispatchStateBefore &&
                receipt.DispatchRevisionAfter == receipt.DispatchRevisionBefore,
            _ => false
        };
        if (receipt.ContractVersion != 1 ||
            receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.ExecutionFence != binding.ExecutionFence ||
            receipt.SourceGrantId != sourceReceipt.SourceGrantId ||
            receipt.DispatchId == Guid.Empty ||
            !Enum.IsDefined(receipt.Stage) ||
            receipt.Code != RuntimeCreditLimitDenialCode.HardLimitReached ||
            binding.CopilotHardCreditLimit is not { } hardLimit ||
            receipt.HardCreditLimit != hardLimit ||
            receipt.ObservedCredits < hardLimit ||
            sourceReceipt.Source.SourceMode != "hosted-copilot" ||
            sourceReceipt.Source.MeterSource != Agentweaver.Abstractions.SdkMeterSources.CopilotNanoAiu ||
            sourceReceipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            sourceReceipt.RegistrationRevision != registration.Revision ||
            sourceReceipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, sourceReceipt.Source) ||
            receipt.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration) ||
            receipt.SourceHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, sourceReceipt.Source) ||
            receipt.SourceHash != sourceReceipt.CanonicalPayloadHash ||
            receipt.LedgerEventCount < 0 ||
            receipt.GuardRevisionBefore < 1 ||
            receipt.GuardRevisionBefore == long.MaxValue ||
            receipt.GuardRevisionAfter != receipt.GuardRevisionBefore + 1 ||
            !validDispatch ||
            receipt.RecordedAt == default)
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_invalid");
        RuntimeContractValidation.ValidateHash(receipt.DispatchHash);
        RuntimeContractValidation.ValidateHash(receipt.CheckpointHash);
        RuntimeContractValidation.ValidateHash(receipt.LedgerDigest);
        RuntimeContractValidation.ValidateHash(receipt.CostIdentity);
        if (receipt.DispatchHash != DispatchHash(receipt.DispatchId, receipt.RegistrationHash,
                receipt.SourceHash, receipt.CheckpointHash, receipt.DispatchStateBefore,
                receipt.DispatchRevisionBefore) ||
            receipt.CanonicalPayloadHash != DenialPayloadHash(receipt))
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_invalid");
    }

    /// <summary>Requires a completed native-turn denial to be backed by a complete empty event range.</summary>
    public static void ValidateNoSend(
        RuntimeNativeTurnNoSendReceipt evidence,
        RuntimeNativeCreditDenialReceipt denial,
        RuntimeRegistration registration,
        RuntimeSdkSourceReceipt sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateDenial(denial, registration, sourceReceipt);
        if (denial.Stage != RuntimeCreditLimitDenialStage.NativeTurnBegin ||
            evidence.ContractVersion != 1 ||
            evidence.RuntimeInstanceId != denial.RuntimeInstanceId ||
            evidence.RegistrationRevision != denial.RegistrationRevision ||
            evidence.DispatchId != denial.DispatchId ||
            evidence.DenialReceiptHash != denial.CanonicalPayloadHash ||
            evidence.RegistrationHash != denial.RegistrationHash ||
            evidence.SourceHash != denial.SourceHash ||
            evidence.NativeSessionHash != RuntimeContractValidation.Hash(
                Encoding.UTF8.GetBytes(sourceReceipt.Source.SdkSessionId)) ||
            evidence.ObservedEventCount != 0 ||
            !evidence.RangeComplete ||
            evidence.BeforeCursorHash != evidence.AfterCursorHash ||
            evidence.VerifiedAt == default ||
            evidence.CanonicalPayloadHash != NoSendPayloadHash(evidence))
            throw new RuntimeAuthorizationException("runtime_native_turn_no_send_invalid");
        RuntimeContractValidation.ValidateHash(evidence.NativeSessionHash);
        RuntimeContractValidation.ValidateHash(evidence.BeforeCursorHash);
        RuntimeContractValidation.ValidateHash(evidence.AfterCursorHash);
    }

    /// <summary>Checks a read result for the requested dispatch and denial stage.</summary>
    public static void ValidateReadResult(
        RuntimeNativeCreditDenialReadResult result,
        RuntimeRegistration registration,
        RuntimeSdkSourceReceipt sourceReceipt,
        Guid dispatchId,
        RuntimeCreditLimitDenialStage stage)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Receipt is { } denial)
        {
            ValidateDenial(denial, registration, sourceReceipt);
            if (denial.DispatchId != dispatchId || denial.Stage != stage)
                throw new RuntimeAuthorizationException("runtime_native_credit_denial_conflict");
        }
        if (result.NoSend is { } evidence)
        {
            if (result.Receipt is not { } nativeDenial ||
                stage != RuntimeCreditLimitDenialStage.NativeTurnBegin ||
                nativeDenial.Stage != RuntimeCreditLimitDenialStage.NativeTurnBegin)
                throw new RuntimeAuthorizationException("runtime_native_credit_denial_read_invalid");
            ValidateNoSend(evidence, nativeDenial, registration, sourceReceipt);
        }
    }

    /// <summary>Validates the authenticated evidence request before it reaches the owner.</summary>
    public static void ValidateRequest(
        RuntimeNativeTurnNoSendEvidenceRequest request,
        RuntimeRegistration registration,
        RuntimeSdkSourceReceipt sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ValidateNoSend(request.Evidence, request.Denial, registration, sourceReceipt);
    }

    /// <summary>Validates the identifiers and stage in a denial read request.</summary>
    public static void ValidateReadRequest(RuntimeNativeCreditDenialReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        if (request.DispatchId == Guid.Empty || !Enum.IsDefined(request.Stage))
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_request_invalid");
    }

    /// <summary>Requires exactly one admission or denial and checks its registration and message binding.</summary>
    public static void ValidateBeginResult(
        RuntimeNativeTurnBeginResult result,
        RuntimeRegistration registration,
        RuntimeSdkSourceReceipt sourceReceipt,
        RuntimeA2ASendRequest message)
    {
        ArgumentNullException.ThrowIfNull(result);
        if ((result.Admission is null) == (result.Denial is null))
            throw new RuntimeAuthorizationException("runtime_native_turn_begin_result_invalid");
        if (result.Admission is { } admission)
            RuntimeNativeTurnContract.ValidateAdmission(admission, registration, sourceReceipt.Source, message);
        else
        {
            ValidateDenial(result.Denial!, registration, sourceReceipt);
            if (result.Denial!.Stage != RuntimeCreditLimitDenialStage.NativeTurnBegin ||
                result.Denial.DispatchId != message.Message?.MessageId)
                throw new RuntimeAuthorizationException("runtime_native_turn_begin_result_invalid");
        }
    }

    /// <summary>Hashes the dispatch's identity, source, checkpoint, and state revision.</summary>
    public static string DispatchHash(
        Guid dispatchId, string registrationHash, string sourceHash,
        string checkpointHash, string state, long revision) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new { dispatchId, registrationHash, sourceHash, checkpointHash, state, revision }));

    private static string DenialPayloadHash(RuntimeNativeCreditDenialReceipt receipt) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            receipt with { CanonicalPayloadHash = string.Empty }));

    private static string NoSendPayloadHash(RuntimeNativeTurnNoSendReceipt receipt) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            receipt with { CanonicalPayloadHash = string.Empty }));
}

/// <summary>Seals and validates denials for a configured zero-credit hard limit.</summary>
public static class RuntimeFixedCreditGuardContract
{
    /// <summary>Returns a guard receipt with a canonical digest of all receipt fields.</summary>
    public static RuntimeFixedCreditGuardReceipt Seal(
        RuntimeFixedCreditGuardReceipt receipt) =>
        receipt with { CanonicalPayloadHash = GuardPayloadHash(receipt) };

    /// <summary>Returns no-send evidence with a canonical digest of all evidence fields.</summary>
    public static RuntimeFixedCreditNoSendReceipt Seal(
        RuntimeFixedCreditNoSendReceipt evidence) =>
        evidence with { CanonicalPayloadHash = EvidencePayloadHash(evidence) };

    /// <summary>Hashes the non-secret host request fields that determine a queued action.</summary>
    public static string HashHostRequest(RuntimeA2ASendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = request.Message
            ?? throw new ArgumentException("A host message is required.", nameof(request));
        var metadata = message.Metadata
            ?? throw new ArgumentException("Host message metadata is required.", nameof(request));
        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            message.Kind,
            message.MessageId,
            message.ContextId,
            message.Role,
            message.Parts,
            metadata.Runtime.Registration,
            metadata.Runtime.Purpose,
            metadata.DeliveryMode
        }));
    }

    /// <summary>Validates a zero-limit receipt against its registration and request hash.</summary>
    public static void ValidateReceipt(RuntimeFixedCreditGuardReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(receipt.Registration);
        RuntimeContractValidation.Validate(receipt.Registration);
        var registration = receipt.Registration;
        var sourceParts = (receipt.SourceHash is not null ? 1 : 0) +
            (receipt.SourceGrantId is not null ? 1 : 0) +
            (receipt.SourceGrantRevision is not null ? 1 : 0);
        if (receipt.ContractVersion != 1 ||
            !Enum.IsDefined(receipt.Stage) ||
            receipt.Code != RuntimeFixedCreditGuardCode.HardLimitZero ||
            registration.Binding.ModelSourceMode != ModelSourceMode.HostedCopilot ||
            registration.Binding.CopilotHardCreditLimit is not 0m ||
            receipt.RequestId == Guid.Empty ||
            receipt.RecordedAt == default ||
            receipt.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration) ||
            sourceParts is not (0 or 3) ||
            receipt.Stage == RuntimeFixedCreditGuardStage.HostPreQueue && sourceParts != 3 ||
            receipt.SourceGrantId == Guid.Empty ||
            receipt.SourceGrantRevision is <= 0)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_invalid");
        RuntimeContractValidation.ValidateHash(receipt.RequestHash);
        if (receipt.SourceHash is not null)
            RuntimeContractValidation.ValidateHash(receipt.SourceHash);
        if (receipt.CanonicalPayloadHash != GuardPayloadHash(receipt))
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_invalid");
    }

    /// <summary>Requires a complete empty native event range tied to the denied host request.</summary>
    public static void ValidateEvidence(
        RuntimeFixedCreditNoSendReceipt evidence,
        RuntimeFixedCreditGuardReceipt receipt,
        RuntimeRegistration registration,
        RuntimeGrantReceipt grant,
        RuntimeSdkSourceReceipt sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(sourceReceipt);
        ValidateSourceTuple(receipt, registration, grant, sourceReceipt);
        var sourceHash = sourceReceipt.CanonicalPayloadHash;
        if (receipt.Stage != RuntimeFixedCreditGuardStage.HostPreQueue ||
            evidence.ContractVersion != 1 ||
            evidence.RuntimeInstanceId != registration.RuntimeInstanceId ||
            evidence.RegistrationRevision != registration.Revision ||
            evidence.ExecutionFence != registration.Binding.ExecutionFence ||
            evidence.RequestId != receipt.RequestId ||
            evidence.Stage != receipt.Stage ||
            evidence.GuardReceiptHash != receipt.CanonicalPayloadHash ||
            evidence.RegistrationHash != receipt.RegistrationHash ||
            evidence.SourceHash != sourceHash ||
            evidence.SourceGrantId != grant.GrantId ||
            evidence.SourceGrantRevision != grant.Revision ||
            evidence.NativeSessionHash != RuntimeContractValidation.Hash(
                Encoding.UTF8.GetBytes(sourceReceipt.Source.SdkSessionId)) ||
            evidence.BeforeCursorHash != evidence.AfterCursorHash ||
            evidence.ObservedEventCount != 0 ||
            !evidence.RangeComplete ||
            evidence.VerifiedAt == default ||
            evidence.CanonicalPayloadHash != EvidencePayloadHash(evidence))
            throw new RuntimeAuthorizationException("runtime_fixed_credit_no_send_invalid");
        RuntimeContractValidation.ValidateHash(evidence.NativeSessionHash);
        RuntimeContractValidation.ValidateHash(evidence.BeforeCursorHash);
        RuntimeContractValidation.ValidateHash(evidence.AfterCursorHash);
    }

    /// <summary>Requires receipt, registration, grant, and source hashes to describe one exact authority tuple.</summary>
    public static void ValidateSourceTuple(
        RuntimeFixedCreditGuardReceipt receipt,
        RuntimeRegistration registration,
        RuntimeGrantReceipt grant,
        RuntimeSdkSourceReceipt sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(sourceReceipt);
        ValidateReceipt(receipt);
        RuntimeContractValidation.Validate(registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, sourceReceipt.Source);
        var sourceHash = RuntimeUsageSourceReceiptContract.HashSource(registration, sourceReceipt.Source);
        if (sourceReceipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            sourceReceipt.RegistrationRevision != registration.Revision ||
            sourceReceipt.SourceGrantId != grant.GrantId ||
            sourceReceipt.CanonicalPayloadHash != sourceHash ||
            grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision ||
            receipt.Registration != registration ||
            receipt.SourceHash != sourceHash ||
            receipt.SourceGrantId != grant.GrantId ||
            receipt.SourceGrantRevision != grant.Revision)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
    }

    /// <summary>Validates the scope and identity fields of a fixed-credit receipt read request.</summary>
    public static void ValidateReadRequest(RuntimeFixedCreditGuardReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RuntimeInstanceId == Guid.Empty ||
            request.RegistrationRevision <= 0 ||
            request.ExecutionFence <= 0 ||
            request.RequestId == Guid.Empty ||
            !Enum.IsDefined(request.Stage))
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_request_invalid");
        RuntimeContractValidation.ValidateHash(request.RequestHash);
    }

    /// <summary>Matches a returned receipt and optional evidence to the registration and read request.</summary>
    public static void ValidateReadResult(
        RuntimeFixedCreditGuardReadResult result,
        RuntimeRegistration registration,
        RuntimeFixedCreditGuardReadRequest request,
        RuntimeGrantReceipt? grant = null,
        RuntimeSdkSourceReceipt? sourceReceipt = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(request);
        if (result.Receipt is not { } receipt)
        {
            if (result.Evidence is not null)
                throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_read_invalid");
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_read_invalid");
        }
        ValidateReceipt(receipt);
        if (receipt.Registration != registration ||
            receipt.Registration.RuntimeInstanceId != request.RuntimeInstanceId ||
            receipt.Registration.Revision != request.RegistrationRevision ||
            receipt.Registration.Binding.ExecutionFence != request.ExecutionFence ||
            receipt.Stage != request.Stage ||
            receipt.RequestId != request.RequestId ||
            receipt.RequestHash != request.RequestHash)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        if (result.Evidence is { } evidence)
        {
            if (grant is null || sourceReceipt is null)
                throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_read_invalid");
            ValidateEvidence(evidence, receipt, registration, grant, sourceReceipt);
        }
    }

    private static string GuardPayloadHash(RuntimeFixedCreditGuardReceipt receipt) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            receipt with { CanonicalPayloadHash = string.Empty }));

    private static string EvidencePayloadHash(RuntimeFixedCreditNoSendReceipt evidence) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            evidence with { CanonicalPayloadHash = string.Empty }));
}
