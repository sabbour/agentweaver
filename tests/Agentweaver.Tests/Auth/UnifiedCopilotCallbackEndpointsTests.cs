using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Security;

namespace Agentweaver.Tests.Auth;

public sealed class UnifiedCopilotCallbackEndpointsTests
{
    private static readonly WebApplicationFactoryClientOptions NoRedirectNoCookies = new()
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    };

    [Fact]
    public async Task SharedCallback_CompletesProjectAuthorizationWhenStateMatchesProjectTransaction()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        var projectId = ProjectId.New();
        await SeedProjectOwnerAsync(factory, projectId, "owner");
        var begin = await PrepareProjectAuthorizationAsync(factory, projectId, "owner");

        using var browser = factory.CreateClient(NoRedirectNoCookies);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/github/copilot-app/callback?state={Uri.EscapeDataString(begin.State)}&code=test-code");
        request.Headers.Add("Cookie", begin.CookieHeader);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be($"http://localhost:5173/projects/{projectId}/settings?section=unattended&copilot_app_auth=success");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ProjectCopilotBindings.CountAsync()).Should().Be(1);
        (await db.PlatformDefaultCopilotBindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SharedCallback_CompletesPlatformDefaultAuthorizationWhenStateMatchesPlatformTransaction()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        var begin = await PreparePlatformAuthorizationAsync(factory, "platform-admin");

        using var browser = factory.CreateClient(NoRedirectNoCookies);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/github/copilot-app/callback?state={Uri.EscapeDataString(begin.State)}&code=test-code");
        request.Headers.Add("Cookie", begin.CookieHeader);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be("http://localhost:5173/platform-settings?copilot_app_auth=success");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ProjectCopilotBindings.CountAsync()).Should().Be(0);
        (await db.PlatformDefaultCopilotBindings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RetiredPlatformDefaultCallbackRoute_IsNotMapped()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        using var browser = factory.CreateClient(NoRedirectNoCookies);

        using var response = await browser.GetAsync(
            "/auth/github/platform-default-copilot/callback?state=retired-state&code=test-code");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SharedCallback_ReturnsProjectInvalidRedirectForUnknownProjectState()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        var projectId = ProjectId.New();
        await SeedProjectOwnerAsync(factory, projectId, "owner");
        var begin = await PrepareProjectAuthorizationAsync(factory, projectId, "owner");

        using var browser = factory.CreateClient(NoRedirectNoCookies);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/github/copilot-app/callback?state=unknown-project-state&code=test-code");
        request.Headers.Add("Cookie", begin.CookieHeader);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be("http://localhost:5173/projects?copilot_app_auth=authorization_transaction_invalid");
    }

    [Fact]
    public async Task SharedCallback_ReturnsProjectInvalidRedirectForExpiredProjectState()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        var projectId = ProjectId.New();
        await SeedProjectOwnerAsync(factory, projectId, "owner");
        var begin = await PrepareProjectAuthorizationAsync(factory, projectId, "owner", DateTimeOffset.UtcNow.AddMinutes(-1));

        using var browser = factory.CreateClient(NoRedirectNoCookies);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/github/copilot-app/callback?state={Uri.EscapeDataString(begin.State)}&code=test-code");
        request.Headers.Add("Cookie", begin.CookieHeader);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be("http://localhost:5173/projects?copilot_app_auth=authorization_transaction_invalid");
    }

    [Fact]
    public async Task SharedCallback_ReturnsPlatformInvalidRedirectForUnknownPlatformState()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        var begin = await PreparePlatformAuthorizationAsync(factory, "platform-admin");

        using var browser = factory.CreateClient(NoRedirectNoCookies);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/github/copilot-app/callback?state=unknown-platform-state&code=test-code");
        request.Headers.Add("Cookie", begin.CookieHeader);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be("http://localhost:5173/platform-settings?copilot_app_auth=authorization_transaction_invalid");
    }

    [Fact]
    public async Task SharedCallback_ReturnsPlatformInvalidRedirectForExpiredPlatformState()
    {
        await using var factory = new CopilotCallbackWebApplicationFactory();
        var begin = await PreparePlatformAuthorizationAsync(factory, "platform-admin", DateTimeOffset.UtcNow.AddMinutes(-1));

        using var browser = factory.CreateClient(NoRedirectNoCookies);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/auth/github/copilot-app/callback?state={Uri.EscapeDataString(begin.State)}&code=test-code");
        request.Headers.Add("Cookie", begin.CookieHeader);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString()
            .Should().Be("http://localhost:5173/platform-settings?copilot_app_auth=authorization_transaction_invalid");
    }

    private static async Task SeedProjectOwnerAsync(
        CopilotCallbackWebApplicationFactory factory,
        ProjectId projectId,
        string ownerObjectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.Projects.Add(new ProjectRecord { ProjectId = projectId.ToString() });
        await db.SaveChangesAsync();

        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        await projectStore.InsertAsync(new Project
        {
            Id = projectId,
            Name = "OAuth callback test project",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = factory.NewWorkingDirectory(),
            DefaultBranch = "main",
            Owner = ownerObjectId,
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var roleAssignments = scope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>();
        await roleAssignments.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = projectId,
            PrincipalId = ownerObjectId,
            Role = ProjectRole.Owner,
            GrantedBy = "test",
            GrantedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<PreparedAuthorization> PrepareProjectAuthorizationAsync(
        CopilotCallbackWebApplicationFactory factory,
        ProjectId projectId,
        string ownerObjectId,
        DateTimeOffset? expiresAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var persistence = services.GetRequiredService<GitHubConnectionsPersistenceStore>();
        var secretStore = services.GetRequiredService<ISecretStore>();
        var state = $"project-state-{Guid.NewGuid():N}";
        var callbackCookie = $"project-cookie-{Guid.NewGuid():N}";
        var verifierReference = $"project-verifier-{Guid.NewGuid():N}";
        await secretStore.SetSecretAsync(verifierReference, "project-verifier");
        await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
        {
            State = state,
            ExternalTransactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId(),
            AppKind = GitHubAppKind.Copilot,
            Purpose = GitHubAuthorizationPurpose.InteractiveCopilot,
            EntraObjectId = ownerObjectId,
            ProjectId = projectId.ToString(),
            ExpiresAtUnixMilliseconds = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10)).ToUnixTimeMilliseconds(),
            ReturnRouteKey = "projects",
            PkceVerifierProtected = verifierReference,
            CallbackCookieHash = HashCookie(callbackCookie),
            Status = GitHubAuthorizationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return new(state, CookieHeader(ProjectCopilotBindingService.SetCallbackCookie, callbackCookie));
    }

    private static async Task<PreparedAuthorization> PreparePlatformAuthorizationAsync(
        CopilotCallbackWebApplicationFactory factory,
        string adminObjectId,
        DateTimeOffset? expiresAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var persistence = services.GetRequiredService<GitHubConnectionsPersistenceStore>();
        var secretStore = services.GetRequiredService<ISecretStore>();
        var state = $"platform-state-{Guid.NewGuid():N}";
        var callbackCookie = $"platform-cookie-{Guid.NewGuid():N}";
        var browserSessionId = $"platform-session-{Guid.NewGuid():N}";
        var verifierReference = $"platform-verifier-{Guid.NewGuid():N}";
        await secretStore.SetSecretAsync(verifierReference, "platform-verifier");
        var db = services.GetRequiredService<MemoryDbContext>();
        db.BrowserEntraSessions.Add(new BrowserEntraSession
        {
            Id = browserSessionId,
            EntraObjectId = adminObjectId,
            PlatformRoles = PlatformRoles.PlatformAdmin,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        await db.SaveChangesAsync();
        await persistence.AddAuthorizationAsync(new GitHubAuthorizationRecord
        {
            State = state,
            ExternalTransactionId = GitHubConnectionsPersistenceStore.CreateExternalTransactionId(),
            AppKind = GitHubAppKind.Copilot,
            Purpose = GitHubAuthorizationPurpose.PlatformDefaultCopilot,
            EntraObjectId = adminObjectId,
            ProjectId = null,
            ExpiresAtUnixMilliseconds = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10)).ToUnixTimeMilliseconds(),
            ReturnRouteKey = "platform-settings",
            PkceVerifierProtected = verifierReference,
            CallbackCookieHash = HashCookie(callbackCookie),
            BrowserSessionId = browserSessionId,
            Status = GitHubAuthorizationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var callbackCookieHeader = CookieHeader(PlatformDefaultCopilotBindingService.SetCallbackCookie, callbackCookie);
        return new(state, $"{callbackCookieHeader}; {BrowserEntraSessionService.CookieName}={browserSessionId}");
    }

    private static string HashCookie(string value) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string CookieHeader(Action<HttpContext, string> writer, string value)
    {
        var context = new DefaultHttpContext();
        writer(context, value);
        var cookies = context.Response.Headers.SetCookie.ToArray();
        return cookies
            .Where(cookie => cookie is not null)
            .Select(cookie => cookie!.ToString().Split(';', 2)[0]!)
            .Single();
    }

    private static string Query(string url, string name) =>
        Uri.UnescapeDataString(
            new Uri(url).Query.TrimStart('?').Split('&')
                .Single(x => x.StartsWith($"{name}=", StringComparison.Ordinal))
                .Split('=', 2)[1]);

    private sealed record PreparedAuthorization(string State, string CookieHeader);

    private sealed class CopilotCallbackWebApplicationFactory : EntraWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:CopilotApp:ClientId"] = "copilot-client",
                    ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
                    ["Auth:CopilotApp:CallbackUrl"] = "https://agentweaver.test/auth/github/copilot-app/callback",
                    ["Auth:CopilotApp:FrontendUrl"] = "http://localhost:5173",
                    ["Auth:CopilotApp:Slug"] = "agentweaver-copilot",
                }));

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory, StubHttpClientFactory>();
            });
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(), disposeHandler: false);

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath switch
                    {
                        var path when path.StartsWith("/apps/", StringComparison.Ordinal) =>
                            """{"permissions":{"metadata":"read"}}""",
                        "/user" => """{"login":"octocat"}""",
                        _ => """{"access_token":"ghu_test_token","refresh_token":"refresh-secret"}""",
                    }),
                });
        }
    }
}
