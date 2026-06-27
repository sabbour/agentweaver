# Feature Specification: Distributed Agent Execution & API Tier Scaling

**Feature Branch**: `018-distributed-agent-execution-scaling`

**Created**: 2026-06-27T02:54:26-07:00

**Status**: Draft (MASTER consolidated design — supersedes all prior scattered drafts)

**Author**: Morpheus (Runtime Engineer, Matrix squad)

**Requested by**: Ahmed Sabbour

**Scope**: DESIGN ONLY — no product code changes in this document.

**Companion docs** (do not duplicate; this doc defers to them):
- Data layer → `specs/018-distributed-agent-execution-scaling/data-postgres-migration.md` (Tank)
- Platform / Deployment → `specs/018-distributed-agent-execution-scaling/platform-deployment.md` (Link)

---

## 1. Problem statement & goals

### 1.1 Root problem (the driving pain: OOM)

The single Agentweaver API pod runs **every run's heavy execution state in-process**, in one pod
capped at 4Gi:

- The agent itself — `CopilotAIAgent : AIAgent` — wraps a live **GitHub Copilot SDK session**
  (`CopilotClient _client`, inner `GitHubCopilotAgent _inner`) and is created and driven **inside the
  API process** (`packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:40,86-87,148,296`). It is invoked
  by `AgentTurnExecutor.HandleAsync`, which calls `_agent.SetupAsync(...)` then `_agent.RunTurnAsync(...)`
  in-process (`packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:89-106`).
- The MAF workflow runs **in-process only**: `RunWorkflowFactory` and `CoordinatorWorkflowFactory`
  both launch via `InProcessExecution.RunStreamingAsync` / `ResumeStreamingAsync`
  (`apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1235,1396,1401`;
  `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:181-182,194-195`). The referenced
  `Microsoft.Agents.AI.Workflows` **1.9.0** ships only the in-process runner — no distributed runner,
  no A2A transport (`apps/Agentweaver.Api/Agentweaver.Api.csproj`,
  `packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj`).
- The coordinator's **own agent turns** (decompose / plan) also run in-process: it news up a
  `CopilotAIAgent` and drives it directly inside the API
  (`apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:479-510`).
- Per-run live state accumulates in the API process: live MAF `StreamingRun` objects, per-run
  `Channel<RunEvent>` writers, and the in-memory `RunStreamStore` that retains event history for up to
  **256** completed runs plus all in-flight runs (`apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:21,163-200`).

Because all of this is in-process, the pod's memory scales with **concurrent + recently-completed runs
× (SDK session + workflow graph + event history)**, and OOMs.

The pod is additionally pinned to **`replicas: 1` with `strategy: Recreate`** because SQLite is a
single-writer file on an RWO PVC (`k8s/api-deployment.yaml:9-14`), so the OOM cannot be mitigated by
scaling out. Container limit is `memory: 4Gi` (`k8s/api-deployment.yaml:156-162`).

### 1.2 The insight: security isolation and memory relief are the SAME fix

