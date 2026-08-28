using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Regression coverage for GitHub issue #333: POST /api/projects must not require
/// working_directory when the active workspace provider can auto-assign one.
/// Uses <see cref="PersistentVolumeProjectsWebApplicationFactory"/>, which selects
/// PersistentVolumeWorkspaceProvider (AutoAssignsPath == true) instead of the default
/// LocalFilesystemWorkspaceProvider used by <see cref="ProjectsWebApplicationFactory"/>.
/// </summary>
public sealed class ProjectWorkspaceAutoAssignTests : IClassFixture<SelectionPersistentVolumeProjectsWebApplicationFactory>
{
    private readonly SelectionPersistentVolumeProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProjectWorkspaceAutoAssignTests(SelectionPersistentVolumeProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();

        // Seed a signed-in GitHub token so the github-origin regression test below only exercises
        // the working_directory validation path, not GitHub sign-in.
        _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("test-access-token", null, null, "test-login", null, Array.Empty<string>()))
            .GetAwaiter().GetResult();
    }

    // =========================================================================
    // PVA-01: /api/server/info reports workspace_auto_assigned == true for this provider
    // =========================================================================
    [Fact]
    public async Task ServerInfo_ReportsWorkspaceAutoAssigned()
    {
        var response = await _client.GetAsync("/api/server/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("workspace_auto_assigned").GetBoolean().Should().BeTrue();
    }

    // =========================================================================
    // PVA-02: POST /api/projects (blank) omitting working_directory succeeds (201) when the
    // active workspace provider auto-assigns paths. This is the core regression for #333: a
    // client should never need to know or construct a server-side absolute filesystem path.
    // =========================================================================
    [Fact]
    public async Task PostProject_Blank_OmittingWorkingDirectory_Returns201_WhenProviderAutoAssigns()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name   = $"Auto Assigned Project {Guid.NewGuid():N}",
            origin = "blank",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var workingDirectory = body.GetProperty("working_directory").GetString();
        workingDirectory.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(workingDirectory).Should().BeTrue();
    }

    // =========================================================================
    // PVA-03: POST /api/projects (github origin) omitting working_directory also succeeds when
    // the provider auto-assigns, since the required-path validation is provider-driven, not
    // origin-driven.
    // =========================================================================
    [Fact]
    public async Task PostProject_FromGitHub_OmittingWorkingDirectory_Returns201_WhenProviderAutoAssigns()
    {
        var selection = await _client.PostAsJsonAsync(
            "/api/github/repository-selections", new { full_name = "example/repo" });
        selection.StatusCode.Should().Be(HttpStatusCode.OK);
        var selectionCode = (await selection.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("selection_code").GetString();

        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Auto Assigned GitHub Project {Guid.NewGuid():N}",
            origin = "github",
            repository_selection_code = selectionCode,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}

public sealed class SelectionPersistentVolumeProjectsWebApplicationFactory : PersistentVolumeProjectsWebApplicationFactory
{
    private readonly HttpMessageHandler _handler = new RepositorySelectionHandler();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
                "github",
                options => options.HttpMessageHandlerBuilderActions.Add(
                    build => build.PrimaryHandler = _handler));
        });
    }

    private sealed class RepositorySelectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":42,"full_name":"example/repo","owner":{"login":"example"},"private":true,"default_branch":"main"}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
