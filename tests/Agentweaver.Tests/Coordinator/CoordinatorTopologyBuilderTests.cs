using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for <see cref="CoordinatorTopology"/> — the topology snapshot/delta builder.
/// Pins the <c>executionPodName</c> projection rules:
/// <list type="bullet">
///   <item>Subtask nodes get their pod name from the <see cref="IPodNameRegistry"/> keyed by childRunId.</item>
///   <item>Subtask nodes with no registry entry (or no childRunId) emit <c>executionPodName: null</c>.</item>
///   <item>The coordinator node gets its pod name from the <c>coordinatorPodName</c> parameter.</item>
///   <item>When <c>coordinatorPodName</c> is null (non-Kubernetes), the coordinator node emits <c>null</c>.</item>
/// </list>
/// </summary>
public sealed class CoordinatorTopologyBuilderTests
{
    private static Subtask MakeSubtask(int id, string? childRunId) => new()
    {
        Id = id,
        WorkPlanId = 1,
        Title = $"Subtask {id}",
        Scope = "scope",
        AssignedAgent = "agent",
        SelectedModelId = "gpt-5",
        Phase = "execution",
        IsolationStrategy = "worktree",
        Status = "pending",
        ChildRunId = childRunId,
    };

    private static JsonElement Serialize(object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement NodeById(JsonElement root, string id) =>
        root.GetProperty("nodes").EnumerateArray()
            .First(n => n.GetProperty("id").GetString() == id);

    // ── Subtask pod registry projection ─────────────────────────────────────

    [Fact]
    public void BuildSnapshot_PopulatesExecutionPodName_ForSubtask_WhenRegistryHasEntry()
    {
        var registry = new StubPodRegistry { ["child-run-1"] = "agent-pod-worker-7" };
        var subtasks = new[] { MakeSubtask(1, "child-run-1") };

        var snapshot = CoordinatorTopology.BuildSnapshot(
            "coord-1", 1, "dispatching", subtasks, [], 0, registry);

        var node = NodeById(Serialize(snapshot), "subtask-1");
        node.GetProperty("executionPodName").GetString().Should().Be("agent-pod-worker-7");
    }

    [Fact]
    public void BuildSnapshot_LeavesExecutionPodNameNull_ForSubtask_WhenRegistryHasNoEntry()
    {
        var registry = new StubPodRegistry(); // empty
        var subtasks = new[] { MakeSubtask(1, "child-run-1") };

        var snapshot = CoordinatorTopology.BuildSnapshot(
            "coord-1", 1, "dispatching", subtasks, [], 0, registry);

        var node = NodeById(Serialize(snapshot), "subtask-1");
        node.GetProperty("executionPodName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void BuildSnapshot_LeavesExecutionPodNameNull_ForSubtask_WhenChildRunIdIsNull()
    {
        var registry = new StubPodRegistry { ["child-run-1"] = "agent-pod-worker-7" };
        // subtask has no childRunId yet (not dispatched)
        var subtasks = new[] { MakeSubtask(1, childRunId: null) };

        var snapshot = CoordinatorTopology.BuildSnapshot(
            "coord-1", 1, "dispatching", subtasks, [], 0, registry);

        var node = NodeById(Serialize(snapshot), "subtask-1");
        node.GetProperty("executionPodName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void BuildSnapshot_LeavesExecutionPodNameNull_ForSubtask_WhenNoPodRegistryProvided()
    {
        var subtasks = new[] { MakeSubtask(1, "child-run-1") };

        var snapshot = CoordinatorTopology.BuildSnapshot(
            "coord-1", 1, "dispatching", subtasks, [], 0, podRegistry: null);

        var node = NodeById(Serialize(snapshot), "subtask-1");
        node.GetProperty("executionPodName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Coordinator node pod name ────────────────────────────────────────────

    [Fact]
    public void BuildSnapshot_PopulatesExecutionPodName_ForCoordinatorNode_WhenCoordinatorPodNameProvided()
    {
        var snapshot = CoordinatorTopology.BuildSnapshot(
            "coord-1", 1, "dispatching", [], [], 0,
            podRegistry: null, coordinatorPodName: "agentweaver-api-pod-abc");

        var node = NodeById(Serialize(snapshot), "coordinator");
        node.GetProperty("executionPodName").GetString().Should().Be("agentweaver-api-pod-abc");
    }

    [Fact]
    public void BuildSnapshot_LeavesExecutionPodNameNull_ForCoordinatorNode_WhenCoordinatorPodNameIsNull()
    {
        var snapshot = CoordinatorTopology.BuildSnapshot(
            "coord-1", 1, "dispatching", [], [], 0,
            podRegistry: null, coordinatorPodName: null);

        var node = NodeById(Serialize(snapshot), "coordinator");
        node.GetProperty("executionPodName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── SubtaskNode delta helper ─────────────────────────────────────────────

    [Fact]
    public void SubtaskNode_PopulatesExecutionPodName_WhenRegistryHasEntry()
    {
        var subtask = MakeSubtask(2, "child-run-2");
        var registry = new StubPodRegistry { ["child-run-2"] = "agent-pod-worker-9" };

        var node = Serialize(CoordinatorTopology.SubtaskNode(subtask, registry));
        node.GetProperty("executionPodName").GetString().Should().Be("agent-pod-worker-9");
    }

    [Fact]
    public void SubtaskNode_LeavesExecutionPodNameNull_WhenRegistryHasNoEntry()
    {
        var subtask = MakeSubtask(2, "child-run-2");
        var registry = new StubPodRegistry(); // empty

        var node = Serialize(CoordinatorTopology.SubtaskNode(subtask, registry));
        node.GetProperty("executionPodName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── CoordinatorNode delta helper ─────────────────────────────────────────

    [Fact]
    public void CoordinatorNode_PopulatesExecutionPodName_WhenProvided()
    {
        var node = Serialize(CoordinatorTopology.CoordinatorNode("dispatching", "api-pod-123"));
        node.GetProperty("executionPodName").GetString().Should().Be("api-pod-123");
    }

    [Fact]
    public void CoordinatorNode_LeavesExecutionPodNameNull_WhenNotProvided()
    {
        var node = Serialize(CoordinatorTopology.CoordinatorNode("dispatching"));
        node.GetProperty("executionPodName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class StubPodRegistry : IPodNameRegistry
    {
        private readonly Dictionary<string, string> _map = [];

        public string this[string key]
        {
            set => _map[key] = value;
        }

        public string? TryGet(string runId) => _map.TryGetValue(runId, out var v) ? v : null;

        public void Register(string runId, string podName) => _map[runId] = podName;
        public void Unregister(string runId) => _map.Remove(runId);
        public void RegisterAgentEndpoint(string runId, string endpointUrl) { }
        public string? TryGetAgentEndpoint(string runId) => null;
    }
}
