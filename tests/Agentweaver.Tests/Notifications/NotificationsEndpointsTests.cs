using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Notifications;

/// <summary>
/// Integration tests for GET /api/notifications (#247 global notification center MVP).
/// Uses ProjectsWebApplicationFactory so created projects/runs are naturally owned by the
/// authenticated test user, matching the auth conventions used by ProjectEndpointsTests.
/// </summary>
public sealed class NotificationsEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public NotificationsEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private string NewWorkingDir() => _factory.NewWorkingDirectory();

    private async Task<string> CreateBlankProjectAsync(string? name = null)
    {
        var request = new CreateProjectRequest
        {
            Name = name ?? $"Notif Test Project {Guid.NewGuid():N}",
            Origin = "blank",
            WorkingDirectory = NewWorkingDir(),
        };
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<Run> InsertAwaitingReviewRunAsync(string projectId, string task, string? agentName = null, string? workflowRunId = null)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = task,
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProjectId = ProjectId.Parse(projectId),
            AgentName = agentName,
            WorkflowRunId = workflowRunId,
        };
        await runStore.InsertAsync(run);
        return run;
    }

    private async Task<Run> InsertInProgressRunAsync(string projectId, string task, string? agentName = null, string? workflowRunId = null)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = task,
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ProjectId = ProjectId.Parse(projectId),
            AgentName = agentName,
            WorkflowRunId = workflowRunId,
        };
        await runStore.InsertAsync(run);
        return run;
    }

    private async Task InsertToolApprovalRequiredEventAsync(
        string runId, string requestId, string toolName = "web_fetch", DateTime? createdAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            Sequence = 1,
            EventType = EventTypes.ToolApprovalRequired,
            PayloadJson = $$"""{"requestId":"{{requestId}}","toolName":"{{toolName}}","url":"https://example.com"}""",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertToolApprovalContextEventAsync(
        string runId, string requestId, string toolName = "coordinator_start", DateTime? createdAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            Sequence = 1,
            EventType = "tool.approval_context",
            PayloadJson = $$"""{"RequestId":"{{requestId}}","ToolName":"{{toolName}}","Url":"https://example.com"}""",
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertToolResultEventAsync(string runId, string callId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            Sequence = 2,
            EventType = EventTypes.ToolResult,
            PayloadJson = $$"""{"callId":"{{callId}}"}""",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertToolApprovalResolvedEventAsync(string runId, string requestId, bool expired)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            Sequence = 2,
            EventType = EventTypes.ToolApprovalResolved,
            PayloadJson = $$"""{"requestId":"{{requestId}}","approved":false,"expired":{{expired.ToString().ToLowerInvariant()}}}""",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> InsertOutcomeSpecAsync(MemoryDbContext db, string projectId, string coordinatorRunId)
    {
        var now = DateTimeOffset.UtcNow;
        var spec = new OutcomeSpec
        {
            ProjectId = projectId,
            CoordinatorRunId = coordinatorRunId,
            Goal = "Ship the thing",
            DesiredOutcome = "It ships",
            Scope = "in",
            Assumptions = "none",
            Status = "confirmed",
            AllowTaskPromotion = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        return spec.Id;
    }

    private async Task InsertDelegatedWorkPlanAsync(string projectId, string coordinatorRunId, DateTimeOffset? updatedAt = null)
        => await InsertWorkPlanAsync(projectId, coordinatorRunId, WorkPlanStatus.Delegated, updatedAt);

    private async Task InsertWorkPlanAsync(string projectId, string coordinatorRunId, string status, DateTimeOffset? updatedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var outcomeSpecId = await InsertOutcomeSpecAsync(db, projectId, coordinatorRunId);
        var now = updatedAt ?? DateTimeOffset.UtcNow;
        db.WorkPlans.Add(new WorkPlan
        {
            OutcomeSpecId = outcomeSpecId,
            ProjectId = projectId,
            CoordinatorRunId = coordinatorRunId,
            Status = status,
            CreatedAt = now.AddMinutes(-3),
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task InsertPromotedBacklogTaskAsync(string projectId, string parentPrdRunId, string title, string orderKey = "n", DateTimeOffset? archivedAt = null)
    {
        var backlogStore = _factory.Services.GetRequiredService<Agentweaver.Domain.IBacklogTaskStore>();
        var taskId = BacklogTaskId.New();
        var pid = ProjectId.Parse(projectId);
        await backlogStore.InsertAsync(new BacklogTask
        {
            Id = taskId,
            ProjectId = pid,
            Title = title,
            State = BacklogTaskState.Backlog,
            OrderKey = orderKey,
            CapturedBy = ProjectsWebApplicationFactory.TestUser,
            CreatedAt = DateTimeOffset.UtcNow,
            ParentPrdRunId = RunId.Parse(parentPrdRunId),
        });
        if (archivedAt is not null)
            await backlogStore.TryArchiveAsync(pid, taskId, archivedAt.Value);
    }

    private async Task InsertReviewRequestedEventAsync(string runId, DateTimeOffset createdAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var nextSequence = await db.RunEvents
            .Where(evt => evt.RunId == runId)
            .Select(evt => (int?)evt.Sequence)
            .MaxAsync() ?? 0;
        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            Sequence = nextSequence + 1,
            EventType = EventTypes.CoordinatorAssemblyReviewRequested,
            PayloadJson = """{"gateKind":"human-review"}""",
            CreatedAt = createdAt.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetNotifications_SurfacesBacklogPromotedNotification_ForDelegatedRun()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Board");
        var coordinatorRunId = RunId.New().ToString();
        await InsertDelegatedWorkPlanAsync(projectId, coordinatorRunId);
        await InsertPromotedBacklogTaskAsync(projectId, coordinatorRunId, "Build the API", orderKey: "n");
        await InsertPromotedBacklogTaskAsync(projectId, coordinatorRunId, "Build the Web", orderKey: "u");

        var response = await _client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("notifications").EnumerateArray()
            .Should().ContainSingle(n => n.GetProperty("id").GetString() == $"backlog_promoted:{coordinatorRunId}")
            .Subject;

        match.GetProperty("type").GetString().Should().Be("backlog_promoted");
        match.GetProperty("project_id").GetString().Should().Be(projectId);
        match.GetProperty("title").GetString().Should().Be("2 subtasks created");
        match.GetProperty("cta_path").GetString().Should().Be($"/projects/{projectId}/board");
    }

    [Fact]
    public async Task GetNotifications_BacklogPromoted_UsesSingularTitle_ForOneTask()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Board Single");
        var coordinatorRunId = RunId.New().ToString();
        await InsertDelegatedWorkPlanAsync(projectId, coordinatorRunId);
        await InsertPromotedBacklogTaskAsync(projectId, coordinatorRunId, "The only task");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == $"backlog_promoted:{coordinatorRunId}");

        match.GetProperty("title").GetString().Should().Be("1 subtask created");
    }

    [Fact]
    public async Task GetNotifications_BacklogPromoted_ExcludesNonDelegatedPlans()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Board NonDelegated");
        var coordinatorRunId = RunId.New().ToString();
        await InsertWorkPlanAsync(projectId, coordinatorRunId, WorkPlanStatus.Complete);
        await InsertPromotedBacklogTaskAsync(projectId, coordinatorRunId, "A promoted task");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("id").GetString() == $"backlog_promoted:{coordinatorRunId}");
    }

    [Fact]
    public async Task GetNotifications_BacklogPromoted_ExcludesDelegatedRunWithNoLiveTasks()
    {
        // Defensive: a delegated plan whose promoted tasks were all archived has nothing to announce.
        var projectId = await CreateBlankProjectAsync("Notif Project Board Archived");
        var coordinatorRunId = RunId.New().ToString();
        await InsertDelegatedWorkPlanAsync(projectId, coordinatorRunId);
        await InsertPromotedBacklogTaskAsync(projectId, coordinatorRunId, "Archived task", archivedAt: DateTimeOffset.UtcNow);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("id").GetString() == $"backlog_promoted:{coordinatorRunId}");
    }

    [Fact]
    public async Task GetNotifications_BacklogPromoted_ExcludesStaleDelegatedRun()
    {
        // Outside the 24h recency window: the terminal delegated run should no longer surface.
        var projectId = await CreateBlankProjectAsync("Notif Project Board Stale");
        var coordinatorRunId = RunId.New().ToString();
        await InsertDelegatedWorkPlanAsync(projectId, coordinatorRunId, updatedAt: DateTimeOffset.UtcNow.AddHours(-30));
        await InsertPromotedBacklogTaskAsync(projectId, coordinatorRunId, "Old task");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("id").GetString() == $"backlog_promoted:{coordinatorRunId}");
    }

    [Fact]
    public async Task GetNotifications_SurfacesOwnedAwaitingReviewRun()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project A");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Implement the checkout flow", "Coordinator", "wf-run-1");
        var reviewRequestedAt = DateTimeOffset.Parse("2026-07-28T01:02:03Z");
        await InsertReviewRequestedEventAsync(run.Id.ToString(), reviewRequestedAt);

        var response = await _client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        var match = notifications.Should().ContainSingle(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .Subject;
        match.GetProperty("type").GetString().Should().Be("human_review");
        match.GetProperty("id").GetString().Should().StartWith($"review:{run.Id}:");
        match.GetProperty("project_id").GetString().Should().Be(projectId);
        match.GetProperty("agent_name").GetString().Should().Be("Coordinator");
        match.GetProperty("title").GetString().Should().Be("Implement the checkout flow");
        match.GetProperty("cta_path").GetString().Should().Be($"/projects/{projectId}/orchestrations/{run.Id}");
    }

    [Fact]
    public async Task GetNotifications_UsesRunIdInCtaPath_WhenWorkflowRunIdIsMissing()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project B");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Legacy run with no workflow id");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        match.GetProperty("cta_path").GetString().Should().Be($"/projects/{projectId}/orchestrations/{run.Id}");
    }

    [Fact]
    public async Task GetNotifications_ExcludesNonAwaitingReviewRuns()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project C");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var inProgress = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Still working",
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = ProjectId.Parse(projectId),
        };
        await runStore.InsertAsync(inProgress);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == inProgress.Id.ToString());
    }

    [Fact]
    public async Task GetNotifications_ExcludesArchivedRuns()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project D");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Archived review");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.ArchiveAsync(run.Id, DateTimeOffset.UtcNow);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
    }

    [Fact]
    public async Task GetNotifications_ExcludesOtherUsersRuns()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project E");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var othersProject = ProjectId.New();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Someone else's review",
            SubmittingUser = "someone-else",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow,
            ProjectId = othersProject,
        };
        // No project row is inserted for othersProject, so it can never be "owned" by the caller —
        // this exercises the project-ownership filter, not just SubmittingUser matching.
        await runStore.InsertAsync(run);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
        _ = projectId; // keep the owned project created so the factory/db is exercised consistently
    }

    [Fact]
    public async Task GetNotifications_TruncatesLongTaskIntoTitle()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project F");
        var longTask = string.Concat(Enumerable.Repeat("word ", 40)).Trim();
        var run = await InsertAwaitingReviewRunAsync(projectId, longTask);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        var title = match.GetProperty("title").GetString();
        title.Should().NotBeNull();
        title!.Length.Should().BeLessThanOrEqualTo(121, "the title should be truncated to ~120 chars plus the ellipsis");
        title.Should().EndWith("\u2026");
    }

    [Fact]
    public async Task GetNotifications_SurfacesOwnedPendingToolApprovalRun()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project G");
        var run = await InsertInProgressRunAsync(projectId, "Fetch the release notes", "Researcher", "wf-run-2");
        await InsertToolApprovalRequiredEventAsync(run.Id.ToString(), "toolu_01pending", "web_fetch");

        var response = await _client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        var match = notifications.Should().ContainSingle(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .Subject;
        match.GetProperty("type").GetString().Should().Be("tool_approval");
        match.GetProperty("project_id").GetString().Should().Be(projectId);
        match.GetProperty("agent_name").GetString().Should().Be("Researcher");
        match.GetProperty("cta_path").GetString().Should().Be($"/projects/{projectId}/orchestrations/{run.Id}");
        match.GetProperty("id").GetString().Should().Be($"tool_approval:{run.Id}:toolu_01pending");
    }

    [Fact]
    public async Task GetNotifications_ToolApprovalCta_UsesPendingRunId_WhenConcurrentRunHasItsWorkflowId()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Concurrent Runs");
        var newerDraft = await InsertInProgressRunAsync(projectId, "Newer unrelated draft", "Coordinator");
        var pendingApprovalRun = await InsertInProgressRunAsync(
            projectId,
            "Run waiting for approval",
            "Researcher",
            newerDraft.Id.ToString());
        await InsertToolApprovalRequiredEventAsync(
            pendingApprovalRun.Id.ToString(),
            "toolu_01concurrent",
            "start_preview");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notification = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == pendingApprovalRun.Id.ToString());

        notification.GetProperty("cta_path").GetString()
            .Should().Be($"/projects/{projectId}/orchestrations/{pendingApprovalRun.Id}");
        notification.GetProperty("cta_path").GetString()
            .Should().NotBe($"/projects/{projectId}/orchestrations/{newerDraft.Id}");
    }

    [Fact]
    public async Task GetNotifications_ExcludesResolvedToolApproval()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project H");
        var run = await InsertInProgressRunAsync(projectId, "Fetch and resolve", "Researcher");
        await InsertToolApprovalRequiredEventAsync(run.Id.ToString(), "toolu_01resolved", "web_fetch");
        // The callId defaults to the requestId in this test's minimal payload shape.
        await InsertToolResultEventAsync(run.Id.ToString(), "toolu_01resolved");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
    }

    [Fact]
    public async Task GetNotifications_ExcludesExpiredToolApprovalResolution()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Expired");
        var run = await InsertInProgressRunAsync(projectId, "Preview approval", "Coordinator");
        await InsertToolApprovalRequiredEventAsync(run.Id.ToString(), "preview-expired", "start_preview");
        await InsertToolApprovalResolvedEventAsync(run.Id.ToString(), "preview-expired", expired: true);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
    }

    [Fact]
    public async Task GetNotifications_DoesNotDoubleFire_HumanReviewAndToolApproval_ForSameRun()
    {
        // A run cannot be both AwaitingReview and InProgress at once, so a pending tool approval on
        // an InProgress run must surface exactly one notification (tool_approval), never both types.
        var projectId = await CreateBlankProjectAsync("Notif Project I");
        var run = await InsertInProgressRunAsync(projectId, "Fetch once", "Researcher");
        await InsertToolApprovalRequiredEventAsync(run.Id.ToString(), "toolu_01single", "web_fetch");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var forRun = body.GetProperty("notifications").EnumerateArray()
            .Where(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .ToList();

        forRun.Should().ContainSingle();
        forRun[0].GetProperty("type").GetString().Should().Be("tool_approval");
    }

    [Fact]
    public async Task GetNotifications_SurfacesPendingDurableToolApprovalContext()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Durable Context");
        var run = await InsertInProgressRunAsync(projectId, "Start a coordinator run", "Operator");
        await InsertToolApprovalContextEventAsync(run.Id.ToString(), "toolu_ctx_01pending", "coordinator_start");

        var response = await _client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notification = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        notification.GetProperty("type").GetString().Should().Be("tool_approval");
        notification.GetProperty("id").GetString().Should().Be($"tool_approval:{run.Id}:toolu_ctx_01pending");
    }

    [Fact]
    public async Task GetNotifications_OperatorToolApproval_UsesAssistantSessionCta()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project Assistant Session");
        var run = await InsertInProgressRunAsync(projectId, "Start a coordinator run", "Operator");
        await InsertToolApprovalContextEventAsync(run.Id.ToString(), "toolu_ctx_01assistant", "coordinator_start");

        var response = await _client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notification = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        notification.GetProperty("cta_path").GetString()
            .Should().Be($"/assistant?runId={run.Id}&project={projectId}");
    }

    [Fact]
    public async Task GetNotifications_ToolApprovalId_IsStableAcrossPolls()
    {
        // Client-side de-duplication relies on a stable id across polling intervals, mirroring the
        // "review:{runId}" stability guarantee already covered for human_review.
        var projectId = await CreateBlankProjectAsync("Notif Project J");
        var run = await InsertInProgressRunAsync(projectId, "Fetch repeatedly", "Researcher");
        await InsertToolApprovalRequiredEventAsync(run.Id.ToString(), "toolu_01stable", "web_fetch");

        var first = await _client.GetAsync("/api/notifications");
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstBody.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .GetProperty("id").GetString();

        var second = await _client.GetAsync("/api/notifications");
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondBody.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .GetProperty("id").GetString();

        secondId.Should().Be(firstId);
    }

    [Fact]
    public async Task GetNotifications_DismissedReview_ReappearsWhenSameRunRequestsReviewAgain()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project L");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Review me again", "Coordinator", "wf-run-3");
        var firstReviewRequestedAt = DateTimeOffset.Parse("2026-07-28T01:00:00Z");
        await InsertReviewRequestedEventAsync(run.Id.ToString(), firstReviewRequestedAt);

        var first = await _client.GetAsync("/api/notifications");
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstBody.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .GetProperty("id").GetString();
        firstId.Should().StartWith($"review:{run.Id}:");

        var dismiss = await _client.PostAsync($"/api/notifications/{Uri.EscapeDataString(firstId!)}/dismiss", content: null);
        dismiss.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDismiss = await _client.GetAsync("/api/notifications");
        var afterDismissBody = await afterDismiss.Content.ReadFromJsonAsync<JsonElement>();
        afterDismissBody.GetProperty("notifications").EnumerateArray()
            .Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        var secondReviewRequestedAt = DateTimeOffset.Parse("2026-07-28T01:05:00Z");
        await InsertReviewRequestedEventAsync(run.Id.ToString(), secondReviewRequestedAt);

        var second = await _client.GetAsync("/api/notifications");
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondBody.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .GetProperty("id").GetString();

        secondId.Should().StartWith($"review:{run.Id}:");
        secondId.Should().NotBe(firstId);
    }

    [Fact]
    public async Task GetNotifications_ExcludesOtherUsersToolApproval()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project K");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var othersProject = ProjectId.New();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Someone else's approval",
            SubmittingUser = "someone-else",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ProjectId = othersProject,
        };
        await runStore.InsertAsync(run);
        await InsertToolApprovalRequiredEventAsync(run.Id.ToString(), "toolu_01others", "web_fetch");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
        _ = projectId;
    }
}
