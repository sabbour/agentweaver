using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Webhooks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentweaver.Tests.Webhooks;

public sealed class RepoAppInstallationServiceTests
{
    [Fact]
    public async Task Mint_UsesKeyVaultPem_AndSingleRepositoryPermissionDownscope_WithoutPersistingCredentials()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var permissions = new Dictionary<string, string>
        {
            ["contents"] = "read",
            ["pull_requests"] = "write",
            ["administration"] = "write",
        };
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 72, AppKind = GitHubAppKind.Repo, ProjectId = null, CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 72, RepositoryId = 99, ProjectId = "project-id", FullNameDisplay = "renamed/repository",
            PermissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(permissions), GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingHandler(
            """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"READ","pull_requests":"write","administration":"write"}}""",
            """{"token":"ghs_metadata_token","expires_at":"2030-01-01T00:00:00Z"}""",
            """{"id":99,"full_name":"renamed/repository"}""",
            """{"token":"ghs_installation_token_should_not_persist","expires_at":"2030-01-01T00:00:00Z"}""");
        var service = new RepoAppInstallationTokenService(Config(), db, secrets, new StubHttpClientFactory(handler));
        string? consumed = null;

        var result = await service.MintForRepositoryAsync(72, 99, (token, _) =>
        {
            consumed = token;
            return Task.CompletedTask;
        });

