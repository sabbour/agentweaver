using System.Collections.Immutable;
using System.Net;
using System.Text.Json;

namespace Agentweaver.EventsAndSessions.Cost;

public sealed record AzureRetailPriceLookupResult(
    string Region,
    string SourceUrl,
    DateOnly RetrievedOn,
    ImmutableArray<AzureRetailMeterCandidate> Items);

public sealed record AzureRetailMeterCandidate(
    string MeterId,
    string MeterName,
    string ProductName,
    string SkuName,
    string UnitOfMeasure,
    string CurrencyCode,
    decimal? RetailPrice,
    decimal? TierMinimumUnits,
    decimal? UsdPer1M);

public sealed class AzureRetailPriceLookupException : Exception
{
    public const string PricingUnavailableCode = "pricing_unavailable";

    public string Code => PricingUnavailableCode;
    internal string Reason { get; }

    internal AzureRetailPriceLookupException(string reason)
        : base("Azure Retail pricing is unavailable.")
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        Reason = reason.Length <= 64 ? reason : reason[..64];
    }
}

public sealed class AzureRetailPriceLookup : IDisposable
{
    private const string Endpoint = "https://prices.azure.com/api/retail/prices";
    private const int MaxPages = 20;
    private const int MaxPageBytes = 1024 * 1024;
    private const int MaxTotalBytes = 8 * 1024 * 1024;
    private const int MaxRows = 10_000;
    private const int MaxUriLength = 4096;
    private const int MaxTextLength = 512;
    private const int MaxMeterIdLength = 128;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _searchTimeout;
    private bool _disposed;

    public AzureRetailPriceLookup(TimeProvider? timeProvider = null)
        : this(new HttpClient(CreateHandler()), timeProvider ?? TimeProvider.System, SearchTimeout)
    {
    }

    internal AzureRetailPriceLookup(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? searchTimeout = null)
        : this(
            new HttpClient(handler, disposeHandler: true),
            timeProvider ?? TimeProvider.System,
            searchTimeout ?? SearchTimeout)
    {
    }

    private AzureRetailPriceLookup(
        HttpClient client,
        TimeProvider timeProvider,
        TimeSpan searchTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (searchTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(searchTimeout));
        _client = client;
        _timeProvider = timeProvider;
        _searchTimeout = searchTimeout;
    }

    public async Task<AzureRetailPriceLookupResult> SearchAsync(
        string region,
        string search,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedRegion = ValidateRegion(region);
        var normalizedSearch = ValidateSearch(search);
        using var deadline = new CancellationTokenSource(_searchTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        try
        {
            var filter = $"serviceName eq 'Foundry Models' and armRegionName eq '{normalizedRegion}' and " +
                $"priceType eq 'Consumption' and contains(meterName, '{normalizedSearch}')";
            string? nextPage = $"{Endpoint}?currencyCode=USD&$filter={Uri.EscapeDataString(filter)}";
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<AzureRetailMeterCandidate>();
            var totalBytes = 0;
            var pageCount = 0;

            while (nextPage is not null)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (pageCount >= MaxPages)
                    throw Unavailable("page_limit");
                var pageUri = ValidatePageUri(nextPage);
                if (!visited.Add(pageUri.AbsoluteUri))
                    throw Unavailable("repeated_page");
                pageCount++;

                using var request = new HttpRequestMessage(HttpMethod.Get, pageUri);
                using var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw Unavailable("http_status");

                var body = await ReadBodyAsync(
                    response.Content,
                    Math.Min(MaxPageBytes, MaxTotalBytes - totalBytes),
                    linked.Token).ConfigureAwait(false);
                totalBytes += body.Length;
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("Items", out var rows) ||
                    rows.ValueKind != JsonValueKind.Array)
                    throw Unavailable("invalid_envelope");

                foreach (var row in rows.EnumerateArray())
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (row.ValueKind != JsonValueKind.Object)
                        throw Unavailable("invalid_row");
                    if (items.Count >= MaxRows)
                        throw Unavailable("row_limit");
                    items.Add(ParseCandidate(row, normalizedRegion));
                }

                if (!root.TryGetProperty("NextPageLink", out var next))
                    nextPage = null;
                else if (next.ValueKind == JsonValueKind.Null)
                    nextPage = null;
                else if (next.ValueKind == JsonValueKind.String)
                {
                    nextPage = next.GetString();
                    if (string.IsNullOrEmpty(nextPage))
                        nextPage = null;
                    else if (totalBytes >= MaxTotalBytes)
                        throw Unavailable("total_body_limit");
                }
                else
                    throw Unavailable("invalid_next_page");
            }

