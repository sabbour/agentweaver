using FluentAssertions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Focused unit tests for the Feature 008 Phase 2 dispatch-frontier / dependency-unblocking logic
/// (<see cref="SubtaskFrontier"/>). This is the pure DAG core the dispatch engine relies on:
/// independent subtasks dispatch in parallel; a dependent subtask only becomes ready once every
/// predecessor reaches assemble_ready/completed; failed/rai_flagged predecessors keep their
/// dependents blocked. No database, no live agent — the heavier end-to-end child-run coverage is
/// owned by the QA wave.
/// </summary>
public sealed class SubtaskFrontierTests
{
    private static (int, int)[] Edges(params (int SubtaskId, int DependsOn)[] edges) => edges;

    [Fact]
    public void IndependentSubtasks_AllReadyTogether()
    {
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Pending,
            [2] = SubtaskStatus.Pending,
            [3] = SubtaskStatus.Pending,
        };

        var ready = SubtaskFrontier.ReadyPending(status, Edges());

        ready.Should().Equal(new[] { 1, 2, 3 }, "with no dependencies every pending subtask is dispatchable in parallel");
    }

    [Fact]
    public void DependentSubtask_BlockedUntilPredecessorSatisfied()
    {
        // 2 depends on 1.
        var edges = Edges((2, 1));
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Pending,
            [2] = SubtaskStatus.Pending,
        };

        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(new[] { 1 },
            "only the independent predecessor is ready while it is still pending");

        status[1] = SubtaskStatus.Running;
        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty(
            "a running predecessor does not yet satisfy the dependency");

        status[1] = SubtaskStatus.AssembleReady;
        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(new[] { 2 },
            "the dependent unblocks once its predecessor reaches assemble_ready");
    }

    [Fact]
    public void CompletedPredecessor_AlsoUnblocksDependent()
    {
        var edges = Edges((2, 1));
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Completed,
            [2] = SubtaskStatus.Pending,
        };

        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(2);
    }

    [Fact]
    public void FailedPredecessor_KeepsDependentBlocked()
    {
        var edges = Edges((2, 1));
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Failed,
            [2] = SubtaskStatus.Pending,
        };

        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty(
            "a failed dependency must never unblock its dependent");
        SubtaskFrontier.IsQuiescent(status, edges, inFlightCount: 0).Should().BeTrue(
            "no in-flight children and no ready frontier means the loop has reached a fixpoint");
    }

    [Fact]
    public void RaiFlaggedPredecessor_KeepsDependentBlocked()
    {
        var edges = Edges((2, 1));
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.RaiFlagged,
            [2] = SubtaskStatus.Pending,
        };

        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty(
            "an rai_flagged dependency does not satisfy a dependency edge");
    }

    [Fact]
    public void MultiplePredecessors_RequireAllSatisfied()
    {
        // 3 depends on both 1 and 2.
        var edges = Edges((3, 1), (3, 2));
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.AssembleReady,
            [2] = SubtaskStatus.Running,
            [3] = SubtaskStatus.Pending,
        };

        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty(
            "a fan-in subtask waits for ALL predecessors");

        status[2] = SubtaskStatus.AssembleReady;
        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(new[] { 3 },
            "once every predecessor is satisfied the fan-in subtask is ready");
    }

    [Fact]
    public void DiamondGraph_DispatchesInCorrectWaves()
    {
        // 1 -> {2, 3} -> 4 (diamond).
        var edges = Edges((2, 1), (3, 1), (4, 2), (4, 3));
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Pending,
            [2] = SubtaskStatus.Pending,
            [3] = SubtaskStatus.Pending,
            [4] = SubtaskStatus.Pending,
        };

        // Wave 1: only the root.
        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(1);

        // Wave 2: the two middle nodes dispatch in parallel once the root is assemble_ready.
        status[1] = SubtaskStatus.AssembleReady;
        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(2, 3);
        SubtaskFrontier.IsQuiescent(status, edges, inFlightCount: 0).Should().BeFalse(
            "the middle wave is still dispatchable");

        // Simulate dispatching both middle nodes (now running, no longer pending).
        status[2] = SubtaskStatus.Running;
        status[3] = SubtaskStatus.Running;

        // Wave 3: the join waits for BOTH middle nodes to reach assemble_ready.
        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty();
        status[2] = SubtaskStatus.AssembleReady;
        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty();
        status[3] = SubtaskStatus.AssembleReady;
        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(4);

        status[4] = SubtaskStatus.AssembleReady;
        SubtaskFrontier.ReadyPending(status, edges).Should().BeEmpty();
        SubtaskFrontier.IsQuiescent(status, edges, inFlightCount: 0).Should().BeTrue(
            "every subtask is terminal");
    }

    [Fact]
    public void DispatchedOrRunningSubtasks_AreNotRedispatched()
    {
        var status = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Dispatched,
            [2] = SubtaskStatus.Running,
            [3] = SubtaskStatus.Pending,
        };

        SubtaskFrontier.ReadyPending(status, Edges()).Should().Equal(new[] { 3 },
            "only pending subtasks are returned; in-flight ones are never re-dispatched");
    }

    [Fact]
    public void IsQuiescent_FalseWhileChildrenInFlight()
    {
        var status = new Dictionary<int, string> { [1] = SubtaskStatus.Running };
        SubtaskFrontier.IsQuiescent(status, Edges(), inFlightCount: 1).Should().BeFalse();
    }

    [Fact]
    public void DanglingDependency_TreatedAsSatisfied()
    {
        // Edge references a subtask id (99) not in the plan — must not deadlock the frontier.
        var edges = Edges((1, 99));
        var status = new Dictionary<int, string> { [1] = SubtaskStatus.Pending };

        SubtaskFrontier.ReadyPending(status, edges).Should().Equal(1);
    }
}
