using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

/// <summary>
/// Verifies FR-009: exactly two model source providers are permitted; all others
/// are rejected at submission time. Uses a real in-process API via
/// WebApplicationFactory.
/// </summary>
public sealed class ModelSourceValidationTests : IClassFixture<ScaffolderWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _repoDir;

    public ModelSourceValidationTests(ScaffolderWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ScaffolderWebApplicationFactory.TestApiKey);

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
    public async Task Submit_WithGitHubCopilot_IsAccepted()
    {
        var response = await PostRunAsync("github-copilot");

        // 202 Accepted or 400 from git/agent setup — what matters is it is not
        // rejected for the model source itself. A 400 from git or agent setup is
        // acceptable; only a model-source 400 would be a failure.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var error = body.GetProperty("error").GetString() ?? string.Empty;
            error.Should().NotContain("model_source",
                because: "github-copilot is a valid model source and must not be rejected for that reason");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task Submit_WithMicrosoftFoundry_IsAccepted()
    {
        var response = await PostRunAsync("microsoft-foundry");

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var error = body.GetProperty("error").GetString() ?? string.Empty;
            error.Should().NotContain("model_source",
                because: "microsoft-foundry is a valid model source and must not be rejected for that reason");
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("")]
    [InlineData("GITHUB-COPILOT")]
    public async Task Submit_WithUnsupportedProvider_Returns400(string source)
    {
        var response = await PostRunAsync(source);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            because: $"'{source}' is not a permitted model source; FR-009 allows only github-copilot and microsoft-foundry");
    }
}
