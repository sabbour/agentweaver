using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.SystemServerInfo;

/// <summary>
/// Regression tests for GET /api/server/info — the endpoint the web app calls BEFORE sign-in to
/// decide whether to render the Entra or GitHub sign-in button. It must be reachable with no
/// Authorization header (it was previously 401'd by the bearer-token middleware's hardcoded
/// allowlist despite AllowAnonymous) and must surface the configured auth mode, otherwise the
/// frontend silently falls back to 'github-legacy' on an Entra deployment.
/// </summary>
public sealed class ServerInfoEndpointTests
{
    [Fact]
    public async Task GetServerInfo_InEntraMode_IsAnonymousAndReportsEntra()
    {
        using var factory = new ServerInfoWebApplicationFactory("Entra", "agentweaver-repo");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/server/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("auth_mode").GetString().Should().Be("entra");
        body.GetProperty("auth_mode_label").GetString().Should().Be("Entra ID");
        body.GetProperty("auth_mode_recommended").GetBoolean().Should().BeTrue();
        body.TryGetProperty("data_directory", out _).Should().BeTrue();
        body.GetProperty("repo_app_install_url").GetString().Should().Be("https://github.com/apps/agentweaver-repo/installations/new");
    }

    [Fact]
    public async Task GetServerInfo_WithBogusBearerToken_StillSucceeds()
    {
        using var factory = new ServerInfoWebApplicationFactory("Entra", null);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/server/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// Factory that runs the real auth pipeline (no <c>Testing:BypassGitHubTokenAuth</c>), so an
/// unauthenticated request genuinely exercises the middleware allowlists.
/// </summary>
file sealed class ServerInfoWebApplicationFactory(string authMode, string? repoAppSlug)
    : ApiWebApplicationFactory("aw-si")
{
    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Auth:Mode"] = authMode;
        configuration["Auth:Entra:TenantId"] = "72f988bf-86f1-41af-91ab-2d7cd011db47";
        configuration["Auth:Entra:ClientId"] = "11111111-2222-3333-4444-555555555555";
        configuration["Auth:ApiKey"] = "server-info-test-key";
        configuration["Auth:RepoApp:Slug"] = repoAppSlug;
        configuration["Auth:User"] = "server-info-test-user";
    }
}
