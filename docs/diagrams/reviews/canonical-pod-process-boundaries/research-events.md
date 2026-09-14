## Summary

The five requested diagram identities remain useful, but the shared-communication overview needs an explicit child-context exception, and the scaling topology needs **two corrections**, not just the HPA label: shipped scaling uses CPU/memory, and **`App:Role=web` is not a verified prohibition on background orchestration**. A2A carries structured `RunEvent` data as well as assistant output; the database/checkpoint authority stays outside AgentHost. PostgreSQL event ordering is allocated under **ReadCommitted + a per-run advisory transaction lock**, followed by cursor polling—not a cross-replica notification bus. Confirm both proposed merges; do not resurrect either redundant local figure.

**Scope:** read-only inspection of `sabbour/agentweaver`, worktree root **`C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`**. All repository citations below resolve under that absolute root. No edits, tests executed, agents spawned, or network uploads. The available GitHub tool surface lacked `get_me`; local worktree evidence was used.

## 1. Verified findings and required qualifications

### Shared context: child exception and current trust semantics

- Ordinary compilation selects active, **approved architectural/scope decisions**, non-legacy agent core memory, high-importance learnings/patterns, approved cross-team contributions, and the current session. Memory selection is budgeted and importance/recency ordered; it is not simply an unconditional concatenation of four complete layers.
  `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:38-135`
- Coordinator children call **`CompileDecisionsAsync`**, not `CompileAsync`. That compiler returns approved active architectural/scope decisions and empty memory/session collections. Children still receive charter, assigned skills, memory protocol, and capabilities: **“decisions-only compiled memory context” does not mean “decisions are the entire child prompt.”** Compilation failure is logged and execution can continue without injected decisions, so “always reaches every child” is too absolute.
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunOrchestrator.cs:1041-1081`
  `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:150-174`
- Another current-code correction: not every learning passes through the decision inbox. Runtime tools expose both inbox submissions and **`record_memory`**; the memory endpoint persists the latter directly as a **Pending** memory record. Do not draw all memory writes as inbox proposals, or pending memory as approved team authority.
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/AgentweaverApiTools.cs:71-153`
  `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/MemoryEndpoints.cs:136-159`
- Compiler context is represented as an `agentweaver.untrusted-context.v1` payload. “Approved decision” is domain authority, not an instruction-priority override.
  `sabbour/agentweaver:apps/Agentweaver.Api/Memory/MemoryContextCompiler.cs:176-198`

### A2A: execution transport with structured events

- Five workflow factory methods return the same proxy type: worker, RAI, Rubberduck, Build/Test, and Scribe. Factory documentation explicitly keeps graph, gates, checkpoint management, and durable ownership in the hosting orchestration process; AgentHost has no checkpoint-store dependency.
  `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs:9-24`
  `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs:50-63`
- Request: one user message containing **setup `DataContent` + task `TextContent`**. Setup includes assembled system context and identity. Return: assistant content plus encoded structured run events, decoded and forwarded to the caller’s event writer.
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:220-251`
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:303-344`
- AgentHost applies the worker-provided context before executing the turn, drains emitted `RunEvent` values, and encodes them back into the A2A stream. A focused test explicitly verifies this.
  `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:202-265`
  `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/A2ATurnBridgeAgentTests.cs:341-366`
- Do not equate transport EOF with successful turn completion: the proxy tracks the definitive `agent.turn.end` marker and structured failures.
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:257-274`
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:324-339`
- Health/auth qualification: `/healthz` returns **200 `"standby"` before configuration**, then `"ready"`; the turn endpoint’s bearer-equality guard is conditional on a **nonempty configured token**. This is not unconditional enforcement for every possible configuration.
  `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:386-411`

### Scaling: shipped metrics and an additional topology caveat

- Actual worker HPA: **min 2 / max 3; CPU 70%; memory 80%**. Backlog-driven KEDA is a commented recommended upgrade, not the active mechanism. The proposed gauge query is `max(agentweaver_run_queued)`, not `sum`, because replicas export the same global count.
  `sabbour/agentweaver:k8s/base/worker-hpa.yaml:7-46`
  `sabbour/agentweaver:k8s/base/worker-hpa.yaml:62-83`
  `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1083-1086`
