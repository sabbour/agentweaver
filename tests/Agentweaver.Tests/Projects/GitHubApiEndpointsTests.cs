using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Integration tests for GET /api/github/accounts and GET /api/github/repos.
/// Each test creates its own factory (using var) to keep the server alive for the
/// duration of the test and avoid disposal races.
/// </summary>
public sealed class GitHubApiEndpointsTests
{
    [Fact]
    public async Task SuggestBlueprint_ForCodeRepo_RecommendsSoftwareDevelopment()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/repos/octocat/hello-world"] =
                """{"name":"hello-world","description":"A TypeScript API service","topics":["api","service"],"has_issues":true}""",
            ["https://api.github.com/repos/octocat/hello-world/languages"] =
                """{"TypeScript":12500,"C#":8400}""",
            ["https://api.github.com/repos/octocat/hello-world/contents"] =
                """[{"name":"package.json"},{"name":"Dockerfile"},{"name":"src"}]""",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/blueprints/suggest", new { repository = "octocat/hello-world" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("fallback").GetBoolean().Should().BeFalse();
        body.GetProperty("recommended_blueprint").GetProperty("id").GetString()
            .Should().Be("blueprint-software-development");
        body.GetProperty("rationale").GetString().Should().Contain("software");
    }
}

// =============================================================================
// Test infrastructure
// =============================================================================

/// <summary>
/// Per-test WebApplicationFactory. Stubs IGitHubAccessTokenProvider and replaces
/// the "github" named HttpClient's primary handler with a caller-supplied UrlDispatchHandler.
/// </summary>
internal sealed class GitHubApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "gh-api-test-key-99999";
    public const string TestUser   = "accounts-test-user";

    private readonly UrlDispatchHandler _handler;
    private readonly string? _accessToken;
    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;

    public GitHubApiWebApplicationFactory(UrlDispatchHandler handler, string? accessToken)
    {
        var uid          = Guid.NewGuid().ToString("N");
        _handler         = handler;
        _accessToken     = accessToken;
        _dbPath          = Path.Combine(Path.GetTempPath(), $"agentweaver-gh-{uid}.db");
        _worktreesPath   = Path.Combine(Path.GetTempPath(), $"agentweaver-gh-wt-{uid}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-gh-cp-{uid}");
    }

    /// <summary>Creates an HttpClient with the test API key pre-set.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"]                        = _dbPath,
                ["Worktrees:BasePath"]                   = _worktreesPath,
                ["Checkpoints:Path"]                     = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"]         = Path.Combine(_checkpointsPath, "coord"),
                ["Testing:BypassGitHubOrgAuthorization"] = "true",
                ["Testing:BypassGitHubTokenAuth"]        = "true",
                ["Auth:Mode"]                            = "GitHubLegacy",
                ["Auth:ApiKey"]                          = TestApiKey,
                ["Auth:User"]                            = TestUser,
                ["Auth:GitHub:ClientId"]                 = "test-github-client-id",
                ["Auth:GitHub:BaseUrl"]                  = "https://github.com",
                ["Git:Author:Name"]                      = "Test",
                ["Git:Author:Email"]                     = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"]       = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"]     = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"]        = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"]    = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"]  = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"]= "gpt-4o",
                ["RunBounds:MaxSteps"]                   = "50",
                ["RunBounds:MaxMinutes"]                 = "10",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Stub IGitHubAccessTokenProvider — return configured token or null for 401 tests.
            var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IGitHubAccessTokenProvider));
            if (existing is not null) services.Remove(existing);
            services.AddSingleton<IGitHubAccessTokenProvider>(new StubAccessTokenProvider(_accessToken));

            // Replace the "github" named HttpClient so no real network calls are made.
            services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
                "github", options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = _handler);
                });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(p); } catch { }
        try { Directory.Delete(_worktreesPath, recursive: true); } catch { }
        try { Directory.Delete(_checkpointsPath, recursive: true); } catch { }
    }
}

/// <summary>Stub IGitHubAccessTokenProvider that returns a fixed token (or null).</summary>
internal sealed class StubAccessTokenProvider : IGitHubAccessTokenProvider
{
    private readonly string? _token;
    public StubAccessTokenProvider(string? token) => _token = token;
    public Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult(_token);
}

/// <summary>
/// Fake HttpMessageHandler dispatching by exact URL. Returns 200 + JSON for registered
/// URLs, 404 otherwise. Tracks all requested URLs for assertion.
/// </summary>
public sealed class UrlDispatchHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses;
    private readonly List<string> _requestedUrls = [];

    public UrlDispatchHandler(Dictionary<string, string>? responses = null)
        => _responses = responses ?? [];

    public IReadOnlyList<string> RequestedUrls => _requestedUrls;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        _requestedUrls.Add(url);

        if (_responses.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