            linked.Token.ThrowIfCancellationRequested();
            var ordered = items
                .OrderBy(item => item.ProductName, StringComparer.Ordinal)
                .ThenBy(item => item.MeterName, StringComparer.Ordinal)
                .ThenBy(item => item.MeterId, StringComparer.Ordinal)
                .ThenBy(item => item.SkuName, StringComparer.Ordinal)
                .ThenBy(item => item.UnitOfMeasure, StringComparer.Ordinal)
                .ThenBy(item => item.TierMinimumUnits)
                .ThenBy(item => item.RetailPrice)
                .ToImmutableArray();
            return new AzureRetailPriceLookupResult(
                normalizedRegion,
                Endpoint,
                DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime),
                ordered);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw Unavailable("deadline_exceeded");
        }
        catch (AzureRetailPriceLookupException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw Unavailable("request_failed");
        }
        catch (IOException)
        {
            throw Unavailable("response_read_failed");
        }
        catch (JsonException)
        {
            throw Unavailable("invalid_json");
        }
        catch (OverflowException)
        {
            throw Unavailable("price_overflow");
        }
    }

    internal static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    };

    private static string ValidateRegion(string? region)
    {
        if (region is null || region.Length is 0 or > 64 ||
            !region.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Region must contain 1 to 64 ASCII letters or digits.", nameof(region));
        return region.ToLowerInvariant();
    }

    private static string ValidateSearch(string? search)
    {
        var normalized = search?.Trim(' ');
        if (normalized is null || normalized.Length is 0 or > 64 ||
            !normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or ' '))
            throw new ArgumentException(
                "Search must contain 1 to 64 ASCII letters, digits, periods, hyphens, or spaces.",
                nameof(search));
        return normalized;
    }

    private static Uri ValidatePageUri(string value)
    {
        if (value.Length > MaxUriLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            uri.UserInfo.Length != 0 ||
            value.Contains('#') ||
            uri.Fragment.Length != 0 ||
            !uri.Host.Equals("prices.azure.com", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath != "/api/retail/prices")
            throw Unavailable("invalid_page_uri");
        return uri;
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 8192));
        var chunk = new byte[8192];
        while (true)
        {
            var remaining = maximumBytes - (int)buffer.Length;
            var count = Math.Min(chunk.Length, remaining + 1);
            var read = await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();
            if (read > remaining)
                throw Unavailable("body_limit");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static AzureRetailMeterCandidate ParseCandidate(JsonElement row, string region)
    {
        var rowRegion = RequiredText(row, "armRegionName", MaxTextLength);
        if (!rowRegion.Equals(region, StringComparison.OrdinalIgnoreCase) ||
            RequiredText(row, "serviceName", MaxTextLength) != "Foundry Models" ||
            RequiredText(row, "currencyCode", MaxTextLength) != "USD" ||
            RequiredText(row, "type", MaxTextLength) != "Consumption")
            throw Unavailable("scope_mismatch");

        var meterId = RequiredText(row, "meterId", MaxMeterIdLength);
        var meterName = RequiredText(row, "meterName", MaxTextLength);
        if (string.IsNullOrWhiteSpace(meterId) || string.IsNullOrWhiteSpace(meterName))
            throw Unavailable("invalid_meter_identity");

        var productName = RequiredText(row, "productName", MaxTextLength);
        var skuName = RequiredText(row, "skuName", MaxTextLength);
        var unit = RequiredText(row, "unitOfMeasure", MaxTextLength);
        var price = OptionalDecimal(row, "retailPrice");
        var tier = OptionalDecimal(row, "tierMinimumUnits");
        if (tier is < 0)
            throw Unavailable("invalid_tier");

        decimal? usdPer1M = null;
        if (price is > 0 && tier == 0)
        {
            var multiplier = unit switch
            {
                "1K" => 1000m,
                "1M" => 1m,
                _ => 0m
            };
            if (multiplier > 0)
                usdPer1M = checked(price.Value * multiplier);
        }

        return new AzureRetailMeterCandidate(
            meterId, meterName, productName, skuName, unit, "USD", price, tier, usdPer1M);
    }

    private static string RequiredText(JsonElement row, string name, int maximumLength)
    {
        if (!row.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text ||
            text.Length > maximumLength)
            throw Unavailable("invalid_text_field");
        return text;
    }

    private static decimal? OptionalDecimal(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            throw Unavailable("invalid_numeric_field");
        return number;
    }

    private static AzureRetailPriceLookupException Unavailable(string reason) => new(reason);

    public void Dispose()
    {
        if (_disposed)
            return;
        _client.Dispose();
        _disposed = true;
    }
}