- **Important delta from the audit report:** “web pods never own runs” is not established by implementation. `AppRole` distinguishes public web surfaces from the worker role, but `CoordinatorHeartbeatService` is registered unconditionally. Its default-enabled guard reads `Coordinator:HeartbeatEnabled`, **not `App:Role`**, and its loop performs backlog pickup. Therefore draw the web/worker split as the logical deployment separation—not as a hard isolation property enforced solely by the role flag.
  `sabbour/agentweaver:apps/Agentweaver.Api/AppRole.cs:3-24`
  `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:521-528`
  `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:45-78`
  `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:91-121`
- The P1/P2 distinction is sound: event/checkpoint/lease providers are selected independently of remote-agent execution. PostgreSQL gets EF events, shared checkpoints, and CAS leases; SQLite gets SQLite events, file checkpoints, and no-op run leases. Moving a leaf turn to AgentHost does not inherently require moving the database.
  `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1043-1074`
  `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs:19-24`

### Events: authoritative sequence allocation, replay, and notifications

- Normal `RecordNext` submits `Sequence=0` to durable storage; only after the returned sequence is known does it insert local history and signal local waiters. Label this **durable allocation/write-through**, not a best-effort asynchronous mirror.
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:183-234`
- PostgreSQL append uses **ReadCommitted**, obtains `pg_advisory_xact_lock(hashtextextended(runId,0))`, allocates `MAX+1` for automatic sequences, writes, commits, and acknowledges. Lock wait is bounded to two seconds; retry classification is explicit and bounded.
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:224-288`
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:311-368`
- Explicit historical sequence values are supported: identical type/payload is idempotent; a different payload at the same sequence throws. Thus promise **ordered run-local cursors**, not universal gaplessness for all append modes.
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:239-266`
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:370-387`
- Cross-replica live delivery is repeated SQL reads of `Sequence > lastSeen`, ordered ascending; sleep is **250 ms only when empty**. Local task-completion signals and SQLite channels are not cross-replica pub/sub, PostgreSQL `NOTIFY`, or an external broker.
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:145-169`
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:409-419`
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs:88-117`
- Tests independently cover eight PostgreSQL writers allocating contiguous automatic sequences and replaying them. The older cross-instance test uses SQLite underneath EF: useful polling verification, **not PostgreSQL advisory-lock proof**.
  `sabbour/agentweaver:tests/Agentweaver.Tests/PostgresIntegration/RunEventStreamPostgresTests.cs:14-75`
  `sabbour/agentweaver:tests/Agentweaver.Tests/EfRunEventStreamTests.cs:15-50`

### Leases and command claims are different mechanisms

- Run lease acquisition conditionally updates an unowned/expired `runs` row, stamps owner/deadline/heartbeat, increments fencing token and attempt. Renew/release require matching owner/token; ownership checks additionally require unexpired lease.
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-93`
- Run watch leases have five-minute TTL and half-TTL renewal; cleanup releases the matching lease. **Failed renewal logs a warning—it does not itself immediately cancel all work.** Terminal handling and failure finalization check current ownership when an active lease is present. Avoid “every write is fenced” or exactly-once execution promises.
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:36-36`
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:84-112`
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:148-159`
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:593-610`
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:881-895`
- PostgreSQL tests verify one CAS winner and successful takeover with an increased token after expiry.
  `sabbour/agentweaver:tests/Agentweaver.Tests/PostgresIntegration/PostgresStoreTests.cs:124-187`
- **Command `SandboxClaim` lifecycle:** derive a stable claim name when run ID exists, otherwise generate a random name; create/reuse claim → wait for bound pod → execute command. Only a newly created **unscoped** claim is deleted in the per-command `finally`. Run-scoped claims are retained for run cleanup/TTL. This is neither the SQL run lease nor “always destroy the pod after each command.”
  `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:287-305`
  `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:328-361`

### Token telemetry: cost units are not token units

- `agent.turn.usage` includes input/output/total token counts, total nano-AIU, model, duration, and first-token timing.
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1021-1032`
- Despite its name, **`agentweaver.token.usage` is a `Counter<long>` with unit `nano_aiu`**, incremented by `_turnNanoAiu`, and only when that value is positive. Token counts are separate event fields/span attributes/metric dimensions—not the counter’s measurement value.
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:45-48`
  `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1238-1281`
- Query code divides metric sums by `1_000_000_000` for AI credits. Metrics endpoints use stored-data fallback for supported usage sections; run token breakdown falls back when telemetry has no agent data. Do not imply all telemetry charts/traces are recreated from durable events.
  `sabbour/agentweaver:apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs:314-321`
  `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:99-110`
  `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:137-158`

## 2. Smallest useful corrected diagram models

These are **authoring proposals**, grounded in the implementation above—not changes performed.

### `canonical-agent-communication-shared` — retain identity; tighten semantics

