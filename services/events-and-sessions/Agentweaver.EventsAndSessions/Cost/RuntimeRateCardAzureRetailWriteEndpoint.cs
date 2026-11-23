using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Agentweaver.EventsAndSessions.Cost;

/// <summary>Maps the platform-admin Azure Retail runtime rate-card write route.</summary>
public static partial class RuntimeRateCardAzureRetailWriteEndpoint
{
    public const string Route = "/api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}";
    public const int MaximumRequestBodyBytes = 64 * 1024;
    public const int MaximumResponseBytes = 256 * 1024;
    internal static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex KeyPattern = LiteralKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralKeyRegex();

    /// <summary>Registers the authenticated Azure Retail runtime rate-card write route.</summary>
    public static IEndpointRouteBuilder MapRuntimeRateCardAzureRetailWriteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut(Route, WriteAsync).RequireAuthorization();
        return app;
    }

    internal static async Task<IResult> WriteAsync(
        string id,
        string version,
        HttpContext context,
        ProjectsAuthorizationContextClient projects,
        AzureRetailPriceLookup lookup,
        RuntimeRateCardStore store,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        timeProvider ??= TimeProvider.System;
        context.Response.Headers.CacheControl = "no-store";

        if (!IsValidRouteRequest(context, id, version))
            return Problem(StatusCodes.Status400BadRequest, "rate_card_request_invalid");

        string audience;
        try
        {
            audience = configuration["Identity:Audience"]
                ?? throw new InvalidOperationException("Missing Events audience.");
            using var authTimeout = CreatePhaseTimeout(timeProvider, PhaseTimeout, cancellationToken);
            await projects.AuthorizePlatformModelPricingRetailRateCardWriteAsync(
                context, audience, timeProvider, authTimeout.Token).ConfigureAwait(false);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Problem((int)exception.StatusCode, exception.Code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status502BadGateway, "projects_authorization_unavailable");
        }

        if (!IsSupportedJsonContentType(context.Request.ContentType))
            return Problem(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");

        if (context.Request.ContentLength is > MaximumRequestBodyBytes)
            return Problem(StatusCodes.Status413PayloadTooLarge, "request_body_too_large");

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeLimit)
            sizeLimit.MaxRequestBodySize = MaximumRequestBodyBytes;

        byte[] payload;
        try
        {
            using var bodyTimeout = CreatePhaseTimeout(timeProvider, PhaseTimeout, cancellationToken);
            payload = await ReadBoundedAsync(context.Request.Body, bodyTimeout.Token).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "request_body_too_large");
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "request_body_too_large");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status408RequestTimeout, "request_body_timeout");
        }

        if (!TryParseRequest(id, payload, out var parsed))
            return Problem(StatusCodes.Status400BadRequest, "rate_card_invalid");

        var digest = ComputeRequestDigest(id, version, parsed);
        var meterIds = parsed.CachedInput is { } cachedSlot
            ? new[] { parsed.Input.MeterId, parsed.Output.MeterId, cachedSlot.MeterId }
            : new[] { parsed.Input.MeterId, parsed.Output.MeterId };

        var replayResult = await TryReadExistingWinnerAsync(
            store, id, version, digest, timeProvider, cancellationToken).ConfigureAwait(false);
        if (replayResult.Outcome == StoreLookupOutcome.Conflict)
            return Problem(StatusCodes.Status409Conflict, "rate_card_conflict");
        if (replayResult.Outcome == StoreLookupOutcome.Unavailable)
            return Problem(StatusCodes.Status503ServiceUnavailable, "rate_card_storage_unavailable");
        if (replayResult.Outcome == StoreLookupOutcome.Found)
            return WriteCanonicalCard(context, replayResult.RateCard!, StatusCodes.Status200OK);

        IReadOnlyDictionary<string, decimal> resolvedPrices;
        try
        {
            resolvedPrices = await lookup.ResolveMetersAsync(parsed.Region, meterIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AzureRetailPriceLookupException)
        {
            return await ResolvePostLookupFailureAsync(
                context, store, id, version, digest,
                StatusCodes.Status503ServiceUnavailable, "pricing_unavailable",
                timeProvider, cancellationToken).ConfigureAwait(false);
        }

        if (resolvedPrices[parsed.Input.MeterId] != parsed.Input.ExpectedUsdPer1M ||
            resolvedPrices[parsed.Output.MeterId] != parsed.Output.ExpectedUsdPer1M ||
            (parsed.CachedInput is { } expectedCached &&
             resolvedPrices[expectedCached.MeterId] != expectedCached.ExpectedUsdPer1M))
        {
            return await ResolvePostLookupFailureAsync(
                context, store, id, version, digest,
                StatusCodes.Status409Conflict, "pricing_changed",
                timeProvider, cancellationToken).ConfigureAwait(false);
        }

        RuntimeRateCard runtimeRateCard;
        try
        {
            var multipliers = ImmutableDictionary.CreateBuilder<string, decimal>(StringComparer.Ordinal);
            multipliers.Add(
                AzureCostProviderOptions.InputRateKey(parsed.ModelId),
                resolvedPrices[parsed.Input.MeterId]);
            multipliers.Add(
                AzureCostProviderOptions.OutputRateKey(parsed.ModelId),
                resolvedPrices[parsed.Output.MeterId]);
            if (parsed.CachedInput is { } verifiedCached)
                multipliers.Add(
                    AzureCostProviderOptions.CachedInputRateKey(parsed.ModelId),
                    resolvedPrices[verifiedCached.MeterId]);

            var card = new CostRateCard(
                id,
                version,
                SdkMeterSources.ByokTokens,
                "USD",
                AzureCostProviderOptions.TokensPerRateUnit,
                multipliers.ToImmutable());
            runtimeRateCard = new RuntimeRateCard(
                card,
                new AzureRetailRateCardProvenance(parsed.Region, [.. meterIds]));
            RuntimeRateCardContract.Validate(runtimeRateCard);
            var canonical = RuntimeRateCardContract.SerializeCanonical(runtimeRateCard);
            if (Encoding.UTF8.GetByteCount(canonical) > MaximumResponseBytes)
                return Problem(StatusCodes.Status400BadRequest, "rate_card_too_large");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, "rate_card_invalid");
        }

        try
        {
            using var recheckTimeout = CreatePhaseTimeout(timeProvider, PhaseTimeout, cancellationToken);
            await projects.AuthorizePlatformModelPricingRetailRateCardWriteAsync(
                context, audience, timeProvider, recheckTimeout.Token).ConfigureAwait(false);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Problem((int)exception.StatusCode, exception.Code);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status502BadGateway, "projects_authorization_unavailable");
        }

        bool inserted;
        RuntimeRateCard savedCard;
        try
        {
            using var saveTimeout = CreatePhaseTimeout(timeProvider, PhaseTimeout, cancellationToken);
            (inserted, savedCard) = await store.SaveRetailAsync(runtimeRateCard, digest, saveTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (RuntimeRateCardConflictException)
        {
            return Problem(StatusCodes.Status409Conflict, "rate_card_conflict");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "rate_card_storage_unavailable");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "rate_card_storage_unavailable");
        }

        return WriteCanonicalCard(
            context,
            savedCard,
            inserted ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> ResolvePostLookupFailureAsync(
        HttpContext context,
        RuntimeRateCardStore store,
        string id,
        string version,
        string digest,
        int fallbackStatusCode,
        string fallbackCode,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var winner = await TryReadExistingWinnerAsync(
            store, id, version, digest, timeProvider, cancellationToken).ConfigureAwait(false);
        return winner.Outcome switch
        {
            StoreLookupOutcome.Found => WriteCanonicalCard(context, winner.RateCard!, StatusCodes.Status200OK),
            StoreLookupOutcome.Conflict => Problem(StatusCodes.Status409Conflict, "rate_card_conflict"),
            StoreLookupOutcome.Unavailable => Problem(
                StatusCodes.Status503ServiceUnavailable, "rate_card_storage_unavailable"),
            _ => Problem(fallbackStatusCode, fallbackCode),
        };
    }

    private static async Task<StoreLookupResult> TryReadExistingWinnerAsync(
        RuntimeRateCardStore store,
        string id,
        string version,
        string digest,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            using var storeTimeout = CreatePhaseTimeout(timeProvider, PhaseTimeout, cancellationToken);
            var existing = await store.TryGetRetailByDigestAsync(id, version, digest, storeTimeout.Token)
                .ConfigureAwait(false);
            return existing is null
                ? new StoreLookupResult(StoreLookupOutcome.NotFound, null)
                : new StoreLookupResult(StoreLookupOutcome.Found, existing);
        }
        catch (RuntimeRateCardConflictException)
        {
            return new StoreLookupResult(StoreLookupOutcome.Conflict, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StoreLookupResult(StoreLookupOutcome.Unavailable, null);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return new StoreLookupResult(StoreLookupOutcome.Unavailable, null);
        }
    }

    private static IResult WriteCanonicalCard(HttpContext context, RuntimeRateCard rateCard, int statusCode)
    {
        var canonical = RuntimeRateCardContract.SerializeCanonical(rateCard);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        if (canonicalBytes.Length > MaximumResponseBytes)
            return Problem(StatusCodes.Status400BadRequest, "rate_card_too_large");
        var hash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
        context.Response.Headers.ETag = new EntityTagHeaderValue($"\"{hash}\"").ToString();
        return Results.Text(canonical, "application/json", Encoding.UTF8, statusCode);
    }

    internal static string ComputeRequestDigest(string id, string version, ParsedRetailWriteRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("digestVersion", 1);
            writer.WriteString("id", id);
            writer.WriteString("version", version);
            writer.WriteString("region", request.Region);
            writer.WriteString("modelId", request.ModelId);
            writer.WritePropertyName("input");
            WriteDigestSlot(writer, request.Input);
            writer.WritePropertyName("output");
            WriteDigestSlot(writer, request.Output);
            if (request.CachedInput is { } cachedInput)
            {
                writer.WritePropertyName("cachedInput");
                WriteDigestSlot(writer, cachedInput);
            }
            writer.WriteEndObject();
        }

        return $"v1:{Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant()}";
    }

    private static void WriteDigestSlot(Utf8JsonWriter writer, ParsedMeterSelection slot)
    {
        writer.WriteStartObject();
        writer.WriteString("meterId", slot.MeterId);
        writer.WritePropertyName("expectedUsdPer1M");
        writer.WriteRawValue(
            NormalizeDecimal(slot.ExpectedUsdPer1M).ToString("G29", CultureInfo.InvariantCulture),
            skipInputValidation: true);
        writer.WriteEndObject();
    }

    private static decimal NormalizeDecimal(decimal value) =>
        value / 1.00000000000000000000000000000m;

    internal static bool TryParseRequest(string id, ReadOnlyMemory<byte> payload, out ParsedRetailWriteRequest request)
    {
        request = default!;
        try
        {
            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    MaxDepth = 16,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string? region = null;
            string? modelId = null;
            ParsedMeterSelection? input = null;
            ParsedMeterSelection? output = null;
            ParsedMeterSelection? cachedInput = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Null)
                    return false;

                switch (property.Name)
                {
                    case "region":
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            property.Value.GetString() is not { } parsedRegion ||
                            !IsValidRegion(parsedRegion) ||
                            !string.Equals(id, $"azure-retail-prices.standard.{parsedRegion}", StringComparison.Ordinal))
                            return false;
                        region = parsedRegion;
                        break;
                    case "modelId":
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            property.Value.GetString() is not { } parsedModelId ||
                            !IsValidModelId(parsedModelId))
                            return false;
                        modelId = parsedModelId;
                        break;
                    case "input":
                        if (!TryParseSlot(property.Value, out var parsedInput))
                            return false;
                        input = parsedInput;
                        break;
                    case "output":
                        if (!TryParseSlot(property.Value, out var parsedOutput))
                            return false;
                        output = parsedOutput;
                        break;
                    case "cachedInput":
                        if (!TryParseSlot(property.Value, out var parsedCached))
                            return false;
                        cachedInput = parsedCached;
                        break;
                    default:
                        return false;
                }
            }

            if (region is null || modelId is null || input is null || output is null)
                return false;

            var meterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                input.MeterId,
            };
            if (!meterIds.Add(output.MeterId))
                return false;
            if (cachedInput is not null && !meterIds.Add(cachedInput.MeterId))
                return false;

            request = new ParsedRetailWriteRequest(region, modelId, input, output, cachedInput);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseSlot(JsonElement element, out ParsedMeterSelection slot)
    {
        slot = default!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        string? meterId = null;
        decimal? expectedUsdPer1M = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Null)
                return false;

            switch (property.Name)
            {
                case "meterId":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        property.Value.GetString() is not { } parsedMeterId ||
                        !IsValidMeterId(parsedMeterId))
                        return false;
                    meterId = parsedMeterId;
                    break;
                case "expectedUsdPer1M":
                    if (!TryParseExactPositiveDecimal(property.Value, out var parsedPrice))
                        return false;
                    expectedUsdPer1M = parsedPrice;
                    break;
                default:
                    return false;
            }
        }

        if (meterId is null || expectedUsdPer1M is null)
            return false;

        slot = new ParsedMeterSelection(meterId, expectedUsdPer1M.Value);
        return true;
    }

    internal static bool TryParseExactPositiveDecimal(JsonElement element, out decimal value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        var raw = element.GetRawText();
        Span<char> rawDigits = stackalloc char[29];
        if (!TryExtractCanonicalBaseTen(raw, rawDigits, out var rawLength, out var rawExponent) ||
            !element.TryGetDecimal(out var parsed) ||
            parsed <= 0m)
            return false;

        var normalized = NormalizeDecimal(parsed).ToString("G29", CultureInfo.InvariantCulture);
        Span<char> parsedDigits = stackalloc char[29];
        if (!TryExtractCanonicalBaseTen(normalized, parsedDigits, out var parsedLength, out var parsedExponent))
            return false;

        if (rawLength != parsedLength ||
            rawExponent != parsedExponent ||
            !rawDigits[..rawLength].SequenceEqual(parsedDigits[..parsedLength]))
            return false;

        value = parsed;
        return true;
    }

    private static bool TryExtractCanonicalBaseTen(
        ReadOnlySpan<char> raw,
        Span<char> significantDigits,
        out int significantLength,
        out long exponent10)
    {
        significantLength = 0;
        exponent10 = 0;
        if (raw.IsEmpty || raw[0] == '-')
            return false;

        var seenDot = false;
        var seenNonZero = false;
        long fractionalDigits = 0;
        long trailingZeros = 0;
        var index = 0;

        for (; index < raw.Length; index++)
        {
            var ch = raw[index];
            if (ch == '.')
            {
                if (seenDot)
                    return false;
                seenDot = true;
                continue;
            }
            if (ch is 'e' or 'E')
                break;
            if (ch is < '0' or > '9')
                return false;

            if (seenDot)
                fractionalDigits++;

            if (ch == '0')
            {
                if (seenNonZero)
                    trailingZeros++;
                continue;
            }

            seenNonZero = true;
            if (trailingZeros > 0)
            {
                if (significantLength + trailingZeros >= significantDigits.Length)
                    return false;
                significantDigits.Slice(significantLength, (int)trailingZeros).Fill('0');
                significantLength += (int)trailingZeros;
                trailingZeros = 0;
            }

            if (significantLength >= significantDigits.Length)
                return false;
            significantDigits[significantLength++] = ch;
        }

        if (!seenNonZero)
            return false;

        long parsedExponent = 0;
        if (index < raw.Length)
        {
            index++;
            var negativeExponent = false;
            if (index < raw.Length && raw[index] is '+' or '-')
            {
                negativeExponent = raw[index] == '-';
                index++;
            }
            if (index == raw.Length)
                return false;

            const long exponentCap = 1_000_000_000L;
            for (; index < raw.Length; index++)
            {
                var ch = raw[index];
                if (ch is < '0' or > '9')
                    return false;
                if (parsedExponent < exponentCap)
                    parsedExponent = Math.Min(exponentCap, parsedExponent * 10 + (ch - '0'));
            }
            if (negativeExponent)
                parsedExponent = -parsedExponent;
        }

        exponent10 = trailingZeros - fractionalDigits + parsedExponent;
        return exponent10 is >= -28 and <= 28;
    }

    private static bool IsValidRegion(string value) =>
        value.Length is > 0 and <= 63 &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static bool IsValidModelId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool IsValidMeterId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    private static bool IsValidRouteRequest(HttpContext context, string id, string version)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128 || !KeyPattern.IsMatch(id) ||
            string.IsNullOrEmpty(version) || version.Length > 64 || !KeyPattern.IsMatch(version))
            return false;

        if (context.Request.Query.Count != 0 || context.Request.QueryString.HasValue)
            return false;

        var expectedPath =
            $"{context.Request.PathBase.Value}/api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}";
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(rawTarget))
            return string.Equals(rawTarget, expectedPath, StringComparison.Ordinal);

        return string.Equals(context.Request.Path.Value, expectedPath, StringComparison.Ordinal);
    }

    private static bool IsSupportedJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.IsNullOrEmpty(parsed.CharSet) ||
            string.Equals(parsed.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaximumRequestBodyBytes - output.Length + 1)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                if (output.Length + read > MaximumRequestBodyBytes)
                    throw new InvalidDataException("Request body exceeds the 64 KiB limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static PhaseTimeoutScope CreatePhaseTimeout(
        TimeProvider timeProvider,
        TimeSpan delay,
        CancellationToken cancellationToken) =>
        new(timeProvider, delay, cancellationToken);

    private readonly struct PhaseTimeoutScope : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _linked;

        public PhaseTimeoutScope(TimeProvider timeProvider, TimeSpan delay, CancellationToken cancellationToken)
        {
            _timer = new CancellationTokenSource(delay, timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timer.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _timer.Dispose();
        }
    }

    private static IResult Problem(int statusCode, string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    internal sealed record ParsedMeterSelection(string MeterId, decimal ExpectedUsdPer1M);

    internal sealed record ParsedRetailWriteRequest(
        string Region,
        string ModelId,
        ParsedMeterSelection Input,
        ParsedMeterSelection Output,
        ParsedMeterSelection? CachedInput);

    private enum StoreLookupOutcome
    {
        NotFound,
        Found,
        Conflict,
        Unavailable,
    }

    private sealed record StoreLookupResult(StoreLookupOutcome Outcome, RuntimeRateCard? RateCard);
}
