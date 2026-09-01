using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubRepositorySelectionBrokerTests
{
    [Fact]
    public async Task IssueAndConsume_BindsTheOpaqueCodeToOneCallerRepositoryAndSingleUse()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, Repositories(42));

        var issued = await broker.IssueAsync("entra-one", "octo/secure-repo", CancellationToken.None);

        issued.Outcome.Should().Be(GitHubRepositorySelectionOutcome.Issued);
        issued.Code.Should().HaveLength(43);
        issued.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        await using (var inspect = new MemoryDbContext(options))
        {
            var persisted = await inspect.GitHubRepositorySelectionCodes.SingleAsync();
            persisted.CodeHash.Should().NotBe(issued.Code);
            JsonSerializer.Serialize(persisted).Should().NotContain(issued.Code!);
            persisted.EntraObjectId.Should().Be("entra-one");
            persisted.RepoAppAuthorizationId.Should().NotBeNullOrWhiteSpace();
            persisted.RepositoryId.Should().Be(42);
        }

        var wrongSubject = await CreateBroker(options, secrets, Repositories(42))
            .TryConsumeAsync(issued.Code!, "entra-two", CancellationToken.None);
        wrongSubject.Should().BeNull();

        var first = await CreateBroker(options, secrets, Repositories(42))
            .TryConsumeAsync(issued.Code!, "entra-one", CancellationToken.None);
        first.Should().BeEquivalentTo(new
        {
            EntraObjectId = "entra-one",
            RepositoryId = 42L,
        });

        var second = await CreateBroker(options, secrets, Repositories(42))
            .TryConsumeAsync(issued.Code!, "entra-one", CancellationToken.None);
        second.Should().BeNull();
    }

    [Fact]
    public async Task Issue_OnlyMintsForTheCallerAuthorizedBrowseResultAndFailsClosed()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, Repositories(42));

        var result = await broker.IssueAsync("entra-one", "octo/not-authorized", CancellationToken.None);

        result.Outcome.Should().Be(GitHubRepositorySelectionOutcome.GitHubCapabilityUnavailable);
        result.Code.Should().BeNull();
        await using var inspect = new MemoryDbContext(options);
        (await inspect.GitHubRepositorySelectionCodes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Consume_RejectsExpiredAndMalformedCodesWithoutDisclosingScope()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, Repositories(42));
        var issued = await broker.IssueAsync("entra-one", "octo/secure-repo", CancellationToken.None);

        await using (var expire = new MemoryDbContext(options))
        {
            var record = await expire.GitHubRepositorySelectionCodes.SingleAsync();
            record.ExpiresAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
            await expire.SaveChangesAsync();
        }

        (await broker.TryConsumeAsync(issued.Code!, "entra-one", CancellationToken.None)).Should().BeNull();
        (await broker.TryConsumeAsync("not-a-code", "entra-one", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Consume_RejectsAnUnconsumedCodeAfterItsIssuingAuthorizationIsRevoked()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, Repositories(42));
        var issued = await broker.IssueAsync("entra-one", "octo/secure-repo", CancellationToken.None);

        await using (var revoke = new MemoryDbContext(options))
        {
            var authorization = await revoke.GitHubAppAuthorizations.SingleAsync();
            authorization.RevokedAt = DateTimeOffset.UtcNow;
            await revoke.SaveChangesAsync();
        }

        (await broker.TryConsumeAsync(issued.Code!, "entra-one", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task List_ReturnsOnlySafeRepositoryMetadataAndNoBindingFailsClosed()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        var noBinding = await CreateBroker(options, secrets, Repositories(42))
            .ListAsync("entra-one", CancellationToken.None);
        noBinding.Outcome.Should().Be(GitHubRepositorySelectionOutcome.GitHubBindingUnavailable);
        noBinding.Candidates.Should().BeEmpty();

        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var listed = await CreateBroker(options, secrets, Repositories(42))
            .ListAsync("entra-one", CancellationToken.None);
        listed.Outcome.Should().Be(GitHubRepositorySelectionOutcome.Issued);
        listed.Candidates.Should().ContainSingle().Which.Should().BeEquivalentTo(new GitHubRepositorySelectionCandidate(
            42, "octo/secure-repo", "octo", true, "main", "https://github.com/octo/secure-repo.git", null));
    }

    [Fact]
    public async Task List_AcceptsTheCredentialSerializationProducedByRepoAppAuthorization()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await secrets.SetSecretAsync(
            "repo-app-user-credential-version",
            JsonSerializer.Serialize(new { Status = "signed-in", AccessToken = "test-token" }));
        await using (var db = new MemoryDbContext(options))
        {
            db.GitHubAppAuthorizations.Add(new GitHubAppAuthorizationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                EntraObjectId = "entra-one",
                AppKind = GitHubAppKind.Repo,
                Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
                CredentialReference = "repo-app-user-credential-version",
                CredentialVersion = "version",
                GrantDigest = "digest",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var listed = await CreateBroker(options, secrets, Repositories(42))
            .ListAsync("entra-one", CancellationToken.None);

        listed.Outcome.Should().Be(GitHubRepositorySelectionOutcome.Issued);
        listed.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task TryUseCredential_ListOwnersRecognizesInstallationScopedRepoAppAccess()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, RepositoriesAndInstallations(42));

        var owners = await broker.TryUseCredentialAsync(
            new CallerContext { User = "entra-one", EntraObjectId = "entra-one" },
            token => new GitHubRepositorySelectionClient(new StubHttpClientFactory(RepositoriesAndInstallations(42)))
                .ListOwnersAsync(token, CancellationToken.None),
            CancellationToken.None);

        owners.Outcome.Should().Be(GitHubRepositorySelectionOutcome.Issued);
        owners.Value.Should().ContainSingle().Which.Should().BeEquivalentTo(new GitHubRepositoryOwner("octo", true));
    }

    [Fact]
    public async Task List_ReturnsTransientErrorDistinctFromCapabilityUnavailableWhenTheLiveGitHubCallFails()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, new ThrowingHttpHandler());

        var result = await broker.ListAsync("entra-one", CancellationToken.None);

        result.Outcome.Should().Be(GitHubRepositorySelectionOutcome.GitHubCapabilityTransientError);
    }

    [Fact]
    public async Task TryUseCredential_ReturnsTransientErrorDistinctFromCapabilityUnavailableWhenTheLiveGitHubCallFails()
    {
        await using var connection = await OpenDatabaseAsync();
        var options = Options(connection);
        var secrets = new InMemorySecretStore();
        await SeedLiveAuthorizationAsync(options, secrets, "entra-one");
        var broker = CreateBroker(options, secrets, new ThrowingHttpHandler());

        var owners = await broker.TryUseCredentialAsync(
            new CallerContext { User = "entra-one", EntraObjectId = "entra-one" },
            token => new GitHubRepositorySelectionClient(new StubHttpClientFactory(new ThrowingHttpHandler()))
                .ListOwnersAsync(token, CancellationToken.None),
            CancellationToken.None);

        owners.Outcome.Should().Be(GitHubRepositorySelectionOutcome.GitHubCapabilityTransientError);
    }

    private static GitHubRepositorySelectionBroker CreateBroker(
        DbContextOptions<MemoryDbContext> options,
        InMemorySecretStore secrets,
        HttpMessageHandler handler) =>
        new(
            new GitHubConnectionsPersistenceStore(new MemoryDbContext(options)),
            new GitHubConnectionsCredentialVault(secrets),
            new GitHubRepositorySelectionClient(new StubHttpClientFactory(handler)));

    private static async Task SeedLiveAuthorizationAsync(
        DbContextOptions<MemoryDbContext> options,
        InMemorySecretStore secrets,
        string subject)
    {
        await secrets.SetSecretAsync(
            "repo-app-user-credential-version",
            """{"status":"signed-in","accessToken":"test-token"}""");
        await using var db = new MemoryDbContext(options);
        db.GitHubAppAuthorizations.Add(new GitHubAppAuthorizationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            EntraObjectId = subject,
            AppKind = GitHubAppKind.Repo,
            Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
            CredentialReference = "repo-app-user-credential-version",
            CredentialVersion = "version",
            GrantDigest = "digest",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static HttpMessageHandler Repositories(long id) => RepositoriesAndInstallations(id);

    private static HttpMessageHandler RepositoriesAndInstallations(long id) => new RouteHttpHandler(request =>
        request.RequestUri!.AbsolutePath switch
        {
            "/user/installations" => """{"installations":[{"id":72,"account":{"login":"octo"},"target_type":"User","repositories_url":"https://api.github.com/user/installations/72/repositories","permissions":{"administration":"write"}}]}""",
            "/user/installations/72/repositories" => $$"""{"repositories":[{"id":{{id}},"full_name":"octo/secure-repo","owner":{"login":"octo"},"private":true,"default_branch":"main","clone_url":"https://github.com/octo/secure-repo.git"}]}""",
            _ => "{}",
        });

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MemoryDbContext(Options(connection));
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static DbContextOptions<MemoryDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RouteHttpHandler(Func<HttpRequestMessage, string> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route(request), Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Simulates a transient network failure (timeout, DNS, connection reset) reaching GitHub.</summary>
    private sealed class ThrowingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated transient network failure reaching GitHub.");
    }

}
