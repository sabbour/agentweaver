using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.EventsAndSessions.Cost;

public static class AzureCostCapabilities
{
    public const string PriceUsage = "cost.usage.price";

    public static ImmutableHashSet<string> All { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, PriceUsage);
}

public sealed class AzureCostProvider : ICostProvider
{
    public const string MeterSource = SdkMeterSources.ByokTokens;
    public const string ProviderId = "azure.byok-usage-cost";

    public static Version AdapterVersion { get; } = new(1, 0, 0);
    public const int OptionsSchemaVersion = AzureCostProviderOptions.CurrentOptionsSchemaVersion;

    private readonly CostRateCard _rateCard;

    public AzureCostProvider(AzureCostProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options with { RateCard = options.SnapshotRateCard() };
        _rateCard = Options.RateCard;
    }

    public AzureCostProviderOptions Options { get; }

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderSeam.Cost,
        ProviderId,
        AdapterVersion,
        OptionsSchemaVersion,
        ProviderHostingPattern.InProcess,
        AzureCostCapabilities.All);

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
                "The selected provider is not the Azure Cost adapter.");
        if (candidate.AdapterVersion != AdapterVersion)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.AdapterVersionMismatch,
                "The Azure Cost adapter version is not supported.");
        if (candidate.OptionsSchemaVersion != Options.OptionsSchemaVersion)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.OptionsSchemaMismatch,
                "The Azure Cost options schema does not match the configured options.");
        if (candidate.OptionsRevision != Options.OptionsRevision)
            return ProviderResult<ResourceNegotiation>.Failure(
                ProviderErrorCode.PinnedBindingMismatch,
                "The Azure Cost options revision does not match the configured options.");

        return ProviderResult<ResourceNegotiation>.Success(new ResourceNegotiation(
            new ProviderResourceRef(
                ProviderSeam.Cost, ProviderId, Options.ResourceId, Options.ResourceGeneration),
            AzureCostCapabilities.All));
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
            !binding.NegotiatedCapabilities.SetEquals(AzureCostCapabilities.All))
            throw new ArgumentException("The pinned Azure Cost binding does not match its options.", nameof(pinned));

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
        if (Options.PricingModel == AzureCostPricingModel.ProvisionedThroughput)
            return Unpriced("provisioned-throughput-share-unavailable");
        if (string.IsNullOrWhiteSpace(model.ModelId))
            return Unpriced("model-rate-unavailable");
        if (usage.ProviderUnits is not null || usage.ProviderUnit is not null)
            return Unpriced("provider-units-unsupported");
        if (usage.InputTokens is null || usage.OutputTokens is null)
            return Unpriced("token-measurements-missing");
        if (usage.CachedTokens is null || usage.CacheWriteTokens is null)
            return Unpriced("token-cache-measurements-missing");
        if (usage.CachedTokens != 0 || usage.CacheWriteTokens != 0)
            return Unpriced("token-cache-pricing-unavailable");
        if (usage.ReasoningTokens is not null && usage.ReasoningTokens > usage.OutputTokens)
            return Unpriced("token-measurement-inconsistent");
        if (!_rateCard.ModelMultipliers.TryGetValue(
                AzureCostProviderOptions.InputRateKey(model.ModelId), out var inputRate) ||
            !_rateCard.ModelMultipliers.TryGetValue(
                AzureCostProviderOptions.OutputRateKey(model.ModelId), out var outputRate))
            return Unpriced("model-rate-unavailable");

        decimal amount;
        checked
        {
            amount = ((decimal)usage.InputTokens.Value * inputRate +
                (decimal)usage.OutputTokens.Value * outputRate) / _rateCard.NanoUnitsPerUnit;
        }

        return new CostPrice(amount, _rateCard.Unit, CostDisposition.Estimate, _rateCard, null);
    }

    public CostPrice Quote(CostQuoteRequest plannedWork, CostBinding binding)
    {
        ArgumentNullException.ThrowIfNull(plannedWork);
        ArgumentNullException.ThrowIfNull(binding);
        if (plannedWork.Units < 0)
            throw new ArgumentOutOfRangeException(nameof(plannedWork), "Planned units cannot be negative.");
        if (!IsBindingCurrent(binding))
            return Unpriced("binding-mismatch");
        return Unpriced("byok-token-quotes-unsupported");
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
        binding.NegotiatedCapabilities.SetEquals(AzureCostCapabilities.All) &&
        RateCardsEqual(binding.RateCard, _rateCard);

    private CostPrice Unpriced(string reason) =>
        new(null, _rateCard.Unit, CostDisposition.Unpriced, _rateCard, reason);

    private static bool RateCardsEqual(CostRateCard? left, CostRateCard right) =>
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

    private static void ValidateMeasurement(UsageMeasurement usage)
    {
        if (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CachedTokens < 0 ||
            usage.CacheWriteTokens < 0 || usage.ReasoningTokens < 0 || usage.RequestCount < 0 ||
            usage.ProviderUnits < 0 || usage.DurationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "Usage measurements cannot be negative.");
    }
}
