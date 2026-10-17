using System.Globalization;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions;

/// <summary>Validates usage records and determines whether their measurements support complete accounting.</summary>
internal static class UsageLedgerValidation
{
    /// <summary>Checks usage attribution, measurement ranges, rate-card identity, and priced/unpriced shape.</summary>
    public static void Validate(
        UsageSubmission submission,
        CostBinding? binding,
        CostPrice price)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(submission.Attribution);
        ArgumentNullException.ThrowIfNull(submission.ModelBinding);
        ArgumentNullException.ThrowIfNull(submission.Measurement);
        ArgumentNullException.ThrowIfNull(price);

        if (submission.EventId == Guid.Empty)
            throw new ArgumentException("A non-empty usage event ID is required.", nameof(submission));

        _ = new SessionIdentity(
            submission.Attribution.ProjectId,
            submission.Attribution.RunId,
            submission.Attribution.SessionId);
        ValidateIdentifier(submission.Attribution.TenantId, nameof(submission), 256);
        ValidateIdentifier(submission.Attribution.AgentId, nameof(submission), 256);
        if (submission.Attribution.TurnId is not null)
            ValidateIdentifier(submission.Attribution.TurnId, nameof(submission), 256);
        ValidateIdentifier(submission.ModelBinding.ModelReference, nameof(submission));
        ValidateIdentifier(submission.ModelBinding.ModelId, nameof(submission));
        ValidateIdentifier(submission.ModelBinding.MeterSource, nameof(submission), 256);
        ValidateIdentifier(submission.ModelBinding.SelectionRevision, nameof(submission), 256);

        var measurement = submission.Measurement;
        RequireNonNegative(measurement.RequestCount, nameof(measurement.RequestCount));
        RequireNonNegative(measurement.InputTokens, nameof(measurement.InputTokens));
        RequireNonNegative(measurement.OutputTokens, nameof(measurement.OutputTokens));
        RequireNonNegative(measurement.CachedTokens, nameof(measurement.CachedTokens));
        RequireNonNegative(measurement.CacheWriteTokens, nameof(measurement.CacheWriteTokens));
        RequireNonNegative(measurement.ReasoningTokens, nameof(measurement.ReasoningTokens));
        RequireNonNegative(measurement.ProviderUnits, nameof(measurement.ProviderUnits));
        RequireNonNegative(measurement.DurationMilliseconds, nameof(measurement.DurationMilliseconds));
        if (measurement.ProviderUnit is not null)
            ValidateIdentifier(measurement.ProviderUnit, nameof(measurement.ProviderUnit), 128);

        if (!Enum.IsDefined(price.Disposition))
            throw new ArgumentException("The cost disposition is unsupported.", nameof(price));
        RequireNonNegative(price.Amount, nameof(price.Amount));
        if (price.Unit is not null)
            ValidateIdentifier(price.Unit, nameof(price.Unit), 128);

        if (binding is null)
        {
            if (price.Disposition != CostDisposition.Unpriced || price.RateCard is not null)
                throw new ArgumentException("Priced usage requires a pinned cost binding.", nameof(binding));
        }
        else
        {
            ValidateBinding(binding);
            if (!string.Equals(
                binding.MeterSource, submission.ModelBinding.MeterSource, StringComparison.Ordinal))
                throw new ArgumentException("The cost binding meter source does not match the usage model.", nameof(binding));
            if (!string.Equals(
                binding.RateCard.MeterSource, binding.MeterSource, StringComparison.Ordinal))
                throw new ArgumentException("The rate card meter source does not match the cost binding.", nameof(binding));
            if (price.Unit is not null &&
                !string.Equals(price.Unit, binding.RateCard.Unit, StringComparison.Ordinal))
                throw new ArgumentException("The price unit does not match the pinned rate card.", nameof(price));
            if (price.RateCard is not null &&
                !UsageLedgerCanonicalizer.RateCardsEqual(price.RateCard, binding.RateCard))
                throw new ArgumentException("The price rate card does not match the pinned cost binding.", nameof(price));
        }

