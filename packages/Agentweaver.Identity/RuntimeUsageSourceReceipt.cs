using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed record RuntimeUsageSourceReceipt(
    int ContractVersion,
    Guid ReceiptId,
    RuntimeRegistration Registration,
    UsageSubmission Usage,
    string CanonicalPayloadHash,
    DateTimeOffset RecordedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageReceiptReferenceRequest(Guid ReceiptId);

public sealed record RuntimeUsageAccountingAcknowledgment(
    Guid SourceReceiptId, UsageAccountingReceipt Accounting, bool IsDuplicate);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageCostSnapshotRequest(
    int ContractVersion, RuntimeRegistration Registration, SdkSessionFacts Source)
{
    public Guid? DispatchId { get; init; }
    public ImmutableArray<RuntimeUsageCostReceiptReference> RequiredReceipts { get; init; } = [];
}

public sealed record RuntimeUsageCostSnapshotReceipt(
    int ContractVersion, string SourceHash, CostBinding? Binding, CostPrice Quote, UsageRunTotals CopilotTotals)
{
    public Guid? DispatchId { get; init; }
    public ImmutableArray<RuntimeUsageCostReceiptReference> RepresentedReceipts { get; init; } = [];
}

public static class RuntimeUsageCostSnapshotContract
{
    public const int MaximumReceiptReferences = 512;

    public static void ValidateRequest(RuntimeUsageCostSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        RuntimeUsageSourceReceiptContract.ValidateSource(request.Registration, request.Source);
        if (request.DispatchId == Guid.Empty || request.RequiredReceipts.IsDefault ||
            request.RequiredReceipts.Length > MaximumReceiptReferences ||
            request.DispatchId is null && (!request.RequiredReceipts.IsEmpty ||
                request.Source.SourceMode != "hosted-copilot" ||
                request.Source.MeterSource != SdkMeterSources.CopilotNanoAiu))
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        var sourceIds = new HashSet<Guid>();
        var eventIds = new HashSet<Guid>();
        var scope = request.Registration.Binding;
        foreach (var reference in request.RequiredReceipts)
        {
            if (reference is null || reference.SourceReceiptId == Guid.Empty ||
                reference.Accounting is not { } accounting || accounting.EventId == Guid.Empty ||
                !sourceIds.Add(reference.SourceReceiptId) || !eventIds.Add(accounting.EventId) ||
                accounting.Attribution is not { } attribution ||
                attribution.TenantId != scope.TenantId || attribution.ProjectId != scope.ProjectId ||
                attribution.RunId != scope.RunId || attribution.SessionId != scope.SessionId ||
                attribution.AgentId != scope.AgentId || attribution.TurnId != scope.TurnId ||
                !Enum.IsDefined(accounting.Disposition) || accounting.RecordedAt == default ||
                accounting.Disposition == CostDisposition.Unpriced &&
                    (accounting.Amount is not null || string.IsNullOrWhiteSpace(accounting.UnpricedReason)) ||
                accounting.Disposition != CostDisposition.Unpriced &&
                    (accounting.Amount is null or < 0 || accounting.UnpricedReason is not null ||
                     string.IsNullOrWhiteSpace(accounting.Unit) ||
                     string.IsNullOrWhiteSpace(accounting.RateCardId) ||
                     string.IsNullOrWhiteSpace(accounting.RateCardVersion)))
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
            RuntimeContractValidation.ValidateHash(accounting.CanonicalPayloadHash);
        }
    }

    public static void ValidateObservedReceipt(
        RuntimeUsageCostSnapshotReceipt receipt, RuntimeUsageCostSnapshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateRequest(request);
        var scope = request.Registration.Binding;
        if (receipt.ContractVersion != 1 ||
            receipt.SourceHash != RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source) ||
            receipt.DispatchId != request.DispatchId || receipt.RepresentedReceipts.IsDefault ||
            receipt.RepresentedReceipts.Length != request.RequiredReceipts.Length ||
            receipt.Quote is null || !Enum.IsDefined(receipt.Quote.Disposition) ||
            receipt.CopilotTotals is not { Events: >= 0 } totals ||
            totals.TenantId != scope.TenantId || totals.ProjectId != scope.ProjectId || totals.RunId != scope.RunId)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        var expected = request.RequiredReceipts.ToDictionary(reference => reference.SourceReceiptId);
        foreach (var represented in receipt.RepresentedReceipts)
            if (represented is null || !expected.Remove(represented.SourceReceiptId, out var required) ||
                represented.Accounting != required.Accounting)
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        if (expected.Count != 0)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
    }

    public static decimal ValidateReceipt(
        RuntimeUsageCostSnapshotReceipt receipt, RuntimeUsageCostSnapshotRequest request)
    {
        ValidateObservedReceipt(receipt, request);
        var scope = request.Registration.Binding;
        if (request.Source.SourceMode != "hosted-copilot" ||
            request.Source.MeterSource != SdkMeterSources.CopilotNanoAiu)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_unpriced");
        return ValidatePricedCopilotSnapshot(receipt.Binding, receipt.Quote, receipt.CopilotTotals,
            request.Source.ModelId, scope.TenantId, scope.ProjectId, scope.RunId);
    }

    public static decimal ValidatePricedCopilotSnapshot(
        CostBinding? binding, CostPrice quote, UsageRunTotals copilotTotals,
        string modelId, string tenantId, string projectId, string runId)
    {
        if (binding is not { MeterSource: SdkMeterSources.CopilotNanoAiu, RateCard: { } card } ||
            binding.NegotiatedCapabilities is null ||
            card.MeterSource != SdkMeterSources.CopilotNanoAiu || card.Unit != "AIC" ||
            card.NanoUnitsPerUnit <= 0 || card.ModelMultipliers is null ||
            !card.ModelMultipliers.ContainsKey(modelId) ||
            quote is not { Amount: 0, Unit: "AIC", UnpricedReason: null, RateCard: { } quoted } ||
            !Enum.IsDefined(quote.Disposition) || quote.Disposition == CostDisposition.Unpriced ||
            quoted.Id != card.Id || quoted.Version != card.Version || quoted.MeterSource != card.MeterSource ||
            quoted.Unit != card.Unit || quoted.NanoUnitsPerUnit != card.NanoUnitsPerUnit ||
            quoted.ModelMultipliers is null || quoted.ModelMultipliers.Count != card.ModelMultipliers.Count ||
            quoted.ModelMultipliers.Any(model =>
                !card.ModelMultipliers.TryGetValue(model.Key, out var multiplier) || multiplier != model.Value) ||
            copilotTotals is not { IsFullyPriced: true, Events: >= 0 } totals ||
            totals.TenantId != tenantId || totals.ProjectId != projectId ||
            totals.RunId != runId || totals.Amounts.IsDefault || totals.Agents.IsDefault ||
            totals.Agents.Any(agent => agent is null || !agent.IsFullyPriced || agent.Events < 0) ||
            totals.Amounts.Any(amount => amount is null || amount.MeterSource != SdkMeterSources.CopilotNanoAiu ||
                amount.Unit != "AIC" || amount.UnpricedEvents != 0 ||
                amount.PricedEvents < 0 || amount.Amount < 0))
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_unpriced");
        try
        {
            if (copilotTotals.Amounts.Sum(amount => amount.PricedEvents) != copilotTotals.Events ||
                copilotTotals.Agents.Sum(agent => agent.Events) != copilotTotals.Events)
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
            return copilotTotals.Amounts.Sum(amount => amount.Amount);
        }
        catch (OverflowException)
        {
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageCostReceiptReference(
    Guid SourceReceiptId, UsageAccountingReceipt Accounting);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageCostReconciliationRequest(
    int ContractVersion,
    Guid CostTurnId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string SessionId,
    string RuntimeTurnId,
    string AcceptedSelectionHash,
    string ModelSelectionReference,
    string ModelId,
    string MeterSource,
    string RateCardId,
    string RateCardVersion,
    string RateCardUnit,
    long MinimumAccountingRevision,
    ImmutableArray<RuntimeUsageCostReceiptReference> RequiredReceipts);

public sealed record RuntimeUsageCostReconciliationReceipt(
    int ContractVersion,
    Guid CostTurnId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string RuntimeTurnId,
    string AcceptedSelectionHash,
    string ModelSelectionReference,
    string ModelId,
    string MeterSource,
    string RateCardId,
    string RateCardVersion,
    string RateCardUnit,
    long AccountingRevision,
    ImmutableArray<RuntimeUsageCostReceiptReference> RepresentedReceipts,
    UsageRunTotals CopilotTotals);

public static class RuntimeUsageCostReconciliationContract
{
    public const int MaximumReceiptReferences = 512;

    public static void ValidateRequest(RuntimeUsageCostReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != 1 || request.CostTurnId == Guid.Empty ||
            request.RuntimeInstanceId == Guid.Empty || request.RegistrationRevision < 1 ||
            request.ExecutionFence < 1 || request.MinimumAccountingRevision < 0 ||
            request.RequiredReceipts.IsDefaultOrEmpty ||
            request.RequiredReceipts.Length > MaximumReceiptReferences)
            throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
        RuntimeContractValidation.ValidateIdentifier(request.SessionId);
        RuntimeContractValidation.ValidateIdentifier(request.RuntimeTurnId);
        RuntimeContractValidation.ValidateHash(request.AcceptedSelectionHash);
        RuntimeContractValidation.ValidateIdentifier(request.ModelSelectionReference);
        RuntimeContractValidation.ValidateIdentifier(request.MeterSource);
        RuntimeContractValidation.ValidateIdentifier(request.RateCardId);
        RuntimeContractValidation.ValidateIdentifier(request.RateCardVersion);
        RuntimeContractValidation.ValidateIdentifier(request.RateCardUnit);
        if (request.MeterSource != SdkMeterSources.CopilotNanoAiu ||
            string.IsNullOrWhiteSpace(request.ModelId) || request.ModelId.Length > 512 ||
            request.ModelId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
        var sourceReceiptIds = new HashSet<Guid>();
        var eventIds = new HashSet<Guid>();
        foreach (var reference in request.RequiredReceipts)
        {
            if (reference is null || reference.SourceReceiptId == Guid.Empty ||
                reference.Accounting is not { } accounting || accounting.EventId == Guid.Empty ||
                accounting.Attribution is null ||
                !sourceReceiptIds.Add(reference.SourceReceiptId) || !eventIds.Add(accounting.EventId))
                throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
            RuntimeContractValidation.ValidateHash(accounting.CanonicalPayloadHash);
            if (accounting.Attribution.SessionId != request.SessionId ||
                accounting.Attribution.TurnId != request.RuntimeTurnId ||
                !Enum.IsDefined(accounting.Disposition) || accounting.Amount is < 0 ||
                accounting.Disposition == CostDisposition.Unpriced &&
                    (accounting.Amount is not null || string.IsNullOrWhiteSpace(accounting.UnpricedReason) ||
                     accounting.RateCardId is not null && accounting.RateCardId != request.RateCardId ||
                     accounting.RateCardVersion is not null &&
                         accounting.RateCardVersion != request.RateCardVersion ||
                     accounting.Unit is not null && accounting.Unit != request.RateCardUnit) ||
                accounting.Disposition != CostDisposition.Unpriced &&
                    (accounting.RateCardId != request.RateCardId ||
                     accounting.RateCardVersion != request.RateCardVersion ||
                     accounting.Unit != request.RateCardUnit || accounting.Amount is null ||
                     accounting.UnpricedReason is not null))
                throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
        }
    }

    public static void ValidateReceipt(
        RuntimeUsageCostReconciliationReceipt receipt,
        RuntimeUsageCostReconciliationRequest request,
        string tenantId,
        string projectId,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateRequest(request);
        if (receipt.ContractVersion != 1 || receipt.CostTurnId != request.CostTurnId ||
            receipt.RuntimeInstanceId != request.RuntimeInstanceId ||
            receipt.RegistrationRevision != request.RegistrationRevision ||
            receipt.ExecutionFence != request.ExecutionFence ||
            receipt.TenantId != tenantId || receipt.ProjectId != projectId || receipt.RunId != runId ||
            receipt.SessionId != request.SessionId || receipt.RuntimeTurnId != request.RuntimeTurnId ||
            receipt.AcceptedSelectionHash != request.AcceptedSelectionHash ||
            receipt.ModelSelectionReference != request.ModelSelectionReference ||
            receipt.ModelId != request.ModelId || receipt.MeterSource != request.MeterSource ||
            receipt.RateCardId != request.RateCardId || receipt.RateCardVersion != request.RateCardVersion ||
            receipt.RateCardUnit != request.RateCardUnit ||
            receipt.AccountingRevision < request.MinimumAccountingRevision ||
            receipt.CopilotTotals is null || receipt.CopilotTotals.TenantId != tenantId ||
            receipt.CopilotTotals.ProjectId != projectId || receipt.CopilotTotals.RunId != runId ||
            receipt.CopilotTotals.Events != receipt.AccountingRevision ||
            !receipt.CopilotTotals.IsFullyPriced || receipt.RepresentedReceipts.IsDefault ||
            receipt.RepresentedReceipts.Length != request.RequiredReceipts.Length ||
            receipt.RepresentedReceipts.Any(reference =>
                reference is null || reference.Accounting is null ||
                reference.Accounting.RateCardId != request.RateCardId ||
                reference.Accounting.RateCardVersion != request.RateCardVersion ||
                reference.Accounting.Unit != request.RateCardUnit) ||
            receipt.CopilotTotals.Amounts.Any(amount =>
                amount.MeterSource != request.MeterSource || amount.Unit != request.RateCardUnit))
            throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
        var expected = request.RequiredReceipts.ToDictionary(reference => reference.SourceReceiptId);
        foreach (var represented in receipt.RepresentedReceipts)
            if (!expected.Remove(represented.SourceReceiptId, out var required) ||
                required.Accounting != represented.Accounting)
                throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
        if (expected.Count != 0)
            throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
    }
}

public static class RuntimeUsageSourceReceiptContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Validate(RuntimeUsageSourceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.ContractVersion != 1 || receipt.ReceiptId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_invalid");
        ValidateUsage(receipt.Registration, receipt.Usage);
        RuntimeContractValidation.ValidateHash(receipt.CanonicalPayloadHash);
        if (Hash(receipt.Registration, receipt.Usage) != receipt.CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("runtime_usage_receipt_hash_invalid");
    }

    public static void ValidateUsage(RuntimeRegistration registration, UsageSubmission usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var binding = registration.Binding;
        var source = usage.SdkSource;
        if (source is null)
            throw new RuntimeAuthorizationException("runtime_usage_source_invalid");
        ValidateSource(registration, source);
        if (
            usage.Attribution is not { } attribution || usage.ModelBinding is not { } model ||
            usage.Measurement is not { } measurement ||
            attribution.TenantId != binding.TenantId || attribution.ProjectId != binding.ProjectId ||
            attribution.RunId != binding.RunId || attribution.SessionId != binding.SessionId ||
            attribution.AgentId != binding.AgentId || attribution.TurnId != binding.TurnId ||
            model.ModelReference != source.ModelSelectionReference || model.ModelId != source.ModelId ||
            model.MeterSource != source.MeterSource || model.SelectionRevision != binding.ContextRevision ||
            measurement.RequestCount is not null ||
            measurement.ProviderUnit != (source.SourceMode == "hosted-copilot" ? "nano_aiu" : "tokens") ||
            source.SourceMode == "byok" && measurement.ProviderUnits is not null ||
            measurement.InputTokens < 0 || measurement.OutputTokens < 0 ||
            measurement.CachedTokens < 0 || measurement.CacheWriteTokens < 0 ||
            measurement.ReasoningTokens < 0 || measurement.ProviderUnits < 0 ||
            measurement.DurationMilliseconds < 0)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        if (usage.A2AMessageId == Guid.Empty || usage.SdkEventId is not { } sdkEventId ||
            !Guid.TryParseExact(sdkEventId, "D", out var nativeEvent) || nativeEvent == Guid.Empty ||
            nativeEvent.ToString("D") != sdkEventId ||
            usage.EventId != SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, sdkEventId))
            throw new RuntimeAuthorizationException("runtime_usage_event_invalid");
        ValidateAccounting(source, usage.SdkAccounting, measurement.ProviderUnits);
    }

    public static void ValidateAccounting(
        SdkSessionFacts source, SdkUsageAccountingObservation? accounting, decimal? reportedNanoAiu)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (accounting is null)
            return;
        if (!Enum.IsDefined(accounting.AiCreditsStatus) ||
            !accounting.AiCreditsStatusReported && accounting.AiCreditsStatus != SdkAiCreditsStatus.Unavailable ||
            accounting.AiCreditsStatus == SdkAiCreditsStatus.Complete &&
            (!accounting.AiCreditsStatusReported || reportedNanoAiu is null ||
             source.SourceMode != "hosted-copilot"))
            throw new RuntimeAuthorizationException("runtime_usage_accounting_invalid");
        if (accounting.Identity is { } identity)
        {
            RequireText(identity.SourceSessionId);
            RequireText(identity.UsageId);
            if (identity.SourceSessionId != source.SdkSessionId || identity.Sequence < 1)
                throw new RuntimeAuthorizationException("runtime_usage_accounting_binding_invalid");
        }
    }

    public static void ValidateSource(RuntimeRegistration registration, SdkSessionFacts source)
    {
        RuntimeContractValidation.Validate(registration);
        ArgumentNullException.ThrowIfNull(source);
        var binding = registration.Binding;
        if (registration.State != RuntimeRegistrationState.Active ||
            binding.PlacementProviderId is null || binding.EnvironmentLifecycleGeneration < 1 ||
            binding.EnvironmentLeaseRevision < 1 ||
            source.RuntimeInstanceId != registration.RuntimeInstanceId ||
            source.RegistrationRevision != registration.Revision ||
            source.SdkSessionId != RuntimeContractValidation.NativeSessionId(binding) ||
            source.ModelSelectionReference != binding.ModelSelectionReference ||
            binding.ModelBindingPin is { } modelPin && source.ModelId != modelPin.ModelId ||
            source.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            !MatchesSourceMode(binding.ModelSourceMode, source) ||
            source.ModelMultiplier < 0 ||
            binding.MaxPromptTokens is null && source.MaxPromptTokens is not null ||
            binding.MaxPromptTokens is { } promptLimit &&
                (source.MaxPromptTokens is null or < 1 || source.MaxPromptTokens > promptLimit))
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        RuntimeContractValidation.ValidateHash(source.CatalogHash);
        RequireText(source.SdkVersion);
        RequireText(source.RuntimeVersion);
        RequireText(source.ModelId);
    }

    private static bool MatchesSourceMode(ModelSourceMode? mode, SdkSessionFacts source) => mode switch
    {
        ModelSourceMode.HostedCopilot =>
            source.SourceMode == "hosted-copilot" && source.MeterSource == SdkMeterSources.CopilotNanoAiu,
        ModelSourceMode.Byok =>
            source.SourceMode == "byok" && source.MeterSource == SdkMeterSources.ByokTokens &&
            source.ModelMultiplier is null,
        _ => false
    };

    public static string Hash(RuntimeRegistration registration, UsageSubmission usage) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalSourceUsage(registration, usage), JsonOptions));

    public static string HashSource(RuntimeRegistration registration, SdkSessionFacts source) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new { Registration = registration, Source = source }, JsonOptions));

    public static UsageSubmission CreateUsage(
        RuntimeRegistration registration, SdkSessionFacts source, SdkUsageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(observation);
        var binding = registration.Binding;
        if (observation.SdkSessionId != source.SdkSessionId || observation.ModelId != source.ModelId)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        var usage = new UsageSubmission(
            observation.EventId, observation.OccurredAt,
            new(binding.TenantId, binding.ProjectId, binding.RunId, binding.SessionId, binding.AgentId)
            {
                TurnId = binding.TurnId
            },
            new(source.ModelSelectionReference, source.ModelId, source.MeterSource, binding.ContextRevision),
            new(observation.InputTokens, observation.OutputTokens, observation.CacheReadTokens,
                observation.ReasoningTokens, null, observation.TotalNanoAiu,
                source.SourceMode == "hosted-copilot" ? "nano_aiu" : "tokens",
                observation.DurationMilliseconds)
            {
                CacheWriteTokens = observation.CacheWriteTokens
            })
        {
            SdkSource = source,
            SdkEventId = observation.SdkEventId,
            A2AMessageId = observation.A2AMessageId,
            SdkAccounting = observation.Accounting
        };
        ValidateUsage(registration, usage);
        return usage;
    }

    private static void RequireText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 ||
            text.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new RuntimeAuthorizationException("runtime_usage_source_invalid");
    }

    private sealed record CanonicalSourceUsage(RuntimeRegistration Registration, UsageSubmission Usage);
}
