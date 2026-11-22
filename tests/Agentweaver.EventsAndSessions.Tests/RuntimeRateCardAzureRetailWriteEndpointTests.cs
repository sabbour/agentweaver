using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class RuntimeRateCardAzureRetailWriteEndpointTests : IAsyncLifetime
{
    private const string ProjectsAudience = "agentweaver.projects";
    private const string EventsAudience = "events";
    private const string ActorId = "33333333-3333-3333-3333-333333333333";
    private const string RetailEndpoint = "https://prices.azure.com/api/retail/prices";

    private readonly SessionsPostgresFixture _fixture;
    private readonly string _schema = "retail_rates_" + Guid.NewGuid().ToString("N");
    private RuntimeRateCardStore _store = null!;

    public RuntimeRateCardAzureRetailWriteEndpointTests(SessionsPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        _store = new RuntimeRateCardStore(_fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CreatesReplaysEquivalentDecimalsWithoutCallingAzureAndMatchesOwnerReadAndPin()
    {
        var projectsCalls = 0;
        var azureCalls = 0;
        using var host = await StartTestHostAsync(
            projectsRespond: request =>
            {
                projectsCalls++;
                Assert.Equal("/api/authorization/platform/model-pricing-rate-cards", request.RequestUri!.PathAndQuery);
                Assert.Equal("tenant-1", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
                return NoContent();
            },
            azureRespond: (_, _) =>
            {
                azureCalls++;
                return Task.FromResult(JsonOk(AzureEnvelope(
                [
                    AzureRow("meter-in", "eastus", "1K", 0.0025m, 0m),
                    AzureRow("meter-out", "eastus", "1M", 10m, 0m),
                    AzureRow("meter-cache", "eastus", "1K", 0.00125m, 0m),
                ])));
            });
        using var client = host.GetTestClient();

        const string id = "azure-retail-prices.standard.eastus";
        const string version = "2026-11-21";
        var path = $"/api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}";

        var firstBody = """
            {
              "region": "eastus",
              "modelId": "gpt-4o",
              "input": { "meterId": "meter-in", "expectedUsdPer1M": 2.5 },
              "output": { "meterId": "meter-out", "expectedUsdPer1M": 10 },
              "cachedInput": { "meterId": "meter-cache", "expectedUsdPer1M": 1.25 }
            }
            """;

        string createdCanonical;
        string createdEtag;
        using (var request = CreatePutRequest(path, firstBody))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            createdCanonical = await response.Content.ReadAsStringAsync();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(createdCanonical)))
                .ToLowerInvariant();
            createdEtag = $"\"{hash}\"";
            Assert.Equal(createdEtag, response.Headers.ETag?.ToString());
            Assert.False(response.Headers.ETag!.IsWeak);
            Assert.Equal(2, projectsCalls);
            Assert.Equal(1, azureCalls);
        }

        // Different JSON formatting and equivalent decimal spellings replay 200 without calling Azure.
        var replayBody = """
            {
              "cachedInput": { "expectedUsdPer1M": 1.2500, "meterId": "meter-cache" },
              "output": { "expectedUsdPer1M": 10.00, "meterId": "meter-out" },
              "input": { "expectedUsdPer1M": 2.500, "meterId": "meter-in" },
              "modelId": "gpt-4o",
              "region": "eastus"
            }
            """;
        using (var replayRequest = CreatePutRequest(path, replayBody))
        using (var replayed = await client.SendAsync(replayRequest))
        {
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            Assert.Equal("no-store", replayed.Headers.CacheControl?.ToString());
            Assert.Equal(createdEtag, replayed.Headers.ETag?.ToString());
            Assert.Equal(createdCanonical, await replayed.Content.ReadAsStringAsync());
            Assert.Equal(3, projectsCalls);
            Assert.Equal(1, azureCalls);
        }

        // Owner read returns the exact same canonical bytes and ETag; digest stays out of card and pin.
        using var ownerReadRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/internal/runtime-rate-cards?id={Uri.EscapeDataString(id)}&version={Uri.EscapeDataString(version)}");
        ownerReadRequest.Headers.Add("X-Subject", "projects-owner");
        using var ownerRead = await client.SendAsync(ownerReadRequest);
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        Assert.Equal("no-store", ownerRead.Headers.CacheControl?.ToString());
        Assert.Equal(createdEtag, ownerRead.Headers.ETag?.ToString());
        var ownerBytes = await ownerRead.Content.ReadAsByteArrayAsync();
        Assert.Equal(createdCanonical, Encoding.UTF8.GetString(ownerBytes));
        Assert.DoesNotContain("azure_retail_request_digest", createdCanonical, StringComparison.Ordinal);

        var parsedRead = RuntimeRateCardReadContract.Deserialize(ownerBytes)!;
        var pin = RuntimeRateCardReadContract.CreatePin(parsedRead);
        Assert.True(RuntimeRateCardReadContract.MatchesPin(parsedRead, pin));
        var retailProvenance = Assert.IsType<AzureRetailRuntimeRateCardReadProvenance>(parsedRead.Provenance);
        Assert.Equal("eastus", retailProvenance.Region);
        Assert.Equal(["meter-cache", "meter-in", "meter-out"], retailProvenance.MeterIds.ToArray());
    }

    [Theory]
    [InlineData("/api/platform/model-pricing/azure-retail/rate-cards/-bad/v1")]
    [InlineData("/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1?extra=1")]
    [InlineData("/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v%2F1")]
    public async Task RejectsInvalidRouteKeysQueriesAndEncodedAliasesBeforeProjectsOrAzure(string path)
    {
        var projectsCalls = 0;
        var azureCalls = 0;
        using var host = await StartTestHostAsync(
            projectsRespond: _ => { projectsCalls++; return NoContent(); },
            azureRespond: (_, _) => { azureCalls++; return Task.FromResult(JsonOk(AzureEnvelope([]))); });
        using var client = host.GetTestClient();

        using var request = CreatePutRequest(path, ValidRequestBody());
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, projectsCalls);
        Assert.Equal(0, azureCalls);
    }

    [Fact]
    public async Task EnforcesMediaTypeAndBounded64KiBUpload()
    {
        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: (_, _) => Task.FromResult(JsonOk(AzureEnvelope([]))));
        using var client = host.GetTestClient();
        var path = "/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v1";

        using (var wrongType = CreatePutRequest(path, ValidRequestBody(), contentType: "text/plain"))
        using (var res = await client.SendAsync(wrongType))
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);

        var oversized = new string(' ', 64 * 1024 + 1);
        using (var tooLargeDeclared = CreatePutRequest(path, oversized))
        using (var res = await client.SendAsync(tooLargeDeclared))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);

        using (var tooLargeStreamed = CreatePutRequest(path, oversized, omitContentLength: true))
        using (var res = await client.SendAsync(tooLargeStreamed))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
    }

    [Theory]
    [InlineData("azure-retail-prices.standard.westus", """{"region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":1},"output":{"meterId":"m2","expectedUsdPer1M":2}}""")]
    [InlineData("azure-retail-prices.standard.eastus", """{"region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":1},"output":{"meterId":"m1","expectedUsdPer1M":2}}""")]
    [InlineData("azure-retail-prices.standard.eastus", """{"region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":1},"output":{"meterId":"M1","expectedUsdPer1M":2}}""")]
    [InlineData("azure-retail-prices.standard.eastus", """{"region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":0},"output":{"meterId":"m2","expectedUsdPer1M":2}}""")]
    [InlineData("azure-retail-prices.standard.eastus", """{"region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":1},"output":{"meterId":"m2","expectedUsdPer1M":2},"cachedInput":null}""")]
    [InlineData("azure-retail-prices.standard.eastus", """{"region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":1},"output":{"meterId":"m2","expectedUsdPer1M":2},"extra":true}""")]
    [InlineData("azure-retail-prices.standard.eastus", """{"region":"eastus","region":"eastus","modelId":"gpt-4o","input":{"meterId":"m1","expectedUsdPer1M":1},"output":{"meterId":"m2","expectedUsdPer1M":2}}""")]
    public async Task RejectsMalformedDuplicateNullOrMismatchedRequestBodies(string id, string body)
    {
        var azureCalls = 0;
        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: (_, _) => { azureCalls++; return Task.FromResult(JsonOk(AzureEnvelope([]))); });
        using var client = host.GetTestClient();

        using var request = CreatePutRequest(
            $"/api/platform/model-pricing/azure-retail/rate-cards/{id}/v1", body);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, azureCalls);
    }

    [Fact]
    public async Task DeniesEarlyAndRechecksExpirationAndAuthorityAfterLookupWithoutInserting()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var projectsAttempt = 0;
        var projectsStatus = HttpStatusCode.NoContent;
        var advanceClockOnAzure = false;

        using var host = await StartTestHostAsync(
            projectsRespond: _ =>
            {
                projectsAttempt++;
                if (projectsAttempt == 2 && projectsStatus != HttpStatusCode.NoContent)
                    return new HttpResponseMessage(projectsStatus);
                return NoContent();
            },
            azureRespond: (_, _) =>
            {
                if (advanceClockOnAzure)
                    clock.Advance(TimeSpan.FromMinutes(2));
                return Task.FromResult(JsonOk(AzureEnvelope(
                [
                    AzureRow("meter-in", "eastus", "1M", 2.5m, 0m),
                    AzureRow("meter-out", "eastus", "1M", 10m, 0m),
                ])));
            },
            timeProvider: clock);
        using var client = host.GetTestClient();
        var path = "/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v-auth";

        // 1. Revoked during Azure lookup -> 403, nothing stored.
        projectsStatus = HttpStatusCode.Forbidden;
        using (var revokedReq = CreatePutRequest(path, ValidRequestBody(), expUnix: clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds()))
        using (var revokedRes = await client.SendAsync(revokedReq))
        {
            Assert.Equal(HttpStatusCode.Forbidden, revokedRes.StatusCode);
            Assert.Null(await _store.GetAsync("azure-retail-prices.standard.eastus", "v-auth"));
        }

        // 2. Token expires during Azure lookup -> 401, nothing stored.
        projectsAttempt = 0;
        projectsStatus = HttpStatusCode.NoContent;
        advanceClockOnAzure = true;
        using (var expReq = CreatePutRequest(path, ValidRequestBody(), expUnix: clock.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds()))
        using (var expRes = await client.SendAsync(expReq))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, expRes.StatusCode);
            Assert.Null(await _store.GetAsync("azure-retail-prices.standard.eastus", "v-auth"));
        }
    }

    [Fact]
    public async Task Returns409PricingChangedAnd503PricingUnavailable()
    {
        var priceMode = "changed";
        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: (_, _) =>
            {
                if (priceMode == "changed")
                    return Task.FromResult(JsonOk(AzureEnvelope(
                    [
                        AzureRow("meter-in", "eastus", "1M", 3.0m, 0m),
                        AzureRow("meter-out", "eastus", "1M", 10m, 0m),
                    ])));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            });
        using var client = host.GetTestClient();
        var path = "/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v-price";

        using (var changedReq = CreatePutRequest(path, ValidRequestBody()))
        using (var changedRes = await client.SendAsync(changedReq))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedRes.StatusCode);
            using var doc = JsonDocument.Parse(await changedRes.Content.ReadAsStringAsync());
            Assert.Equal("pricing_changed", doc.RootElement.GetProperty("code").GetString());
        }

        priceMode = "unavailable";
        using (var unavailReq = CreatePutRequest(path, ValidRequestBody()))
        using (var unavailRes = await client.SendAsync(unavailReq))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailRes.StatusCode);
            using var doc = JsonDocument.Parse(await unavailRes.Content.ReadAsStringAsync());
            Assert.Equal("pricing_unavailable", doc.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task RejectsLegacyCollisionAndDifferentDigestWithoutCallingAzure()
    {
        var azureCalls = 0;
        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: (_, _) =>
            {
                azureCalls++;
                return Task.FromResult(JsonOk(AzureEnvelope(
                [
                    AzureRow("meter-in", "eastus", "1M", 2.5m, 0m),
                    AzureRow("meter-out", "eastus", "1M", 10m, 0m),
                ])));
            });
        using var client = host.GetTestClient();

        // Seed a manual card (null digest) at a matching retail ID.
        const string legacyId = "azure-retail-prices.standard.eastus";
        await _store.SaveAsync(new RuntimeRateCard(
            new CostRateCard(
                legacyId,
                "v-legacy",
                SdkMeterSources.ByokTokens,
                "USD",
                1_000_000m,
                new Dictionary<string, decimal> { ["gpt-4o|input"] = 2.5m, ["gpt-4o|output"] = 10m }
                    .ToImmutableDictionary(StringComparer.Ordinal)),
            new ManualRateCardProvenance("https://example.com/prices", new DateOnly(2026, 11, 21))));

        using (var legacyCollisionReq = CreatePutRequest(
            $"/api/platform/model-pricing/azure-retail/rate-cards/{legacyId}/v-legacy", ValidRequestBody()))
        using (var legacyCollisionRes = await client.SendAsync(legacyCollisionReq))
        {
            Assert.Equal(HttpStatusCode.Conflict, legacyCollisionRes.StatusCode);
            Assert.Equal(0, azureCalls);
        }

        // Create a retail card, then send a different request at the same key -> 409 without calling Azure.
        var retailPath = $"/api/platform/model-pricing/azure-retail/rate-cards/{legacyId}/v-retail";
        using (var createReq = CreatePutRequest(retailPath, ValidRequestBody()))
        using (var createRes = await client.SendAsync(createReq))
            Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        Assert.Equal(1, azureCalls);

        var differentBody = """
            {
              "region": "eastus",
              "modelId": "gpt-4o",
              "input": { "meterId": "meter-in", "expectedUsdPer1M": 3.0 },
              "output": { "meterId": "meter-out", "expectedUsdPer1M": 10 }
            }
            """;
        using (var conflictReq = CreatePutRequest(retailPath, differentBody))
        using (var conflictRes = await client.SendAsync(conflictReq))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflictRes.StatusCode);
            Assert.Equal(1, azureCalls);
        }
    }

    [Fact]
    public async Task WinnerCommittedDuringFailedOrChangedAzureLookupReturnsWinner200()
    {
        const string id = "azure-retail-prices.standard.eastus";
        const string version = "v-race-winner";
        var path = $"/api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}";
        var body = ValidRequestBody();

        var parsed = default(RuntimeRateCardAzureRetailWriteEndpoint.ParsedRetailWriteRequest);
        Assert.True(RuntimeRateCardAzureRetailWriteEndpoint.TryParseRequest(id, Encoding.UTF8.GetBytes(body), out parsed));
        var digest = RuntimeRateCardAzureRetailWriteEndpoint.ComputeRequestDigest(id, version, parsed);
        var winnerCard = new RuntimeRateCard(
            new CostRateCard(
                id,
                version,
                SdkMeterSources.ByokTokens,
                "USD",
                1_000_000m,
                new Dictionary<string, decimal> { ["gpt-4o|input"] = 2.5m, ["gpt-4o|output"] = 10m }
                    .ToImmutableDictionary(StringComparer.Ordinal)),
            new AzureRetailRateCardProvenance("eastus", ["meter-in", "meter-out"]));

        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: async (_, _) =>
            {
                // Simulate a concurrent winner committing while this request's Azure lookup fails.
                await _store.SaveRetailAsync(winnerCard, digest);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });
        using var client = host.GetTestClient();

        using var req = CreatePutRequest(path, body);
        using var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(RuntimeRateCardContract.SerializeCanonical(winnerCard), await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConcurrentInsertRaceReturns201And200WithCommittedWinner()
    {
        const string id = "azure-retail-prices.standard.eastus";
        const string version = "v-concurrent";
        var path = $"/api/platform/model-pricing/azure-retail/rate-cards/{id}/{version}";
        var bothEnteredAzure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var azureEntered = 0;

        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: async (_, _) =>
            {
                if (Interlocked.Increment(ref azureEntered) == 2)
                    bothEnteredAzure.TrySetResult();
                await bothEnteredAzure.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return JsonOk(AzureEnvelope(
                [
                    AzureRow("meter-in", "eastus", "1M", 2.5m, 0m),
                    AzureRow("meter-out", "eastus", "1M", 10m, 0m),
                ]));
            });
        using var client = host.GetTestClient();

        var task1 = Task.Run(async () =>
        {
            using var req = CreatePutRequest(path, ValidRequestBody());
            return await client.SendAsync(req);
        });
        var task2 = Task.Run(async () =>
        {
            using var req = CreatePutRequest(path, ValidRequestBody());
            return await client.SendAsync(req);
        });

        using var res1 = await task1;
        using var res2 = await task2;
        var statuses = new[] { res1.StatusCode, res2.StatusCode }.OrderBy(s => (int)s).ToArray();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], statuses);
        Assert.Equal(await res1.Content.ReadAsStringAsync(), await res2.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EnforcesPhaseDeadlinesAndCallerCancellation()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var delayProjects = true;
        using var host = await StartTestHostAsync(
            projectsRespond: _ => NoContent(),
            azureRespond: (_, _) => Task.FromResult(JsonOk(AzureEnvelope([]))),
            timeProvider: clock,
            projectsAsyncRespond: async (_, cancellationToken) =>
            {
                if (delayProjects)
                {
                    clock.Advance(TimeSpan.FromSeconds(6));
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return NoContent();
            });
        using var client = host.GetTestClient();
        var path = "/api/platform/model-pricing/azure-retail/rate-cards/azure-retail-prices.standard.eastus/v-timeout";

        using (var timeoutReq = CreatePutRequest(path, ValidRequestBody()))
        using (var timeoutRes = await client.SendAsync(timeoutReq))
            Assert.Equal(HttpStatusCode.BadGateway, timeoutRes.StatusCode);

        delayProjects = false;
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        using var cancelledReq = CreatePutRequest(path, ValidRequestBody());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync(cancelledReq, callerCts.Token));
    }

    private async Task<IHost> StartTestHostAsync(
        Func<HttpRequestMessage, HttpResponseMessage> projectsRespond,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> azureRespond,
        TimeProvider? timeProvider = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? projectsAsyncRespond = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Audience"] = EventsAudience,
        });
        builder.Services.AddAuthentication("TestAuth")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                PlatformComponentServiceAuthenticationOptions.PolicyName,
                policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        builder.Services.AddSingleton(new ProjectsAuthorizationContextOptions(
            "https://projects.internal/", ProjectsAudience, "https://issuer.example/"));
        builder.Services.AddHttpClient<ProjectsAuthorizationContextClient>()
            .ConfigurePrimaryHttpMessageHandler(() => projectsAsyncRespond is not null
                ? new AsyncDelegateHandler(projectsAsyncRespond)
                : new DelegateHandler(projectsRespond));
        builder.Services.AddSingleton(_ => new AzureRetailPriceLookup(
            new AsyncDelegateHandler(azureRespond),
            timeProvider ?? TimeProvider.System));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRuntimeRateCardOwnerRead(new PlatformComponentServiceAuthenticationOptions(
            new Uri("https://issuer.example/"),
            new Uri("https://events-private.example/"),
            "projects-owner"));
        app.MapRuntimeRateCardAzureRetailWriteEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage CreatePutRequest(
        string path,
        string body,
        string contentType = "application/json",
        string scopes = "api.read projects.admin",
        long? expUnix = null,
        bool omitContentLength = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        request.Headers.Add("X-Scopes", scopes);
        if (expUnix is { } exp)
            request.Headers.Add("X-Exp", exp.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var bytes = Encoding.UTF8.GetBytes(body);
        request.Content = omitContentLength
            ? new StreamContent(new NonSeekableStream(bytes))
            : new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return request;
    }

    private static string ValidRequestBody() => """
        {
          "region": "eastus",
          "modelId": "gpt-4o",
          "input": { "meterId": "meter-in", "expectedUsdPer1M": 2.5 },
          "output": { "meterId": "meter-out", "expectedUsdPer1M": 10 }
        }
        """;

    private static HttpResponseMessage NoContent() =>
        new(HttpStatusCode.NoContent)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

    private static HttpResponseMessage JsonOk(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static string AzureEnvelope(IEnumerable<JsonObject> rows, string? nextPage = null)
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

    private static JsonObject AzureRow(
        string meterId,
        string region,
        string unit,
        decimal price,
        decimal tier) =>
        new()
        {
            ["meterId"] = meterId,
            ["meterName"] = meterId,
            ["productName"] = "Foundry Models",
            ["skuName"] = "Standard",
            ["unitOfMeasure"] = unit,
            ["currencyCode"] = "USD",
            ["armRegionName"] = region,
            ["serviceName"] = "Foundry Models",
            ["type"] = "Consumption",
            ["retailPrice"] = JsonValue.Create(price),
            ["tierMinimumUnits"] = JsonValue.Create(tier),
        };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class AsyncDelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return respond(request, cancellationToken);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.TryGetValue("X-Subject", out var ownerSubject))
            {
                var ownerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", ownerSubject.ToString())],
                    "TestAuth"));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(ownerPrincipal, "TestAuth")));
            }

            if (!Request.Headers.TryGetValue("Authorization", out var auth) ||
                string.IsNullOrWhiteSpace(auth.ToString()))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new("sub", ActorId),
                new("aud", EventsAudience),
                new("aud", ProjectsAudience),
            };
            if (Request.Headers.TryGetValue("X-Scopes", out var scopes) && !string.IsNullOrWhiteSpace(scopes))
                claims.Add(new Claim("scope", scopes.ToString()));
            if (Request.Headers.TryGetValue("X-Exp", out var exp) && !string.IsNullOrWhiteSpace(exp))
                claims.Add(new Claim("exp", exp.ToString()));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestAuth")));
        }
    }
}
