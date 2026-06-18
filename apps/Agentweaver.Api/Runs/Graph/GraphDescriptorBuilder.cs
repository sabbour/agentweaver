using Microsoft.Agents.AI.Workflows;
using Agentweaver.AgentRuntime.Workflow;

namespace Agentweaver.Api.Runs.Graph;

/// <summary>
/// A thin tee over <see cref="WorkflowBuilder"/>: every <c>AddEdge</c> / <c>WithOutputFrom</c> is
/// forwarded to the real builder AND recorded into a raw node/edge accumulator by reading
/// <see cref="IWorkflowNodeMeta"/> off each executor instance. After <see cref="Build"/>, call
/// <see cref="BuildDescriptor"/> to collapse the raw graph into the rendered
/// <see cref="GraphDescriptor"/> (drop hidden plumbing, transitively re-stitch edges, compute
/// cardinality + loopback). No runtime reflection — the descriptor comes from the same code that
/// wires the MAF workflow. Reflection is used only by the build-time drift-guard test.
/// </summary>
public sealed class GraphDescriptorBuilder
{
    /// <summary>
    /// The MAF <see cref="RequestPort"/> review gate is a framework type and cannot implement
    /// <see cref="IWorkflowNodeMeta"/>. This is the ONLY id-to-metadata fallback allowed; it is
    /// keyed on the port's known id and is covered by the drift-guard test.
    /// </summary>
    public const string ReviewGatePortId = "review-gate";

    private readonly WorkflowBuilder _inner;
    private readonly ExecutorBinding _start;
    private readonly Dictionary<string, RawMeta> _rawNodes = new(StringComparer.Ordinal);
    private readonly List<string> _rawOrder = new();
    private readonly List<(string From, string To)> _rawEdges = new();

    public GraphDescriptorBuilder(ExecutorBinding start)
    {
        _start = start;
        _inner = new WorkflowBuilder(start);
        Register(start);
    }

    public GraphDescriptorBuilder AddEdge(ExecutorBinding source, ExecutorBinding target)
    {
        _inner.AddEdge(source, target);
        RecordEdge(source, target);
        return this;
    }

    public GraphDescriptorBuilder AddEdge(ExecutorBinding source, ExecutorBinding target, bool idempotent)
    {
        _inner.AddEdge(source, target, idempotent);
        RecordEdge(source, target);
        return this;
    }

    public GraphDescriptorBuilder AddEdge<T>(ExecutorBinding source, ExecutorBinding target, Func<T?, bool> condition)
    {
        _inner.AddEdge<T>(source, target, condition);
        RecordEdge(source, target);
        return this;
    }

    public GraphDescriptorBuilder AddEdge<T>(ExecutorBinding source, ExecutorBinding target, Func<T?, bool> condition, bool idempotent)
    {
        _inner.AddEdge<T>(source, target, condition, idempotent);
        RecordEdge(source, target);
        return this;
    }

    public GraphDescriptorBuilder WithOutputFrom(params ExecutorBinding[] executors)
    {
        _inner.WithOutputFrom(executors);
        foreach (var e in executors) Register(e);
        return this;
    }

    /// <summary>Builds the underlying MAF workflow.</summary>
    public Workflow Build() => _inner.Build()!;

    // ── Recording ──────────────────────────────────────────────────────────

    private void RecordEdge(ExecutorBinding source, ExecutorBinding target)
    {
        Register(source);
        Register(target);
        _rawEdges.Add((source.Id, target.Id));
    }

    private void Register(ExecutorBinding b)
    {
        if (_rawNodes.ContainsKey(b.Id)) return;
        _rawNodes[b.Id] = Resolve(b);
        _rawOrder.Add(b.Id);
    }

    private static RawMeta Resolve(ExecutorBinding b)
    {
        if (b.RawValue is IWorkflowNodeMeta m)
            return new RawMeta(m.LogicalNodeId, m.DisplayLabel, m.Role, m.NodeType, m.NodeKind, m.Hidden);

        // ONLY allowed fallback: the framework RequestPort review gate.
        if (string.Equals(b.Id, ReviewGatePortId, StringComparison.Ordinal))
            return new RawMeta("review", "Human Review", "review", "gate", "live", Hidden: false);

        throw new InvalidOperationException(
            $"Executor '{b.Id}' does not implement IWorkflowNodeMeta and is not the known " +
            $"'{ReviewGatePortId}' port. Wrap it in VisualFunctionExecutor (or implement the " +
            $"interface) so the graph descriptor can self-describe it.");
    }

    // ── Assembly ───────────────────────────────────────────────────────────

