using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

public enum AzureCostPricingModel
{
    StandardTokens,
    ProvisionedThroughput
}

public sealed record AzureCostProviderOptions(
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion,
    AzureCostPricingModel PricingModel,
    CostRateCard RateCard)
{
    public const int CurrentOptionsSchemaVersion = 1;
    public const decimal TokensPerRateUnit = 1_000_000m;
    public const string RateCardSource = "azure-retail-prices";

    public static AzureCostProviderOptions? FromConfiguration(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!section.Exists())
            return null;

        var card = section.GetRequiredSection(nameof(RateCard));
        var modelRates = card.GetSection(nameof(CostRateCard.ModelMultipliers))
            .Get<Dictionary<string, decimal>>() ?? [];
        var pricingModel = section[nameof(PricingModel)] switch
        {
            nameof(AzureCostPricingModel.StandardTokens) => AzureCostPricingModel.StandardTokens,
            nameof(AzureCostPricingModel.ProvisionedThroughput) => AzureCostPricingModel.ProvisionedThroughput,
            _ => throw new ArgumentException("Azure Cost pricing model must be explicitly configured.")
        };
        var options = new AzureCostProviderOptions(
            section[nameof(ResourceId)] ?? string.Empty,
            section.GetValue<long>(nameof(ResourceGeneration)),
            section[nameof(OptionsRevision)] ?? string.Empty,
            section.GetValue<int>(nameof(OptionsSchemaVersion)),
            pricingModel,
            new CostRateCard(
                card[nameof(CostRateCard.Id)] ?? string.Empty,
                card[nameof(CostRateCard.Version)] ?? string.Empty,
                card[nameof(CostRateCard.MeterSource)] ?? string.Empty,
                card[nameof(CostRateCard.Unit)] ?? string.Empty,
                card.GetValue<decimal>(nameof(CostRateCard.NanoUnitsPerUnit)),
                modelRates.ToImmutableDictionary(StringComparer.Ordinal)));
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!IsToken(ResourceId, 256) || ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion ||
            !Enum.IsDefined(PricingModel) ||
            RateCard is null ||
            !IsToken(RateCard.Id, 128) ||
            !RateCard.Id.StartsWith(
                $"{RateCardSource}.{PricingModelId(PricingModel)}.", StringComparison.Ordinal) ||
            !IsToken(RateCard.Version, 64) ||
            RateCard.MeterSource != AzureCostProvider.MeterSource ||
            !IsCurrency(RateCard.Unit) ||
            RateCard.NanoUnitsPerUnit <= 0 ||
            RateCard.ModelMultipliers is null)
            throw new ArgumentException("Azure Cost provider options or rate card are invalid.");

        if (PricingModel == AzureCostPricingModel.ProvisionedThroughput)
        {
            if (RateCard.ModelMultipliers.Count != 0)
                throw new ArgumentException(
                    "Provisioned-throughput rates require usage-share evidence and cannot be priced per token.");
            return;
        }

        if (RateCard.NanoUnitsPerUnit != TokensPerRateUnit)
            throw new ArgumentException("Azure token rates must be denominated per million tokens.");

        var modelCategories = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var rate in RateCard.ModelMultipliers)
        {
            if (!TryReadRateKey(rate.Key, out var modelId, out var category) ||
                rate.Value <= 0)
                throw new ArgumentException("Azure token rates require positive, model-scoped input and output rates.");

            if (!modelCategories.TryGetValue(modelId, out var categories))
            {
                categories = new HashSet<string>(StringComparer.Ordinal);
                modelCategories.Add(modelId, categories);
            }

            categories.Add(category);
        }

        if (modelCategories.Count == 0 || modelCategories.Values.Any(categories =>
                !categories.SetEquals(["input", "output"])))
            throw new ArgumentException("Each Azure model requires explicit input and output rates.");
    }

    internal CostRateCard SnapshotRateCard()
    {
        Validate();
        try
        {
            return RateCard with
            {
                ModelMultipliers = RateCard.ModelMultipliers.ToImmutableDictionary(
                    pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
        }
        catch (ArgumentException)
        {
            throw new ArgumentException("Azure Cost model rates are ambiguous.");
        }
    }

    internal static string InputRateKey(string modelId) => $"{modelId}|input";
    internal static string OutputRateKey(string modelId) => $"{modelId}|output";

    private static string PricingModelId(AzureCostPricingModel pricingModel) =>
        pricingModel switch
        {
            AzureCostPricingModel.StandardTokens => "standard",
            AzureCostPricingModel.ProvisionedThroughput => "ptu",
            _ => throw new ArgumentOutOfRangeException(nameof(pricingModel))
        };

    private static bool TryReadRateKey(string? key, out string modelId, out string category)
    {
        modelId = string.Empty;
        category = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;
        var separator = key.LastIndexOf('|');
        if (separator <= 0 || separator != key.IndexOf('|'))
            return false;
        modelId = key[..separator];
        category = key[(separator + 1)..];
        return IsToken(modelId, 256) && category is "input" or "output";
    }

    private static bool IsCurrency(string? value) =>
        value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z');

    private static bool IsToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
