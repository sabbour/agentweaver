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
    [property: JsonRequired] RuntimeNumericLimitDenialReceipt? Receipt);

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
        return result;
    }
}

/// <summary>Authenticated current-owner identity used to request a recovery comparison.</summary>
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
    [property: JsonRequired] IReadOnlyList<string> MissingProvenance)
{
    /// <summary>Validates correlation identity and explicit missing-provenance reporting.</summary>
    public RuntimeReplacementComparisonBlocker Validate()
    {
        if (Status != "blocked" || DispatchId == Guid.Empty || CheckpointRevision < 1 || ExecutionFence < 1 ||
            MissingProvenance is null || MissingProvenance.Count == 0 ||
            MissingProvenance.Distinct(StringComparer.Ordinal).Count() != MissingProvenance.Count)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_invalid");
        RuntimeContractValidation.ValidateIdentifier(InvocationId);
        RuntimeContractValidation.ValidateIdentifier(CheckpointId);
        RuntimeContractValidation.ValidateIdentifier(WorkPlanId);
        RuntimeContractValidation.ValidateIdentifier(AssociationId);
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
            result.ExecutionFence != request.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_conflict");
        return result.Validate();
    }
}
