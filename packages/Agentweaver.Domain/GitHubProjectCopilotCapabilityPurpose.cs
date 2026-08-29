namespace Agentweaver.Domain;

/// <summary>
/// The explicitly bounded non-run operation authorized by a project Copilot capability.
/// A capability for one purpose must never be redeemable by another purpose.
/// </summary>
public enum GitHubProjectCopilotCapabilityPurpose
{
    MarketplaceCatalogClassification,
    BacklogDecomposition,
}
