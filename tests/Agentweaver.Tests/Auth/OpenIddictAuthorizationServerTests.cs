using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

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

        using var oidcResponse = await _client.GetAsync("/.well-known/openid-configuration");
        oidcResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var oidcDocument = JsonDocument.Parse(await oidcResponse.Content.ReadAsStringAsync());
        oidcDocument.RootElement.GetProperty("issuer").GetString().Should().Be(
            metadata.GetProperty("issuer").GetString());
        oidcDocument.RootElement.GetProperty("jwks_uri").GetString().Should().Be(
            metadata.GetProperty("jwks_uri").GetString());

        using var jwksResponse = await _client.GetAsync("/oauth/jwks");
        jwksResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var jwksDocument = JsonDocument.Parse(await jwksResponse.Content.ReadAsStringAsync());
        jwksDocument.RootElement.GetProperty("keys").EnumerateArray().Should().Contain(key =>
            key.GetProperty("use").GetString() == "sig"
            && key.GetProperty("alg").GetString() == SecurityAlgorithms.RsaSha256
            && !string.IsNullOrWhiteSpace(key.GetProperty("kid").GetString()));
    }

    [Fact]
    public async Task DynamicRegistration_AcceptsNarrowPublicClientWithoutSecret()
    {
        using var response = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Copilot CLI test",
            redirect_uris = new[]
            {
                "http://127.0.0.1:49152/callback",
                "com.github.copilot:/oauth/callback",
            },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            scope = "mcp:invoke offline_access",
        });
        response.StatusCode.Should().Be(
            HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var clientId = document.RootElement.GetProperty("client_id").GetString()!;
        var advertisedExpiration = document.RootElement.GetProperty("client_id_expires_at").GetInt64();
        clientId.Should().StartWith("aw_native_");
        advertisedExpiration.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        document.RootElement.TryGetProperty("client_secret", out _).Should().BeFalse();

        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(clientId);
        application.Should().NotBeNull();
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application!);
        OAuthDynamicClientExpiration.TryGetExpiration(descriptor, out var persistedExpiration)
            .Should().BeTrue();
        persistedExpiration.ToUnixTimeSeconds().Should().Be(advertisedExpiration,
            "the durable application metadata must retain the exact advertised expiration");
    }

    [Fact]
    public async Task ClaudeHostedClient_IsReconciledAsPublicPkceClientWithExactRedirect()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(OAuthKnownClients.ClaudeHostedClientId);
        application.Should().NotBeNull();

        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application!);
        descriptor.ClientType.Should().Be(OpenIddictConstants.ClientTypes.Public);
        descriptor.ClientSecret.Should().BeNull();
        descriptor.RedirectUris.Select(uri => uri.AbsoluteUri)
            .Should().Equal(OAuthKnownClients.ClaudeHostedRedirectUri);
        descriptor.Requirements.Should().Contain(OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange);
        descriptor.Properties["agentweaver_static"].GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ClaudeHostedClient_AuthorizationAcceptsOnlyExactRedirectWithPkce()
    {
        var exactQuery = ClaudeAuthorizationQuery(OAuthKnownClients.ClaudeHostedRedirectUri);
        using var accepted = await _client.GetAsync("/oauth/authorize" + exactQuery);
        accepted.StatusCode.Should().Be(HttpStatusCode.Redirect);
        accepted.Headers.Location!.OriginalString.Should().StartWith("/auth/entra/authorize?");

        foreach (var redirectUri in new[]
                 {
                     "https://claude.ai/api/mcp/auth_callback/",
                     "https://claude.ai/api/mcp/auth_callback?next=%2Fmcp",
                     "https://claude.ai.evil.example/api/mcp/auth_callback",
                 })
        {
            using var rejected = await _client.GetAsync(
                "/oauth/authorize" + ClaudeAuthorizationQuery(redirectUri));
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var missingPkce = QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = OAuthKnownClients.ClaudeHostedClientId,
            ["redirect_uri"] = OAuthKnownClients.ClaudeHostedRedirectUri,
            ["response_type"] = "code",
            ["scope"] = OAuthServerConfiguration.McpScope,
            ["resource"] = "http://localhost:5000/mcp",
        });
        using var rejectedWithoutPkce = await _client.GetAsync("/oauth/authorize" + missingPkce);
        rejectedWithoutPkce.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ClaudeHostedClient_RedeemsAndRefreshesWithoutClientSecret()
    {
        var (code, verifier) = await IssueAuthorizationCodeAsync(
            OAuthKnownClients.ClaudeHostedClientId,
            OAuthKnownClients.ClaudeHostedRedirectUri,
            "mcp:invoke offline_access");

        using var tokenResponse = await _client.PostAsync(
            "/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = OAuthKnownClients.ClaudeHostedClientId,
                ["code"] = code,
                ["redirect_uri"] = OAuthKnownClients.ClaudeHostedRedirectUri,
                ["code_verifier"] = verifier,
                ["resource"] = "http://localhost:5000/mcp",
            }));
        tokenResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, await tokenResponse.Content.ReadAsStringAsync());
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = token.GetProperty("refresh_token").GetString();
        refreshToken.Should().NotBeNullOrWhiteSpace();

        using var refreshResponse = await _client.PostAsync(
            "/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = OAuthKnownClients.ClaudeHostedClientId,
                ["refresh_token"] = refreshToken!,
                ["resource"] = "http://localhost:5000/mcp",
            }));
        refreshResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, await refreshResponse.Content.ReadAsStringAsync());
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        refreshed.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        refreshed.GetProperty("refresh_token").GetString().Should().NotBe(refreshToken);
    }

    [Fact]
    public async Task DynamicRegistration_RejectsClaudeHostedHttpsRedirect()
    {
        using var response = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Claude hosted connector",
            redirect_uris = new[] { OAuthKnownClients.ClaudeHostedRedirectUri },
            token_endpoint_auth_method = "none",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_redirect_uri");
        body.Should().Contain(OAuthKnownClients.ClaudeHostedClientId);
    }

    [Theory]
    [InlineData("http://localhost:49152/callback")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/nativeclient")]
    [InlineData("https://example.com/*")]
    [InlineData("http://10.0.0.7/callback")]
    [InlineData("com.example.app://evil.example/callback")]
    [InlineData("com.app:/callback")]
    [InlineData("http://2130706433/callback")]
    public async Task DynamicRegistration_RejectsUnsafeRedirects(string redirect)
    {
        using var response = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Unsafe client",
            redirect_uris = new[] { redirect },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://other.example/mcp")]
    [InlineData("http://localhost:5000/mcp/")]
    public async Task Authorization_RequiresExactlyCanonicalResource(string? resource)
    {
        const string redirectUri = "http://127.0.0.1:49158/callback";
        var clientId = await RegisterClientAsync(redirectUri);
        var values = new List<KeyValuePair<string, string?>>
        {
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("response_type", "code"),
            new("scope", "mcp:invoke"),
            new("code_challenge", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
            new("code_challenge_method", "S256"),
        };
        if (resource is not null)
            values.Add(new("resource", resource));

        using var response = await _client.GetAsync(
            "/oauth/authorize" + QueryString.Create(values));
        await AssertInvalidTargetAsync(response);
    }

    [Fact]
    public async Task Authorization_RejectsMultipleResourceValues()
    {
        const string redirectUri = "http://127.0.0.1:49159/callback";
        var clientId = await RegisterClientAsync(redirectUri);
        var query = QueryString.Create(new[]
        {
            KeyValuePair.Create("client_id", (string?) clientId),
            KeyValuePair.Create("redirect_uri", (string?) redirectUri),
            KeyValuePair.Create("response_type", (string?) "code"),
            KeyValuePair.Create("scope", (string?) "mcp:invoke"),
            KeyValuePair.Create("code_challenge", (string?) "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
            KeyValuePair.Create("code_challenge_method", (string?) "S256"),
            KeyValuePair.Create("resource", (string?) "http://localhost:5000/mcp"),
            KeyValuePair.Create("resource", (string?) "http://localhost:5000/mcp"),
        });

        using var response = await _client.GetAsync("/oauth/authorize" + query);
        await AssertInvalidTargetAsync(response);
    }

    [Fact]
    public async Task Authorization_ConsentPageShowsClientPermissionsAndSignedInIdentity()
    {
        const string redirectUri = "http://127.0.0.1:49161/callback";
        const string subject = "consent-page-user";
        using var registration = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Desktop MCP client",
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
                EntraObjectId = subject,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync();
        }

        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp:invoke offline_access",
            ["code_challenge"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["code_challenge_method"] = "S256",
            ["resource"] = "http://localhost:5000/mcp",
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/authorize" + query);
        request.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");
        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<title>Authorize Desktop MCP client | Agentweaver</title>");
        html.Should().Contain("Requesting application");
        html.Should().Contain($"Client ID: {clientId}");
        html.Should().Contain("Use Agentweaver MCP tools");
        html.Should().Contain("Stay connected");
        html.Should().Contain($"<strong>{subject}</strong>");
        html.Should().Contain("value=\"approve\">Allow</button>");
        html.Should().Contain("value=\"deny\">Deny</button>");
        var styleNonce = Regex.Match(html, "<style nonce=\"([^\"]+)\">").Groups[1].Value;
        styleNonce.Should().NotBeNullOrWhiteSpace();
        policy.Should().Contain($"style-src 'nonce-{styleNonce}'");
    }

    [Fact]
    public async Task Token_RequiresExactlyCanonicalResource()
    {
        var clientId = await RegisterClientAsync("http://127.0.0.1:49160/callback");
        var cases = new[]
        {
            Array.Empty<KeyValuePair<string, string>>(),
            new[] { KeyValuePair.Create("resource", "https://other.example/mcp") },
            new[] { KeyValuePair.Create("resource", "http://localhost:5000/mcp/") },
            new[]
            {
                KeyValuePair.Create("resource", "http://localhost:5000/mcp"),
                KeyValuePair.Create("resource", "http://localhost:5000/mcp"),
            },
        };

        foreach (var resources in cases)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                KeyValuePair.Create("grant_type", "refresh_token"),
                KeyValuePair.Create("client_id", clientId),
                KeyValuePair.Create("refresh_token", "not-a-token"),
            };
            form.AddRange(resources);
            using var response = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_target");
        }
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

        var (code, verifier) = await IssueAuthorizationCodeAsync(
            clientId, redirectUri, "mcp:invoke offline_access");

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
            ["resource"] = "http://localhost:5000/mcp",
        });
        var accessToken = token.GetProperty("access_token").GetString()!;
        accessToken.Count(c => c == '.').Should().Be(2);
        var unownedProjectId = ProjectId.New();
        await using (var projectScope = _factory.Services.CreateAsyncScope())
        {
            var now = DateTimeOffset.UtcNow;
            await projectScope.ServiceProvider.GetRequiredService<IProjectStore>().InsertAsync(new Project
            {
                Id = unownedProjectId,
                Name = $"Broker authorization {Guid.NewGuid():N}",
                Origin = ProjectOrigin.Blank(),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                DefaultBranch = "main",
                Owner = "different-subject",
                ProviderSettings = new ProjectProviderSettings
                {
                    DefaultProvider = ModelSource.GitHubCopilot,
                },
                State = ProjectState.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await projectScope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>()
                .UpsertAsync(new ProjectRoleAssignment
                {
                    ProjectId = unownedProjectId,
                    PrincipalId = "different-subject",
                    Role = ProjectRole.Owner,
                    GrantedBy = "different-subject",
                    GrantedAt = now,
                });
        }
        using (var brokerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/projects"))
        {
            brokerRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var brokerResponse = await _client.SendAsync(brokerRequest);
            brokerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        using (var unownedRequest = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/api/projects/{unownedProjectId.Value}"))
        {
            unownedRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var unownedResponse = await _client.SendAsync(unownedRequest);
            unownedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "broker authentication must not bypass project authorization");
        }
        using (var unrelatedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session"))
        {
            unrelatedRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var unrelatedResponse = await _client.SendAsync(unrelatedRequest);
            unrelatedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        var refreshToken = token.GetProperty("refresh_token").GetString()!;

        async Task<HttpResponseMessage> RefreshAsync(string value) =>
            await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = value,
                ["resource"] = "http://localhost:5000/mcp",
            }));

        var concurrent = await Task.WhenAll(RefreshAsync(refreshToken), RefreshAsync(refreshToken));
        concurrent.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        concurrent.Count(response => response.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
        var winner = concurrent.Single(response => response.StatusCode == HttpStatusCode.OK);
        var rotated = await winner.Content.ReadFromJsonAsync<JsonElement>();
        var rotatedRefreshToken = rotated.GetProperty("refresh_token").GetString()!;
        rotatedRefreshToken.Should().NotBe(refreshToken);

        string redeemedTokenId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await manager.FindByReferenceIdAsync(refreshToken);
            entry.Should().NotBeNull();
            redeemedTokenId = (await manager.GetIdAsync(entry!))!;
            var descriptor = new OpenIddictTokenDescriptor();
            await manager.PopulateAsync(descriptor, entry!);
            descriptor.CreationDate = DateTimeOffset.UtcNow.AddDays(-31);
            await manager.UpdateAsync(entry!, descriptor);
        }
        var maintenance = new OAuthMaintenanceService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OAuthMaintenanceService>.Instance);
        await maintenance.RunOnceAsync(CancellationToken.None);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            (await manager.FindByIdAsync(redeemedTokenId)).Should().NotBeNull(
                "replay-identifying entries must survive the full fixed refresh-family lifetime plus margin");
        }

        using var codeReplay = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["resource"] = "http://localhost:5000/mcp",
        }));
        codeReplay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var replay = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["resource"] = "http://localhost:5000/mcp",
        }));
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var familyUse = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = rotatedRefreshToken,
            ["resource"] = "http://localhost:5000/mcp",
        }));
        familyUse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        foreach (var response in concurrent)
            response.Dispose();
    }

    [Fact]
    public async Task ExpiredDynamicClient_IsRejectedBeforeMaintenanceForAuthorizationAndTokenRequests()
    {
        const string redirectUri = "http://127.0.0.1:49163/callback";
        var clientId = await RegisterClientAsync(redirectUri);
        var (code, verifier) = await IssueAuthorizationCodeAsync(clientId, redirectUri, "mcp:invoke");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await applications.FindByClientIdAsync(clientId);
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, application!);
            descriptor.Properties[OAuthDynamicClientExpiration.ExpirationProperty] =
                JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
            await applications.UpdateAsync(application!, descriptor);
        }

        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp:invoke",
            ["code_challenge"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["code_challenge_method"] = "S256",
            ["resource"] = "http://localhost:5000/mcp",
        });
        using var authorization = await _client.GetAsync("/oauth/authorize" + query);
        authorization.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
        (await authorization.Content.ReadAsStringAsync()).Should().Contain("invalid_client");

        using var token = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["resource"] = "http://localhost:5000/mcp",
            }));
        token.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
        var tokenBody = await token.Content.ReadAsStringAsync();
        tokenBody.Should().Contain("invalid_client");
        tokenBody.Should().Contain("dynamically registered client has expired");

        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verificationDb.OAuthDynamicRegistrations.SingleAsync(x => x.ClientId == clientId))
            .DisabledAt.Should().BeNull("request validation must not depend on the maintenance sweep");
    }

    [Fact]
    public async Task StaticClient_IsUnaffectedByDynamicExpirationValidation()
    {
        var clientId = $"static-expiration-test-{Guid.NewGuid():N}";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            await applications.CreateAsync(OAuthStaticClientReconciler.CreateDescriptor(
                new OAuthStaticClient
                {
                    ClientId = clientId,
                    DisplayName = "Static expiration test",
                    RedirectUris = ["http://127.0.0.1:49164/callback"],
                },
                "http://localhost:5000/mcp"));
        }

        using var response = await _client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = "not-a-token",
                ["resource"] = "http://localhost:5000/mcp",
            }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_grant");
        body.Should().NotContain("client has expired");
    }

    [Fact]
    public async Task DynamicRegistration_UsesPersistedExpirationAcrossConfigChangesAndMaintenanceReclaimsQuota()
    {
        var firstId = await RegisterClientAsync("http://127.0.0.1:49165/callback");
        DateTimeOffset persistedExpiration;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var registration = await db.OAuthDynamicRegistrations.SingleAsync(x => x.ClientId == firstId);
            registration.RegisteredAt = DateTimeOffset.UtcNow.AddDays(-31);
            var configChangeApplications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var application = await configChangeApplications.FindByClientIdAsync(firstId);
            var descriptor = new OpenIddictApplicationDescriptor();
            await configChangeApplications.PopulateAsync(descriptor, application!);
            persistedExpiration = DateTimeOffset.UtcNow.AddDays(2);
            descriptor.Properties[OAuthDynamicClientExpiration.ExpirationProperty] =
                JsonSerializer.SerializeToElement(persistedExpiration.ToUnixTimeSeconds());
            await configChangeApplications.UpdateAsync(application!, descriptor);
            await db.SaveChangesAsync();

            var changedConfiguration = scope.ServiceProvider.GetRequiredService<OAuthServerConfiguration>() with
            {
                DynamicRegistrationLifetime = TimeSpan.FromDays(1),
            };
            var configChangeService = new OAuthDynamicClientRegistrationService(
                db, configChangeApplications, changedConfiguration);
            using var configChangeDocument = JsonDocument.Parse(
                """{"client_name":"Config change client","redirect_uris":["http://127.0.0.1:49166/callback"]}""");
            await configChangeService.RegisterAsync(
                configChangeDocument.RootElement, "config-change-test", CancellationToken.None);
            (await db.OAuthDynamicRegistrations.SingleAsync(x => x.ClientId == firstId))
                .DisabledAt.Should().BeNull(
                    "the changed one-day lifetime must not replace the first client's persisted expiration");

            application = await configChangeApplications.FindByClientIdAsync(firstId);
            descriptor = new OpenIddictApplicationDescriptor();
            await configChangeApplications.PopulateAsync(descriptor, application!);
            OAuthDynamicClientExpiration.TryGetExpiration(descriptor, out var expirationAfterConfigChange)
                .Should().BeTrue();
            expirationAfterConfigChange.ToUnixTimeSeconds()
                .Should().Be(persistedExpiration.ToUnixTimeSeconds());

            descriptor.Properties[OAuthDynamicClientExpiration.ExpirationProperty] =
                JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds());
            await configChangeApplications.UpdateAsync(application!, descriptor);
        }

        int activeBeforeMaintenance;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            activeBeforeMaintenance = await db.OAuthDynamicRegistrations.CountAsync(x => x.DisabledAt == null);
        }
        var maintenance = new OAuthMaintenanceService(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OAuthMaintenanceService>.Instance);
        await maintenance.RunOnceAsync(CancellationToken.None);

        await using var verificationScope = _factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verificationDb.OAuthDynamicRegistrations.SingleAsync(x => x.ClientId == firstId))
            .DisabledAt.Should().NotBeNull();
        var applications = verificationScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var expiredApplication = await applications.FindByClientIdAsync(firstId);
        expiredApplication.Should().NotBeNull();
        (await applications.GetRedirectUrisAsync(expiredApplication!)).Should().BeEmpty();
        (await applications.GetPermissionsAsync(expiredApplication!)).Should().BeEmpty();

        var activeAfterMaintenance = await verificationDb.OAuthDynamicRegistrations
            .CountAsync(x => x.DisabledAt == null);
        activeAfterMaintenance.Should().BeLessThan(activeBeforeMaintenance);
        var configuration = verificationScope.ServiceProvider
            .GetRequiredService<OAuthServerConfiguration>() with
        {
            DynamicRegistrationsTotal = activeAfterMaintenance + 1,
        };
        var service = new OAuthDynamicClientRegistrationService(
            verificationDb, applications, configuration);
        using var document = JsonDocument.Parse(
            """{"client_name":"Replacement client","redirect_uris":["http://127.0.0.1:49167/callback"]}""");
        var replacement = await service.RegisterAsync(
            document.RootElement, "quota-test", CancellationToken.None);
        replacement.client_id.Should().StartWith("aw_native_");
        (await verificationDb.OAuthDynamicRegistrations.CountAsync(x => x.DisabledAt == null))
            .Should().Be(activeAfterMaintenance + 1,
                "maintenance must reclaim the expired client's active-registration quota");
    }

    private async Task<string> RegisterClientAsync(string redirectUri)
    {
        using var registration = await _client.PostAsJsonAsync("/oauth/register", new
        {
            client_name = "Resource validation test",
            redirect_uris = new[] { redirectUri },
        });
        registration.EnsureSuccessStatusCode();
        return (await registration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("client_id").GetString()!;
    }

    private static QueryString ClaudeAuthorizationQuery(string redirectUri) =>
        QueryString.Create(new Dictionary<string, string?>
        {
            ["client_id"] = OAuthKnownClients.ClaudeHostedClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = OAuthServerConfiguration.McpScope,
            ["code_challenge"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            ["code_challenge_method"] = "S256",
            ["resource"] = "http://localhost:5000/mcp",
        });

    private async Task<(string Code, string Verifier)> IssueAuthorizationCodeAsync(
        string clientId,
        string redirectUri,
        string scope)
    {
        var sessionId = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        await using (var serviceScope = _factory.Services.CreateAsyncScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.BrowserEntraSessions.Add(new BrowserEntraSession
            {
                Id = sessionId,
                EntraObjectId = $"expiration-test-{Guid.NewGuid():N}",
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
            ["scope"] = scope,
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
        ParseQuery(callback.Query)["state"].Should().Be("client-state");
        return (ParseQuery(callback.Query)["code"], verifier);
    }

    private static async Task AssertInvalidTargetAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Redirect);
        var error = response.StatusCode == HttpStatusCode.Redirect
            ? response.Headers.Location!.Query
            : await response.Content.ReadAsStringAsync();
        error.Should().Contain("invalid_target");
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]));
}

public sealed class OpenIddictServerFixture : IDisposable
{
    public AgentweaverWebApplicationFactory Factory { get; } =
        new(bypassAuthentication: false);
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