Moving agent execution into per-run sandbox pods is **simultaneously**:
1. the **security isolation** (each run's tool/model execution gets its own Kata-isolated pod), and
2. the **primary memory-relief / scalability fix** (the heavy SDK session, MAF runner, and tool
   execution leave the API process).

After the move, the API becomes a **thin orchestrator**: HTTP proxy + SSE relay + database. This is the
core of this design.

### 1.3 Goals

- G1. **Stop the OOM.** Evict the per-run heavy execution state (Copilot SDK session, in-pod MAF runner,
  tool execution) from the API process into per-run/per-turn **sandbox pods**.
- G2. **Isolate execution.** Each run's agent turns execute in a Kata-isolated sandbox pod with
  restricted egress.
- G3. **Scale the API tier horizontally** by removing the single-writer SQLite constraint and splitting
  web/worker roles with durable run leasing.
- G4. **Reuse MAF, not reinvent it.** Transport heavy execution over a thin MAF bridge that forwards
  MAF's already-serializable payloads — no bespoke wire protocol.
- G5. **Instant rollback.** Everything behind a flag defaulting to today's in-process behavior.

### 1.4 Non-goals

- N1. **No distributed MAF runner.** MAF stays **in-process within whichever tier owns the run** (the
  in-pod `AgentHost` runs its own `InProcessRunner`; the coordinator loop keeps using the in-API
  `InProcessExecution`). We do not build or wait for a cross-process MAF runner.
- N2. **No pod-per-coordinator recursion.** The coordinator's **orchestration loop stays in the
  API/worker tier**; only its individual agent turns are sandboxed (see §4.4).
- N3. **No "pods hold no secrets" constraint.** Pods MAY hold a run-scoped model credential / use AKS
  workload identity. (This was a security *recommendation*, not a user requirement.) Consequently the
  **capability-token broker is dropped** (see §10).
- N4. **No bespoke duplex sandbox-agent protocol.** Dropped in favor of the MAF bridge (see §10).
- N5. No change to the run/review domain model, the HITL review semantics, or the public REST/SSE
  contract surfaced to the frontend.

---

## 2. Current architecture (as-is)

### 2.1 Component inventory (with citations)

| Component | File | Role today |
|---|---|---|
| `CopilotAIAgent : AIAgent, IWorkflowTurnAgent` | `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:40` | Wraps the GitHub Copilot SDK session; serializable into MAF checkpoints (`SetupAsync` :148, `RunTurnAsync` :296, `Serialize`/`Deserialize` session :323-335). **Runs in-process.** |
| `AgentTurnExecutor : Executor<AgentTurnInput,AgentTurnOutput>` | `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:14` | MAF leaf executor for an agent turn: `SetupAsync` + `RunTurnAsync` then commit/diff/stepcount (:89-152). Token deltas stream via a side-channel `RecordingChannelWriter` (:32,67). |
| `CoordinatorOrchestratorExecutor` | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:43` | Owns the coordinator's decompose/plan/assemble logic. Drives its **own** in-process `CopilotAIAgent` for the decomposition turn (:479-510). |
| `CoordinatorWorkflowFactory` | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs` | Builds the coordinator graph; launches/resumes via `InProcessExecution.RunStreamingAsync`/`ResumeStreamingAsync` (:181-182,194-195). The confirm/revise HITL gate uses a `RequestPort` (`ConfirmationGateId`). |
| `RunWorkflowFactory` | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:25` | Builds per-run graphs; HITL review uses `RequestPort.Create<WorkflowReviewRequest,WorkflowReviewDecision>` (:380,1175); launches via `InProcessExecution.RunStreamingAsync` (:1235); checkpointing via `CheckpointManager.CreateJson(ResilientCheckpointStore.Create(_checkpointDir,...))` (:170-172). |
| `RunOrchestrator` | `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:17` | Entry points `StartRunAsync` (:76), `StartChildRunAsync` (:160), `StartRevisionAsync` (:328); `StartWorkflowOrFailAsync` (:384) launches the `StreamingRun`. |
| `RunWatchLoopService` | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:105` | The **orchestration loop**: single-consumer `await foreach (evt in streamingRun.WatchStreamAsync(ct))`; handles `ExecutorCompletedEvent` (:118) and the HITL `RequestInfoEvent` (:126). |
| `CoordinatorRunService` | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:459,723` | Watches the coordinator stream; resumes the `RequestPort` via `streamingRun.SendResponseAsync` (:377). |
| `RunStreamStore` / `RunStreamEntry` | `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:13,159` | In-memory per-run event history + completion signaling; retains 256 completed runs (:163); feeds SSE. |
| `KubernetesSandboxExecutor : ISandboxExecutor` | `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:39` | Today: runs **individual shell commands** in a `SandboxClaim` warm pod via WebSocket pod-exec (:70-148,247-285). It does **not** host an agent — it execs one command and tears down. |

### 2.2 As-is data flow (single tier)

```
        ┌──────────────────────── API pod (replicas:1, 4Gi) ──────────────────────────┐
        │                                                                              │
 HTTP → │ RunOrchestrator.StartRunAsync → RunWorkflowFactory.Build →                   │
        │   InProcessExecution.RunStreamingAsync ── StreamingRun                        │
        │        │                                                                      │
        │        ▼                                                                      │
        │   AgentTurnExecutor.HandleAsync                                               │
        │        │  _agent.SetupAsync()/RunTurnAsync()   ◄── HEAVY: Copilot SDK session │
        │        ▼                                          live in THIS process        │
        │   CopilotAIAgent → GitHub Copilot SDK → (KubernetesSandboxExecutor execs      │
        │                                          one shell command at a time)         │
        │        │                                                                      │
        │   RecordingChannelWriter → RunStreamEntry (RunStreamStore, 256 retained) ─────┼─→ SSE
        │                                                                              │
        │   RunWatchLoopService: await foreach WatchStreamAsync → RequestInfoEvent (HITL)│
        │                                                                              │
        │   SQLite (single-writer RWO PVC) ── forces replicas:1                         │
        └──────────────────────────────────────────────────────────────────────────────┘
```

**Why it OOMs:** every box above lives in the one pod, and the Copilot SDK session + MAF graph +
`RunStreamStore` history multiply by concurrent and recently-completed runs.

---

## 3. Target architecture (to-be)

### 3.1 Tier split

| Concern | **API / coordinator-worker tier** (stays) | **Sandbox pod tier** (moves out) |
|---|---|---|
| HTTP/REST + auth | ✅ | — |
| SSE relay to clients | ✅ (re-injects forwarded stream into `RunStreamStore`) | — |
| Database (runs, reviews, memory, leases) | ✅ (Postgres) | — |
| MAF **orchestration graph** for a run | ✅ in-process (`InProcessExecution`) in the owning **worker** | — |
| MAF **agent-turn** execution (leaf) | proxied via A2A | ✅ `MapA2A`-hosted `CopilotAIAgent` (`Agentweaver.AgentHost`) |
| GitHub Copilot SDK session (heavy) | — | ✅ in pod |
| Tool / shell / file execution | — | ✅ in pod (already Kata-isolated) |
| Coordinator **loop** (decompose/plan/assemble control flow) | ✅ in worker (NOT a pod) | — |
| Coordinator **agent turns** (the LLM calls) | proxied | ✅ in pod (same seam as any agent) |
| HITL `RequestPort` suspend/resume | ✅ (gate lives in the graph in the worker) | request forwarded from pod, resumed from worker |
| Checkpoints | ✅ brokered/DB-backed `ICheckpointStore` | pod reads/writes through the broker so it is resumable |

Key principle: **the orchestration graph and its HITL gates stay in the worker tier**; only the
**leaf agent turn** is relocated into a pod. The worker still owns `WatchStreamAsync` and
`SendResponseAsync`. MAF stays in-process on **both** sides of the bridge — just two different
processes, each with its own `InProcessRunner`.

### 3.2 To-be component diagram (ASCII)

```
            ┌──────────── WEB pods (stateless, N replicas) ────────────┐
  HTTP/SSE→ │  REST endpoints · auth · SSE relay from RunStreamStore   │
            │  (no MAF, no SDK session)                                 │
            └───────────────┬───────────────────────▲──────────────────┘
                            │ enqueue / lease         │ SSE events (DB or bus tailed)
                            ▼                          │
            ┌──────────── WORKER pods (own runs via lease) ────────────┐
            │  RunOrchestrator → RunWorkflowFactory                     │
            │  InProcessExecution.RunStreamingAsync  ── StreamingRun    │
            │  RunWatchLoopService: WatchStreamAsync (HITL RequestPort) │
            │                                                          │
            │  AgentTurnExecutor ── leaf agent is now:                  │
            │     RemoteAgentProxy : AIAgent  ───────────┐             │
            │  CoordinatorOrchestratorExecutor (LOOP)     │ MAF bridge  │
            │     coordinator agent turn → RemoteAgentProxy│ (HTTP/2 +  │
            │                                              │  SSE/gRPC) │
            │  Brokered ICheckpointStore (DB-backed) ◄─────┼──────┐     │
            └──────────────────────────────────────────────┼──────┼─────┘
                                                            │      │ checkpoint blobs
                                          turn input /      ▼      │ (read/write via broker)
                                          WorkflowEvent stream /   │
                                          ExternalRequest/Response │
            ┌──────────── SANDBOX POD (per run, Kata-isolated) ────┼─────┐
            │  Agentweaver.AgentHost                                │     │
            │    InProcessExecution.RunStreamingAsync (own runner)  │     │
            │    AgentTurnExecutor → CopilotAIAgent → Copilot SDK   │     │
            │    tool/shell/file exec in-pod                        ──────┘
            │  Run-scoped model credential (workload identity / claim-time token)
            │  Egress allowlist: model endpoint + API broker + git remote
            └────────────────────────────────────────────────────────────┘
                                       ▲
                                       │ shared workspace PVC (worktree) — unchanged
            ┌──────────── Azure Database for PostgreSQL Flexible Server ─┐
            │  runs · reviews · memory · run leases · checkpoint blobs   │
            └────────────────────────────────────────────────────────────┘
```

---

## 4. The MAF bridge design (detail)

The whole transport reuses MAF's already-serializable payloads. There is **no bespoke protocol**: we
forward exactly what MAF already produces — turn input, the `WorkflowEvent` stream,
`ExternalRequest`/`ExternalResponse` (the HITL `RequestPort` payloads), and checkpoint blobs.

### 4.1 `RemoteAgentProxy : AIAgent` (worker side)

`AgentTurnExecutor` already targets the `IWorkflowTurnAgent` / `AIAgent` abstraction
(`AgentTurnExecutor.cs:29,39`; `CopilotAIAgent` *is* an `AIAgent` at `CopilotAIAgent.cs:40`). Today the
executor is handed a concrete `CopilotAIAgent`. The bridge introduces a drop-in replacement leaf:

- `RemoteAgentProxy : AIAgent` implements the same `AIAgent` surface that `AgentTurnExecutor` drives
  (`SetupAsync`, `RunTurnAsync`/`RunStreamingAsync`, session serialize/deserialize at
  `CopilotAIAgent.cs:296,319,323-335`).
- Instead of holding a Copilot SDK session, it **forwards** each call over the bridge to the pod's
  `AgentHost`, and **re-emits** the pod's `WorkflowEvent` / token-delta stream locally so the rest of the
  worker graph (and `RecordingChannelWriter` → `RunStreamStore` → SSE) is unchanged.
- The worker's `AgentTurnExecutor.HandleAsync` flow (`AgentTurnExecutor.cs:89-152`) is **untouched in
  shape**: it still calls `SetupAsync`/`RunTurnAsync`, still commits the worktree, still computes the
  diff and step count (:135-137). Only the agent instance is now remote.

> **Transport realization (see §4.7):** `RemoteAgentProxy` is realized by the framework-native A2A
> client `Microsoft.Agents.AI.A2A.A2AAgent : AIAgent` (or `agentCard.AsAIAgent()`) rather than a
> bespoke HTTP/2+SSE proxy — this is "RemoteAgentProxy for free" with a standard schema, AgentCard
> discovery, and a built-in auth model. The HITL `RequestPort` and cross-process resume do **not** ride
> on A2A (reasons in §4.7); they stay in the worker / on our checkpoint store.

### 4.2 `Agentweaver.AgentHost` (in-pod, owns an in-process MAF runner)

A new minimal host process baked into the sandbox image:

> **A2A simplification (RESOLVED, §4.7.5):** with the A2A transport, `AgentHost` does **not** run an
> in-pod MAF graph/`InProcessExecution` at all — it hosts the leaf `CopilotAIAgent` (an `AIAgent`)
> directly via `MapA2A(agent, ...)` (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`). The bullets below
> describe the *logical* bridge contract; the A2A realization replaces "in-pod `InProcessRunner`" with
> "`MapA2A`-hosted `AIAgent`". MAF stays in-process only in the **worker**.

