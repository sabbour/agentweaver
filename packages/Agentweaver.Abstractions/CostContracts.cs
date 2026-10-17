using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>States whether a price is provisional, reconciled to usage, or unavailable.</summary>
public enum CostDisposition { Estimate, Reconciled, Unpriced }

/// <summary>Defines the provider facts used to calculate a work quote.</summary>
public enum CostQuoteBasis { ProviderWeightedNanoAiu, UnweightedAiCredits, ProviderPublishedTokenRates }

/// <summary>Versioned pricing inputs owned by a cost provider and pinned into a run binding.</summary>
public sealed record CostRateCard(
    string Id,
    string Version,
    string MeterSource,
    string Unit,
    decimal NanoUnitsPerUnit,
    ImmutableDictionary<string, decimal> ModelMultipliers)
{
    /// <summary>Provider-published model token rates, keyed by the exact model ID.</summary>
    public ImmutableDictionary<string, CostModelTokenRates> ModelTokenRates { get; init; } =
        ImmutableDictionary<string, CostModelTokenRates>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>Official source URL for published token rates.</summary>
    public string? ProviderSourceUrl { get; init; }

    /// <summary>Version of the provider pricing snapshot.</summary>
    public string? SnapshotVersion { get; init; }

    /// <summary>Date the provider pricing source was retrieved.</summary>
    public DateOnly? RetrievedOn { get; init; }

    /// <summary>Currency used by the provider's per-token rates.</summary>
    public string? TokenRateCurrency { get; init; }

    /// <summary>Number of tokens represented by each token-rate value.</summary>
    public decimal? TokensPerRateUnit { get; init; }

    /// <summary>Credits per unit of the published token-rate currency.</summary>
    public decimal? CreditsPerCurrencyUnit { get; init; }

    /// <summary>Input-token count above which the long-context rates apply.</summary>
    public long? LongContextThresholdTokens { get; init; }
}

/// <summary>Token prices for one context tier, expressed per card-defined rate unit.</summary>
public sealed record CostTokenRates(
    decimal? Input,
    decimal? CachedInput,
    decimal? CacheWrite,
    decimal? Output);

/// <summary>Published token prices for the default and long-context tiers.</summary>
public sealed record CostModelTokenRates(
    CostTokenRates? DefaultContext,
    CostTokenRates? LongContext);

