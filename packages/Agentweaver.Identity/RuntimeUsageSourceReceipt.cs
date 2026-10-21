using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Identity;

/// <summary>Durable owner receipt for one usage submission attributed to a runtime SDK source.</summary>
public sealed record RuntimeUsageSourceReceipt(
    int ContractVersion,
    Guid ReceiptId,
    RuntimeRegistration Registration,
    UsageSubmission Usage,
    string CanonicalPayloadHash,
    DateTimeOffset RecordedAt);

/// <summary>Azure BYOK provenance that an owner-issued usage receipt proves for one dispatch.</summary>
/// <param name="SourceReceiptId">The owner receipt ID.</param>
/// <param name="RuntimeInstanceId">The registered runtime instance that produced the usage.</param>
/// <param name="RegistrationRevision">The registration revision that was active for the usage.</param>
/// <param name="DispatchId">The A2A dispatch message ID that the usage belongs to.</param>
/// <param name="DeploymentId">The Azure deployment ID. It equals the pinned model ID.</param>
/// <param name="ConfigurationRevision">The pinned BYOK configuration revision.</param>
/// <param name="ConfigurationHash">The pinned BYOK configuration hash.</param>
/// <param name="SourcePayloadHash">The canonical payload hash of the owner receipt.</param>
public sealed record TrustedAzureByokUsage(
    Guid SourceReceiptId,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    Guid DispatchId,
    string DeploymentId,
    string ConfigurationRevision,
    string ConfigurationHash,
    string SourcePayloadHash);

/// <summary>Request to retrieve one stored usage receipt by its exact receipt ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageReceiptReferenceRequest(Guid ReceiptId);

/// <summary>Owner acknowledgment that a source usage receipt was accounted, possibly as a duplicate.</summary>
public sealed record RuntimeUsageAccountingAcknowledgment(
    Guid SourceReceiptId, UsageAccountingReceipt Accounting, bool IsDuplicate);

/// <summary>Request for an immutable cost snapshot over a bounded set of accounting receipts.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageCostSnapshotRequest(
    int ContractVersion, RuntimeRegistration Registration, SdkSessionFacts Source)
{
    public Guid? DispatchId { get; init; }
    public ImmutableArray<RuntimeUsageCostReceiptReference> RequiredReceipts { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeNativeTurnRecordedReceipt? NativeTurn { get; init; }
}

/// <summary>Owner response with pricing and totals for the exact required receipt set.</summary>
public sealed record RuntimeUsageCostSnapshotReceipt(
    int ContractVersion, string SourceHash, CostBinding? Binding, CostPrice Quote, UsageRunTotals CopilotTotals)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RateCardContentHash { get; init; }
    public Guid? DispatchId { get; init; }
    public ImmutableArray<RuntimeUsageCostReceiptReference> RepresentedReceipts { get; init; } = [];
}

/// <summary>Validates usage cost snapshot requests, receipts, and hosted-Copilot pricing results.</summary>
public static class RuntimeUsageCostSnapshotContract
{
    /// <summary>Maximum number of accounting receipt references accepted per snapshot.</summary>
    public const int MaximumReceiptReferences = 512;

