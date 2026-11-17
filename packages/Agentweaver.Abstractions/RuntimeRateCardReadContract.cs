using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>Provenance recorded with an immutable runtime rate card.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "source")]
[JsonDerivedType(typeof(ManualRuntimeRateCardReadProvenance), "manual")]
[JsonDerivedType(typeof(AzureRetailRuntimeRateCardReadProvenance), "azure-retail-prices")]
public abstract record RuntimeRateCardReadProvenance;

/// <summary>Manual rate-card source and retrieval date.</summary>
public sealed record ManualRuntimeRateCardReadProvenance(string SourceUrl, DateOnly RetrievedOn)
    : RuntimeRateCardReadProvenance;

/// <summary>Azure Retail region and the meter IDs used to build a rate card.</summary>
public sealed record AzureRetailRuntimeRateCardReadProvenance(
    string Region,
    ImmutableArray<string> MeterIds) : RuntimeRateCardReadProvenance;

/// <summary>The Events-owned rate card and its recorded source provenance.</summary>
public sealed record RuntimeRateCardReadResponse(
    int RuntimeRateCardSchemaVersion,
    CostRateCard RateCard,
    RuntimeRateCardReadProvenance Provenance);

/// <summary>Validates and serializes the private Events runtime rate-card read contract.</summary>
public static class RuntimeRateCardReadContract
{
    /// <summary>The current private runtime rate-card contract version.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    /// <summary>Checks the card, provenance, and optional requested key.</summary>
    public static void Validate(
        RuntimeRateCardReadResponse response,
        string? expectedId = null,
        string? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(response.RateCard);
        ArgumentNullException.ThrowIfNull(response.Provenance);
        if (response.RuntimeRateCardSchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("The runtime rate-card schema version is not supported.");

        var card = response.RateCard;
        ValidateIdentity(card.Id, card.Version);
        CostRateCardContract.Validate(card);
        if (card.Unit != "USD" ||
            card.MeterSource != SdkMeterSources.ByokTokens ||
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

        if (expectedId is not null &&
                !string.Equals(card.Id, expectedId, StringComparison.Ordinal) ||
            expectedVersion is not null &&
                !string.Equals(card.Version, expectedVersion, StringComparison.Ordinal))
            throw new ArgumentException("The runtime rate card does not match the requested ID and version.");

        switch (response.Provenance)
        {
            case ManualRuntimeRateCardReadProvenance manual when
                Uri.TryCreate(manual.SourceUrl, UriKind.Absolute, out var source) &&
                source.Scheme == Uri.UriSchemeHttps &&
                !string.IsNullOrEmpty(source.Host) &&
                string.IsNullOrEmpty(source.UserInfo) &&
                manual.RetrievedOn != default:
                break;
            case AzureRetailRuntimeRateCardReadProvenance retail when
                IsRegion(retail.Region) &&
                card.Id == $"azure-retail-prices.standard.{retail.Region}" &&
                !retail.MeterIds.IsDefaultOrEmpty &&
                retail.MeterIds.All(IsIdentifier) &&
                retail.MeterIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == retail.MeterIds.Length:
                break;
            case ManualRuntimeRateCardReadProvenance:
                throw new ArgumentException("Manual rate cards require an HTTPS source URL and retrieval date.");
            case AzureRetailRuntimeRateCardReadProvenance:
                throw new ArgumentException("Azure Retail rate cards require a matching region and distinct meter IDs.");
            default:
                throw new ArgumentException("Runtime rate-card provenance is not supported.");
        }
    }

    /// <summary>Checks whether an opaque card ID and version fit the owner route contract.</summary>
    public static void ValidateIdentity(string id, string version)
    {
        if (!IsIdentifier(id) || id.Length > 128 ||
            !IsIdentifier(version) || version.Length > 64)
            throw new ArgumentException("A valid runtime rate-card ID and version are required.");
    }

    /// <summary>Serializes every card and provenance field in the canonical byte order.</summary>
    public static string SerializeCanonical(RuntimeRateCardReadResponse response)
    {
        Validate(response);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("runtimeRateCardSchemaVersion", CurrentSchemaVersion);
            writer.WritePropertyName("rateCard");
            writer.WriteRawValue(CostRateCardContract.SerializeCanonical(response.RateCard));
            writer.WritePropertyName("provenance");
            writer.WriteStartObject();
            switch (response.Provenance)
            {
                case ManualRuntimeRateCardReadProvenance manual:
                    writer.WriteString("source", "manual");
                    writer.WriteString("sourceUrl", manual.SourceUrl);
                    writer.WriteString(
                        "retrievedOn",
                        manual.RetrievedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    break;
                case AzureRetailRuntimeRateCardReadProvenance retail:
                    writer.WriteString("source", "azure-retail-prices");
                    writer.WriteString("region", retail.Region);
                    writer.WritePropertyName("meterIds");
                    writer.WriteStartArray();
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

    /// <summary>Reads the canonical owner response. A missing schema field means legacy content.</summary>
    public static RuntimeRateCardReadResponse? Deserialize(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("runtimeRateCardSchemaVersion", out var schemaVersion))
                return null;
            if (!HasExactProperties(
                    root, "runtimeRateCardSchemaVersion", "rateCard", "provenance"))
                throw new JsonException("The runtime rate-card response has unexpected properties.");

            var card = JsonSerializer.Deserialize<CostRateCard>(
                root.GetProperty("rateCard").GetRawText(), JsonOptions)
                ?? throw new JsonException("The runtime rate card is empty.");
            var provenanceElement = root.GetProperty("provenance");
            var provenance = ReadProvenance(provenanceElement);
            var response = new RuntimeRateCardReadResponse(
                schemaVersion.GetInt32(),
                card,
                provenance);
            Validate(response);
            return response;
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException or
                InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("The runtime rate-card read response is invalid.", exception);
        }
    }

    private static RuntimeRateCardReadProvenance ReadProvenance(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String)
            throw new JsonException("Runtime rate-card provenance is missing its source.");

        return sourceElement.GetString() switch
        {
            "manual" when HasExactProperties(element, "source", "sourceUrl", "retrievedOn") =>
                new ManualRuntimeRateCardReadProvenance(
                    element.GetProperty("sourceUrl").GetString() ?? string.Empty,
                    DateOnly.ParseExact(
                        element.GetProperty("retrievedOn").GetString() ?? string.Empty,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture)),
            "azure-retail-prices" when HasExactProperties(element, "source", "region", "meterIds") =>
                new AzureRetailRuntimeRateCardReadProvenance(
                    element.GetProperty("region").GetString() ?? string.Empty,
                    [.. element.GetProperty("meterIds").EnumerateArray()
                        .Select(value => value.GetString() ?? string.Empty)]),
            _ => throw new JsonException("Runtime rate-card provenance is not supported."),
        };
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == expected.Length &&
            properties.Distinct(StringComparer.Ordinal).Count() == expected.Length &&
            properties.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
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
