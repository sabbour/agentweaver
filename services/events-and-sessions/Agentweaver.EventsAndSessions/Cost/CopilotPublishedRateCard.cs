namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Stable provenance and unit constants for GitHub-published Copilot rates.</summary>
public static class CopilotPublishedRateCard
{
    /// <summary>The official GitHub Copilot model-pricing documentation.</summary>
    public const string SourceUrl = "https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing";

    /// <summary>AI credits per USD, using GitHub's published 1 AIC = $0.01 definition.</summary>
    public const decimal AiCreditsPerUsd = 100m;
}