    /// <summary>Validates scope, uniqueness, attribution, and optional native-turn binding.</summary>
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
            if (accounting.RateCardContentHash is { } accountingContentHash)
                RuntimeContractValidation.ValidateHash(accountingContentHash);
        }
        if (request.NativeTurn is { } recorded)
            RuntimeNativeTurnContract.ValidateAccountingSnapshot(request, recorded);
    }

    /// <summary>Requires exact receipts and Copilot totals that include every represented receipt.</summary>
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
        if (receipt.RateCardContentHash is { } contentHash)
            RuntimeContractValidation.ValidateHash(contentHash);
        var card = receipt.Binding?.RateCard;
        if (card?.ModelTokenRates is { Count: > 0 } &&
            (receipt.RateCardContentHash is null || card.ContentHash is null))
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        if (receipt.RateCardContentHash != receipt.Binding?.RateCard?.ContentHash ||
            receipt.RepresentedReceipts.Any(reference =>
                reference is null || reference.Accounting is null ||
                reference.Accounting.RateCardContentHash != receipt.RateCardContentHash ||
                receipt.Binding is { RateCard: { ContentHash: not null } binding } &&
                    (reference.Accounting.RateCardId != binding.Id ||
                     reference.Accounting.RateCardVersion != binding.Version)))
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        var expected = request.RequiredReceipts.ToDictionary(reference => reference.SourceReceiptId);
        foreach (var represented in receipt.RepresentedReceipts)
            if (represented is null || !expected.Remove(represented.SourceReceiptId, out var required) ||
                represented.Accounting != required.Accounting)
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        if (expected.Count != 0)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        if (request.Source.SourceMode == "hosted-copilot" &&
            request.Source.MeterSource == SdkMeterSources.CopilotNanoAiu)
        {
            var allowLegacyUnpricedOmission = request.RequiredReceipts.IsEmpty &&
                IsLegacyUnpricedOmission(receipt.Binding, receipt.Quote, receipt.CopilotTotals);
            RuntimeUsageCostTotalsContract.ValidateRepresentedCopilotTotals(
                receipt.CopilotTotals, receipt.RepresentedReceipts, "runtime_usage_cost_snapshot_invalid",
                allowLegacyUnpricedOmission);
        }
    }

    /// <summary>Validates a hosted-Copilot snapshot and returns only its known priced AIC subtotal.</summary>
    public static decimal ValidateReceipt(
        RuntimeUsageCostSnapshotReceipt receipt, RuntimeUsageCostSnapshotRequest request)
    {
        ValidateObservedReceipt(receipt, request);
        var scope = request.Registration.Binding;
        if (request.Source.SourceMode != "hosted-copilot" ||
            request.Source.MeterSource != SdkMeterSources.CopilotNanoAiu)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_unpriced");
        var pricedSubtotal = ValidatePricedCopilotSnapshot(receipt.Binding, receipt.Quote, receipt.CopilotTotals,
            request.Source.ModelId, scope.TenantId, scope.ProjectId, scope.RunId);
        return pricedSubtotal;
    }

    /// <summary>Checks published-rate identity, explicit Unpriced shape, totals, and arithmetic consistency.</summary>
    public static decimal ValidatePricedCopilotSnapshot(
        CostBinding? binding, CostPrice quote, UsageRunTotals copilotTotals,
        string modelId, string tenantId, string projectId, string runId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(runId) ||
            quote is null || !Enum.IsDefined(quote.Disposition) ||
            copilotTotals is not { Events: >= 0 } totals ||
            totals.TenantId != tenantId || totals.ProjectId != projectId ||
            totals.RunId != runId || totals.Amounts.IsDefault || totals.Agents.IsDefault ||
            totals.Agents.Any(agent => agent is null || agent.Events < 0 ||
                agent.Amounts.IsDefault ||
                agent.Amounts.Any(amount => !RuntimeUsageCostTotalsContract.ValidCopilotAmount(amount))) ||
            totals.Amounts.Any(amount => !RuntimeUsageCostTotalsContract.ValidCopilotAmount(amount)))
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_unpriced");
        try
        {
            var legacyUnpricedOmission = IsLegacyUnpricedOmission(binding, quote, totals);
            var recordedEvents = totals.Amounts.Sum(amount => checked(amount.PricedEvents + amount.UnpricedEvents));
            var isFullyPriced = RuntimeUsageCostTotalsContract.AmountsAreFullyPriced(totals.Amounts);
            if (!legacyUnpricedOmission &&
                (recordedEvents != totals.Events || totals.IsFullyPriced != isFullyPriced ||
                totals.Agents.Any(agent =>
                    agent.IsFullyPriced != RuntimeUsageCostTotalsContract.AmountsAreFullyPriced(agent.Amounts)) ||
                totals.Agents.Sum(agent => agent.Events) != totals.Events ||
                !RuntimeUsageCostTotalsContract.RunAmountsMatchAgents(totals)))
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");

            if (binding is null)
            {
                if (quote is not { Amount: null, Disposition: CostDisposition.Unpriced, RateCard: null } ||
                    quote.Unit is not (null or "AIC") || !ValidUnpricedReason(quote.UnpricedReason) ||
                    totals.Amounts.Any(amount => amount.PricedEvents != 0))
                    throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
            }
            else
            {
                if (binding is not
                    {
                        MeterSource: SdkMeterSources.CopilotNanoAiu,
                        RateCard: { } card,
                        NegotiatedCapabilities: not null
                    } ||
                    card.MeterSource != SdkMeterSources.CopilotNanoAiu || card.Unit != "AIC" ||
                        card.ModelMultipliers is null || card.ModelTokenRates is null ||
                        (card.ModelTokenRates.Count == 0
                            ? card.ModelMultipliers.Count == 0
                            : card.ModelMultipliers.Count != 0) ||
                        card.ModelTokenRates.Count > 0 && card.ContentHash is null)
                    throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
                try
                {
                    CostRateCardContract.Validate(card);
                }
                catch (ArgumentException)
                {
                    throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
                }

                var hasModelRate = card.ModelTokenRates.ContainsKey(modelId);
                // Legacy multiplier quotes keep their recorded estimate; absent model rates remain Unpriced.
                var hasLegacyModelMultiplier =
                    card.ModelTokenRates.Count == 0 && card.ModelMultipliers.ContainsKey(modelId);
                var quoteMatchesCard = CostRateCardContract.Equals(quote.RateCard, card);
                var validEstimate = hasModelRate &&
                    quote is { Amount: 0, Unit: "AIC", Disposition: CostDisposition.Estimate,
                        UnpricedReason: null };
                var validLegacyEstimate = hasLegacyModelMultiplier &&
                    quote is { Amount: 0, Unit: "AIC", Disposition: CostDisposition.Estimate,
                        UnpricedReason: null } &&
                    totals.IsFullyPriced;
                var validUnpriced = quote is
                    { Amount: null, Unit: "AIC", Disposition: CostDisposition.Unpriced } &&
                    ValidUnpricedReason(quote.UnpricedReason);
                if (!quoteMatchesCard ||
                    !validEstimate && !validLegacyEstimate && !validUnpriced)
                    throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
            }

            return totals.Amounts.Sum(amount => amount.Amount);
        }
        catch (OverflowException)
        {
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        }
    }

    private static bool ValidUnpricedReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 1024 &&
        !reason.Any(char.IsControl);

    /// <summary>Allows only the pre-token-rate Unpriced snapshot shape with omitted amount groups.</summary>
    private static bool IsLegacyUnpricedOmission(
        CostBinding? binding, CostPrice quote, UsageRunTotals totals) =>
        binding?.RateCard is { ModelTokenRates.Count: 0, ModelMultipliers.Count: > 0 } card &&
        quote is
        {
            Amount: null,
            Unit: "AIC",
            Disposition: CostDisposition.Unpriced,
            RateCard: { } quoteCard
        } &&
        ValidUnpricedReason(quote.UnpricedReason) &&
        CostRateCardContract.Equals(quoteCard, card) &&
        RuntimeUsageCostTotalsContract.HasLegacyUnpricedTotalsShape(totals);
}

