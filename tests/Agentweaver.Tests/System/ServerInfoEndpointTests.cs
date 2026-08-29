using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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
        using var factory = new ServerInfoWebApplicationFactory("Entra");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/server/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("auth_mode").GetString().Should().Be("entra");
        body.GetProperty("auth_mode_label").GetString().Should().Be("Entra ID");
        body.GetProperty("auth_mode_recommended").GetBoolean().Should().BeTrue();
        body.TryGetProperty("data_directory", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetServerInfo_WithBogusBearerToken_StillSucceeds()
    {
        using var factory = new ServerInfoWebApplicationFactory("Entra");
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
file sealed class ServerInfoWebApplicationFactory(string authMode) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"aw-si-{Guid.NewGuid():N}.db"),
                ["Worktrees:BasePath"] = Path.Combine(Path.GetTempPath(), $"aw-si-wt-{Guid.NewGuid():N}"),
                ["Checkpoints:Path"] = Path.Combine(Path.GetTempPath(), $"aw-si-cp-{Guid.NewGuid():N}"),
                ["Coordinator:Checkpoints:Path"] = Path.Combine(Path.GetTempPath(), $"aw-si-ccp-{Guid.NewGuid():N}"),
                ["Auth:Mode"] = authMode,
                ["Auth:Entra:TenantId"] = "72f988bf-86f1-41af-91ab-2d7cd011db47",
                ["Auth:Entra:ClientId"] = "11111111-2222-3333-4444-555555555555",
                ["Auth:ApiKey"] = "server-info-test-key",
                ["Auth:User"] = "server-info-test-user",
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"] = "50",
                ["RunBounds:MaxMinutes"] = "10",
            });
        });
    }
}
