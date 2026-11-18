using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public enum ModelProviderBindingScope
{
    Platform,
    Project,
}

public sealed record ModelProviderBindingRevision(
    int ContractVersion,
    string ProviderId,
    ModelProviderBindingScope Scope,
    string? ProjectId,
    string DisplayName,
    string ProviderType,
    string BaseUrl,
    string Deployment,
    string? WireApi,
    string? AzureApiVersion,
    ImmutableArray<string> ReasoningEfforts,
    int? PromptCapacityTokens,
    long ProviderRevision,
    string? CredentialVersion,
    string? RateCardId,
    string? RateCardVersion,
    string ConfigurationHash)
{
    public const int CurrentContractVersion = 4;

    public ModelCredentialIdentity? CredentialIdentity { get; init; }

    public SecretRef? CredentialReference { get; init; }

    public RuntimeRateCardPin? RateCardPin { get; init; }

    public bool Equals(ModelProviderBindingRevision? other) =>
        other is not null &&
        ContractVersion == other.ContractVersion &&
        ProviderId == other.ProviderId &&
        Scope == other.Scope &&
        ProjectId == other.ProjectId &&
        DisplayName == other.DisplayName &&
        ProviderType == other.ProviderType &&
        BaseUrl == other.BaseUrl &&
        Deployment == other.Deployment &&
        WireApi == other.WireApi &&
        AzureApiVersion == other.AzureApiVersion &&
        ReasoningEfforts.IsDefault == other.ReasoningEfforts.IsDefault &&
        (ReasoningEfforts.IsDefault ||
         ReasoningEfforts.SequenceEqual(other.ReasoningEfforts, StringComparer.Ordinal)) &&
        PromptCapacityTokens == other.PromptCapacityTokens &&
        ProviderRevision == other.ProviderRevision &&
        CredentialVersion == other.CredentialVersion &&
        RateCardId == other.RateCardId &&
        RateCardVersion == other.RateCardVersion &&
        ConfigurationHash == other.ConfigurationHash &&
        CredentialIdentity == other.CredentialIdentity &&
        CredentialReference == other.CredentialReference &&
        RateCardPinsEqual(RateCardPin, other.RateCardPin);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ContractVersion);
        hash.Add(ProviderId, StringComparer.Ordinal);
        hash.Add(Scope);
        hash.Add(ProjectId, StringComparer.Ordinal);
        hash.Add(DisplayName, StringComparer.Ordinal);
        hash.Add(ProviderType, StringComparer.Ordinal);
        hash.Add(BaseUrl, StringComparer.Ordinal);
        hash.Add(Deployment, StringComparer.Ordinal);
        hash.Add(WireApi, StringComparer.Ordinal);
        hash.Add(AzureApiVersion, StringComparer.Ordinal);
        hash.Add(ReasoningEfforts.IsDefault);
        if (!ReasoningEfforts.IsDefault)
            foreach (var effort in ReasoningEfforts)
                hash.Add(effort, StringComparer.Ordinal);
        hash.Add(PromptCapacityTokens);
        hash.Add(ProviderRevision);
        hash.Add(CredentialVersion, StringComparer.Ordinal);
        hash.Add(RateCardId, StringComparer.Ordinal);
        hash.Add(RateCardVersion, StringComparer.Ordinal);
        hash.Add(ConfigurationHash, StringComparer.Ordinal);
        hash.Add(CredentialIdentity);
        hash.Add(CredentialReference);
        AddRateCardPinHash(ref hash, RateCardPin);
        return hash.ToHashCode();
    }

    private static bool RateCardPinsEqual(RuntimeRateCardPin? left, RuntimeRateCardPin? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left.RateCardId != right.RateCardId ||
            left.RateCardVersion != right.RateCardVersion ||
            left.ContentHash != right.ContentHash)
            return false;
        return (left.Provenance, right.Provenance) switch
        {
            (ManualRuntimeRateCardReadProvenance a, ManualRuntimeRateCardReadProvenance b) =>
                a.SourceUrl == b.SourceUrl && a.RetrievedOn == b.RetrievedOn,
            (AzureRetailRuntimeRateCardReadProvenance a, AzureRetailRuntimeRateCardReadProvenance b) =>
                a.Region == b.Region && a.MeterIds.IsDefault == b.MeterIds.IsDefault &&
                (a.MeterIds.IsDefault || a.MeterIds.SequenceEqual(b.MeterIds, StringComparer.Ordinal)),
            _ => false
        };
    }

    private static void AddRateCardPinHash(ref HashCode hash, RuntimeRateCardPin? pin)
    {
        if (pin is null)
        {
            hash.Add(false);
            return;
        }
        hash.Add(true);
        hash.Add(pin.RateCardId, StringComparer.Ordinal);
        hash.Add(pin.RateCardVersion, StringComparer.Ordinal);
        hash.Add(pin.ContentHash, StringComparer.Ordinal);
        switch (pin.Provenance)
        {
            case ManualRuntimeRateCardReadProvenance manual:
                hash.Add(manual.SourceUrl, StringComparer.Ordinal);
                hash.Add(manual.RetrievedOn);
                break;
            case AzureRetailRuntimeRateCardReadProvenance retail:
                hash.Add(retail.Region, StringComparer.Ordinal);
                hash.Add(retail.MeterIds.IsDefault);
                if (!retail.MeterIds.IsDefault)
                    foreach (var meterId in retail.MeterIds)
                        hash.Add(meterId, StringComparer.Ordinal);
                break;
        }
    }
}
