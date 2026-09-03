using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Tests.Auth;

public sealed class OpenIddictAuthorizationServerTests : IClassFixture<OpenIddictServerFixture>
{
    private readonly HttpClient _client;
    private readonly AgentweaverWebApplicationFactory _factory;

    public OpenIddictAuthorizationServerTests(OpenIddictServerFixture fixture)
    {
        _client = fixture.Client;
        _factory = fixture.Factory;
    }

    [Fact]
    public async Task Metadata_UsesCanonicalConfiguredEndpointsAndCapabilities()
    {
        using var response = await _client.GetAsync("/.well-known/oauth-authorization-server");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var metadata = document.RootElement;
        metadata.GetProperty("issuer").GetString().Should().Be("http://localhost:5000/");
        metadata.GetProperty("authorization_endpoint").GetString().Should().Be("http://localhost:5000/oauth/authorize");
        metadata.GetProperty("token_endpoint").GetString().Should().Be("http://localhost:5000/oauth/token");
        metadata.GetProperty("revocation_endpoint").GetString().Should().Be("http://localhost:5000/oauth/revoke");
        metadata.GetProperty("jwks_uri").GetString().Should().Be("http://localhost:5000/oauth/jwks");
        metadata.GetProperty("registration_endpoint").GetString().Should().Be("http://localhost:5000/oauth/register");
        metadata.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(x => x.GetString()).Should().Equal("S256");
        metadata.GetProperty("grant_types_supported").EnumerateArray()
            .Select(x => x.GetString()).Should().BeEquivalentTo("authorization_code", "refresh_token");
        metadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray()
            .Select(x => x.GetString()).Should().Equal("none");
    }

    [Fact]
    public async Task DynamicRegistration_AcceptsNarrowPublicClientWithoutSecret()
    {
        using var response = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Copilot CLI test",
            redirect_uris = new[] { "http://127.0.0.1:49152/callback" },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            scope = "mcp:invoke offline_access",
        });
        response.StatusCode.Should().Be(
            HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("client_id").GetString().Should().StartWith("aw_native_");
        document.RootElement.TryGetProperty("client_secret", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost:49152/callback")]
    [InlineData("https://example.com/*")]
    [InlineData("http://10.0.0.7/callback")]
    public async Task DynamicRegistration_RejectsUnsafeRedirects(string redirect)
    {
        using var response = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Unsafe client",
            redirect_uris = new[] { redirect },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Authorization_RejectsMissingPkceBeforeBrokerLogin()
    {
        using var registration = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "PKCE test",
            redirect_uris = new[] { "http://127.0.0.1:49153/callback" },
        });
        var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("client_id").GetString();
        using var response = await _client.GetAsync(
            $"/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:49153/callback")}" +
            "&response_type=code&scope=mcp%3Ainvoke");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var validTail = $"client_id={clientId}&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:49153/callback")}" +
            "&response_type=code&scope=mcp%3Ainvoke&code_challenge=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "&code_challenge_method=S256";
        using var wrongResource = await _client.GetAsync(
            $"/oauth/authorize?{validTail}&resource={Uri.EscapeDataString("https://other.example/mcp")}");
        wrongResource.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var hostileHost = await _client.GetAsync(
            $"http://evil.example/oauth/authorize?{validTail}&resource={Uri.EscapeDataString("http://localhost:5000/mcp")}");
        hostileHost.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AuthorizationCodeAndRefresh_AreBoundSingleUseAndRevokeFamilyOnReplay()
    {
        const string redirectUri = "http://127.0.0.1:49154/callback";
        using var registration = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Lifecycle test",
            redirect_uris = new[] { redirectUri },
            scope = "mcp:invoke offline_access",
        });
        var clientId = (await registration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("client_id").GetString()!;

        var sessionId = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.BrowserEntraSessions.Add(new BrowserEntraSession
            {
                Id = sessionId,
                EntraObjectId = "00000000-0000-0000-0000-000000000123",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizeParameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp:invoke offline_access",
            ["state"] = "client-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = "http://localhost:5000/mcp",
        };
        using var authorize = new HttpRequestMessage(
            HttpMethod.Get, "/oauth/authorize" + QueryString.Create(
                authorizeParameters.Select(x => KeyValuePair.Create(x.Key, (string?)x.Value))));
        authorize.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");
        using var consent = await _client.SendAsync(authorize);
        consent.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await consent.Content.ReadAsStringAsync();
        var consentHandle = Regex.Match(html, "name=\"consent_handle\" value=\"([^\"]+)\"").Groups[1].Value;
        consentHandle.Should().NotBeNullOrWhiteSpace();

        var form = new Dictionary<string, string>(authorizeParameters)
        {
            ["consent_handle"] = consentHandle,
            ["decision"] = "approve",
        };
        using var approval = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
        {
            Content = new FormUrlEncodedContent(form),
        };
        approval.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");
        using var approved = await _client.SendAsync(approval);
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var callback = approved.Headers.Location!;
        var code = ParseQuery(callback.Query)["code"];
        ParseQuery(callback.Query)["state"].Should().Be("client-state");

        async Task<JsonElement> RedeemAsync(Dictionary<string, string> values)
        {
            using var response = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(values));
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var token = await RedeemAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        });
        token.GetProperty("access_token").GetString()!.Count(c => c == '.').Should().Be(2);
        var refreshToken = token.GetProperty("refresh_token").GetString()!;

        var rotated = await RedeemAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        });
        var rotatedRefreshToken = rotated.GetProperty("refresh_token").GetString()!;
        rotatedRefreshToken.Should().NotBe(refreshToken);

        using var codeReplay = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }));
        codeReplay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var replay = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        }));
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var familyUse = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = rotatedRefreshToken,
        }));
        familyUse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]));
}

public sealed class OpenIddictServerFixture : IDisposable
{
    public AgentweaverWebApplicationFactory Factory { get; } = new();
    public HttpClient Client { get; }

    public OpenIddictServerFixture() => Client = Factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://localhost:5000"),
        });

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }
}