/// <summary>Checks Copilot run totals against agent subtotals and represented usage receipts.</summary>
internal static class RuntimeUsageCostTotalsContract
{
    /// <summary>Requires totals to include represented receipts without counting Unpriced as zero.</summary>
    internal static void ValidateRepresentedCopilotTotals(
        UsageRunTotals totals,
        ImmutableArray<RuntimeUsageCostReceiptReference> receipts,
        string invalidCode,
        bool allowLegacyUnpricedOmission = false)
    {
        try
        {
            if (totals.Amounts.IsDefault || totals.Agents.IsDefault ||
                totals.Amounts.Any(amount => !ValidCopilotAmount(amount)) ||
                totals.Agents.Any(agent => agent is null || agent.Events < 0 ||
                    agent.Amounts.IsDefault || agent.Amounts.Any(amount => !ValidCopilotAmount(amount))) ||
                receipts.Any(reference => reference.Accounting.Unit is not (null or "AIC")))
                throw new RuntimeAuthorizationException(invalidCode);

            if (allowLegacyUnpricedOmission && receipts.IsEmpty && HasLegacyUnpricedTotalsShape(totals))
                return;

            var recordedEvents = totals.Amounts.Sum(amount =>
                checked(amount.PricedEvents + amount.UnpricedEvents));
            var isFullyPriced = AmountsAreFullyPriced(totals.Amounts);
            if (recordedEvents != totals.Events || totals.IsFullyPriced != isFullyPriced ||
                totals.Agents.Any(agent => agent.IsFullyPriced != AmountsAreFullyPriced(agent.Amounts)) ||
                totals.Agents.Sum(agent => agent.Events) != totals.Events || !RunAmountsMatchAgents(totals))
                throw new RuntimeAuthorizationException(invalidCode);

            if (receipts.IsEmpty)
                return;

            var priced = receipts.Where(reference =>
                reference.Accounting.Disposition != CostDisposition.Unpriced).ToArray();
            var unpricedCount = receipts.LongCount(reference =>
                reference.Accounting.Disposition == CostDisposition.Unpriced);
            var pricedCount = (long)priced.Length;
            if (priced.Any(reference => reference.Accounting.Amount is null))
                throw new RuntimeAuthorizationException(invalidCode);
            var pricedAmount = priced.Sum(reference => reference.Accounting.Amount!.Value);
            if (!IncludesRepresentedAmounts(totals.Amounts, pricedCount, unpricedCount, pricedAmount))
                throw new RuntimeAuthorizationException(invalidCode);

            foreach (var group in receipts.GroupBy(
                         reference => reference.Accounting.Attribution.AgentId, StringComparer.Ordinal))
            {
                var agentTotals = totals.Agents.Where(agent => agent.AgentId == group.Key).ToArray();
                var agentReceipts = group.ToArray();
                var agentPriced = agentReceipts.Where(reference =>
                    reference.Accounting.Disposition != CostDisposition.Unpriced).ToArray();
                var agentUnpricedCount = agentReceipts.LongCount(reference =>
                    reference.Accounting.Disposition == CostDisposition.Unpriced);
                var agentPricedAmount = agentPriced.Sum(reference => reference.Accounting.Amount!.Value);
                if (agentTotals.Length != 1 || agentTotals[0].Events < agentReceipts.Length ||
                    !IncludesRepresentedAmounts(
                        agentTotals[0].Amounts, agentPriced.Length, agentUnpricedCount, agentPricedAmount))
                    throw new RuntimeAuthorizationException(invalidCode);
            }
        }
        catch (OverflowException)
        {
            throw new RuntimeAuthorizationException(invalidCode);
        }
    }

