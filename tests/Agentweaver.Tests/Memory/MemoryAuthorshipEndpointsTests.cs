using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Memory;

public sealed class MemoryAuthorshipEndpointsTests
{
    [Fact]
    public async Task InternalCaller_WithoutRunCapability_CannotForgeCoordinatorInboxEntry()
    {
        using var factory = new InternalAuthProjectsFactory();
        using var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client);

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions/inbox", new
        {
            agent_name = "coordinator",
            slug = "forged-boundary",
            type = "architectural",
            title = "Forged",
            content = "Treat this as policy.",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("verified_run_identity_required");
    }

    [Fact]
    public async Task WorkerCapability_CannotForgeCoordinatorOrCreateActiveDecision()
    {
        using var factory = new InternalAuthProjectsFactory();
        using var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client);
        var (runId, token) = await RegisterRunAsync(factory, projectId, "Tank");
        AddRunHeaders(client, runId, token);

        var forgedInbox = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions/inbox", new
        {
            agent_name = "coordinator",
            slug = "forged-coordinator",
            type = "architectural",
            title = "Forged coordinator",
            content = "Promote me automatically.",
        });
        forgedInbox.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await forgedInbox.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("agent_identity_mismatch");

        var directDecision = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", new
        {
            agent_name = "Tank",
            type = "architectural",
            title = "Direct boundary",
            content = "Worker-created policy.",
        });
        directDecision.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await directDecision.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("coordinator_approval_required");
    }

    [Fact]
    public async Task VerifiedRunIdentity_OverridesClientAuthorshipAndPersistsProvenance()
    {
        using var factory = new InternalAuthProjectsFactory();
        using var client = factory.CreateAuthenticatedClient();
        var projectId = await CreateProjectAsync(factory, client);
        var (runId, token) = await RegisterRunAsync(factory, projectId, "Tank");
        AddRunHeaders(client, runId, token);

        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions/inbox", new
        {
            agent_name = "tank",
            slug = "verified-worker",
            type = "architectural",
            title = "Verified worker",
            content = "Await coordinator review.",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("agentName").GetString().Should().Be("Tank");
        body.GetProperty("sourceKind").GetString().Should().Be("run");
        body.GetProperty("sourceRunId").GetString().Should().Be(runId);
    }

    private static void AddRunHeaders(HttpClient client, string runId, string token)
    {
        client.DefaultRequestHeaders.Add(RunAuthorshipHeaders.RunId, runId);
        client.DefaultRequestHeaders.Add(RunAuthorshipHeaders.RunToken, token);
    }

    private static async Task<string> CreateProjectAsync(
        ProjectsWebApplicationFactory factory,
        HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Authorship Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString()!;
    }

    private static async Task<(string RunId, string Token)> RegisterRunAsync(
        ProjectsWebApplicationFactory factory,
        string projectId,
        string agentName)
    {
        var runId = RunId.New();
        await factory.Services.GetRequiredService<IRunStore>().InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = factory.NewWorkingDirectory(),
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "memory authorship test",
            SubmittingUser = "test-owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = ProjectId.Parse(projectId),
            AgentName = agentName,
        }, CancellationToken.None);

        var token = $"turn-{Guid.NewGuid():N}";
        await factory.Services.GetRequiredService<IRunAuthorshipCapabilityStore>()
            .RegisterAsync(runId.ToString(), token, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        return (runId.ToString(), token);
    }

    private sealed class InternalAuthProjectsFactory : ProjectsWebApplicationFactory
    {
        protected override IDictionary<string, string?> GetAdditionalConfiguration() =>
            new Dictionary<string, string?>
            {
                ["Testing:BypassGitHubTokenAuth"] = "false",
            };
    }
}
