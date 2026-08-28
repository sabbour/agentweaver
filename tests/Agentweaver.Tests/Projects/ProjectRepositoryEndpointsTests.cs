using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Integration tests for GET /api/projects/{id}/github/repository-owners and
/// POST /api/projects/{id}/github/repository (issue: allow creating a GitHub repository for a
/// project that has none connected).
/// </summary>
public sealed class ProjectRepositoryEndpointsTests
{
    [Fact]
    public async Task ListRepositoryOwners_ReturnsOwnersFromClient()
    {
        using var factory = new RepositoryConnectWebApplicationFactory(
            new FakeGitHubRepositoryClient(
                [new GitHubRepositoryOwner("octo", true), new GitHubRepositoryOwner("octo-org", false)]));
        await SeedGitHubTokenAsync(factory);
        var client = factory.CreateAuthenticatedClient();

        var createResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Blank Project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        if (!createResponse.IsSuccessStatusCode)
            throw new Exception(await createResponse.Content.ReadAsStringAsync());
        var project = await createResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var response = await client.GetAsync($"/api/projects/{project!.ProjectId}/github/repository-owners");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var owners = await response.Content.ReadFromJsonAsync<RepositoryOwnerResponse[]>();
        owners.Should().HaveCount(2);
        owners![0].Login.Should().Be("octo");
        owners[0].Type.Should().Be("user");
        owners[1].Type.Should().Be("org");
    }

    [Fact]
    public async Task CreateRepository_ConnectsBlankProject()
    {
        var fakeClient = new FakeGitHubRepositoryClient([new GitHubRepositoryOwner("octo", true)]);
        using var factory = new RepositoryConnectWebApplicationFactory(fakeClient);
        await SeedGitHubTokenAsync(factory);
        var client = factory.CreateAuthenticatedClient();

        var createResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Blank Project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        createResponse.EnsureSuccessStatusCode();
        var project = await createResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project!.ProjectId}/github/repository",
            new { owner = "octo", name = "blank-project", @private = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var connected = await response.Content.ReadFromJsonAsync<ConnectedRepositoryResponse>();
        connected!.SourceRepository.Should().Be("octo/blank-project");
        connected.HtmlUrl.Should().Be("https://github.com/octo/blank-project");

        var getResponse = await client.GetAsync($"/api/projects/{project.ProjectId}");
        var updated = await getResponse.Content.ReadFromJsonAsync<ProjectResponse>();
        updated!.Origin.Should().Be("github");
        updated.SourceRepository.Should().Be("octo/blank-project");
    }

    [Fact]
    public async Task CreateRepository_ReturnsConflict_WhenProjectAlreadyConnected()
    {
        var fakeClient = new FakeGitHubRepositoryClient([new GitHubRepositoryOwner("octo", true)]);
        using var factory = new RepositoryConnectWebApplicationFactory(fakeClient);
        await SeedGitHubTokenAsync(factory);
        var client = factory.CreateAuthenticatedClient();

        var selection = await client.PostAsJsonAsync(
            "/api/github/repository-selections", new { full_name = "octo/already-connected" });
        selection.StatusCode.Should().Be(HttpStatusCode.OK);
        var selectionCode = (await selection.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("selection_code").GetString();

        var createResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "GH Project",
            origin = "github",
            working_directory = factory.NewWorkingDirectory(),
            repository_selection_code = selectionCode,
        });
        createResponse.EnsureSuccessStatusCode();
        var project = await createResponse.Content.ReadFromJsonAsync<ProjectResponse>();

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project!.ProjectId}/github/repository",
            new { owner = "octo", name = "whatever" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CreateRepository_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        using var factory = new RepositoryConnectWebApplicationFactory(new FakeGitHubRepositoryClient([]));
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{ProjectId.New()}/github/repository",
            new { owner = "octo", name = "whatever" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static Task SeedGitHubTokenAsync(RepositoryConnectWebApplicationFactory factory) =>
        factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("test-access-token", null, null, "test-login", null, []));

    private sealed class RepositoryConnectWebApplicationFactory : ProjectsWebApplicationFactory
    {
        private readonly FakeGitHubRepositoryClient _repoClient;
        private readonly HttpMessageHandler _selectionHandler = new RepositorySelectionHandler();

        public RepositoryConnectWebApplicationFactory(FakeGitHubRepositoryClient repoClient) => _repoClient = repoClient;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var existingRepoClient = services.FirstOrDefault(d => d.ServiceType == typeof(IGitHubRepositoryClient));
                if (existingRepoClient is not null) services.Remove(existingRepoClient);
                services.AddSingleton<IGitHubRepositoryClient>(_repoClient);
                services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
                    "github",
                    options => options.HttpMessageHandlerBuilderActions.Add(
                        build => build.PrimaryHandler = _selectionHandler));
            });
        }
    }

    private sealed class FakeGitHubRepositoryClient(IReadOnlyList<GitHubRepositoryOwner> owners) : IGitHubRepositoryClient
    {
        public Task<IReadOnlyList<GitHubRepositoryOwner>> ListRepositoryOwnersAsync(string accessToken, CancellationToken ct = default) =>
            Task.FromResult(owners);

        public Task<GitHubRepositoryResult> CreateRepositoryAsync(
            string owner, string name, bool isPrivate, string accessToken, CancellationToken ct = default)
        {
            var fullName = $"{owner}/{name}";
            return Task.FromResult(GitHubRepositoryResult.Ok(
                fullName, $"https://github.com/{fullName}", $"https://github.com/{fullName}.git", "main"));
        }
    }

    private sealed class RepositorySelectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":42,"full_name":"octo/already-connected","owner":{"login":"octo"},"private":true,"default_branch":"main"}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