- Hosts the leaf `CopilotAIAgent` (an `AIAgent`) exposed over A2A via `MapA2A`. (Pre-A2A framing: a tiny
  in-pod MAF graph with its own `InProcessExecution.RunStreamingAsync` — superseded by §4.7.5.)
- Receives the forwarded turn input, sets up the real `CopilotAIAgent` (`SetupAsync` at
  `CopilotAIAgent.cs:148`), runs the turn, and streams agent updates + token deltas (and `RunEvent`
  `DataPart`s) back over A2A `message:stream`.
- Executes tools/shell/file ops in-pod (Kata-isolated). The agent runs **inside** the pod and uses
  local execution, so the per-command WebSocket pod-exec is **not** the agent-turn transport (A2A is —
  §4.7.6). **`KubernetesSandboxExecutor`'s exec path is retained unchanged for its existing
  per-command `run_command` purpose** (`KubernetesSandboxExecutor.cs:70-285`) and claim lifecycle
  (create/bind/TTL — `:165-227`); it is simply never used to carry agent turns. Nothing here deletes the
  existing exec functionality.

### 4.3 Mapping the existing `AgentTurnExecutor` graph onto the bridge

```
WORKER graph (unchanged shape)          BRIDGE                 POD graph (new, minimal)
──────────────────────────────          ──────                 ────────────────────────
AgentTurnExecutor.HandleAsync
  ├─ _agent.SetupAsync(...)  ───────────► forward SetupAsync ──► CopilotAIAgent.SetupAsync (real)
  ├─ _agent.RunTurnAsync(...) ──────────► forward RunTurnAsync ─► AgentHost InProcessRunner
  │     ◄── WorkflowEvent + token deltas ◄── stream back ◄────────  drives CopilotAIAgent
  ├─ _worktreeOps.CommitChanges  (worker side, shared PVC)
  ├─ _worktreeOps.GetDiff        (worker side, shared PVC)
  └─ return AgentTurnOutput      (worker side)
```

The worktree commit/diff/stepcount (`AgentTurnExecutor.cs:135-137`) stay on the worker side because the
worktree lives on the **shared workspace PVC** mounted by both tiers (the same PVC the sandbox already
mounts — `KubernetesSandboxOptions.WorkspaceMountPath`, `KubernetesSandboxExecutor.cs:18-19,351-364`).
This keeps the diff/commit logic where the run record is written.

### 4.4 Streaming

- Pod → worker: the pod's `InProcessRunner` emits MAF `WorkflowEvent`s and the same token deltas
  `CopilotAIAgent` produces (the side-channel `RecordingChannelWriter` model,
  `AgentTurnExecutor.cs:12,32,67`). These are serialized over the bridge (HTTP/2 SSE or gRPC server
  stream) and **re-injected** into the worker's `RunStreamEntry` via `RecordNext`/`Record`
  (`RunStreamStore.cs:83-110`) → existing SSE relay. The frontend contract is unchanged.
- Ordering is preserved by the existing monotonic sequence allocation under lock
  (`RunStreamStore.cs:70-95`); the bridge must deliver events in order (single stream per turn) so the
  re-injection assigns sequences in arrival order.

### 4.5 Checkpointing (brokered / DB-backed `ICheckpointStore`)

Today checkpoints are **local JSON files**: `CheckpointManager.CreateJson(ResilientCheckpointStore.Create(_checkpointDir,...))`
(`RunWorkflowFactory.cs:170-172`), with `_checkpointDir` defaulting under `AppPaths.DataDirectory`
(`RunOrchestrator.cs:160-162`). A pod cannot resume a run if the checkpoint only exists on one API
pod's local disk.

Target: a **brokered, DB-backed `ICheckpointStore`** so any worker (and the pod) can read/write the same
checkpoint:
- The `CopilotAIAgent` session serialize/deserialize seam (`CopilotAIAgent.cs:323-335`) already produces
  serializable session blobs; the pod forwards these blobs to the worker, which persists them through
  the brokered store into Postgres (blob column — see Data layer §7).
- `CheckpointManager.CreateJson(...)` is re-pointed at the brokered store instead of the file-backed
  `ResilientCheckpointStore`. Resume paths (`RunWorkflowFactory.cs:1396-1401`,
  `CoordinatorWorkflowFactory.cs:191-196`) read from the same store, so a different worker can resume.
- The pod is therefore **resumable**: on pod death, the worker re-claims the run, restores the latest
  checkpoint blob, and re-attaches (or re-spawns) a pod, replaying the session via
  `DeserializeSessionCoreAsync` (`CopilotAIAgent.cs:335`).

### 4.6 Coordinator: same seam, loop stays put

The coordinator's decomposition turn news up a `CopilotAIAgent` and drives it in-process today
(`CoordinatorOrchestratorExecutor.cs:479-510`). Under the bridge:
- The **loop** (`OrchestrateAsync`, decompose → plan → assemble, the `coordinator-orchestrate`
  `FunctionExecutor` at `CoordinatorWorkflowFactory.cs:150-161`) **stays in the worker**.
