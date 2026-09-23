using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Casting;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression for the hardcoded <c>Run.ModelSource = ModelSource.GitHubCopilot</c> at every
/// coordinator run insert site: the row (and therefore the UI) always claimed "GitHub Copilot" even
/// when <see cref="EffectiveModelProviderResolver"/> had resolved a deployment-wide BYOK provider.
/// The reserved-pickup path must persist the resolver's actual source before outcome drafting begins.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorPickupModelProvenanceTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;

    public CoordinatorPickupModelProvenanceTests()
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
    public async Task Define_outcome_with_azure_byok_without_copilot_binding_passes_durable_byok_boundary_to_background_drafting()
    {
        var projectId = await CreateProjectAsync();
        ByokProviderConfiguration provider;

        // Activate a deployment-wide BYOK provider — the resolver's platform-scope winner when a
        // project has no Copilot binding of its own.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var byok = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            provider = await byok.AddAsync(
                new ByokProviderConfiguration(
                    Id: "unused",
                    Name: "Test Azure provider",
                    Type: "azure",
                    BaseUrl: "https://byok-resource.openai.azure.com",
                    Model: "gpt-4.1",
                    ApiKey: "test-byok-key"),
                CancellationToken.None);
            await byok.SetActiveAsync(provider.Id, CancellationToken.None);
        }

        await _factory.PrepareAiExecutionAsync(
            _owner, "orchestration", projectId, ensureProvider: false);
        var response = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/orchestrations",
            new
            {
                goal = "Define Outcome must keep its accepted Azure BYOK provider after the request ends.",
                modelId = "claude-sonnet-5",
            });
        response.EnsureSuccessStatusCode();
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("runId").GetString()!;

        var run = await _factory.Services.GetRequiredService<IRunStore>().GetAsync(RunId.Parse(runId));
        run.Should().NotBeNull();
        run!.ModelSource.Should().Be(ModelSource.Byok,
            "the persisted source must be the resolver's actual result, never a hardcoded Copilot literal");
        run.ModelId.Should().Be(provider.Model,
            "the durable run must report the frozen BYOK execution model instead of a requested role model");

        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (drafter.LastInput is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        drafter.LastInput.Should().NotBeNull(
            "the background Define Outcome workflow must invoke its drafter");
        drafter.LastInput!.ModelSource.Should().Be(ModelSource.Byok.ToApiString(),
            "background drafting must select the persisted accepted BYOK source, not a disposed request-local plan");
        drafter.LastInput.ModelId.Should().Be(provider.Model,
            "work-plan model selection must inherit the effective BYOK execution model");
        drafter.LastInput.ByokProviderFingerprint.Should().Be(provider.ExecutionFingerprint(),
            "the drafting client must validate the durable accepted Azure provider identity before invocation");

        var entry = _factory.Services.GetRequiredService<RunStreamStore>().Get(runId);
        entry.Should().NotBeNull();
        var provenance = entry!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.RunModelProviderResolved);
        var provenancePayload = JsonSerializer.SerializeToElement(provenance.Payload);
        provenancePayload.GetProperty("modelSource").GetString()
            .Should().Be(ModelSource.Byok.ToApiString());
        provenancePayload.GetProperty("modelId").GetString().Should().Be(provider.Model);

        (await _owner.PostAsync($"/api/runs/{runId}/outcome-spec/confirm", content: null))
            .EnsureSuccessStatusCode();
        WorkPlan? plan = null;
        var planDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (plan is null && DateTime.UtcNow < planDeadline)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            plan = db.WorkPlans.SingleOrDefault(candidate => candidate.CoordinatorRunId == runId);
            if (plan is null)
                await Task.Delay(50);
        }
        plan.Should().NotBeNull("confirming the deterministic draft must persist a work plan");

        List<string> selectedModels = [];
        var subtaskDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (selectedModels.Count == 0 && DateTime.UtcNow < subtaskDeadline)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            selectedModels = db.Subtasks
                .Where(subtask => subtask.WorkPlanId == plan!.Id)
                .Select(subtask => subtask.SelectedModelId)
                .ToList();
            if (selectedModels.Count == 0)
                await Task.Delay(50);
        }
        selectedModels.Should().NotBeEmpty();
        selectedModels.Should().OnlyContain(model => model == provider.Model,
            "BYOK work-plan and topology metadata must report the frozen execution model");

        await using var graphScope = _factory.Services.CreateAsyncScope();
        var graphDb = graphScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var persistedSubtasks = graphDb.Subtasks
            .Where(subtask => subtask.WorkPlanId == plan.Id)
            .ToList();
        var subtaskIds = persistedSubtasks.Select(subtask => subtask.Id).ToHashSet();
        var dependencies = graphDb.SubtaskDependencies
            .Where(edge => subtaskIds.Contains(edge.SubtaskId))
            .Select(edge => new ValueTuple<int, int>(edge.SubtaskId, edge.DependsOnSubtaskId))
            .ToList();
        var graph = CoordinatorGraphDescriptor.Build(
            runId,
            persistedSubtasks,
            dependencies,
            coordinatorModel: run.ModelId);
        var attributedNodes = graph.Nodes
            .Where(node => node.Role is "coordinator" or "subtask")
            .ToList();
        attributedNodes.Should().NotBeEmpty();
        attributedNodes.Should().OnlyContain(
            node => node.Model == provider.Model,
            "the topology must display the same effective model as the run and work plan");
    }

    [Fact]
    public async Task Pickup_run_without_byok_stays_github_copilot_sourced()
    {
        var projectId = await CreateProjectAsync();
        var pid = ProjectId.Parse(projectId);

        var backlogStore = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = pid,
            Title = "Pickup without BYOK",
            Description = "deterministic pickup",
            State = BacklogTaskState.Ready,
            OrderKey = "n",
            CapturedBy = "owner-github-login",
            CapturedByUserId = CoordinatorWebApplicationFactory.OwnerUser,
            CreatedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
        };
        await backlogStore.InsertAsync(task);

        var project = await _factory.Services.GetRequiredService<IProjectStore>().GetAsync(pid);
        await _factory.Services.GetRequiredService<CoordinatorPickupService>()
            .TryPickupAsync(project!, task, CancellationToken.None);

        var claimed = await backlogStore.GetAsync(pid, task.Id);
        var run = await _factory.Services.GetRequiredService<IRunStore>().GetAsync(claimed!.RunId!.Value);

        run!.ModelSource.Should().Be(ModelSource.GitHubCopilot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservedByokPickup_CapturesAcceptedProviderBeforeConfigurationChangesOrRemoval(
        bool removeProvider)
    {
        var projectId = await CreateProjectAsync();
        var pid = ProjectId.Parse(projectId);
        ByokProviderConfiguration accepted;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var byok = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            accepted = await byok.AddAsync(
                new ByokProviderConfiguration(
                    "unused", "Accepted Azure", "azure", "https://accepted.example.test",
                    "gpt-4.1", "accepted-key"),
                CancellationToken.None);
            await byok.SetActiveAsync(accepted.Id, CancellationToken.None);
        }

        AiOperationCatalog.TryGet("orchestration", out var operation).Should().BeTrue();
        string providerKey;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var executionPlans = scope.ServiceProvider.GetRequiredService<AiExecutionPlanService>();
            var plan = await executionPlans.PrepareAsync(
                operation,
                pid,
                new CallerContext { User = CoordinatorWebApplicationFactory.OwnerUser },
                CancellationToken.None);
            providerKey = executionPlans.CreateQueuedProviderKey(plan);
        }
        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        drafter.BeforeDraftAsync = async _ =>
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var byok = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            if (removeProvider)
            {
                await byok.RemoveAsync(accepted.Id, CancellationToken.None);
                return;
            }

            await byok.UpdateAsync(
                accepted.Id,
                accepted with { Model = "gpt-4.2", ApiKey = "replacement-key" },
                CancellationToken.None);
        };

        var backlogStore = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = pid,
            Title = "Freeze reserved BYOK pickup",
            Description = "The accepted configuration must survive until the first model turn.",
            State = BacklogTaskState.Ready,
            OrderKey = "n",
            CapturedBy = "owner-github-login",
            CapturedByUserId = CoordinatorWebApplicationFactory.OwnerUser,
            AiExecutionProviderKey = providerKey,
            CreatedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
        };
        await backlogStore.InsertAsync(task);

        var project = await _factory.Services.GetRequiredService<IProjectStore>().GetAsync(pid);
        await _factory.Services.GetRequiredService<CoordinatorPickupService>()
            .TryPickupAsync(project!, task, CancellationToken.None);

        var drafted = await WaitForDraftAsync(drafter);
        drafted.Should().BeTrue("the test changes or removes the configuration at the first draft boundary");
        var claimed = await backlogStore.GetAsync(pid, task.Id);
        var run = await _factory.Services.GetRequiredService<IRunStore>().GetAsync(claimed!.RunId!.Value);
        var boundary = await _factory.Services.GetRequiredService<IRunModelProviderBoundaryResolver>()
            .ResolveDurableProviderBoundaryAsync(run!, CancellationToken.None);

        boundary.Provider.Should().BeOfType<EffectiveModelProviderResult.Byok>();
        boundary.ByokProviderConfiguration.Should().Be(accepted);
        boundary.ByokProviderFingerprint.Should().Be(accepted.ExecutionFingerprint());
    }

    [Fact]
    public async Task TrustedAutomationPickup_UsesActivatedByokWithoutBrowserExecutionKey()
    {
        var projectId = await CreateProjectAsync();
        var pid = ProjectId.Parse(projectId);
        ByokProviderConfiguration provider;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var byok = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            provider = await byok.AddAsync(
                new ByokProviderConfiguration(
                    "unused", "Automation BYOK", "azure",
                    "https://automation.example.test", "gpt-4.1", "key"),
                CancellationToken.None);
            await byok.SetActiveAsync(provider.Id, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            if (!db.Projects.Any(x => x.ProjectId == projectId))
                db.Projects.Add(new ProjectRecord { ProjectId = projectId, OriginKind = "blank" });
            db.AutomationActivations.Add(new AutomationActivationRecord
            {
                Id = SnapshotRef.Create().Value,
                ProjectId = projectId,
                ModelProviderSource = AutomationModelProviderSource.Byok,
                ByokProviderId = provider.Id,
                AutomationKey = "trusted-byok-activation",
                Status = AutomationActivationStatus.Active,
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using var invocationScope = _factory.Services.CreateAsyncScope();
        var invocations = invocationScope.ServiceProvider.GetRequiredService<IAutomationInvocationService>();
        var claim = await invocations.TryClaimForProjectAsync(
            pid, "workflow-schedule-trigger:test:2026-09-10", null, "schedule");
        claim.Should().NotBeNull();
        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = pid,
            Title = "Activated BYOK automation",
            Description = "Draft this outcome without borrowing an interactive bearer.",
            State = BacklogTaskState.Ready,
            OrderKey = "n",
            CapturedBy = "automation-owner",
            CreatedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
            WorkflowOverrideId = "automation-workflow",
            SourceFilePath = "workflow-schedule-trigger:test:2026-09-10",
        };
        var backlogStore = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        await backlogStore.InsertAsync(task);
        (await invocations.TryBindBacklogTaskAsync(claim!.InvocationId, pid, task.Id)).Should().BeTrue();

        var project = await _factory.Services.GetRequiredService<IProjectStore>().GetAsync(pid);
        await _factory.Services.GetRequiredService<CoordinatorPickupService>()
            .TryPickupAsync(project!, task, CancellationToken.None);

        var drafted = await WaitForDraftAsync(
            _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
                .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject);
        drafted.Should().BeTrue();
        var claimed = await backlogStore.GetAsync(pid, task.Id);
        var run = await _factory.Services.GetRequiredService<IRunStore>().GetAsync(claimed!.RunId!.Value);
        run!.ModelSource.Should().Be(ModelSource.Byok);
        run.Result.Should().NotBe("operation_requires_github_copilot");
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Provenance Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        SquadTestFixtureHelper.CreateMinimalSquad(dir, "Provenance Test");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private static async Task<bool> WaitForDraftAsync(FakeCoordinatorSpecDrafter drafter)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (drafter.LastInput is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        return drafter.LastInput is not null;
    }
}
