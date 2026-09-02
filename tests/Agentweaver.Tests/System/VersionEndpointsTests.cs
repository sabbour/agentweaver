using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.SystemVersion;

/// <summary>
/// Integration tests for GET /api/version, guarding the version-badge-provenance fix
/// end-to-end through the actual HTTP endpoint (complementing the unit-level
/// <see cref="AppVersionProviderTests"/>). Explicitly exercises BOTH deploy paths:
/// a real `npm run azure:release` build (IMAGE_TAG is a semver tag) and a git-SHA-tagged
/// `azure:upgrade`/`azure:deploy-from-local` build (IMAGE_TAG is a short commit hash).
/// </summary>
public sealed class VersionEndpointsTests
{
    [Fact]
    public async Task GetVersion_ForRealReleaseBuild_ReturnsSemverAndBuildGitSha()
    {
        // Simulates `npm run azure:release` having tagged and deployed v0.9.71 —
        // IMAGE_TAG is a real semver tag, matching the VERSION file bumped by that release.
        var stub = new StubAppVersionProvider(version: "0.9.71", gitSha: "a1c11f1", isRelease: true);
        using var factory = new VersionWebApplicationFactory(stub);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("version").GetString().Should().Be("0.9.71");
        body.GetProperty("gitSha").GetString().Should().Be("a1c11f1");
        body.GetProperty("isRelease").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetVersion_ForShaTaggedLocalUpgradeBuild_ReturnsBaseVersionPlusGitShaAndIsReleaseFalse()
    {
        // Simulates `azure:upgrade`/`azure:deploy-from-local` — IMAGE_TAG is a short git SHA,
        // never a semver tag, so AppVersionProvider falls back to the VERSION file's base
        // semver and surfaces the actual deployed commit SHA separately.
        var stub = new StubAppVersionProvider(version: "0.9.71", gitSha: "a1c11f1", isRelease: false);
        using var factory = new VersionWebApplicationFactory(stub);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("version").GetString().Should().Be("0.9.71");
        body.GetProperty("gitSha").GetString().Should().Be("a1c11f1");
        body.GetProperty("isRelease").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetVersion_WireFormat_UsesCamelCaseFieldNames()
    {
        var stub = new StubAppVersionProvider(version: "0.9.70", gitSha: null, isRelease: true);
        using var factory = new VersionWebApplicationFactory(stub);
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/version");
        var json = await response.Content.ReadAsStringAsync();

        // Exact wire names — the frontend (useAppVersion.ts) contract depends on these.
        json.Should().Contain("\"version\"");
        json.Should().Contain("\"gitSha\"");
        json.Should().Contain("\"isRelease\"");
    }
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal factory that replaces <see cref="IAppVersionProvider"/> with a stub so both
/// release and SHA-tagged branches can be tested without touching real environment
/// variables (avoids flakiness from process-wide IMAGE_TAG/GIT_SHA mutation across
/// parallel test classes).
/// </summary>
file sealed class VersionWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "version-endpoint-test-key";
    public const string TestUser = "version-endpoint-test-user";

    private readonly IAppVersionProvider _versionProvider;

    public VersionWebApplicationFactory(IAppVersionProvider versionProvider)
    {
        _versionProvider = versionProvider;
    }

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
                ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"aw-ver-{Guid.NewGuid():N}.db"),
                ["Worktrees:BasePath"] = Path.Combine(Path.GetTempPath(), $"aw-ver-wt-{Guid.NewGuid():N}"),
                ["Checkpoints:Path"] = Path.Combine(Path.GetTempPath(), $"aw-ver-cp-{Guid.NewGuid():N}"),
                ["Coordinator:Checkpoints:Path"] = Path.Combine(Path.GetTempPath(), $"aw-ver-ccp-{Guid.NewGuid():N}"),
                ["Testing:BypassGitHubOrgAuthorization"] = "true",
                ["Testing:BypassGitHubTokenAuth"] = "true",
                ["Auth:Mode"] = "GitHubLegacy",
                ["Auth:ApiKey"] = TestApiKey,
                ["Auth:User"] = TestUser,
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

        builder.ConfigureServices(services =>
        {
            // Replace the real (env-var/VERSION-file-driven) provider with a stub.
            services.AddSingleton<IAppVersionProvider>(_versionProvider);
        });
    }
}

file sealed class StubAppVersionProvider(string version, string? gitSha, bool isRelease) : IAppVersionProvider
{
    public string Version => version;
    public string? GitSha => gitSha;
    public bool IsRelease => isRelease;
}
