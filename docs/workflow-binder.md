# Workflow Binder — Open Executor Factory

How an authored [`WorkflowDefinition`](../apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs) is bound
onto the live Microsoft Agent Framework (MAF) run graph (Feature 015, US1).

Before US1 the binder switched on five hardcoded node ids (`agent`, `rai`, `review`, `merge`, `scribe`)
and literal edge keys (`"agent->rai:"`). Any other node id hit a `default → throw`, and the loader
rejected `fan_out` / `fan_in` / `serial` / `peer_review` outright. The generalized binder instead resolves
each node's executor from its **type** and wires edges from `(from, to, when)` triples — so an authored
workflow whose node ids differ runs unchanged, while the existing default workflow still produces a
**byte-for-byte identical** graph.

## Pieces

| File | Responsibility |
| --- | --- |
| [`NodeClassifier`](../apps/Agentweaver.Api/Workflows/NodeClassifier.cs) | Maps a `WorkflowNode` to a `NodeKind` from its `type` (+ gate kind), **never** its id. |
| [`INodeExecutorFactory` / `NodeExecutorRegistry`](../apps/Agentweaver.Api/Workflows/NodeExecutorRegistry.cs) | Resolves a node's *primary* executor (its entry point) from its kind. |
| [`RunWorkflowGraphBinder`](../apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs) | Iterates nodes/edges, expands each transition into raw executor wiring, declares terminal outputs. |
| [`WorkflowBindException`](../apps/Agentweaver.Api/Workflows/WorkflowBindException.cs) | Node-scoped, fail-closed error when a node/edge cannot be bound. |

## 1. How the executor factory resolves node types

`NodeClassifier.Classify(node)` derives a `NodeKind` from `WorkflowNode.Type` (and, for `check` nodes, the
canonical `gate_kind`):

| Node `type` | Gate kind | `NodeKind` | Primary executor (`RunWorkflowBindings`) |
| --- | --- | --- | --- |
| `prompt` | — | `Agent` | `AgentBinding` |
| `check` | `rai` | `Rai` | per-node policy gate, else `RaiBinding` |
| `check` | `human-review` | `HumanReview` | per-node policy gate, else `ReviewBinding` |
| `check` | `rubberduck` | `Rubberduck` | per-node policy gate |
| `merge` | — | `Merge` | `MergeBinding` |
| `scribe` | — | `Scribe` | `ScribeBindingMerge` |
| `terminal` | — | `Terminal` | resolved from incoming edges (see §3) |
| `fan_out` / `fan_in` / `serial` / `peer_review` / `coordinator_composed` | — | the matching kind | **load-accepted, runtime pending** (see §5) |

`NodeExecutorRegistry.ResolveExecutor(node, bindings)` returns the executor a node is *entered* at. It draws
from the real, pre-built executors in `RunWorkflowBindings` (Principle VII: bind to real executors, never
mocks). The multi-executor *plumbing* a logical edge expands into is owned by the binder, not the factory.

The start node is resolved this way too — the entry edge is
`AgentInputStorer → factory.ResolveExecutor(startNode)`, so the start is the declared `start` id, **not** a
hardcoded `"agent"`.

## 2. How edge wiring works: `(from, to, when)` → subgraph expansion

For each `WorkflowEdge`, the binder classifies both endpoints and dispatches on the tuple
`(fromKind, toKind, when)`. Each logical edge expands into a **subgraph** of raw executor-to-executor edges
plus hidden plumbing (adapters, storers, terminals) that the
[`GraphDescriptorBuilder`](../apps/Agentweaver.Api/Runs/Graph/GraphDescriptorBuilder.cs) later collapses.

The default workflow's transitions and their expansions:

| `(fromKind, toKind, when)` | Raw expansion |
| --- | --- |
| `(Agent, Rai, ∅)` | `agent → rai` |
| `(Rai, Agent, revise)` | `rai →[predicate] raiRevisionAdapter → agent` (idempotent loop) |
| `(Rai, Terminal, safety-failed)` | `rai →[predicate] terminalSafetyFailed` |
| `(Rai, Scribe, no-changes)` | `rai →[predicate] terminalNoOp → scribeInputNoChanges → scribeNoChanges → scribeOutputNoChanges` |
| `(Rai, HumanReview, review)` | `rai →[predicate] reviewAdapter → review` |
| `(HumanReview, Merge, approved)` | `review →[predicate] mergeAdapter → merge` |
| `(HumanReview, Agent, request-changes)` | `review →[predicate] reviewChangesAdapter → agent` (idempotent loop) |
| `(HumanReview, Terminal, declined)` | `review →[predicate] terminalDeclined` |
| `(Merge, Scribe, merged)` | `merge →[predicate] terminalMerge → scribeInputMerge → scribeMerge → scribeOutputMerge` |
| `(Merge, HumanReview, blocked)` | `merge →[predicate] blockedAdapter → review` (idempotent loop) |
| `(Scribe, Terminal, ∅)` | no raw edge — the scribe output executors *are* the graph outputs |

