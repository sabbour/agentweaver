using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Workflows;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.Tests.Casting;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Integration test for the Feature 008 Phase 2 coordinator ORCHESTRATOR (decompose + persist).
///
/// Runs against a real in-process API host, a real SQLite + EF <see cref="MemoryDbContext"/>, and
/// the real coordinator MAF workflow with its request-port suspend/resume. The only seam is the
/// signed-out <see cref="SignedOutGitHubTokenStore"/> baked into
/// <see cref="CoordinatorWebApplicationFactory"/>: the decomposition agent turn fails closed and
/// the orchestrator uses its built-in DETERMINISTIC fallback (a real component, exercised exactly
/// as in production when Copilot is unavailable) — no mocks (Principle VII).
///
/// Asserts the wave's contract: confirming a spec routes the run to orchestration, which persists
/// one WorkPlan (planned), the Subtask rows (pending, with a real assigned agent + selected model),
/// any SubtaskDependency edges, and emits a single coordinator.work_plan snapshot event.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorOrchestratorTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;

    public CoordinatorOrchestratorTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Confirm_DecomposesAndPersistsWorkPlanSubtasksAndDependencies()
    {
        var projectId = await CreateProjectAsync();
        var runId = await StartOrchestrationAsync(projectId, "Build a deterministic work plan for testing");
        await WaitForGateAsync(runId);

        // Confirm -> the run finalizes the spec then runs orchestration (decompose + persist).
        var confirm = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // Orchestration runs asynchronously after confirm; poll EF until the work plan is persisted.
        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));
        workPlan.Should().NotBeNull("confirm must route to orchestration and persist a work plan");
        workPlan!.Status.Should().Be("planned");
        workPlan.ProjectId.Should().Be(projectId);
        workPlan.OutcomeSpecId.Should().BeGreaterThan(0, "the work plan must link to the confirmed outcome spec");

        // Subtasks are persisted pending, with a real assigned agent and a selected Copilot model.
        var subtasks = await PollAsync(async db =>
        {
            var rows = await db.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == workPlan.Id).ToListAsync();
            return rows.Count > 0 ? rows : null;
        });
        subtasks.Should().NotBeNull("the work plan must decompose into at least one subtask");
        subtasks!.Should().OnlyContain(s => s.Status == "pending",
            "this wave persists subtasks pending and does not dispatch any child run");
        subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.AssignedAgent),
            "each subtask must be assigned a roster agent (FR-011)");
        subtasks.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.SelectedModelId),
            "each subtask must have a selected Copilot model (FR-012)");
        subtasks.Should().OnlyContain(s => s.ChildRunId == null,
            "no child run is dispatched in the decompose + persist wave");

        // Dependency edges, when present, must reference subtasks of THIS plan and be acyclic.
        var subtaskIds = subtasks.Select(s => s.Id).ToHashSet();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var edges = await db.SubtaskDependencies.AsNoTracking()
                .Where(d => subtaskIds.Contains(d.SubtaskId))
                .ToListAsync();
            edges.Should().OnlyContain(e => subtaskIds.Contains(e.DependsOnSubtaskId),
                "every dependency edge must reference a subtask in the same work plan");
            edges.Should().OnlyContain(e => e.SubtaskId != e.DependsOnSubtaskId, "no self-dependencies");
        }

        // A single coordinator.work_plan snapshot event is emitted on the coordinator run stream.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Get(runId);
        entry.Should().NotBeNull();
        var planEvents = await PollForWorkPlanEventsAsync(entry!);
        planEvents.Should().HaveCount(1, "exactly one plan-time snapshot event is emitted");
    }

    [Fact]
    public async Task ReservedComposedPlan_IsPopulatedInPlace_WithoutSelectingNestedWorkflow()
    {
        var projectId = await CreateProjectAsync();
        var project = await _factory.Services.GetRequiredService<IProjectStore>()
            .GetAsync(ProjectId.Parse(projectId));
        var runId = RunId.New().ToString();
        var parentRunId = RunId.New().ToString();
        int planId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var spec = new OutcomeSpec
            {
                ProjectId = projectId,
                CoordinatorRunId = runId,
                Goal = "Derive a dependent work plan",
                DesiredOutcome = "Derive a dependent work plan",
                Scope = "Embedded workflow stage",
                Assumptions = string.Empty,
                Status = "confirmed",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OutcomeSpecs.Add(spec);
            await db.SaveChangesAsync();
            var plan = new WorkPlan
            {
                OutcomeSpecId = spec.Id,
                ProjectId = projectId,
                CoordinatorRunId = runId,
                ParentRunId = parentRunId,
                ParentWorkflowId = "workflow-v1",
                ParentWorkflowNodeId = "composed",
                ParentResumeState = "committed",
                Status = WorkPlanStatus.Planned,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WorkPlans.Add(plan);
            await db.SaveChangesAsync();
            planId = plan.Id;
        }

        var input = new CoordinatorDraftInput(
            runId, projectId, "Derive a dependent work plan", "octocat",
            project!.WorkingDirectory, "test-model");
        var executor = new CoordinatorOrchestratorExecutor(
            new DependentDagWorkflowAgentFactory(),
            _factory.Services.GetRequiredService<RunStreamStore>(),
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILoggerFactory>(),
            _factory.Services.GetRequiredService<IStoryIndependenceClassifier>(),
            _factory.Services.GetRequiredService<IAssemblyGateCodeClassifier>(),
            "gpt-5-mini",
            null,
            null);
        var first = await executor.OrchestrateAsync(input, CancellationToken.None);
        var second = await _factory.Services.GetRequiredService<CoordinatorWorkflowFactory>()
            .OrchestrateComposedAsync(input, CancellationToken.None);

        first.WorkPlanId.Should().Be(planId);
        second.WorkPlanId.Should().Be(planId);
        second.InlineSubtaskCount.Should().Be(first.InlineSubtaskCount);
        first.InlineSubtaskCount.Should().Be(2);
        using var verifyScope = _factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verify.WorkPlans.CountAsync(plan => plan.CoordinatorRunId == runId)).Should().Be(1);
        (await verify.Subtasks.CountAsync(subtask => subtask.WorkPlanId == planId))
            .Should().Be(first.InlineSubtaskCount);
        var subtasks = await verify.Subtasks.AsNoTracking()
            .Where(subtask => subtask.WorkPlanId == planId)
            .OrderBy(subtask => subtask.Id)
            .ToListAsync();
        var dependency = await verify.SubtaskDependencies.AsNoTracking().SingleAsync();
        dependency.SubtaskId.Should().Be(subtasks[1].Id);
        dependency.DependsOnSubtaskId.Should().Be(subtasks[0].Id);
        (await verify.WorkPlans.SingleAsync(plan => plan.Id == planId))
            .WorkflowId.Should().BeNull("the child must not select a recursive authored workflow");
    }

    // #238 — a non-empty run model pin (explicit request `modelId` OR the project's GitHub Copilot
    // default) must pin EVERY subtask, regardless of complexity. The deterministic decomposition
    // fallback yields a single MEDIUM-complexity subtask assigned to the roster's sole member, whose
    // role carries a NON-EMPTY default model ("claude-opus-4.8") that differs from the pin — exactly
    // the #238 scenario where planning subtasks used to fall back to the role's claude-* default.
    // BEFORE the fix, a non-high subtask ignored the run pin and adopted that role default, so this
    // test would observe "claude-opus-4.8". AFTER the fix, the pin wins for the medium subtask, so
    // every subtask carries exactly the pinned model.
    [Fact]
    public async Task Confirm_WithExplicitRunModel_PinsEverySubtask_IncludingNonHighComplexity()
    {
        const string pinnedModel = "gpt-5.6-sol";
        const string roleDefaultModel = "claude-opus-4.8";

        var projectId = await CreateProjectWithRoleDefaultModelAsync(roleDefaultModel);
        var runId = await StartOrchestrationAsync(
            projectId, "Build a deterministic work plan for testing", modelId: pinnedModel);
        await WaitForGateAsync(runId);

        var confirm = await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null);
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));
        workPlan.Should().NotBeNull("confirm must route to orchestration and persist a work plan");

        var subtasks = await PollAsync(async db =>
        {
            var rows = await db.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == workPlan!.Id).ToListAsync();
            return rows.Count > 0 ? rows : null;
        });
        subtasks.Should().NotBeNull("the work plan must decompose into at least one subtask");
        subtasks!.Should().OnlyContain(s => s.SelectedModelId == pinnedModel,
            "a non-empty run model pin wins for EVERY subtask regardless of complexity (#238), " +
            "including the deterministic fallback's MEDIUM-complexity subtask — it must NOT fall back " +
            "to the assigned role's non-empty default model (claude-opus-4.8)");
    }

    [Fact]
    public async Task Confirm_AutoSelectedPmDiscovery_CodeDecomposition_ReSelectsWorkflowWithBuildTest()
    {
        var projectId = await CreateProjectAsync();
        _factory.WorkflowSelectionModel.Override = context =>
            context.AvailableWorkflows.Any(w => w.Id == "pm-discovery")
                ? """{"selected":"pm-discovery","rationale":"Discovery appears suitable."}"""
                : """{"selected":"software-delivery","rationale":"Code requires delivery gates."}""";
        _factory.AssemblyGateCodeClassifier.Override = _ => true;

        var runId = await StartOrchestrationAsync(projectId, "Implement a new API endpoint");
        await WaitForGateAsync(runId);
        (await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));

        workPlan.Should().NotBeNull();
        workPlan!.WorkflowId.Should().BeOneOf("bug-fix", "software-delivery",
            "code-producing work must be re-selected onto an available topology with Build & Test");
        _factory.AssemblyGateCodeClassifier.CallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Direct_StaticSiteServerCurlPreview_WithRestrictedWorkflows_UsesPlatformBuildTestFallback()
    {
        var projectId = await CreateProjectAsync();
        var projectStore = _factory.Services.GetRequiredService<IProjectStore>();
        await projectStore.UpdateAllowedWorkflowIdsAsync(
            ProjectId.Parse(projectId),
            ["issue-insights-to-prds"],
            DateTimeOffset.UtcNow);
        _factory.AssemblyGateCodeClassifier.Override = _ => true;

        const string goal =
            "Create site/index.html, serve it on port 3000, curl it, and register browser preview.";
        var runId = await StartOrchestrationAsync(projectId, goal, startMode: "direct");

        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));

        workPlan.Should().NotBeNull(
            "a direct code-producing run must not fail merely because its blueprint allowed-set lacks Build & Test");
        workPlan!.WorkflowId.Should().Be("software-delivery",
            "the platform-owned fallback preserves build, server health, curl, and preview validation");
    }

    [Fact]
    public async Task Direct_ExplicitNonFanOverride_RetainsCoordinatorPlanning()
    {
        var projectId = await CreateProjectAsync();

        var runId = await StartOrchestrationAsync(
            projectId,
            "Draft a concise launch announcement.",
            workflowOverrideId: "content-authoring",
            startMode: "direct");

        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));
        workPlan.Should().NotBeNull();
        workPlan!.WorkflowId.Should().Be("content-authoring");

        var store = _factory.Services.GetRequiredService<IRunStore>();
        var pin = await PollAsync(async _ => (await store.GetAsync(RunId.Parse(runId)))?
            .GetExecutableWorkflowPin());
        var run = await store.GetAsync(RunId.Parse(runId));
        run!.AgentName.Should().Be("Coordinator");
        pin.Should().NotBeNull("the coordinator saves its selected workflow when the plan commits");
        pin!.DefinitionId.Should().Be(workPlan.WorkflowId);
        pin.ContentDigest.Should().Be(ExecutableWorkflowSnapshots.Digest(pin.DefinitionYaml));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlannedCoordinator_UsesSavedReviewGates_AfterWorkflowEditOrDelete(bool delete)
    {
        var projectId = await CreateProjectAsync();
        var project = (await _factory.Services.GetRequiredService<IProjectStore>()
            .GetAsync(ProjectId.Parse(projectId)))!;
        var definition = BuiltInWorkflows.Default.Definition! with
        {
            Id = "frozen-review",
            Name = "Frozen Review",
            Nodes = BuiltInWorkflows.Default.Definition!.Nodes
                .Select(node => node.Id == "review" ? node with { Label = "Pinned Review" } : node)
                .ToList(),
        };
        var path = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows", "frozen-review.yaml");
        await File.WriteAllTextAsync(path, WorkflowDefinitionYamlSerializer.Serialize(definition));
        var runId = await StartOrchestrationAsync(
            projectId, "Draft release notes", workflowOverrideId: "frozen-review");
        await WaitForGateAsync(runId);
        (await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = await PollAsync(async db => await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));
        plan.Should().NotBeNull();
        plan!.WorkflowId.Should().Be("frozen-review");
        var store = _factory.Services.GetRequiredService<IRunStore>();
        var saved = await PollAsync(async _ => (await store.GetAsync(RunId.Parse(runId)))?
            .GetExecutableWorkflowPin());
        saved.Should().NotBeNull();
        saved!.DefinitionId.Should().Be(plan.WorkflowId);
        saved.ContentDigest.Should().Be(ExecutableWorkflowSnapshots.Digest(saved.DefinitionYaml));

        if (delete)
            File.Delete(path);
        else
            await File.WriteAllTextAsync(path, WorkflowDefinitionYamlSerializer.Serialize(
                definition with
                {
                    Nodes = definition.Nodes.Select(node => node.Id == "review"
                        ? node with { Label = "Edited Review" } : node).ToList(),
                }));

        using var scope = _factory.Services.CreateScope();
        var gates = await CoordinatorAssemblyGateResolver.ResolveAsync(
            scope.ServiceProvider, plan.Id, CancellationToken.None);
        gates.Single(g => g.GateKind == "human-review").Label.Should().Be("Pinned Review");

        await store.UpdateExecutableWorkflowPinAsync(RunId.Parse(runId),
            saved with { ContentDigest = "sha256:" + new string('0', 64) });
        var resolveCorrupt = () => CoordinatorAssemblyGateResolver.ResolveAsync(
            scope.ServiceProvider, plan.Id, CancellationToken.None);
        await resolveCorrupt.Should().ThrowAsync<WorkflowBindException>();

        await store.UpdateExecutableWorkflowPinAsync(RunId.Parse(runId),
            saved with { DefinitionYaml = "" });
        var resolveMissing = () => CoordinatorAssemblyGateResolver.ResolveAsync(
            scope.ServiceProvider, plan.Id, CancellationToken.None);
        await resolveMissing.Should().ThrowAsync<WorkflowBindException>();
    }

    [Fact]
    public async Task SQLite_InterruptedPlanCommit_ReusesDurableSelection_AndRejectsStaleOwner()
    {
        var projectId = await CreateProjectAsync();
        var project = (await _factory.Services.GetRequiredService<IProjectStore>()
            .GetAsync(ProjectId.Parse(projectId)))!;
        var definition = BuiltInWorkflows.Default.Definition! with
        {
            Id = "interrupted-plan",
            Name = "Interrupted Plan",
            Nodes = BuiltInWorkflows.Default.Definition!.Nodes
                .Select(node => node.Id == "review" ? node with { Label = "Saved Before Plan" } : node)
                .ToList(),
        };
        var path = Path.Combine(project.WorkingDirectory, ".agentweaver", "workflows", "interrupted-plan.yaml");
        await File.WriteAllTextAsync(path, WorkflowDefinitionYamlSerializer.Serialize(definition));

        var runId = RunId.New();
        var now = DateTimeOffset.UtcNow;
        await _factory.Services.GetRequiredService<IRunStore>().InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = project.WorkingDirectory,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Draft release notes",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = now,
            ProjectId = ProjectId.Parse(projectId),
            AgentName = "Coordinator",
        });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var spec = new OutcomeSpec
            {
                CoordinatorRunId = runId.ToString(),
                ProjectId = projectId,
                Goal = "Draft release notes",
                DesiredOutcome = "Draft release notes",
                Scope = "Documentation",
                Assumptions = "",
                Status = "confirmed",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.OutcomeSpecs.Add(spec);
            await db.SaveChangesAsync();
            var runTx = await CoordinatorWorkflowFactory.BeginFencedWriteAsync(
                db, runId.ToString(), null, CancellationToken.None,
                scope.ServiceProvider.GetRequiredService<SqliteDb>(), lockUnfenced: true);
            await using (runTx)
            {
                await using (var interruptedPlanTx = await db.Database.BeginTransactionAsync())
                {
                    db.WorkPlans.Add(new WorkPlan
                    {
                        CoordinatorRunId = runId.ToString(),
                        OutcomeSpecId = spec.Id,
                        ProjectId = projectId,
                        WorkflowId = definition.Id,
                        Status = WorkPlanStatus.Planned,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    await db.SaveChangesAsync();
                    await CoordinatorOrchestratorExecutor.PinSelectedWorkflowAsync(
                        db, runTx!, runId.ToString(),
                        ExecutableWorkflowSnapshots.Create(definition, "coordinator-selection"),
                        CancellationToken.None);
                    await runTx!.CommitAsync(CancellationToken.None);
                }
            }
        }

        File.Delete(path);
        var store = _factory.Services.GetRequiredService<IRunStore>();
        var saved = (await store.GetAsync(runId))!.GetExecutableWorkflowPin();
        saved.Should().NotBeNull("the run pin committed even though the separate plan transaction rolled back");

        var input = new CoordinatorDraftInput(
            runId.ToString(), projectId, "Draft release notes",
            CoordinatorWebApplicationFactory.OwnerUser, project.WorkingDirectory, "test-model");
        var executor = new CoordinatorOrchestratorExecutor(
            new DependentDagWorkflowAgentFactory(),
            _factory.Services.GetRequiredService<RunStreamStore>(),
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILoggerFactory>(),
            _factory.Services.GetRequiredService<IStoryIndependenceClassifier>(),
            _factory.Services.GetRequiredService<IAssemblyGateCodeClassifier>(),
            "gpt-5-mini", null, null);
        var recovered = await executor.OrchestrateAsync(input, CancellationToken.None);
        using var verifyScope = _factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await verify.WorkPlans.SingleAsync(w => w.CoordinatorRunId == runId.ToString()))
            .WorkflowId.Should().Be(definition.Id);
        var gates = await CoordinatorAssemblyGateResolver.ResolveAsync(
            verifyScope.ServiceProvider, recovered.WorkPlanId, CancellationToken.None);
        gates.Single(g => g.GateKind == "human-review").Label.Should().Be("Saved Before Plan");
        (await store.GetAsync(runId))!.GetExecutableWorkflowPin()!.DefinitionYaml
            .Should().Be(saved!.DefinitionYaml);

        var staleOwner = () => CoordinatorWorkflowFactory.BeginFencedWriteAsync(
            verify, runId.ToString(), new RunLeaseFence("losing-owner", 0, 1),
            CancellationToken.None, verifyScope.ServiceProvider.GetRequiredService<SqliteDb>());
        await staleOwner.Should().ThrowAsync<CoordinatorExecutionFenceLostException>();
        (await store.GetAsync(runId))!.GetExecutableWorkflowPin()!.DefinitionYaml
            .Should().Be(saved.DefinitionYaml);
    }

    [Fact]
    public void ProviderConnectionFailure_CannotFallBackToDeterministicDecomposition()
    {
        var exception = new ModelProviderConnectionRequiredException(ProjectId.New());

        CoordinatorOrchestratorExecutor.CanUseModelFallback(exception).Should().BeFalse(
            "an AgentHost pre-launch provider failure must remain the terminal actionable cause");
        CoordinatorOrchestratorExecutor.CanUseModelFallback(new HttpRequestException()).Should().BeTrue(
            "ordinary model availability failures may still use deterministic decomposition");
    }

    [Fact]
    public async Task Confirm_AutoSelectedPmDiscovery_NonCodeDecomposition_KeepsPmDiscovery()
    {
        var projectId = await CreateProjectAsync();
        _factory.WorkflowSelectionModel.Override = _ =>
            """{"selected":"pm-discovery","rationale":"This is document-only discovery."}""";

        var runId = await StartOrchestrationAsync(projectId, "Research customer needs and draft a PRD");
        await WaitForGateAsync(runId);
        (await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));

        workPlan.Should().NotBeNull();
        workPlan!.WorkflowId.Should().Be("pm-discovery");
        _factory.AssemblyGateCodeClassifier.CallCount.Should().Be(0,
            "planning-phase decomposition is a confident non-code fast path");
    }

    [Fact]
    public async Task Confirm_ExplicitPmDiscovery_CodeDecomposition_HonorsOverrideAndSurfacesWarning()
    {
        var projectId = await CreateProjectAsync();
        _factory.AssemblyGateCodeClassifier.Override = _ => true;

        var runId = await StartOrchestrationAsync(
            projectId, "Implement a new API endpoint", workflowOverrideId: "pm-discovery");
        await WaitForGateAsync(runId);
        (await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var workPlan = await PollAsync(async db =>
            await db.WorkPlans.AsNoTracking().FirstOrDefaultAsync(w => w.CoordinatorRunId == runId));
        workPlan.Should().NotBeNull();
        workPlan!.WorkflowId.Should().Be("pm-discovery", "explicit workflow overrides remain honored");

        var entry = _factory.Services.GetRequiredService<RunStreamStore>().Get(runId);
        var events = await PollForWorkPlanEventsAsync(entry!);
        var payload = JsonSerializer.SerializeToElement(events.Single().Payload);
        payload.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Any(w => w is not null
                && w.Contains("no Build & Test stage", StringComparison.Ordinal)
                && w.Contains("override was honored", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator Orchestrate {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        SquadTestFixtureHelper.CreateMinimalSquad(dir, "Coordinator Orchestrate");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    // Creates a project whose sole roster member ("Alpha") resolves a NON-EMPTY role default model.
    // The minimal fixture keys registry.json by role id ("lead-architect"), but SquadReader resolves a
    // member's DefaultModel by the team.md member NAME ("Alpha") — so the default silently reads empty.
    // We overwrite registry.json keyed by the member name so the assigned member carries a real role
    // default (mirroring a production blueprint), making the pin-vs-role-default distinction observable.
    private async Task<string> CreateProjectWithRoleDefaultModelAsync(string roleDefaultModel)
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Coordinator Orchestrate {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        SquadTestFixtureHelper.CreateMinimalSquad(dir, "Coordinator Orchestrate");

        var registryPath = Path.Combine(dir, ".squad", "casting", "registry.json");
        File.WriteAllText(registryPath,
            $$"""
            {
              "agents": {
                "Alpha": {
                  "name": "Alpha",
                  "persistent_name": "Alpha",
                  "universe": "Inception",
                  "default_model": "{{roleDefaultModel}}",
                  "status": "Active",
                  "created_at": "2026-01-01T00:00:00Z",
                  "previous_name": null,
                  "succeeded_by": null,
                  "retired_at": null,
                  "charter_path": ".squad/agents/alpha/charter.md"
                }
              }
            }
            """);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<string> StartOrchestrationAsync(
        string projectId,
        string goal,
        string? modelId = null,
        string? workflowOverrideId = null,
        string? startMode = null)
    {
        await _factory.PrepareAiExecutionAsync(
            _owner, "orchestration", projectId);
        object request = startMode is null
            ? new { goal, modelId, workflow_override_id = workflowOverrideId }
            : new { goal, modelId, workflow_override_id = workflowOverrideId, start_mode = startMode };
        var resp = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/orchestrations",
            request);
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
            if (await pendingStore.GetAsync(runId) is not null) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Coordinator run {runId} did not suspend at the confirmation gate in time.");
    }

    private async Task<T?> PollAsync<T>(Func<MemoryDbContext, Task<T?>> query, int timeoutSeconds = 20) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var result = await query(db);
                if (result is not null) return result;
            }
            await Task.Delay(50);
        }

        return null;
    }

    private static async Task<List<RunEvent>> PollForWorkPlanEventsAsync(
        RunStreamEntry entry,
        int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var events = entry.GetSnapshotSince(0).Events
                .Where(e => e.Type == EventTypes.CoordinatorWorkPlan).ToList();
            if (events.Count > 0)
                return events;

            await Task.Delay(50);
        }

        return [];
    }

    private sealed class DependentDagWorkflowAgentFactory : IWorkflowAgentFactory
    {
        private readonly DependentDagWorkflowTurnAgent _agent = new();

        public IWorkflowTurnAgent CreateWorkerAgent() => _agent;
        public IWorkflowTurnAgent CreateRaiAgent() => _agent;
        public IWorkflowTurnAgent CreateRubberduckAgent() => _agent;
        public IWorkflowTurnAgent CreateBuildTestAgent() => _agent;
        public IWorkflowTurnAgent CreateScribeAgent() => _agent;
    }

    private sealed class DependentDagWorkflowTurnAgent : IWorkflowTurnAgent
    {
        public Task SetupAsync(
            string workingDirectory,
            string repositoryPath,
            string runId,
            string? modelId,
            string? systemPromptContext,
            ChannelWriter<RunEvent>? streamWriter,
            string? projectId,
            string? agentName,
            string? apiBaseUrl,
            string? apiKey,
            CancellationToken ct,
            string? userId = null) => Task.CompletedTask;

        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct) =>
            Task.FromResult(
                """
                [
                  {
                    "story_key": "schema",
                    "title": "Create schema",
                    "scope": "Create generated/schema.txt.",
                    "role": "lead-architect",
                    "complexity": "low",
                    "phase": "implementation",
                    "isolation": "worktree",
                    "declared_output_paths": ["generated/schema.txt"],
                    "depends_on": []
                  },
                  {
                    "story_key": "consumer",
                    "title": "Consume schema",
                    "scope": "Read generated/schema.txt and create generated/consumer.txt.",
                    "role": "lead-architect",
                    "complexity": "low",
                    "phase": "implementation",
                    "isolation": "worktree",
                    "declared_output_paths": ["generated/consumer.txt"],
                    "depends_on": [1]
                  }
                ]
                """);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
