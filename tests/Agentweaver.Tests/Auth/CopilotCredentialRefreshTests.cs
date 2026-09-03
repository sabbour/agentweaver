using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// GitHub App user-to-server access tokens expire after about eight hours. Both Copilot bindings
/// persist the refresh token GitHub returns with them, so an expired access token must be redeemed
/// from storage instead of pushing the operator back through the OAuth flow.
/// </summary>
public sealed class CopilotCredentialRefreshTests
{
    private const string PlatformCredentialReference = "copilot-app-platform-default-refresh";

    [Fact]
    public async Task ExpiredPlatformCredential_IsRedeemedFromTheStoredRefreshTokenAndStaysUsable()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory();
        var service = CreatePlatformService(db, secrets, provider);

        (await service.RefreshCredentialAsync()).Should().Be(CopilotCredentialRefreshOutcome.Refreshed);

        provider.TokenRequests.Should().Be(1);
        provider.LastTokenRequestForm!["grant_type"].Should().Be("refresh_token");
        provider.LastTokenRequestForm["refresh_token"].Should().Be("refresh-old");
        var stored = await secrets.GetSecretAsync(PlatformCredentialReference);
        GitHubCapabilityBroker.TryGetUsableCopilotCredential(stored.Value, DateTimeOffset.UtcNow, out var credential)
            .Should().BeTrue("the redeemed credential must be usable without re-authentication");
        credential.AccessToken.Should().Be("ghu_refreshed");
        credential.GitHubLogin.Should().Be("octocat");
        stored.Value.Should().Contain("refresh-rotated", "the rotated refresh token must replace the redeemed one");
    }

    [Fact]
    public async Task NearExpiryPlatformCredential_IsRedeemedAheadOfExpiryButAHealthyOneIsLeftAlone()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddHours(4));
        var provider = new StubHttpClientFactory();
        var service = CreatePlatformService(db, secrets, provider);

        (await service.RefreshCredentialAsync()).Should().Be(CopilotCredentialRefreshOutcome.NotNeeded);
        provider.TokenRequests.Should().Be(0);

        await WriteCredentialAsync(
            secrets,
            PlatformCredentialReference,
            "ghu_old",
            "refresh-old",
            DateTimeOffset.UtcNow.Add(CopilotCredentialRefreshService.RefreshAheadWindow).AddSeconds(-30));

        (await service.RefreshCredentialAsync()).Should().Be(CopilotCredentialRefreshOutcome.Refreshed);
        provider.TokenRequests.Should().Be(1);
        (await secrets.GetSecretAsync(PlatformCredentialReference)).Value.Should().Contain("ghu_refreshed");
    }

    [Fact]
    public async Task RejectedRefreshToken_MarksThePlatformBindingAsNeedingReauthentication()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory { TokenResponseBody = """{"error":"bad_refresh_token"}""" };
        var service = CreatePlatformService(db, secrets, provider);

        (await service.RefreshCredentialAsync()).Should().Be(CopilotCredentialRefreshOutcome.ReauthRequired);

        var stored = await secrets.GetSecretAsync(PlatformCredentialReference);
        stored.Value.Should().Contain(CopilotCredentialRefreshService.ReauthRequiredStatus)
            .And.NotContain("ghu_old").And.NotContain("refresh-old");
        GitHubCapabilityBroker.TryGetUsableCopilotCredential(stored.Value, DateTimeOffset.UtcNow, out _)
            .Should().BeFalse();
        (await service.GetConnectionAsync(Admin("platform-admin"), HumanPrincipal()))
            .Outcome.Should().Be(PlatformDefaultCopilotBindingOutcome.GitHubBindingUnavailable);
    }

    [Fact]
    public async Task TransientProviderFailure_LeavesTheStoredCredentialUntouched()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory { TokenStatusCode = HttpStatusCode.InternalServerError };
        var service = CreatePlatformService(db, secrets, provider);

        (await service.RefreshCredentialAsync()).Should().Be(CopilotCredentialRefreshOutcome.Unavailable);

        var stored = await secrets.GetSecretAsync(PlatformCredentialReference);
        stored.Value.Should().Contain("refresh-old")
            .And.NotContain(CopilotCredentialRefreshService.ReauthRequiredStatus,
                "a transient provider failure must never force the operator to re-authenticate");
    }

    [Fact]
    public async Task ConcurrentRedemptions_ExchangeTheRefreshTokenOnlyOnce()
    {
        var secrets = new InMemorySecretStore();
        await WriteCredentialAsync(
            secrets, PlatformCredentialReference, "ghu_old", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory();
        var refresh = new CopilotCredentialRefreshService(
            Configuration(), secrets, provider, NullLogger<CopilotCredentialRefreshService>.Instance);
        var now = DateTimeOffset.UtcNow;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => refresh.EnsureFreshAsync(PlatformCredentialReference, now))));

        provider.TokenRequests.Should().Be(1);
        outcomes.Should().ContainSingle(outcome => outcome == CopilotCredentialRefreshOutcome.Refreshed);
        outcomes.Should().OnlyContain(outcome =>
            outcome == CopilotCredentialRefreshOutcome.Refreshed ||
            outcome == CopilotCredentialRefreshOutcome.NotNeeded);
        (await secrets.GetSecretAsync(PlatformCredentialReference)).Value.Should().Contain("ghu_refreshed");
    }

    [Fact]
    public async Task ProjectBinding_RedeemsItsOwnRefreshTokenAndSurfacesReauthWhenGitHubRejectsIt()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        var project = ProjectId.New();
        var reference = $"copilot-app-project-{project}-version";
        db.Projects.Add(new ProjectRecord { ProjectId = project.ToString() });
        await db.SaveChangesAsync();
        await new GitHubConnectionsPersistenceStore(db).ReplaceCopilotBindingAsync(new ProjectCopilotBindingRecord
        {
            Id = "project-binding",
            ProjectId = project.ToString(),
            EntraObjectId = "owner",
            CredentialReference = reference,
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await WriteCredentialAsync(secrets, reference, "ghu_old", "refresh-old", DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory();
        var service = CreateProjectService(db, secrets, provider);

        (await service.RefreshCredentialAsync(project)).Should().Be(CopilotCredentialRefreshOutcome.Refreshed);
        (await secrets.GetSecretAsync(reference)).Value.Should().Contain("ghu_refreshed");

        await WriteCredentialAsync(
            secrets, reference, "ghu_refreshed", "refresh-rotated", DateTimeOffset.UtcNow.AddMinutes(-1));
        provider.TokenResponseBody = """{"error":"bad_refresh_token"}""";

        (await service.RefreshCredentialAsync(project)).Should().Be(CopilotCredentialRefreshOutcome.ReauthRequired);
        (await secrets.GetSecretAsync(reference)).Value.Should()
            .Contain(CopilotCredentialRefreshService.ReauthRequiredStatus).And.NotContain("ghu_refreshed");
    }

    [Fact]
    public async Task UnattendedCopilotLaunch_RecoversFromAnExpiredCredentialWithoutReauthentication()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory();
        var persistence = new GitHubConnectionsPersistenceStore(db);

        (await CreateLifecycle(db, persistence, secrets, provider)
            .PrepareForUnattendedCopilotLaunchAsync(PlatformScopedRun(), CancellationToken.None, platformScoped: true))
            .Should().BeTrue("a stored refresh token must be redeemed instead of failing the launch");
        provider.TokenRequests.Should().Be(1);
        (await secrets.GetSecretAsync(PlatformCredentialReference)).Value.Should().Contain("ghu_refreshed");
    }

    [Fact]
    public async Task UnattendedCopilotLaunch_StillFailsWhenTheRefreshTokenIsAlsoRejected()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory { TokenResponseBody = """{"error":"bad_refresh_token"}""" };
        var persistence = new GitHubConnectionsPersistenceStore(db);

        (await CreateLifecycle(db, persistence, secrets, provider)
            .PrepareForUnattendedCopilotLaunchAsync(PlatformScopedRun(), CancellationToken.None, platformScoped: true))
            .Should().BeFalse();
        (await secrets.GetSecretAsync(PlatformCredentialReference)).Value.Should()
            .Contain(CopilotCredentialRefreshService.ReauthRequiredStatus);
    }

    [Fact]
    public async Task DependencyInjection_WiresTheRefreshServiceIntoTheCapabilityBroker()
    {
        await using var db = await OpenDatabaseAsync();
        var secrets = new InMemorySecretStore();
        await SeedPlatformBindingAsync(db, secrets, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var provider = new StubHttpClientFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton(Configuration());
        services.AddSingleton<ISecretStore>(secrets);
        services.AddSingleton<IHttpClientFactory>(provider);
        services.AddSingleton<GitHubConnectionsPersistenceStore>();
        services.AddSingleton<IGitHubConnectionsCredentialVault, GitHubConnectionsCredentialVault>();
        services.AddSingleton<RepoAppInstallationTokenService>();
        services.AddSingleton<CopilotCredentialRefreshService>(sp => new(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<CopilotCredentialRefreshService>>()));
        services.AddSingleton<GitHubCapabilityBroker>();
        services.AddSingleton<RunGitHubCapabilitySnapshotLifecycle>();
        await using var container = services.BuildServiceProvider();

        (await container.GetRequiredService<RunGitHubCapabilitySnapshotLifecycle>()
            .PrepareForUnattendedCopilotLaunchAsync(PlatformScopedRun(), CancellationToken.None, platformScoped: true))
            .Should().BeTrue();
        provider.TokenRequests.Should().Be(1, "the container must hand the broker a real refresh service");
    }

    private static RunGitHubCapabilitySnapshotLifecycle CreateLifecycle(
        MemoryDbContext db,
        GitHubConnectionsPersistenceStore persistence,
        ISecretStore secrets,
        IHttpClientFactory httpClientFactory) =>
        new(persistence, new GitHubCapabilityBroker(
            persistence,
            new GitHubConnectionsCredentialVault(secrets),
            new RepoAppInstallationTokenService(Configuration(), db, secrets, httpClientFactory),
            new CopilotCredentialRefreshService(
                Configuration(), secrets, httpClientFactory, NullLogger<CopilotCredentialRefreshService>.Instance)));

    private static PlatformDefaultCopilotBindingService CreatePlatformService(
        MemoryDbContext db,
        ISecretStore secrets,
        IHttpClientFactory httpClientFactory)
    {
        var configuration = Configuration();
        return new(
            configuration,
            new GitHubConnectionsPersistenceStore(db),
            secrets,
            new GitHubConnectionsCredentialVault(secrets),
            httpClientFactory,
            new CopilotAppRegistrationService(configuration, httpClientFactory),
            NullLogger<PlatformDefaultCopilotBindingService>.Instance);
    }

    private static ProjectCopilotBindingService CreateProjectService(
        MemoryDbContext db,
        ISecretStore secrets,
        IHttpClientFactory httpClientFactory)
    {
        var configuration = Configuration();
        return new(
            configuration,
            new GitHubConnectionsPersistenceStore(db),
            secrets,
            httpClientFactory,
            new NoRoles(),
            new CopilotAppRegistrationService(configuration, httpClientFactory),
            NullLogger<ProjectCopilotBindingService>.Instance);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:CopilotApp:ClientId"] = "copilot-client",
            ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
            ["Auth:CopilotApp:CallbackUrl"] = "https://agentweaver.test/auth/github/copilot-app/callback",
            ["Auth:CopilotApp:FrontendUrl"] = "http://localhost:5173",
            ["Auth:CopilotApp:Slug"] = "agentweaver-copilot",
        }).Build();

    private static async Task SeedPlatformBindingAsync(
        MemoryDbContext db,
        ISecretStore secrets,
        DateTimeOffset expiresAt)
    {
        await new GitHubConnectionsPersistenceStore(db).ReplacePlatformDefaultCopilotBindingAsync(
            new PlatformDefaultCopilotBindingRecord
            {
                Id = PlatformDefaultCopilotBindingRecord.SingletonId,
                EntraObjectId = "platform-admin",
                CredentialReference = PlatformCredentialReference,
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
        await WriteCredentialAsync(secrets, PlatformCredentialReference, "ghu_old", "refresh-old", expiresAt);
    }

    private static async Task WriteCredentialAsync(
        ISecretStore secrets,
        string reference,
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt) =>
        await secrets.SetSecretAsync(reference, JsonSerializer.Serialize(new
        {
            Status = "signed-in",
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            GitHubLogin = "octocat",
            ExpiresAt = expiresAt,
        }));

    private static Run PlatformScopedRun() => new()
    {
        Id = RunId.New(),
        RepositoryPath = "repository",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "unattended copilot launch",
        SubmittingUser = "entra",
        Status = RunStatus.Pending,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = null,
    };

    private static CallerContext Admin(string id) =>
        new() { User = id, EntraObjectId = id, PlatformRoles = [PlatformRoles.PlatformAdmin] };

    private static ClaimsPrincipal HumanPrincipal() =>
        new(new ClaimsIdentity([new Claim("oid", "user")], "test"));

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class NoRoles : IProjectRoleAssignmentStore
    {
        public Task<ProjectRoleAssignment?> GetAsync(ProjectId p, string s, CancellationToken ct = default) =>
            Task.FromResult<ProjectRoleAssignment?>(null);
        public Task UpsertAsync(ProjectRoleAssignment a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId p, string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId p, string s, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly object _lock = new();

        public string TokenResponseBody { get; set; } =
            """{"access_token":"ghu_refreshed","refresh_token":"refresh-rotated","expires_in":28800}""";
        public HttpStatusCode TokenStatusCode { get; set; } = HttpStatusCode.OK;
        public int TokenRequests { get; private set; }
        public IReadOnlyDictionary<string, string>? LastTokenRequestForm { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this));

        private void RecordTokenRequest(IReadOnlyDictionary<string, string> form)
        {
            lock (_lock)
            {
                TokenRequests++;
                LastTokenRequestForm = form;
            }
        }

        private sealed class Handler(StubHttpClientFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri!.AbsolutePath == "/login/oauth/access_token")
                {
                    var content = await request.Content!.ReadAsStringAsync(ct);
                    owner.RecordTokenRequest(content
                        .Split('&')
                        .Select(field => field.Split('=', 2))
                        .ToDictionary(
                            field => WebUtility.UrlDecode(field[0]),
                            field => WebUtility.UrlDecode(field[1])));
                    return new HttpResponseMessage(owner.TokenStatusCode)
                    {
                        Content = new StringContent(owner.TokenResponseBody),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri.AbsolutePath switch
                    {
                        var path when path.StartsWith("/apps/", StringComparison.Ordinal) =>
                            """{"permissions":{"metadata":"read"}}""",
                        "/user" => """{"login":"octocat"}""",
                        _ => "{}",
                    }),
                };
            }
        }
    }
}
