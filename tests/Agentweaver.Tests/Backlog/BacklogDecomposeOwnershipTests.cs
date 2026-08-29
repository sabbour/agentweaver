using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Backlog;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentweaver.Tests.Backlog;

public sealed class BacklogDecomposeOwnershipTests : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public BacklogDecomposeOwnershipTests(CoordinatorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task OutcomeSpecDecomposeConfirm_CreatesTasksOwnedByRequester_AndPickupRunStaysOwnerScoped()
    {
        using var app = CreateApp();
        using var owner = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OwnerApiKey);
        using var other = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OtherApiKey);

        var projectId = await CreateProjectAsync(owner);
        var sourceRunId = await InsertConfirmedOutcomeSpecAsync(
            app.Services, projectId, CoordinatorWebApplicationFactory.OwnerUser);

        var decompose = await owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/decompose",
            new { run_id = sourceRunId, confirm = true });

        var decomposeBody = await decompose.Content.ReadAsStringAsync();
        decompose.StatusCode.Should().Be(HttpStatusCode.OK, decomposeBody);

        var task = await GetOnlyTaskAsync(app.Services, projectId);
        task.CapturedBy.Should().Be(
            CoordinatorWebApplicationFactory.OwnerUser,
            "decomposed tasks must be accountable to the requesting user, not a synthetic 'decompose' owner");
        task.CapturedBy.Should().NotBe("decompose");
        task.CapturedBy.Should().NotBe("agentweaver-internal");
        task.SourceFilePath.Should().Be($"__outcome-spec__/{sourceRunId}");

        var pickupRunId = await ReservePickupRunFromTaskAsync(app.Services, projectId, task);

        (await owner.GetAsync($"/api/runs/{pickupRunId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.GetAsync($"/api/runs/{pickupRunId}")).StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the ownership fix must not broaden access to non-owners");
    }

    [Fact]
    public async Task OutcomeSpecDecompose_NonOwner_ReturnsForbidden_AndCreatesNoTasks()
    {
        using var app = CreateApp();
        using var owner = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OwnerApiKey);
        using var other = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OtherApiKey);

        var projectId = await CreateProjectAsync(owner);
        var sourceRunId = await InsertConfirmedOutcomeSpecAsync(
            app.Services, projectId, CoordinatorWebApplicationFactory.OwnerUser);

        var decompose = await other.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/decompose",
            new { run_id = sourceRunId, confirm = true });

        decompose.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var backlogStore = app.Services.GetRequiredService<IBacklogTaskStore>();
        var tasks = await backlogStore.ListByProjectAsync(ProjectId.Parse(projectId));
        tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkspaceFilesNoRef_NonOwner_ReturnsForbidden()
    {
        using var app = CreateApp();
        using var owner = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OwnerApiKey);
        using var other = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OtherApiKey);

        var projectId = await CreateProjectAsync(owner);

        var resp = await other.GetAsync($"/api/projects/{projectId}/workspace/files");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OutcomeSpecDecompose_InvalidRunId_ReturnsBadRequest_AndCreatesNoTasks()
    {
        using var app = CreateApp();
        using var owner = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OwnerApiKey);

        var projectId = await CreateProjectAsync(owner);

        var decompose = await owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/decompose",
            new { run_id = "not-a-run-id", confirm = true });

        decompose.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var backlogStore = app.Services.GetRequiredService<IBacklogTaskStore>();
        var tasks = await backlogStore.ListByProjectAsync(ProjectId.Parse(projectId));
        tasks.Should().BeEmpty();
    }

    [Fact]
    public async Task OutcomeSpecDecompose_RunFromDifferentProject_ReturnsNotFound_AndCreatesNoTasks()
    {
        using var app = CreateApp();
        using var owner = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OwnerApiKey);

        var sourceProjectId = await CreateProjectAsync(owner);
        var targetProjectId = await CreateProjectAsync(owner);
        var sourceRunId = await InsertConfirmedOutcomeSpecAsync(
            app.Services, sourceProjectId, CoordinatorWebApplicationFactory.OwnerUser);

        var decompose = await owner.PostAsJsonAsync(
            $"/api/projects/{targetProjectId}/backlog/decompose",
            new { run_id = sourceRunId, confirm = true });

        decompose.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var backlogStore = app.Services.GetRequiredService<IBacklogTaskStore>();
        var targetTasks = await backlogStore.ListByProjectAsync(ProjectId.Parse(targetProjectId));
        targetTasks.Should().BeEmpty();
    }

    [Fact]
    public async Task OutcomeSpecDecompose_WithoutCopilotConnection_ReturnsSharedConnectAction()
    {
        using var app = CreateApp(useFakeDecomposeService: false);
        using var owner = CreateClientWithKey(app, CoordinatorWebApplicationFactory.OwnerApiKey);
        var projectId = await CreateProjectAsync(owner);
        var sourceRunId = await InsertConfirmedOutcomeSpecAsync(
            app.Services, projectId, CoordinatorWebApplicationFactory.OwnerUser);

        var response = await owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/decompose",
            new { run_id = sourceRunId, confirm = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var requirement = await response.Content.ReadFromJsonAsync<GitHubCopilotConnectionRequirement>();
        requirement.Should().NotBeNull();
        requirement!.Code.Should().Be(GitHubCopilotConnectionRequirement.RequirementCode);
        requirement.Message.Should().Be(GitHubCopilotConnectionRequirement.RequirementMessage);
        requirement.Action.Type.Should().Be(GitHubCopilotConnectionAction.ConnectProjectCopilotApp);
        requirement.Action.ProjectId.Should().Be(projectId);

        var tasks = await app.Services.GetRequiredService<IBacklogTaskStore>()
            .ListByProjectAsync(ProjectId.Parse(projectId));
        tasks.Should().BeEmpty();
    }

    private WebApplicationFactory<Program> CreateApp(bool useFakeDecomposeService = true) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                if (useFakeDecomposeService)
                {
                    services.RemoveAll<IBacklogDecomposeService>();
                    services.AddSingleton<IBacklogDecomposeService, FakeBacklogDecomposeService>();
                }
            });
        });

    private static HttpClient CreateClientWithKey(WebApplicationFactory<Program> app, string apiKey)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Decompose Ownership Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the owner should be able to create the project");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }

    private static async Task<string> InsertConfirmedOutcomeSpecAsync(
        IServiceProvider services,
        string projectId,
        string submittingUser)
    {
        var pid = ProjectId.Parse(projectId);
        var project = await services.GetRequiredService<IProjectStore>().GetAsync(pid);
        project.Should().NotBeNull();

        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = project!.WorkingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "owner-scoped outcome spec",
            SubmittingUser = submittingUser,
            Status = RunStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            ProjectId = pid,
            ModelId = "gpt-4o",
            AgentName = "Coordinator",
            WorkflowRunId = null,
            Origin = RunOrigin.Interactive,
        };

        await services.GetRequiredService<SqliteRunStore>().InsertAsync(run);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.OutcomeSpecs.Add(new OutcomeSpec
        {
            ProjectId = projectId,
            CoordinatorRunId = run.Id.ToString(),
            Goal = "Ship owner-scoped generated tasks",
            DesiredOutcome = "Generated backlog items produce runs the requester can open.",
            Scope = "Backend ownership propagation.",
            Assumptions = "The outcome spec has already been confirmed.",
            Status = "confirmed",
            ConfirmedBy = submittingUser,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        return run.Id.ToString();
    }

    private static async Task<BacklogTask> GetOnlyTaskAsync(IServiceProvider services, string projectId)
    {
        var backlogStore = services.GetRequiredService<IBacklogTaskStore>();
        var tasks = await backlogStore.ListByProjectAsync(ProjectId.Parse(projectId));
        return tasks.Should().ContainSingle().Subject;
    }

    private static async Task<string> ReservePickupRunFromTaskAsync(
        IServiceProvider services,
        string projectId,
        BacklogTask task)
    {
        var pid = ProjectId.Parse(projectId);
        var backlogStore = services.GetRequiredService<IBacklogTaskStore>();
        var project = await services.GetRequiredService<IProjectStore>().GetAsync(pid);
        project.Should().NotBeNull();

        var moved = await backlogStore.TryMoveToReadyAsync(
            pid, task.Id, OrderKey.Between(null, null), DateTimeOffset.UtcNow);
        moved.Should().BeTrue();

        var readyTask = await backlogStore.GetAsync(pid, task.Id);
        readyTask.Should().NotBeNull();

        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = project!.WorkingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = readyTask!.Title,
            SubmittingUser = readyTask.CapturedBy,
            Status = RunStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            ProjectId = pid,
            ModelId = "gpt-4o",
            AgentName = "Coordinator",
            WorkflowRunId = null,
            Origin = RunOrigin.BacklogPickup,
        };

        var claim = await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            pid, readyTask.Id, run, DateTimeOffset.UtcNow);
        claim.Should().Be(ClaimReserveResult.Won);
        return run.Id.ToString();
    }

    private sealed class FakeBacklogDecomposeService : IBacklogDecomposeService
    {
        public Task<DecomposeAgentResult> DecomposeAsync(
            Project project,
            string fileContent,
            CallerContext caller,
            CancellationToken ct) =>
            Task.FromResult(new DecomposeAgentResult(
                new[] { new ProposedItem("Implement ownership propagation", "Keep generated runs owner-scoped.") },
                WasCapped: false,
                TotalFound: 1));
    }
}
