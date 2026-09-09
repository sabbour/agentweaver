using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
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
            new { goal = "Define Outcome must keep its accepted Azure BYOK provider after the request ends." });
        response.EnsureSuccessStatusCode();
        var runId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("runId").GetString()!;

        var run = await _factory.Services.GetRequiredService<IRunStore>().GetAsync(RunId.Parse(runId));
        run.Should().NotBeNull();
        run!.ModelSource.Should().Be(ModelSource.Byok,
            "the persisted source must be the resolver's actual result, never a hardcoded Copilot literal");

        var drafter = _factory.Services.GetRequiredService<ICoordinatorSpecDrafter>()
            .Should().BeOfType<FakeCoordinatorSpecDrafter>().Subject;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (drafter.LastInput is null && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        drafter.LastInput.Should().NotBeNull(
            "the background Define Outcome workflow must invoke its drafter");
        drafter.LastInput!.ModelSource.Should().Be(ModelSource.Byok.ToApiString(),
            "background drafting must select the persisted accepted BYOK source, not a disposed request-local plan");
        drafter.LastInput.ByokProviderFingerprint.Should().Be(provider.ExecutionFingerprint(),
            "the drafting client must validate the durable accepted Azure provider identity before invocation");

        var entry = _factory.Services.GetRequiredService<RunStreamStore>().Get(runId);
        entry.Should().NotBeNull();
        var provenance = entry!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.RunModelProviderResolved);
        JsonSerializer.SerializeToElement(provenance.Payload)
            .GetProperty("modelSource").GetString()
            .Should().Be(ModelSource.Byok.ToApiString());
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
}
