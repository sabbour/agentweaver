using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Capabilities advertised by the Copilot usage-pricing adapter.</summary>
public static class CopilotCostCapabilities
{
    public const string PriceUsage = "cost.usage.price";
    public const string QuoteWork = "cost.work.quote";

    public static ImmutableHashSet<string> All { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, PriceUsage, QuoteWork);
}

/// <summary>Prices Copilot token usage and quotes supported work from a pinned provider rate card.</summary>
public sealed class CopilotCostProvider : ICostProvider
{
    /// <summary>The stable meter-source key for Copilot SDK TotalNanoAiu values.</summary>
    public const string MeterSource = SdkMeterSources.CopilotNanoAiu;
    public const string NanoAiuUnit = "nano_aiu";
    public const string AiCreditUnit = "AIC";
    public const string ProviderId = "copilot.usage-cost";

    public static Version AdapterVersion { get; } = new(1, 0, 0);
    public const int OptionsSchemaVersion = CopilotCostProviderOptions.CurrentOptionsSchemaVersion;

    private readonly CostRateCard _rateCard;

    public CopilotCostProvider(CopilotCostProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        _rateCard = options.SnapshotRateCard();
    }

    public CopilotCostProviderOptions Options { get; }

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Cost,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.InProcess,
        CopilotCostCapabilities.All);

    /// <summary>Creates the enabled catalog registration after validating provider-owned pricing.</summary>
    public ProviderRegistration CreateRegistration()
    {
        Options.Validate();
        return new ProviderRegistration(
            Descriptor,
            Enabled: true,
            Options.OptionsRevision,
            Options.OptionsSchemaVersion);
    }

    /// <summary>Negotiates only candidates matching this adapter's options revision and schema.</summary>
    public ProviderResult<ResourceNegotiation> Negotiate(ProviderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Seam != ProviderSeam.Cost || candidate.ProviderId != ProviderId)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.ProviderSeamMismatch,
                "The selected provider is not the Copilot Cost adapter.");
        if (candidate.AdapterVersion != AdapterVersion)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.AdapterVersionMismatch,
                "The Copilot Cost adapter version is not supported.");
        if (candidate.OptionsSchemaVersion != Options.OptionsSchemaVersion)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.OptionsSchemaMismatch,
                "The Copilot Cost options schema does not match the configured options.");
        if (candidate.OptionsRevision != Options.OptionsRevision)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.PinnedBindingMismatch,
                "The Copilot Cost options revision does not match the configured options.");

        // Copilot usage units are carried in usage records; this binds the configured meter
        // identity and adapter capabilities without claiming remote source readiness.
        return ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
            new ProviderResourceRef(
                ProviderSeam.Cost, ProviderId, Options.ResourceId, Options.ResourceGeneration),
            CopilotCostCapabilities.All));
    }

    /// <summary>Builds the persisted cost binding from a matching provider pin and rate-card snapshot.</summary>
    public CostBinding CreateBinding(PinnedCostProviderBinding pinned)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        var binding = pinned.ProviderBinding;
        if (pinned.MeterSource != MeterSource || binding.Seam != ProviderSeam.Cost ||
            binding.ProviderId != ProviderId || binding.AdapterVersion != AdapterVersion ||
            binding.OptionsSchemaVersion != Options.OptionsSchemaVersion ||
            binding.OptionsRevision != Options.OptionsRevision ||
            binding.Resource.ResourceId != Options.ResourceId ||
            binding.Resource.Generation != Options.ResourceGeneration ||
            !binding.NegotiatedCapabilities.SetEquals(CopilotCostCapabilities.All))
            throw new ArgumentException("The pinned Copilot Cost binding does not match its options.", nameof(pinned));

        return new CostBinding(
            pinned.MeterSource,
            binding.ProviderId,
            binding.AdapterVersion.ToString(),
            binding.OptionsSchemaVersion,
            binding.OptionsRevision,
            binding.Resource.ResourceId,
            binding.Resource.Generation,
            binding.NegotiatedCapabilities,
            _rateCard);
    }

    /// <summary>Prices the reported input, cached-input, cache-write, and output token categories.</summary>
    public CostPrice Price(UsageMeasurement usage, UsageModelBinding model, CostBinding binding)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(binding);
        ValidateMeasurement(usage);

        if (!IsBindingCurrent(binding))
            return Unpriced("binding-mismatch");
        if (!string.Equals(model.MeterSource, MeterSource, StringComparison.Ordinal) ||
            !string.Equals(binding.MeterSource, model.MeterSource, StringComparison.Ordinal))
            return Unpriced("meter-source-mismatch");
        if (string.IsNullOrWhiteSpace(model.ModelId) ||
            !_rateCard.ModelTokenRates.ContainsKey(model.ModelId))
            return Unpriced("model-rate-unavailable");
        return PriceTokenMeasurement(model.ModelId, usage);
    }

    /// <summary>Prices published token estimates or already-weighted Nano AIU without multipliers.</summary>
    public CostPrice Quote(CostQuoteRequest plannedWork, CostBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plannedWork);
        ArgumentNullException.ThrowIfNull(binding);
        if (plannedWork.Units < 0)
            throw new ArgumentOutOfRangeException(nameof(plannedWork), "Planned units cannot be negative.");
        if (!IsBindingCurrent(binding))
            return Unpriced("binding-mismatch");
        if (!string.Equals(binding.MeterSource, MeterSource, StringComparison.Ordinal))
            return Unpriced("meter-source-mismatch");
        if (string.IsNullOrWhiteSpace(plannedWork.ModelId) ||
            !_rateCard.ModelTokenRates.ContainsKey(plannedWork.ModelId))
            return Unpriced("model-rate-unavailable");

        switch (plannedWork.Basis)
        {
            case CostQuoteBasis.ProviderWeightedNanoAiu:
                if (plannedWork.Units is null)
                    return Unpriced("planned-units-missing");
                return new CostPrice(
                    plannedWork.Units.Value / _rateCard.NanoUnitsPerUnit,
                    _rateCard.Unit,
                    CostDisposition.Estimate,
                    _rateCard,
                    null);
            case CostQuoteBasis.UnweightedAiCredits:
                return Unpriced("quote-basis-unsupported");
            case CostQuoteBasis.ProviderPublishedTokenRates:
                if (plannedWork.TokenMeasurement is null)
                    return Unpriced("token-measurement-missing");
                ValidateMeasurement(plannedWork.TokenMeasurement);
                return PriceTokenMeasurement(plannedWork.ModelId, plannedWork.TokenMeasurement);
            default:
                return Unpriced("quote-basis-unsupported");
        }
    }

    internal bool IsBindingCurrent(CostBinding binding) =>
        binding.MeterSource == MeterSource &&
        binding.ProviderId == ProviderId &&
        binding.AdapterVersion == AdapterVersion.ToString() &&
        binding.OptionsSchemaVersion == Options.OptionsSchemaVersion &&
        binding.OptionsRevision == Options.OptionsRevision &&
        binding.ResourceId == Options.ResourceId &&
        binding.ResourceGeneration == Options.ResourceGeneration &&
        binding.NegotiatedCapabilities is not null &&
        binding.NegotiatedCapabilities.SetEquals(CopilotCostCapabilities.All) &&
        RateCardsEqual(binding.RateCard, _rateCard);

    internal static bool RateCardsEqual(CostRateCard? left, CostRateCard right) =>
        CostRateCardContract.Equals(left, right);

    private CostPrice PriceTokenMeasurement(string modelId, UsageMeasurement usage)
    {
        if (usage.InputTokens is null || usage.OutputTokens is null ||
            usage.CachedTokens is null || usage.CacheWriteTokens is null)
            return Unpriced("token-measurement-incomplete");
        if (!_rateCard.ModelTokenRates.TryGetValue(modelId, out var modelRates))
            return Unpriced("model-rate-unavailable");

        var threshold = modelRates.LongContextThresholdTokens ?? _rateCard.LongContextThresholdTokens;
        var longContext = modelRates.LongContext is not null &&
            threshold is not null && usage.InputTokens.Value > threshold.Value;
        var rates = longContext ? modelRates.LongContext : modelRates.DefaultContext;
        if (rates is null)
            return Unpriced("context-tier-rate-unavailable");

        decimal amount = 0;
        var reason = AddTokenCategory(usage.InputTokens.Value, rates.Input, "input", ref amount);
        reason ??= AddTokenCategory(usage.CachedTokens.Value, rates.CachedInput, "cached-input", ref amount);
        reason ??= AddTokenCategory(usage.CacheWriteTokens.Value, rates.CacheWrite, "cache-write", ref amount);
        reason ??= AddTokenCategory(usage.OutputTokens.Value, rates.Output, "output", ref amount);
        if (reason is not null)
            return Unpriced(reason);

        return new CostPrice(amount, _rateCard.Unit, CostDisposition.Estimate, _rateCard, null);
    }

    private string? AddTokenCategory(long tokens, decimal? rate, string category, ref decimal amount)
    {
        if (tokens == 0)
            return null;
        if (rate is null)
            return $"token-rate-unavailable-{category}";
        amount = checked(amount +
            (tokens * rate.Value * _rateCard.CreditsPerCurrencyUnit!.Value) /
            _rateCard.TokensPerRateUnit!.Value);
        return null;
    }

    private CostPrice Unpriced(string reason) =>
        new(null, _rateCard.Unit, CostDisposition.Unpriced, _rateCard, reason);

    private static void ValidateMeasurement(UsageMeasurement usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CachedTokens < 0 ||
            usage.CacheWriteTokens < 0 || usage.ReasoningTokens < 0 || usage.RequestCount < 0 ||
            usage.ProviderUnits < 0 || usage.DurationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "Usage measurements cannot be negative.");
    }
}
