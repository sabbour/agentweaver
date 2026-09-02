using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task AuthSession_ReportsAiConfiguredFalse_WhenNoByokOrPlatformDefaultBindingExists()
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
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeFalse();
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
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.Contributor);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AuthSession_ReportsAiConfiguredTrue_WhenPlatformDefaultCopilotBindingExists()
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
                CredentialReference = "copilot-app-platform-default-version",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await secrets.SetSecretAsync(
                "copilot-app-platform-default-version",
                """{"status":"signed-in","accessToken":"ghu_platform","expiresAt":"2099-01-01T00:00:00Z","githubLogin":"octocat"}""");
            await db.SaveChangesAsync();
        }
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.Contributor);

        var response = await client.GetAsync("/api/auth/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task AuthSession_ReportsAiConfiguredFalse_WhenPlatformDefaultBindingSecretIsMissing()
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
        json.RootElement.GetProperty("ai_configured").GetBoolean().Should().BeFalse();
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