- Only the coordinator's **agent turn** swaps `new CopilotAIAgent(...)` (:479-486) for a
  `RemoteAgentProxy` pointed at a sandbox pod — identical mechanism to any worker agent. Its streaming
  onto the coordinator run timeline (`_streamStore.Get(...)` + `RecordingChannelWriter` at
  `CoordinatorOrchestratorExecutor.cs:493-494`) is preserved by the same re-injection used in §4.4.
- The confirm/revise HITL gate (`RequestPort` at `ConfirmationGateId`) and its `SendResponseAsync`
  resume (`CoordinatorRunService.cs:377`) remain in the worker — they are graph constructs, not agent
  turns.

---

### 4.7 Q1 transport decision: A2A (Agent2Agent) analysis

**Question (Ahmed):** can A2A serve as the worker→agent-host-pod transport instead of bespoke
HTTP/2+SSE — aligning with the standing directive *"don't reinvent the wheel; prefer framework-native
primitives"*? This section is grounded in Ahmed's authoritative MS Learn facts; nothing below
contradicts them.

#### 4.7.1 Authoritative facts (MS Learn + confirmed source)

- **Server/host side (.NET, confirmed):** `Microsoft.Agents.AI.Hosting.A2A` +
  `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` expose an `AIAgent` over A2A:
  `app.MapA2A(agent, path: "/a2a/x", agentCard: new AgentCard{...})`. Endpoints
  `POST .../v1/message:stream` (SSE) and `GET .../v1/card`; messages keyed by `messageId` + `contextId`
  (the server maintains conversation history per `contextId`). `AgentCard` advertises Capabilities
  (streaming, push notifications) and security schemes.
- **Client side (.NET, CONFIRMED to exist):** the MS Learn .NET sample shows the *host* side, but a .NET
  **client wrapper does exist** — `Microsoft.Agents.AI.A2A` ships `public sealed class A2AAgent : AIAgent`
  (constructed from an `IA2AClient`) **and** an extension `public static AIAgent AsAIAgent(this IA2AClient
  client, ...)` (`microsoft/agent-framework` `dotnet/src/Microsoft.Agents.AI.A2A/A2AAgent.cs`,
  `Extensions/A2AClientExtensions.cs`). The `A2A` SDK supplies `A2ACardResolver`/`IA2AClient`. So the
  worker does **not** drop to the raw `A2A` client — it wraps the remote endpoint as an `AIAgent`. This
  is the Python `A2AAgent` equivalent (`background=True`→`continuation_token`, `poll_task`).
  **Consequence: `RemoteAgentProxy`'s transport plumbing is essentially free** (the `AIAgent` surface is
  provided); we still hand-write a thin `IWorkflowTurnAgent` adapter (next point).
- **Versioning:** `Microsoft.Agents.AI.A2A` + `Microsoft.Agents.AI.Hosting.A2A(.AspNetCore)` are published
  and **version-aligned with our exact build** — they include `1.9.0-preview.260603.1` (the same stamp as
  our `Microsoft.Agents.AI.GitHub.Copilot`) through `1.11.1-preview.260625.1`. **Caveat:** the A2A line is
  `-preview` at every version, whereas `Microsoft.Agents.AI.Workflows` reached **stable `1.9.0`**.

#### 4.7.2 The decisive architectural fact: we remote at the AIAgent LEAF seam, not the graph

This resolves the "key tension." `AgentTurnExecutor` drives an `IWorkflowTurnAgent`/`AIAgent` leaf
(`AgentTurnExecutor.cs:29,39`), and `CopilotAIAgent` *is* an `AIAgent` (`CopilotAIAgent.cs:40`). We expose
**that leaf agent** via `MapA2A(copilotAgent)` and consume it on the worker as an `A2AAgent`. **The MAF
workflow graph stays entirely in the worker.** Therefore:

- MAF's **typed `WorkflowEvent` stream** (`ExecutorInvokedEvent`/`ExecutorCompletedEvent`/
  `RequestInfoEvent`, consumed by `RunWatchLoopService.cs:105,118,126`) is emitted by the **worker graph
  around the leaf** — it **never crosses the A2A boundary**. So there is **no MAF-event↔A2A-task
  translation layer**; A2A's `submitted/working/input-required/completed` task state machine simply
  isn't in the path. The crux ("does A2A flatten our typed MAF event stream?") is **avoided by
  construction**, because only the leaf's chat-style `AgentRunResponseUpdate` stream is transported, which
  A2A's `message:stream` carries natively.
- What *does* cross is (a) the turn input → A2A `message:stream`, and (b) the agent's output. Our agent
  emits **rich `RunEvent`s** through a **side-channel** (`RecordingChannelWriter`/`streamWriter`,
  `AgentTurnExecutor.cs:12,32,67`), not the `AIAgent` return stream. Those must be encoded as A2A
  `DataPart`s on the `message:stream` and decoded back into `RunEvent`s on the worker. **This `RunEvent`
  codec is the only shim we own — and it is required by ANY transport** (even bespoke Option C must
  serialize the `RunEvent` side-channel across the process boundary). A2A is therefore **not** adding a
  translation tax here; it is supplying a standard envelope (`DataPart`) for a serialization we must do
  regardless. Because we own both ends (same team/framework/language), the encoding is lossless.

#### 4.7.3 Ahmed's two caveats, weighed honestly

1. **"A2A adds value when you cross a process, service, or organizational boundary."** Our case is
   **same team / same framework / same language**, crossing only a **process** boundary (worker → sandbox
   pod). So A2A's **headline** value (cross-framework/org interop) does **not** apply. The *only*
   applicable driver is the process boundary, where A2A's contribution reduces to **"standardized HTTP+SSE
   framing + AgentCard discovery + a security-scheme model, with a maintained .NET host *and* client, so
   we don't hand-roll Option C."** Honest weighing: we are **not** paying a task-model tax, because we use
   A2A in **plain `message:stream` mode** (no long-running A2A task, no `input-required` over the wire —
   §4.7.2). The residual cost is a **preview dependency** + an in-pod HTTP listener (which Option C also
   needs). The benefit is deleting bespoke transport/schema/auth code. Given the standing directive, the
   benefit outweighs the (low, message-mode) cost — **but the win is "standardized Option C," not
   "interop we needed."** We state that plainly.
2. **"The remote agent manages its own conversation state (keyed by context ID)... if it restarts and
   loses state, your conversation context may be lost."** Confirmed: A2A's server-side `contextId` history
   is **ephemeral** — it is **not** durable MAF-superstep/checkpoint resume. This is exactly our
   pod-restart concern. **A2A will not carry the `CopilotAIAgent` session blob or MAF superstep state for
   us.** Our Q3 hybrid (checkpoint-and-release on `RequestPort`/coordinator-idle suspension, rehydrate on
   resume) therefore **still requires our own durable, out-of-band `ICheckpointStore`** (§4.5) plus the
   serialized session blob (`CopilotAIAgent.cs:323-335`). We **do not rely on A2A `contextId` state** for
   resumability; we largely bypass it. This is consistent — A2A handles *live* per-turn streaming; *we*
   handle durable resume.

