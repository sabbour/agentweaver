---
title: Agent eXecutor (AX) Comparison
---

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

TODO: Document what an Agentweaver-on-AX deployment model would look like, including controller boundaries, workspace ownership, and where review/merge state would live.

---

## Running Agentweaver on top of AX

[Agent eXecutor (AX)](https://github.com/google/ax) is Google's open-source distributed agent harness runtime (Go, Apache 2.0). It provides a single-writer session controller, an append-only event log, resumable gRPC streams, and a pluggable, compute-agnostic actor model. This section analyzes what it would take to run Agentweaver's child-agent runtime *on top of* AX, what maps cleanly, what must be built, and whether the trade is worth it today.

### Why you might want to

AX supplies several primitives Agentweaver does not have first-class:

- **Explicit resumption protocol.** AX's `--last-seq` cursor and `ConversationId` give durable session identity across disconnects, harness restarts, and *compute migrations*. Agentweaver has cursor-based SSE replay from `RunEvents`, but no notion of moving a live run between machines.
- **Cross-machine distribution & session portability.** AX sessions are location-independent actors; a run can survive a worker being drained.
- **Framework-agnosticism.** The harness plug-in system lets you bring your own model/runtime. Agentweaver is currently welded to the GitHub Copilot A2A agent.
- **Pluggable event-log backends** rather than a single PostgreSQL schema.
- **Scale.** Agentweaver's AgentHost warm pool is fixed-size (×2 standby per cluster). AX on Agent Substrate reportedly reaches ~30× oversubscription — attractive for large coordinator runs fanning out many parallel child agents across the WorkPlan DAG.

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

**e) Git worktree management.** AX has no git awareness. `WorktreeManager` (branch/checkout/write, integration-branch build, child-wins conflict resolution) is untouched. The worktree still lives on the Azure Files RWX PVC — but AX actors must have that PVC mounted. *Low effort (mount plumbing).*

**f) Human review gate + steering.** AX's human-in-the-loop ("tool call approvals from harnesses") is a **roadmap item, not implemented**. Agentweaver's `OutcomeSpec` review policy (RAI → rubberduck → human approve/request-changes/decline) and `CoordinatorSteeringService` (`Send`/`Redirect`/`Amend`, `assembly_blocked` steering-wait loop) have no AX equivalent and remain Agentweaver-native.

**g) Isolation model.** Agentweaver uses **Kata VM** (hardware boundary). AX+Substrate assumes **gVisor** (its `ateom-gvisor` component). For code-executing agents, gVisor is a weaker threat model. Options: accept gVisor, or set a `kata-containers` runtime class on Substrate workers — technically possible on Kubernetes but fights Substrate's gVisor assumption. *Medium effort / risk.*

**h) Authentication / Key Vault.** AgentHost uses Azure Workload Identity to fetch per-user GitHub tokens from Key Vault. This must be preserved: AX actor worker pods need the same Workload Identity annotations. *Low effort, but mandatory.*

### Effort estimate and assessment

- **Thin integration (low effort):** harness plug-in wrapping the Copilot A2A client; PVC mount; Workload Identity annotations; SSE fed from the event log.
- **Meaningful rearchitecture (medium effort):** PostgreSQL event-log adapter for AX; compute lifecycle migration from `/configure` to `Resume`/`Suspend`; isolation-runtime decision.
- **Current blockers:** human-in-the-loop approvals (roadmap only) — so review gate and steering **cannot** move onto AX; the weaker default isolation model; and AX offering nothing for DAG orchestration, git, assembly, or merge.

**Verdict.** AX is a credible *single-session substrate*: its resumption, portability, and oversubscription directly address Agentweaver's fixed warm-pool ceiling and lack of cross-machine durability. But roughly half of Agentweaver's value — DAG coordination, git worktree/assembly, review policy, steering — lives entirely above AX's abstraction and stays put. The genuine win (elastic, migratable child-run compute) is real, but it is gated on building a PostgreSQL event-log adapter and resolving the Kata-vs-gVisor isolation regression. **Today the impedance mismatch and the unimplemented human-in-the-loop feature outweigh the gains for a code-executing, review-gated platform.** AX is worth prototyping as the compute/resumption layer for child runs at scale — not as a wholesale replacement for the coordinator.

| Component | Current (Agentweaver-native) | With AX | Effort |
|---|---|---|---|
| Child-run compute | AgentHost pod, `SandboxClaim` + `POST /configure` | AX actor, `Resume`/`Suspend` on Substrate | Medium |
| Warm pool / scale | Fixed ×2 standby per cluster | AX actors, ~30× oversubscription | Medium |
| Event persistence | `RunEvents` PostgreSQL, multi-replica fan-out | AX event log **+ required PostgreSQL adapter** | Medium |
| Client streaming | SSE cursor replay | SSE fed from adapter, or gRPC↔SSE gateway | Low–Medium |
| DAG orchestration | Coordinator, `SubtaskFrontier`, Assembly | Unchanged (no AX equivalent) | None (stays) |
| Git worktree / merge | `WorktreeManager`, integration branch | Unchanged; PVC mounted into AX actor | Low |
| Review gate + steering | `OutcomeSpec` policy, `CoordinatorSteeringService` | **Blocked** — AX HITL is roadmap-only | N/A |
| Isolation | Kata VM (hardware boundary) | gVisor default, or Kata runtime class | Medium / risk |
| Auth / secrets | Workload Identity → Key Vault | Same, on AX worker pods | Low |
