using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
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
        repository.GetProperty("repository_id").GetInt64().Should().Be(42);
        repository.TryGetProperty("permissions", out _).Should().BeFalse();
        repository.TryGetProperty("clone_url", out _).Should().BeFalse();

        var issue = await client.PostAsJsonAsync(
            "/api/github/repository-selections",
            new { repository_id = 42 });

        issue.StatusCode.Should().Be(HttpStatusCode.OK);
        var issueBody = await issue.Content.ReadFromJsonAsync<JsonElement>();
        var code = issueBody.GetProperty("selection_code").GetString();
        code.Should().NotBeNull().And.HaveLength(43);
        issueBody.TryGetProperty("repository_id", out _).Should().BeFalse();
        issueBody.TryGetProperty("installation_id", out _).Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        var broker = scope.ServiceProvider.GetRequiredService<GitHubRepositorySelectionBroker>();
        (await broker.TryConsumeAsync(code!, subject, CancellationToken.None))
            .Should().Be(new ConsumedGitHubRepositorySelection(subject, 42));
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
            new { repository_id = 99 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("github_capability_unavailable");
        body.TryGetProperty("repository_id", out _).Should().BeFalse();
    }

    private sealed class RepositorySelectionWebApplicationFactory : EntraWebApplicationFactory
    {
        private readonly HttpMessageHandler _handler = new RepositoryHandler();

        public async Task SeedRepoAppAuthorizationAsync(string subject)
        {
            using var scope = Services.CreateScope();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
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
                    """[{"id":42,"full_name":"octo/secure-repo","owner":{"login":"octo"},"private":true,"default_branch":"main"}]""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