**Nodes/groups**
- Team coordination: Coordinator; Agent A child; Agent B child; Project context/state API.
- Execution transport: Orchestration host/RemoteAgentProxy; AgentHost leaf turn.
- A context-compilation label/callout is sufficient; no need for another memory architecture diagram.

**Arrows**
1. Coordinator → A/B: bounded subtask dispatch.
2. A/B → Coordinator: result / assemble-ready handoff.
3. Project state → orchestration-side compilation → A/B: **approved active architectural/scope decisions for children**.
4. A/B → state API: inbox proposals / pending memory writes.
5. Proxy → AgentHost: setup + task over A2A.
6. AgentHost → Proxy: assistant output + structured `RunEvent` values.

**Callouts:** ordinary non-child compilation may include eligible memory/session context; no lateral peer-chat arrow; no pod → database “compile prompt” arrow. Evidence: compiler/orchestrator/API/A2A references above.

### `canonical-agent-communication-a2a` — retain

Keep four participants: **workflow graph → proxy → AgentHost bridge → leaf runtime**.

Sequence:
1. Invoke leaf turn.
2. Proxy → Host: `message:stream`, setup DataContent + task TextContent.
3. Host: apply per-turn context.
4. Host → runtime: execute leaf turn.
5. Runtime → Host → proxy: structured events and assistant content.
6. Proxy → local event pipeline: decoded run events.
7. Proxy → workflow: final result/completion.

Keep graph/gates/checkpoints outside the AgentHost boundary. Put claim/configure in the separate shared configuration reference, not this sequence. Evidence: `RemoteAgentProxy.cs:220-251,303-344`; `A2ATurnBridgeAgent.cs:202-265`.

### `distributed-execution-scaling-fig3` — redesign, including the newly identified caveat

**Minimum nodes:** Clients; Web/API replicas; Orchestration/worker replicas; AgentHost pods; PostgreSQL.

**Arrows:** clients ↔ web for HTTP/SSE; web ↔ SQL for requests/state/event cursor reads; orchestration ↔ SQL for ownership/checkpoints/events; orchestration ↔ AgentHost for A2A turn setup/output.

**Labels**
- Worker autoscaling: **“Shipped HPA: CPU 70% / memory 80%; 2–3 replicas.”**
- Optional annotation: **“Backlog/KEDA scaling: proposed alternative.”**
- **Do not label web tier “never owns runs.”** Add: “Logical role separation; background pickup is independently enabled and not exclusively gated by App:Role.”

Keep command claim lifecycle out of this picture; it would confuse SQL ownership with Kubernetes resource allocation.

### `distributed-execution-scaling-fig4` — retain; modernize write-path labels

Keep worker, local stream entry, EF stream/shared SQL, two web replicas, watcher.

1. Worker → local entry: `RecordNext`.
2. Entry → EF: append with `Sequence=0`.
3. EF → SQL: ReadCommitted + per-run advisory lock; MAX+1; insert; commit.
4. EF → entry: assigned sequence.
5. Entry: insert local history; notify local waiters; acknowledge.
6. Web A → SQL: ordered cursor polling; 250-ms wait only when empty.
7. Web A → browser: SSE with sequence IDs.
8. Browser reconnects → Web B: `Last-Event-ID`.
9. Web B → SQL → browser: replay/tail after cursor.

Do not add SQL push notifications. Keep the SQLite channel branch as prose, not a competing production transport.

### `distributed-execution-scaling-fig5` — retain bounded CAS/reclaim sequence

Three participants remain enough: Worker A, Worker B, run row.

1. A conditional claim → one affected row + token `t`.
2. B conditional claim → zero affected rows.
3. A renews using owner/token.
4. A crashes; lease expires.
5. B claims → token `t+1`.
6. Optional short callout: stale A renew/release fails; terminal ownership checks reject stale ownership.

Do not append an invented “every data write rejected atomically” guarantee.

## 3. Specific prose corrections