    /// <summary>Checks a Copilot total's AIC unit and nonnegative event and amount fields.</summary>
    internal static bool ValidCopilotAmount(UsageAmountTotal? amount) =>
        amount is not null &&
        amount.MeterSource == SdkMeterSources.CopilotNanoAiu &&
        amount.Unit == "AIC" &&
        amount.PricedEvents >= 0 &&
        amount.UnpricedEvents >= 0 &&
        amount.Amount >= 0 &&
        (amount.PricedEvents > 0 || amount.Amount == 0);

    /// <summary>Derives pricing completeness from explicit Unpriced event counts.</summary>
    internal static bool AmountsAreFullyPriced(ImmutableArray<UsageAmountTotal> amounts) =>
        amounts.All(amount => amount.UnpricedEvents == 0);

    /// <summary>Recognizes the historical Unpriced totals shape that omitted amount groups.</summary>
    internal static bool HasLegacyUnpricedTotalsShape(UsageRunTotals totals)
    {
        if (totals.Events <= 0 || !totals.Amounts.IsEmpty || totals.IsFullyPriced ||
            totals.Agents.IsDefaultOrEmpty ||
            totals.Agents.Any(agent => agent is null || agent.Events < 0 || agent.IsFullyPriced ||
                agent.Amounts.IsDefault || !agent.Amounts.IsEmpty))
            return false;
        try
        {
            return totals.Agents.Sum(agent => agent.Events) == totals.Events;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Checks that each run amount equals the sum of its per-agent amounts.</summary>
    internal static bool RunAmountsMatchAgents(UsageRunTotals totals)
    {
        var agentAmounts = new Dictionary<(string MeterSource, string Unit), UsageAmountTotal>();
        foreach (var agent in totals.Agents)
        foreach (var amount in agent.Amounts)
        {
            var key = (amount.MeterSource, amount.Unit);
            if (agentAmounts.TryGetValue(key, out var current))
                agentAmounts[key] = current with
                {
                    Amount = checked(current.Amount + amount.Amount),
                    PricedEvents = checked(current.PricedEvents + amount.PricedEvents),
                    UnpricedEvents = checked(current.UnpricedEvents + amount.UnpricedEvents)
                };
            else
                agentAmounts.Add(key, amount);
        }

        if (agentAmounts.Count != totals.Amounts.Length)
            return false;
        var seen = new HashSet<(string MeterSource, string Unit)>();
        foreach (var total in totals.Amounts)
        {
            var key = (total.MeterSource, total.Unit);
            if (!seen.Add(key) || !agentAmounts.TryGetValue(key, out var agentTotal) ||
                total.Amount != agentTotal.Amount ||
                total.PricedEvents != agentTotal.PricedEvents ||
                total.UnpricedEvents != agentTotal.UnpricedEvents)
                return false;
        }
        return true;
    }

    private static bool IncludesRepresentedAmounts(
        ImmutableArray<UsageAmountTotal> amounts,
        long pricedEvents,
        long unpricedEvents,
        decimal pricedAmount)
    {
        var copilotAmounts = amounts.Where(amount =>
            amount.MeterSource == SdkMeterSources.CopilotNanoAiu && amount.Unit == "AIC").ToArray();
        return copilotAmounts.Length == 1 &&
            copilotAmounts[0].PricedEvents >= pricedEvents &&
            copilotAmounts[0].UnpricedEvents >= unpricedEvents &&
            copilotAmounts[0].Amount >= pricedAmount;
    }
}

/// <summary>One source receipt and accounting receipt represented by a cost snapshot.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeUsageCostReceiptReference(
    Guid SourceReceiptId, UsageAccountingReceipt Accounting);

/// <summary>Request to reconcile one model turn's usage against its owner accounting revision.</summary>
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
    ImmutableArray<RuntimeUsageCostReceiptReference> RequiredReceipts)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RateCardContentHash { get; init; }
}

