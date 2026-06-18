using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Integration tests for the Feature 008 Phase 2 coordinator HTTP endpoints (Tank's wave):
/// <c>GET /api/runs/{coordinatorRunId}/work-plan</c>, <c>GET .../children</c>, and
/// <c>POST .../steer</c>.
///
/// Each test runs against a real in-process API host, a real SQLite database, and the real
/// <see cref="CoordinatorRunService"/>/<see cref="Agentweaver.Api.Coordinator.CoordinatorSteeringService"/>;
/// the only seam is the signed-out <see cref="SignedOutGitHubTokenStore"/> baked into
/// <see cref="CoordinatorWebApplicationFactory"/> (no mocks, Principle VII). Auto-dispatch is off in
/// the harness, so children stays empty and the work plan is deterministic.
///
/// Coverage:
///   - work-plan: 200 + camelCase shape after confirm decomposes a plan; 404 when the run has no
///     plan (not a coordinator run); 404 unknown run; 400 invalid id; 403 non-owner.
///   - children: 200 empty array when nothing is dispatched; 404 unknown run; 403 non-owner.
///   - steer: 400 on the descoped 'pause' verb; 400 on an unknown verb; 400 when a redirect/amend
///     omits the instruction; 201 + camelCase view on a valid stop; 403 non-owner; 404 unknown run.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorPhase2EndpointsTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public CoordinatorPhase2EndpointsTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
        _other = _factory.CreateOtherClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _other.Dispose();
        _factory.Dispose();
    }

    // =========================================================================
    // work-plan: 200 + shape after confirm decomposes a plan.
    // =========================================================================
    [Fact]
    public async Task WorkPlan_AfterConfirm_Returns200_WithCamelCaseShape()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Build a deterministic work plan for the work-plan endpoint");
        await WaitForGateAsync(runId);

        var confirm = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // Orchestration persists the plan asynchronously; poll the endpoint until it materializes.
        var plan = await PollWorkPlanAsync(runId);
        plan.Should().NotBeNull("confirm must route to orchestration and the endpoint must surface the plan");
        plan!.WorkPlanId.Should().BeGreaterThan(0);
        plan.CoordinatorRunId.Should().Be(runId);
        plan.OutcomeSpecId.Should().BeGreaterThan(0);
        plan.Status.Should().Be("planned");
        plan.Subtasks.Should().NotBeEmpty("the plan must decompose into at least one subtask");
        plan.Subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.AssignedAgent));
        plan.Subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.SelectedModelId));
        plan.Subtasks.Should().OnlyContain(s => s.ChildRunId == null,
            "no child run is dispatched while auto-dispatch is off");
        plan.Dependencies.Should().OnlyContain(d => d.SubtaskId != d.DependsOnSubtaskId);
    }

    // =========================================================================
    // work-plan: 404 when the run exists but has no plan (not a coordinator plan yet).
    // =========================================================================
    [Fact]
    public async Task WorkPlan_RunWithoutPlan_Returns404()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.GetAsync($"/api/runs/{runId}/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a run with no persisted work plan must 404 from the work-plan endpoint");
    }

    [Fact]
    public async Task WorkPlan_UnknownRun_Returns404()
    {
        var resp = await _owner.GetAsync($"/api/runs/{RunId.New()}/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WorkPlan_InvalidRunId_Returns400()
    {
        var resp = await _owner.GetAsync("/api/runs/not-a-guid/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WorkPlan_NonOwner_Returns403()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.GetAsync($"/api/runs/{runId}/work-plan");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner work-plan reads must be 403");
    }

    // =========================================================================
    // children: 200 empty array when nothing is dispatched.
    // =========================================================================
    [Fact]
    public async Task Children_NothingDispatched_Returns200_EmptyArray()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.GetAsync($"/api/runs/{runId}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "children is always 200 for an owned run");
        var children = await resp.Content.ReadFromJsonAsync<List<CoordinatorChildResponse>>();
        children.Should().NotBeNull();
        children!.Should().BeEmpty("auto-dispatch is off, so no child runs exist");
    }

    [Fact]
    public async Task Children_UnknownRun_Returns404()
    {
        var resp = await _owner.GetAsync($"/api/runs/{RunId.New()}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Children_NonOwner_Returns403()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.GetAsync($"/api/runs/{runId}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // =========================================================================
    // steer: validation -> 400.
    // =========================================================================
    [Fact]
    public async Task Steer_PauseVerb_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "pause", instruction = "hold on" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the descoped 'pause' verb maps to 400");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("steering_invalid");
    }

    [Fact]
    public async Task Steer_UnknownVerb_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "explode", instruction = "boom" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown verb maps to 400");
    }

    [Fact]
    public async Task Steer_RedirectWithoutInstruction_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { kind = "redirect" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "redirect requires a non-empty instruction");
    }

    [Fact]
    public async Task Steer_MissingKind_Returns400()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer",
            new { instruction = "no verb here" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "kind is required");
    }

    // =========================================================================
    // steer: a valid stop (instruction may be omitted) -> 201 + camelCase view.
    // =========================================================================
    [Fact]
    public async Task Steer_Stop_NoInstruction_Returns201_WithDirectiveView()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/steer", new { kind = "stop" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created, "a valid stop creates a directive (201)");
        var directive = await resp.Content.ReadFromJsonAsync<SteeringDirectiveResponse>();
        directive.Should().NotBeNull();
        directive!.Id.Should().BeGreaterThan(0);
        directive.CoordinatorRunId.Should().Be(runId);
        directive.Kind.Should().Be("stop");
        directive.Status.Should().Be("applied", "stop collapses to applied immediately");
        directive.CreatedBy.Should().Be(CoordinatorWebApplicationFactory.OwnerUser,
            "createdBy must be the authenticated caller");
    }

    [Fact]
    public async Task Steer_NonOwner_Returns403()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.PostAsJsonAsync($"/api/runs/{runId}/steer", new { kind = "stop" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "non-owner steering must be 403");
    }

    [Fact]
    public async Task Steer_UnknownRun_Returns404()
    {
        var resp = await _owner.PostAsJsonAsync($"/api/runs/{RunId.New()}/steer", new { kind = "stop" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // Feature 008: coordinator orchestration status + failure reason surfacing.
    // A coordinator run parked at a terminal assembly status must expose the work-plan status on the
    // run detail (coordinator_status) and the failure reason on the work-plan (statusReason), so the
    // UI never shows a bare "Failed" the user can't act on.
    // =========================================================================
    [Fact]
    public async Task RunDetail_And_WorkPlan_SurfaceCoordinatorStatusAndReason_ForBlockedAssembly()
    {
        var runId = await InsertInactiveCoordinatorRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        // Park the run at a terminal blocked assembly: run Failed + reason, work plan assembly_blocked.
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.UpdateResultAsync(
            RunId.Parse(runId), RunStatus.Failed, "assembly_blocked: integration_conflict",
            DateTimeOffset.UtcNow, CancellationToken.None);
        await SeedWorkPlanAsync(runId, "assembly_blocked");

        var detail = await _owner.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}");
        detail.GetProperty("status").GetString().Should().Be("failed");
        detail.GetProperty("coordinator_status").GetString().Should().Be("assembly_blocked");
        detail.GetProperty("result").GetString().Should().Be("assembly_blocked: integration_conflict");
        detail.GetProperty("coordinator_status_reason").GetString().Should().Be("assembly_blocked: integration_conflict");

        var plan = await _owner.GetFromJsonAsync<JsonElement>($"/api/runs/{runId}/work-plan");
        plan.GetProperty("status").GetString().Should().Be("assembly_blocked");
        plan.GetProperty("statusReason").GetString().Should().Be("assembly_blocked: integration_conflict");
    }

    /// <summary>Seeds an OutcomeSpec + WorkPlan (with the given status) + one subtask for a run.</summary>
    private async Task SeedWorkPlanAsync(string coordinatorRunId, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Agentweaver.Api.Memory.MemoryDbContext>();

        var spec = new Agentweaver.Api.Memory.OutcomeSpec
        {
            ProjectId = "proj-x",
            CoordinatorRunId = coordinatorRunId,
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new Agentweaver.Api.Memory.WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-x",
            CoordinatorRunId = coordinatorRunId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        db.Subtasks.Add(new Agentweaver.Api.Memory.Subtask
        {
            WorkPlanId = plan.Id,
            Title = "t",
            Scope = "s",
            AssignedAgent = "morpheus",
            SelectedModelId = "gpt",
            Phase = "execution",
            IsolationStrategy = "worktree",
            Status = Agentweaver.Api.Coordinator.SubtaskStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator P2 {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<string> StartOrchestrationAsync(string projectId, string goal)
    {
        var resp = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/orchestrations", new { goal });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("runId").GetString()!;
    }

    private async Task WaitForGateAsync(string runId, int timeoutSeconds = 20)
    {
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (pendingStore.Get(runId) is not null) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Coordinator run {runId} did not suspend at the confirmation gate in time.");
    }

    private async Task<WorkPlanResponse?> PollWorkPlanAsync(string runId, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _owner.GetAsync($"/api/runs/{runId}/work-plan");
            if (resp.StatusCode == HttpStatusCode.OK)
                return await resp.Content.ReadFromJsonAsync<WorkPlanResponse>();
            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// Inserts a coordinator-style run owned by <paramref name="ownerUser"/> with no live workflow
    /// and no work plan, mirroring the shape produced by StartCoordinatorRunAsync.
    /// </summary>
    private async Task<string> InsertInactiveCoordinatorRunAsync(string ownerUser)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "inactive coordinator run",
            SubmittingUser = ownerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
            ParentRunId = null,
            SubtaskId = null,
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        return runId.ToString();
    }
}
