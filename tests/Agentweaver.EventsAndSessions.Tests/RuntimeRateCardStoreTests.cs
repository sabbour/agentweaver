using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class RuntimeRateCardStoreTests : IAsyncLifetime
{
    private readonly SessionsPostgresFixture _fixture;
    private readonly string _schema = "runtime_rates_" + Guid.NewGuid().ToString("N");
    private RuntimeRateCardStore _store = null!;

    public RuntimeRateCardStoreTests(SessionsPostgresFixture fixture) => _fixture = fixture;

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
    public async Task StoresManualAndRetailProvenanceAndReadsAfterDataSourceRestart()
    {
        var manual = ManualCard();
        var retail = RetailCard();
        Assert.True(await _store.SaveAsync(manual));
        Assert.True(await _store.SaveAsync(retail));

        await using var restartedDataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        var restartedStore = new RuntimeRateCardStore(restartedDataSource, _schema);
        AssertRuntimeRateCard(manual, await restartedStore.GetAsync(manual.RateCard.Id, manual.RateCard.Version));
        AssertRuntimeRateCard(retail, await restartedStore.GetAsync(retail.RateCard.Id, retail.RateCard.Version));
        Assert.Null(await restartedStore.GetAsync("missing-card", "v1"));
    }

    [Fact]
    public async Task OwnerReadRequiresProjectsIdentityAndReturnsCanonicalCardWithStrongEtag()
    {
        var card = ManualCard(id: "owner-read-card");
        await _store.SaveAsync(card);
        var authentication = new PlatformComponentServiceAuthenticationOptions(
            new Uri("https://issuer.example/"),
            new Uri("https://events-private.example/"),
            "projects-workload");
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(OwnerReadAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, OwnerReadAuthenticationHandler>(
                            OwnerReadAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization(options => options.AddPolicy(
                        PlatformComponentServiceAuthenticationOptions.PolicyName,
                        policy => policy
                            .AddAuthenticationSchemes(OwnerReadAuthenticationHandler.SchemeName)
                            .RequireAuthenticatedUser()
                            .RequireClaim("sub", authentication.CallerSubject)));
                    services.AddSingleton(_store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapRuntimeRateCardOwnerRead(authentication));
                });
            })
            .StartAsync();
        using var client = host.GetTestClient();
        var path = $"{RuntimeRateCardOwnerReadEndpoint.Route.Replace("{id}/{version}", $"{card.RateCard.Id}/{card.RateCard.Version}", StringComparison.Ordinal)}";

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.Add("X-Subject", "identity-broker");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.Add("X-Subject", "projects-workload");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(
                $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(RuntimeRateCardContract.SerializeCanonical(card))))
                    .ToLowerInvariant()}\"",
                response.Headers.ETag?.ToString());
            Assert.Equal(RuntimeRateCardContract.SerializeCanonical(card), await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task SameIdAndVersionAcceptsExactRetryAndRejectsChangedCardOrProvenance()
    {
        var card = ManualCard();

        Assert.True(await _store.SaveAsync(card));
        Assert.False(await _store.SaveAsync(card));

        var changedRates = card with
        {
            RateCard = card.RateCard with
            {
                ModelMultipliers = Rates(inputRate: 1.26m)
            }
        };
        await Assert.ThrowsAsync<RuntimeRateCardConflictException>(() => _store.SaveAsync(changedRates));

        var changedProvenance = card with
        {
            Provenance = new ManualRateCardProvenance("https://example.com/other-prices", new DateOnly(2026, 11, 10))
        };
        await Assert.ThrowsAsync<RuntimeRateCardConflictException>(() => _store.SaveAsync(changedProvenance));
    }

    [Fact]
    public async Task ConcurrentDuplicateAndConflictingWritesAreSerializedByTheDatabaseKey()
    {
        var duplicate = ManualCard(id: "concurrent-duplicate");
        var duplicateResults = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => _store.SaveAsync(duplicate)));
        Assert.Single(duplicateResults, inserted => inserted);
        Assert.Equal(7, duplicateResults.Count(inserted => !inserted));

        var first = ManualCard(id: "concurrent-conflict", inputRate: 1m);
        var second = ManualCard(id: "concurrent-conflict", inputRate: 2m);
        var conflictResults = await Task.WhenAll(
            TrySaveAsync(first),
            TrySaveAsync(second));
        Assert.Single(conflictResults, result => result == SaveResult.Inserted);
        Assert.Single(conflictResults, result => result == SaveResult.Conflict);
    }

    [Fact]
    public void RejectsInvalidUnitsAndSourceProvenance()
    {
        var manual = ManualCard();
        Assert.Throws<ArgumentException>(() => RuntimeRateCardContract.Validate(manual with
        {
            RateCard = manual.RateCard with { Unit = "tokens" }
        }));
        Assert.Throws<ArgumentException>(() => RuntimeRateCardContract.Validate(manual with
        {
            Provenance = new ManualRateCardProvenance("http://example.com/prices", new DateOnly(2026, 11, 10))
        }));
        Assert.Throws<ArgumentException>(() => RuntimeRateCardContract.Validate(manual with
        {
            Provenance = new ManualRateCardProvenance("https://example.com/prices", default)
        }));
        Assert.Throws<ArgumentException>(() => RuntimeRateCardContract.Validate(manual with
        {
            RateCard = manual.RateCard with
            {
                ModelMultipliers = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
                    [KeyValuePair.Create("gpt-4.1|input", 1.25m)])
            }
        }));

        var retail = RetailCard();
        Assert.Throws<ArgumentException>(() => RuntimeRateCardContract.Validate(retail with
        {
            Provenance = new AzureRetailRateCardProvenance("westus", ["meter-1"])
        }));
        Assert.Throws<ArgumentException>(() => RuntimeRateCardContract.Validate(retail with
        {
            Provenance = new AzureRetailRateCardProvenance("eastus2", [])
        }));
    }

    [Fact]
    public async Task RejectsCardIdentityThatCannotBeRead()
    {
        var card = ManualCard();
        var invalidId = card with
        {
            RateCard = card.RateCard with { Id = "manual/azure" }
        };
        var invalidVersion = card with
        {
            RateCard = card.RateCard with { Version = "2026/11/10" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(invalidId));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveAsync(invalidVersion));
    }

    [Fact]
    public async Task DoesNotPromoteLegacyUsageRateCardsWithoutRuntimeProvenance()
    {
        var runtimeCard = ManualCard(id: "legacy-card");
        var card = runtimeCard.RateCard;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".usage_rate_cards (
                card_id, version, meter_source, unit, nano_units_per_unit,
                model_multipliers, canonical_input, payload)
            VALUES (
                @card_id, @version, @meter_source, @unit, @nano_units_per_unit,
                @model_multipliers, @canonical_input, @payload)
            """, connection))
        {
            insert.Parameters.AddWithValue("card_id", NpgsqlDbType.Varchar, card.Id);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, card.Version);
            insert.Parameters.AddWithValue("meter_source", NpgsqlDbType.Varchar, card.MeterSource);
            insert.Parameters.AddWithValue("unit", NpgsqlDbType.Varchar, card.Unit);
            insert.Parameters.AddWithValue("nano_units_per_unit", NpgsqlDbType.Numeric, card.NanoUnitsPerUnit);
            insert.Parameters.AddWithValue(
                "model_multipliers", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(card.ModelMultipliers, options));
            insert.Parameters.AddWithValue(
                "canonical_input", NpgsqlDbType.Text, CostRateCardContract.SerializeCanonical(card));
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(card, options));
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Null(await _store.GetAsync(card.Id, card.Version));
        await Assert.ThrowsAsync<RuntimeRateCardConflictException>(() => _store.SaveAsync(runtimeCard));
    }

    private async Task<SaveResult> TrySaveAsync(RuntimeRateCard runtimeRateCard)
    {
        try
        {
            return await _store.SaveAsync(runtimeRateCard) ? SaveResult.Inserted : SaveResult.Duplicate;
        }
        catch (RuntimeRateCardConflictException)
        {
            return SaveResult.Conflict;
        }
    }

    private static RuntimeRateCard ManualCard(
        string id = "manual-azure-card",
        decimal inputRate = 1.25m) =>
        new(
            new CostRateCard(
                id,
                "2026-11-10",
                AzureCostProvider.MeterSource,
                "USD",
                AzureCostProviderOptions.TokensPerRateUnit,
                Rates(inputRate)),
            new ManualRateCardProvenance("https://example.com/azure-prices", new DateOnly(2026, 11, 10)));

    private static RuntimeRateCard RetailCard() =>
        new(
            new CostRateCard(
                "azure-retail-prices.standard.eastus2",
                "2026-11-10",
                AzureCostProvider.MeterSource,
                "USD",
                AzureCostProviderOptions.TokensPerRateUnit,
                Rates()),
            new AzureRetailRateCardProvenance("eastus2", ["meter-1", "meter-2"]));

    private static ImmutableDictionary<string, decimal> Rates(decimal inputRate = 1.25m) =>
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            KeyValuePair.Create("gpt-4.1|input", inputRate),
            KeyValuePair.Create("gpt-4.1|output", 5.75m)
        });

    private static void AssertRuntimeRateCard(RuntimeRateCard expected, RuntimeRateCard? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(
            CostRateCardContract.SerializeCanonical(expected.RateCard),
            CostRateCardContract.SerializeCanonical(actual.RateCard));
        switch (expected.Provenance)
        {
            case ManualRateCardProvenance expectedManual:
                var actualManual = Assert.IsType<ManualRateCardProvenance>(actual.Provenance);
                Assert.Equal(expectedManual.SourceUrl, actualManual.SourceUrl);
                Assert.Equal(expectedManual.RetrievedOn, actualManual.RetrievedOn);
                break;
            case AzureRetailRateCardProvenance expectedRetail:
                var actualRetail = Assert.IsType<AzureRetailRateCardProvenance>(actual.Provenance);
                Assert.Equal(expectedRetail.Region, actualRetail.Region);
                Assert.Equal(expectedRetail.MeterIds.ToArray(), actualRetail.MeterIds.ToArray());
                break;
        }
    }

    private sealed class OwnerReadAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string SchemeName = "RuntimeRateCardOwnerReadTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Subject"].ToString();
            if (string.IsNullOrEmpty(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", subject)], SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }

    private enum SaveResult
    {
        Inserted,
        Duplicate,
        Conflict
    }
}
