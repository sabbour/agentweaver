using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Agentweaver.Tests.Webhooks;

public sealed class GitHubWebhookProvisioningEndpointsTests
{
    [Fact]
    public async Task ProvisionWebhook_CreatesSignedWebhookAndPersistsSecret()
    {
        var handler = new RecordingGitHubHooksHandler(
            listResponse: "[]",
            mutateStatus: HttpStatusCode.Created,
            mutateResponse: """{"id":42,"config":{"url":"http://localhost/api/projects/ignored/webhooks/github","content_type":"json","insecure_ssl":"0"}}""");
        using var factory = new WebhookProvisioningWebApplicationFactory(handler, "github-token");
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client, "https://github.com/Octocat/Hello-World.git");

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/webhooks/github/provision",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubWebhookProvisioningResponse>();
        body.Should().BeEquivalentTo(new GitHubWebhookProvisioningResponse(
            42,
            Created: true,
            "octocat/hello-world",
            $"http://localhost/api/projects/{projectId}/webhooks/github"));

        handler.Requests.Select(r => (r.Method, r.Url)).Should().Equal(
            ("GET", "https://api.github.com/repos/octocat/hello-world/hooks?per_page=100"),
            ("POST", "https://api.github.com/repos/octocat/hello-world/hooks"));
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer github-token");

        var createPayload = JsonDocument.Parse(handler.Requests[1].Body!);
        createPayload.RootElement.GetProperty("name").GetString().Should().Be("web");
        createPayload.RootElement.GetProperty("active").GetBoolean().Should().BeTrue();
        createPayload.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetString())
            .Should().Contain(["issues", "issue_comment", "pull_request", "push"]);
        createPayload.RootElement.GetProperty("config").GetProperty("url").GetString()
            .Should().Be($"http://localhost/api/projects/{projectId}/webhooks/github");
        createPayload.RootElement.GetProperty("config").GetProperty("content_type").GetString()
            .Should().Be("json");

        using var scope = factory.Services.CreateScope();
        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var project = await projectStore.GetAsync(ProjectId.Parse(projectId));
        project!.WebhookSecret.Should().NotBeNullOrWhiteSpace();
        var storedSecret = await secretStore.GetSecretAsync(project.WebhookSecret!);
        storedSecret.Found.Should().BeTrue();
        createPayload.RootElement.GetProperty("config").GetProperty("secret").GetString()
            .Should().Be(storedSecret.Value);
    }

    [Fact]
    public async Task ProvisionWebhook_UpdatesMatchingWebhookInsteadOfCreatingDuplicate()
    {
        using var factory = new WebhookProvisioningWebApplicationFactory(
            handler: null,
            accessToken: "github-token");
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client, "https://github.com/octocat/hello-world");
        var payloadUrl = $"http://localhost/api/projects/{projectId}/webhooks/github";
        factory.Handler.ListResponse = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = 77,
                config = new { url = payloadUrl, content_type = "json", insecure_ssl = "0" },
            },
        });
        factory.Handler.MutateStatus = HttpStatusCode.OK;
        factory.Handler.MutateResponse = JsonSerializer.Serialize(new
        {
            id = 77,
            config = new { url = payloadUrl, content_type = "json", insecure_ssl = "0" },
        });

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/webhooks/github/provision",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubWebhookProvisioningResponse>();
        body!.HookId.Should().Be(77);
        body.Created.Should().BeFalse();
        factory.Handler.Requests.Select(r => r.Method).Should().Equal("GET", "PATCH");
        factory.Handler.Requests[1].Url.Should().EndWith("/hooks/77");
        JsonDocument.Parse(factory.Handler.Requests[1].Body!).RootElement
            .TryGetProperty("name", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionWebhook_RejectsProjectWithoutGitHubRepository()
    {
        using var factory = new WebhookProvisioningWebApplicationFactory(
            new RecordingGitHubHooksHandler(),
            "github-token");
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client, sourceRepository: null);

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/webhooks/github/provision",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionWebhook_ReturnsUnauthorizedWhenGitHubTokenIsMissing()
    {
        using var factory = new WebhookProvisioningWebApplicationFactory(
            new RecordingGitHubHooksHandler(),
            accessToken: null);
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client, "https://github.com/octocat/hello-world");

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/webhooks/github/provision",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factory.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProvisionWebhook_MapsMissingHookPermissionToActionableForbidden()
    {
        var handler = new RecordingGitHubHooksHandler(
            listStatus: HttpStatusCode.Forbidden,
            listResponse: """{"message":"Resource not accessible by integration"}""");
        using var factory = new WebhookProvisioningWebApplicationFactory(handler, "github-token");
        var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client, "https://github.com/octocat/hello-world");

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/webhooks/github/provision",
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("write:repo_hook");
    }

    private static async Task<string> CreateProjectAsync(
        ProjectsWebApplicationFactory factory,
        HttpClient client,
        string? sourceRepository)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Webhook project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        response.EnsureSuccessStatusCode();
        var projectId = (await response.Content.ReadFromJsonAsync<ProjectResponse>())!.ProjectId;
        if (sourceRepository is not null)
        {
            using var scope = factory.Services.CreateScope();
            var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();
            await projectStore.UpdateOriginAsync(
                ProjectId.Parse(projectId),
                ProjectOrigin.FromGitHub(sourceRepository),
                DateTimeOffset.UtcNow);
        }
        return projectId;
    }

    private sealed class WebhookProvisioningWebApplicationFactory : ProjectsWebApplicationFactory
    {
        private readonly string? _accessToken;

        public WebhookProvisioningWebApplicationFactory(
            RecordingGitHubHooksHandler? handler,
            string? accessToken)
        {
            Handler = handler ?? new RecordingGitHubHooksHandler();
            _accessToken = accessToken;
        }

        public RecordingGitHubHooksHandler Handler { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var tokenProvider = services.FirstOrDefault(
                    d => d.ServiceType == typeof(IGitHubAccessTokenProvider));
                if (tokenProvider is not null) services.Remove(tokenProvider);
                services.AddSingleton<IGitHubAccessTokenProvider>(
                    new StubAccessTokenProvider(_accessToken));

                services.Configure<HttpClientFactoryOptions>("github", options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(
                        b => b.PrimaryHandler = Handler);
                });
            });
        }
    }

    private sealed class StubAccessTokenProvider(string? token) : IGitHubAccessTokenProvider
    {
        public Task<string?> GetValidAccessTokenAsync(
            GitHubTokenScope scope,
            CancellationToken ct = default) => Task.FromResult(token);
    }

    private sealed record CapturedRequest(
        string Method,
        string Url,
        string? Authorization,
        string? Body);

    private sealed class RecordingGitHubHooksHandler(
        HttpStatusCode listStatus = HttpStatusCode.OK,
        string listResponse = "[]",
        HttpStatusCode mutateStatus = HttpStatusCode.Created,
        string mutateResponse = """{"id":42}""") : HttpMessageHandler
    {
        public HttpStatusCode ListStatus { get; set; } = listStatus;
        public string ListResponse { get; set; } = listResponse;
        public HttpStatusCode MutateStatus { get; set; } = mutateStatus;
        public string MutateResponse { get; set; } = mutateResponse;
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                body));

            var isList = request.Method == HttpMethod.Get;
            return new HttpResponseMessage(isList ? ListStatus : MutateStatus)
            {
                Content = new StringContent(
                    isList ? ListResponse : MutateResponse,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
