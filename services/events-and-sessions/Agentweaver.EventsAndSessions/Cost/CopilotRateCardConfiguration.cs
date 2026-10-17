using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Loads the strict operator-supplied JSON schema for a Copilot rate card.</summary>
internal static class CopilotRateCardConfiguration
{
    private const int MaximumDocumentBytes = 262_144;
    private const int MaximumModels = 256;
    private static readonly string[] RootProperties =
    [
        "schemaVersion", "id", "version", "meterSource", "unit", "nanoUnitsPerUnit",
        "providerSourceUrl", "snapshotVersion", "retrievedOn", "tokenRateCurrency",
        "tokensPerRateUnit", "creditsPerCurrencyUnit", "modelTokenRates"
    ];
    private static readonly string[] ModelProperties =
        ["defaultContext", "longContext", "longContextThresholdTokens"];
    private static readonly string[] TierProperties = ["input", "cachedInput", "cacheWrite", "output"];

    /// <summary>Parses a schema-versioned rate card and hashes the exact supplied document text.</summary>
    public static CostRateCard Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw InvalidCard();

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            RequireExactProperties(root, RootProperties);
            if (ReadInt32(root, "schemaVersion") != 1)
                throw InvalidCard();

            var modelsElement = root.GetProperty("modelTokenRates");
            if (modelsElement.ValueKind != JsonValueKind.Object)
                throw InvalidCard();
            var models = ImmutableDictionary.CreateBuilder<string, CostModelTokenRates>(StringComparer.Ordinal);
            foreach (var modelProperty in modelsElement.EnumerateObject())
            {
                if (models.Count >= MaximumModels || string.IsNullOrWhiteSpace(modelProperty.Name) ||
                    !models.TryAdd(modelProperty.Name, ReadModelRates(modelProperty.Value)))
                    throw InvalidCard();
            }

            if (models.Count == 0)
                throw InvalidCard();

            var sourceUrl = ReadString(root, "providerSourceUrl");
            if (sourceUrl != CopilotPublishedRateCard.SourceUrl)
                throw InvalidCard();
            if (!DateOnly.TryParseExact(ReadString(root, "retrievedOn"), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var retrievedOn))
                throw InvalidCard();

            var card = new CostRateCard(
                ReadString(root, "id"),
                ReadString(root, "version"),
                ReadString(root, "meterSource"),
                ReadString(root, "unit"),
                ReadDecimal(root, "nanoUnitsPerUnit"),
                ImmutableDictionary<string, decimal>.Empty.WithComparers(StringComparer.Ordinal))
            {
                ProviderSourceUrl = sourceUrl,
                SnapshotVersion = ReadString(root, "snapshotVersion"),
                RetrievedOn = retrievedOn,
                TokenRateCurrency = ReadString(root, "tokenRateCurrency"),
                TokensPerRateUnit = ReadDecimal(root, "tokensPerRateUnit"),
                CreditsPerCurrencyUnit = ReadDecimal(root, "creditsPerCurrencyUnit"),
                ModelTokenRates = models.ToImmutable(),
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            };
            CostRateCardContract.Validate(card);
            if (card.ModelMultipliers.Count != 0 || card.TokenRateCurrency != "USD" ||
                card.CreditsPerCurrencyUnit != CopilotPublishedRateCard.AiCreditsPerUsd)
                throw InvalidCard();
            return card;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                           InvalidOperationException or ArgumentException or OverflowException)
        {
            throw InvalidCard(exception);
        }
    }

    private static CostModelTokenRates ReadModelRates(JsonElement model)
    {
        RequireExactProperties(model, ModelProperties);
        var defaultRates = ReadTokenRates(model.GetProperty("defaultContext"));
        var longRates = ReadTokenRates(model.GetProperty("longContext"));
        if (defaultRates is null)
            throw InvalidCard();
        var thresholdElement = model.GetProperty("longContextThresholdTokens");
        long? threshold = thresholdElement.ValueKind == JsonValueKind.Null
            ? null
            : ReadInt64(thresholdElement);
        return new CostModelTokenRates(defaultRates, longRates)
        {
            LongContextThresholdTokens = threshold
        };
    }

    private static CostTokenRates? ReadTokenRates(JsonElement tier)
    {
        if (tier.ValueKind == JsonValueKind.Null)
            return null;
        RequireExactProperties(tier, TierProperties);
        return new(
            ReadNullableDecimal(tier, "input"),
            ReadNullableDecimal(tier, "cachedInput"),
            ReadNullableDecimal(tier, "cacheWrite"),
            ReadNullableDecimal(tier, "output"));
    }

    private static void RequireExactProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidCard();
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!actual.Add(property.Name))
                throw InvalidCard();
        if (actual.Count != expected.Length || expected.Any(name => !actual.Contains(name)))
            throw InvalidCard();
    }

    private static string ReadString(JsonElement owner, string name)
    {
        var value = owner.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidCard();
        return value.GetString() ?? throw InvalidCard();
    }

    private static int ReadInt32(JsonElement owner, string name)
    {
        var value = owner.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw InvalidCard();
        return number;
    }

    private static long ReadInt64(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw InvalidCard();
        return number;
    }

    private static decimal ReadDecimal(JsonElement owner, string name)
    {
        var value = owner.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw InvalidCard();
        return number;
    }

    private static decimal? ReadNullableDecimal(JsonElement owner, string name)
    {
        var value = owner.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw InvalidCard();
        return number;
    }

    private static ArgumentException InvalidCard(Exception? inner = null) =>
        new("The operator-supplied Copilot rate card is malformed.", "json", inner);
}
