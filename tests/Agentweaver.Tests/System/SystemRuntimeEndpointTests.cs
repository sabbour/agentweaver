using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.SystemRuntime;

/// <summary>
/// Integration tests for GET /api/system/runtime.
/// Tests both the "running in Kubernetes" and "not in Kubernetes" branches by swapping
/// <see cref="IKubernetesEnvironment"/> via the DI container.
/// </summary>
public sealed class SystemRuntimeEndpointTests
{
    // -------------------------------------------------------------------------
    // Not in Kubernetes (default — no KUBERNETES_SERVICE_HOST)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Runtime_WhenNotInKubernetes_ReturnsKubernetesFalseAndNullPodName()
    {
        using var factory = new SystemRuntimeWebApplicationFactory(isKubernetes: false, podName: null);
        using var client  = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/system/runtime");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("kubernetes").GetBoolean().Should().BeFalse();
        body.GetProperty("podName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // -------------------------------------------------------------------------
    // Running in Kubernetes
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Runtime_WhenInKubernetes_ReturnsKubernetesTrueAndPodName()
    {
        const string expectedPodName = "agentweaver-api-abc123-xyz";
        using var factory = new SystemRuntimeWebApplicationFactory(isKubernetes: true, podName: expectedPodName);
        using var client  = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/system/runtime");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("kubernetes").GetBoolean().Should().BeTrue();
        body.GetProperty("podName").GetString().Should().Be(expectedPodName);
    }

    // -------------------------------------------------------------------------
    // Wire-format field name assertions (contract pinning for Trinity)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Runtime_ResponseBodyContainsExactFieldNames()
    {
        using var factory = new SystemRuntimeWebApplicationFactory(isKubernetes: false, podName: null);
        using var client  = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/system/runtime");
        var json = await response.Content.ReadAsStringAsync();

        // Exact wire names — the frontend contract depends on these.
        json.Should().Contain("\"kubernetes\"");
        json.Should().Contain("\"podName\"");
        // Must NOT emit PascalCase variants.
        json.Should().NotContain("\"Kubernetes\"");
        json.Should().NotContain("\"PodName\"");
    }
}

// ---------------------------------------------------------------------------
// Factory
// ---------------------------------------------------------------------------

/// <summary>
/// Minimal factory that replaces <see cref="IKubernetesEnvironment"/> with a stub
/// so both detection branches can be tested without touching real environment variables.
/// </summary>
file sealed class SystemRuntimeWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "system-runtime-test-key";
    public const string TestUser   = "system-runtime-test-user";

    private readonly bool    _isKubernetes;
    private readonly string? _podName;

    public SystemRuntimeWebApplicationFactory(bool isKubernetes, string? podName)
    {
        _isKubernetes = isKubernetes;
        _podName      = podName;
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
                ["Database:Path"]                         = Path.Combine(Path.GetTempPath(), $"aw-sysrt-{Guid.NewGuid():N}.db"),
                ["Worktrees:BasePath"]                    = Path.Combine(Path.GetTempPath(), $"aw-sysrt-wt-{Guid.NewGuid():N}"),
                ["Checkpoints:Path"]                      = Path.Combine(Path.GetTempPath(), $"aw-sysrt-cp-{Guid.NewGuid():N}"),
                ["Coordinator:Checkpoints:Path"]          = Path.Combine(Path.GetTempPath(), $"aw-sysrt-ccp-{Guid.NewGuid():N}"),
                ["Testing:BypassGitHubOrgAuthorization"]  = "true",
                ["Testing:BypassGitHubTokenAuth"]         = "true",
                ["Auth:ApiKey"]                           = TestApiKey,
                ["Auth:User"]                             = TestUser,
                ["Auth:GitHub:ClientId"]                  = "test-github-client-id",
                ["Auth:GitHub:BaseUrl"]                   = "https://github.com",
                ["Git:Author:Name"]                       = "Test",
                ["Git:Author:Email"]                      = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"]        = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"]      = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"]         = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"]     = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"]   = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"]                    = "50",
                ["RunBounds:MaxMinutes"]                  = "10",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the real runtime detector with a stub.
            services.AddSingleton<IKubernetesEnvironment>(
                new StubKubernetesEnvironment(_isKubernetes, _podName));
        });
    }
}

file sealed class StubKubernetesEnvironment(bool isKubernetes, string? podName) : IKubernetesEnvironment
{
    public bool    IsKubernetes => isKubernetes;
    public string? PodName      => podName;
}
