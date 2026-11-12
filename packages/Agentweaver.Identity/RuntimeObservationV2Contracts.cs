using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeObservationFactKind>))]
public enum RuntimeObservationFactKind
{
    CoverageStarted,
    ModelTurnReserved,
    ToolInvocationReserved,
    Denied,
    CreditGuardChanged,
    NativeNoSend,
    ToolPermissionRejected
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeToolPermissionRejectionRequest(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] Guid RuntimeInstanceId,
    [property: JsonRequired] long RegistrationRevision,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] Guid AttemptId,
    [property: JsonRequired] Guid DispatchId,
    [property: JsonRequired] string RequestHash,
    [property: JsonRequired] string ReasonCode)
{
    public RuntimeNativeToolPermissionRejectionRequest Validate()
    {
        if (ContractVersion != 1 || RuntimeInstanceId == Guid.Empty ||
            RegistrationRevision < 1 || ExecutionFence < 1 ||
            AttemptId == Guid.Empty || DispatchId == Guid.Empty ||
            ReasonCode != RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode)
            throw new RuntimeAuthorizationException("runtime_native_tool_rejection_invalid");
        RuntimeContractValidation.ValidateHash(RequestHash);
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeNativeToolPermissionRejectionAcknowledgement(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] Guid FactId,
    [property: JsonRequired] string RequestHash)
{
    public RuntimeNativeToolPermissionRejectionAcknowledgement Validate(
        RuntimeNativeToolPermissionRejectionRequest request)
    {
        if (ContractVersion != 1 || FactId == Guid.Empty || RequestHash != request.RequestHash)
            throw new RuntimeAuthorizationException("runtime_native_tool_rejection_ack_invalid");
        return this;
    }
}

public static class RuntimeNativeToolPermissionRejectionContract
{
    public const string NumericToolLimitReasonCode = "runtime_tool_call_limit_exhausted";
    public const string RecordPath = "/internal/runtime/actions/tool-permission-rejections";

    public static void Validate(RuntimeNativeToolPermissionRejectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = request.Validate();
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeObservationLimitKind>))]
public enum RuntimeObservationLimitKind
{
    ModelTurns,
    ToolCalls,
    CopilotHardCredits
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationEvidenceLink(
    [property: JsonRequired] RuntimeObservationSectionState State,
    [property: JsonRequired] string? Reason,
    [property: JsonRequired] Guid? ReceiptId,
    [property: JsonRequired] string? PayloadHash,
    [property: JsonRequired] bool? RangeComplete,
    [property: JsonRequired] long? EventCount)
{
    public RuntimeObservationEvidenceLink Validate()
    {
        if (!Enum.IsDefined(State) ||
            (State == RuntimeObservationSectionState.Available) != (ReceiptId is not null) ||
            State == RuntimeObservationSectionState.Available && string.IsNullOrWhiteSpace(PayloadHash) ||
            State != RuntimeObservationSectionState.Available && string.IsNullOrWhiteSpace(Reason) ||
            EventCount is < 0 ||
            RangeComplete is not null && EventCount is null)
            throw new RuntimeAuthorizationException("runtime_observation_v2_evidence_invalid");
        if (PayloadHash is not null)
            RuntimeContractValidation.ValidateHash(PayloadHash);
        return this;
    }

    public static RuntimeObservationEvidenceLink Unavailable(string reason) =>
        new(RuntimeObservationSectionState.Unavailable, reason, null, null, null, null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV2Fact(
    [property: JsonRequired] Guid FactId,
    [property: JsonRequired] long? Sequence,
    [property: JsonRequired] RuntimeObservationFactKind Kind,
    [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] Guid? AttemptId,
    [property: JsonRequired] Guid? DispatchId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] Guid? RuntimeInstanceId,
    [property: JsonRequired] long? RegistrationRevision,
    [property: JsonRequired] string AcceptedSelectionHash,
    [property: JsonRequired] long? OwnerRevision,
    [property: JsonRequired] RuntimeObservationLimitKind? LimitKind,
    [property: JsonRequired] decimal? ConfiguredValue,
    [property: JsonRequired] decimal? ObservedValue,
    [property: JsonRequired] string? Unit,
    [property: JsonRequired] string? Scope,
    [property: JsonRequired] string? Stage,
    [property: JsonRequired] string? ReasonCode,
    [property: JsonRequired] long? ConsumedCount,
    [property: JsonRequired] long? ReservedCount,
    [property: JsonRequired] DateTimeOffset RecordedAt,
    [property: JsonRequired] RuntimeObservationEvidenceLink OwnerDecision,
    [property: JsonRequired] RuntimeObservationEvidenceLink NativeNoSend,
    [property: JsonRequired] RuntimeObservationEvidenceLink ToolPermission,
    [property: JsonRequired] RuntimeObservationEvidenceLink ToolExecution)
{
    [JsonRequired]
    public long? AccountedThroughLedgerEventCount { get; init; }

    [JsonRequired]
    public bool? SoftWarningEmitted { get; init; }

    [JsonRequired]
    public bool? HardCapExhausted { get; init; }

    [JsonRequired]
    public string? CheckpointId { get; init; }

    [JsonRequired]
    public long? CheckpointRevision { get; init; }

    [JsonRequired]
    public string? CheckpointHash { get; init; }

    [JsonRequired]
    public long? CheckpointDecisionStateVersion { get; init; }

    [JsonRequired]
    public string? WorkPlanId { get; init; }

    [JsonRequired]
    public string? AssociationId { get; init; }

    public RuntimeObservationV2Fact Validate()
    {
        var isCreditGuard = Kind == RuntimeObservationFactKind.CreditGuardChanged;
        var requiresLimit = Kind is RuntimeObservationFactKind.Denied or
            RuntimeObservationFactKind.NativeNoSend or RuntimeObservationFactKind.ToolPermissionRejected;
        var hasToolPermissionEvidence = ToolPermission.State == RuntimeObservationSectionState.Available;
        var noDispatchRevision = Kind == RuntimeObservationFactKind.Denied &&
            LimitKind == RuntimeObservationLimitKind.CopilotHardCredits &&
            Stage == "childPreparation" && OwnerRevision is null;
        var hasCheckpoint = CheckpointId is not null;
        var checkpointFieldsInvalid = hasCheckpoint != (CheckpointRevision is not null) ||
            hasCheckpoint != (CheckpointHash is not null) ||
            hasCheckpoint != (CheckpointDecisionStateVersion is not null) ||
            hasCheckpoint != (WorkPlanId is not null) ||
            hasCheckpoint != (AssociationId is not null) ||
            CheckpointRevision is < 1 || CheckpointDecisionStateVersion is < 1;
        var limitFieldsInvalid = isCreditGuard
            ? LimitKind is not null || ConfiguredValue is not null ||
                Unit != "nanoAiu" || Scope != "copilotRun" || ObservedValue is null
            : (LimitKind is null) != (Unit is null) ||
                (LimitKind is null) != (Scope is null) ||
                LimitKind is null && (ConfiguredValue is not null || ObservedValue is not null) ||
                requiresLimit && (LimitKind is null || ConfiguredValue is null);
        if (FactId == Guid.Empty || Sequence is < 0 || !Enum.IsDefined(Kind) ||
            ExecutionFence < 1 || RegistrationRevision is < 1 ||
            (Kind != RuntimeObservationFactKind.CoverageStarted && OwnerRevision is null && !noDispatchRevision) ||
            OwnerRevision is < 1 ||
            ConfiguredValue < 0 || ObservedValue < 0 || ConsumedCount is < 0 ||
            ReservedCount is < 0 || RecordedAt == default ||
            (RuntimeInstanceId == Guid.Empty) ||
            (isCreditGuard
                ? AccountedThroughLedgerEventCount is null or < 0 ||
                    SoftWarningEmitted is null || HardCapExhausted is null
                : AccountedThroughLedgerEventCount is not null ||
                    SoftWarningEmitted is not null || HardCapExhausted is not null) ||
            checkpointFieldsInvalid ||
            limitFieldsInvalid ||
            Kind == RuntimeObservationFactKind.CoverageStarted &&
                (AttemptId is not null || DispatchId is not null || LimitKind is not null ||
                 ConfiguredValue is not null || ObservedValue is not null ||
                 OwnerDecision.State != RuntimeObservationSectionState.Available) ||
            Kind == RuntimeObservationFactKind.NativeNoSend && NativeNoSend.State != RuntimeObservationSectionState.Available)
            throw new RuntimeAuthorizationException("runtime_observation_v2_fact_invalid");
        if (hasToolPermissionEvidence &&
                Kind is not (RuntimeObservationFactKind.ToolInvocationReserved or
                    RuntimeObservationFactKind.ToolPermissionRejected) ||
            Kind == RuntimeObservationFactKind.ToolPermissionRejected &&
                (AttemptId is null || AttemptId == Guid.Empty ||
                 DispatchId is null || DispatchId == Guid.Empty ||
                 LimitKind != RuntimeObservationLimitKind.ToolCalls ||
                 Stage != "toolPermission" ||
                 ReasonCode != RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode ||
                 ToolPermission.State != RuntimeObservationSectionState.Available ||
                 OwnerDecision.State != RuntimeObservationSectionState.Available ||
                 NativeNoSend.State != RuntimeObservationSectionState.Unavailable ||
                 ToolExecution.State != RuntimeObservationSectionState.Unavailable))
            throw new RuntimeAuthorizationException("runtime_observation_v2_fact_invalid");
        RuntimeContractValidation.ValidateIdentifier(SessionId);
        RuntimeContractValidation.ValidateIdentifier(ProjectId);
        RuntimeContractValidation.ValidateIdentifier(RunId);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        if (hasCheckpoint)
        {
            RuntimeContractValidation.ValidateIdentifier(CheckpointId!);
            RuntimeContractValidation.ValidateHash(CheckpointHash!);
            RuntimeContractValidation.ValidateIdentifier(WorkPlanId!);
            RuntimeContractValidation.ValidateIdentifier(AssociationId!);
        }
        foreach (var value in new[] { Unit, Scope, Stage, ReasonCode })
            if (value is not null)
                RuntimeContractValidation.ValidateIdentifier(value);
        _ = OwnerDecision.Validate();
        _ = NativeNoSend.Validate();
        _ = ToolPermission.Validate();
        _ = ToolExecution.Validate();
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationCount(
    [property: JsonRequired] RuntimeObservationSectionState State,
    [property: JsonRequired] long? Value,
    [property: JsonRequired] string? Reason)
{
    public RuntimeObservationCount Validate()
    {
        if (!Enum.IsDefined(State) ||
            (State == RuntimeObservationSectionState.Available) != (Value is not null) ||
            State == RuntimeObservationSectionState.Available && (Value < 0 || Reason is not null) ||
            State != RuntimeObservationSectionState.Available && (Value is not null || string.IsNullOrWhiteSpace(Reason)))
            throw new RuntimeAuthorizationException("runtime_observation_v2_count_invalid");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV2Binding(
    [property: JsonRequired] string ProjectId,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] string RootSessionId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] bool IsRoot,
    [property: JsonRequired] string AcceptedSelectionHash,
    [property: JsonRequired] long CurrentExecutionFence,
    [property: JsonRequired] long SessionExecutionFence,
    [property: JsonRequired] Guid? RuntimeInstanceId,
    [property: JsonRequired] long? RegistrationRevision,
    [property: JsonRequired] DateTimeOffset ObservedAt)
{
    public RuntimeObservationV2Binding Validate()
    {
        if (CurrentExecutionFence < 1 || SessionExecutionFence < 1 ||
            RegistrationRevision is < 1 || RuntimeInstanceId == Guid.Empty || ObservedAt == default ||
            (RuntimeInstanceId is null) != (RegistrationRevision is null))
            throw new RuntimeAuthorizationException("runtime_observation_v2_binding_invalid");
        RuntimeContractValidation.ValidateIdentifier(ProjectId);
        RuntimeContractValidation.ValidateIdentifier(RunId);
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateIdentifier(SessionId);
        RuntimeContractValidation.ValidateHash(AcceptedSelectionHash);
        if (IsRoot != (RootSessionId == SessionId))
            throw new RuntimeAuthorizationException("runtime_observation_v2_binding_invalid");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV2Limits(
    [property: JsonRequired] int? MaxModelTurns,
    [property: JsonRequired] int? MaxToolCalls,
    [property: JsonRequired] int? MaxPromptTokens,
    [property: JsonRequired] decimal? CopilotSoftCreditLimit,
    [property: JsonRequired] decimal? CopilotHardCreditLimit,
    [property: JsonRequired] string CreditUnit,
    [property: JsonRequired] string CreditScope)
{
    public RuntimeObservationV2Limits Validate()
    {
        if (MaxModelTurns is < 0 || MaxToolCalls is < 0 || MaxPromptTokens is < 0 ||
            CopilotSoftCreditLimit < 0 || CopilotHardCreditLimit < 0 ||
            CopilotSoftCreditLimit is { } soft && CopilotHardCreditLimit is { } hard && soft > hard ||
            CreditUnit != "nanoAiu" || CreditScope != "copilotRun")
            throw new RuntimeAuthorizationException("runtime_observation_v2_limits_invalid");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV2Totals(
    [property: JsonRequired] RuntimeObservationCount ModelSlotsReserved,
    [property: JsonRequired] RuntimeObservationCount ModelSlotsConsumed,
    [property: JsonRequired] RuntimeObservationCount NativeSendsKnown,
    [property: JsonRequired] RuntimeObservationCount NativeSendsUncertain,
    [property: JsonRequired] RuntimeObservationCount ToolSlotsReserved,
    [property: JsonRequired] RuntimeObservationCount ToolSlotsConsumed)
{
    public RuntimeObservationV2Totals Validate()
    {
        _ = ModelSlotsReserved.Validate();
        _ = ModelSlotsConsumed.Validate();
        _ = NativeSendsKnown.Validate();
        _ = NativeSendsUncertain.Validate();
        _ = ToolSlotsReserved.Validate();
        _ = ToolSlotsConsumed.Validate();
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV2History(
    [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] long HighWatermark,
    [property: JsonRequired] bool Complete,
    [property: JsonRequired] DateTimeOffset? CoverageStart,
    [property: JsonRequired] string? IncompleteReason,
    [property: JsonRequired] bool ExactAttempt,
    [property: JsonRequired] IReadOnlyList<RuntimeObservationV2Fact> Items,
    [property: JsonRequired] string? NextCursor,
    [property: JsonRequired] string? ExactUnavailableReason)
{
    public const int MaximumItems = 100;

    public RuntimeObservationV2History Validate()
    {
        if (SnapshotId == Guid.Empty || HighWatermark < 0 ||
            Complete != (IncompleteReason is null) ||
            Items is null || Items.Count > MaximumItems ||
            Items.Any(item => item is null) ||
            Items.Select(item => item.FactId).Distinct().Count() != Items.Count ||
            ExactUnavailableReason is not null &&
                (!ExactAttempt || string.IsNullOrWhiteSpace(ExactUnavailableReason)) ||
            ExactUnavailableReason == "attempt_history_exceeds_bounded_aggregate" && NextCursor is null ||
            ExactAttempt && Items.Count == 0 && ExactUnavailableReason is null ||
            Items.Any(item => ExactAttempt
                ? item.Sequence.HasValue &&
                    (item.Sequence.Value <= 0 || item.Sequence.Value > HighWatermark)
                : !item.Sequence.HasValue || item.Sequence.Value <= 0 || item.Sequence.Value > HighWatermark) ||
            Items.Zip(Items.Skip(1), (left, right) =>
                left.Sequence is not null && right.Sequence is not null && left.Sequence >= right.Sequence)
                .Any(isOutOfOrder => isOutOfOrder) ||
            NextCursor is { Length: > 4096 })
            throw new RuntimeAuthorizationException("runtime_observation_v2_history_invalid");
        foreach (var item in Items)
            _ = item.Validate();
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV2(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] RuntimeObservationV2Binding Binding,
    [property: JsonRequired] RuntimeObservationV2Limits AcceptedLimits,
    [property: JsonRequired] RuntimeObservationV2Totals Totals,
    [property: JsonRequired] RuntimeObservationSection<RuntimeObservationCreditGuard> CreditGuard,
    [property: JsonRequired] RuntimeObservationV2History History)
{
    public RuntimeObservationV2 Validate()
    {
        if (ContractVersion != 2 || Binding is null || AcceptedLimits is null ||
            Totals is null || CreditGuard is null || History is null)
            throw new RuntimeAuthorizationException("runtime_observation_v2_invalid");
        _ = Binding.Validate();
        _ = AcceptedLimits.Validate();
        _ = Totals.Validate();
        _ = CreditGuard.Validate();
        if (CreditGuard.Value is not null)
            _ = CreditGuard.Value.Validate();
        _ = History.Validate();
        return this;
    }
}
