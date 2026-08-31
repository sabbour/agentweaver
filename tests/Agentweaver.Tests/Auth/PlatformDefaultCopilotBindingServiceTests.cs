using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

public sealed class PlatformDefaultCopilotBindingServiceTests
{
    [Fact]
    public async Task Begin_RequiresPlatformAdminAndPinsThePlatformDefaultPurpose()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore());

        (await service.BeginAsync(Human("member"), HumanPrincipal()))
            .Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired);

        var begin = await service.BeginAsync(Admin("admin"), HumanPrincipal());
        begin.Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.Success);
        var stored = await db.GitHubAuthorizations.SingleAsync();
        stored.ProjectId.Should().BeNull();
        stored.AppKind.Should().Be(GitHubAppKind.Copilot);
        stored.Purpose.Should().Be(GitHubAuthorizationPurpose.PlatformDefaultCopilot);
        begin.AuthorizationUrl.Should().Contain("code_challenge_method=S256");
    }

    [Fact]
    public async Task CompleteBrowserCallback_WritesSingletonBindingAndReturnsPlatformSettingsRedirect()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore(), """{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal());
        var state = Query(begin.AuthorizationUrl!, "state");

        (await service.CompleteBrowserCallbackAsync(null, null, state, "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
        binding.Id.Should().Be(PlatformDefaultCopilotBindingRecord.SingletonId);
        binding.Status.Should().Be(GitHubBindingStatus.Active);
        JsonSerializer.Serialize(binding).Should().NotContain("ghu_platform").And.NotContain("refresh-secret");
        (await service.GetCallbackRedirectAsync(PlatformDefaultCopilotBindingOutcome.Success))
            .Should().Be("http://localhost:5173/platform-settings?copilot_app_auth=success");
    }

    [Fact]
    public async Task ConnectionStatus_ReturnsVerifiedGitHubLoginWithoutCredentialMaterial()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore(), """{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal());

        (await service.CompleteBrowserCallbackAsync(null, null, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        var connection = await service.GetConnectionAsync(Admin("platform-admin"), HumanPrincipal());
        connection.Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.Success);
        connection.Connected.Should().BeTrue();
        connection.GitHubLogin.Should().Be("octocat");
        JsonSerializer.Serialize(connection).Should().NotContain("ghu_platform").And.NotContain("refresh-secret");
    }

    [Fact]
    public async Task Disconnect_RevokesOnlyTheSingletonBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await new GitHubConnectionsPersistenceStore(db).ReplacePlatformDefaultCopilotBindingAsync(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-existing",
            CredentialVersion = "version-one",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await secrets.SetSecretAsync("copilot-app-platform-default-existing", """{"accessToken":"ghu_platform"}""");
        var service = CreateService(db, secrets);

        (await service.DisconnectAsync(Admin("platform-admin"), HumanPrincipal()))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        db.ChangeTracker.Clear();
        (await db.PlatformDefaultCopilotBindings.SingleAsync()).Status.Should().Be(GitHubBindingStatus.Revoked);
        (await secrets.GetSecretAsync("copilot-app-platform-default-existing")).Should().Be(SecretGetResult.NotFound);
    }

    [Fact]
    public async Task CompleteBrowserCallback_RevokeAndTombstonesReplacedCredentialAfterRebind()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await new GitHubConnectionsPersistenceStore(db).ReplacePlatformDefaultCopilotBindingAsync(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "first-admin",
            CredentialReference = "copilot-app-platform-default-old",
            CredentialVersion = "version-old",
            GrantDigest = "digest-old",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await secrets.SetSecretAsync("copilot-app-platform-default-old", """{"access_token":"ghu_old","refresh_token":"refresh-old","status":"signed-in","github_login":"old-login"}""");
        var service = CreateService(db, secrets, """{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal());

        (await service.CompleteBrowserCallbackAsync(null, null, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        db.ChangeTracker.Clear();
        var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
        binding.CredentialReference.Should().NotBe("copilot-app-platform-default-old");
        (await secrets.GetSecretAsync("copilot-app-platform-default-old")).Should().Be(SecretGetResult.NotFound);
    }

    [Fact]
    public async Task CompleteBrowserCallback_DoesNotRevokeTheReplacementGrantWhenTheSameGitHubLoginReconnects()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var httpClientFactory = new StubHttpClientFactory("""{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        await new GitHubConnectionsPersistenceStore(db).ReplacePlatformDefaultCopilotBindingAsync(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "first-admin",
            CredentialReference = "copilot-app-platform-default-old",
            CredentialVersion = "version-old",
            GrantDigest = "digest-old",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await secrets.SetSecretAsync("copilot-app-platform-default-old", """{"access_token":"ghu_old","refresh_token":"refresh-old","status":"signed-in","github_login":"octocat"}""");
        var service = CreateService(db, secrets, httpClientFactory: httpClientFactory);
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal());

        (await service.CompleteBrowserCallbackAsync(null, null, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        httpClientFactory.ProviderGrantRevocations.Should().Be(0);
        (await secrets.GetSecretAsync("copilot-app-platform-default-old")).Should().Be(SecretGetResult.NotFound);
    }

    private static PlatformDefaultCopilotBindingService CreateService(
        MemoryDbContext db,
        ISecretStore secrets,
        string? provider = null,
        StubHttpClientFactory? httpClientFactory = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:CopilotApp:ClientId"] = "copilot-client",
            ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
            ["Auth:CopilotApp:CallbackUrl"] = "https://agentweaver.test/auth/github/copilot-app/callback",
            ["Auth:CopilotApp:FrontendUrl"] = "http://localhost:5173",
            ["Auth:CopilotApp:Slug"] = "agentweaver-copilot",
        }).Build();
        httpClientFactory ??= new StubHttpClientFactory(provider);
        return new(
            configuration,
            new GitHubConnectionsPersistenceStore(db),
            secrets,
            new GitHubConnectionsCredentialVault(secrets),
            httpClientFactory,
            new CopilotAppRegistrationService(configuration, httpClientFactory),
            NullLogger<PlatformDefaultCopilotBindingService>.Instance);
    }

    private static CallerContext Human(string id) => new() { User = id, EntraObjectId = id };
    private static CallerContext Admin(string id) => new() { User = id, EntraObjectId = id, PlatformRoles = [PlatformRoles.PlatformAdmin] };
    private static ClaimsPrincipal HumanPrincipal() => new(new ClaimsIdentity([new Claim("oid", "user")], "test"));

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static string Query(string url, string name) => Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Single(x => x.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1]);

    private sealed class StubHttpClientFactory(string? response) : IHttpClientFactory
    {
        public int ProviderGrantRevocations { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(response ?? """{"access_token":"ghu_token"}""", this));

        private sealed class Handler(string body, StubHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(CreateResponse(request, owner, body));
        }

        private static HttpResponseMessage CreateResponse(HttpRequestMessage request, StubHttpClientFactory owner, string body)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/applications/", StringComparison.Ordinal))
                owner.ProviderGrantRevocations++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsolutePath switch
                {
                    var path when path.StartsWith("/apps/", StringComparison.Ordinal) =>
                        """{"permissions":{"metadata":"read"}}""",
                    "/user" => """{"login":"octocat"}""",
                    _ => body,
                }),
            };
        }
    }
}
