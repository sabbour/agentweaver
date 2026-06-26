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
    // =========================================================================
    // GA-01: GET /api/github/accounts — returns user first, then orgs
    // =========================================================================
    [Fact]
    public async Task GetAccounts_ReturnsUserFirstThenOrgs()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] =
                """{"login":"sabbour","name":"Ahmed Sabbour","avatar_url":"https://avatars.example.com/sabbour"}""",
            ["https://api.github.com/user/orgs?per_page=100&page=1"] =
                """[{"login":"myorg","avatar_url":"https://avatars.example.com/myorg"},{"login":"anotherorg","avatar_url":"https://avatars.example.com/anotherorg"}]""",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var accounts = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        accounts.Should().HaveCount(3);

        accounts![0].GetProperty("login").GetString().Should().Be("sabbour");
        accounts[0].GetProperty("name").GetString().Should().Be("Ahmed Sabbour");
        accounts[0].GetProperty("avatar_url").GetString().Should().Be("https://avatars.example.com/sabbour");
        accounts[0].GetProperty("type").GetString().Should().Be("user");

        accounts[1].GetProperty("login").GetString().Should().Be("myorg");
        accounts[1].GetProperty("type").GetString().Should().Be("org");
        accounts[2].GetProperty("login").GetString().Should().Be("anotherorg");
        accounts[2].GetProperty("type").GetString().Should().Be("org");
    }

    // =========================================================================
    // GA-02: GET /api/github/accounts — user with no orgs returns single entry
    // =========================================================================
    [Fact]
    public async Task GetAccounts_NoOrgs_ReturnsOnlyUser()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] =
                """{"login":"solo","name":"Solo User","avatar_url":"https://avatars.example.com/solo"}""",
            ["https://api.github.com/user/orgs?per_page=100&page=1"] = "[]",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var accounts = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        accounts.Should().HaveCount(1);
        accounts![0].GetProperty("type").GetString().Should().Be("user");
        accounts[0].GetProperty("login").GetString().Should().Be("solo");
    }

    // =========================================================================
    // GA-03: GET /api/github/accounts — org name falls back to login
    // =========================================================================
    [Fact]
    public async Task GetAccounts_OrgWithNoName_UsesLoginAsName()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user"] =
                """{"login":"user","name":"User","avatar_url":"https://avatars.example.com/u"}""",
            ["https://api.github.com/user/orgs?per_page=100&page=1"] =
                """[{"login":"nameless-org","avatar_url":"https://avatars.example.com/no"}]""",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/accounts");
        var accounts = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        accounts![1].GetProperty("login").GetString().Should().Be("nameless-org");
        accounts[1].GetProperty("name").GetString().Should().Be("nameless-org",
            because: "org name falls back to login when GitHub does not return a display name");
    }

    // =========================================================================
    // GA-04: GET /api/github/accounts — 401 when no access token
    // =========================================================================
    [Fact]
    public async Task GetAccounts_NoToken_Returns401()
    {
        using var factory = new GitHubApiWebApplicationFactory(new UrlDispatchHandler(), accessToken: null);
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =========================================================================
    // GR-01: GET /api/github/repos (no account param) — calls /user/repos path
    // =========================================================================
    [Fact]
    public async Task GetRepos_NoAccountParam_CallsUserReposPath()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user/repos?sort=pushed&per_page=100&page=1&affiliation=owner"] =
                """[{"full_name":"sabbour/repo-a","description":"Repo A","private":false,"default_branch":"main"}]""",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/repos");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repos = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        repos.Should().HaveCount(1);
        handler.RequestedUrls.Should().Contain(u =>
            u.Contains("/user/repos") && u.Contains("affiliation=owner"),
            because: "no account param must use the /user/repos affiliation=owner path");
    }

    // =========================================================================
    // GR-02: GET /api/github/repos?account=<own-login> — stays on /user/repos
    // =========================================================================
    [Fact]
    public async Task GetRepos_AccountIsOwnLogin_CallsUserReposPath()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/user/repos?sort=pushed&per_page=100&page=1&affiliation=owner"] =
                """[{"full_name":"sabbour/repo-b","description":null,"private":true,"default_branch":"develop"}]""",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        // TestUser = "accounts-test-user" (the caller.User from factory config).
        // Passing own login as ?account must still route to /user/repos, not /orgs/.
        var response = await client.GetAsync($"/api/github/repos?account={GitHubApiWebApplicationFactory.TestUser}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestedUrls.Should().Contain(u => u.Contains("/user/repos"));
        handler.RequestedUrls.Should().NotContain(u => u.Contains("/orgs/"));
    }

    // =========================================================================
    // GR-03: GET /api/github/repos?account=<org> — calls /orgs/{org}/repos path
    // =========================================================================
    [Fact]
    public async Task GetRepos_AccountIsOrg_CallsOrgReposPath()
    {
        var handler = new UrlDispatchHandler(new Dictionary<string, string>
        {
            ["https://api.github.com/orgs/myorg/repos?sort=pushed&per_page=100&page=1&type=all"] =
                """[{"full_name":"myorg/org-repo","description":"Org repo","private":false,"default_branch":"main"}]""",
        });
        using var factory = new GitHubApiWebApplicationFactory(handler, "fake-github-token");
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/repos?account=myorg");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repos = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        repos.Should().HaveCount(1);
        handler.RequestedUrls.Should().Contain(u => u.Contains("/orgs/myorg/repos"));
        handler.RequestedUrls.Should().NotContain(u => u.Contains("/user/repos"));
    }

    // =========================================================================
    // GR-04: GET /api/github/repos — 401 when no access token
    // =========================================================================
    [Fact]
    public async Task GetRepos_NoToken_Returns401()
    {
        using var factory = new GitHubApiWebApplicationFactory(new UrlDispatchHandler(), accessToken: null);
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/github/repos");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
