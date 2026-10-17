using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Configures Copilot cost binding identity and operator-supplied published model rates.</summary>
public sealed record CopilotCostProviderOptions(
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion)
{
    /// <summary>The current options schema binds operator-supplied Copilot pricing data.</summary>
    public const int CurrentOptionsSchemaVersion = 2;
    public const decimal NanoAiuPerAiCredit = 1_000_000_000m;

    /// <summary>The validated immutable provider-published rates used by this adapter.</summary>
    public CostRateCard RateCard { get; init; } = null!;

    /// <summary>Reads cost binding identity and the strict runtime rate-card JSON document.</summary>
    /// <returns>Null when absent; incomplete identity, malformed cards, and legacy multipliers throw.</returns>
    public static CopilotCostProviderOptions? FromConfiguration(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;
        if (section.GetSection("RateCard:ModelMultipliers").GetChildren().Any())
            throw new ArgumentException(
                "Copilot model prices must be configured as a versioned published rate card, not multipliers.",
                nameof(section));

        var cardJson = section["RateCard:Json"];
        if (string.IsNullOrWhiteSpace(cardJson))
            throw new ArgumentException(
                "The operator-supplied Copilot rate-card JSON document is required.",
                nameof(section));
        var options = new CopilotCostProviderOptions(
            section[nameof(ResourceId)] ?? string.Empty,
            section.GetValue<long>(nameof(ResourceGeneration)),
            section[nameof(OptionsRevision)] ?? string.Empty,
            section.GetValue<int>(nameof(OptionsSchemaVersion)))
        {
            RateCard = CopilotRateCardConfiguration.Parse(cardJson)
        };
        options.Validate();
        return options;
    }

    /// <summary>Checks provider binding identity and the configured official pricing snapshot.</summary>
    public void Validate()
    {
        if (!IsToken(ResourceId, 256) || ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion)
            throw new ArgumentException("Copilot Cost provider options are invalid.");
        if (RateCard is null)
            throw new ArgumentException("The operator-supplied Copilot rate card is required.");
        CostRateCardContract.Validate(RateCard);
        if (RateCard.ModelMultipliers.Count != 0 ||
            RateCard.ProviderSourceUrl != CopilotPublishedRateCard.SourceUrl ||
            RateCard.ContentHash is null ||
            RateCard.MeterSource != CopilotCostProvider.MeterSource ||
            RateCard.Unit != CopilotCostProvider.AiCreditUnit ||
            RateCard.NanoUnitsPerUnit != NanoAiuPerAiCredit ||
            RateCard.TokenRateCurrency != "USD" ||
            RateCard.TokensPerRateUnit != 1_000_000m ||
            RateCard.CreditsPerCurrencyUnit != CopilotPublishedRateCard.AiCreditsPerUsd)
            throw new ArgumentException("The Copilot provider rate card is invalid.");
    }

    /// <summary>Returns an immutable snapshot for run binding and accounting.</summary>
    internal CostRateCard SnapshotRateCard()
    {
        Validate();
        return RateCard with
        {
            ModelMultipliers = RateCard.ModelMultipliers.ToImmutableDictionary(StringComparer.Ordinal),
            ModelTokenRates = RateCard.ModelTokenRates.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    DefaultContext = SnapshotRates(pair.Value.DefaultContext),
                    LongContext = SnapshotRates(pair.Value.LongContext)
                },
                StringComparer.Ordinal)
        };
    }

    private static CostTokenRates? SnapshotRates(CostTokenRates? rates) =>
        rates is null ? null : rates with { };

    private static bool IsToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
