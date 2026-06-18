using System.Text.Json.Serialization;

namespace Scaffolder.Api.Runs.Graph;

/// <summary>
/// FROZEN CONTRACT — the dynamic per-run workflow visualization descriptor. Built from the same
/// code that wires the MAF workflow (see <see cref="GraphDescriptorBuilder"/>); plumbing
/// adapters/storers/terminals are collapsed into their logical node and dropped, with edges
/// transitively re-stitched. snake_case JSON mirrors the existing DTO convention.
/// </summary>
public sealed record GraphDescriptor(
    [property: JsonPropertyName("graph_id")] string GraphId,
    [property: JsonPropertyName("variant")] string Variant,
    [property: JsonPropertyName("start_node_id")] string StartNodeId,
    [property: JsonPropertyName("nodes")] GraphNode[] Nodes,
    [property: JsonPropertyName("edges")] GraphEdge[] Edges);

/// <summary>A single rendered node. <see cref="Id"/> equals the executor's LogicalNodeId.</summary>
public sealed record GraphNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("child_graph_ref")] string? ChildGraphRef);

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
