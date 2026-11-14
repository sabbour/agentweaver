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
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Validate(RuntimeRateCard runtimeRateCard)
    {
        ArgumentNullException.ThrowIfNull(runtimeRateCard);
        ArgumentNullException.ThrowIfNull(runtimeRateCard.RateCard);
        ArgumentNullException.ThrowIfNull(runtimeRateCard.Provenance);
        CostRateCardContract.Validate(runtimeRateCard.RateCard);

        var card = runtimeRateCard.RateCard;
        ValidateIdentity(card.Id, card.Version);
        if (card.Unit != "USD" ||
            card.ModelTokenRates.Count != 0 ||
            card.ProviderSourceUrl is not null ||
            card.SnapshotVersion is not null ||
            card.RetrievedOn is not null ||
            card.TokenRateCurrency is not null ||
            card.TokensPerRateUnit is not null ||
            card.CreditsPerCurrencyUnit is not null ||
            card.LongContextThresholdTokens is not null ||
            card.ContentHash is not null)
            throw new ArgumentException("A runtime Azure rate card must use USD token prices and manual or retail provenance.");

        AzureCostProviderOptions.ValidateRateCardPricing(card, AzureCostPricingModel.StandardTokens);

        switch (runtimeRateCard.Provenance)
        {
            case ManualRateCardProvenance manual when
                Uri.TryCreate(manual.SourceUrl, UriKind.Absolute, out var source) &&
                source.Scheme == Uri.UriSchemeHttps &&
                !string.IsNullOrEmpty(source.Host) &&
                string.IsNullOrEmpty(source.UserInfo) &&
                manual.RetrievedOn != default:
                break;
            case AzureRetailRateCardProvenance retail when
                IsRegion(retail.Region) &&
                card.Id == $"{AzureCostProviderOptions.RateCardSource}.standard.{retail.Region}" &&
                !retail.MeterIds.IsDefaultOrEmpty &&
                retail.MeterIds.All(IsIdentifier) &&
                retail.MeterIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == retail.MeterIds.Length:
                break;
            case ManualRateCardProvenance:
                throw new ArgumentException("Manual rate cards require an HTTPS source URL and retrieval date.");
            case AzureRetailRateCardProvenance:
                throw new ArgumentException("Azure Retail rate cards require a matching region and distinct meter IDs.");
            default:
                throw new ArgumentException("Runtime rate-card provenance is not supported.");
        }
    }

    public static void ValidateIdentity(string id, string version)
    {
        if (!IsIdentifier(id) || id.Length > 128 ||
            !IsIdentifier(version) || version.Length > 64)
            throw new ArgumentException("A valid runtime rate-card ID and version are required.");
    }

    public static string SerializeCanonical(RuntimeRateCard runtimeRateCard)
    {
        Validate(runtimeRateCard);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("runtimeRateCardSchemaVersion", CurrentSchemaVersion);
            writer.WritePropertyName("rateCard");
            writer.WriteRawValue(CostRateCardContract.SerializeCanonical(runtimeRateCard.RateCard));
            writer.WritePropertyName("provenance");
            writer.WriteStartObject();
            switch (runtimeRateCard.Provenance)
            {
                case ManualRateCardProvenance manual:
                    writer.WriteString("source", "manual");
                    writer.WriteString("sourceUrl", manual.SourceUrl);
                    writer.WriteString("retrievedOn", manual.RetrievedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
                case AzureRetailRateCardProvenance retail:
                    writer.WriteString("source", "azure-retail-prices");
                    writer.WriteString("region", retail.Region);
                    writer.WritePropertyName("meterIds");
                    writer.WriteStartArray();
                    // Sort meter IDs so input order does not change the canonical bytes.
                    foreach (var meterId in retail.MeterIds.OrderBy(value => value, StringComparer.Ordinal))
                        writer.WriteStringValue(meterId);
                    writer.WriteEndArray();
                    break;
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static RuntimeRateCard? DeserializeRuntimePayload(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("runtimeRateCardSchemaVersion", out var schemaVersion))
                return null;
            if (schemaVersion.GetInt32() != CurrentSchemaVersion)
                throw new JsonException("The runtime rate-card schema version is not supported.");

            var card = JsonSerializer.Deserialize<CostRateCard>(
                root.GetProperty("rateCard").GetRawText(), JsonOptions)
                ?? throw new JsonException("The runtime rate card is empty.");
            var provenanceElement = root.GetProperty("provenance");
            RuntimeRateCardProvenance provenance = provenanceElement.GetProperty("source").GetString() switch
            {
                "manual" => new ManualRateCardProvenance(
                    provenanceElement.GetProperty("sourceUrl").GetString() ?? string.Empty,
                    DateOnly.ParseExact(
                        provenanceElement.GetProperty("retrievedOn").GetString() ?? string.Empty,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)),
                "azure-retail-prices" => new AzureRetailRateCardProvenance(
                    provenanceElement.GetProperty("region").GetString() ?? string.Empty,
                    [.. provenanceElement.GetProperty("meterIds").EnumerateArray()
                        .Select(value => value.GetString() ?? string.Empty)]),
                _ => throw new JsonException("The runtime rate-card provenance is not supported.")
            };
            var runtimeRateCard = new RuntimeRateCard(card, provenance);
            Validate(runtimeRateCard);
            return runtimeRateCard;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException or
                InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("The stored runtime rate card is invalid.", exception);
        }
    }

    private static bool IsRegion(string? value) =>
        value is { Length: > 0 and <= 63 } &&
        value[0] is >= 'a' and <= 'z' &&
        value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');
}