#### 4.7.4 Security/Ops delta vs Seraph's 5 conditions

A2A still requires an **in-pod HTTP listener + east-west ingress rule**, so Seraph's conditions still
apply:

| Seraph condition | A2A delta |
|---|---|
| 1. mTLS / SPIFFE identity | **Helps, partially satisfies.** `AgentCard` carries **security schemes** (bearer/OAuth2) — a standard app-layer auth model (the `GET /v1/card` advertises it) we'd otherwise hand-roll. mTLS/SPIFFE still wraps it at the mesh layer. |
| 2. Scoped NetworkPolicy ingress to the pod listener | **Unchanged** — still required; A2A is just the listener's protocol. |
| 3. Last-Event-ID style resume | **Not provided by A2A** (live `contextId` is ephemeral; reconnection is beginning-only/task-only). Satisfied by our checkpoint store, not A2A. |
| 4. Bounded listener (no arbitrary exec surface) | **Marginally better** — `MapA2A` exposes a fixed `v1/message:stream` + `v1/card` surface, narrower than an ad-hoc endpoint. |
| 5. No egress broadening | **Unchanged** — egress allowlist (§5) identical. |

Net: A2A's AgentCard security schemes **help/partially satisfy** condition 1 and tighten 4, are
**orthogonal** to 2 and 5, and **do not** address 3 (we own it). **Seraph MUST re-review** the auth-model
delta (AgentCard bearer/OAuth2 vs our own API key) and confirm mTLS still wraps the A2A listener and the
`v1/message:stream` ingress is NetworkPolicy-scoped.

#### 4.7.5 VERDICT — (a) Adopt A2A as the Q1 transport, scoped to message/stream mode

**Adopt A2A as the standardized realization of Option C at the AIAgent leaf boundary, in plain
`message:stream` mode. Resume/checkpoint stays our out-of-band durable store (matches Q3); HITL stays in
the worker.** This is verdict **(a)** with one explicit scope: we use A2A's *streaming chat* surface, not
its task/`input-required`/`contextId`-durability features.

Concretely:

