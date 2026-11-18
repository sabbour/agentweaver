using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

public abstract record RuntimeRateCardProvenance;

public sealed record ManualRateCardProvenance(string SourceUrl, DateOnly RetrievedOn)
    : RuntimeRateCardProvenance;

public sealed record AzureRetailRateCardProvenance(string Region, ImmutableArray<string> MeterIds)
    : RuntimeRateCardProvenance;

public sealed record RuntimeRateCard(CostRateCard RateCard, RuntimeRateCardProvenance Provenance);

public static class RuntimeRateCardContract
{
    public const int CurrentSchemaVersion = RuntimeRateCardReadContract.CurrentSchemaVersion;

    public static void Validate(RuntimeRateCard runtimeRateCard)
    {
        ArgumentNullException.ThrowIfNull(runtimeRateCard);
        RuntimeRateCardReadContract.Validate(ToReadResponse(runtimeRateCard));
        var card = runtimeRateCard.RateCard;
        AzureCostProviderOptions.ValidateRateCardPricing(card, AzureCostPricingModel.StandardTokens);
    }

    public static void ValidateIdentity(string id, string version) =>
        RuntimeRateCardReadContract.ValidateIdentity(id, version);

    public static string SerializeCanonical(RuntimeRateCard runtimeRateCard) =>
        RuntimeRateCardReadContract.SerializeCanonical(ToReadResponse(runtimeRateCard));

    internal static RuntimeRateCard? DeserializeRuntimePayload(string payload)
    {
        try
        {
            var response = RuntimeRateCardReadContract.Deserialize(Encoding.UTF8.GetBytes(payload));
            if (response is null)
                return null;
            RuntimeRateCardProvenance provenance = response.Provenance switch
            {
                ManualRuntimeRateCardReadProvenance manual =>
                    new ManualRateCardProvenance(manual.SourceUrl, manual.RetrievedOn),
                AzureRetailRuntimeRateCardReadProvenance retail =>
                    new AzureRetailRateCardProvenance(retail.Region, retail.MeterIds),
                _ => throw new JsonException("The runtime rate-card provenance is not supported.")
            };
            var runtimeRateCard = new RuntimeRateCard(response.RateCard, provenance);
            Validate(runtimeRateCard);
            return runtimeRateCard;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("The stored runtime rate card is invalid.", exception);
        }
    }

    internal static RuntimeRateCardReadResponse ToReadResponse(RuntimeRateCard runtimeRateCard)
    {
        ArgumentNullException.ThrowIfNull(runtimeRateCard);
        RuntimeRateCardReadProvenance provenance = runtimeRateCard.Provenance switch
        {
            ManualRateCardProvenance manual =>
                new ManualRuntimeRateCardReadProvenance(manual.SourceUrl, manual.RetrievedOn),
            AzureRetailRateCardProvenance retail =>
                new AzureRetailRuntimeRateCardReadProvenance(retail.Region, retail.MeterIds),
            _ => throw new ArgumentException("Runtime rate-card provenance is not supported.", nameof(runtimeRateCard)),
        };
        return new RuntimeRateCardReadResponse(CurrentSchemaVersion, runtimeRateCard.RateCard, provenance);
    }
}
