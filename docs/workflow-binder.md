# Workflow Binder — Open Executor Factory

How an authored [`WorkflowDefinition`](../apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs) is bound
onto the live Microsoft Agent Framework (MAF) run graph (Feature 015, US1).

Before US1 the binder switched on five hardcoded node ids (`agent`, `rai`, `review`, `merge`, `scribe`)
and literal edge keys (`"agent->rai:"`). Any other node id hit a `default → throw`, and the loader
rejected `fan_out` / `fan_in` / `peer_review` outright. The generalized binder instead resolves
each node's executor from its **type** and wires edges from `(from, to, when)` triples — so an authored
workflow whose node ids differ can bind without relying on fixed stage names.
The original five-stage parity guarantee was historical; the current default is six-stage.

## Pieces

**Current scope.** Historical parity examples below describe the original five-stage
binder migration. The current built-in default adds the publish/reuse-PR action between
Merge and Scribe. Catalog graphs are a separate layer: their YAML does not author
Merge, PR or Scribe nodes. Collective assembly owns integration, gates, merge and
recording; the inspected collective path does not establish automatic PR publication.

| File | Responsibility |
| --- | --- |
| [`NodeClassifier`](../apps/Agentweaver.Api/Workflows/NodeClassifier.cs) | Maps a `WorkflowNode` to a `NodeKind` from its `type` (+ gate kind), **never** its id. |
| [`NodeExecutorRegistry`](../apps/Agentweaver.Api/Workflows/NodeExecutorRegistry.cs) | Resolves a node's *primary* executor (its entry point) from its kind. |
| [`RunWorkflowGraphBinder`](../apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs) | Iterates nodes/edges, expands each transition into raw executor wiring, declares terminal outputs. |
| [`WorkflowBindException`](../apps/Agentweaver.Api/Workflows/WorkflowBindException.cs) | Node-scoped, fail-closed error when a node/edge cannot be bound. |

## 1. How the executor factory resolves node types

`NodeClassifier.Classify(node)` derives a `NodeKind` from `WorkflowNode.Type` (and, for `check` nodes, the
canonical `gate_kind`):

| Node `type` | Gate kind | `NodeKind` | Primary executor (`RunWorkflowBindings`) |
| --- | --- | --- | --- |
| `prompt` / `publish` | — | `Agent` | per-node `Wiring.ResolveAgentNode` |
| `check` | `rai` | `Rai` | per-node policy gate, else `RaiBinding` |
| `check` | `human-review` | `HumanReview` | per-node policy gate, else `ReviewBinding` |
| `check` | `rubberduck` | `Rubberduck` | per-node policy gate |
| `merge` | — | `Merge` | `MergeBinding` |
| `scribe` | — | `Scribe` | `ScribeBindingMerge` |
| `terminal` | — | `Terminal` | resolved from incoming edges (see §3) |
| `peer_review` | — | `PeerReview` (verdict-routed) **or** `Agent` (plain turn) | per-node peer-review or producing executor — **wired** (see §2a) |
| `build_test` | — | `PeerReview` | platform-owned build/test/preview instruction through the per-node review executor |
| `open_pull_request` | — | `OpenPullRequest` | `Wiring.ResolveOpenPullRequestNode`; deterministic, not an agent turn |
| `fan_out` / `fan_in` | — | the matching kind | **runtime-bound for one static wait-all region** (see §5) |
| `coordinator_composed` | — | `CoordinatorComposed` | **load-accepted, runtime pending** (see §5) |

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

Historical five-stage transitions and their expansions (not an exhaustive current
default wiring table):

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

## 2a. Peer-review nodes and generic catalog topologies (Feature 015 US3)

The §2 table records the historical default five-stage workflow. The current catalog
contains `software-delivery`, `bug-fix`, `content-authoring`, `pm-discovery`,
`agent-evaluation`, `incident-response` and `infra-ops`, not standalone `code-review`.
These definitions use generic transitions in `RunWorkflowGraphBinder`; evaluation's
setup/run/collect nodes are prompts, not the unsupported fan-out/fan-in forms.
See the [current workflow library](workflow-library.md).

**`peer_review` effective kind.** `EffectiveKind` decides how a `peer_review` node wires:

- It is a real **AI review verdict gate** (`NodeKind.PeerReview`) **only** when it has at least one
  outgoing edge whose `when` is a verdict — `approved`, `request-changes`, `declined`, `pass`, or `fail`.
