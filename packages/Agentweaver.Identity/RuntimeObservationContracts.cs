using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

/// <summary>Availability state for one optional section of a runtime observation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeObservationSectionState>))]
public enum RuntimeObservationSectionState
{
    Available,
    Unknown,
    Unavailable
}

/// <summary>Registration lifecycle state exposed to an observer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeObservationRegistrationState>))]
public enum RuntimeObservationRegistrationState
{
    Active,
    Revoked
}

/// <summary>
/// Optional observation section whose state distinguishes known absence/unavailability from
/// an available value. Available sections must contain a value and other states must not.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationSection<T>(
    [property: JsonRequired] RuntimeObservationSectionState State,
    [property: JsonRequired] T? Value)
    where T : class
{
    /// <summary>Checks the closed state enum and value/state consistency rule.</summary>
    public RuntimeObservationSection<T> Validate()
    {
        if (!Enum.IsDefined(State) ||
            (State == RuntimeObservationSectionState.Available) != (Value is not null))
            throw new RuntimeAuthorizationException("runtime_observation_section_invalid");
        return this;
    }
}

/// <summary>Minimal owner registration state and expiry included in runtime observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationRegistration(
    [property: JsonRequired] Guid RuntimeInstanceId,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] RuntimeObservationRegistrationState State,
    [property: JsonRequired] DateTimeOffset ExpiresAt)
{
    /// <summary>Checks identity, positive revision, lifecycle state, and expiry.</summary>
    public RuntimeObservationRegistration Validate()
    {
        if (RuntimeInstanceId == Guid.Empty || Revision < 1 ||
            !Enum.IsDefined(State) || ExpiresAt == default)
            throw new RuntimeAuthorizationException("runtime_observation_registration_invalid");
        return this;
    }
}

/// <summary>Read-only cost guard totals and ledger position exposed in an observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationCreditGuard(
    [property: JsonRequired] decimal CopilotAicFloor,
    [property: JsonRequired] long AccountedThroughLedgerEventCount,
    [property: JsonRequired] string LedgerDigest,
    [property: JsonRequired] long GuardRevision,
    [property: JsonRequired] bool SoftWarningEmitted,
    [property: JsonRequired] bool HardCapExhausted,
    [property: JsonRequired] DateTimeOffset UpdatedAt)
{
    /// <summary>Checks nonnegative totals, positive revision, timestamp, and ledger digest.</summary>
    public RuntimeObservationCreditGuard Validate()
    {
        if (CopilotAicFloor < 0 || AccountedThroughLedgerEventCount < 0 ||
            GuardRevision < 1 || UpdatedAt == default)
            throw new RuntimeAuthorizationException("runtime_observation_credit_guard_invalid");
        RuntimeContractValidation.ValidateHash(LedgerDigest);
        return this;
    }
}

/// <summary>One bounded owner dispatch record, including optional report and accounting witness state.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationDispatch(
    [property: JsonRequired] Guid DispatchId,
    [property: JsonRequired] string RootSessionId,
    [property: JsonRequired] string ChildSessionId,
    [property: JsonRequired] string WorkPlanId,
    [property: JsonRequired] string AssociationId,
    [property: JsonRequired] string CheckpointId,
    [property: JsonRequired] long CheckpointRevision,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] long OwnerRevision,
    [property: JsonRequired] string State,
    [property: JsonRequired] string? ReportState,
    [property: JsonRequired] string? AccountingState,
    [property: JsonRequired] long? WitnessWatermark,
    [property: JsonRequired] string? WitnessDigest,
    [property: JsonRequired] DateTimeOffset CreatedAt,
    [property: JsonRequired] DateTimeOffset UpdatedAt)
{
    /// <summary>Checks dispatch scope, revisions, timestamps, and optional witness digest.</summary>
    public RuntimeObservationDispatch Validate()
    {
        if (DispatchId == Guid.Empty || ExecutionFence < 1 || OwnerRevision < 1 ||
            CheckpointRevision < 1 || WitnessWatermark is < 0 ||
            (WitnessWatermark is null) != (WitnessDigest is null) ||
            CreatedAt == default || UpdatedAt == default)
            throw new RuntimeAuthorizationException("runtime_observation_dispatch_invalid");
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateIdentifier(ChildSessionId);
        RuntimeContractValidation.ValidateIdentifier(WorkPlanId);
        RuntimeContractValidation.ValidateIdentifier(AssociationId);
        RuntimeContractValidation.ValidateIdentifier(CheckpointId);
        ValidateState(State);
        if (ReportState is not null)
            ValidateState(ReportState);
        if (AccountingState is not null)
            ValidateState(AccountingState);
        if (WitnessDigest is not null)
            RuntimeContractValidation.ValidateHash(WitnessDigest);
        return this;
    }

    private static void ValidateState(string value)
    {
        RuntimeContractValidation.ValidateIdentifier(value);
        if (value.Length > 64)
            throw new RuntimeAuthorizationException("runtime_observation_dispatch_invalid");
    }
}