- **Host the leaf `CopilotAIAgent` via `MapA2A(agent, path, agentCard)`** in `Agentweaver.AgentHost`
  (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`). The pod needs **no in-pod MAF graph/InProcessRunner** —
  `MapA2A` hosts the `AIAgent` directly (simpler than the earlier §4.2 framing).
- **Realize `RemoteAgentProxy` as a thin `IWorkflowTurnAgent` adapter over `A2AAgent`/
  `IA2AClient.AsAIAgent()`** (`Microsoft.Agents.AI.A2A`). It maps our custom `SetupAsync(...)` params
  (worktree/repo/runId/modelId/systemPrompt — `CopilotAIAgent.cs:148`) onto the A2A `contextId`-init
  message and `RunTurnAsync(task)` onto `message:stream`; both ends are `AIAgent`, so
  `AgentTurnExecutor.HandleAsync` (`:89-152`) is untouched.
- **Carry the `RunEvent` side-channel as A2A `DataPart`s** on `message:stream`; decode → `RunEvent` →
  `RunStreamStore` → SSE on the worker. (Required by any transport; A2A just standardizes the envelope.)
- **Keep HITL review/confirm `RequestPort` in the WORKER graph** (§4.6) and **cross-process resume on the
  DB-backed `ICheckpointStore` + serialized session blob** (§4.5, §4.7.3 caveat 2). Do **not** depend on
  A2A `contextId` state or continuation tokens for durability.
- **Pin a specific A2A preview build** aligned with our line (`1.9.0-preview.260603.1`+) and gate the
  whole thing behind `Sandbox:AgentExecutionMode=pod-per-run` so `in-api` remains the instant-rollback
  default. Track the A2A line toward GA.

**Why (a) over (b)/(c)/(d):** the MS Learn facts confirm a **.NET client wrapper exists** (`A2AAgent`,
`AsAIAgent()`), so adoption is near-free and there is **no MAF-event↔task translation** (§4.7.2) — the
two concerns that previously argued for partial adoption (b) are dissolved by remoting at the AIAgent
seam and using message-mode. **(c)** *(A2A wire concepts but not the stack)* is dominated: the .NET host
**and** client both exist and are version-aligned, so re-implementing them ourselves would be the
reinvention we're told to avoid. **(b)** *(not a fit for inner MAF fidelity)* is rejected because MAF
fidelity is preserved precisely **because** WorkflowEvents never cross the boundary. **(d)
kube-exec-stdio as a wire transport is REJECTED entirely** (user directive) — *not* the v1 default and
*not* a fallback. **A2A is the SOLE worker→`AgentHost` wire transport.** There is no second wire
transport: the degraded/rollback path is `Sandbox:AgentExecutionMode=in-api` (revert to today's
in-process execution), **not** another protocol (§4.7.6).

**Honest caveats carried forward:** (i) we adopt a **preview** dependency on the run path **with no
alternate wire transport** — mitigated by version-pinning a known-good A2A build, the
`Sandbox:AgentExecutionMode=in-api` instant-rollback flag, and tracking the A2A line to GA (§4.7.6);
(ii) A2A's headline interop value does **not** apply to us — we adopt it purely as standardized Option C;
(iii) A2A gives us **nothing** for durable resume (its `contextId` state is ephemeral) — that stays our
problem, already solved by the checkpoint store. **Seraph re-review is REQUIRED** (§4.7.4: AgentCard auth
delta + listener/ingress).

#### 4.7.6 Sole transport + rollback model (no second wire)

- **A2A is the only wire transport** for agent-turn execution between the worker and the sandbox
  `AgentHost`. kube-exec-stdio is **not** a transport option in any mode.
- **Rollback / degraded mode is the flag, not a protocol:** `Sandbox:AgentExecutionMode=in-api`
  (default) runs the agent in-process exactly as today — the instant, fully-tested rollback for any A2A
  defect or outage. `pod-per-run` activates the A2A transport. Switching back requires no second wire and
  no exec-stdio path.
- **Residual risk (explicit):** an **A2A `-preview` library on the hot path with no alternate wire
  transport.** Mitigations: (1) **pin** a specific known-good A2A build aligned with our line
  (`1.9.0-preview.260603.1`+); (2) the **`in-api` rollback flag** removes the A2A path entirely without
  redeploying a different transport; (3) **GA tracking** of the A2A package line, upgrading the pin only
  after validation. This residual risk is the reason `in-api` remains the default until P1 soak
  completes. **This adjusts Seraph's gate H7:** drop "exec-stdio kept as a live fallback"; the rollback
  is the `in-api` flag, and H7 instead records the above residual-risk mitigations. H1–H6 remain
  mandatory gates unchanged (see Seraph's artifact).

## 5. Credential model WITHOUT the broker

No capability-token broker. The pod legitimately holds a **run-scoped model credential**:

- **Preferred: AKS workload identity.** The sandbox pod's ServiceAccount is federated (the API
  deployment already uses `azure.workload.identity/use: "true"` — `k8s/api-deployment.yaml`), so the
  pod obtains a short-lived model token via the OIDC-federated identity. Nothing is baked into the
  image.
- **Alternative: claim-time injection.** When the worker creates the `SandboxClaim`
  (`KubernetesSandboxExecutor.CreateClaimAsync`, `KubernetesSandboxExecutor.cs:165-184`), it injects a
  **run-scoped, short-lived** model token (e.g. via a projected secret / claim spec field) consumed by
  `AgentHost` at startup. The token's lifetime is bounded by the `SandboxClaim` TTL
  (`KubernetesSandboxOptions.TimeoutSeconds`, `:20-21`).
- **Never baked into the image.** The image contains only `AgentHost` + the Copilot SDK runtime; the
  credential arrives at claim/startup time and is scoped to the single run.

**Egress allowlist** (NetworkPolicy on the sandbox pod — the executor already excludes the cluster
service CIDR, `KubernetesSandboxExecutor.cs:22-24,166-168`):
- ✅ Model endpoint (GitHub Copilot / Foundry inference).
- ✅ The API broker endpoint (bridge: stream-back, checkpoint persist, loopback Agentweaver API tools —
  the same tools `CopilotAIAgent` calls today via `_apiBaseUrl`/`_apiKey`, `AgentTurnExecutor.cs:33-34,98-99`).
- ✅ Git remote(s) the run legitimately needs (clone/push for the worktree).
- ❌ Everything else (default deny), including arbitrary in-cluster services.

---

## 6. Memory-relief analysis (why this fixes the OOM)

**Leaves the API/worker process entirely (into the pod):**

| State | Where today | After |
|---|---|---|
| GitHub Copilot SDK client + session (`_client`, `_inner`, `_sessionConfig`) | API process, per active run (`CopilotAIAgent.cs:86-94`) | In pod, dies with the pod |
| Inner agent streaming buffers / tool-call state | API process (`CopilotAIAgent.cs:319`) | In pod |
| Tool/shell/file execution buffers (≤4 MB stdout/stderr per command, `KubernetesSandboxExecutor.cs:250,291-307`) | API process | In pod |
| Coordinator decomposition agent session | API process (`CoordinatorOrchestratorExecutor.cs:479-510`) | In pod |

**Stays in the worker but is comparatively light:**
- The MAF orchestration graph structure (executors, edges) — small, no SDK session.
- `RunStreamEntry` event history — already bounded to 256 completed runs (`RunStreamStore.cs:163`) and
  can be trimmed further / offloaded to Postgres once events are durable.
- The `WatchStreamAsync` loop + HITL `RequestPort` state — small.

**Quantified relief:** the dominant per-run footprint is the live Copilot SDK session plus its inner
agent streaming/tool state, which is exactly what moves. The API/worker process footprint becomes
~O(number of runs this worker *owns the graph for*) × (graph + bounded event history), instead of
O(active + 256 recent runs) × (graph + **SDK session** + history). Removing the SDK session and tool
buffers from the 4Gi process is the OOM fix. Combined with §8 web/worker split, no single process holds
more than its leased subset of runs, so memory scales horizontally rather than vertically.

---

## 7. Data layer (DEFERS to `data-postgres-migration.md` — Tank)

This design **depends on** the following decisions/interfaces owned by Tank; it does not re-specify
them:

- **LOCKED:** SQLite → **Azure Database for PostgreSQL Flexible Server**. This removes the single-writer
  RWO constraint that forces `replicas: 1` / `strategy: Recreate` (`k8s/api-deployment.yaml:9-14`).
- **Interfaces this doc relies on:**
  - A **run lease** table/primitive (durable leasing with project/run affinity) for the web/worker split
    in §8.
  - A **checkpoint blob** store reachable by all workers (the brokered `ICheckpointStore` backing in
    §4.5) — replaces the local-file `ResilientCheckpointStore` (`RunWorkflowFactory.cs:170-172`).
  - Durable run-event persistence sufficient for web pods to relay SSE without holding the producing
    process (so `RunStreamStore`'s in-memory history can shrink).

Concurrency, schema, migration order, and connection management are Tank's to specify.

---

## 8. Platform / Deployment (DEFERS to `platform-deployment.md` — Link)

This design **depends on** the following decisions/interfaces owned by Link:

- **Web/worker role split:** stateless **web** pods (REST + SSE relay, scalable to N) and **worker**
  pods that own runs via the durable lease (§7). The current single deployment
  (`k8s/api-deployment.yaml`) splits into these roles.
- **Run leasing with project/run affinity** (acceptable per user) — a worker that holds a lease owns
  that run's MAF graph in-process; HITL resume and `RemoteAgentProxy` setup target the owning worker.
- **Sandbox pod tier:** the `SandboxClaim` warm-pool mechanism already exists
  (`KubernetesSandboxExecutor.cs:27-55,165-227`); platform owns the pool sizing, the `AgentHost` image,
  NetworkPolicy egress allowlist (§5), and workload-identity federation for the run-scoped credential.
- **Shared workspace PVC** mounted by both worker and sandbox pods (the worktree path,
  `KubernetesSandboxOptions.WorkspaceMountPath`, `KubernetesSandboxExecutor.cs:18-19`).

Routing/affinity implementation, pod templates, autoscaling, and the warm-pool controller are Link's to
specify.

---

## 9. Phased rollout behind a flag

**Flag:** `Sandbox:AgentExecutionMode` ∈ { `in-api`, `pod-per-run` }. **Default `in-api`** (today's
in-process behavior) for instant rollback. `pod-per-run` activates the MAF bridge. Granularity within
`pod-per-run` is **hybrid** (Q3 resolved): the pod is warm for an active reasoning burst but
checkpoint-released when the graph suspends on a `RequestPort` (HITL/review) or the coordinator idles
awaiting children, then re-claimed+rehydrated on resume. Tuning sub-flag `Sandbox:ReleasePodOnSuspend`
(default `true`) disables the release for low-latency-resume/debug.

| Phase | Goal | Primary files touched |
|---|---|---|
| **P1 — Agent execution in pods via MAF bridge (THE OOM FIX)** | Relocate agent turns (worker + coordinator agents) into sandbox pods; API/worker becomes thin orchestrator. Gated by `Sandbox:AgentExecutionMode=pod-per-run`. | NEW `RemoteAgentProxy`, NEW `Agentweaver.AgentHost`, NEW bridge transport; MODIFY `AgentTurnExecutor.cs` (inject proxy vs concrete), `CoordinatorOrchestratorExecutor.cs:479-510` (proxy vs `new CopilotAIAgent`), `RunWorkflowFactory.cs` (brokered `ICheckpointStore` :170-172), `KubernetesSandboxExecutor.cs` (claim hosts `AgentHost` rather than per-command exec), DI wiring. |
| **P2 — Postgres migration** | SQLite → Azure PostgreSQL Flexible Server; durable checkpoint blobs + run events. Unblocks removing `replicas:1`/`Recreate`. | Owned by Tank (`data-postgres-migration.md`); MODIFY `k8s/api-deployment.yaml:9-14` (drop single-replica constraint), checkpoint store backing. |
| **P3 — Web/worker split + run leasing** | Split stateless web (SSE relay) from worker (owns run graph) with durable leasing + affinity; scale web horizontally. | Owned by Link (`platform-deployment.md`); MODIFY `RunOrchestrator.cs` (claim lease before `StartWorkflowOrFailAsync` :384), `RunWatchLoopService.cs` (worker-owned watch), SSE relay reads durable events. |

Each phase is independently shippable; P1 alone stops the OOM. P2/P3 deliver horizontal scale.

---

## 10. File-by-file change list

### 10.1 New files

- `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs` — thin adapter wrapping the
  framework-native `Microsoft.Agents.AI.A2A.A2AAgent` (an `AIAgent`) as our `IWorkflowTurnAgent` leaf, so
  `AgentTurnExecutor` is unchanged (§4.1, §4.7.5). **No bespoke transport** — A2A (HTTP+SSE) carries the
  turn-forward + streaming path.
- `apps/Agentweaver.AgentHost/` (new project) — in-pod host that exposes the leaf `CopilotAIAgent`
  (an `AIAgent`) over A2A via `Microsoft.Agents.AI.Hosting.A2A` / `A2A.AspNetCore` `MapA2A` (§4.2,
  §4.7.5). No in-pod MAF runner — `MapA2A` hosts the `AIAgent` directly.
- `RunEvent`↔A2A `DataPart` encoder — the only translation layer we own (§4.7.2); maps our rich
  side-channel `RunEvent`s onto A2A `message/stream` updates.
- A brokered/DB-backed `ICheckpointStore` implementation replacing file-backed
  `ResilientCheckpointStore` for cross-worker/pod resume (§4.5; backing owned by Tank §7).

### 10.2 Modified files

- `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs` — leaf agent becomes the injected
  `IWorkflowTurnAgent` resolved per `Sandbox:AgentExecutionMode` (concrete `CopilotAIAgent` for
  `in-api`, `RemoteAgentProxy` for `pod-per-run`). Shape at :89-152 preserved.
- `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:479-510` — coordinator agent
  turn uses the same flag-driven proxy; loop unchanged.
- `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:170-172` — `CheckpointManager` backed by brokered
  store; resume paths :1396-1401 read from it.
- `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:191-196` — resume via brokered store.
- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs` — claim now provisions a pod that runs
  `AgentHost` (exposed over A2A) for the run's lifetime; claim lifecycle :165-227 and
  workspace resolution :351-364 reused; credential injected at claim time (§5). **The existing
  per-command `run_command` exec path (:70-285) is kept as-is** for its current purpose — it is just
  never used as the agent-turn transport (A2A is the sole transport, §4.7.6).
- DI/composition root — register `RemoteAgentProxy`/`A2AAgent` client, `AgentExecutionMode` flag binding.
- `packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj` (+ a new `Agentweaver.AgentHost.csproj`) —
  add `Microsoft.Agents.AI.A2A` (worker client) and `Microsoft.Agents.AI.Hosting.A2A` /
  `A2A.AspNetCore` (pod host), pinned to the build aligned with our `1.9.0-preview.260603.1` line (§4.7.1).
- `k8s/api-deployment.yaml` — (P2/P3) drop `replicas:1`/`Recreate` once Postgres lands; split roles.

### 10.3 Explicitly REMOVED from prior drafts (and why)

- **Capability-token broker / `CapabilityTokenService`** — REMOVED. The "pods hold no secrets"
  constraint is dropped (N3); pods legitimately hold a run-scoped model credential via workload
  identity / claim-time injection (§5). A broker is unnecessary complexity.
- **Coordinator-as-pod recursion** — REMOVED. The coordinator **loop** stays in the worker (N2, §4.6);
  only its agent turns are sandboxed via the same seam. No recursive pod-per-coordinator.
- **`ISandboxAgentHost` bespoke duplex protocol** — REMOVED. Replaced by the thin MAF bridge that
  forwards MAF's already-serializable payloads (N4, §4). We reuse MAF streaming + checkpointing +
  `RequestPort` suspend/resume instead of inventing a wire protocol.

---

## 11. Test plan

### 11.1 New coverage for the bridge

- **`RemoteAgentProxy` unit tests:** forwards `SetupAsync`/`RunTurnAsync`; re-emits a fake pod
  `WorkflowEvent`/token-delta stream into a `RunStreamEntry` and asserts monotonic sequence ordering
  (`RunStreamStore.cs:70-95`) and that the existing SSE snapshot (`GetSnapshotSince`, :117-124) is
  unchanged vs `in-api` mode.
- **`AgentHost` integration test:** stand up the `MapA2A`-hosted `AgentHost` with a fake `CopilotAIAgent`
  (no live SDK), drive it through the worker-side `A2AAgent`/`RemoteAgentProxy`, and assert the forwarded
  `AgentTurnOutput` matches the in-process executor's output (diff/stepcount parity with
  `AgentTurnExecutor.cs:135-152`).