- A `peer_review` node with only **unconditional** outgoing edge(s) is a plain **producing turn** and
  wires **identically to an `agent` node** (`NodeKind.Agent`). So `review (peer_review) → feedback`
  with no verdict routing is just a turn.

**Generic transitions** (in addition to the default five-stage table):

| `(fromKind, toKind, when)` | Meaning |
| --- | --- |
| `(Agent, Agent, ∅)` | Sequential agent turn (output feeds the next turn) |
| `(Agent, PeerReview, ∅)` | Producer turn → peer-review verdict gate |
| `(Agent, Scribe, ∅)` | Direct completion (record outcome, no merge) |
| `(Agent, HumanReview, ∅)` | Producer turn → human review gate (no RAI in between) |
| `(Rai, Merge, review)` | RAI cleared → merge directly (publish-style, no human gate) |
| `(Rai, Agent, review)` | RAI cleared → next agent turn |
| `(Rai, PeerReview, review)` | RAI cleared → peer-review verdict gate |
| `(PeerReview, Merge, approved\|pass)` | Peer-review passed → merge |
| `(PeerReview, Rai, pass)` | Peer-review passed → RAI safety gate |
| `(PeerReview, Agent, request-changes\|fail)` | Peer-review rejected → loop back to producer turn |
| `(PeerReview, Terminal, declined)` | Peer-review hard-declined → terminal |
| `(HumanReview, Agent, approved)` | Human approved → next agent turn (e.g. postmortem) |
| `(HumanReview, Scribe, approved)` | Human approved → direct completion (no merge) |
| `(Merge, PeerReview, blocked)` | Merge blocked → re-enter peer-review gate |
| `(Merge, Agent, blocked)` | Merge blocked → re-enter producer turn |

The authoritative exhaustive allowlist lives in `WorkflowTransitionContract`. Both
`RunWorkflowGraphBinder` and the generation prompt consume that contract, and the binder
guards its wiring switch with it. In particular, the supported advanced review sequence is
`Rai --pass/approved/review--> PeerReview(BuildTest)
--pass/approved--> PeerReview --pass/approved--> HumanReview`. A condition such as
`Rai --no-changes--> PeerReview` is not runtime wiring and is rejected with supported
outgoing alternatives rather than being discovered during execution. `build_test` is
classified as `PeerReview`, so the same matrix governs build/test and authored peer-review
gates.

`NodeClassifier.NormalizeGateKind` also retains a legacy check-node-id fallback when
`gate_kind` is absent; ordinary executor selection is by type.

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

## 4. Historical parity guarantee — what it means and how it was verified

**Parity** means the default workflow, built through the generalized binder, emits the **identical** raw
`GraphDescriptorBuilder` edges, predicates, idempotent flags, and outputs as the pre-change hand-wired
pipeline — so the collapsed `GraphDescriptor`, the `workflow.step` stage stream, and the terminal states are
unchanged. This is mandatory because the binder is on the **live run pipeline** (the highest-risk change).

Verified by [`RunWorkflowGraphBinderTests`](../tests/Agentweaver.Tests/Workflows/RunWorkflowGraphBinderTests.cs):

- **`DefaultWorkflow_RealPath_ProducesCanonicalSixStageGraph`** — builds the descriptor through the **real**
  `RunWorkflowFactory` and asserts the current six-stage graph (nodes
  `agent, rai, review, merge, push-pr, scribe`; start `agent`).
- **`DefaultDefinition_Binder_ProducesCanonicalSixStageGraph`** — pins the same graph at the binder/unit
  level over the built-in default definition.
- **`RenamedNodeIds_ResolveByType_ProduceIdenticalGraph`** — a definition whose node ids are all renamed
  (types unchanged) collapses to the **same** graph, proving resolution is by type, not id.
- **`UnwiredNodeType_FailsClosed_WithNodeScopedError`** — an unbindable node throws a node-scoped
  `WorkflowBindException`.
- **`Loader_Accepts_PreviouslyRejectedNodeTypes`** — `fan_out` / `fan_in` / `peer_review` load.
- **`Loader_RejectsSerialNodeType_WithSequentialEdgesGuidance`** — legacy `serial` YAML fails with
  explicit guidance to use ordinary workflow edges for sequential execution.

The reflection-based **drift guard**
([`RunWorkflowDefinitionBindingTests`](../tests/Agentweaver.Tests/Graph/RunWorkflowDefinitionBindingTests.cs),
`CoordinatorWorkflowGraphDriftGuardTests`) continues to assert the built MAF graph matches the descriptor.

## 5. Status of `fan_out` / `fan_in` / `coordinator_composed`

