using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Auth;

public sealed class AuthModeSwitchTests : IClassFixture<GitHubLegacyProjectsWebApplicationFactory>
{
    private readonly GitHubLegacyProjectsWebApplicationFactory _factory;

    public AuthModeSwitchTests(GitHubLegacyProjectsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void AuthModeResolver_DefaultsToEntra()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        AuthModeResolver.Resolve(configuration).Should().Be(AuthMode.Entra);
    }

    [Fact]
    public void AuthModeResolver_RecognizesGitHubLegacy()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = "GitHubLegacy" })
            .Build();

        AuthModeResolver.Resolve(configuration).Should().Be(AuthMode.GitHubLegacy);
    }

    [Fact]
    public async Task GitHubLegacyMode_PreservesProjectOwnerAuthorization()
    {
        using var owner = _factory.CreateAuthenticatedClient();
        using var intruder = _factory.CreateClient();
        intruder.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "githublegacy-intruder-token");

        var create = await owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"GitHubLegacy Ownership {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString();
        projectId.Should().NotBeNullOrWhiteSpace();

        (await owner.GetAsync($"/api/projects/{projectId}/memory")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await intruder.GetAsync($"/api/projects/{projectId}/memory")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task EntraMode_RejectsGitHubLegacyBearerFlow()
    {
        using var factory = new EntraWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "gho_legacy_session_token");

        var response = await client.GetAsync("/api/auth/context");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EntraMode_DoesNotFallBackToLegacyProjectOwnerChecks_WhenRoleAssignmentsAreRequired()
    {
        const string ownerOid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        using var factory = new EntraWebApplicationFactory();
        using var owner = factory.CreateAuthenticatedClientForObjectId(ownerOid, PlatformRoles.ProjectCreator);

        var create = await owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Entra RBAC {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var assignments = scope.ServiceProvider.GetRequiredService<Agentweaver.Domain.IProjectRoleAssignmentStore>();
            (await assignments.DeleteAsync(Agentweaver.Domain.ProjectId.Parse(projectId), ownerOid)).Should().BeTrue();
        }

        (await owner.GetAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(Skip = "pending Tank's explicit informational auth-mode exposure/log contract")]
    public async Task AuthMode_IsExposedInformationallyWithoutDeprecationWarning()
    {
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GitHubLegacyMode_RejectsEntraBearerFlow()
    {
        using var entraFactory = new EntraWebApplicationFactory();
        var entraToken = entraFactory.CreateBearerToken(Guid.NewGuid().ToString(), PlatformRoles.Contributor);

        var unique = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), $"agentweaver-legacy-auth-{unique}");
        var dbPath = Path.Combine(root, "auth.db");
        Directory.CreateDirectory(root);
        try
        {
            using var legacyFactory = new SharedAuthModeWebApplicationFactory(dbPath, root, "GitHubLegacy", enableBypass: false);
            using var client = legacyFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", entraToken);

            var response = await client.GetAsync("/api/auth/context");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(path); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RollingModeSwitch_OldInstanceRejectsLegacyBearer_WithoutRestart()
    {
        var unique = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), $"agentweaver-auth-mode-epoch-{unique}");
        var dbPath = Path.Combine(root, "auth.db");

        Directory.CreateDirectory(root);
        try
        {
            using var oldFactory = new SharedAuthModeWebApplicationFactory(dbPath, root, "GitHubLegacy", enableBypass: true);
            using var oldClient = oldFactory.CreateClient();
            oldClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "legacy-bypass-token");

            (await oldClient.GetAsync("/api/auth/context")).StatusCode.Should().Be(HttpStatusCode.OK);

            using var newFactory = new SharedAuthModeWebApplicationFactory(dbPath, root, "Entra", enableBypass: false);
            using var newClient = newFactory.CreateClient();
            (await newClient.GetAsync("/api/auth/config")).StatusCode.Should().Be(HttpStatusCode.OK);

            var newEpoch = await newFactory.Services.GetRequiredService<AuthModeEpochService>().EnsureInitializedAsync();
            var currentEpoch = await oldFactory.Services.GetRequiredService<AuthModeEpochService>().GetCurrentSnapshotAsync();
            currentEpoch.AuthMode.Should().Be(AuthMode.Entra);
            currentEpoch.Epoch.Should().Be(newEpoch.Epoch);

            (await oldClient.GetAsync("/api/auth/context")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(path); } catch { }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

public sealed class GitHubLegacyProjectsWebApplicationFactory : ProjectsWebApplicationFactory
{
    protected override IDictionary<string, string?> GetAdditionalConfiguration() =>
        new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "GitHubLegacy",
        };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient("entra-oidc");
            services.AddSingleton<EntraAccessTokenValidator>();
        });
    }
}

internal sealed class SharedAuthModeWebApplicationFactory(
    string dbPath,
    string rootPath,
    string authMode,
    bool enableBypass) : EntraWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = dbPath,
                ["Worktrees:BasePath"] = Path.Combine(rootPath, "worktrees"),
                ["Checkpoints:Path"] = Path.Combine(rootPath, "checkpoints"),
                ["Coordinator:Checkpoints:Path"] = Path.Combine(rootPath, "coordinator-checkpoints"),
                ["Auth:Mode"] = authMode,
                ["Auth:Entra:TenantId"] = TenantId,
                ["Auth:Entra:ClientId"] = ClientId,
                ["Auth:Entra:Issuer"] = Issuer,
                ["Auth:Entra:JwksJson"] = BuildJwksJson(),
                ["Auth:GitHub:ClientId"] = "test-github-client-id",
                ["Auth:GitHub:BaseUrl"] = "https://github.com",
                ["Auth:ApiKey"] = "internal-test-api-key",
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
                ["Testing:BypassGitHubTokenAuth"] = enableBypass ? "true" : "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            RemoveService<Agentweaver.Domain.IGitHubTokenStore>(services);
            services.AddSingleton<Agentweaver.Domain.IGitHubTokenStore, InMemoryGitHubTokenStore>();
            RemoveService<Agentweaver.Api.Git.ProjectGitInitializer>(services);
            services.AddSingleton<Agentweaver.Api.Git.ProjectGitInitializer, Agentweaver.Tests.Helpers.NoOpProjectGitInitializer>();
        });
    }
}
