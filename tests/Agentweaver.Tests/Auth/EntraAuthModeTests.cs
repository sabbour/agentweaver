using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class EntraAuthModeTests : IClassFixture<EntraWebApplicationFactory>
{
    private readonly EntraWebApplicationFactory _factory;

    public EntraAuthModeTests(EntraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AuthConfig_ReturnsEntraModeAndPublicOidcConfig()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("mode").GetString().Should().Be("Entra");
        json.RootElement.GetProperty("entra").GetProperty("client_id").GetString().Should().Be(EntraWebApplicationFactory.ClientId);
        json.RootElement.GetProperty("entra").GetProperty("enterprise_app_object_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task AuthConfig_ReturnsConfiguredEnterpriseAppObjectId_WhenPresent()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Entra:EnterpriseAppObjectId"] = "fef39db7-a690-4383-8cf2-32da2b27a3d3",
                })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("entra").GetProperty("enterprise_app_object_id").GetString()
            .Should().Be("fef39db7-a690-4383-8cf2-32da2b27a3d3");
    }

    [Fact]
    public async Task EntraToken_WithRecognizedRole_CanAccessContextAndProjects()
    {
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.Contributor);

        var contextResponse = await client.GetAsync("/api/auth/context");
        var projectsResponse = await client.GetAsync("/api/projects");

        contextResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        projectsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await contextResponse.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("primary_platform_role").GetString().Should().Be(PlatformRoles.Contributor);
    }

    [Fact]
    public async Task EntraToken_WithoutRecognizedRole_IsForbidden()
    {
        using var client = _factory.CreateAuthenticatedClient("UnknownRole");

        var response = await client.GetAsync("/api/auth/context");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonJwtBearer_IsRejectedInEntraMode()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BrowserSessionCookie_BootstrapsNewTabIdentity_ButDoesNotAuthorizePlatformApis()
    {
        const string sessionId = "browser-session-new-tab";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.BrowserEntraSessions.Add(new BrowserEntraSession
            {
                Id = sessionId,
                EntraObjectId = "entra-user-new-tab",
                DisplayName = "New Tab Test User",
                Email = "new-tab-test@example.com",
                PlatformRoles = PlatformRoles.Contributor,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        using var newTab = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
        request.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");

        using var response = await newTab.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        json.GetProperty("display_name").GetString().Should().Be("New Tab Test User");
        json.GetProperty("email").GetString().Should().Be("new-tab-test@example.com");
        json.GetProperty("platform_roles").EnumerateArray().Select(role => role.GetString())
            .Should().Contain(PlatformRoles.Contributor);

        using var projectsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        projectsRequest.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");
        using var projectsResponse = await newTab.SendAsync(projectsRequest);

        projectsResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the shared browser cookie remains limited to session bootstrap and OAuth handoffs and must not replace bearer auth for platform APIs");
    }

    [Fact]
    public async Task AuthSession_AllowsContributorToConfigurePersonalProvider_WhenNoPlatformProviderExists()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            await secrets.DeleteSecretAsync("byok-provider-configurations");
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.Contributor);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeTrue(
            "non-admin users must enter the app to configure their own provider");
    }

    [Fact]
    public async Task AuthSession_ReportsAiConfiguredTrue_WhenByokConfigurationExists()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            await secrets.SetSecretAsync(
                "byok-provider-configurations",
                """
                {"active_provider_id":"p1","providers":[{"id":"p1","name":"Test","type":"openai","baseUrl":"https://api.example.com","model":"gpt-4o","apiKey":"sk-test"}]}
                """);
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.PlatformAdmin);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AuthSession_ReportsConfigured_WhenPlatformCopilotStatusIsConnectedAndNoByokIsActive()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await secrets.DeleteSecretAsync("byok-provider-configurations");
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = "copilot-app-platform-default-version",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await secrets.SetSecretAsync(
                "copilot-app-platform-default-version",
                """{"Status":"signed-in","AccessToken":"ghu_platform","ExpiresAt":"2099-01-01T00:00:00Z","GitHubLogin":"octocat"}""");
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.PlatformAdmin);

        var providerListResponse = await client.GetAsync("/api/admin/byok-providers");
        providerListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        providerListResponse.Headers.CacheControl?.NoStore.Should().BeTrue();
        var providerList = JsonDocument.Parse(await providerListResponse.Content.ReadAsStringAsync());
        providerList.RootElement.GetProperty("active_provider_id").ValueKind.Should().Be(JsonValueKind.Null);

        var copilotStatusResponse = await client.GetAsync("/api/admin/platform-default-copilot/status");
        copilotStatusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        copilotStatusResponse.Headers.CacheControl?.NoStore.Should().BeTrue();
        var copilotStatus = JsonDocument.Parse(await copilotStatusResponse.Content.ReadAsStringAsync());
        copilotStatus.RootElement.GetProperty("connected").GetBoolean().Should().BeTrue();
        copilotStatus.RootElement.GetProperty("github_login").GetString().Should().Be("octocat");

        var sessionResponse = await client.GetAsync("/api/auth/session");
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        sessionResponse.Headers.CacheControl?.NoStore.Should().BeTrue();
        var session = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        session.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        session.RootElement.GetProperty("platform_roles").EnumerateArray()
            .Select(role => role.GetString())
            .Should().Contain(PlatformRoles.PlatformAdmin);
        session.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeTrue(
            "the setup guard must agree with the connected platform Copilot status");
    }

    [Fact]
    public async Task AuthSession_TracksPlatformCopilotDisconnectAndReconnect()
    {
        const string credentialReference = "copilot-app-platform-default-loop-regression";
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = credentialReference,
                CredentialVersion = "version-one",
                GrantDigest = "digest-one",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await secrets.SetSecretAsync(
                credentialReference,
                """{"status":"signed-in","accessToken":"ghu_platform","expiresAt":"2099-01-01T00:00:00Z","githubLogin":"octocat"}""");
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.PlatformAdmin);

        var connected = await client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        connected.GetProperty("ai_configured").GetBoolean().Should().BeTrue();

        using var disconnect = await client.PostAsJsonAsync(
            "/api/admin/platform-default-copilot/disconnect",
            new { });
        disconnect.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var disconnected = await client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        disconnected.GetProperty("ai_configured").GetBoolean().Should().BeFalse();

        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
            binding.Status = GitHubBindingStatus.Active;
            binding.CredentialReference = credentialReference;
            binding.CredentialVersion = "version-two";
            binding.GrantDigest = "digest-two";
            binding.DeactivatedAt = null;
            await secrets.SetSecretAsync(
                credentialReference,
                """{"status":"signed-in","accessToken":"ghu_platform_2","expiresAt":"2099-01-01T00:00:00Z","githubLogin":"octocat"}""");
            await db.SaveChangesAsync();
        }

        var reconnected = await client.GetFromJsonAsync<JsonElement>("/api/auth/session");
        reconnected.GetProperty("ai_configured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AuthSession_AllowsContributorToConfigurePersonalProvider_WhenPlatformBindingIsUnusable()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = "copilot-app-platform-default-missing",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await secrets.DeleteSecretAsync("copilot-app-platform-default-missing");
            await secrets.DeleteSecretAsync("byok-provider-configurations");
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.Contributor);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeTrue(
            "an unusable platform provider must not block a user from configuring personal access");
    }

    [Fact]
    public async Task AuthSession_ReportsAiConfiguredFalse_WhenByokSecretIsMalformed()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            await secrets.SetSecretAsync("byok-provider-configurations", "{\"active_provider_id\":\"p1\"");
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.PlatformAdmin);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AuthSession_ReportsAiConfiguredFalse_WhenPlatformDefaultSecretIsNotAnObject()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
            db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = "copilot-app-platform-default-invalid-shape",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await secrets.SetSecretAsync("copilot-app-platform-default-invalid-shape", "\"signed-in\"");
            await secrets.DeleteSecretAsync("byok-provider-configurations");
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.PlatformAdmin);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SignOut_ClearsBrowserSessionCookie_AndRevokesPersistedBrowserSession()
    {
        const string objectId = "entra-user-signout";
        const string sessionId = "browser-session-signout";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.BrowserEntraSessions.Add(new BrowserEntraSession
            {
                Id = sessionId,
                EntraObjectId = objectId,
                DisplayName = "Sign-out Test User",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClientForObjectId(objectId, PlatformRoles.Contributor);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/session/sign-out");
        request.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies.Should().Contain(cookie =>
            cookie.StartsWith($"{BrowserEntraSessionService.CookieName}=", StringComparison.Ordinal) &&
            cookie.Contains("expires=thu, 01 jan 1970", StringComparison.OrdinalIgnoreCase));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verifyDb.BrowserEntraSessions.FindAsync([sessionId])).Should().BeNull();
    }
}
