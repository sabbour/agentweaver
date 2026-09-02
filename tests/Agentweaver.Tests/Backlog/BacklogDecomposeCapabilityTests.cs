using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Backlog;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentweaver.Tests.Backlog;

public sealed class BacklogDecomposeCapabilityTests : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public BacklogDecomposeCapabilityTests(CoordinatorWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task DecomposeAsync_WithConnectedBoundCapability_RedeemsBeforeTheSingleModelTurn()
    {
        var runner = new CapturingRunner("""{"items":[{"title":"Implement capability fencing","description":"Keep decomposition scoped."}]}""");
        using var app = CreateApp(runner);
        using var owner = CreateClient(app, CoordinatorWebApplicationFactory.OwnerApiKey);
        var projectId = await CreateProjectAsync(owner);
        const string entraObjectId = "decompose-entra-owner";
        await AddActiveBindingAsync(app.Services, projectId, entraObjectId);

        var project = await app.Services.GetRequiredService<IProjectStore>()
            .GetAsync(ProjectId.Parse(projectId));
        project.Should().NotBeNull();

        var result = await app.Services.GetRequiredService<IBacklogDecomposeService>().DecomposeAsync(
            project!,
            "# Plan\n- Secure decomposition",
            new CallerContext
            {
                User = CoordinatorWebApplicationFactory.OwnerUser,
                EntraObjectId = entraObjectId,
            },
            CancellationToken.None);

        result.ConnectionRequirement.Should().BeNull();
        result.Items.Should().ContainSingle().Which.Title.Should().Be("Implement capability fencing");
        runner.Invocations.Should().Be(1, "the model runner receives a client only after broker redemption");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.MarketplaceCopilotCapabilities.CountAsync()).Should().Be(0,
            "the non-run authority is single-use and deleted after the model turn");
    }

    [Fact]
    public async Task DecomposeAsync_WithoutAnActiveBinding_ReturnsConnectActionWithoutCallingTheModel()
    {
        var runner = new CapturingRunner("""{"items":[]}""");
        using var app = CreateApp(runner);
        using var owner = CreateClient(app, CoordinatorWebApplicationFactory.OwnerApiKey);
        var projectId = await CreateProjectAsync(owner);
        var project = await app.Services.GetRequiredService<IProjectStore>()
            .GetAsync(ProjectId.Parse(projectId));
        project.Should().NotBeNull();

        var result = await app.Services.GetRequiredService<IBacklogDecomposeService>().DecomposeAsync(
            project!,
            "# Plan",
            new CallerContext
            {
                User = CoordinatorWebApplicationFactory.OwnerUser,
                EntraObjectId = "disconnected-entra-owner",
            },
            CancellationToken.None);

        result.ConnectionRequirement.Should().BeEquivalentTo(
            ModelProviderConnectionRequirement.ForProject(project!.Id));
        runner.Invocations.Should().Be(0,
            "the explicit project capability must exist before any decomposition model turn is attempted");
    }

    private WebApplicationFactory<Program> CreateApp(CapturingRunner runner) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBacklogDecomposeAgentRunner>();
                services.AddSingleton<IBacklogDecomposeAgentRunner>(runner);
                services.RemoveAll<IGitHubConnectionsCredentialVault>();
                services.AddScoped<IGitHubConnectionsCredentialVault>(_ => new FixedCopilotCredentialVault());
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> app, string apiKey)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Decompose Capability Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("project_id").GetString()!;
    }

    private static async Task AddActiveBindingAsync(IServiceProvider services, string projectId, string entraObjectId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        if (!await db.Projects.AnyAsync(project => project.ProjectId == projectId))
        {
            db.Projects.Add(new ProjectRecord
            {
                ProjectId = projectId,
                Name = "Test project",
                OriginKind = "blank",
                WorkingDirectory = "C:\\workspace",
                Owner = CoordinatorWebApplicationFactory.OwnerUser,
                DefaultProvider = ModelSource.GitHubCopilot.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = "decompose-binding-" + Guid.NewGuid().ToString("N"),
            ProjectId = projectId,
            EntraObjectId = entraObjectId,
            CredentialReference = "copilot-app-project-decompose",
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class CapturingRunner(string response) : IBacklogDecomposeAgentRunner
    {
        public int Invocations { get; private set; }

        public Task<string?> RunAsync(CopilotClient client, string prompt, string? modelId, CancellationToken ct)
        {
            Invocations++;
            prompt.Should().Contain("<<<DOCUMENT>>>");
            return Task.FromResult<string?>(response);
        }
    }

    private sealed class FixedCopilotCredentialVault : IGitHubConnectionsCredentialVault
    {
        public Task<SecretGetResult> ReadCurrentAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default) =>
            Task.FromResult(new SecretGetResult(
                """{"status":"signed-in","accessToken":"decompose-test-token","expiresAt":"2099-01-01T00:00:00Z"}""",
                ETag: null,
                Found: true));

        public Task WriteAsync(GitHubConnectionsCredentialLocator locator, string value, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task TombstoneAndDeleteAsync(GitHubConnectionsCredentialLocator locator, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
