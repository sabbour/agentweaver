using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

public sealed record CopilotCostProviderOptions(
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion,
    CostRateCard RateCard)
{
    public const int CurrentOptionsSchemaVersion = 1;
    public const decimal NanoAiuPerAiCredit = 1_000_000_000m;

    public static CopilotCostProviderOptions? FromConfiguration(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;
        var card = section.GetRequiredSection(nameof(RateCard));
        var options = new CopilotCostProviderOptions(
            section[nameof(ResourceId)] ?? string.Empty,
            section.GetValue<long>(nameof(ResourceGeneration)),
            section[nameof(OptionsRevision)] ?? string.Empty,
            section.GetValue<int>(nameof(OptionsSchemaVersion)),
            new CostRateCard(
                card[nameof(CostRateCard.Id)] ?? string.Empty,
                card[nameof(CostRateCard.Version)] ?? string.Empty,
                card[nameof(CostRateCard.MeterSource)] ?? string.Empty,
                card[nameof(CostRateCard.Unit)] ?? string.Empty,
                card.GetValue<decimal>(nameof(CostRateCard.NanoUnitsPerUnit)),
                (card.GetSection(nameof(CostRateCard.ModelMultipliers))
                    .Get<Dictionary<string, decimal>>() ?? [])
                    .ToImmutableDictionary(StringComparer.Ordinal)));
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!IsToken(ResourceId, 256) || ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion ||
            RateCard is null || !IsToken(RateCard.Id, 128) ||
            !IsToken(RateCard.Version, 64) ||
            RateCard.MeterSource != CopilotCostProvider.MeterSource ||
            RateCard.Unit != CopilotCostProvider.AiCreditUnit ||
            RateCard.NanoUnitsPerUnit != NanoAiuPerAiCredit ||
            RateCard.ModelMultipliers is null ||
            RateCard.ModelMultipliers.Any(rate =>
                !IsToken(rate.Key, 256) || rate.Value <= 0))
            throw new ArgumentException("Copilot Cost provider options or rate card are invalid.");
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
            throw new ArgumentException("Copilot Cost provider model rates are ambiguous.");
        }
    }

    private static bool IsToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