/// <summary>Bounded collection of unique observed dispatches and an explicit truncation flag.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationDispatchCollection(
    [property: JsonRequired] IReadOnlyList<RuntimeObservationDispatch> Items,
    [property: JsonRequired] bool Truncated)
{
    public const int MaximumItems = 100;

    /// <summary>Enforces the maximum count, uniqueness, truncation semantics, and item validation.</summary>
    public RuntimeObservationDispatchCollection Validate()
    {
        if (Items is null || Items.Count > MaximumItems ||
            Truncated && Items.Count != MaximumItems ||
            Items.Any(item => item is null) ||
            Items.Select(item => item.DispatchId).Distinct().Count() != Items.Count)
            throw new RuntimeAuthorizationException("runtime_observation_dispatches_invalid");
        foreach (var item in Items)
            _ = item.Validate();
        return this;
    }
}

/// <summary>One observed tool-reservation event associated with a session execution fence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationToolReservation(
    [property: JsonRequired] Guid EventId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] DateTimeOffset OccurredAt)
{
    /// <summary>Checks the event identity, session, fence, and recorded time.</summary>
    public RuntimeObservationToolReservation Validate()
    {
        if (EventId == Guid.Empty || ExecutionFence < 1 || OccurredAt == default)
            throw new RuntimeAuthorizationException("runtime_observation_tool_reservation_invalid");
        RuntimeContractValidation.ValidateIdentifier(SessionId);
        return this;
    }
}

/// <summary>Bounded collection of unique tool reservations and an explicit truncation flag.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationToolReservationCollection(
    [property: JsonRequired] IReadOnlyList<RuntimeObservationToolReservation> Items,
    [property: JsonRequired] bool Truncated)
{
    public const int MaximumItems = 100;

    /// <summary>Enforces the maximum count, uniqueness, truncation semantics, and item validation.</summary>
    public RuntimeObservationToolReservationCollection Validate()
    {
        if (Items is null || Items.Count > MaximumItems ||
            Truncated && Items.Count != MaximumItems ||
            Items.Any(item => item is null) ||
            Items.Select(item => item.EventId).Distinct().Count() != Items.Count)
            throw new RuntimeAuthorizationException("runtime_observation_tool_reservations_invalid");
        foreach (var item in Items)
            _ = item.Validate();
        return this;
    }
}

/// <summary>Identity of a native message that is currently awaiting execution observation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationPendingDispatch(
    [property: JsonRequired] Guid MessageId)
{
    /// <summary>Requires a nonempty message identity.</summary>
    public RuntimeObservationPendingDispatch Validate()
    {
        if (MessageId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_observation_execution_invalid");
        return this;
    }
}

/// <summary>Child execution status and checkpoint evidence exposed through the observation API.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationExecution(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] string RootSessionId,
    [property: JsonRequired] string SessionId,
    [property: JsonRequired] long ExecutionFence,
    [property: JsonRequired] string WorkPlanId,
    [property: JsonRequired] string AssociationId,
    [property: JsonRequired] long CheckpointRevision,
    [property: JsonRequired] string CheckpointId,
    [property: JsonRequired] long CheckpointDecisionStateVersion,
    [property: JsonRequired] long CurrentDecisionStateVersion,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeExecutionTaskStatus>))]
    [property: JsonRequired] RuntimeExecutionTaskStatus Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeObservationPendingDispatch? PendingDispatch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeExecutionResultDigest? ResultDigest { get; init; }

    /// <summary>Checks execution scope, checkpoint versions, and status-specific evidence.</summary>
    public RuntimeObservationExecution Validate()
    {
        if (ContractVersion != 1 || ExecutionFence < 1 || CheckpointRevision < 1 ||
            CheckpointDecisionStateVersion < 1 ||
            CheckpointDecisionStateVersion > CurrentDecisionStateVersion ||
            CurrentDecisionStateVersion < 1 || !Enum.IsDefined(Status) ||
            PendingDispatch is not null && ResultDigest is not null)
            throw new RuntimeAuthorizationException("runtime_observation_execution_invalid");
        RuntimeContractValidation.ValidateIdentifier(RootSessionId);
        RuntimeContractValidation.ValidateIdentifier(SessionId);
        RuntimeContractValidation.ValidateIdentifier(WorkPlanId);
        RuntimeContractValidation.ValidateIdentifier(AssociationId);
        RuntimeContractValidation.ValidateIdentifier(CheckpointId);
        if (PendingDispatch is { } pending &&
            (Status != RuntimeExecutionTaskStatus.Running || pending.MessageId == Guid.Empty))
            throw new RuntimeAuthorizationException("runtime_observation_execution_invalid");
        if (ResultDigest is { } result &&
            (Status != RuntimeExecutionTaskStatus.Succeeded || result.Utf8ByteLength < 0))
            throw new RuntimeAuthorizationException("runtime_observation_execution_invalid");
        if (ResultDigest is { } digest)
            RuntimeContractValidation.ValidateHash(digest.Sha256);
        return this;
    }
}