`peer_review` is **fully wired** (see §2a) — both as a verdict gate and as a plain producing turn.

`fan_out` and `fan_in` are runtime-bound for one validated static region. The binder collapses the
declared branch nodes into a durable child-work request port:

1. `fan_out` creates or reattaches one correlated child coordinator work plan and one ordered
   subtask per declared branch.
2. The MAF request port checkpoints the parent. The watch loop persists that exact continuation
   before enabling child dispatch.
3. Existing child-run orchestration executes independent branches concurrently with durable
   observation and recovery fencing.
4. `fan_in` waits for every branch and returns one joined result in persisted branch-ordinal order.

This path deliberately bypasses coordinator integration-branch construction and collective
assembly: it performs no final Git merge, review, publication, or Scribe work. Failed, blocked,
cancelled, or RAI-flagged branches fail the join. The first release supports one non-nested fan
region, at least two unconditional one-node branches of type `prompt`, and a prompt or terminal
successor. `peer_review` and `build_test` remain valid outside a static fan, but are rejected as fan
branches until a specialized static executor preserves their semantics.

At first attachment, the child-work plan persists the incoming `AgentTurnInput` and current
worktree tree hash. Branch tasks are composed from that submitted/predecessor context plus the
branch prompt, and reattachment uses the persisted context rather than current workflow YAML.
Each branch reserves its child run id before launch and uses `(coordinatorRunId, subtaskId)` as the
durable idempotency key. Restart recovery re-observes terminal branches and restarts interrupted
active branches under their original Run ids.

The embedded coordinator is projected as a coordinator plan with only fan-out, branch, and fan-in
nodes. Its child graph references use `run:{childRunId}`. The parent suspension is classified as
`workflow_child_work`, not human review, so `/review`, MCP, and the web UI do not expose a review
action for that wait.

`coordinator_composed` remains load-accepted but runtime-pending and fails bindability validation.

`serial` is no longer a workflow node type. Use ordinary directed edges between nodes to express sequential
execution, for example `plan -> implement -> review`.

<details id="diagram-context-canonical-default-workflow">
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Generic default workflow</td></tr>
<tr><td>subtitle</td><td>Built-in template • merge → PR publication → Scribe</td></tr>
<tr><td>returns-heading</td><td>SOURCE / RETURN</td></tr>
<tr><td>outcomes-heading</td><td>OUTCOMES</td></tr>
<tr><td>footer</td><td>PR action can skip / fail and still reach Scribe. No-changes also reaches Scribe.</td></tr>
<tr><td>Agent work</td><td>Agent</td></tr>
<tr><td>Agent work</td><td>Agent task</td></tr>
<tr><td>Agent work</td><td>agent</td></tr>
<tr><td>RAI gate</td><td>Rai</td></tr>
<tr><td>RAI gate</td><td>Verdict routing</td></tr>
<tr><td>RAI gate</td><td>rai</td></tr>
<tr><td>Human review</td><td>Review</td></tr>
<tr><td>Human review</td><td>human-review</td></tr>
<tr><td>Merge</td><td>Merge</td></tr>
<tr><td>Merge</td><td>Merge outcome routing</td></tr>
<tr><td>Merge</td><td>merge</td></tr>
<tr><td>Publish / reuse PR</td><td>Publish / reuse PR</td></tr>
<tr><td>Publish / reuse PR</td><td>Create / reuse; not git push</td></tr>
<tr><td>Publish / reuse PR</td><td>action</td></tr>
<tr><td>Scribe</td><td>Scribe</td></tr>
<tr><td>Scribe</td><td>Record the run outcome</td></tr>
<tr><td>Scribe</td><td>scribe</td></tr>
<tr><td>Safety failed</td><td>Safety failed</td></tr>
<tr><td>Safety failed</td><td>Workflow endpoint</td></tr>
<tr><td>Declined</td><td>Declined</td></tr>
<tr><td>Done</td><td>Done</td></tr>
<tr><td>edge-02-label</td><td>revise</td></tr>
<tr><td>edge-03-label</td><td>safety- failed</td></tr>
<tr><td>edge-04-label</td><td>no- changes</td></tr>
<tr><td>edge-05-label</td><td>review</td></tr>
<tr><td>edge-06-label</td><td>approved</td></tr>
<tr><td>edge-07-label</td><td>request-changes</td></tr>
<tr><td>edge-08-label</td><td>declined</td></tr>
<tr><td>edge-09-label</td><td>merged</td></tr>
<tr><td>edge-10-label</td><td>blocked</td></tr>
</tbody></table>
</details>
