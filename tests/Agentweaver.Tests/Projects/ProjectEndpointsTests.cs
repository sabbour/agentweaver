using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Projects;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Integration tests for the project CRUD and run endpoints.
/// Uses ProjectsWebApplicationFactory which wires InMemoryGitHubTokenStore and
/// a no-op git initializer so tests do not touch real git or the OS credential store.
/// </summary>
public sealed class ProjectEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProjectEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private string NewWorkingDir() => _factory.NewWorkingDirectory();

    /// <summary>
    /// Reads the paginated <c>{ items, page, page_size, total_count, total_pages }</c> envelope
    /// (see <see cref="Agentweaver.Api.Contracts.PagedResult{T}"/>) and returns just the <c>items</c>
    /// array, so existing array-shaped test assertions keep working against the new contract.
    /// </summary>
    private static async Task<JsonElement[]> GetItemsAsync(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        return envelope.GetProperty("items").EnumerateArray().ToArray();
    }

    private async Task<string> CreateBlankProjectAsync(string? name = null, string? dir = null)
    {
        dir ??= NewWorkingDir();
        var request = new CreateProjectRequest
        {
            Name             = name ?? $"Test Project {Guid.NewGuid():N}",
            Origin           = "blank",
            WorkingDirectory = dir,
        };
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task ResetBackgroundAiConfigurationAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
        await db.SaveChangesAsync();
        await secrets.DeleteSecretAsync("copilot-app-platform-default-version");
        await secrets.DeleteSecretAsync("byok-provider-configurations");
    }

    private async Task SeedPlatformDefaultCopilotBindingAsync(string login = "platform-bot")
    {
        await ResetBackgroundAiConfigurationAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        db.PlatformDefaultCopilotBindings.RemoveRange(db.PlatformDefaultCopilotBindings);
        db.PlatformDefaultCopilotBindings.Add(new PlatformDefaultCopilotBindingRecord
        {
            Id = PlatformDefaultCopilotBindingRecord.SingletonId,
            EntraObjectId = "platform-admin",
            CredentialReference = "copilot-app-platform-default-version",
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await secrets.SetSecretAsync(
            "copilot-app-platform-default-version",
            $$"""{"status":"signed-in","accessToken":"ghu_platform","expiresAt":"2099-01-01T00:00:00Z","githubLogin":"{{login}}"}""");
        await db.SaveChangesAsync();
    }

    private async Task SeedByokProviderConfigurationAsync()
    {
        await ResetBackgroundAiConfigurationAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        var created = await settings.AddAsync(
            new ByokProviderConfiguration(
                Id: string.Empty,
                Name: "Test Azure provider",
                Type: "azure",
                BaseUrl: "https://byok-resource.openai.azure.com",
                Model: "gpt-4.1",
                ApiKey: "test-byok-key"),
            CancellationToken.None);
        await settings.SetActiveAsync(created.Id, CancellationToken.None);
    }

    private async Task SeedRepoAppInstallationAsync(string projectId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        if (!db.Projects.Any(x => x.ProjectId == projectId))
        {
            db.Projects.Add(new ProjectRecord
            {
                ProjectId = projectId,
                Name = "Seeded Project",
                OriginKind = "blank",
                WorkingDirectory = _factory.NewWorkingDirectory(),
                Owner = ProjectsWebApplicationFactory.TestUser,
                DefaultProvider = "github-copilot",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        db.GitHubInstallations.Add(new GitHubInstallationRecord
        {
            AppKind = GitHubAppKind.Repo,
            InstallationId = 1234,
            ProjectId = projectId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // =========================================================================
    // PE-01: POST /api/projects (blank) returns 201 with project fields
    // =========================================================================
    [Fact]
    public async Task PostProject_Blank_Returns201()
    {
        var dir  = NewWorkingDir();
        var body = new CreateProjectRequest
        {
            Name             = "Integration Blank Project",
            Origin           = "blank",
            WorkingDirectory = dir,
        };

        var response = await _client.PostAsJsonAsync("/api/projects", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        result.Should().NotBeNull();
        result!.Name.Should().Be("Integration Blank Project");
        result.Origin.Should().Be("blank");
        result.State.Should().Be("active");
        result.Available.Should().BeTrue();
        result.PreviewApprovalTimeoutMinutes.Should().Be(30);
        response.Headers.Location.Should().NotBeNull();
    }

    // =========================================================================
    // PE-02: GET /api/projects lists created projects
    // =========================================================================
    [Fact]
    public async Task GetProjects_ListsCreatedProjects()
    {
        var name = $"Listed Project {Guid.NewGuid():N}";
        await CreateBlankProjectAsync(name);

        var response = await _client.GetAsync("/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await GetItemsAsync(response);
        list.Should().NotBeNull();
        list!.Any(p => p.GetProperty("name").GetString() == name).Should().BeTrue();
    }

    // =========================================================================
    // PE-03: GET /api/projects/{id} returns the project
    // =========================================================================
    [Fact]
    public async Task GetProject_ById_ReturnsProject()
    {
        var id = await CreateBlankProjectAsync("Show Project");

        var response = await _client.GetAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        result!.ProjectId.Should().Be(id);
        result.Name.Should().Be("Show Project");
    }

    [Fact]
    public async Task LegacyProjectWebhookSecretRoute_IsNotAvailable()
    {
        var id = await CreateBlankProjectAsync("Webhook Project");

        var response = await _client.PostAsJsonAsync($"/api/projects/{id}/webhook-secret/rotate", new { });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PE-04: GET /api/projects/{id} returns 404 for unknown id
    // =========================================================================
    [Fact]
    public async Task GetProject_UnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PE-05: PATCH /api/projects/{id} renames the project
    // =========================================================================
    [Fact]
    public async Task PatchProject_Rename_Returns204()
    {
        var id = await CreateBlankProjectAsync("Original Name");

        var response = await _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/projects/{id}")
        {
            Content = JsonContent.Create(new { name = "Renamed Project" }),
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await _client.GetAsync($"/api/projects/{id}");
        var result  = await getResp.Content.ReadFromJsonAsync<ProjectResponse>();
        result!.Name.Should().Be("Renamed Project");
    }

    // =========================================================================
    // PE-06: PUT /api/projects/{id}/provider-settings updates provider config
    // =========================================================================
    [Fact]
    public async Task PutProviderSettings_Returns204()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/provider-settings",
            new UpdateProjectProviderSettingsRequest
            {
                DefaultProvider           = "byok",
                DefaultModelMicrosoftFoundry = "gpt-4o",
                BlueprintGenerationModel = "gpt-5-mini",
                WorkflowGenerationModel = "gpt-5.3-codex",
                OutcomeSpecGenerationModel = "claude-sonnet-4.6",
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await _client.GetAsync($"/api/projects/{id}");
        var result  = await getResp.Content.ReadFromJsonAsync<ProjectResponse>();
        result!.DefaultProvider.Should().Be("byok");
        result.DefaultModelMicrosoftFoundry.Should().Be("gpt-4o");
        result.BlueprintGenerationModel.Should().Be("gpt-5-mini");
        result.WorkflowGenerationModel.Should().Be("gpt-5.3-codex");
        result.OutcomeSpecGenerationModel.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task PutPreviewSettings_PersistsProjectScopedTimeout()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/preview-settings",
            new UpdateProjectPreviewSettingsRequest { ApprovalTimeoutMinutes = 45 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await response.Content.ReadFromJsonAsync<ProjectPreviewSettingsResponse>();
        saved!.ApprovalTimeoutMinutes.Should().Be(45);

        var project = await _client.GetFromJsonAsync<ProjectResponse>($"/api/projects/{id}");
        project!.PreviewApprovalTimeoutMinutes.Should().Be(45);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task PutPreviewSettings_RejectsOutOfRangeTimeout(int minutes)
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/preview-settings",
            new UpdateProjectPreviewSettingsRequest { ApprovalTimeoutMinutes = minutes });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutRepoAppInstallation_IsNotAvailableForCallerSuppliedScope()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/github/repo-app-installation",
            new
            {
                installationId = 72,
                repositoryId = 99,
                permissions = new { contents = "write" },
                fullNameDisplay = "forged/repository",
            });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnattendedReadiness_ReturnsOnlyAClosedRedactedStatus()
    {
        await ResetBackgroundAiConfigurationAsync();
        var id = await CreateBlankProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/github/unattended-readiness");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("reason_code").GetString().Should().Be("model_provider_connection_required");
        body.GetProperty("repo_app_installation_connected").GetBoolean().Should().BeFalse();
        body.GetProperty("model_provider").GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("model_provider").GetProperty("source").GetString().Should().Be("none");
        body.GetProperty("model_provider").GetProperty("reason_code").GetString()
            .Should().Be("model_provider_connection_required");
        body.GetProperty("repository").GetProperty("required").GetBoolean().Should().BeFalse();
        body.GetProperty("repository").GetProperty("status").GetString().Should().Be("not_required");
        body.GetProperty("repository").GetProperty("reason_code").GetString().Should().Be("not_required");
        body.GetProperty("repository").GetProperty("repo_app_installation_connected").GetBoolean().Should().BeFalse();
        body.TryGetProperty("installation_id", out _).Should().BeFalse();
        body.TryGetProperty("repository_id", out _).Should().BeFalse();
        body.TryGetProperty("permissions", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetProjectCopilotConnection_ReturnsOnlyRedactedConnectionState()
    {
        await ResetBackgroundAiConfigurationAsync();
        var id = await CreateBlankProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/github/copilot/connection");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("not_connected");
        body.GetProperty("github_login").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("effective_source").GetString().Should().Be("none");
        body.TryGetProperty("authorization_url", out _).Should().BeFalse();
        body.TryGetProperty("transaction_id", out _).Should().BeFalse();
        body.TryGetProperty("access_token", out _).Should().BeFalse();
        body.TryGetProperty("refresh_token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetProjectCopilotConnection_FallsBackToPlatformDefaultConnection()
    {
        var id = await CreateBlankProjectAsync();
        await SeedPlatformDefaultCopilotBindingAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/github/copilot/connection");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("not_connected");
        body.GetProperty("github_login").GetString().Should().Be("platform-bot");
        body.GetProperty("effective_source").GetString().Should().Be("platform_default");
        body.GetProperty("platform_default_connected").GetBoolean().Should().BeTrue();
        body.GetProperty("byok_configured").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetProjectCopilotConnection_ReportsByokWhenCustomKeyIsConfigured()
    {
        var id = await CreateBlankProjectAsync();
        await SeedByokProviderConfigurationAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/github/copilot/connection");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("not_connected");
        body.GetProperty("github_login").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("effective_source").GetString().Should().Be("byok");
        body.GetProperty("platform_default_connected").GetBoolean().Should().BeFalse();
        body.GetProperty("byok_configured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectCopilotConnection_StaleProjectBindingRequiresReconnect()
    {
        await ResetBackgroundAiConfigurationAsync();
        var id = await CreateBlankProjectAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (!db.Projects.Any(project => project.ProjectId == id))
                db.Projects.Add(new ProjectRecord { ProjectId = id, OriginKind = "blank" });
            db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
            {
                Id = "stale-project-connection-binding",
                ProjectId = id,
                EntraObjectId = ProjectsWebApplicationFactory.TestUser,
                CredentialReference = "missing-project-connection-credential",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/projects/{id}/github/copilot/connection");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_model_provider_reconnect_required");
    }

    [Fact]
    public async Task GetProjectCopilotConnection_StaleProjectBindingStillRequiresReconnectWhenPlatformDefaultExists()
    {
        var id = await CreateBlankProjectAsync();
        await SeedPlatformDefaultCopilotBindingAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (!db.Projects.Any(project => project.ProjectId == id))
                db.Projects.Add(new ProjectRecord { ProjectId = id, OriginKind = "blank" });
            db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
            {
                Id = "stale-project-connection-with-platform-default-binding",
                ProjectId = id,
                EntraObjectId = ProjectsWebApplicationFactory.TestUser,
                CredentialReference = "missing-project-connection-with-platform-default-credential",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/projects/{id}/github/copilot/connection");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_model_provider_reconnect_required");
    }

    [Fact]
    public async Task GetUnattendedReadiness_BlankProjectWithPlatformDefaultDoesNotRequireRepository()
    {
        var id = await CreateBlankProjectAsync();
        await SeedPlatformDefaultCopilotBindingAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/github/unattended-readiness");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ready");
        body.GetProperty("reason_code").GetString().Should().Be("ready");
        body.GetProperty("repo_app_installation_connected").GetBoolean().Should().BeFalse();
        body.GetProperty("model_provider").GetProperty("source").GetString().Should().Be("platform_default");
        body.GetProperty("repository").GetProperty("status").GetString().Should().Be("not_required");
    }

    [Fact]
    public async Task GetUnattendedReadiness_BlankProjectWithByokDoesNotRequireRepository()
    {
        var id = await CreateBlankProjectAsync();
        await SeedByokProviderConfigurationAsync();
        await SeedRepoAppInstallationAsync(id);

        var response = await _client.GetAsync($"/api/projects/{id}/github/unattended-readiness");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ready");
        body.GetProperty("reason_code").GetString().Should().Be("ready");
        body.GetProperty("model_provider").GetProperty("source").GetString().Should().Be("byok");
        body.GetProperty("repository").GetProperty("required").GetBoolean().Should().BeFalse();
        body.GetProperty("repository").GetProperty("status").GetString().Should().Be("not_required");
    }

    [Fact]
    public async Task GetUnattendedReadiness_StaleProjectBindingFailsClosedOverByok()
    {
        var id = await CreateBlankProjectAsync();
        await SeedByokProviderConfigurationAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (!db.Projects.Any(project => project.ProjectId == id))
                db.Projects.Add(new ProjectRecord { ProjectId = id, OriginKind = "blank" });
            db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
            {
                Id = "stale-project-binding",
                ProjectId = id,
                EntraObjectId = ProjectsWebApplicationFactory.TestUser,
                CredentialReference = "missing-project-credential",
                CredentialVersion = "version",
                GrantDigest = "digest",
                Status = GitHubBindingStatus.Active,
                BoundAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/projects/{id}/github/unattended-readiness");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("reason_code").GetString().Should().Be("project_model_provider_reconnect_required");
        body.GetProperty("model_provider").GetProperty("status").GetString().Should().Be("not_ready");
        body.GetProperty("model_provider").GetProperty("source").GetString().Should().Be("project");
        body.GetProperty("model_provider").GetProperty("reason_code").GetString()
            .Should().Be("project_model_provider_reconnect_required");
        body.GetProperty("repository").GetProperty("status").GetString().Should().Be("not_required");
    }

    [Fact]
    public async Task ConnectingRepository_InvalidatesActiveRepositorylessActivation()
    {
        var id = await CreateBlankProjectAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        if (!db.Projects.Any(project => project.ProjectId == id))
            db.Projects.Add(new ProjectRecord { ProjectId = id, OriginKind = "blank" });
        db.AutomationActivations.Add(new AutomationActivationRecord
        {
            Id = SnapshotRef.Create().Value,
            ProjectId = id,
            ModelProviderSource = AutomationModelProviderSource.Byok,
            ByokProviderId = "provider",
            AutomationKey = "repositoryless",
            Status = AutomationActivationStatus.Active,
            ActivatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<ProjectService>();
        await service.ConnectCreatedRepositoryAsync(
            ProjectId.Parse(id),
            "octo/connected",
            "https://github.com/octo/connected.git",
            "ephemeral-token");

        db.ChangeTracker.Clear();
        var activation = await db.AutomationActivations.SingleAsync(x => x.ProjectId == id);
        activation.Status.Should().Be(AutomationActivationStatus.Invalidated);
        activation.InvalidatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetProjectAccessOverview_ReturnsCurrentRoleSnapshot()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/access");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("auth_mode").GetString().Should().Be("entra");
        body.GetProperty("current_user_project_role").GetString().Should().Be("Owner");
        body.GetProperty("can_manage_role_assignments").GetBoolean().Should().BeTrue();
        body.GetProperty("can_manage_project_github_identity").GetBoolean().Should().BeTrue();
        body.GetProperty("project_role_assignments").ValueKind.Should().Be(JsonValueKind.Array);
        body.GetProperty("effective_github_login").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // =========================================================================
    // PE-07: DELETE /api/projects/{id}?confirm=true returns 204
    // =========================================================================
    [Fact]
    public async Task DeleteProject_WithConfirm_Returns204()
    {
        var id = await CreateBlankProjectAsync("To Delete");

        var response = await _client.DeleteAsync($"/api/projects/{id}?confirm=true");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Project should no longer be findable
        var getResp = await _client.GetAsync($"/api/projects/{id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PE-08: DELETE /api/projects/{id} without confirm=true returns 400
    // =========================================================================
    [Fact]
    public async Task DeleteProject_WithoutConfirm_Returns400()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.DeleteAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostProjectRelink_Returns404()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/relink",
            new { working_directory = NewWorkingDir() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PE-09: GET /api/projects/{id}/runs returns empty list for new project
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_EmptyForNewProject()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await GetItemsAsync(response);
        runs.Should().NotBeNull();
        runs.Should().BeEmpty();
    }

    // =========================================================================
    // PE-10: GET /api/projects/{id}/runs returns runs for that project
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_ReturnsRunsForProject()
    {
        var id       = await CreateBlankProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();

        // Insert a run for this project directly
        var projectId = ProjectId.Parse(id);
        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "endpoint test task",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Failed,
            StartedAt         = DateTimeOffset.UtcNow,
            EndedAt           = DateTimeOffset.UtcNow,
            ProjectId         = projectId,
        };
        await runStore.InsertAsync(run);

        var response = await _client.GetAsync($"/api/projects/{id}/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await GetItemsAsync(response);
        runs.Should().NotBeNull();
        runs!.Any(r => r.GetProperty("execution_id").GetString() == run.Id.ToString())
             .Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectRunByWorkflowRunId_Returns404()
    {
        var id       = await CreateBlankProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var projectId = ProjectId.Parse(id);
        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "removed run page endpoint",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Completed,
            StartedAt         = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt           = DateTimeOffset.UtcNow,
            ProjectId         = projectId,
            WorkflowRunId     = "workflow-run-page-removed",
        };
        await runStore.InsertAsync(run);

        var response = await _client.GetAsync($"/api/projects/{id}/runs/{run.WorkflowRunId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProjectRuns_CanFilterTerminalAgentHistoryIncludingChildren()
    {
        var id       = await CreateBlankProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var projectId = ProjectId.Parse(id);
        var now = DateTimeOffset.UtcNow;
        var parentId = RunId.New();

        await runStore.InsertAsync(new Run
        {
            Id                = parentId,
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Coordinator",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.InProgress,
            StartedAt         = now.AddMinutes(-20),
            ProjectId         = projectId,
            AgentName         = "Coordinator",
        });

        var adaChild = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Ada terminal work",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Completed,
            StartedAt         = now.AddMinutes(-10),
            EndedAt           = now.AddMinutes(-5),
            ProjectId         = projectId,
            AgentName         = "Ada",
            ParentRunId       = parentId.ToString(),
            SubtaskId         = "1",
        };
        await runStore.InsertAsync(adaChild);

        await runStore.InsertAsync(new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Ada active work",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.InProgress,
            StartedAt         = now.AddMinutes(-1),
            ProjectId         = projectId,
            AgentName         = "Ada",
            ParentRunId       = parentId.ToString(),
            SubtaskId         = "2",
        });

        var response = await _client.GetAsync($"/api/projects/{id}/runs?agent=Ada&terminal_only=true&include_children=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await GetItemsAsync(response);
        runs.Should().ContainSingle();
        runs[0].GetProperty("execution_id").GetString().Should().Be(adaChild.Id.ToString());
        runs[0].GetProperty("status").GetString().Should().Be("completed");
        runs[0].GetProperty("agent_name").GetString().Should().Be("Ada");
    }

    // =========================================================================
    // PE-11: POST /api/projects/{id}/runs is deprecated
    // =========================================================================
    [Fact]
    public async Task PostProjectRun_Returns410Gone()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/runs",
            new CreateProjectRunRequest { Task = "should be rejected" });

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    // =========================================================================
    // PE-12: POST /api/projects with missing required fields returns 400
    // =========================================================================
    [Fact]
    public async Task PostProject_MissingName_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            origin           = "blank",
            working_directory = NewWorkingDir(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // PE-12b: POST /api/projects without working_directory returns 400 when the active
    // workspace provider cannot auto-assign one (LocalFilesystemWorkspaceProvider, this
    // factory's default). Regression guard for #333: this behavior must be preserved for
    // providers that genuinely need an explicit client-supplied path.
    // =========================================================================
    [Fact]
    public async Task PostProject_MissingWorkingDirectory_Returns400_WhenProviderCannotAutoAssign()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name   = $"No Dir Project {Guid.NewGuid():N}",
            origin = "blank",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("working_directory is required.");
    }

    // =========================================================================
    // PE-15: GET /healthz/workspace returns 200 with local-filesystem provider
    //   (LocalFilesystemWorkspaceProvider.IsMountRootHealthy is always true)
    // =========================================================================
    [Fact]
    public async Task GetWorkspaceReadiness_Returns200_ForLocalProvider()
    {
        // This factory uses the default LocalFilesystemWorkspaceProvider which always reports healthy.
        var unauthClient = _factory.CreateClient();

        var response = await unauthClient.GetAsync("/healthz/workspace");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
    }
}
