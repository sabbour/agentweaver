using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Integration tests verifying the /api/runs/{id}/stream endpoint:
/// - Authorization: non-owner is denied (returns 404 to prevent run-id enumeration)
/// - Both in-progress and completed runs are protected
/// </summary>
public sealed class StreamEndpointTests : IClassFixture<AgentweaverWebApplicationFactory>
{
    private readonly AgentweaverWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;

    public StreamEndpointTests(AgentweaverWebApplicationFactory factory)
    {
        _factory = factory;
        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
    }

    [Fact]
    public async Task NonOwner_IsDenied_InProgressRun()
    {
        // Arrange: create a stream entry owned by a different user
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, "other-user");
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "secret data" }));

        // Act: the test user (who is not "other-user") requests the stream
        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");

        // Assert: 404 (not 403, to prevent enumeration)
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NonOwner_IsDenied_CompletedRun()
    {
        // Arrange: create a completed stream entry owned by a different user
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, "other-user");
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "secret" }));
        entry.Record(new RunEvent(2, "run.completed", new { }));
        streamStore.Complete(runId);

        // Act
        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Owner_CanStream_InProgressRun()
    {
        // Arrange: create an entry owned by the test user
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var runId = Guid.NewGuid().ToString();
        var entry = streamStore.Create(runId, AgentweaverWebApplicationFactory.TestUser);
        entry.Record(new RunEvent(1, "agent.message.delta", new { delta = "hello" }));
        entry.Record(new RunEvent(2, "run.completed", new { }));
        entry.MarkCompleted();

        // Act
        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");

        // Assert: 200 with SSE content
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("agent.message.delta");
        body.Should().Contain("event: done");
    }

    [Fact]
    public async Task ReopenedRunWithoutLiveEntry_DoesNotCloseOnHistoricalTerminalEvent()
    {
        var runStore = _factory.Services.GetRequiredService<IRunStore>();
        var eventStream = _factory.Services.GetRequiredService<IRunEventStream>();
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "replay terminal lifecycle",
            SubmittingUser = AgentweaverWebApplicationFactory.TestUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        var oldOutcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old_generation" },
            DateTimeOffset.UtcNow, 1);
        (await runStore.TrySetTerminalOutcomeAsync(runId, oldOutcome, "old_generation")).Should().BeTrue();
        await eventStream.AppendTerminalOutcomeAsync(runId.ToString(), oldOutcome);
        (await runStore.TryReopenTerminalToInProgressAsync(runId)).Should().BeTrue();

        var responseTask = _ownerClient.GetAsync($"/api/runs/{runId}/stream");
        await Task.Delay(TimeSpan.FromSeconds(11));
        responseTask.IsCompleted.Should().BeFalse(
            "the historical terminal event belongs to the earlier lifecycle generation");

        await eventStream.AppendAsync(runId.ToString(),
            new RunEvent(0, "agent.message.delta", new { delta = "current_generation" }));
        var reopened = (await runStore.GetAsync(runId))!;
        var currentOutcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "current_generation" },
            DateTimeOffset.UtcNow, reopened.LifecycleGeneration);
        (await runStore.TrySetTerminalOutcomeAsync(runId, currentOutcome, "current_generation")).Should().BeTrue();
        await eventStream.AppendTerminalOutcomeAsync(runId.ToString(), currentOutcome);

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(EventTypes.RunFailed)
            .And.Contain("current_generation")
            .And.Contain(EventTypes.RunCompleted)
            .And.Contain("event: done");
    }

    [Fact]
    public async Task PersistedSystemPromptReplay_ProjectsMetadataWithoutRawPromptCanary()
    {
        const string canary = "historical-system-prompt-canary";
        var runStore = _factory.Services.GetRequiredService<IRunStore>();
        var eventStream = _factory.Services.GetRequiredService<IRunEventStream>();
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "replay prompt metadata",
            SubmittingUser = AgentweaverWebApplicationFactory.TestUser,
            Status = RunStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
        });
        await eventStream.AppendAsync(runId.ToString(), new RunEvent(0, EventTypes.AgentSystemPrompt, new
        {
            provider = "copilot",
            runId = runId.ToString(),
            baseCharacters = 100,
            runContextCharacters = 0,
            skillCharacters = 0,
            separatorCharacters = 0,
            taskCharacters = 10,
            toolDeclarationCharacters = 20,
            skillDeliveryMode = "none",
            totalCharacters = 130,
            estimatedTokens = 33,
            callableMemoryGuidanceIncluded = false,
            prompt = canary,
            unknown = canary,
        }));
        await eventStream.AppendAsync(runId.ToString(),
            new RunEvent(0, EventTypes.RunCompleted, new { result = "done" }));

        var response = await _ownerClient.GetAsync($"/api/runs/{runId}/stream");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain(EventTypes.AgentSystemPrompt)
            .And.Contain("\"callableMemoryGuidanceIncluded\":false")
            .And.NotContain(canary)
            .And.NotContain("\"prompt\"");
    }

    [Fact]
    public async Task NonexistentRun_Returns404()
    {
        var response = await _ownerClient.GetAsync($"/api/runs/{Guid.NewGuid()}/stream");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