- **Checkpoint round-trip:** serialize a session blob (`CopilotAIAgent.cs:323-335`), persist via the
  brokered store, resume on a *different* simulated worker, assert run continues. Covers pod-death
  resumability (§4.5).
- **HITL across the bridge:** an `ExternalRequest` raised inside a pod turn surfaces as a worker-side
  `RequestInfoEvent` (`RunWatchLoopService.cs:126`) and `SendResponseAsync` (`CoordinatorRunService.cs:377`)
  resumes correctly.
- **Flag parity:** the same run produces equivalent event streams under `in-api` and `pod-per-run`
  (golden-stream comparison) — guards the rollback path.

### 11.2 Two existing MUST-FIXES (called out explicitly)

1. **`KubernetesSandboxExecutor` has ZERO coverage.** Add unit tests with a faked `IKubernetes`:
   - `CreateClaimAsync` emits the correct CRD manifest (group/version/plural, TTL —
     `KubernetesSandboxExecutor.cs:165-184`).
   - `WaitForBoundAsync` parses `status.sandbox.name` and `status.podName` and the `Bound` phase
     (:190-227).
   - `ParseExitCode` for Success / Failure-with-`ExitCode`-cause / unparseable (:314-349).
   - `ResolvePodWorkingDirectory` accepts mount + children and rejects escapes (:351-369).
   - timeout clamp below TTL (:80-90) and ≤4 MB bounded read truncation (:291-307).
2. **`SandboxEscapeEndToEndTests` are silent false-positives.** Both facts `return;` when
   `RUN_LIVE_PROVIDER_TESTS` is unset (`tests/Agentweaver.Tests/SandboxEscapeEndToEndTests.cs:43-49,67-73`),
   so they report **passing** while doing nothing. **Convert to `Assert.Skip(...)`** (xUnit v3 dynamic
   skip) so the suite reports *skipped*, not *passed*, when the opt-in is absent.

### 11.3 Regression / non-functional

- Memory ceiling test (or load harness) demonstrating the worker process no longer grows with SDK
  sessions under N concurrent runs (validates §6).
