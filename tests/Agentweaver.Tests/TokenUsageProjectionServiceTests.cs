using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Tests for <see cref="TokenUsageProjectionService"/> event-processing logic.
/// Uses the internal <c>ProcessUsageEventAsync</c> method directly (no mocks —
/// Constitution Principle VII) against real in-memory SQLite stores.
/// </summary>
public sealed class TokenUsageProjectionServiceTests
{
    // =========================================================================
    // TPS-01: A well-formed agent.turn.usage event is parsed and written to the store.
    // =========================================================================
    [Fact]
    public async Task ProcessesAgentTurnUsageEvent_WritesRecord()
    {
        // Arrange: real SQLite stores backed by an isolated temp file.
        await using var testDb = await TestSqliteDb.CreateAsync();

        var runStore = new SqliteRunStore(testDb.Db);
        var usageStore = new SqliteTokenUsageStore(testDb.Db);

        // Insert an active run so the service can resolve WorkflowRunId / ProjectId.
        var projectId = ProjectId.New();
        var runId = RunId.New();
        var workflowRunId = Guid.NewGuid().ToString();

        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = "dummy-repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test task",
            SubmittingUser = "test-user",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            WorkflowRunId = workflowRunId,
        });

        // Build a SqliteRunEventStream pointing at the same directory as the test DB.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = testDb.FilePath,
            })
            .Build();

        var eventStream = new SqliteRunEventStream(config);

        var service = new TokenUsageProjectionService(
            runStore,
            eventStream,
            usageStore,
            NullLogger<TokenUsageProjectionService>.Instance);

        // Build a JsonElement payload that matches the agent.turn.usage schema.
        var payloadJson = JsonSerializer.SerializeToElement(new
        {
            modelId = "gpt-4o",
            inputTokens = 250,
            outputTokens = 90,
            totalNanoAiu = 3_000_000_000L,
        });
        var evt = new RunEvent(1, EventTypes.AgentTurnUsage, payloadJson);

        // Act: invoke the internal processing method directly.
        await service.ProcessUsageEventAsync(runId.ToString(), evt, CancellationToken.None);

        // Assert: one record was persisted with the expected field values.
        var summary = await usageStore.GetRunUsageAsync(runId.ToString());

        summary.InputTokens.Should().Be(250);
        summary.OutputTokens.Should().Be(90);
        summary.TotalTokens.Should().Be(340);
        summary.TotalNanoAiu.Should().Be(3_000_000_000L);
        summary.ByModel.Should().HaveCount(1);
        summary.ByModel[0].ModelId.Should().Be("gpt-4o");
    }

    // =========================================================================
    // TPS-02: An event with a missing modelId field is skipped gracefully.
    // =========================================================================
    [Fact]
    public async Task ProcessesAgentTurnUsageEvent_SkipsEventWithMissingModelId()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();

        var runStore = new SqliteRunStore(testDb.Db);
        var usageStore = new SqliteTokenUsageStore(testDb.Db);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = testDb.FilePath,
            })
            .Build();

        var eventStream = new SqliteRunEventStream(config);
        var runId = Guid.NewGuid().ToString();

        var service = new TokenUsageProjectionService(
            runStore,
            eventStream,
            usageStore,
            NullLogger<TokenUsageProjectionService>.Instance);

        // Payload has no modelId — TryExtractUsage should return false.
        var payloadJson = JsonSerializer.SerializeToElement(new
        {
            inputTokens = 100,
            outputTokens = 50,
        });
        var evt = new RunEvent(1, EventTypes.AgentTurnUsage, payloadJson);

        // Must not throw; no record should be written.
        await service.ProcessUsageEventAsync(runId, evt, CancellationToken.None);

        var summary = await usageStore.GetRunUsageAsync(runId);
        summary.TotalTokens.Should().Be(0, "an event with no modelId must be ignored");
    }
}
