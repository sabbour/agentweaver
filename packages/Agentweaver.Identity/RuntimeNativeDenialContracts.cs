using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public enum RuntimeCreditLimitDenialStage { Preparation, NativeTurnBegin }

public enum RuntimeCreditLimitDenialCode { HardLimitReached }

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnBeginResult(
    RuntimeNativeTurnAdmissionReceipt? Admission,
    RuntimeNativeCreditDenialReceipt? Denial);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeCreditDenialReadRequest(
    RuntimeCredentialHttpRequest Authorization,
    Guid DispatchId,
    RuntimeCreditLimitDenialStage Stage)
{
    public override string ToString() => nameof(RuntimeNativeCreditDenialReadRequest) + " [REDACTED]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeCreditDenialReadResult(RuntimeNativeCreditDenialReceipt? Receipt);

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeTurnNoSendEvidenceRequest(
    RuntimeCredentialHttpRequest Authorization,
    RuntimeNativeCreditDenialReceipt Denial,
    RuntimeNativeTurnNoSendReceipt Evidence)
{
    public override string ToString() => nameof(RuntimeNativeTurnNoSendEvidenceRequest) + " [REDACTED]";
}

public static class RuntimeNativeDenialContract
{
    public static RuntimeNativeCreditDenialReceipt SealDenial(RuntimeNativeCreditDenialReceipt receipt) =>
        receipt with { CanonicalPayloadHash = DenialPayloadHash(receipt) };

    public static RuntimeNativeTurnNoSendReceipt SealNoSend(RuntimeNativeTurnNoSendReceipt receipt) =>
        receipt with { CanonicalPayloadHash = NoSendPayloadHash(receipt) };

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

    public static void ValidateRequest(
        RuntimeNativeTurnNoSendEvidenceRequest request,
        RuntimeRegistration registration,
        RuntimeSdkSourceReceipt sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ValidateNoSend(request.Evidence, request.Denial, registration, sourceReceipt);
    }

    public static void ValidateReadRequest(RuntimeNativeCreditDenialReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        if (request.DispatchId == Guid.Empty || !Enum.IsDefined(request.Stage))
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_request_invalid");
    }

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
