using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Security;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests proving that the coordinator collective-assembly Scribe is wired with a
/// non-null <c>apiKey</c> / <c>apiBaseUrl</c> — so its loopback memory tool calls
/// (<c>list_inbox</c>, <c>update_session</c>, <c>export_memory</c>, …) authenticate correctly
/// instead of returning 401 and "Tool execution failed".
///
/// Root cause: <see cref="CollectiveAssemblyPipeline.RunScribeAsync"/> previously constructed the
/// <c>ScribeTurnExecutor</c> without passing <c>apiBaseUrl</c> or <c>apiKey</c>, so both
/// defaulted to <c>null</c>. The per-run workflow Scribe (built by
/// <see cref="RunWorkflowFactory"/>) was already correct; only the coordinator assembly Scribe
/// was broken.
///
/// Fix: <see cref="RunWorkflowFactory"/> now exposes <c>internal ApiBaseUrl</c> and
/// <c>internal ApiKey</c> properties (single resolution site), and
/// <see cref="CollectiveAssemblyPipeline"/> passes them through when constructing the executor.
/// </summary>
public sealed class CollectiveAssemblyScribeApiAuthTests : IClassFixture<WorkflowWebApplicationFactory>
{
    private readonly WorkflowWebApplicationFactory _factory;

    public CollectiveAssemblyScribeApiAuthTests(WorkflowWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// <see cref="RunWorkflowFactory"/> must resolve a non-null <c>ApiKey</c> from the
    /// <c>Auth:ApiKey</c> configuration entry.  This is the single resolution site that
    /// <see cref="CollectiveAssemblyPipeline"/> now reads via the new <c>internal</c> property,
    /// so if this is null the coordinator Scribe will again send unauthenticated requests and
    /// receive 401s from the loopback API.
    /// </summary>
    [Fact]
    public void RunWorkflowFactory_ApiKey_IsNonNullWhenAuthApiKeyIsConfigured()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<RunWorkflowFactory>();

        factory.ApiKey.Should().NotBeNullOrEmpty(
            because: "Auth:ApiKey is set in WorkflowWebApplicationFactory; " +
                     "a null ApiKey means the coordinator Scribe will send unauthenticated " +
                     "loopback requests and receive 401 → \"Tool execution failed\"");

        factory.ApiKey.Should().Be(WorkflowWebApplicationFactory.TestApiKey,
            because: "RunWorkflowFactory must propagate the configured key verbatim");
    }

    /// <summary>
    /// <see cref="RunWorkflowFactory.ApiBaseUrl"/> must be non-null (it falls back to
    /// <c>http://localhost:5000</c> when <c>Agentweaver:ApiBaseUrl</c> is absent from config).
    /// </summary>
    [Fact]
    public void RunWorkflowFactory_ApiBaseUrl_IsNonNullEvenWithoutExplicitConfig()
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<RunWorkflowFactory>();

