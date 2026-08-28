using System.Net;
using System.Net.Http.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class McpBrowserHandoffEndpointsTests
{
    private static WebApplicationFactoryClientOptions NoRedirectNoCookies => new()
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    };

    [Fact]
    public async Task RepoAppMcpHandoff_RequiresAndPinsTheInitiatorsAuthenticatedBrowserSession()
    {
        await using var factory = new RepoAppHandoffWebApplicationFactory();
        using var initiator = factory.CreateAuthenticatedClientForObjectId(
            "initiator", PlatformRoles.Contributor);
        var beginResponse = await initiator.PostAsJsonAsync(
            "/api/auth/github/repo-app/authorizations/handoff", new { return_route_key = "settings" });
        beginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var transactionId = (await beginResponse.Content.ReadFromJsonAsync<GitHubHandoffResponse>())!.TransactionId;

        var initiatorSession = await IssueBrowserSessionAsync(factory, "initiator");
        var attackerSession = await IssueBrowserSessionAsync(factory, "attacker");
        var expiredSession = await IssueBrowserSessionAsync(factory, "initiator", DateTimeOffset.UtcNow.AddMinutes(-1));
        using var browser = factory.CreateClient(NoRedirectNoCookies);

        (await GetWithBrowserSessionAsync(browser,
            $"/auth/github/repo-app/handoff/{transactionId}", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await GetWithBrowserSessionAsync(
            browser, $"/auth/github/repo-app/handoff/{transactionId}", expiredSession)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        var attacker = await GetWithBrowserSessionAsync(
            browser, $"/auth/github/repo-app/handoff/{transactionId}", attackerSession);
        attacker.StatusCode.Should().Be(HttpStatusCode.NotFound);
        attacker.Headers.Contains("Set-Cookie").Should().BeFalse();

        var legitimate = await GetWithBrowserSessionAsync(
            browser, $"/auth/github/repo-app/handoff/{transactionId}", initiatorSession);
        legitimate.StatusCode.Should().Be(HttpStatusCode.Redirect);
        legitimate.Headers.Location!.ToString().Should().Contain("github.com/login/oauth/authorize");
        legitimate.Headers.GetValues("Set-Cookie").Should().Contain(cookie =>
            cookie.StartsWith($"{RepoAppUserAuthorizationService.CallbackCookieName}=", StringComparison.Ordinal));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.GitHubAuthorizations.SingleAsync()).BrowserSessionId.Should().Be(initiatorSession);
    }

    private static async Task<string> IssueBrowserSessionAsync(
        RepoAppHandoffWebApplicationFactory factory,
        string subject,
        DateTimeOffset? expiresAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<BrowserEntraSessionService>();
        var context = new DefaultHttpContext();
        var session = await sessions.IssueAsync(context, new EntraAccessTokenClaims(
            subject,
            EntraWebApplicationFactory.TenantId,
            subject,
            null,
            [],
            [],
            null,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10)));
        return session.Id;
    }

    private static async Task<HttpResponseMessage> GetWithBrowserSessionAsync(
        HttpClient browser,
        string path,
        string? sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (sessionId is not null)
            request.Headers.Add("Cookie", $"{BrowserEntraSessionService.CookieName}={sessionId}");
        return await browser.SendAsync(request);
    }

    private sealed class RepoAppHandoffWebApplicationFactory : EntraWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:RepoApp:ClientId"] = "repo-app-client",
                ["Auth:RepoApp:ClientSecret"] = "repo-app-secret",
                ["Auth:RepoApp:CallbackUrl"] = "https://agentweaver.test/auth/github/repo-app/callback",
            }));
        }
    }

    private sealed record GitHubHandoffResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("transaction_id")] string TransactionId);
}