/// <summary>Owner receipt proving the exact turn usage has been represented at an accounting revision.</summary>
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
    UsageRunTotals CopilotTotals)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RateCardContentHash { get; init; }
}

/// <summary>Validates model-turn cost reconciliation requests and exact represented receipt sets.</summary>
public static class RuntimeUsageCostReconciliationContract
{
    /// <summary>Maximum number of accounting receipts accepted in a reconciliation request.</summary>
    public const int MaximumReceiptReferences = 512;

    /// <summary>Checks model, rate-card, turn, revisions, and unique receipt identities.</summary>
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
        if (request.RateCardContentHash is { } contentHash)
            RuntimeContractValidation.ValidateHash(contentHash);
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
            if (accounting.RateCardContentHash is { } accountingContentHash)
                RuntimeContractValidation.ValidateHash(accountingContentHash);
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
                     accounting.UnpricedReason is not null) ||
                accounting.RateCardContentHash != request.RateCardContentHash)
                throw new RuntimeAuthorizationException("runtime_usage_reconciliation_invalid");
        }
    }

    /// <summary>Requires owner totals and represented receipts to match the reconciliation request.</summary>
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
            receipt.RateCardContentHash != request.RateCardContentHash ||
            receipt.AccountingRevision < request.MinimumAccountingRevision ||
            receipt.CopilotTotals is null || receipt.CopilotTotals.TenantId != tenantId ||
            receipt.CopilotTotals.ProjectId != projectId || receipt.CopilotTotals.RunId != runId ||
            receipt.CopilotTotals.Events != receipt.AccountingRevision ||
            receipt.CopilotTotals.Amounts.IsDefault || receipt.CopilotTotals.Agents.IsDefault ||
            receipt.CopilotTotals.Amounts.Any(amount =>
                amount is null || amount.MeterSource != request.MeterSource ||
                amount.Unit != request.RateCardUnit || amount.Amount < 0 ||
                amount.PricedEvents < 0 || amount.UnpricedEvents < 0) ||
            receipt.CopilotTotals.Agents.Any(agent =>
                agent is null || agent.Events < 0 || agent.Amounts.IsDefault ||
                agent.Amounts.Any(amount => amount is null || amount.MeterSource != request.MeterSource ||
                    amount.Unit != request.RateCardUnit || amount.Amount < 0 ||
                    amount.PricedEvents < 0 || amount.UnpricedEvents < 0)) ||
            receipt.RepresentedReceipts.IsDefault ||
            receipt.RepresentedReceipts.Length != request.RequiredReceipts.Length ||
            receipt.RepresentedReceipts.Any(reference =>
                reference is null || reference.Accounting is null ||
                reference.Accounting.RateCardId != request.RateCardId ||
                reference.Accounting.RateCardVersion != request.RateCardVersion ||
                reference.Accounting.Unit != request.RateCardUnit ||
                reference.Accounting.RateCardContentHash != request.RateCardContentHash) ||
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
        RuntimeUsageCostTotalsContract.ValidateRepresentedCopilotTotals(
            receipt.CopilotTotals, receipt.RepresentedReceipts, "runtime_usage_reconciliation_invalid");
    }
}

