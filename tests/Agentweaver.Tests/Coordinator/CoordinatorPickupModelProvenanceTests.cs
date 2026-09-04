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
/// The reserved-pickup path must persist the resolver's ACTUAL source, and the run's event stream
/// must carry the <c>run.model_provider_resolved</c> provenance a successful run previously left
/// nowhere at all.
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
    public async Task Pickup_run_persists_the_resolved_byok_source_and_emits_provenance()
    {
        var projectId = await CreateProjectAsync();
        var pid = ProjectId.Parse(projectId);

        // Activate a deployment-wide BYOK provider — the resolver's platform-scope winner when a
        // project has no Copilot binding of its own.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var byok = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            var provider = await byok.AddAsync(
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

        var backlogStore = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = pid,
            Title = "Pickup must record the provider that actually ran",
            Description = "deterministic pickup",
            State = BacklogTaskState.Ready,
            OrderKey = "n",
            CapturedBy = "owner-github-login",
            CapturedByUserId = CoordinatorWebApplicationFactory.OwnerUser,
            CreatedAt = DateTimeOffset.UtcNow,
            CommittedAt = DateTimeOffset.UtcNow,
        };
        await backlogStore.InsertAsync(task);

        var projectStore = _factory.Services.GetRequiredService<IProjectStore>();
        var project = await projectStore.GetAsync(pid);
        project.Should().NotBeNull();

        await _factory.Services.GetRequiredService<CoordinatorPickupService>()
            .TryPickupAsync(project!, task, CancellationToken.None);

        var claimed = await backlogStore.GetAsync(pid, task.Id);
        claimed!.RunId.Should().NotBeNull();

        var run = await _factory.Services.GetRequiredService<IRunStore>().GetAsync(claimed.RunId!.Value);
        run.Should().NotBeNull();
        run!.ModelSource.Should().Be(ModelSource.Byok,
            "the persisted source must be the resolver's actual result, never a hardcoded Copilot literal");

        var provenance = await PollForProvenanceAsync(claimed.RunId!.Value.ToString());
        provenance.Should().NotBeNull(
            "a successful run must leave durable provenance for the provider that served it");
        provenance!.Value.GetProperty("providerKind").GetString()
            .Should().Be(EffectiveModelProviderProvenance.KindByok);
        provenance.Value.GetProperty("modelSource").GetString().Should().Be("byok");
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

    /// <summary>
    /// Reads the run's event stream (the durable provenance channel) until the
    /// <c>run.model_provider_resolved</c> event appears, or the poll deadline expires.
    /// </summary>
    private async Task<JsonElement?> PollForProvenanceAsync(string runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var response = await _owner.GetAsync($"/api/runs/{runId}/events");
            if (response.IsSuccessStatusCode)
            {
                var events = await response.Content.ReadFromJsonAsync<JsonElement>();
                var match = EnumerateEvents(events).FirstOrDefault(e =>
                    e.TryGetProperty("type", out var type)
                    && type.GetString() == EventTypes.RunModelProviderResolved);
                if (match.ValueKind != JsonValueKind.Undefined)
                    return match.GetProperty("payload");
            }

            await Task.Delay(50);
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateEvents(JsonElement body) => body.ValueKind switch
    {
        JsonValueKind.Array => body.EnumerateArray(),
        JsonValueKind.Object when body.TryGetProperty("events", out var events)
            && events.ValueKind == JsonValueKind.Array => events.EnumerateArray(),
        _ => [],
    };

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