        if (price.Disposition == CostDisposition.Unpriced)
        {
            if (price.Amount is not null || string.IsNullOrWhiteSpace(price.UnpricedReason) ||
                price.UnpricedReason.Length > 1024 || price.UnpricedReason.Any(char.IsControl))
                throw new ArgumentException("Unpriced usage requires a reason and cannot include an amount.", nameof(price));
            return;
        }

        if (binding is null || price.Amount is null || price.Unit is null ||
            price.RateCard is null || price.UnpricedReason is not null ||
            !string.Equals(price.Unit, binding.RateCard.Unit, StringComparison.Ordinal))
            throw new ArgumentException("A priced usage record must match its pinned rate card.", nameof(price));
    }

    /// <summary>Validates tenant and project/run scope before ledger reads or writes.</summary>
    public static void ValidateRunScope(string tenantId, string projectId, string runId)
    {
        ValidateIdentifier(tenantId, nameof(tenantId), 256);
        _ = new SessionIdentity(projectId, runId, "_");
    }

    /// <summary>Reports complete pricing only when cost is priced and its source accounting is complete.</summary>
    internal static bool IsFinanciallyComplete(
        UsageSubmission submission, CostDisposition disposition) =>
        disposition != CostDisposition.Unpriced && HasCompleteSdkAccounting(submission);

    /// <summary>Checks native source measurements without treating missing values as zero.</summary>
    internal static bool HasCompleteSdkAccounting(UsageSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.SdkSource is null)
            return submission.SdkAccounting is null;
        if (submission.ModelBinding.MeterSource != submission.SdkSource.MeterSource)
            return false;
        var measurement = submission.Measurement;
        var hasReportedCopilotNanoAiu = measurement.ProviderUnits is not null &&
            measurement.ProviderUnit == "nano_aiu";
        var hasCompleteCopilotTokenMeasurements = measurement.ProviderUnits is null &&
            (measurement.ProviderUnit is null or "tokens" or "nano_aiu") &&
            HasCompleteCopilotTokenMeasurements(measurement);
        var identity = submission.SdkAccounting?.Identity;
        return submission.SdkSource.SourceMode switch
        {
            "byok" =>
                submission.SdkSource.MeterSource == SdkMeterSources.ByokTokens &&
                submission.Measurement.ProviderUnits is null &&
                submission.Measurement.ProviderUnit == "tokens" &&
                submission.Measurement.InputTokens is not null &&
                submission.Measurement.OutputTokens is not null,
            "hosted-copilot" =>
                submission.SdkSource.MeterSource == SdkMeterSources.CopilotNanoAiu &&
                (hasReportedCopilotNanoAiu || hasCompleteCopilotTokenMeasurements) &&
                (identity is null ||
                 identity.Sequence > 0 &&
                 identity.SourceSessionId == submission.SdkSource.SdkSessionId &&
                 IsAccountingIdentityText(identity.SourceSessionId) &&
                 IsAccountingIdentityText(identity.UsageId)),
            _ => false
        };
    }

    /// <summary>Requires every token category used by the published Copilot rates.</summary>
    private static bool HasCompleteCopilotTokenMeasurements(UsageMeasurement measurement) =>
        measurement.InputTokens is not null && measurement.OutputTokens is not null &&
        measurement.CachedTokens is not null && measurement.CacheWriteTokens is not null;

    /// <summary>Accepts bounded, non-whitespace SDK accounting identity values.</summary>
    private static bool IsAccountingIdentityText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));

    /// <summary>Validates every persisted cost binding and rate-card identity before canonicalization.</summary>
    private static void ValidateBinding(CostBinding binding)
    {
        ValidateIdentifier(binding.MeterSource, nameof(binding.MeterSource), 256);
        ValidateIdentifier(binding.ProviderId, nameof(binding.ProviderId), 256);
        ValidateIdentifier(binding.AdapterVersion, nameof(binding.AdapterVersion), 64);
        ValidateIdentifier(binding.OptionsRevision, nameof(binding.OptionsRevision), 128);
        ValidateIdentifier(binding.ResourceId, nameof(binding.ResourceId), 256);
        if (binding.OptionsSchemaVersion <= 0 || binding.ResourceGeneration <= 0)
            throw new ArgumentException("Cost binding schema and resource generations must be positive.", nameof(binding));

        ArgumentNullException.ThrowIfNull(binding.RateCard);
        CostRateCardContract.Validate(binding.RateCard);

        ArgumentNullException.ThrowIfNull(binding.NegotiatedCapabilities);
        foreach (var capability in binding.NegotiatedCapabilities)
            ValidateIdentifier(capability, nameof(binding.NegotiatedCapabilities));
    }

    /// <summary>Requires a bounded identifier without whitespace or control characters.</summary>
    private static void ValidateIdentifier(string value, string parameter, int maximumLength = 512)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("A non-empty bounded identifier without whitespace is required.", parameter);
    }

    /// <summary>Rejects a negative required usage number.</summary>
    private static void RequireNonNegative(long value, string parameter)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameter, "Usage values cannot be negative.");
    }

    /// <summary>Rejects negative values while preserving null as unknown.</summary>
    private static void RequireNonNegative(long? value, string parameter)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameter, "Usage values cannot be negative.");
    }

    /// <summary>Rejects a negative price while preserving null as unknown.</summary>
    private static void RequireNonNegative(decimal? value, string parameter)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameter, "Usage values cannot be negative.");
    }
}