/// <summary>Version 2 execution observation with explicit parent-checkpoint metadata.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationExecutionV2(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] RuntimeObservationExecution Observation,
    [property: JsonRequired] RuntimeExecutionCheckpointParent CheckpointParent)
{
    /// <summary>Validates the nested execution data and parent metadata.</summary>
    public RuntimeObservationExecutionV2 Validate()
    {
        if (ContractVersion != 2 || Observation is null || CheckpointParent is null)
            throw new RuntimeAuthorizationException("runtime_observation_execution_invalid");
        _ = Observation.Validate();
        _ = CheckpointParent.Validate();
        return this;
    }

    /// <summary>Projects a validated execution observation into the runtime observation contract.</summary>
    public static RuntimeObservationExecutionV2 From(RuntimeExecutionObservationV2 observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _ = observation.Validate();
        var source = observation.Observation;
        return new RuntimeObservationExecutionV2(
            2,
            new RuntimeObservationExecution(
                source.ContractVersion,
                source.RootSessionId,
                source.SessionId,
                source.ExecutionFence,
                source.WorkPlanId,
                source.AssociationId,
                source.CheckpointRevision,
                source.CheckpointId,
                source.CheckpointDecisionStateVersion,
                source.CurrentDecisionStateVersion,
                source.Status)
            {
                PendingDispatch = source.PendingDispatch is null
                    ? null
                    : new RuntimeObservationPendingDispatch(source.PendingDispatch.MessageId),
                ResultDigest = source.ResultDigest
            },
            observation.CheckpointParent).Validate();
    }
}

/// <summary>Versioned aggregate of optional registration, execution, credit, dispatch, and reservation sections.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeObservationV1(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] RuntimeObservationSection<RuntimeObservationRegistration> Registration,
    [property: JsonRequired] RuntimeObservationSection<RuntimeObservationExecutionV2> Execution,
    [property: JsonRequired] RuntimeObservationSection<RuntimeObservationCreditGuard> CreditGuard,
    [property: JsonRequired] RuntimeObservationSection<RuntimeObservationDispatchCollection> Dispatches,
    [property: JsonRequired]
    RuntimeObservationSection<RuntimeObservationToolReservationCollection> ToolReservations)
{
    /// <summary>Validates every section state and every supplied section payload.</summary>
    public RuntimeObservationV1 Validate()
    {
        if (ContractVersion != 1 || Registration is null || Execution is null ||
            CreditGuard is null || Dispatches is null || ToolReservations is null)
            throw new RuntimeAuthorizationException("runtime_observation_invalid");
        _ = Registration.Validate();
        _ = Execution.Validate();
        _ = CreditGuard.Validate();
        _ = Dispatches.Validate();
        _ = ToolReservations.Validate();
        if (Registration.Value is { } registration)
            _ = registration.Validate();
        if (Execution.Value is { } execution)
            _ = execution.Validate();
        if (CreditGuard.Value is { } creditGuard)
            _ = creditGuard.Validate();
        if (Dispatches.Value is { } dispatches)
            _ = dispatches.Validate();
        if (ToolReservations.Value is { } reservations)
            _ = reservations.Validate();
        return this;
    }
}
