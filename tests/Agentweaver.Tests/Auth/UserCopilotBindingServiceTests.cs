using System.Net;
using System.Security.Claims;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

public sealed class UserCopilotBindingServiceTests
{
    private const string CallbackUrl = "https://agentweaver.test/auth/github/copilot-app/callback";

    [Fact]
    public async Task CompleteBrowserCallback_BindsOnlyTheAuthenticatedUser()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore());
        var session = Session("user-one");
        var begin = await service.BeginAsync(Human("user-one"), HumanPrincipal(), session.Id);

        begin.Outcome.Should().Be(UserCopilotBindingOutcome.Success);
        (await service.CompleteBrowserCallbackAsync(
            session, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(UserCopilotBindingOutcome.Success);

        var binding = await db.UserCopilotBindings.SingleAsync();
        binding.EntraObjectId.Should().Be("user-one");
        binding.Status.Should().Be(GitHubBindingStatus.Active);
        (await db.UserModelProviderSettings.SingleAsync()).Preference
            .Should().Be(UserModelProviderPreference.GitHubCopilot);
        (await service.GetConnectionAsync(Human("user-two"), HumanPrincipal())).Connected.Should().BeFalse();
        (await service.GetConnectionAsync(Human("user-one"), HumanPrincipal())).Connected.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteBrowserCallback_WhenCredentialReadBackCannotBeVerified_DoesNotCreateBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new DisappearingCredentialReadSecretStore("copilot-app-user-"));
        var session = Session("user-one");
        var begin = await service.BeginAsync(Human("user-one"), HumanPrincipal(), session.Id);

        (await service.CompleteBrowserCallbackAsync(
            session, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(UserCopilotBindingOutcome.GitHubBindingUnavailable);

        db.ChangeTracker.Clear();
        (await db.UserCopilotBindings.CountAsync()).Should().Be(0);
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Failed);
    }

    private static UserCopilotBindingService CreateService(MemoryDbContext db, ISecretStore secrets)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:CopilotApp:ClientId"] = "copilot-client",
            ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
            ["Auth:CopilotApp:CallbackUrl"] = CallbackUrl,
            ["Auth:CopilotApp:FrontendUrl"] = "http://localhost:5173",
            ["Auth:CopilotApp:Slug"] = "agentweaver-copilot",
        }).Build();
        var clients = new StubHttpClientFactory();
        return new(
            configuration,
            new GitHubConnectionsPersistenceStore(db),
            secrets,
            new GitHubConnectionsCredentialVault(secrets),
            clients,
            new CopilotAppRegistrationService(configuration, clients),
            NullLogger<UserCopilotBindingService>.Instance);
    }

    private static CallerContext Human(string id) => new() { User = id, EntraObjectId = id };
    private static ClaimsPrincipal HumanPrincipal() =>
        new(new ClaimsIdentity([new Claim("oid", "user")], "test"));
    private static BrowserEntraSession Session(string id) => new()
    {
        Id = $"session-{id}",
        EntraObjectId = id,
        PlatformRoles = PlatformRoles.Contributor,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(
            new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static string Query(string url, string name) =>
        Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&')
            .Single(x => x.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1]);

    private sealed class DisappearingCredentialReadSecretStore(string prefix) : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        private readonly HashSet<string> _missingOnNextRead = new(StringComparer.Ordinal);

        public async Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            _missingOnNextRead.Remove(key)
                ? SecretGetResult.NotFound
                : await _inner.GetSecretAsync(key, ct);

        public async Task<string> SetSecretAsync(
            string key, string value, string? etag = null, CancellationToken ct = default)
        {
            var result = await _inner.SetSecretAsync(key, value, etag, ct);
            if (key.StartsWith(prefix, StringComparison.Ordinal) &&
                !string.Equals(value, """{"status":"revoked"}""", StringComparison.Ordinal))
                _missingOnNextRead.Add(key);
            return result;
        }

        public Task DeleteSecretAsync(string key, CancellationToken ct = default) =>
            _inner.DeleteSecretAsync(key, ct);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath switch
                    {
                        var path when path.StartsWith("/apps/", StringComparison.Ordinal) =>
                            """{"permissions":{"metadata":"read"}}""",
                        "/user" => """{"login":"octocat"}""",
                        _ => """{"access_token":"ghu_user","refresh_token":"refresh"}""",
                    }),
                });
        }
    }
}