/// <summary>Creates, hashes, and validates SDK source receipts and their usage submissions.</summary>
public static class RuntimeUsageSourceReceiptContract
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Checks the receipt version, ID, usage binding, and canonical payload digest.</summary>
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

    /// <summary>Validates source, attribution, model, measurement, and event identity against registration.</summary>
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
            !HasSupportedMeasurementShape(source, measurement) ||
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

    /// <summary>Accepts hosted Nano AIU or token usage and the existing BYOK token shape.</summary>
    private static bool HasSupportedMeasurementShape(SdkSessionFacts source, UsageMeasurement measurement)
    {
        if (source.SourceMode == "hosted-copilot")
            return measurement.ProviderUnit == "nano_aiu" ||
                (measurement.ProviderUnits is null &&
                 (measurement.ProviderUnit is null or "tokens"));
        return source.SourceMode == "byok" && measurement.ProviderUnit == "tokens" &&
            measurement.ProviderUnits is null && measurement.InputTokens is not null &&
            measurement.OutputTokens is not null;
    }

    /// <summary>Checks optional SDK accounting state and its source-session identity.</summary>
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

    /// <summary>Requires active placement and exact model/source fields for the current registration.</summary>
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
            source.ReasoningEffort != binding.ReasoningEffort ||
            source.ModelBindingPin != binding.ModelBindingPin ||
            source.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            !MatchesSourceMode(binding.ModelSourceMode, source) ||
            source.ModelMultiplier < 0 ||
            binding.MaxPromptTokens is null && source.MaxPromptTokens is not null ||
            binding.MaxPromptTokens is { } promptLimit &&
                (source.MaxPromptTokens is null or < 1 || source.MaxPromptTokens > promptLimit))
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        RuntimeContractValidation.ValidateHash(source.CatalogHash);
        var resolvedPin = source.ResolvedModelBindingPin;
        if (resolvedPin is not null)
        {
            RuntimeModelBindingsResolver.ValidatePin(resolvedPin);
            if (resolvedPin.ModelSelectionReference != binding.ModelSelectionReference ||
                resolvedPin.ModelId != source.ModelId ||
                resolvedPin.SourceMode != binding.ModelSourceMode ||
                resolvedPin.ReasoningEffort != binding.ReasoningEffort ||
                binding.ModelBindingPin is { } acceptedPin && resolvedPin != acceptedPin)
                throw new RuntimeAuthorizationException("runtime_model_binding_pin_mismatch");
        }
        else if (binding.ModelBindingPin is not null || binding.ReasoningEffort != "high")
            throw new RuntimeAuthorizationException("runtime_model_binding_pin_required");
        if (binding.ModelSourceMode == ModelSourceMode.Byok)
        {
            if (binding.ModelBindingPin is not { } pin || source.ByokProvider is not { } provider ||
                provider.Type != pin.ProviderType || provider.Type is not ("azure" or "openai" or "anthropic") ||
                provider.DeploymentId != source.ModelId || provider.ConfigurationHash != pin.ConfigurationHash)
                throw new RuntimeAuthorizationException("runtime_byok_source_binding_invalid");
        }
        else if (source.ByokProvider is not null)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        RequireText(source.SdkVersion);
        RequireText(source.RuntimeVersion);
        RequireText(source.ModelId);
    }

    /// <summary>Reports whether a receipt claims Azure BYOK provenance without validating the claim.</summary>
    public static bool ClaimsAzureByok(RuntimeUsageSourceReceipt receipt) =>
        // A receipt claims Azure BYOK when either side says so: the SDK source that the runtime
        // reported, or the model binding pin that the owner registered. Checking both sides means a
        // receipt can't avoid the provenance check by setting Azure on only one of them.
        // Null-safe access lets callers run this before full validation.
        receipt?.Usage?.SdkSource is { SourceMode: "byok", ByokProvider.Type: "azure" } ||
        receipt?.Registration?.Binding?.ModelBindingPin?.ProviderType == "azure";

    /// <summary>Proves that an owner-issued receipt is trusted Azure BYOK usage for one dispatch.</summary>
    /// <remarks>
    /// The receipt must pass <see cref="Validate"/>. The registration must use BYOK with an Azure model
    /// binding pin. The SDK source must report the same Azure provider type, deployment ID, model ID,
    /// configuration hash, and the <c>byok.tokens</c> meter. The usage must carry its dispatch message ID.
    /// Events calls this check before it prices any Azure BYOK usage.
    /// </remarks>
    /// <returns>The provenance facts that Azure pricing and budget exposure use.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown with <c>runtime_byok_azure_provenance_invalid</c> when any fact does not match.</exception>
    public static TrustedAzureByokUsage RequireTrustedAzureByok(RuntimeUsageSourceReceipt receipt)
    {
        // First prove the source facts: provider, deployment, configuration, and meter.
        // This also recomputes and checks the receipt payload hash.
        var (pin, provider) = RequireAzureByokSource(receipt);

        // Pricing and budget exposure are charged to one dispatch. Usage without a dispatch message ID,
        // or with the empty GUID, can't be attributed to a dispatch, so it isn't trusted for pricing.
        // The caller can still record that usage as Unpriced after RequireAzureByokProvenance passes.
        if (receipt.Usage.A2AMessageId is not { } dispatchId || dispatchId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_byok_azure_provenance_invalid");

        // Return the dispatch ID plus facts from the validated receipt and the pin. The configuration
        // revision and hash come from the pin. The payload hash ties the result to the exact receipt.
        return new(receipt.ReceiptId, receipt.Registration.RuntimeInstanceId, receipt.Registration.Revision,
            dispatchId, provider.DeploymentId, pin.ConfigurationRevision, pin.ConfigurationHash,
            receipt.CanonicalPayloadHash);
    }

    /// <summary>Proves that an owner-issued receipt has consistent Azure BYOK source facts.</summary>
    /// <remarks>
    /// This check applies the same registration, provider, deployment, configuration, and meter rules as
    /// <see cref="RequireTrustedAzureByok"/>, but it doesn't require a dispatch message ID. Native SDK usage
    /// that isn't correlated to one dispatch passes this check. Events records that usage as Unpriced.
    /// </remarks>
    /// <exception cref="RuntimeAuthorizationException">Thrown with <c>runtime_byok_azure_provenance_invalid</c> when any fact does not match.</exception>
    public static void RequireAzureByokProvenance(RuntimeUsageSourceReceipt receipt) =>
        RequireAzureByokSource(receipt);

    private static (RuntimeModelBindingPin Pin, SdkByokProviderFacts Provider) RequireAzureByokSource(
        RuntimeUsageSourceReceipt receipt)
    {
        // Validate checks the receipt version and usage binding, then recomputes the canonical payload
        // hash. A changed registration or usage field no longer matches the hash and fails here, before
        // any Azure-specific check runs. Validate throws its own error codes.
        Validate(receipt);
        var binding = receipt.Registration.Binding;
        var usage = receipt.Usage;

        // Every Azure-specific failure below uses one error code so a caller can't learn which fact was wrong.

        // The owner registered this runtime for BYOK with an Azure pin. The pin is the trusted record
        // of the model and configuration that the owner accepted.
        if (binding.ModelSourceMode != ModelSourceMode.Byok ||
            binding.ModelBindingPin is not { ProviderType: "azure", SourceMode: ModelSourceMode.Byok } pin)
            throw new RuntimeAuthorizationException("runtime_byok_azure_provenance_invalid");

        // The runtime's SDK source reports Azure BYOK too. A source that reports hosted Copilot or
        // another BYOK provider doesn't match the pin.
        if (usage.SdkSource is not { SourceMode: "byok", ByokProvider: { Type: "azure" } provider } source)
            throw new RuntimeAuthorizationException("runtime_byok_azure_provenance_invalid");

        // BYOK usage is metered in raw tokens on both the source and the usage binding. A hosted
        // premium-request multiplier never applies to BYOK, so its presence means the source is mixed.
        if (source.MeterSource != SdkMeterSources.ByokTokens || source.ModelMultiplier is not null ||
            usage.ModelBinding.MeterSource != SdkMeterSources.ByokTokens)
            throw new RuntimeAuthorizationException("runtime_byok_azure_provenance_invalid");

        // On Azure, the deployment name is the model ID that the pin records. The SDK deployment, the
        // usage model, and the pin must all name the same deployment, so usage can't be billed against a
        // different deployment's rates.
        if (provider.DeploymentId != pin.ModelId || usage.ModelBinding.ModelId != pin.ModelId)
            throw new RuntimeAuthorizationException("runtime_byok_azure_provenance_invalid");

        // The configuration hash is a digest of the normalized model binding settings. A mismatch means
        // the runtime reported a binding configuration that differs from the one the owner pinned.
        if (provider.ConfigurationHash != pin.ConfigurationHash)
            throw new RuntimeAuthorizationException("runtime_byok_azure_provenance_invalid");

        return (pin, provider);
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

    /// <summary>Hashes the canonical registration and usage submission.</summary>
    public static string Hash(RuntimeRegistration registration, UsageSubmission usage) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new CanonicalSourceUsage(registration, usage), JsonOptions));

    /// <summary>Hashes the registration and SDK source facts used by usage receipts.</summary>
    public static string HashSource(RuntimeRegistration registration, SdkSessionFacts source) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(
            new { Registration = registration, Source = source }, JsonOptions));

    /// <summary>Maps an SDK observation into a scoped usage submission and validates the result.</summary>
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

    /// <summary>Fixed property shape for canonical usage receipt hashing.</summary>
    private sealed record CanonicalSourceUsage(RuntimeRegistration Registration, UsageSubmission Usage);
}