/// <summary>Serializes ledger inputs in stable property and collection order before hashing or duplicate comparison.</summary>
internal static class UsageLedgerCanonicalizer
{
    /// <summary>Serializes attribution, measurements, source metadata, binding, and price for immutable event identity.</summary>
    public static string Serialize(
        UsageSubmission submission,
        CostBinding? binding,
        CostPrice price)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("eventId", submission.EventId.ToString("D"));
            writer.WriteString("occurredAt", submission.OccurredAt.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));

            writer.WritePropertyName("attribution");
            writer.WriteStartObject();
            writer.WriteString("tenantId", submission.Attribution.TenantId);
            writer.WriteString("projectId", submission.Attribution.ProjectId);
            writer.WriteString("runId", submission.Attribution.RunId);
            writer.WriteString("sessionId", submission.Attribution.SessionId);
            writer.WriteString("agentId", submission.Attribution.AgentId);
            if (submission.Attribution.TurnId is not null)
                writer.WriteString("turnId", submission.Attribution.TurnId);
            writer.WriteEndObject();

            writer.WritePropertyName("modelBinding");
            writer.WriteStartObject();
            writer.WriteString("modelReference", submission.ModelBinding.ModelReference);
            writer.WriteString("modelId", submission.ModelBinding.ModelId);
            writer.WriteString("meterSource", submission.ModelBinding.MeterSource);
            writer.WriteString("selectionRevision", submission.ModelBinding.SelectionRevision);
            writer.WriteEndObject();

            writer.WritePropertyName("measurement");
            writer.WriteStartObject();
            WriteNullableNumber(writer, "inputTokens", submission.Measurement.InputTokens);
            WriteNullableNumber(writer, "outputTokens", submission.Measurement.OutputTokens);
            WriteNullableNumber(writer, "cachedTokens", submission.Measurement.CachedTokens);
            if (submission.Measurement.CacheWriteTokens is not null)
                WriteNullableNumber(writer, "cacheWriteTokens", submission.Measurement.CacheWriteTokens);
            WriteNullableNumber(writer, "reasoningTokens", submission.Measurement.ReasoningTokens);
            WriteNullableNumber(writer, "requestCount", submission.Measurement.RequestCount);
            WriteNullableNumber(writer, "providerUnits", submission.Measurement.ProviderUnits);
            WriteNullableString(writer, "providerUnit", submission.Measurement.ProviderUnit);
            WriteNullableNumber(writer, "durationMilliseconds", submission.Measurement.DurationMilliseconds);
            writer.WriteEndObject();

            if (submission.SdkSource is not null)
            {
                writer.WritePropertyName("sdkSource");
                JsonSerializer.Serialize(writer, submission.SdkSource);
            }
            if (submission.SdkEventId is not null)
                writer.WriteString("sdkEventId", submission.SdkEventId);
            if (submission.A2AMessageId is not null)
                writer.WriteString("a2AMessageId", submission.A2AMessageId.Value);
            if (submission.SdkAccounting is not null)
            {
                writer.WritePropertyName("sdkAccounting");
                JsonSerializer.Serialize(writer, submission.SdkAccounting);
            }

            writer.WritePropertyName("costBinding");
            if (binding is null)
                writer.WriteNullValue();
            else
                WriteBinding(writer, binding);

            writer.WritePropertyName("price");
            writer.WriteStartObject();
            WriteNullableNumber(writer, "amount", price.Amount);
            WriteNullableString(writer, "unit", price.Unit);
            writer.WriteString("disposition", price.Disposition.ToString());
            writer.WritePropertyName("rateCard");
            if (price.RateCard is null)
                writer.WriteNullValue();
            else
                WriteRateCard(writer, price.RateCard);
            WriteNullableString(writer, "unpricedReason", price.UnpricedReason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Serializes a rate card in canonical key order for stable identity checks.</summary>
    public static string SerializeRateCard(CostRateCard card)
        => CostRateCardContract.SerializeCanonical(card);

    /// <summary>Compares rate-card contents rather than object identity.</summary>
    public static bool RateCardsEqual(CostRateCard left, CostRateCard right) =>
        CostRateCardContract.Equals(left, right);

    /// <summary>Writes the pinned provider and capabilities in deterministic order.</summary>
    private static void WriteBinding(Utf8JsonWriter writer, CostBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("meterSource", binding.MeterSource);
        writer.WriteString("providerId", binding.ProviderId);
        writer.WriteString("adapterVersion", binding.AdapterVersion);
        writer.WriteNumber("optionsSchemaVersion", binding.OptionsSchemaVersion);
        writer.WriteString("optionsRevision", binding.OptionsRevision);
        writer.WriteString("resourceId", binding.ResourceId);
        writer.WriteNumber("resourceGeneration", binding.ResourceGeneration);
        writer.WritePropertyName("negotiatedCapabilities");
        writer.WriteStartArray();
        foreach (var capability in binding.NegotiatedCapabilities.Order(StringComparer.Ordinal))
            writer.WriteStringValue(capability);
        writer.WriteEndArray();
        writer.WritePropertyName("rateCard");
        WriteRateCard(writer, binding.RateCard);
        writer.WriteEndObject();
    }

    /// <summary>Writes rate-card values and model rates in deterministic key order.</summary>
    private static void WriteRateCard(Utf8JsonWriter writer, CostRateCard card)
        => writer.WriteRawValue(CostRateCardContract.SerializeCanonical(card), skipInputValidation: true);

    /// <summary>Writes a nullable integer as a number or explicit JSON null.</summary>
    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value.HasValue)
            writer.WriteNumber(name, value.Value);
        else
            writer.WriteNull(name);
    }

    /// <summary>Writes a nullable decimal without conversion through binary floating point.</summary>
    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, decimal? value)
    {
        writer.WritePropertyName(name);
        if (value.HasValue)
            WriteNumberValue(writer, value.Value);
        else
            writer.WriteNullValue();
    }

    /// <summary>Writes a nullable string as its value or explicit JSON null.</summary>
    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    /// <summary>Writes a decimal using invariant exact decimal text.</summary>
    private static void WriteNumber(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        WriteNumberValue(writer, value);
    }

    /// <summary>Emits exact decimal digits without locale-dependent formatting.</summary>
    private static void WriteNumberValue(Utf8JsonWriter writer, decimal value) =>
        writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture), skipInputValidation: true);
}
