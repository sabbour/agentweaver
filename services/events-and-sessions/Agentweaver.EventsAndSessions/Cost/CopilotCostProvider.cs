using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.EventsAndSessions.Cost;

public static class CopilotCostCapabilities
{
    public const string PriceUsage = "cost.usage.price";
    public const string QuoteWork = "cost.work.quote";

    public static ImmutableHashSet<string> All { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, PriceUsage, QuoteWork);
}

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
        Options = options with { RateCard = options.SnapshotRateCard() };
        _rateCard = Options.RateCard;
    }

    public CopilotCostProviderOptions Options { get; }

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Cost,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.InProcess,
        CopilotCostCapabilities.All);

    public ProviderRegistration CreateRegistration()
    {
        Options.Validate();
        return new ProviderRegistration(
            Descriptor,
            Enabled: true,
            Options.OptionsRevision,
            Options.OptionsSchemaVersion);
    }

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
            !_rateCard.ModelMultipliers.ContainsKey(model.ModelId))
            return Unpriced("model-rate-unavailable");
        if (usage.ProviderUnits is null)
            return Unpriced("provider-units-missing");
        if (usage.ProviderUnit is null)
            return Unpriced("provider-unit-missing");
        if (!string.Equals(usage.ProviderUnit, NanoAiuUnit, StringComparison.Ordinal))
            return Unpriced("provider-unit-unsupported");

        return new CostPrice(
            usage.ProviderUnits.Value / _rateCard.NanoUnitsPerUnit,
            _rateCard.Unit,
            CostDisposition.Estimate,
            _rateCard,
            null);
    }

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
            !_rateCard.ModelMultipliers.TryGetValue(plannedWork.ModelId, out var multiplier))
            return Unpriced("model-rate-unavailable");
        if (plannedWork.Units is null)
            return Unpriced("planned-units-missing");

        decimal amount;
        switch (plannedWork.Basis)
        {
            case CostQuoteBasis.ProviderWeightedNanoAiu:
                amount = plannedWork.Units.Value / _rateCard.NanoUnitsPerUnit;
                break;
            case CostQuoteBasis.UnweightedAiCredits:
                amount = plannedWork.Units.Value * multiplier;
                break;
            default:
                return Unpriced("quote-basis-unsupported");
        }

        return new CostPrice(amount, _rateCard.Unit, CostDisposition.Estimate, _rateCard, null);
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
        left is not null &&
        left.Id == right.Id &&
        left.Version == right.Version &&
        left.MeterSource == right.MeterSource &&
        left.Unit == right.Unit &&
        left.NanoUnitsPerUnit == right.NanoUnitsPerUnit &&
        left.ModelMultipliers is not null &&
        left.ModelMultipliers.Count == right.ModelMultipliers.Count &&
        left.ModelMultipliers.All(pair =>
            right.ModelMultipliers.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private CostPrice Unpriced(string reason) =>
        new(null, _rateCard.Unit, CostDisposition.Unpriced, _rateCard, reason);

    private static void ValidateMeasurement(UsageMeasurement usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CachedTokens < 0 ||
            usage.ReasoningTokens < 0 || usage.RequestCount < 0 ||
            usage.ProviderUnits < 0 || usage.DurationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "Usage measurements cannot be negative.");
    }
}
