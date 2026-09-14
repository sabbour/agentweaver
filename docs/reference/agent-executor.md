---
title: Agent eXecutor (AX) Comparison
---

> **AX claims unverified in this repository-only review.** Treat AX API, roadmap, isolation, oversubscription, and effort comparisons below as hypotheses requiring dated primary-source validation, not deployed Agentweaver facts.

# Agent eXecutor (AX) Comparison

Agent eXecutor (AX) is Google's open-source distributed agent runtime. This comparison helps clarify where AX overlaps with Agentweaver, where the two systems operate at different layers, and when each approach is the better fit.

| Dimension | Agent eXecutor (AX) | Agentweaver |
| --- | --- | --- |
| What it is / Layer | Distributed agent harness runtime; orchestration substrate | Full-stack agent orchestration runtime with workspace, review, and merge flow |
| Language / Stack | Go | C#, TypeScript |
| Execution model | Single-writer controller with append-only event log, `conversationId` sessions, resumable gRPC streams | Coordinator expands an OutcomeSpec into a WorkPlan DAG and runs child tasks in parallel git worktrees on AgentHost pods |
| Isolation / Sandboxing | Compute-agnostic; can run on different substrates, but does not prescribe VM isolation | Kata VM-backed sandbox execution on AKS with layered network controls |
| Human-in-the-loop | On roadmap; approvals are not first-class today | Built-in review gates with approve, request-changes, and decline flows |
| Streaming / Observability | gRPC streaming plus durable event log and OpenTelemetry support | SSE streaming, durable `RunEvents` in PostgreSQL, live topology and run visibility |
| Git / Workspace | No built-in git workspace model | Per-run git worktree and branch lifecycle with merge serialization |
| MCP integration | No built-in MCP surface | Native MCP server that exposes runs and outcomes as tools |
| Steering | No redirect / steering concept called out in the runtime model | Mid-run coordinator steering and redirection |
| Status / License / Links | Open source, Apache 2.0, [GitHub](https://github.com/google/ax), [Google Cloud blog](https://cloud.google.com/blog/products/ai-machine-learning/agent-executor-googles-distributed-agent-runtime) | Alpha software, MIT, [GitHub](https://github.com/sabbour/agentweaver), [Docs](https://sabbour.me/agentweaver/) |

AX and Agentweaver overlap most at the orchestration layer, but they optimize for different boundaries. AX is stronger when the goal is a framework-agnostic distributed runtime that can sit over multiple compute backends at scale without owning the full developer workflow.

Agentweaver is broader in scope. It couples orchestration to sandboxed git workspaces, human review, and the run-to-merge path, while AX stays closer to the runtime substrate and eventing layer.

## Running Agentweaver on top of AX

[Agent eXecutor (AX)](https://github.com/google/ax) is Google's open-source distributed agent harness runtime (Go, Apache 2.0). It provides a single-writer session controller, an append-only event log, resumable gRPC streams, and a pluggable, compute-agnostic actor model. This section analyzes what it would take to run Agentweaver's child-agent runtime *on top of* AX, what maps cleanly, what must be built, and whether the trade is worth it today.

### Why you might want to

AX supplies several primitives Agentweaver does not have first-class:

- **Explicit resumption protocol.** AX's `--last-seq` cursor and `ConversationId` give durable session identity across disconnects, harness restarts, and *compute migrations*. Agentweaver has cursor-based SSE replay from `RunEvents`, but no notion of moving a live run between machines.
- **Cross-machine distribution & session portability.** AX sessions are location-independent actors; a run can survive a worker being drained.
- **Provider boundary.** Agentweaver uses A2A for remote AgentHost turns, but supports a snapshot-bound Copilot capability or BYOK provider configuration.
- **Pluggable event-log backends** rather than a single PostgreSQL schema.
- **Scale.** The warm pool requests two standby replicas; this is not a total-run capacity ceiling. Worker HPA is separate, currently two to three replicas. An AX oversubscription advantage remains an unverified hypothesis.

### What maps naturally

| AX concept | Agentweaver concept |
|---|---|
| `ConversationId` | `RunId` |
| Harness Actor (session) | AgentHost pod (one child run) |
| Append-only event log | `RunEvents` table (SSE event store) |
| Resumable gRPC stream + `--last-seq` | SSE cursor-based replay |
| `ax.yaml` server address | A2A endpoint `:8088` |
| Harness plug-in interface | wrapper around the Copilot A2A client |

The conceptual alignment is strong at the *single-run* level. The natural integration is to wrap Agentweaver's Copilot A2A client as an AX harness plug-in, and treat each child run as one AX session.

### What Agentweaver would need to add or change

**a) Compute layer.** Replace AgentHost pod management (`SandboxClaim` + `POST /configure`) with AX actor lifecycle RPCs (`ControlService.Resume`/`Suspend`). The warm pool becomes pre-registered AX actors on Substrate workers. The `/configure` step (run context + user token injection) must be re-expressed as AX actor activation. *Medium effort.*

**b) Event persistence.** AX's default per-session SQLite log cannot satisfy Agentweaver's multi-replica fan-out requirement — every API replica must stream the same run. This requires implementing a **PostgreSQL event-log adapter** for AX so the existing cursor semantics survive. *Medium effort, and load-bearing.*