    /// <summary>
    /// Collapses the recorded raw graph into the rendered descriptor: groups raw executors by
    /// LogicalNodeId, drops hidden plumbing while transitively re-stitching edges through it
    /// (cross-producting hidden fan-in/fan-out neighbors), then computes per-edge cardinality and
    /// loopback (back-edge under a DFS rooted at the start node).
    /// </summary>
    public GraphDescriptor BuildDescriptor(string graphId, string variant)
    {
        var rawAdj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in _rawOrder) rawAdj[id] = new List<string>();
        foreach (var (f, t) in _rawEdges)
            if (!rawAdj[f].Contains(t)) rawAdj[f].Add(t);

        bool Visible(string rawId) => !_rawNodes[rawId].Hidden;
        string Logical(string rawId) => _rawNodes[rawId].LogicalNodeId;

        // From a raw node, the visible logical ids reachable by walking ONLY hidden intermediate
        // nodes (stop at the first visible node on each path). Order-preserving (BFS).
        List<string> ReachVisible(string fromRaw)
        {
            var result = new List<string>();
            var seenHidden = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(rawAdj[fromRaw]);
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (Visible(n))
                {
                    var lg = Logical(n);
                    if (!result.Contains(lg)) result.Add(lg);
                }
                else if (seenHidden.Add(n))
                {
                    foreach (var s in rawAdj[n]) queue.Enqueue(s);
                }
            }
            return result;
        }

        // Visible logical nodes, in first-seen raw order.
        var logicalOrder = new List<string>();
        foreach (var rawId in _rawOrder)
        {
            if (!Visible(rawId)) continue;
            var lg = Logical(rawId);
            if (!logicalOrder.Contains(lg)) logicalOrder.Add(lg);
        }

        // Collapsed visible edges (deduped).
        var edges = new List<(string From, string To)>();
        foreach (var rawId in _rawOrder)
        {
            if (!Visible(rawId)) continue;
            var src = Logical(rawId);
            foreach (var tgt in ReachVisible(rawId))
            {
                if (string.Equals(tgt, src, StringComparison.Ordinal)) continue;
                if (!edges.Any(e => e.From == src && e.To == tgt)) edges.Add((src, tgt));
            }
        }

        // Start node: the first visible logical node entered from the raw start.
        string startNodeId = Visible(_start.Id)
            ? Logical(_start.Id)
            : ReachVisible(_start.Id).FirstOrDefault() ?? logicalOrder.FirstOrDefault() ?? string.Empty;

        var loopbacks = DetectLoopbacks(startNodeId, logicalOrder, edges);

        // Forward (non-loopback) degrees drive fan-out / fan-in detection.
        var fwdOut = logicalOrder.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        var fwdIn = logicalOrder.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
        foreach (var e in edges)
        {
            if (loopbacks.Contains(e)) continue;
            fwdOut[e.From]++;
            fwdIn[e.To]++;
        }

        string Cardinality((string From, string To) e)
        {
            if (loopbacks.Contains(e)) return "direct";
            if (fwdOut[e.From] > 1) return "fanout";
            if (fwdIn[e.To] > 1) return "fanin";
            return "direct";
        }

        var nodes = logicalOrder
            .Select(id =>
            {
                var meta = _rawNodes.Values.First(m => m.LogicalNodeId == id && !m.Hidden);
                return new GraphNode(id, meta.Label, meta.Role, meta.Kind, meta.NodeType, ChildGraphRef: null);
            })
            .ToArray();

        var edgeArr = edges
            .Select(e => new GraphEdge(e.From, e.To, Cardinality(e), loopbacks.Contains(e)))
            .ToArray();

        return new GraphDescriptor(graphId, variant, startNodeId, nodes, edgeArr);
    }

    // Back-edge detection via colored DFS: an edge whose target is currently on the recursion
    // stack (gray) is a back-edge (target is an ancestor of the source).
    private static HashSet<(string From, string To)> DetectLoopbacks(
        string start, List<string> nodes, List<(string From, string To)> edges)
    {
        var loop = new HashSet<(string, string)>();
        if (string.IsNullOrEmpty(start)) return loop;

        var adj = nodes.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges)
            if (adj.ContainsKey(e.From)) adj[e.From].Add(e.To);

        // 0 = white, 1 = gray (on stack), 2 = black.
        var color = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);

        void Dfs(string u)
        {
            color[u] = 1;
            foreach (var v in adj[u])
            {
                if (!color.ContainsKey(v)) continue;
                if (color[v] == 1) loop.Add((u, v));
                else if (color[v] == 0) Dfs(v);
            }
            color[u] = 2;
        }

        if (color.ContainsKey(start)) Dfs(start);
        // Cover any nodes not reachable from start so their edges still get classified.
        foreach (var n in nodes)
            if (color[n] == 0) Dfs(n);

        return loop;
    }

    private readonly record struct RawMeta(
        string LogicalNodeId, string Label, string Role, string NodeType, string Kind, bool Hidden);
}
