using System.Text.Json.Serialization;

namespace Scaffolder.Api.Runs.Graph;

/// <summary>
/// FROZEN CONTRACT — the dynamic per-run workflow visualization descriptor. Built from the same
/// code that wires the MAF workflow (see <see cref="GraphDescriptorBuilder"/>); plumbing
/// adapters/storers/terminals are collapsed into their logical node and dropped, with edges
/// transitively re-stitched. snake_case JSON mirrors the existing DTO convention.
///
/// The same record also carries the unified <c>coordinator</c> variant (graph_id
/// <c>coordinator:{coordinatorRunId}</c>) built from the work plan — see
/// <c>CoordinatorGraphDescriptor</c> — so the frontend renders the coordinator, its fan-out
/// subtask children, and the PLANNED collective-assembly stage with the same generic renderer.
/// </summary>
public sealed record GraphDescriptor(
    [property: JsonPropertyName("graph_id")] string GraphId,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("start_node_id")] string StartNodeId,
    [property: JsonPropertyName("nodes")] GraphNode[] Nodes,
    [property: JsonPropertyName("edges")] GraphEdge[] Edges);

/// <summary>
/// A single rendered node. <see cref="Id"/> equals the executor's LogicalNodeId for per-run
/// graphs. <see cref="NodeType"/> is the self-declared category (<c>agent</c> | <c>action</c> |
/// <c>gate</c> | <c>terminal</c> | <c>subtask</c>) that drives the frontend's rendered shape.
/// The trailing optional fields are populated ONLY by the <c>coordinator</c> variant's subtask
/// nodes (they are omitted from JSON when null); per-run nodes never carry them.
/// </summary>
public sealed record GraphNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("node_type")] string NodeType,
    [property: JsonPropertyName("child_graph_ref")] string? ChildGraphRef,
    [property: JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonPropertyName("phase"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phase = null,
    [property: JsonPropertyName("isolation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Isolation = null,
    [property: JsonPropertyName("child_run_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChildRunId = null);

/// <summary>
/// A directed edge between two logical nodes after collapse + re-stitch.
/// <see cref="Loopback"/> is true when the edge target is an ancestor of its source
/// (a back-edge under a DFS rooted at <see cref="GraphDescriptor.StartNodeId"/>).
/// </summary>
public sealed record GraphEdge(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("cardinality")] string Cardinality,
    [property: JsonPropertyName("loopback")] bool Loopback);
