using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Configures Copilot cost binding identity; published model rates are provider-owned.</summary>
public sealed record CopilotCostProviderOptions(
    string ResourceId,
    long ResourceGeneration,
    string OptionsRevision,
    int OptionsSchemaVersion)
{
    /// <summary>The current options schema excludes operator-supplied Copilot prices.</summary>
    public const int CurrentOptionsSchemaVersion = 2;
    public const decimal NanoAiuPerAiCredit = 1_000_000_000m;

    /// <summary>The immutable provider-published rates used by this adapter.</summary>
    public CostRateCard RateCard => CopilotPublishedRateCard.Current;

    /// <summary>Reads cost binding identity; Copilot token rates are never sourced from configuration.</summary>
    /// <returns>Null when absent; incomplete identity or legacy model multipliers throw.</returns>
    public static CopilotCostProviderOptions? FromConfiguration(IConfigurationSection section)
    {
        if (!section.Exists())
            return null;
        if (section.GetSection("RateCard:ModelMultipliers").GetChildren().Any())
            throw new ArgumentException(
                "Copilot model prices are provider-published and cannot be configured as multipliers.",
                nameof(section));

        var options = new CopilotCostProviderOptions(
            section[nameof(ResourceId)] ?? string.Empty,
            section.GetValue<long>(nameof(ResourceGeneration)),
            section[nameof(OptionsRevision)] ?? string.Empty,
            section.GetValue<int>(nameof(OptionsSchemaVersion)));
        options.Validate();
        return options;
    }

    /// <summary>Checks provider binding identity and the embedded official pricing snapshot.</summary>
    public void Validate()
    {
        if (!IsToken(ResourceId, 256) || ResourceGeneration < 1 ||
            !IsToken(OptionsRevision, 128) ||
            OptionsSchemaVersion != CurrentOptionsSchemaVersion)
            throw new ArgumentException("Copilot Cost provider options are invalid.");
        CostRateCardContract.Validate(RateCard);
        if (RateCard.ModelMultipliers.Count != 0 ||
            RateCard.ProviderSourceUrl != CopilotPublishedRateCard.SourceUrl ||
            RateCard.SnapshotVersion != CopilotPublishedRateCard.SnapshotVersion)
            throw new ArgumentException("The Copilot provider rate card is invalid.");
    }

    /// <summary>Returns the immutable provider-owned card for run binding and accounting.</summary>
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
