using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Provider-supplied GitHub Copilot token rates from the official pricing snapshot.</summary>
public static class CopilotPublishedRateCard
{
    /// <summary>The official GitHub Copilot model-pricing documentation.</summary>
    public const string SourceUrl = "https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing";

    /// <summary>Retrieval date and immutable provider snapshot version.</summary>
    public const string SnapshotVersion = "2026-10-17";

    /// <summary>AI credits per USD, using GitHub's published 1 AIC = $0.01 definition.</summary>
    public const decimal AiCreditsPerUsd = 100m;

    /// <summary>Returns the immutable card for rates verified on the snapshot date.</summary>
    public static CostRateCard Current { get; } = new(
        "github-copilot-models-and-pricing",
        SnapshotVersion,
        CopilotCostProvider.MeterSource,
        CopilotCostProvider.AiCreditUnit,
        CopilotCostProviderOptions.NanoAiuPerAiCredit,
        ImmutableDictionary<string, decimal>.Empty.WithComparers(StringComparer.Ordinal))
    {
        ProviderSourceUrl = SourceUrl,
        SnapshotVersion = SnapshotVersion,
        RetrievedOn = new DateOnly(2026, 10, 17),
        TokenRateCurrency = "USD",
        TokensPerRateUnit = 1_000_000m,
        CreditsPerCurrencyUnit = AiCreditsPerUsd,
        LongContextThresholdTokens = 272_000,
        ModelTokenRates = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, CostModelTokenRates>(
                "gpt-6.1-sol",
                new(
                    new(2.00m, 0.10m, 2.50m, 10.00m),
                    new(4.00m, 0.20m, 5.00m, 15.00m))),
            new KeyValuePair<string, CostModelTokenRates>(
                "gpt-6-luna",
                new(
                    new(0.10m, 0.01m, 0.125m, 0.50m),
                    new(0.20m, 0.02m, 0.25m, 0.75m)))
        ])
    };
}
