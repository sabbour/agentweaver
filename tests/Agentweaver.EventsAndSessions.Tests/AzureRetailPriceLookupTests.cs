using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.EventsAndSessions.Cost;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class AzureRetailPriceLookupTests
{
    private const string Region = "eastus";
    private const string Endpoint = "https://prices.azure.com/api/retail/prices";

    [Fact]
    public async Task BuildsFixedUsdFilterReadsPagesAndSortsTypedCandidates()
    {
        var continuation = $"{Endpoint}?currencyCode=USD&$skip=1000";
        var handler = new QueueHandler((_, number, _) => Task.FromResult(Ok(number == 1
            ? Envelope(
                [
                    Row("meter-z", "z-meter", "Zulu"),
                    Row("meter-a", "a-meter", "Alpha"),
                    Row("meter-a", "a-meter", "Alpha", price: 2m, tier: 3m)
                ],
                continuation)
            : Envelope([Row("meter-b", "b-meter", "Beta")]))));
        using var lookup = new AzureRetailPriceLookup(
            handler, new FixedClock(new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero)));

        var result = await lookup.SearchAsync("EASTUS", "  5.4  ");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(Region, result.Region);
        Assert.Equal(Endpoint, result.SourceUrl);
        Assert.Equal(new DateOnly(2026, 11, 15), result.RetrievedOn);
        Assert.Equal(new[] { "meter-a", "meter-a", "meter-b", "meter-z" }, result.Items.Select(item => item.MeterId));
        Assert.Equal(2, result.Items.Count(item => item.MeterId == "meter-a"));
        Assert.All(result.Items, item => Assert.Equal("USD", item.CurrencyCode));
        var query = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
        Assert.Contains("currencyCode=USD", query, StringComparison.Ordinal);
        Assert.Contains(
            "$filter=serviceName eq 'Foundry Models' and armRegionName eq 'eastus' and priceType eq 'Consumption' and contains(meterName, '5.4')",
            query,
            StringComparison.Ordinal);
        Assert.Equal(continuation, handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task NormalizesOnlySupportedPositiveTierZeroTokenPrices()
    {
        var thousand = Row("k", "k-meter", unit: "1K", price: 0.00025m);
        var million = Row("m", "m-meter", price: 15.125m);
        var unsupported = Row("u", "u-meter", unit: "hour", price: 0.25m);
        var nonzeroTier = Row("t", "t-meter", price: 1m, tier: 10m);
        var noPrice = Row("n", "n-meter", price: null);
        noPrice.Remove("retailPrice");
        var zeroPrice = Row("z", "z-meter", price: 0m);
        var noTier = Row("x", "x-meter");
        noTier.Remove("tierMinimumUnits");
        var lookup = Create(new QueueHandler((_, _, _) =>
            Task.FromResult(Ok(Envelope([thousand, million, unsupported, nonzeroTier, noPrice, zeroPrice, noTier])))));
        using (lookup)
        {
            var result = await lookup.SearchAsync(Region, "model");
            var items = result.Items.ToDictionary(item => item.MeterId, StringComparer.Ordinal);

            Assert.Equal(0.25m, items["k"].UsdPer1M);
            Assert.Equal(15.125m, items["m"].UsdPer1M);
            Assert.Null(items["u"].UsdPer1M);
            Assert.Null(items["t"].UsdPer1M);
            Assert.Null(items["n"].UsdPer1M);
            Assert.Null(items["z"].UsdPer1M);
            Assert.Null(items["x"].UsdPer1M);
            Assert.Null(items["x"].TierMinimumUnits);
        }
    }

    [Theory]
    [InlineData("", "model")]
    [InlineData("bad-region!", "model")]
    [InlineData(" eastus", "model")]
    [InlineData("eastus", "")]
    [InlineData("eastus", "meter' OR true")]
    [InlineData("eastus", "has\t tab")]
    [InlineData("eastus", "   ")]
    public async Task RejectsInvalidInputBeforeSendingARequest(string region, string search)
    {
        var handler = new QueueHandler((_, _, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP request."));
        using var lookup = Create(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => lookup.SearchAsync(region, search));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://example.com/api/retail/prices")]
    [InlineData("http://prices.azure.com/api/retail/prices")]
    [InlineData("https://prices.azure.com:444/api/retail/prices")]
    [InlineData("https://prices.azure.com/api/other")]
    [InlineData("https://user@prices.azure.com/api/retail/prices")]
    [InlineData("https://prices.azure.com/api/retail/prices#fragment")]
    [InlineData("https://prices.azure.com/api/retail/prices#")]
    public async Task RejectsUnsafeContinuationBeforeSendingIt(string nextPage)
    {
        var handler = new QueueHandler((_, _, _) => Task.FromResult(Ok(Envelope([Row()], nextPage))));
        using var lookup = Create(handler);

        var error = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
            () => lookup.SearchAsync(Region, "model"));

        Assert.Equal(AzureRetailPriceLookupException.PricingUnavailableCode, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RejectsOversizedAndRepeatedContinuationLinksBeforeTheNextRequest()
    {
        var oversizedHandler = new QueueHandler((_, _, _) =>
            Task.FromResult(Ok(Envelope([Row()], $"{Endpoint}?q={new string('a', 4100)}"))));
        using (var lookup = Create(oversizedHandler))
        {
            await Assert.ThrowsAsync<AzureRetailPriceLookupException>(() =>
                lookup.SearchAsync(Region, "model"));
            Assert.Single(oversizedHandler.Requests);
        }

        var repeatedUri = $"{Endpoint}?currencyCode=USD&$skip=1000";
        var repeatedHandler = new QueueHandler((_, _, _) =>
            Task.FromResult(Ok(Envelope([Row()], repeatedUri))));
        using (var lookup = Create(repeatedHandler))
        {
            await Assert.ThrowsAsync<AzureRetailPriceLookupException>(() =>
                lookup.SearchAsync(Region, "model"));
            Assert.Equal(2, repeatedHandler.Requests.Count);
        }
    }

    [Fact]
    public async Task RejectsRedirectsMalformedRowsAndInconsistentScope()
    {
        var redirectHandler = new QueueHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://example.com/") }
            }));
        using (var lookup = Create(redirectHandler))
        {
            await Assert.ThrowsAsync<AzureRetailPriceLookupException>(() =>
                lookup.SearchAsync(Region, "model"));
            Assert.Single(redirectHandler.Requests);
        }

        var wrongRegion = Row();
        wrongRegion["armRegionName"] = "westus";
        await AssertBadEnvelope(Envelope([wrongRegion]));
        var wrongCurrency = Row();
        wrongCurrency["currencyCode"] = "EUR";
        await AssertBadEnvelope(Envelope([wrongCurrency]));
        var wrongType = Row();
        wrongType["type"] = "Reservation";
        await AssertBadEnvelope(Envelope([wrongType]));
        await AssertBadEnvelope("{}");
        await AssertBadEnvelope("{");

        var malformedPrice = Row();
        malformedPrice["retailPrice"] = "free";
        await AssertBadEnvelope(Envelope([malformedPrice]));
        await AssertBadEnvelope(Envelope([Row(unit: "1K", price: decimal.MaxValue)]));

        var tooLongName = Row();
        tooLongName["productName"] = new string('x', 513);
        await AssertBadEnvelope(Envelope([tooLongName]));
    }

    [Fact]
    public async Task FailsTheWholeSearchWhenAStreamedPageExceedsItsByteLimit()
    {
        var body = Encoding.UTF8.GetBytes(Envelope([Row()]).Insert(1, $"\"padding\":\"{new string('x', 1_100_000)}\","));
        var handler = new QueueHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(body))
            };
            Assert.Null(response.Content.Headers.ContentLength);
            return Task.FromResult(response);
        });
        using var lookup = Create(handler);

        var error = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
            () => lookup.SearchAsync(Region, "model"));

        Assert.Equal(AzureRetailPriceLookupException.PricingUnavailableCode, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EnforcesPageAndRowLimitsWithoutReturningPartialResults()
    {
        var pageHandler = new QueueHandler((_, number, _) =>
            Task.FromResult(Ok(Envelope([Row($"m{number}", "meter")], $"{Endpoint}?$skip={number}"))));
        using (var lookup = Create(pageHandler))
        {
            await Assert.ThrowsAsync<AzureRetailPriceLookupException>(() =>
                lookup.SearchAsync(Region, "model"));
            Assert.Equal(20, pageHandler.Requests.Count);
        }

        var page = Enumerable.Range(0, 4000).Select(index => Row($"m{index}", "meter")).ToArray();
        var rowHandler = new QueueHandler((_, number, _) =>
        {
            JsonObject[] rows = number < 3
                ? page.Select(row => (JsonObject)row.DeepClone()).ToArray()
                : number == 3
                    ? page.Take(2000).ToArray()
                    : [Row("last", "meter")];
            return Task.FromResult(Ok(Envelope(rows, number < 4 ? $"{Endpoint}?$skip={number}" : null)));
        });
        using var rowLookup = Create(rowHandler);

        var rowError = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
            () => rowLookup.SearchAsync(Region, "model"));
        Assert.Equal(4, rowHandler.Requests.Count);
        Assert.Equal("row_limit", rowError.Reason);
    }

    [Fact]
    public async Task AppliesAnAggregateBodyLimitAcrossPages()
    {
        var padding = new string('x', 900_000);
        var handler = new QueueHandler((_, number, _) =>
        {
            var body = Envelope([Row($"m{number}", "meter")], number < 10 ? $"{Endpoint}?$skip={number}" : null);
            var root = JsonNode.Parse(body)!.AsObject();
            root["ignored"] = padding;
            return Task.FromResult(Ok(root.ToJsonString()));
        });
        using var lookup = Create(handler);

        await Assert.ThrowsAsync<AzureRetailPriceLookupException>(() =>
            lookup.SearchAsync(Region, "model"));

        Assert.Equal(10, handler.Requests.Count);
    }

    [Fact]
    public async Task SearchPropagatesCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new QueueHandler(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok(Envelope([Row()]));
        });
        using var lookup = Create(handler, timeout: TimeSpan.FromSeconds(20));
        using var callerCancellation = new CancellationTokenSource();

        var search = lookup.SearchAsync(Region, "model", callerCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        callerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => search.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UsesANewDeadlineForEachSearch()
    {
        var wait = true;
        var handler = new QueueHandler(async (_, _, cancellationToken) =>
        {
            if (wait)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok(Envelope([Row()]));
        });
        using var lookup = Create(handler, timeout: TimeSpan.FromMilliseconds(100));

        var deadlineError = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(() =>
            lookup.SearchAsync(Region, "model"));
        Assert.Equal("deadline_exceeded", deadlineError.Reason);

        wait = false;
        var retry = await lookup.SearchAsync(Region, "model");
        Assert.Single(retry.Items);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendsNoCredentialHeadersAndUsesASafeProductionHandler()
    {
        var handler = new QueueHandler((request, _, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.False(request.Headers.Contains("X-Tenant-ID"));
            Assert.False(request.Headers.Contains("X-Api-Key"));
            return Task.FromResult(Ok(Envelope([Row()])));
        });
        using var lookup = Create(handler);

        await lookup.SearchAsync(Region, "model");

        using var productionHandler = AzureRetailPriceLookup.CreateHandler();
        Assert.False(productionHandler.AllowAutoRedirect);
        Assert.False(productionHandler.UseCookies);
        Assert.False(productionHandler.UseDefaultCredentials);
        Assert.Null(productionHandler.Credentials);
        Assert.NotEqual(DecompressionMethods.None, productionHandler.AutomaticDecompression);
    }

    [Fact]
    public async Task EmptySearchReturnsTypedEmptyResult()
    {
        using var lookup = Create(new QueueHandler((_, _, _) =>
            Task.FromResult(Ok(Envelope([], string.Empty)))));

        var result = await lookup.SearchAsync(Region, "not-found");

        Assert.Empty(result.Items);
        Assert.Equal(Region, result.Region);
    }

    [Fact]
    public async Task ConcurrentSearchesKeepTheirOwnPaginationState()
    {
        var leftNext = $"{Endpoint}?$skip=left";
        var rightNext = $"{Endpoint}?$skip=right";
        var handler = new QueueHandler(async (request, _, cancellationToken) =>
        {
            await Task.Yield();
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var left = query.Contains("left", StringComparison.Ordinal);
            var continuation = query.Contains("$skip=", StringComparison.Ordinal);
            if (!continuation)
                return Ok(Envelope([Row(left ? "left-1" : "right-1", "meter")], left ? leftNext : rightNext));
            return Ok(Envelope([Row(left ? "left-2" : "right-2", "meter")]));
        });
        using var lookup = Create(handler);

        var results = await Task.WhenAll(
            lookup.SearchAsync(Region, "left"),
            lookup.SearchAsync(Region, "right"));

        Assert.Equal(new[] { "left-1", "left-2" }, results[0].Items.Select(item => item.MeterId));
        Assert.Equal(new[] { "right-1", "right-2" }, results[1].Items.Select(item => item.MeterId));
        Assert.Equal(4, handler.Requests.Count);
    }

    private static async Task AssertBadEnvelope(string body)
    {
        using var lookup = Create(new QueueHandler((_, _, _) => Task.FromResult(Ok(body))));
        await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
            () => lookup.SearchAsync(Region, "model"));
    }

    private static AzureRetailPriceLookup Create(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null,
        TimeSpan? resolveTimeout = null) =>
        new(handler, timeProvider ?? TimeProvider.System, timeout, resolveTimeout);

    [Fact]
    public async Task ResolveMetersNormalizesUnitsAndIgnoresIneligibleRowsAcrossPages()
    {
        var handler = new QueueHandler((request, number, _) =>
        {
            if (number == 1)
            {
                var filter = Uri.UnescapeDataString(request.RequestUri!.Query);
                Assert.Contains("meterId eq 'm-in'", filter, StringComparison.Ordinal);
                Assert.Contains("meterId eq 'm-out'", filter, StringComparison.Ordinal);
                return Task.FromResult(Ok(Envelope(
                    [
                        Row("m-in", "Input", unit: "1K", price: 0.0025m, tier: 0m),
                        Row("m-in", "Input Tiered", unit: "1K", price: 0.001m, tier: 100m),
                    ],
                    $"{Endpoint}?$skip=1")));
            }
            return Task.FromResult(Ok(Envelope(
                [
                    Row("m-out", "Output", unit: "1M", price: 10m, tier: 0m),
                    Row("m-out", "Output Bad Unit", unit: "100", price: 1m, tier: 0m),
                ])));
        });
        using var lookup = Create(handler);

        var resolved = await lookup.ResolveMetersAsync(Region, ["m-in", "m-out"]);

        Assert.Equal(2.5m, resolved["m-in"]);
        Assert.Equal(10m, resolved["m-out"]);
    }

    [Fact]
    public async Task ResolveMetersRejectsMissingAndAmbiguousMetersAfterFullPaging()
    {
        var missingHandler = new QueueHandler((_, _, _) =>
            Task.FromResult(Ok(Envelope([Row("m-in", "Input", unit: "1M", price: 2.5m, tier: 0m)]))));
        using (var missingLookup = Create(missingHandler))
        {
            var error = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
                () => missingLookup.ResolveMetersAsync(Region, ["m-in", "m-out"]));
            Assert.Equal("meter_not_found", error.Reason);
        }

        // Duplicate eligible row on page 2 must still be rejected even if page 1 matched.
        var ambiguousHandler = new QueueHandler((_, number, _) => number == 1
            ? Task.FromResult(Ok(Envelope(
                [
                    Row("m-in", "Input", unit: "1M", price: 2.5m, tier: 0m),
                    Row("m-out", "Output", unit: "1M", price: 10m, tier: 0m),
                ],
                $"{Endpoint}?$skip=1")))
            : Task.FromResult(Ok(Envelope(
                [Row("m-in", "Input Duplicate", unit: "1M", price: 3.0m, tier: 0m)]))));
        using var ambiguousLookup = Create(ambiguousHandler);

        var ambiguousError = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
            () => ambiguousLookup.ResolveMetersAsync(Region, ["m-in", "m-out"]));
        Assert.Equal("ambiguous_meter", ambiguousError.Reason);
        Assert.Equal(2, ambiguousHandler.Requests.Count);
    }

    [Fact]
    public async Task ResolveMetersEnforcesDeadline()
    {
        var handler = new QueueHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok(Envelope([]));
        });
        using var lookup = Create(handler, resolveTimeout: TimeSpan.FromMilliseconds(50));

        var deadlineError = await Assert.ThrowsAsync<AzureRetailPriceLookupException>(
            () => lookup.ResolveMetersAsync(Region, ["m-in", "m-out"]));
        Assert.Equal("deadline_exceeded", deadlineError.Reason);
    }

    [Fact]
    public async Task ResolveMetersPropagatesCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new QueueHandler(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok(Envelope([]));
        });
        using var lookup = Create(handler, resolveTimeout: TimeSpan.FromSeconds(20));
        using var callerCts = new CancellationTokenSource();

        var resolution = lookup.ResolveMetersAsync(Region, ["m-in", "m-out"], callerCts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolution.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static string Envelope(IEnumerable<JsonObject> rows, string? nextPage = null)
    {
        var items = new JsonArray();
        foreach (var row in rows)
            items.Add(row);
        return new JsonObject
        {
            ["Items"] = items,
            ["NextPageLink"] = nextPage
        }.ToJsonString();
    }

    private static JsonObject Row(
        string meterId = "meter-1",
        string meterName = "model meter",
        string productName = "Product",
        string unit = "1M",
        decimal? price = 1m,
        decimal? tier = 0m) =>
        new()
        {
            ["meterId"] = meterId,
            ["meterName"] = meterName,
            ["productName"] = productName,
            ["skuName"] = "Standard",
            ["unitOfMeasure"] = unit,
            ["currencyCode"] = "USD",
            ["armRegionName"] = Region,
            ["serviceName"] = "Foundry Models",
            ["type"] = "Consumption",
            ["retailPrice"] = JsonValue.Create(price),
            ["tierMinimumUnits"] = JsonValue.Create(tier)
        };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class QueueHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly object _gate = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int number;
            lock (_gate)
            {
                Requests.Add(request);
                number = Requests.Count;
            }
            return send(request, number, cancellationToken);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