| Assigned document | Proposed replacement/qualification |
|---|---|
| `agent-communication.md` | Replace “Before every turn … four layers” with **“Orchestration builds eligible project context when preparing the agent. Coordinator children use decisions-only compilation; charter, skills, and capabilities are composed separately.”** Replace “always reaches” with “is selected for injection,” since missing project/context failures can omit it. |
| `agent-communication.md` | Replace “A2A carries … chat/output stream and nothing more” with **“A2A carries turn setup, assistant output, and structured run events; orchestration/checkpoint state remains in the orchestration host.”** Qualify all-writes-via-inbox prose to distinguish pending direct memory records. |
| `a2a-bridge.md` | Replace “durable events do not cross this boundary” with **“Event persistence does not move into AgentHost; structured run events cross A2A and are persisted through the caller’s event pipeline.”** Distinguish healthz standby/ready; qualify optional token enforcement. Current conflicting text: `sabbour/agentweaver:docs/deep-dive/a2a-bridge.md:5-9,27-39`. |
| `distributed-execution-scaling.md` | Change active backlog-scaling assertion to shipped CPU/memory HPA plus proposed KEDA. Replace “web pods … never own runs” with logical deployment responsibility plus independently enabled background-service caveat. Current assertions: `sabbour/agentweaver:docs/deep-dive/distributed-execution-scaling.md:62-74`. |
| `distributed-execution-scaling.md` | Replace **serializable transaction** with **ReadCommitted + per-run advisory transaction lock**; replace generic “mirror” with durable sequence allocation/write-through. Narrow fencing to implemented lease/terminal checks. Do not call workers stateless merely because SDK state moved to pods: their graph remains in process. Current text: `sabbour/agentweaver:docs/deep-dive/distributed-execution-scaling.md:84-105,109-127,135-143`. |
| `events-observability.md` | Retain short page; add reference to fig4. Specify **run-local database-assigned cursors**, PostgreSQL polling, and local-only signals/channels. Distinguish operational event persistence from Azure Monitor export. Azure registration: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1129-1132`; exporter: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/AzureMonitorBootstrap.cs:15-25`. |
| `token-usage-monitoring.md` | Explicitly state **“The metric named agentweaver.token.usage measures nano-AIU cost, not token count.”** Keep formula/table. Say runtime emits usage into the durable event pipeline rather than implying the runtime/AgentHost itself owns durable storage. Keep fallback limitations. |

## 4. All assigned concept dispositions

Audit concept IDs below omit the common `deep-dive-execution-` prefix.

| Concept | Confirmed disposition / plan delta |
|---|---|
| `a2a-leaf-transport` | Reuse `canonical-agent-communication-a2a`; correct structured-event prose. |
| `a2a-warm-configure` | Reuse `sandbox-pod-execution-fig6`; health/readiness/token qualifications supplied above. Shared owner retains configuration diagram scope. |
| `shared-communication` | Retain canonical identity; add child-context and pending-memory distinctions. |
| `blackboard-authority` | Retain prose; clarify approved decisions, compiled-memory exception, and conditional injection. |
| `coordinator-handoff` | Reuse `canonical-agent-communication-handoff`; no local duplicate. Preserve owner’s confirmation/assemble-ready boundaries. |
| `a2a-sequence` | Retain canonical A2A sequence. |
| `communication-rollup` | **Merge `agent-communication-fig5` into shared canonical.** Remove recap image/provenance; retain recap bullets/reference. |
| `pod-migration-phases` | **Merge `distributed-execution-scaling-fig1` into `canonical-sandbox-pod-evolution`.** Preserve P1/P2/P3 prose and independent database selection. |
| `web-worker-topology` | Redesign fig3; HPA correction **plus role-enforcement qualification**. |
| `run-leases` | Retain fig5; narrow fencing guarantees. |
| `cross-replica-events` | Retain fig4; authoritative sequence/write-through labels and corrected SQL isolation prose. |
| `event-stream-reference` | Reuse fig4 by reference; no new local event graphic. |
| `observability-surfaces` | Retain prose. |
| `token-telemetry-flow` | Retain prose; explicit cost-counter units. Do not restore retired projection diagram. |
| `token-query-units` | Retain endpoint table/formula; no new diagram. |

Concept inventory evidence:
`sabbour/agentweaver:.github/skills/docs-diagram-audit/reports/deep-dive-execution.json:9-89,183-251,641-660`

## Gaps and boundaries

- Both merges are semantically justified; replacement **visual/publication quality** remains the owning author’s review, not approved by this research.
- Tests were read, not run. PostgreSQL concurrency/lease findings have implementation and test coverage, not a fresh execution result.
- No live deployment inspection was performed. HPA claims mean **checked-in manifest behavior**, not proof of current cluster configuration.
- The strict web/worker ownership claim is the significant newly discovered planning issue: the checked code permits background pickup independently of role. Treat exclusive worker ownership as a deployment-policy/intended-topology statement unless separately demonstrated—not as established fact.
- Cross-owner configuration/handoff targets were referenced without auditing sandbox internal-tool, preview, or infrastructure designs.
