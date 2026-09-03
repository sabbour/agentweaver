using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

public sealed class PlatformDefaultCopilotBindingServiceTests
{
    private const string ConfiguredCallbackUrl = "https://agentweaver.test/auth/github/copilot-app/callback";

    [Fact]
    public async Task Begin_RequiresPlatformAdminAndPinsThePlatformDefaultPurpose()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore());

        (await service.BeginAsync(Human("member"), HumanPrincipal()))
            .Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired);
        (await service.BeginAsync(Admin("admin"), HumanPrincipal()))
            .Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired);

        var begin = await service.BeginAsync(Admin("admin"), HumanPrincipal(), AdminSession("admin").Id);
        begin.Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.Success);
        var stored = await db.GitHubAuthorizations.SingleAsync();
        stored.ProjectId.Should().BeNull();
        stored.AppKind.Should().Be(GitHubAppKind.Copilot);
        stored.Purpose.Should().Be(GitHubAuthorizationPurpose.PlatformDefaultCopilot);
        begin.AuthorizationUrl.Should().Contain("code_challenge_method=S256");
        Query(begin.AuthorizationUrl!, "redirect_uri")
            .Should().Be(ConfiguredCallbackUrl);
    }

    [Fact]
    public async Task CompleteBrowserCallback_WritesSingletonBindingAndReturnsPlatformSettingsRedirect()
    {
        await using var db = await OpenDatabaseAsync();
        var httpClientFactory = new StubHttpClientFactory("""{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        var service = CreateService(db, new InMemorySecretStore(), httpClientFactory: httpClientFactory);
        var session = AdminSession("platform-admin");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal(), session.Id);
        var state = Query(begin.AuthorizationUrl!, "state");

        (await service.CompleteBrowserCallbackAsync(session, state, "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);
        httpClientFactory.LastTokenRequestForm.Should().NotBeNull();
        httpClientFactory.LastTokenRequestForm!["redirect_uri"].Should().Be(ConfiguredCallbackUrl);

        var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
        binding.Id.Should().Be(PlatformDefaultCopilotBindingRecord.SingletonId);
        binding.Status.Should().Be(GitHubBindingStatus.Active);
        JsonSerializer.Serialize(binding).Should().NotContain("ghu_platform").And.NotContain("refresh-secret");
        (await service.GetCallbackRedirectAsync(PlatformDefaultCopilotBindingOutcome.Success))
            .Should().Be("http://localhost:5173/platform-settings?copilot_app_auth=success");
    }

    [Fact]
    public async Task CompleteBrowserCallback_RequiresAuthenticatedMatchingPlatformAdmin()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore(), """{"access_token":"ghu_platform"}""");
        var session = AdminSession("platform-admin");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal(), session.Id);
        var state = Query(begin.AuthorizationUrl!, "state");

        (await service.CompleteBrowserCallbackAsync(null, state, "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.HumanEntraSubjectRequired);
        (await service.CompleteBrowserCallbackAsync(AdminSession("other-admin"), state, "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.AuthorizationTransactionInvalid);
        (await service.CompleteBrowserCallbackAsync(
            new BrowserEntraSession
            {
                Id = session.Id,
                EntraObjectId = session.EntraObjectId,
                PlatformRoles = PlatformRoles.Contributor,
                ExpiresAt = session.ExpiresAt,
            },
            state,
            "code",
            begin.CallbackCookie)).Should().Be(PlatformDefaultCopilotBindingOutcome.PlatformAdminRequired);

        db.PlatformDefaultCopilotBindings.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteBrowserCallback_WhenCredentialReadBackCannotBeVerified_FailsWithoutCreatingAnActiveBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new DisappearingCredentialReadSecretStore("copilot-app-platform-default-");
        var service = CreateService(db, secrets, """{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        var session = AdminSession("platform-admin");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal(), session.Id);

        (await service.CompleteBrowserCallbackAsync(session, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable);

        db.ChangeTracker.Clear();
        (await db.PlatformDefaultCopilotBindings.CountAsync()).Should().Be(0);
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Failed);
    }

    [Fact]
    public async Task CompleteBrowserCallback_WhenPersistenceFailsAfterSecretWrite_LogsCredentialCleanupFailure()
    {
        await using var db = await OpenDatabaseAsync();
        var logger = new CapturingLogger();
        var secrets = new FailingPlatformDefaultCommitSecretStore(db);
        var service = CreateService(
            db,
            secrets,
            """{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""",
            logger: new TypedLoggerAdapter<PlatformDefaultCopilotBindingService>(logger));
        var session = AdminSession("platform-admin");
        var begin = await service.BeginAsync(Admin("platform-admin"), HumanPrincipal(), session.Id);

        (await service.CompleteBrowserCallbackAsync(session, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable);

        db.ChangeTracker.Clear();
        (await db.PlatformDefaultCopilotBindings.CountAsync()).Should().Be(0);
        logger.HasEntryMatching(LogLevel.Error, "failed to remove credential secret").Should().BeTrue();
    }

    [Fact]
    public async Task ConnectionStatus_ReturnsVerifiedGitHubLoginWithoutCredentialMaterial()
    {
        await using var db = await OpenDatabaseAsync();
        var service = CreateService(db, new InMemorySecretStore(), """{"access_token":"ghu_platform","refresh_token":"refresh-secret"}""");
        var begin = await service.BeginAsync(
            Admin("platform-admin"),
            HumanPrincipal(),
            AdminSession("platform-admin").Id);

        (await service.CompleteBrowserCallbackAsync(AdminSession("platform-admin"), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
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
    public async Task Disconnect_DoesNotRevokeATokenThatIsStillUsedByAProjectBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var httpClientFactory = new StubHttpClientFactory();
        db.Projects.Add(new ProjectRecord { ProjectId = "project" });
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
        await new GitHubConnectionsPersistenceStore(db).ReplaceCopilotBindingAsync(new ProjectCopilotBindingRecord
        {
            Id = "project-binding",
            ProjectId = "project",
            EntraObjectId = "owner",
            CredentialReference = "copilot-app-project-project-version-two",
            CredentialVersion = "version-two",
            GrantDigest = "digest-project",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await secrets.SetSecretAsync("copilot-app-platform-default-existing", """{"Status":"signed-in","AccessToken":"ghu_shared","GitHubLogin":"octocat"}""");
        await secrets.SetSecretAsync("copilot-app-project-project-version-two", """{"Status":"signed-in","AccessToken":"ghu_shared","GitHubLogin":"octocat"}""");
        await db.SaveChangesAsync();
        var service = CreateService(db, secrets, httpClientFactory: httpClientFactory);

        (await service.DisconnectAsync(Admin("platform-admin"), HumanPrincipal()))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        httpClientFactory.ProviderGrantRevocations.Should().Be(0);
        (await secrets.GetSecretAsync("copilot-app-project-project-version-two")).Value.Should().Contain("ghu_shared");
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
        var begin = await service.BeginAsync(
            Admin("platform-admin"),
            HumanPrincipal(),
            AdminSession("platform-admin").Id);

        (await service.CompleteBrowserCallbackAsync(AdminSession("platform-admin"), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        db.ChangeTracker.Clear();
        var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
        binding.CredentialReference.Should().NotBe("copilot-app-platform-default-old");
        (await secrets.GetSecretAsync("copilot-app-platform-default-old")).Should().Be(SecretGetResult.NotFound);
    }

    [Fact]
    public async Task CompleteBrowserCallback_RevokesOnlyTheRemovedTokenWhenTheSameGitHubLoginReconnects()
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
        await secrets.SetSecretAsync("copilot-app-platform-default-old", """{"Status":"signed-in","AccessToken":"ghu_old","RefreshToken":"refresh-old","GitHubLogin":"octocat"}""");
        var service = CreateService(db, secrets, httpClientFactory: httpClientFactory);
        var begin = await service.BeginAsync(
            Admin("platform-admin"),
            HumanPrincipal(),
            AdminSession("platform-admin").Id);

        (await service.CompleteBrowserCallbackAsync(AdminSession("platform-admin"), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        httpClientFactory.ProviderGrantRevocations.Should().Be(1);
        (await secrets.GetSecretAsync("copilot-app-platform-default-old")).Should().Be(SecretGetResult.NotFound);
    }

    [Fact]
    public async Task CompleteBrowserCallback_DoesNotRevokeTheActiveBindingWhenGitHubReturnsTheSameAccessToken()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var httpClientFactory = new StubHttpClientFactory("""{"access_token":"ghu_same","refresh_token":"refresh-secret"}""");
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
        await secrets.SetSecretAsync("copilot-app-platform-default-old", """{"Status":"signed-in","AccessToken":"ghu_same","RefreshToken":"refresh-old","GitHubLogin":"octocat"}""");
        var service = CreateService(db, secrets, httpClientFactory: httpClientFactory);
        var begin = await service.BeginAsync(
            Admin("platform-admin"),
            HumanPrincipal(),
            AdminSession("platform-admin").Id);

        (await service.CompleteBrowserCallbackAsync(AdminSession("platform-admin"), Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(PlatformDefaultCopilotBindingOutcome.Success);

        httpClientFactory.ProviderGrantRevocations.Should().Be(0);
        var binding = await db.PlatformDefaultCopilotBindings.SingleAsync();
        var activeSecret = await secrets.GetSecretAsync(binding.CredentialReference);
        activeSecret.Found.Should().BeTrue();
        activeSecret.Value.Should().Contain("ghu_same");
        (await secrets.GetSecretAsync("copilot-app-platform-default-old")).Should().Be(SecretGetResult.NotFound);
    }

    private static PlatformDefaultCopilotBindingService CreateService(
        MemoryDbContext db,
        ISecretStore secrets,
        string? provider = null,
        StubHttpClientFactory? httpClientFactory = null,
        ILogger<PlatformDefaultCopilotBindingService>? logger = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:CopilotApp:ClientId"] = "copilot-client",
            ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
            ["Auth:CopilotApp:CallbackUrl"] = ConfiguredCallbackUrl,
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
            logger ?? NullLogger<PlatformDefaultCopilotBindingService>.Instance);
    }

    private static CallerContext Human(string id) => new() { User = id, EntraObjectId = id };
    private static CallerContext Admin(string id) => new() { User = id, EntraObjectId = id, PlatformRoles = [PlatformRoles.PlatformAdmin] };
    private static ClaimsPrincipal HumanPrincipal() => new(new ClaimsIdentity([new Claim("oid", "user")], "test"));
    private static BrowserEntraSession AdminSession(string id) => new()
    {
        Id = $"session-{id}",
        EntraObjectId = id,
        PlatformRoles = PlatformRoles.PlatformAdmin,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static string Query(string url, string name) => Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Single(x => x.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1]);

    private sealed class TypedLoggerAdapter<TCategory>(CapturingLogger inner) : ILogger<TCategory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class DisappearingCredentialReadSecretStore(string credentialPrefix) : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        private readonly HashSet<string> _pendingMissingReads = new(StringComparer.Ordinal);

        public async Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default)
        {
            if (_pendingMissingReads.Remove(key))
                return SecretGetResult.NotFound;

            return await _inner.GetSecretAsync(key, ct).ConfigureAwait(false);
        }

        public async Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default)
        {
            var written = await _inner.SetSecretAsync(key, value, etag, ct).ConfigureAwait(false);
            if (key.StartsWith(credentialPrefix, StringComparison.Ordinal) &&
                !string.Equals(value, """{"status":"revoked"}""", StringComparison.Ordinal))
            {
                _pendingMissingReads.Add(key);
            }

            return written;
        }

        public Task DeleteSecretAsync(string key, CancellationToken ct = default) => _inner.DeleteSecretAsync(key, ct);
    }

    private sealed class FailingPlatformDefaultCommitSecretStore(MemoryDbContext db) : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();

        public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            _inner.GetSecretAsync(key, ct);

        public async Task<string> SetSecretAsync(string key, string value, string? etag = null, CancellationToken ct = default)
        {
            if (key.StartsWith("copilot-app-platform-default-", StringComparison.Ordinal) &&
                string.Equals(value, """{"status":"revoked"}""", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated credential cleanup failure.");
            }

            var written = await _inner.SetSecretAsync(key, value, etag, ct).ConfigureAwait(false);
            if (key.StartsWith("copilot-app-platform-default-", StringComparison.Ordinal))
            {
                await db.GitHubAuthorizations
                    .Where(x => x.Status == GitHubAuthorizationStatus.Redeeming)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(x => x.Status, GitHubAuthorizationStatus.Pending),
                        ct)
                    .ConfigureAwait(false);
            }

            return written;
        }

        public Task DeleteSecretAsync(string key, CancellationToken ct = default) => _inner.DeleteSecretAsync(key, ct);
    }

    private sealed class StubHttpClientFactory(string? response = null) : IHttpClientFactory
    {
        public int ProviderGrantRevocations { get; private set; }
        public IReadOnlyDictionary<string, string>? LastTokenRequestForm { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(response ?? """{"access_token":"ghu_token"}""", this));

        private sealed class Handler(string body, StubHttpClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.AbsolutePath == "/login/oauth/access_token")
                {
                    var content = await request.Content!.ReadAsStringAsync(ct);
                    owner.LastTokenRequestForm = content
                        .Split('&')
                        .Select(field => field.Split('=', 2))
                        .ToDictionary(
                            field => WebUtility.UrlDecode(field[0]),
                            field => WebUtility.UrlDecode(field[1]));
                }

                return CreateResponse(request, owner, body);
            }
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
