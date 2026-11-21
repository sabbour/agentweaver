using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class RuntimeRateCardManualWriteEndpointTests : IAsyncLifetime
{
    private const string ProjectsAudience = "agentweaver.projects";
    private const string EventsAudience = "events";
    private const string ActorId = "33333333-3333-3333-3333-333333333333";

    private readonly SessionsPostgresFixture _fixture;
    private readonly string _schema = "manual_rates_" + Guid.NewGuid().ToString("N");
    private RuntimeRateCardStore _store = null!;

    public RuntimeRateCardManualWriteEndpointTests(SessionsPostgresFixture fixture) => _fixture = fixture;

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
    public async Task CreatesReplaysAndRejectsConflictsWithCanonicalBodyAndStrongEtag()
    {
        var projectsCalls = 0;
        using var host = await StartTestHostAsync(request =>
        {
            projectsCalls++;
            Assert.Equal("/api/authorization/platform/model-pricing-rate-cards", request.RequestUri!.PathAndQuery);
            Assert.Equal("tenant-1", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            return NoContent();
        });
        using var client = host.GetTestClient();

        const string id = "manual-azure.2026";
        const string version = "v1_0-rev1";
        var path = $"/api/platform/model-pricing/rate-cards/{id}/{version}";
        var nonCanonicalBody = $$"""
            {
              "provenance": {
                "retrievedOn": "2026-11-21",
                "sourceUrl": "https://example.com/prices",
                "source": "manual"
              },
              "rateCard": {
                "modelMultipliers": {
                  "gpt-4o|output": 10.0,
                  "gpt-4o|input": 2.50
                },
                "nanoUnitsPerUnit": 1000000,
                "unit": "USD",
                "meterSource": "byok.tokens",
                "version": "{{version}}",
                "id": "{{id}}"
              },
              "runtimeRateCardSchemaVersion": 1
            }
            """;

        string expectedCanonical;
        string expectedEtag;
        using (var createRequest = CreatePutRequest(path, nonCanonicalBody))
        using (var created = await client.SendAsync(createRequest))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
            expectedCanonical = await created.Content.ReadAsStringAsync();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expectedCanonical)))
                .ToLowerInvariant();
            expectedEtag = $"\"{hash}\"";
            Assert.Equal(expectedEtag, created.Headers.ETag?.ToString());
            Assert.False(created.Headers.ETag!.IsWeak);
            Assert.Equal(1, projectsCalls);
        }

        // Exact canonical replay (different formatting, identical content) returns 200 and same ETag.
        using (var replayRequest = CreatePutRequest(path, expectedCanonical))
        using (var replayed = await client.SendAsync(replayRequest))
        {
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            Assert.Equal("no-store", replayed.Headers.CacheControl?.ToString());
            Assert.Equal(expectedEtag, replayed.Headers.ETag?.ToString());
            Assert.Equal(expectedCanonical, await replayed.Content.ReadAsStringAsync());
        }

        // Different price at the same key returns 409.
        var conflictingBody = expectedCanonical.Replace("\"gpt-4o|output\":10", "\"gpt-4o|output\":11", StringComparison.Ordinal);
        using (var conflictRequest = CreatePutRequest(path, conflictingBody))
        using (var conflict = await client.SendAsync(conflictRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("no-store", conflict.Headers.CacheControl?.ToString());
        }

        // Existing owner read returns the exact canonical bytes and matching ETag.
        var readPath = $"{RuntimeRateCardOwnerReadEndpoint.Route}?id={id}&version={version}";
        using (var readRequest = new HttpRequestMessage(HttpMethod.Get, readPath))
        {
            readRequest.Headers.Add("X-Subject", "projects-workload");
            using var readResponse = await client.SendAsync(readRequest);
            Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
            Assert.Equal(expectedEtag, readResponse.Headers.ETag?.ToString());
            var readBody = await readResponse.Content.ReadAsStringAsync();
            Assert.Equal(expectedCanonical, readBody);

            var readCard = RuntimeRateCardReadContract.Deserialize(Encoding.UTF8.GetBytes(readBody));
            Assert.NotNull(readCard);
            var pin = RuntimeRateCardReadContract.CreatePin(readCard);
            Assert.Equal(id, pin.RateCardId);
            Assert.Equal(version, pin.RateCardVersion);
            Assert.Equal(expectedEtag, $"\"{pin.ContentHash}\"");
        }
    }

    [Fact]
    public async Task LegacyRowAtSameKeyReturnsConflictWithoutOverwriting()
    {
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".usage_rate_cards (
                card_id, version, meter_source, unit, nano_units_per_unit,
                model_multipliers, canonical_input, payload)
            VALUES (
                'legacy-card', 'v1', 'byok-tokens', 'USD', 1000000,
                @multipliers,
                @canonical,
                @payload)
            """, connection))
        {
            insert.Parameters.AddWithValue("multipliers", NpgsqlDbType.Jsonb, """{"gpt-4o|input":1,"gpt-4o|output":2}""");
            insert.Parameters.AddWithValue("canonical", NpgsqlDbType.Text, """{"id":"legacy-card","version":"v1"}""");
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, """{"id":"legacy-card","version":"v1"}""");
            await insert.ExecuteNonQueryAsync();
        }

        using var host = await StartTestHostAsync(_ => NoContent());
        using var client = host.GetTestClient();
        var body = ValidBody("legacy-card", "v1");
        using var request = CreatePutRequest("/api/platform/model-pricing/rate-cards/legacy-card/v1", body);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("api.read", null, null, null, HttpStatusCode.Forbidden, false)]
    [InlineData("projects.admin", null, null, null, HttpStatusCode.Forbidden, false)]
    [InlineData("api.read projects.admin", "project-1", null, null, HttpStatusCode.Forbidden, false)]
    [InlineData("api.read projects.admin", null, "run-1", null, HttpStatusCode.Forbidden, false)]
    [InlineData("api.read projects.admin", null, null, "credential-publication", HttpStatusCode.Forbidden, false)]
    public async Task EnforcesUnboundPlatformAdminScopesBeforeCallingProjectsOrReadingBody(
        string scopes,
        string? projectId,
        string? runId,
        string? purpose,
        HttpStatusCode expectedStatus,
        bool expectProjectsCall)
    {
        var projectsCalls = 0;
        using var host = await StartTestHostAsync(_ =>
        {
            projectsCalls++;
            return NoContent();
        });
        using var client = host.GetTestClient();
        using var request = CreatePutRequest(
            "/api/platform/model-pricing/rate-cards/card-auth/v1",
            ValidBody("card-auth", "v1"),
            scopes: scopes,
            projectId: projectId,
            runId: runId,
            purpose: purpose);

        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(expectProjectsCall ? 1 : 0, projectsCalls);
        Assert.Null(await _store.GetAsync("card-auth", "v1"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true, HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NoContent, false, HttpStatusCode.BadGateway)]
    public async Task FailsClosedWhenProjectsDeniesIsUnavailableOrOmitsNoStore(
        HttpStatusCode projectsStatus,
        bool includeNoStore,
        HttpStatusCode expectedStatus)
    {
        using var host = await StartTestHostAsync(_ =>
        {
            var response = new HttpResponseMessage(projectsStatus);
            if (includeNoStore)
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return response;
        });
        using var client = host.GetTestClient();
        using var request = CreatePutRequest(
            "/api/platform/model-pricing/rate-cards/card-fail/v1",
            ValidBody("card-fail", "v1"));

        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Null(await _store.GetAsync("card-fail", "v1"));
    }

    [Fact]
    public async Task ProjectsDenialReturns403BeforeReadingRequestBody()
    {
        using var host = await StartTestHostAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        });
        var server = host.GetTestServer();
        var bodyRead = false;
        var context = await server.SendAsync(http =>
        {
            http.Request.Method = HttpMethods.Put;
            http.Request.Path = "/api/platform/model-pricing/rate-cards/card-auth-body/v1";
            http.Request.Headers.Authorization = "Bearer caller-token";
            http.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
            http.Request.Headers["X-Scopes"] = "api.read projects.admin";
            http.Request.ContentType = "application/json";
            http.Request.Body = new ObserveReadStream(
                Encoding.UTF8.GetBytes(ValidBody("card-auth-body", "v1")),
                () => bodyRead = true);
        });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(bodyRead);
    }

    [Theory]
    [InlineData("/api/platform/model-pricing/rate-cards/-card/v1")]
    [InlineData("/api/platform/model-pricing/rate-cards/card%2F1/v1")]
    [InlineData("/api/platform/model-pricing/rate-cards/card-1/v1?extra=1")]
    public async Task RejectsInvalidKeysEncodedAliasesAndQueryParametersBeforeProjects(string path)
    {
        var projectsCalls = 0;
        using var host = await StartTestHostAsync(_ =>
        {
            projectsCalls++;
            return NoContent();
        });
        using var client = host.GetTestClient();
        using var request = CreatePutRequest(path, ValidBody("card-1", "v1"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, projectsCalls);
    }

    [Fact]
    public async Task RejectsPercentEncodedUnreservedAliasAndDotSegmentsInRawTargetBeforeProjects()
    {
        var projectsCalls = 0;
        using var host = await StartTestHostAsync(_ =>
        {
            projectsCalls++;
            return NoContent();
        });
        var server = host.GetTestServer();
        foreach (var (path, rawTarget) in new[]
        {
            ("/api/platform/model-pricing/rate-cards/card-1/v1", "/api/platform/model-pricing/rate-cards/card%2D1/v1"),
            ("/api/platform/model-pricing/rate-cards/./v1", "/api/platform/model-pricing/rate-cards/./v1"),
            ("/api/platform/model-pricing/rate-cards/../v1", "/api/platform/model-pricing/rate-cards/../v1"),
        })
        {
            var context = await server.SendAsync(http =>
            {
                http.Request.Method = HttpMethods.Put;
                http.Request.Path = path;
                http.Request.Headers.Authorization = "Bearer caller-token";
                http.Request.Headers["X-Scopes"] = "api.read projects.admin";
                http.Request.ContentType = "application/json";
                var raw = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!;
                raw.RawTarget = rawTarget;
            });
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        }
        Assert.Equal(0, projectsCalls);
    }

    [Fact]
    public async Task RejectsOversizedDeclaredAndStreamedBodiesAndUnsupportedMediaTypesWithoutSaving()
    {
        using var host = await StartTestHostAsync(_ => NoContent());
        using var client = host.GetTestClient();

        // Unsupported media type -> 415
        using (var wrongType = CreatePutRequest(
            "/api/platform/model-pricing/rate-cards/card-media/v1",
            ValidBody("card-media", "v1"),
            contentType: "text/plain"))
        using (var response = await client.SendAsync(wrongType))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Null(await _store.GetAsync("card-media", "v1"));
        }

        // Declared Content-Length > 64 KiB -> 413
        var oversized = new string(' ', 64 * 1024 + 1);
        using (var declaredLarge = CreatePutRequest(
            "/api/platform/model-pricing/rate-cards/card-big1/v1",
            oversized))
        using (var response = await client.SendAsync(declaredLarge))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Null(await _store.GetAsync("card-big1", "v1"));
        }

        // Streamed body > 64 KiB without Content-Length -> 413
        using (var streamedLarge = CreatePutRequest(
            "/api/platform/model-pricing/rate-cards/card-big2/v1",
            oversized,
            omitContentLength: true))
        using (var response = await client.SendAsync(streamedLarge))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Null(await _store.GetAsync("card-big2", "v1"));
        }
    }

    [Theory]
    [InlineData("""{"runtimeRateCardSchemaVersion":1,"rateCard":{"id":"other","version":"v1","meterSource":"byok.tokens","unit":"USD","nanoUnitsPerUnit":1000000,"modelMultipliers":{"gpt-4o|input":1,"gpt-4o|output":2}},"provenance":{"source":"manual","sourceUrl":"https://example.com/p","retrievedOn":"2026-11-21"}}""")]
    [InlineData("""{"runtimeRateCardSchemaVersion":1,"rateCard":{"id":"card-bad","version":"v1","meterSource":"byok.tokens","unit":"USD","nanoUnitsPerUnit":1000000,"modelMultipliers":{"gpt-4o|input":1,"gpt-4o|output":2}},"provenance":{"source":"manual","sourceUrl":"http://example.com/p","retrievedOn":"2026-11-21"}}""")]
    [InlineData("""{"runtimeRateCardSchemaVersion":1,"rateCard":{"id":"card-bad","version":"v1","meterSource":"byok.tokens","unit":"USD","nanoUnitsPerUnit":1000000,"modelMultipliers":{"gpt-4o|input":1,"gpt-4o|output":2}},"provenance":{"source":"manual","sourceUrl":"https://user:pass@example.com/p","retrievedOn":"2026-11-21"}}""")]
    [InlineData("""{"runtimeRateCardSchemaVersion":1,"rateCard":{"id":"card-bad","version":"v1","meterSource":"byok.tokens","unit":"USD","nanoUnitsPerUnit":1000000,"modelMultipliers":{"gpt-4o|input":0,"gpt-4o|output":2}},"provenance":{"source":"manual","sourceUrl":"https://example.com/p","retrievedOn":"2026-11-21"}}""")]
    [InlineData("""{"runtimeRateCardSchemaVersion":1,"rateCard":{"id":"azure-retail-prices.standard.eastus","version":"v1","meterSource":"byok.tokens","unit":"USD","nanoUnitsPerUnit":1000000,"modelMultipliers":{"gpt-4o|input":1,"gpt-4o|output":2}},"provenance":{"source":"azure-retail-prices","region":"eastus","meterIds":["m1"]}}""")]
    public async Task RejectsInvalidBodyProvenanceUrlsAndPricesWithBadRequest(string body)
    {
        using var host = await StartTestHostAsync(_ => NoContent());
        using var client = host.GetTestClient();
        using var request = CreatePutRequest("/api/platform/model-pricing/rate-cards/card-bad/v1", body);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await _store.GetAsync("card-bad", "v1"));
    }

    private async Task<IHost> StartTestHostAsync(Func<HttpRequestMessage, HttpResponseMessage> projectsRespond)
    {
        var projectsClient = new ProjectsAuthorizationContextClient(
            new HttpClient(new DelegateHandler(projectsRespond)),
            new ProjectsAuthorizationContextOptions(
                "https://projects.internal/", ProjectsAudience, "https://identity.example"));
        var authentication = new PlatformComponentServiceAuthenticationOptions(
            new Uri("https://issuer.example/"),
            new Uri("https://events-private.example/"),
            "projects-workload");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Identity:Audience"] = EventsAudience }).Build();

        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication("TestAuth")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
                    services.AddAuthorization(options => options.AddPolicy(
                        PlatformComponentServiceAuthenticationOptions.PolicyName,
                        policy => policy
                            .AddAuthenticationSchemes("TestAuth")
                            .RequireAuthenticatedUser()
                            .RequireClaim("sub", authentication.CallerSubject)));
                    services.AddSingleton(_store);
                    services.AddSingleton(projectsClient);
                    services.AddSingleton<IConfiguration>(configuration);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapRuntimeRateCardOwnerRead(authentication);
                        endpoints.MapRuntimeRateCardManualWriteEndpoints();
                    });
                });
            })
            .StartAsync();
    }

    private static HttpRequestMessage CreatePutRequest(
        string path,
        string body,
        string scopes = "api.read projects.admin",
        string? projectId = null,
        string? runId = null,
        string? purpose = null,
        string contentType = "application/json",
        bool omitContentLength = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        request.Headers.Add("X-Scopes", scopes);
        if (projectId is not null)
            request.Headers.Add("X-Project-Id", projectId);
        if (runId is not null)
            request.Headers.Add("X-Run-Id", runId);
        if (purpose is not null)
            request.Headers.Add("X-Purpose", purpose);

        var bytes = Encoding.UTF8.GetBytes(body);
        if (omitContentLength)
        {
            request.Content = new StreamContent(new NonSeekableStream(bytes));
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        else
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        return request;
    }

    private static string ValidBody(string id, string version) => $$"""
        {
          "runtimeRateCardSchemaVersion": 1,
          "rateCard": {
            "id": "{{id}}",
            "version": "{{version}}",
            "meterSource": "byok.tokens",
            "unit": "USD",
            "nanoUnitsPerUnit": 1000000,
            "modelMultipliers": {
              "gpt-4o|input": 2.5,
              "gpt-4o|output": 10
            }
          },
          "provenance": {
            "source": "manual",
            "sourceUrl": "https://example.com/prices",
            "retrievedOn": "2026-11-21"
          }
        }
        """;

    private static HttpResponseMessage NoContent() =>
        new(HttpStatusCode.NoContent)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

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

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private sealed class ObserveReadStream(byte[] data, Action onRead) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
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
            if (Request.Headers.TryGetValue("X-Project-Id", out var projectId))
                claims.Add(new Claim("project_id", projectId.ToString()));
            if (Request.Headers.TryGetValue("X-Run-Id", out var runId))
                claims.Add(new Claim("run_id", runId.ToString()));
            if (Request.Headers.TryGetValue("X-Purpose", out var purpose))
                claims.Add(new Claim("purpose", purpose.ToString()));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "TestAuth")));
        }
    }
}