- Egress-policy test: sandbox pod can reach model + broker + git remote and is denied everything else
  (§5).

---

## 12. Open questions for the user

1. **Bridge transport — RESOLVED (§4.7): adopt A2A primitives for the message/stream path (verdict a, scoped to message-mode).**
   Realize `RemoteAgentProxy` via the framework-native, version-aligned `Microsoft.Agents.AI.A2A.A2AAgent`
   + `A2A.AspNetCore` hosting; keep HITL `RequestPort` in the worker and resume on the checkpoint store
   (A2A tasks are unimplemented in the current `A2AAgent` and offer beginning-only replay). **Seraph
   re-review requested** on the AgentCard bearer/OAuth2 auth delta. Remaining sub-question: pin to which
   A2A preview build, given the line is preview-only at every version.
2. **Pod granularity:** **RESOLVED → HYBRID** (Morpheus, decision
   `Morpheus-q3-pod-granularity-hybrid-pod-per-run-by-default-r.md`). Pod-per-run by default — one
   warm `AgentHost` (InProcessRunner + live Copilot SDK session) serves all consecutive agent turns
   of an active reasoning burst, matching the run-scoped claim retention at
   `KubernetesSandboxExecutor.cs:128-129,141-146`. **But the pod is checkpoint-and-released whenever
   the MAF graph suspends on a `RequestPort`** — a HITL/review gate (`RunWatchLoopService.cs:126`
   `RequestInfoEvent`; coordinator `ConfirmationGateId`) or the coordinator loop idling awaiting child
   runs — and **re-claimed + rehydrated from the brokered checkpoint on resume**
   (`SendResponseAsync`, `CoordinatorRunService.cs:377`, or child-run completion). The release
   boundary is graph suspension on an external gate, **not** mere inter-turn boundaries (those stay
   warm). Pure **pod-per-turn** is rejected: per-turn warm-pool claim + `SetupAsync` + session
   `DeserializeSessionCoreAsync` (`CopilotAIAgent.cs:148,323-335`) on every turn is unaffordable
   latency/token cost and risks session round-trip drift. Pure **pod-per-run** is rejected: pods held
   through unbounded HITL/child-await idle recreate a softer, distributed OOM and waste the
   coordinator's long idle life (its loop stays in the worker per §4.6). Resume correctness requires
   the serialized session blob + MAF superstep checkpoint (incl. the suspended `ExternalRequest`
   correlation id) in the brokered store (§4.5); the worktree is already durable on the shared PVC and
   the run-scoped credential is re-injected at re-claim (§5). Surface: keeps flag value
   `Sandbox:AgentExecutionMode=pod-per-run` (suspend-release is internal behavior) plus tuning
   sub-flag `Sandbox:ReleasePodOnSuspend` (default `true`).
3. **Credential mechanism (§5):** workload identity (preferred, no token in pod) vs claim-time token
   injection — or both, with workload identity primary and injection as fallback?
4. **Worktree location during a turn:** keep commit/diff on the worker over the shared PVC (§4.3), or
   move commit/diff into the pod and forward only the resulting tree hash/diff? Shared-PVC-on-worker is
   simpler and keeps DB-write logic central.
5. **Coordinator decomposition streaming:** confirm re-injecting pod events onto the coordinator run
   timeline (`CoordinatorOrchestratorExecutor.cs:493-494`) is acceptable as-is, or should coordinator
   agent turns get a distinct sub-stream?
6. **P2/P3 sequencing vs P1:** is shipping P1 (OOM fix) on **current SQLite + replicas:1** acceptable as
   an interim (agent state still leaves the process, but API still can't scale out), or must Postgres
   (P2) land in the same release?
7. **`KubernetesSandboxExecutor` per-command path:** RESOLVED for transport — it is **never** the
   agent-turn transport (A2A is sole, §4.7.6). The existing per-command `run_command` exec path is
   **retained** for its current utility-exec purpose. (Open only: whether any non-agent utility execs
   still rely on it long-term, or it can eventually be narrowed.)

---

## 13. Summary

This MASTER doc consolidates the agreed architecture: **move agent execution (worker agents AND the
coordinator's own agent turns) out of the single API pod into per-run Kata-isolated sandbox pods**,
turning the API into a thin orchestrator (proxy + SSE relay + DB). This is simultaneously the security
isolation and the **primary OOM fix**, because the heavy GitHub Copilot SDK session, in-pod MAF runner,
and tool execution leave the 4Gi API process. Transport is the **framework-native A2A protocol**
(verdict a, scoped to message/stream mode, §4.7): `RemoteAgentProxy` is a thin `IWorkflowTurnAgent`
adapter over `Microsoft.Agents.AI.A2A.A2AAgent` / `IA2AClient.AsAIAgent()` (an `AIAgent`, version-aligned
`1.9.0-preview.260603.1`), and `Agentweaver.AgentHost` exposes the leaf `CopilotAIAgent` directly via
`MapA2A` (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`) — so the pod needs **no in-pod MAF runner**. A2A
is HTTP+SSE under the hood, giving us a standard schema, AgentCard discovery, and a built-in auth model
"for free" on the turn-forward + streaming path, honoring the "don't reinvent the wheel" directive. We
remote at the **AIAgent leaf seam**, so MAF's typed `WorkflowEvent`s never cross the boundary (no
event↔task translation); only the leaf's chat updates + our `RunEvent` `DataPart` codec cross. Crucially,
the HITL `RequestPort` suspend/resume **stays in the worker graph**, and cross-process resume rides our
**DB-backed `ICheckpointStore`** (A2A's `contextId` state is ephemeral; reconnection is beginning-only,
task-only) — so we take A2A only where it is ready. The **coordinator's loop stays in the worker**; only
its agent turns are sandboxed. Pods may
hold a **run-scoped model credential** (workload identity / claim-time injection), so the
**capability-token broker, `CapabilityTokenService`, coordinator-as-pod recursion, and the bespoke
`ISandboxAgentHost` duplex protocol are all dropped**. Rollout is phased behind
`Sandbox:AgentExecutionMode` (default `in-api`): **P1** agent-execution-in-pods (the OOM fix) → **P2**
Postgres migration (defers to Tank) → **P3** web/worker split + run leasing (defers to Link). The test
plan adds bridge/checkpoint/HITL coverage and fixes the two known gaps: **zero coverage on
`KubernetesSandboxExecutor`** and the **silent false-positive `SandboxEscapeEndToEndTests`** (convert
`return;` to `Assert.Skip`).

**Key open questions:** (1) ~~bridge transport~~ **RESOLVED → adopt A2A primitives (verdict a, message-mode);
Seraph re-review the AgentCard auth delta; pick the pinned A2A preview build**; (2) ~~pod-per-run vs pod-per-turn~~
**RESOLVED → hybrid (pod-per-run with checkpoint-release on RequestPort/coordinator-idle suspension,
re-claim+rehydrate on resume)**; (3) credential via workload identity vs claim-time injection; (4) worktree commit/diff on worker vs in
pod; (5) whether P1 can ship on current SQLite or must wait for Postgres.
