using System.Net;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Webhooks;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubRepositorySelectionClientTests
{
    [Fact]
    public async Task List_UsesUserTokenForInstallationsAndInstallationTokenForRepositoryPages()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var handler = new RecordingRouteHandler();
        var httpClientFactory = new StubHttpClientFactory(handler);
        var client = new GitHubRepositorySelectionClient(
            httpClientFactory,
            new RepoAppInstallationTokenService(Config(), db, secrets, httpClientFactory));

        var repositories = await client.ListAsync("user-oauth-token", CancellationToken.None);

        repositories.Should().ContainSingle().Which.Should().BeEquivalentTo(new GitHubRepositorySelectionCandidate(
            42,
            "octo/secure-repo",
            "octo",
            true,
            "main",
            "https://github.com/octo/secure-repo.git",
            null));
        handler.Requests.Select(x => x.Path).Should().Equal(
            "/user/installations",
            "/app/installations/72/access_tokens",
            "/installation/repositories");
        handler.Requests[0].Authorization.Should().Be("Bearer user-oauth-token");
        handler.Requests[1].Authorization.Should().StartWith("Bearer ").And.NotBe("Bearer user-oauth-token");
        handler.Requests[1].Body.Should().Contain("\"metadata\":\"read\"").And.NotContain("repository_ids");
        handler.Requests[2].Authorization.Should().Be("Bearer ghs_installation_token");
    }

    [Fact]
    public async Task ListOwners_UsesOnlyTheUserTokenForAccessibleInstallations()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        var handler = new RecordingRouteHandler();
        var httpClientFactory = new StubHttpClientFactory(handler);
        var client = new GitHubRepositorySelectionClient(
            httpClientFactory,
            new RepoAppInstallationTokenService(Config(), db, secrets, httpClientFactory));

        var owners = await client.ListOwnersAsync("user-oauth-token", CancellationToken.None);

        owners.Should().ContainSingle().Which.Should().BeEquivalentTo(new GitHubRepositoryOwner("octo", true));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/user/installations");
        handler.Requests[0].Authorization.Should().Be("Bearer user-oauth-token");
    }

    [Fact]
    public async Task List_PreservesProviderRepositoriesUrlForInstallationRepositoryPages()
    {
        await using var db = await OpenDbAsync();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
        var handler = new RecordingRouteHandler(
            "https://ghe.example.com/api/v3/installation/repositories",
            "/api/v3/installation/repositories");
        var httpClientFactory = new StubHttpClientFactory(handler);
        var client = new GitHubRepositorySelectionClient(
            httpClientFactory,
            new RepoAppInstallationTokenService(Config(), db, secrets, httpClientFactory));

        var repositories = await client.ListAsync("user-oauth-token", CancellationToken.None);

        repositories.Should().ContainSingle();
        handler.Requests.Select(x => x.Path).Should().ContainInOrder(
            "/user/installations",
            "/app/installations/72/access_tokens",
            "/api/v3/installation/repositories");
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
        return db;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingRouteHandler(
        string repositoriesUrl = "https://api.github.com/installation/repositories",
        string installationRepositoriesPath = "/installation/repositories") : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));

            var response = request.RequestUri!.AbsolutePath switch
            {
                "/user/installations" => CreateResponse(HttpStatusCode.OK,
                    "{\"installations\":[{\"id\":72,\"account\":{\"login\":\"octo\"},\"target_type\":\"User\",\"repositories_url\":\"" + repositoriesUrl + "\",\"permissions\":{\"administration\":\"write\"}}]}"),
                "/app/installations/72/access_tokens" => CreateResponse(HttpStatusCode.Created,
                    """{"token":"ghs_installation_token","expires_at":"2030-01-01T00:00:00Z"}"""),
                var path when path == installationRepositoriesPath => CreateResponse(HttpStatusCode.OK,
                    """{"repositories":[{"id":42,"full_name":"octo/secure-repo","owner":{"login":"octo"},"private":true,"default_branch":"main","clone_url":"https://github.com/octo/secure-repo.git"}]}"""),
                _ => CreateResponse(HttpStatusCode.NotFound, "{}"),
            };

            return response;
        }

        private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body) =>
            new(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    private sealed record RecordedRequest(string Path, string? Authorization, string? Body);
}