**c) SSE ↔ gRPC.** The frontend speaks SSE; AX speaks gRPC. Preferred path: keep SSE delivery and feed it from the PostgreSQL adapter in (b), so the browser contract is untouched. Alternative (a gRPC-to-SSE gateway) adds a hop and a second replay cursor to reconcile. *Low–medium effort.*

**d) WorkPlan DAG dispatch.** AX has **no multi-agent orchestration**. The entire coordinator — `CoordinatorDispatchService`, `SubtaskFrontier` (ready/in-flight/blocked/done tracking), and `CoordinatorAssemblyService` — stays in Agentweaver and simply swaps direct Kubernetes calls for AX lifecycle RPCs. *No AX help here; unchanged.*

**Workspace contract.** Agentweaver owns branch/assembly/merge semantics. Implementation turns use verified pod-local writable checkouts and prepared Git writeback; assembly Build/Test uses a local read-only checkout. An AX adapter would have to preserve commit/tree verification and writeback, not necessarily mount a shared writable PVC.

**f) Human review gate + steering.** AX's human-in-the-loop ("tool call approvals from harnesses") is a **roadmap item, not implemented**. Agentweaver's `OutcomeSpec` review policy (RAI → rubberduck → human approve/request-changes/decline) and `CoordinatorSteeringService` (`Send`/`Redirect`/`Amend`, `assembly_blocked` steering-wait loop) have no AX equivalent and remain Agentweaver-native.

**Isolation hypothesis.** An adapter must preserve execution isolation and filesystem boundaries. AX runtime defaults, gVisor assumptions, and Kata compatibility require separate dated validation; this review establishes neither an isolation regression nor an effort estimate.

**Run capability boundary.** AgentHost receives one-time configuration containing a live `copilotCredential` or BYOK configuration. Repository/MCP credentials are separate purpose-scoped values. A hypothetical AX activation path must preserve these boundaries without ambient user-token lookup.

### Assessment: hypothesis only

An AX spike must validate lifecycle, recovery, provider-capability delivery, event persistence, verified workspace writeback and isolation. DAG dispatch, assembly, review, steering and persistence remain Agentweaver responsibilities unless an adapter proves otherwise. The repository establishes neither a two-run ceiling nor lack of cross-replica durable checkpoints. AX performance and effort claims require dated primary evidence.

| Component | Current (Agentweaver-native) | With AX | Unverified AX effort estimate |
|---|---|---|---|
| Child-run compute | AgentHost pod, `SandboxClaim` + `POST /configure` | AX actor, `Resume`/`Suspend` on Substrate | Medium |
| Warm pool / scale | Two standby pods; separate worker HPA | AX actors, ~30× oversubscription | Medium |
| Event persistence | `RunEvents` PostgreSQL, multi-replica fan-out | AX event log **+ required PostgreSQL adapter** | Medium |
| Client streaming | SSE cursor replay | SSE fed from adapter, or gRPC↔SSE gateway | Low–Medium |
| DAG orchestration | Coordinator, `SubtaskFrontier`, Assembly | Unchanged (no AX equivalent) | None (stays) |
| Git worktree / merge | `WorktreeManager`, integration branch | Preserve verified source and writeback contracts | Low |
| Review gate + steering | `OutcomeSpec` policy, `CoordinatorSteeringService` | Agentweaver-native; AX support unverified | N/A |
| Isolation | Kata VM (hardware boundary) | Isolation compatibility to validate | Medium / risk |
| Auth / secrets | One-time run capability or BYOK; separate repository/MCP credentials | Adapter must preserve capability boundaries | Low |
