using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Recurrence guard for #196: a tool approval raised by a CHILD subtask of a Coordinator run must
/// resolve to the child run id when the operator approves from the coordinator view (posting to the
/// PARENT coordinator run id). Before the fix, the approval gate only looked at the posted run id and
/// returned 404 ("No approval request found for this request_id on this run") because the pending
/// request is owned by the child, not the parent.
/// </summary>
public sealed class ToolApprovalOwningRunResolutionTests
{
    private static async Task<Run> InsertRunAsync(
        SqliteRunStore store, RunId id, string? parentRunId, string? agentName, string user = "op-user")
    {
        var run = new Run
        {
            Id                = id,
            RepositoryPath    = "dummy-repo-path",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "approval routing test",
            SubmittingUser    = user,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            ParentRunId       = parentRunId,
            AgentName         = agentName,
            SubtaskId         = parentRunId is null ? null : "1",
        };
        await store.InsertAsync(run);
        return run;
    }

    [Fact]
    public async Task Approve_FromCoordinator_ResolvesToChildSubtaskRun_AndRecordsApprovalOnChild()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var gate = new InMemoryToolApprovalGate();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        var coordinatorRun = await InsertRunAsync(store, coordinatorId, parentRunId: null, agentName: "Coordinator");
        await InsertRunAsync(store, childId, parentRunId: coordinatorId.ToString(), agentName: "Researcher");

        const string requestId = "toolu_01childweb_fetch";

        // The child subtask raises a web_fetch approval — the context is owned by the CHILD run.
        var waitTask = gate.WaitForApprovalAsync(
            childId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Posting to the coordinator run id must NOT be known there (this is exactly the 404 case).
        gate.GetRequestState(coordinatorId.ToString(), requestId).Should().Be(ToolApprovalRequestState.Unknown);

        // Server-side resolution walks the coordinator's children and finds the owning child run.
        var owningRunId = await EndpointHelpers.ResolveApprovalOwningRunIdAsync(
            coordinatorId.ToString(), coordinatorRun, requestId, gate, store, CancellationToken.None);

        owningRunId.Should().Be(childId.ToString(),
            "the approval request is owned by the child subtask run, not the coordinator");

        // Granting on the resolved run id records the approval on the child and unblocks the agent.
        (await gate.GrantAsync(owningRunId!, requestId, ApprovalScope.Once)).Should().BeTrue();
        (await waitTask).Should().BeTrue("the child's web_fetch call must be approved");
        gate.GetRequestState(childId.ToString(), requestId).Should().Be(ToolApprovalRequestState.Approved);
    }

    [Fact]
    public async Task Approve_PostedDirectlyToChild_ResolvesToSameChild()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var gate = new InMemoryToolApprovalGate();

        var coordinatorId = RunId.New();
        var childId = RunId.New();
        await InsertRunAsync(store, coordinatorId, parentRunId: null, agentName: "Coordinator");
        var childRun = await InsertRunAsync(store, childId, parentRunId: coordinatorId.ToString(), agentName: "Researcher");

        const string requestId = "toolu_01directpost";
        _ = gate.WaitForApprovalAsync(
            childId.ToString(), requestId, "web_fetch", "https://example.com",
            TimeSpan.FromSeconds(5), CancellationToken.None);

        var owningRunId = await EndpointHelpers.ResolveApprovalOwningRunIdAsync(
            childId.ToString(), childRun, requestId, gate, store, CancellationToken.None);

        owningRunId.Should().Be(childId.ToString(),
            "posting directly to the owning child run must resolve to itself without a child search");
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenNoParentOrChildOwnsTheRequest()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteRunStore(testDb.Db);
        var gate = new InMemoryToolApprovalGate();

        var coordinatorId = RunId.New();
        var coordinatorRun = await InsertRunAsync(store, coordinatorId, parentRunId: null, agentName: "Coordinator");

        var owningRunId = await EndpointHelpers.ResolveApprovalOwningRunIdAsync(
            coordinatorId.ToString(), coordinatorRun, "toolu_01missing", gate, store, CancellationToken.None);

        owningRunId.Should().BeNull("no run knows this request_id, so the endpoint should return 404");
    }
}
