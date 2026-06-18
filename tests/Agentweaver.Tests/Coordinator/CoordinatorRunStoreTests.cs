using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for the Feature 008 Phase 1 persistence additions on <see cref="SqliteRunStore"/>:
/// the <c>ParentRunId</c> and <c>SubtaskId</c> columns that link a child run back to the
/// coordinator run and the subtask it executes.
///
/// Each test uses an isolated real SQLite database (via <see cref="TestSqliteDb"/>) and exercises
/// the real store with no mocks (Principle VII).
/// </summary>
public sealed class CoordinatorRunStoreTests
{
    // =========================================================================
    // A coordinator (parent) run round-trips with null ParentRunId/SubtaskId and
    // AgentName "Coordinator" — exactly the shape StartCoordinatorRunAsync persists.
    // =========================================================================
    [Fact]
    public async Task CoordinatorRun_RoundTrips_WithNullParentAndSubtask()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);

        var runId = RunId.New();
        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = "coordinator-repo-path",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "coordinator goal",
            SubmittingUser    = "coordinator-user",
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            AgentName         = "Coordinator",
            ParentRunId       = null,
            SubtaskId         = null,
        };

        await store.InsertAsync(run);

        var fetched = await store.GetAsync(runId);
        fetched.Should().NotBeNull();
        fetched!.AgentName.Should().Be("Coordinator");
        fetched.ParentRunId.Should().BeNull("the coordinator run has no parent");
        fetched.SubtaskId.Should().BeNull("the coordinator run is not a subtask execution");
    }

    // =========================================================================
    // A child run round-trips ParentRunId and SubtaskId exactly as written, so a
    // dispatched run can always be linked back to its coordinator and subtask.
    // =========================================================================
    [Fact]
    public async Task ChildRun_RoundTrips_ParentRunId_And_SubtaskId()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);

        var parentRunId = RunId.New().ToString();
        const string subtaskId = "subtask-42";

        var childRunId = RunId.New();
        var child = new Run
        {
            Id                = childRunId,
            RepositoryPath    = "child-repo-path",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "child subtask work",
            SubmittingUser    = "child-user",
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            AgentName         = "morpheus",
            ParentRunId       = parentRunId,
            SubtaskId         = subtaskId,
        };

        await store.InsertAsync(child);

        var fetched = await store.GetAsync(childRunId);
        fetched.Should().NotBeNull();
        fetched!.ParentRunId.Should().Be(parentRunId,
            "ParentRunId must round-trip so the child run links back to its coordinator");
        fetched.SubtaskId.Should().Be(subtaskId,
            "SubtaskId must round-trip so the child run links back to the subtask it executes");
        fetched.AgentName.Should().Be("morpheus");
    }
}