        result.Should().Be(RepoAppInstallationOutcome.Success);
        consumed.Should().Be("ghs_installation_token_should_not_persist");
        handler.Authorization.Should().StartWith("Bearer eyJ");
        var appJwt = new JsonWebTokenHandler().ReadJsonWebToken(handler.Authorization!["Bearer ".Length..]);
        appJwt.Issuer.Should().Be("123");
        appJwt.Alg.Should().Be(SecurityAlgorithms.RsaSha256);
        appJwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(9), TimeSpan.FromMinutes(1));
        handler.Body.Should().Contain("\"repository_ids\":[99]")
            .And.Contain("\"contents\":\"read\"").And.Contain("\"pull_requests\":\"write\"")
            .And.NotContain("administration");
        handler.Bodies[1].Should().Contain("\"repository_ids\":[99]").And.Contain("\"metadata\":\"read\"");
        (await db.GitHubInstallations.SingleAsync()).ToString().Should().NotContain("ghs_");
        (await db.GitHubRepositoryGrants.SingleAsync()).PermissionDigest.Should().NotContain("ghs_");
    }

    [Fact]
    public async Task Mint_UsesProviderExpiryInsteadOfSynthesizingAnExpiry()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var permissions = new Dictionary<string, string> { ["contents"] = "read" };
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            InstallationId = 72, AppKind = GitHubAppKind.Repo, ProjectId = "project-id", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 72, RepositoryId = 99, ProjectId = "project-id", FullNameDisplay = "owner/repository",
            PermissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(permissions), GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var expectedExpiry = DateTimeOffset.UtcNow.AddMinutes(17);
        var service = new RepoAppInstallationTokenService(
            Config(), db, secrets, new StubHttpClientFactory(new RecordingHandler(
                """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"read"}}""",
                """{"token":"ghs_metadata","expires_at":"2030-01-01T00:00:00Z"}""",
                """{"id":99,"full_name":"owner/repository"}""",
                $$"""{"token":"ghs_transient","expires_at":"{{expectedExpiry:O}}"}""")));
        DateTimeOffset? receivedExpiry = null;

        (await service.MintForRepositoryAsync(72, 99, (_, expiry) =>
        {
            receivedExpiry = expiry;
            return Task.CompletedTask;
        })).Should().Be(RepoAppInstallationOutcome.Success);

        receivedExpiry.Should().BeCloseTo(expectedExpiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Mint_RejectsDifferentPermissionDigestOrRepositoryBeforeCallingGitHub()
    {
        await using var db = await OpenDbAsync();
        var permissions = new Dictionary<string, string> { ["contents"] = "read" };
        db.GitHubInstallations.Add(new GitHubInstallationRecord { InstallationId = 8, AppKind = GitHubAppKind.Repo, CreatedAt = DateTimeOffset.UtcNow });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 8, RepositoryId = 9, ProjectId = "project-id", FullNameDisplay = "display",
            PermissionDigest = RepoAppInstallationTokenService.CreatePermissionDigest(permissions), GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var handler = new RecordingHandler("""{"token":"ghs_unused"}""");
        var service = new RepoAppInstallationTokenService(Config(), db, new InMemorySecretStore(), new StubHttpClientFactory(handler));

        (await service.MintForRepositoryAsync(8, 10, (_, _) => Task.CompletedTask))
            .Should().Be(RepoAppInstallationOutcome.InstallationUnavailable);
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task Lifecycle_ClaimsDeliveriesOnce_AndRoutesOnlyNumericInstallationRepositoryGrant()
    {
        await using var db = await OpenDbAsync();
        db.GitHubInstallations.Add(new GitHubInstallationRecord { InstallationId = 21, AppKind = GitHubAppKind.Repo, ProjectId = "project-a", CreatedAt = DateTimeOffset.UtcNow });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 21, RepositoryId = 22, ProjectId = "project-a", FullNameDisplay = "before/rename",
            PermissionDigest = "digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var lifecycle = new RepoAppInstallationLifecycleService(db);
        var renamed = new GitHubWebhookPayload
        {
            Installation = new GitHubWebhookInstallation { Id = 21 },
            Repository = new GitHubWebhookRepository { Id = 22, FullName = "after/transfer" },
        };

        var first = await lifecycle.ProcessAsync("delivery-1", "push", renamed);
        var duplicate = await lifecycle.ProcessAsync("delivery-1", "push", renamed);
        var wrongRepository = await lifecycle.ProcessAsync("delivery-2", "push",
            renamed with { Repository = new GitHubWebhookRepository { Id = 23, FullName = "before/rename" } });

        first.Claimed.Should().BeTrue();
        first.ProjectIds.Should().Equal("project-a");
        (await lifecycle.IsCompletedAsync("delivery-1")).Should().BeFalse();
        (await lifecycle.CompleteAsync("delivery-1")).Should().BeTrue();
        (await lifecycle.IsCompletedAsync("delivery-1")).Should().BeTrue();
        duplicate.Claimed.Should().BeFalse();
        wrongRepository.ProjectIds.Should().BeEmpty();
        (await db.GitHubLifecycleDeliveries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Lifecycle_RevokesGrantOnRemovalWithoutTrustingFullName()
    {
        await using var db = await OpenDbAsync();
        db.GitHubInstallations.Add(new GitHubInstallationRecord { InstallationId = 2, AppKind = GitHubAppKind.Repo, ProjectId = "p", CreatedAt = DateTimeOffset.UtcNow });
        db.GitHubRepositoryGrants.Add(new GitHubRepositoryGrantRecord
        {
            InstallationId = 2, RepositoryId = 3, ProjectId = "p", FullNameDisplay = "safe/repo",
            PermissionDigest = "digest", GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await new RepoAppInstallationLifecycleService(db).ProcessAsync("removal", "installation_repositories",
            new GitHubWebhookPayload
            {
                Action = "removed",
                Installation = new GitHubWebhookInstallation { Id = 2 },
                RepositoriesRemoved = [new GitHubWebhookRepository { Id = 3, FullName = "attacker/renamed" }],
            });

        (await db.GitHubRepositoryGrants.SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task VerifyRepositoryInstallation_RequiresExactInstallationFromGitHub()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var service = new RepoAppInstallationTokenService(
            Config(), db, secrets, new StubHttpClientFactory(new RecordingHandler(
                """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"read"}}""",
                """{"token":"ghs_metadata_token"}""",
                """{"id":99,"full_name":"owner/repository"}""",
                """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"read"}}""")));

        (await service.VerifyRepositoryInstallationAsync(72, 99)).Should().BeTrue();
        (await service.VerifyRepositoryInstallationAsync(73, 99)).Should().BeFalse();
    }

    [Fact]
    public async Task Bind_RejectsCrossProjectReplacement()
    {
        await using var db = await OpenDbAsync();
        var lifecycle = new RepoAppInstallationLifecycleService(db);

        var authority = new RepoAppInstallationAuthority(
            7, 8, "owner/repo", new Dictionary<string, string> { ["contents"] = "read" });
        (await lifecycle.BindAsync("project-id", authority)).Should().Be(RepoAppInstallationBindingOutcome.Bound);
        (await lifecycle.BindAsync("project-a", authority)).Should().Be(RepoAppInstallationBindingOutcome.Conflict);
        (await db.GitHubRepositoryGrants.SingleAsync()).ProjectId.Should().Be("project-id");
    }

    [Fact]
    public async Task Authority_IsProviderDerivedAndRequiresTheCanonicalNumericRepository()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var service = new RepoAppInstallationTokenService(
            Config(), db, secrets, new StubHttpClientFactory(new RecordingHandler(
                """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"CONTENTS":" READ ","pull_requests":"WRITE"}}""",
                """{"token":"ghs_metadata_token"}""",
                """{"id":99,"full_name":"provider/repository"}""")));

        var authority = await service.GetRepositoryAuthorityAsync(72, 99);

        authority.Should().NotBeNull();
        authority!.InstallationId.Should().Be(72);
        authority.RepositoryId.Should().Be(99);
        authority.FullNameDisplay.Should().Be("provider/repository");
        authority.Permissions.Should().Equal(new Dictionary<string, string>
        {
            ["contents"] = "read",
            ["pull_requests"] = "write",
        });
    }

    [Fact]
    public async Task Authority_RejectsProviderRepositoryWithDifferentNumericIdentity()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var service = new RepoAppInstallationTokenService(
            Config(), db, secrets, new StubHttpClientFactory(new RecordingHandler(
                """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"read"}}""",
                """{"token":"ghs_metadata_token"}""",
                """{"id":100,"full_name":"provider/repository"}""")));

        (await service.GetRepositoryAuthorityAsync(72, 99)).Should().BeNull();
    }

    [Theory]
    [InlineData("read", "write")]
    [InlineData("write", "read")]
    public async Task Bind_InvalidatesUnattendedReadinessForAnyProviderPermissionChange(
        string priorPermission,
        string currentPermission)
    {
        await using var db = await OpenDbAsync();
        var lifecycle = new RepoAppInstallationLifecycleService(db);
        var prior = new RepoAppInstallationAuthority(
            72, 99, "provider/repository", new Dictionary<string, string> { ["contents"] = priorPermission });
        (await lifecycle.BindAsync("project-id", prior)).Should().Be(RepoAppInstallationBindingOutcome.Bound);
        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = Guid.NewGuid().ToString("N"), ProjectId = "project-id", InstallationId = 72, RepositoryId = 99,
            AutomationKey = "nightly", Status = AutomationActivationStatus.Active, ActivatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var current = prior with
        {
            Permissions = new Dictionary<string, string> { ["contents"] = currentPermission },
        };
        (await lifecycle.BindAsync("project-id", current)).Should().Be(RepoAppInstallationBindingOutcome.PermissionChanged);
        (await lifecycle.BindAsync("project-id", current)).Should().Be(RepoAppInstallationBindingOutcome.PermissionChanged);

        db.ChangeTracker.Clear();
        var grant = await db.GitHubRepositoryGrants.SingleAsync();
        grant.RevokedAt.Should().NotBeNull();
        grant.PermissionDigest.Should().Be(RepoAppInstallationTokenService.CreatePermissionDigest(prior.Permissions));
        var activation = await db.AutomationActivations.SingleAsync();
        activation.Status.Should().Be(AutomationActivationStatus.Invalidated);
        activation.InvalidatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Lifecycle_ReclaimsAnAbandonedDeliveryLease()
    {
        await using var db = await OpenDbAsync();
        db.GitHubLifecycleDeliveries.Add(new GitHubLifecycleDeliveryRecord
        {
            DeliveryId = "abandoned", EventName = "push", ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-11),
        });
        await db.SaveChangesAsync();

        var result = await new RepoAppInstallationLifecycleService(db).ProcessAsync(
            "abandoned", "push", new GitHubWebhookPayload());

        result.Claimed.Should().BeTrue();
        (await db.GitHubLifecycleDeliveries.SingleAsync()).ReceivedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Auth:RepoApp:AppId"] = "123",
        ["Auth:RepoApp:PrivateKeySecretName"] = "repo-app-pem",
        ["Auth:RepoApp:ApiUrl"] = "https://api.github.test",
    }).Build();

    private static async Task<MemoryDbContext> OpenDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Projects.Add(new ProjectRecord
        {
            ProjectId = "project-id", Name = "Project", OriginKind = "blank", WorkingDirectory = "C:\\project",
            Owner = "owner", DefaultProvider = "github_copilot", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Projects.Add(new ProjectRecord
        {
            ProjectId = "project-a", Name = "Project A", OriginKind = "blank", WorkingDirectory = "C:\\project-a",
            Owner = "owner", DefaultProvider = "github_copilot", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.Projects.Add(new ProjectRecord
        {
            ProjectId = "p", Name = "Project P", OriginKind = "blank", WorkingDirectory = "C:\\project-p",
            Owner = "owner", DefaultProvider = "github_copilot", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private sealed class StubHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class RecordingHandler(params string[] payloads) : HttpMessageHandler
    {
        private readonly Queue<string> _payloads = new(payloads);
        public int RequestCount { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(Body);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(_payloads.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