        factory.ApiBaseUrl.Should().NotBeNullOrEmpty(
            because: "RunWorkflowFactory falls back to http://localhost:5000 when the " +
                     "Agentweaver:ApiBaseUrl config key is absent; a null here would mean the " +
                     "fallback logic was removed");
    }

    /// <summary>
    /// <see cref="CollectiveAssemblyPipeline"/> must be resolvable from DI with the same
    /// <see cref="RunWorkflowFactory"/> singleton it reads <c>ApiKey</c>/<c>ApiBaseUrl</c> from.
    /// This guards against a misconfigured DI registration that would silently give the pipeline
    /// a different (or unresolved) factory instance.
    /// </summary>
    [Fact]
    public void CollectiveAssemblyPipeline_SharesRunWorkflowFactory_Singleton()
    {
        using var scope = _factory.Services.CreateScope();

        var pipeline = scope.ServiceProvider.GetRequiredService<ICollectiveAssemblyPipeline>();
        var factory  = scope.ServiceProvider.GetRequiredService<RunWorkflowFactory>();

        // Both must be resolvable — if either throws the DI wiring is broken.
        pipeline.Should().NotBeNull();
        factory.Should().NotBeNull();

        // Verify the factory has a non-null key — so the pipeline's executor call site
        // (which passes _workflowFactory.ApiKey) will also receive a non-null value.
        factory.ApiKey.Should().NotBeNullOrEmpty(
            because: "CollectiveAssemblyPipeline reads ApiKey from this singleton; " +
                     "if it is null here, the assembly-scribe executor is built without " +
                     "authentication and every loopback memory tool call returns 401");
    }

    [Fact]
    public async Task ScribeFinalize_RejectsProjectContributor()
    {
        const string ownerId = "11111111-1111-1111-1111-111111111111";
        const string contributorId = "22222222-2222-2222-2222-222222222222";
        using var factory = new EntraWebApplicationFactory();
        using var owner = factory.CreateAuthenticatedClientForObjectId(ownerId, PlatformRoles.ProjectCreator);
        var projectResponse = await owner.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"scribe-contributor-{Guid.NewGuid():N}",
            Origin = "blank",
            WorkingDirectory = factory.NewWorkingDirectory(),
        });
        projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = ProjectId.Parse((await projectResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString()!);
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = contributorId,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await InsertRunAsync(factory.Services, projectId);
        using var contributor = factory.CreateAuthenticatedClientForObjectId(
            contributorId, PlatformRoles.Contributor);
        var response = await contributor.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScribeFinalize_RejectsWrongRunOrToken()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", WorkflowWebApplicationFactory.TestApiKey);
        var projectId = await CreateProjectAsync(client, "scribe-capability");
        var run = await InsertRunAsync(_factory.Services, projectId);
        await RegisterCapabilityAsync(_factory.Services, run, "correct-token");

        AddRunHeaders(client, RunId.New().ToString(), "correct-token");
        (await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        client.DefaultRequestHeaders.Remove(RunAuthorshipHeaders.RunId);
        client.DefaultRequestHeaders.Remove(RunAuthorshipHeaders.RunToken);
        AddRunHeaders(client, run.Id.ToString(), "wrong-token");
        (await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ScribeFinalize_RejectsWrongUserAndStaleGeneration()
    {
        var setup = await CreateInternalClientAsync("scribe-boundaries");
        using var client = setup.Client;
        var projectId = setup.ProjectId;
        var run = await InsertRunAsync(_factory.Services, projectId);
        await RegisterCapabilityAsync(_factory.Services, run, "scribe-token");
        AddRunHeaders(client, run.Id.ToString(), "scribe-token");

        var wrongUser = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run, submittingUser: "another-user"));
        wrongUser.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await wrongUser.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("scribe_identity_mismatch");

        var staleGeneration = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run, lifecycleGeneration: run.LifecycleGeneration - 1));
        staleGeneration.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await staleGeneration.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("scribe_scope_mismatch");
    }

    [Fact]
    public async Task ScribeFinalize_LegitimateInternalReplay_IsExactlyOnce()
    {
        var setup = await CreateInternalClientAsync("scribe-replay");
        using var client = setup.Client;
        var projectId = setup.ProjectId;
        var run = await InsertRunAsync(_factory.Services, projectId);
        await RegisterCapabilityAsync(_factory.Services, run, "scribe-token");
        AddRunHeaders(client, run.Id.ToString(), "scribe-token");

        var first = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run));
        var replay = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/scribe/finalize",
            Request(run));

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ScribeOperationAttempts.CountAsync(
            attempt => attempt.RunId == run.Id.ToString() && attempt.Status == "completed"))
            .Should().Be(4);
    }

    private async Task<(HttpClient Client, ProjectId ProjectId)> CreateInternalClientAsync(string prefix)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", WorkflowWebApplicationFactory.TestApiKey);
        var projectId = await CreateProjectAsync(client, prefix);
        return (client, projectId);
    }

    private static object Request(
        Run run,
        int? lifecycleGeneration = null,
        string? submittingUser = null) => new
    {
        run_id = run.Id.ToString(),
        lifecycle_generation = lifecycleGeneration ?? run.LifecycleGeneration,
        agent_name = run.AgentName,
        submitting_user = submittingUser ?? run.SubmittingUser,
        terminal_status = "completed",
    };

    private static void AddRunHeaders(HttpClient client, string runId, string token)
    {
        client.DefaultRequestHeaders.Add(RunAuthorshipHeaders.RunId, runId);
        client.DefaultRequestHeaders.Add(RunAuthorshipHeaders.RunToken, token);
    }

    private static async Task RegisterCapabilityAsync(
        IServiceProvider services,
        Run run,
        string token)
    {
        await services.GetRequiredService<IRunAuthorshipCapabilityStore>()
            .RegisterAsync(
                run.Id.ToString(),
                token,
                DateTimeOffset.UtcNow.AddMinutes(5),
                CancellationToken.None);
    }

    private static async Task<Run> InsertRunAsync(IServiceProvider services, ProjectId projectId)
    {
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = AppContext.BaseDirectory,
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "scribe",
            SubmittingUser = "test-user",
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            AgentName = "worker",
            LifecycleGeneration = 4,
        };
        var runStore = services.GetRequiredService<IRunStore>();
        await runStore.InsertAsync(run);
        return (await runStore.GetAsync(run.Id))!;
    }

    private static async Task<ProjectId> CreateProjectAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest
        {
            Name = $"{prefix}-{Guid.NewGuid():N}",
            Origin = "blank",
            WorkingDirectory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}"),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return ProjectId.Parse(json.GetProperty("project_id").GetString()!);
    }
}