The edge predicates (e.g. *RAI revise iff `RaiRevisionRequired && Iteration < MaxIterations`*) are the exact
lambdas the previous hand-coded pipeline used; they are not altered (parity).

**Terminal outputs** (`WithOutputFrom`) are resolved from a terminal's **incoming** edges, not its id: a
`safety-failed` verdict → the safety terminal, a `declined` verdict → the declined terminal, a
scribe-sourced edge → both scribe-output executors (`done` sink). So a renamed terminal still binds.

**Review-policy multi-gate workflows** (Feature 010): extra or renamed gate nodes that received a dedicated
per-node policy binding keep their existing policy plumbing (`PolicyAgentTurnStorer`,
`PolicyAgentOutputAdapter`, …). The canonical `rai`/`review` gates never get a policy binding, so the default
workflow always takes the canonical path above.

## 3. How to author a new node type (extension point)

1. Add the member to [`WorkflowNodeType`](../apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs) and its
   `TryParseNodeType` mapping in
   [`WorkflowDefinitionLoader`](../apps/Agentweaver.Api/Workflows/WorkflowDefinitionLoader.cs).
2. Map the type to a `NodeKind` in `NodeClassifier.Classify`.
3. Construct the real executor(s) in `RunWorkflowFactory.BuildWorkflow`, add them to `RunWorkflowBindings`,
   and resolve them in `NodeExecutorRegistry.ResolveExecutor`.
4. Add the `(fromKind, toKind, when)` expansion(s) to `RunWorkflowGraphBinder.TryWireCanonicalEdge` and, if
   the node is a graph output, to `WireOutputs`.

A node that classifies to a kind with no executor mapping (or an edge with no matching expansion) **fails
closed** with a `WorkflowBindException` naming the offending node — the binder never silently skips,
mis-wires, or partially executes a graph. This is also the governance guard: an authored node's fields can
never weaken the sandbox boundary, the human-approval gate, or RAI content-safety, because those guarantees
live in the executors the binder wires, not in the definition.

## 4. Parity guarantee — what it means and how it's verified

**Parity** means the default workflow, built through the generalized binder, emits the **identical** raw
`GraphDescriptorBuilder` edges, predicates, idempotent flags, and outputs as the pre-change hand-wired
pipeline — so the collapsed `GraphDescriptor`, the `workflow.step` stage stream, and the terminal states are
unchanged. This is mandatory because the binder is on the **live run pipeline** (the highest-risk change).

Verified by [`RunWorkflowGraphBinderTests`](../tests/Agentweaver.Tests/Workflows/RunWorkflowGraphBinderTests.cs):

- **`DefaultWorkflow_RealPath_ProducesCanonicalFiveStageGraph`** — builds the descriptor through the **real**
  `RunWorkflowFactory` (real executors) and asserts the canonical five-stage graph (nodes
  `agent, rai, review, merge, scribe`; start `agent`; the eight edges with the three expected loopbacks).
- **`DefaultDefinition_Binder_ProducesCanonicalFiveStageGraph`** — pins the same graph at the binder/unit
  level over the built-in default definition.
- **`RenamedNodeIds_ResolveByType_ProduceIdenticalGraph`** — a definition whose node ids are all renamed
  (types unchanged) collapses to the **same** graph, proving resolution is by type, not id.
- **`UnwiredNodeType_FailsClosed_WithNodeScopedError`** — an unbindable node throws a node-scoped
  `WorkflowBindException`.
- **`Loader_Accepts_PreviouslyRejectedNodeTypes`** — `fan_out` / `fan_in` / `serial` / `peer_review` load.

The reflection-based **drift guard**
([`RunWorkflowDefinitionBindingTests`](../tests/Agentweaver.Tests/Graph/RunWorkflowDefinitionBindingTests.cs),
`CoordinatorWorkflowGraphDriftGuardTests`) continues to assert the built MAF graph matches the descriptor.

## 5. Status of `fan_out` / `fan_in` / `serial` / `peer_review`

These node types are now **accepted by the loader** (the bindable-type gate was removed) and modeled by the
schema, but are **not yet wired to a runtime executor**. They map onto existing seams —
`fan_out` → the coordinator's [`SubtaskFrontier`](../apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs),
`fan_in` → [`AssemblyPlanning`](../apps/Agentweaver.Api/Coordinator/AssemblyPlanning.cs), `serial` → a
sequential chain, `peer_review` → the peer-review seam — which require dispatch infrastructure beyond the
per-run graph. Until that lands, a workflow that actually *wires* one of these nodes fails closed at **build
time** with a clear `WorkflowBindException`, rather than being rejected at load time. This is the deliberate
"load-accepted, runtime-pending" boundary for US1.
