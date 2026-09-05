using System.Net;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubRepositorySelectionClientTests
{
    [Fact]
    public async Task List_UsesUserTokenForInstallationsAndRepositoryPages()
    {
        var handler = Handler(
            Installations("""
                {"id":72,"account":{"login":"octo"},"target_type":"User",
                 "repository_selection":"selected",
                 "html_url":"https://github.com/settings/installations/72",
                 "permissions":{"administration":"write"}}
                """),
            ("/user/installations/72/repositories", Repositories(42, "octo/secure-repo")));
        var client = Client(handler);

        var repositories = await client.ListAsync("user-oauth-token", CancellationToken.None);

        repositories.Should().ContainSingle().Which.Should().BeEquivalentTo(new GitHubRepositorySelectionCandidate(
            42,
            "octo/secure-repo",
            "octo",
            true,
            "main",
            "https://github.com/octo/secure-repo",
            "https://github.com/octo/secure-repo.git",
            null));
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/user/installations",
            "/user/installations/72/repositories");
        var authorizations = handler.Requests.Select(request => request.Authorization).ToList();
        authorizations.Should()
            .OnlyContain(authorization => authorization!.StartsWith("Bearer ", StringComparison.Ordinal));
        authorizations.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Browse_ReturnsSafePersonalAndOrganizationInstallationMetadata()
    {
        var handler = Handler(
            Installations(
                """
                {"id":73,"account":{"login":"example-org"},"target_type":"Organization",
                 "repository_selection":"all",
                 "html_url":"https://github.com/organizations/example-org/settings/installations/73",
                 "permissions":{"contents":"read"}}
                """,
                """
                {"id":72,"account":{"login":"octo"},"target_type":"User",
                 "repository_selection":"selected",
                 "html_url":"https://github.com/settings/installations/72",
                 "permissions":{"administration":"write"}}
                """),
            ("/user/installations/72/repositories", Repositories(42, "octo/secure-repo")),
            ("/user/installations/73/repositories", Repositories(84, "example-org/service")));
        var client = Client(handler);

        var result = await client.BrowseAsync("user-oauth-token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Repositories.Should().HaveCount(2);
        result.Installations.Should().Equal(
            new GitHubRepositoryInstallationMetadata(
                "octo", "user", "selected", "https://github.com/settings/installations/72"),
            new GitHubRepositoryInstallationMetadata(
                "example-org",
                "organization",
                "all",
                "https://github.com/organizations/example-org/settings/installations/73"));
    }

    [Fact]
    public async Task Browse_AppliesOneGlobalRepositoryLimitAcrossInstallations()
    {
        var handler = new PagingRouteHandler(Installations(
            """
            {"id":72,"account":{"login":"octo"},"target_type":"User",
             "repository_selection":"selected",
             "html_url":"https://github.com/settings/installations/72","permissions":{}}
            """,
            """
            {"id":73,"account":{"login":"example-org"},"target_type":"Organization",
             "repository_selection":"all",
             "html_url":"https://github.com/organizations/example-org/settings/installations/73","permissions":{}}
            """));
        var client = Client(handler);

        var result = await client.BrowseAsync("user-oauth-token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Repositories.Should().HaveCount(200);
        result.Repositories.Select(repository => repository.RepositoryId)
            .Should().OnlyHaveUniqueItems();
        handler.Requests.Should().Equal(
            "/user/installations?per_page=100&page=1",
            "/user/installations/72/repositories?per_page=100&page=1",
            "/user/installations/72/repositories?per_page=100&page=2",
            "/user/installations/73/repositories?per_page=100&page=1");
    }

    [Fact]
    public async Task Browse_EmptyInstallationsReturnsEmptyLists()
    {
        var client = Client(Handler(Installations()));

        var result = await client.BrowseAsync("user-oauth-token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Repositories.Should().BeEmpty();
        result.Installations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://github.com/settings/installations/72")]
    [InlineData("https://evil.example/settings/installations/72")]
    [InlineData("https://user@github.com/settings/installations/72")]
    [InlineData("https://github.com/settings/installations/72#fragment")]
    public async Task Browse_RejectsUnsafeInstallationManagementUrl(string managementUrl)
    {
        var installation =
            """{"id":72,"account":{"login":"octo"},"target_type":"User","repository_selection":"selected","html_url":""" +
            JsonSerializer.Serialize(managementUrl) +
            ""","permissions":{}}""";
        var handler = Handler(Installations(installation));
        var client = Client(handler);

        var result = await client.BrowseAsync("user-oauth-token", CancellationToken.None);

        result.Should().BeNull();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Browse_UsesConfiguredGitHubEnterpriseOrigins()
    {
        var handler = Handler(
            Installations("""
                {"id":72,"account":{"login":"octo"},"target_type":"User",
                 "repository_selection":"all",
                 "html_url":"https://ghe.example.com/settings/installations/72",
                 "permissions":{"administration":"write"}}
                """),
            ("/api/v3/user/installations/72/repositories", Repositories(42, "octo/secure-repo")));
        var client = Client(handler, new Dictionary<string, string?>
        {
            ["Auth:RepoApp:BaseUrl"] = "https://ghe.example.com",
            ["Auth:RepoApp:ApiUrl"] = "https://ghe.example.com/api/v3",
        });

        var result = await client.BrowseAsync("user-oauth-token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Installations.Should().ContainSingle().Which.ManagementUrl
            .Should().Be("https://ghe.example.com/settings/installations/72");
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/api/v3/user/installations",
            "/api/v3/user/installations/72/repositories");
    }

    [Fact]
    public async Task Browse_RejectsCloneUrlOutsideConfiguredGitHubOrigin()
    {
        var handler = Handler(
            Installations("""
                {"id":72,"account":{"login":"octo"},"target_type":"User",
                 "repository_selection":"all",
                 "html_url":"https://github.com/settings/installations/72","permissions":{}}
                """),
            ("/user/installations/72/repositories",
             """{"repositories":[{"id":42,"full_name":"octo/secure-repo","owner":{"login":"octo"},"private":true,"default_branch":"main","clone_url":"https://evil.example/octo/secure-repo.git"}]}"""));
        var client = Client(handler);

        var result = await client.BrowseAsync("user-oauth-token", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Repositories.Should().BeEmpty();
    }

    [Fact]
    public async Task ListOwners_UsesOnlyTheUserTokenForAccessibleInstallations()
    {
        var handler = Handler(Installations("""
            {"id":72,"account":{"login":"octo"},"target_type":"User",
             "repository_selection":"selected",
             "html_url":"https://github.com/settings/installations/72",
             "permissions":{"administration":"write"}}
            """));
        var client = Client(handler);

        var owners = await client.ListOwnersAsync("user-oauth-token", CancellationToken.None);

        owners.Should().ContainSingle().Which.Should().BeEquivalentTo(new GitHubRepositoryOwner("octo", true));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Authorization.Should().StartWith("Bearer ");
    }

    [Fact]
    public async Task Create_RejectsCloneUrlOutsideConfiguredGitHubOrigin()
    {
        var handler = Handler(
            Installations("""
                {"id":72,"account":{"login":"octo"},"target_type":"User",
                 "repository_selection":"all",
                 "html_url":"https://github.com/settings/installations/72",
                 "permissions":{"administration":"write"}}
                """),
            ("/user/repos", """{"full_name":"octo/new-repo","clone_url":"https://evil.example/octo/new-repo.git","html_url":"https://github.com/octo/new-repo"}"""));
        var client = Client(handler);

        var repository = await client.CreateAsync(
            "octo", "new-repo", true, "user-oauth-token", CancellationToken.None);

        repository.Should().BeNull();
    }

    private static GitHubRepositorySelectionClient Client(
        HttpMessageHandler handler,
        IReadOnlyDictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
        return new GitHubRepositorySelectionClient(new StubHttpClientFactory(handler), configuration);
    }

    private static RecordingRouteHandler Handler(
        string installations,
        params (string Path, string Body)[] responses)
    {
        var routes = responses.ToDictionary(response => response.Path, response => response.Body);
        routes["/user/installations"] = installations;
        if (responses.Any(response => response.Path.StartsWith("/api/v3/", StringComparison.Ordinal)))
        {
            routes["/api/v3/user/installations"] = installations;
            routes.Remove("/user/installations");
        }
        return new RecordingRouteHandler(routes);
    }

    private static string Installations(params string[] installations) =>
        $$"""{"installations":[{{string.Join(',', installations)}}]}""";

    private static string Repositories(long id, string fullName)
    {
        var owner = fullName.Split('/')[0];
        return $$"""
            {"repositories":[{"id":{{id}},"full_name":"{{fullName}}","owner":{"login":"{{owner}}"},
            "private":true,"default_branch":"main","clone_url":"https://github.com/{{fullName}}.git"}]}
            """;
    }

    private static string RepositoryRange(long firstId, int count, string owner) =>
        JsonSerializer.Serialize(new
        {
            repositories = Enumerable.Range(0, count).Select(offset =>
            {
                var id = firstId + offset;
                return new
                {
                    id,
                    full_name = $"{owner}/repo-{id}",
                    owner = new { login = owner },
                    @private = true,
                    default_branch = "main",
                    clone_url = $"https://github.com/{owner}/repo-{id}.git",
                };
            }),
        });

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingRouteHandler(IReadOnlyDictionary<string, string> routes) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(
                routes.ContainsKey(request.RequestUri.AbsolutePath)
                    ? HttpStatusCode.OK
                    : HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    routes.GetValueOrDefault(request.RequestUri.AbsolutePath, "{}"),
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class PagingRouteHandler(string installations) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            var route = request.RequestUri!.PathAndQuery;
            Requests.Add(route);
            var body = route switch
            {
                "/user/installations?per_page=100&page=1" => installations,
                "/user/installations/72/repositories?per_page=100&page=1" =>
                    RepositoryRange(1, 100, "octo"),
                "/user/installations/72/repositories?per_page=100&page=2" =>
                    RepositoryRange(101, 50, "octo"),
                "/user/installations/73/repositories?per_page=100&page=1" =>
                    RepositoryRange(151, 100, "example-org"),
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(
                body == "{}" ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record RecordedRequest(string Path, string? Authorization);
}
