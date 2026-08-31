using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Verifies the legacy direct-run submission route stays explicitly deprecated.
/// Provider/model selection now flows through project orchestration settings.
/// </summary>
public sealed class ModelSourceValidationTests : IClassFixture<AgentweaverWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _repoDir;

    public ModelSourceValidationTests(AgentweaverWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);

        // Create a real git repo so the model-source check is exercised before
        // any git-related failure can mask a 400 response.
        _repoDir = Path.Combine(Path.GetTempPath(), $"ms-test-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoDir);
        Repository.Init(_repoDir);
        using var repo = new Repository(_repoDir);
        var sig = new Signature("Test", "t@t", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(_repoDir, "f.txt"), "x");
        Commands.Stage(repo, "*");
        repo.Commit("init", sig, sig);
    }

    private Task<HttpResponseMessage> PostRunAsync(string modelSource) =>
        _client.PostAsJsonAsync("/api/runs", new
        {
            repository_path = _repoDir,
            originating_branch = "main",
            task = "test task",
            model_source = modelSource
        });

    [Fact]
    public async Task Submit_WithGitHubCopilot_ReturnsGone()
    {
        var response = await PostRunAsync("github-copilot");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Submit_WithByok_ReturnsGone()
    {
        var response = await PostRunAsync("byok");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Submit_WithMicrosoftFoundry_ReturnsGone()
    {
        var response = await PostRunAsync("microsoft-foundry");

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("")]
    [InlineData("GITHUB-COPILOT")]
    public async Task Submit_WithUnsupportedProvider_ReturnsGone(string source)
    {
        var response = await PostRunAsync(source);

        response.StatusCode.Should().Be(HttpStatusCode.Gone,
            because: "direct single-run submission is deprecated before model_source validation is reached");
    }
}