/// <summary>Validates and canonically serializes provider-owned cost rate cards.</summary>
public static class CostRateCardContract
{
    /// <summary>Rejects incomplete provenance, malformed rates, and invalid model identifiers.</summary>
    public static void Validate(CostRateCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        RequireIdentifier(card.Id, nameof(card.Id), 128);
        RequireIdentifier(card.Version, nameof(card.Version), 128);
        RequireIdentifier(card.MeterSource, nameof(card.MeterSource), 256);
        RequireIdentifier(card.Unit, nameof(card.Unit), 128);
        if (card.NanoUnitsPerUnit <= 0)
            throw new ArgumentException("The rate-card unit scale must be positive.", nameof(card));
        ArgumentNullException.ThrowIfNull(card.ModelMultipliers);
        foreach (var rate in card.ModelMultipliers)
        {
            RequireIdentifier(rate.Key, nameof(card.ModelMultipliers), 512);
            RequireRate(rate.Value, nameof(card.ModelMultipliers));
        }

        ArgumentNullException.ThrowIfNull(card.ModelTokenRates);
        if (card.ModelTokenRates.Count == 0)
        {
            if (card.ProviderSourceUrl is not null || card.SnapshotVersion is not null ||
                card.RetrievedOn is not null || card.TokenRateCurrency is not null ||
                card.TokensPerRateUnit is not null || card.CreditsPerCurrencyUnit is not null ||
                card.LongContextThresholdTokens is not null)
                throw new ArgumentException("Token-rate metadata requires at least one published model rate.", nameof(card));
            return;
        }
        if (!Uri.TryCreate(card.ProviderSourceUrl, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(card.SnapshotVersion) ||
            card.SnapshotVersion.Length > 128 ||
            card.SnapshotVersion.Any(char.IsControl) ||
            card.RetrievedOn is null ||
            card.TokenRateCurrency is not { Length: 3 } currency ||
            !currency.All(character => character is >= 'A' and <= 'Z') ||
            card.TokensPerRateUnit is not > 0 ||
            card.CreditsPerCurrencyUnit is not > 0 ||
            card.LongContextThresholdTokens is not > 0)
            throw new ArgumentException("Published token rates require complete source and unit metadata.", nameof(card));

        foreach (var model in card.ModelTokenRates)
        {
            RequireIdentifier(model.Key, nameof(card.ModelTokenRates), 512);
            if (model.Value is null ||
                model.Value.DefaultContext is null && model.Value.LongContext is null)
                throw new ArgumentException("Each published model requires at least one context tier.", nameof(card));
            ValidateTokenRates(model.Value.DefaultContext, nameof(card.ModelTokenRates));
            ValidateTokenRates(model.Value.LongContext, nameof(card.ModelTokenRates));
        }
    }

    /// <summary>Serializes all rate-card fields in stable property and model order.</summary>
    public static string SerializeCanonical(CostRateCard card)
    {
        Validate(card);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", card.Id);
            writer.WriteString("version", card.Version);
            writer.WriteString("meterSource", card.MeterSource);
            writer.WriteString("unit", card.Unit);
            WriteNumber(writer, "nanoUnitsPerUnit", card.NanoUnitsPerUnit);
            writer.WritePropertyName("modelMultipliers");
            writer.WriteStartObject();
            foreach (var (model, rate) in card.ModelMultipliers.OrderBy(item => item.Key, StringComparer.Ordinal))
                WriteNumber(writer, model, rate);
            writer.WriteEndObject();
            if (card.ModelTokenRates.Count > 0)
            {
                writer.WriteString("providerSourceUrl", card.ProviderSourceUrl);
                writer.WriteString("snapshotVersion", card.SnapshotVersion);
                writer.WriteString("retrievedOn", card.RetrievedOn!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                writer.WriteString("tokenRateCurrency", card.TokenRateCurrency);
                WriteNumber(writer, "tokensPerRateUnit", card.TokensPerRateUnit!.Value);
                WriteNumber(writer, "creditsPerCurrencyUnit", card.CreditsPerCurrencyUnit!.Value);
                writer.WriteNumber("longContextThresholdTokens", card.LongContextThresholdTokens!.Value);
                writer.WritePropertyName("modelTokenRates");
                writer.WriteStartObject();
                foreach (var (model, tiers) in card.ModelTokenRates.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(model);
                    writer.WriteStartObject();
                    WriteTokenRates(writer, "defaultContext", tiers.DefaultContext);
                    WriteTokenRates(writer, "longContext", tiers.LongContext);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Compares immutable card contents using their canonical representation.</summary>
    public static bool Equals(CostRateCard? left, CostRateCard? right)
    {
        if (left is null || right is null)
            return false;
        try
        {
            return string.Equals(SerializeCanonical(left), SerializeCanonical(right), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateTokenRates(CostTokenRates? rates, string parameter)
    {
        if (rates is null)
            return;
        RequireRate(rates.Input, parameter);
        RequireRate(rates.CachedInput, parameter);
        RequireRate(rates.CacheWrite, parameter);
        RequireRate(rates.Output, parameter);
        if (rates.Input is null && rates.CachedInput is null &&
            rates.CacheWrite is null && rates.Output is null)
            throw new ArgumentException("A published context tier must contain a token rate.", parameter);
    }

    private static void RequireIdentifier(string? value, string parameter, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("A bounded identifier without whitespace is required.", parameter);
    }

    private static void RequireRate(decimal? value, string parameter)
    {
        if (value is < 0)
            throw new ArgumentOutOfRangeException(parameter, "Rates cannot be negative.");
    }

    private static void WriteTokenRates(Utf8JsonWriter writer, string name, CostTokenRates? rates)
    {
        writer.WritePropertyName(name);
        if (rates is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        WriteNullableNumber(writer, "input", rates.Input);
        WriteNullableNumber(writer, "cachedInput", rates.CachedInput);
        WriteNullableNumber(writer, "cacheWrite", rates.CacheWrite);
        WriteNullableNumber(writer, "output", rates.Output);
        writer.WriteEndObject();
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            WriteNumber(writer, name, value.Value);
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture), skipInputValidation: true);
    }
}

/// <summary>
/// Pins the selected meter provider, its resource generation and capabilities, and the
/// rate card used to price a run.
/// </summary>
public sealed record CostBinding(
    string MeterSource,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string ResourceId,
    long ResourceGeneration,
    ImmutableHashSet<string> NegotiatedCapabilities,
    CostRateCard RateCard);

/// <summary>Price returned by a cost provider; unpriced results carry a reason instead of an amount.</summary>
public sealed record CostPrice(
    decimal? Amount, string? Unit, CostDisposition Disposition,
    CostRateCard? RateCard, string? UnpricedReason);

/// <summary>Work estimate requested from a cost provider before usage has been measured.</summary>
public sealed record CostQuoteRequest(
    string ModelId, decimal? Units, CostQuoteBasis Basis)
{
    /// <summary>Token measurements used only with the provider-published token-rate basis.</summary>
    public UsageMeasurement? TokenMeasurement { get; init; }
}

/// <summary>Prices measured usage and planned work for the selected run-scoped cost binding.</summary>
public interface ICostProvider
{
    /// <summary>Reconciles a usage observation against its resolved model and pinned rate card.</summary>
    CostPrice Price(UsageMeasurement usage, UsageModelBinding model, CostBinding binding);

    /// <summary>Quotes planned units using the requested pricing basis and pinned rate card.</summary>
    CostPrice Quote(CostQuoteRequest plannedWork, CostBinding binding);
}
