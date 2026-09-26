using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubRepositorySelectionEndpointsTests
{
    [Fact]
    public async Task BrowseAndIssue_RequireHumanEntraAuthorizationAndReturnOnlyAnOpaqueCode()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory();
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);

        var browse = await client.GetAsync("/api/github/repository-selections");

        browse.StatusCode.Should().Be(HttpStatusCode.OK);
        var browseBody = await browse.Content.ReadFromJsonAsync<JsonElement>();
        var repository = browseBody.GetProperty("repositories").EnumerateArray().Single();
        repository.GetProperty("full_name").GetString().Should().Be("octo/secure-repo");
        repository.TryGetProperty("repository_id", out _).Should().BeFalse();
        repository.TryGetProperty("authorization_id", out _).Should().BeFalse();
        repository.TryGetProperty("permissions", out _).Should().BeFalse();
        repository.TryGetProperty("clone_url", out _).Should().BeFalse();
        var installation = browseBody.GetProperty("installations").EnumerateArray().Single();
        installation.GetProperty("account_login").GetString().Should().Be("octo");
        installation.GetProperty("account_type").GetString().Should().Be("user");
        installation.GetProperty("repository_selection").GetString().Should().Be("selected");
        installation.GetProperty("management_url").GetString()
            .Should().Be("https://github.com/settings/installations/72");
        installation.TryGetProperty("installation_id", out _).Should().BeFalse();
        installation.TryGetProperty("permissions", out _).Should().BeFalse();

        var issue = await client.PostAsJsonAsync(
            "/api/github/repository-selections",
            new { full_name = "octo/secure-repo" });

        issue.StatusCode.Should().Be(HttpStatusCode.OK);
        var issueBody = await issue.Content.ReadFromJsonAsync<JsonElement>();
        var code = issueBody.GetProperty("selection_code").GetString();
        code.Should().NotBeNull().And.HaveLength(43);
        issueBody.TryGetProperty("repository_id", out _).Should().BeFalse();
        issueBody.TryGetProperty("installation_id", out _).Should().BeFalse();
        issueBody.TryGetProperty("authorization_id", out _).Should().BeFalse();
        issueBody.TryGetProperty("credential", out _).Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        var broker = scope.ServiceProvider.GetRequiredService<GitHubRepositorySelectionBroker>();
        (await broker.TryConsumeAsync(code!, subject, CancellationToken.None))
            .Should().BeEquivalentTo(new
            {
                EntraObjectId = subject,
                RepositoryId = 42L,
            });
    }

    [Fact]
    public async Task Issue_RejectsARepositoryOutsideTheCallerBrowseResultWithoutScopeDetails()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory();
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);

        var response = await client.PostAsJsonAsync(
            "/api/github/repository-selections",
            new { full_name = "octo/not-authorized" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("github_capability_unavailable");
        body.TryGetProperty("repository_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateGitHubProject_RejectsDirectRepositoryInputAndRetryReturnsTheSameProject()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory();
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);

        var issue = await client.PostAsJsonAsync(
            "/api/github/repository-selections",
            new { full_name = "octo/secure-repo" });
        var issued = await issue.Content.ReadFromJsonAsync<JsonElement>();
        var code = issued.GetProperty("selection_code").GetString()!;

        var directInput = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Rejected direct input",
            origin = "github",
            working_directory = factory.NewWorkingDirectory(),
            repository_selection_code = code,
            source_repository = "https://github.com/arbitrary/untrusted",
        });
        directInput.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var directInputBody = await directInput.Content.ReadFromJsonAsync<JsonElement>();
        directInputBody.GetProperty("error").GetString().Should().Contain("repository_selection_code");
        directInputBody.GetRawText().Should().NotContain("arbitrary/untrusted");

        var created = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Server resolved GitHub project",
            origin = "github",
            working_directory = factory.NewWorkingDirectory(),
            repository_selection_code = code,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        createdBody.GetProperty("source_repository").GetString().Should().Be("octo/secure-repo");
        var projectId = createdBody.GetProperty("project_id").GetString();
        var readiness = await client.GetAsync(
            $"/api/projects/{projectId}/github/unattended-readiness");
        readiness.StatusCode.Should().Be(HttpStatusCode.OK);
        var readinessBody = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        readinessBody.GetProperty("repository_ready").GetBoolean().Should().BeTrue();
        readinessBody.GetProperty("repo_app_installation_connected").GetBoolean().Should().BeTrue();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.GitHubInstallations.SingleAsync()).Should().BeEquivalentTo(new
            {
                InstallationId = 72L,
                ProjectId = (string?)null,
                RevokedAt = (DateTimeOffset?)null,
            });
            (await db.GitHubRepositoryGrants.SingleAsync()).Should().BeEquivalentTo(new
            {
                InstallationId = 72L,
                RepositoryId = 42L,
                ProjectId = projectId,
                FullNameDisplay = "octo/secure-repo",
                RevokedAt = (DateTimeOffset?)null,
            });
        }

        // Simulate process termination after clone/scaffolding and durable authorization binding,
        // but before the operational project row was activated.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
            await store.UpdateCreationStateAsync(
                ProjectId.Parse(projectId!),
                ProjectState.Creating,
                "main",
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        var reused = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Reused selection code",
            origin = "github",
            working_directory = factory.NewWorkingDirectory(),
            repository_selection_code = code,
        });
        reused.StatusCode.Should().Be(HttpStatusCode.OK);
        var reusedBody = await reused.Content.ReadFromJsonAsync<JsonElement>();
        reusedBody.GetProperty("project_id").GetString()
            .Should().Be(projectId);
        reusedBody.GetProperty("state").GetString().Should().Be("active");
        reusedBody.GetProperty("source_repository").GetString().Should().Be("octo/secure-repo");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.GitHubInstallations.CountAsync()).Should().Be(1);
            (await db.GitHubRepositoryGrants.CountAsync()).Should().Be(1);
            var grant = await db.GitHubRepositoryGrants.SingleAsync();
            grant.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var retryAfterRevocation = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Retry after revocation",
            origin = "github",
            working_directory = factory.NewWorkingDirectory(),
            repository_selection_code = code,
        });
        retryAfterRevocation.StatusCode.Should().Be(HttpStatusCode.OK);
        var retryAfterRevocationBody =
            await retryAfterRevocation.Content.ReadFromJsonAsync<JsonElement>();
        retryAfterRevocationBody.GetProperty("project_id").GetString().Should().Be(projectId);

        var revokedReadiness = await client.GetAsync(
            $"/api/projects/{projectId}/github/unattended-readiness");
        revokedReadiness.StatusCode.Should().Be(HttpStatusCode.OK);
        var revokedReadinessBody = await revokedReadiness.Content.ReadFromJsonAsync<JsonElement>();
        revokedReadinessBody.GetProperty("repository_ready").GetBoolean().Should().BeFalse();
        revokedReadinessBody.GetProperty("repository").GetProperty("reason_code").GetString()
            .Should().Be("repo_app_repository_grant_required");
    }

    [Fact]
    public async Task ListRepositoryOwners_ForBlankProjectUsesTheRepoAppCredential()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory();
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);
        var created = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Blank project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.GetAsync(
            $"/api/projects/{project.GetProperty("project_id").GetString()}/github/repository-owners");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var owners = await response.Content.ReadFromJsonAsync<JsonElement>();
        owners.EnumerateArray().Should().ContainSingle(owner =>
            owner.GetProperty("login").GetString() == "octo" &&
            owner.GetProperty("type").GetString() == "user");
    }

    [Fact]
    public async Task ListRepositoryOwners_ReturnsCapabilityUnavailableWhenGitHubLookupFailsAfterAuthorization()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory(new FailingRepositoryHandler(HttpStatusCode.Forbidden));
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);
        var created = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Blank project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.GetAsync(
            $"/api/projects/{project.GetProperty("project_id").GetString()}/github/repository-owners");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("github_capability_unavailable");
    }

    [Fact]
    public async Task ConnectExistingRepository_ForBlankProjectConsumesASelectionCode()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory();
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);
        var created = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Blank project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();

        var issue = await client.PostAsJsonAsync(
            "/api/github/repository-selections",
            new { full_name = "octo/secure-repo" });
        issue.StatusCode.Should().Be(HttpStatusCode.OK);
        var issued = await issue.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project.GetProperty("project_id").GetString()}/github/repository/connection",
            new { repository_selection_code = issued.GetProperty("selection_code").GetString() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("source_repository").GetString().Should().Be("octo/secure-repo");
        body.GetProperty("html_url").GetString().Should().Be("https://github.com/octo/secure-repo");
        body.TryGetProperty("repository_id", out _).Should().BeFalse();
        body.TryGetProperty("installation_id", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CreateRepository_ForBlankProjectUsesAnInstallationBackedOwner()
    {
        const string subject = "selection-subject";
        using var factory = new RepositorySelectionWebApplicationFactory();
        await factory.SeedRepoAppAuthorizationAsync(subject);
        var client = factory.CreateAuthenticatedClientForObjectId(subject, PlatformRoles.ProjectCreator);
        var created = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Blank project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{project.GetProperty("project_id").GetString()}/github/repository",
            new { owner = "octo", name = "new-repo", @private = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("source_repository").GetString().Should().Be("octo/new-repo");
        body.GetProperty("html_url").GetString().Should().Be("https://github.com/octo/new-repo");
    }

    private sealed class RepositorySelectionWebApplicationFactory(HttpMessageHandler? handler = null) : EntraWebApplicationFactory
    {
        private readonly HttpMessageHandler _handler = handler ?? new RepositoryHandler();

        public async Task SeedRepoAppAuthorizationAsync(string subject)
        {
            using var scope = Services.CreateScope();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            using var rsa = RSA.Create(2048);
            await secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem());
            await secrets.SetSecretAsync(
                "repo-app-user-credential-version",
                """{"status":"signed-in","accessToken":"test-token"}""");

            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.GitHubAppAuthorizations.Add(new GitHubAppAuthorizationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                EntraObjectId = subject,
                AppKind = GitHubAppKind.Repo,
                Purpose = GitHubAuthorizationPurpose.InteractiveRepository,
                CredentialReference = "repo-app-user-credential-version",
                CredentialVersion = "version",
                GrantDigest = "digest",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:RepoApp:AppId"] = "123",
                    ["Auth:RepoApp:PrivateKeySecretName"] = "repo-app-pem",
                    ["Auth:RepoApp:ApiUrl"] = "https://api.github.test",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
                    "github",
                    options =>
                    {
                        options.HttpMessageHandlerBuilderActions.Add(build => build.PrimaryHandler = _handler);
                    });
            });
        }
    }

    private sealed class RepositoryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath switch
                    {
                        "/user/installations" => """{"installations":[{"id":72,"account":{"login":"octo"},"target_type":"User","repository_selection":"selected","html_url":"https://github.com/settings/installations/72","permissions":{"administration":"write"}}]}""",
                        "/user/installations/72/repositories" => """{"repositories":[{"id":42,"full_name":"octo/secure-repo","owner":{"login":"octo"},"private":true,"default_branch":"main","clone_url":"https://github.com/octo/secure-repo.git"}]}""",
                        "/repositories/42/installation" => """{"id":72,"repository_selection":"selected","account":{"login":"octo"},"permissions":{"contents":"write","pull_requests":"write"}}""",
                        "/app/installations/72/access_tokens" => """{"token":"ghs_metadata_token","expires_at":"2030-01-01T00:00:00Z"}""",
                        "/repositories/42" => """{"id":42,"full_name":"octo/secure-repo"}""",
                        "/user/repos" when request.Method == HttpMethod.Post => """{"full_name":"octo/new-repo","clone_url":"https://github.com/octo/new-repo.git","html_url":"https://github.com/octo/new-repo"}""",
                        _ => "{}",
                    },
                        Encoding.UTF8,
                        "application/json"),
                StatusCode = request.RequestUri!.AbsolutePath switch
                {
                    "/user/repos" when request.Method == HttpMethod.Post => HttpStatusCode.Created,
                    "/app/installations/72/access_tokens" => HttpStatusCode.Created,
                    _ => HttpStatusCode.OK,
                },
                });
    }

    private sealed class FailingRepositoryHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

}
