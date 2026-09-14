## Summary

The scaling redesign should depict **the checked-in deployment**, not the migration proposal: `agentweaver-api` and `agentweaver-worker` both have two replicas, PostgreSQL-backed state, RollingUpdate, and the shared Azure Files workspace; the worker has a **CPU/memory HPA with a 2–3 replica range**. Implementation children execute in a **verified detached pod-local checkout**, push a temporary Git ref into the shared repository, and return an A2A writeback descriptor that the orchestration side validates before fast-forwarding the authoritative child branch. The diagram must distinguish that publication path from both direct editing of Azure Files and pushing to GitHub. Sources: `sabbour/agentweaver:k8s/base/api-deployment.yaml:10-18`, `sabbour/agentweaver:k8s/base/worker-deployment.yaml:10-18`, `sabbour/agentweaver:k8s/base/worker-hpa.yaml:58-82`, `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-135`, `sabbour/agentweaver:apps/Agentweaver.Api/Git/WorktreeManager.cs:598-672`.

The old sandbox-pods preview consumer should point to `sandbox-browser-preview-fig1`; **Gateway-direct HTTPS is primary**, while disabled-preview `kubectl` forwarding binds only API-host loopback. Current retention is **`PreviewActive` / `Previewable` reconciliation**, including reversal of TTL and eviction protections. Importantly, a process with preview creation disabled still honors an existing cluster-visible preview during cleanup. Sources: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:16-22`, `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/PortForwardService.cs:99-115`, `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:788-850`, `sabbour/agentweaver:tests/Agentweaver.Tests/SandboxPreviewServiceClusterTests.cs:492-515`.

**Scope:** read-only local research in absolute root
`C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`.
All citations below identify files beneath that root. No assets were changed, no agents launched, no external content fetched, and no tests executed. Test findings mean **test source inspected**, not a passing test run.

---

# 1. Scaling diagram: complete redesign model

## 1.1 Boundary model

| Boundary | What belongs inside | Important restriction |
|---|---|---|
| **Application/control-plane workloads in AKS** | API Deployment, worker Deployment; optionally a small Kubernetes lifecycle-control node | Do not present the API as merely a stateless enqueue façade with all orchestration removed. Heartbeat, pickup/reconcile, sandbox and preview services are registered on the shared application path. |
| **Per-run Kata sandbox pod** | AgentHost control/A2A process; execution sidecar; pod-local checkout and scratch | Separate the pod boundary from the model-controlled execution process boundary. The template currently runs model-controlled commands in `agentweaver-exec`. |
| **Shared persistent workspace** | Azure Files RWX PVC; repository and authoritative child worktrees; workspace files/artifacts | Shared RWX storage is **not itself a per-run isolation boundary**. |
| **Durable operational state** | Azure Database for PostgreSQL Flexible Server | API/worker persist state; no direct sandbox PostgreSQL edge. |
| **Historical rollback resource** | Retained RWO `agentweaver-data` PVC | Not mounted by the current API/worker deployment; optional grey annotation, not a live storage dependency. |

Evidence:

- Shared heartbeat/pickup registration and execution: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:515-521`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:55-64`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:90-121`.
- Kata and execution sidecar: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:70-72`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:151-160`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:332-371`.
- RWX and isolation qualification: `sabbour/agentweaver:k8s/base/pvc-workspace.yaml:3-7`; `sabbour/agentweaver:k8s/base/pvc-workspace.yaml:34-51`.
- RWO retained but unmounted: `sabbour/agentweaver:k8s/base/pvc-data.yaml:1-11`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:404-409`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:256-260`.

## 1.2 Nodes

| ID | Exact suggested label | Essential? | Evidence |
|---|---|---:|---|
| `api` | **agentweaver-api** / HTTP, auth, SSE, sandbox & preview control / **2 replicas** | Yes | `sabbour/agentweaver:k8s/base/api-deployment.yaml:1-18`; `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:812-840` |
| `worker` | **agentweaver-worker** / run processing & orchestration / **2 replicas; HPA 2–3** | Yes | `sabbour/agentweaver:k8s/base/worker-deployment.yaml:1-18`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:111-140`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:58-82` |
| `postgres` | **Azure PostgreSQL Flexible Server** / operational state, memory, run events, checkpoints & leases | Yes | `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1027-1074`; `sabbour/agentweaver:scripts/azure/steps/17-provision-postgres.mjs:26-40` |
| `files` | **Azure Files — shared RWX workspace** / `/workspace` | Yes | `sabbour/agentweaver:k8s/base/pvc-workspace.yaml:41-51` |
| `shared-repo` | **Shared repository + authoritative child worktree** / `agentweaver/<childRunId>` | Yes; can be nested inside `files` | `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/IRunAgentHostContextResolver.cs:57-98` |
| `agenthost` | **Per-run AgentHost** / configure once; A2A control | Yes | `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:258-287`; `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:35-48` |
| `execution` | **Execution sidecar** / implementation turn, build/test, preview process | Yes, but a compact compartment is enough | `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:151-160`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:206-215` |
| `local-checkout` | **Pod-local detached checkout** / `/local-workspace` / disk-backed `emptyDir` | Yes | `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-148`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:367-371` |
| `claim-control` | **SandboxClaim → warm pod binding** / used pod deleted; pool replenishes | Optional compact control node | `sabbour/agentweaver:docs/reference/sandbox-setup.md:30-37`; `sabbour/agentweaver:k8s/base/sandbox-warmpool-agenthost.yaml:29-34` |
| `hpa` | **Worker HPA** / CPU 70%, memory 80% | Optional separate node; essential label somewhere | `sabbour/agentweaver:k8s/base/worker-hpa.yaml:65-82` |
| `legacy-rwo` | **Retained SQLite rollback PVC — not mounted** | Optional callout outside active paths | `sabbour/agentweaver:k8s/base/api-deployment.yaml:404-409` |

**Do not add a live `agentweaver-web` Deployment or a live KEDA scaler.** The API’s real resource name is `agentweaver-api`; the active worker scaler is HPA. The API HPA example at the bottom of `worker-hpa.yaml` is commented guidance, not an active resource. Evidence: `sabbour/agentweaver:k8s/base/api-deployment.yaml:1-7`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:50-82`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:116-142`.

## 1.3 Edges

| From → To | Suggested label | Meaning/evidence |
|---|---|---|
| API ↔ PostgreSQL | **durable state / events / checkpoints** | PostgreSQL stores and shared checkpoint registration: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1027-1074`. |
| Worker ↔ PostgreSQL | **claim / renew / release + durable orchestration** | Worker PostgreSQL configuration: `sabbour/agentweaver:k8s/base/worker-deployment.yaml:146-160`; lease CAS: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-91`. |
| API ↔ Azure Files | **workspace access** | `sabbour/agentweaver:k8s/base/api-deployment.yaml:350-354`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:406-409`. |
| Worker ↔ Azure Files | **repository / authoritative worktrees** | `sabbour/agentweaver:k8s/base/worker-deployment.yaml:118-124`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:256-260`. |
| Worker → AgentHost | **configure + A2A turns :8088** | `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:35-48`. |
| API → AgentHost | **sandbox / preview / approval control :8088** | Same allowed ingress plus retained API preview orchestration: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:41-48`; `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:812-840`. |
| AgentHost → API | **run-scoped tool callbacks :8080** | Explicit callback ingress and egress: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:50-73`; `sabbour/agentweaver:k8s/base/networkpolicy-agenthost-egress.yaml:63-74`. |
| Shared repository → local checkout | **fetch exact source ref; verify commit + tree** | `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-135`. |
| Local checkout → shared repository | **push temporary writeback ref — no force** | `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:328-348`. |
| AgentHost → orchestration | **A2A prepared-writeback receipt** | `sabbour/agentweaver:apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-353`; consumption: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:183-210`. |
| Orchestration → authoritative child worktree | **validate receipt; fast-forward; verify result** | `sabbour/agentweaver:apps/Agentweaver.Api/Git/WorktreeManager.cs:521-578`; `sabbour/agentweaver:apps/Agentweaver.Api/Git/WorktreeManager.cs:598-672`. |
| Sandbox ⇥ PostgreSQL | **No direct database access** | Draw a denied edge/boundary annotation, **not an `x` node**. API/worker PostgreSQL policies permit TCP 5432; sandbox egress allows explicit services/DNS/public HTTPS, not database access: `sabbour/agentweaver:k8s/base/networkpolicy-postgres-egress.yaml:24-36`; `sabbour/agentweaver:k8s/base/networkpolicy-worker.yaml:132-158`; `sabbour/agentweaver:k8s/base/networkpolicy-agenthost-egress.yaml:48-107`. |

Avoid one unlabeled bidirectional “worktrees” line between sandboxes and Azure Files. It would obscure the most important distinction: **shared persistence versus local execution versus validated publication**.

## 1.4 Verified Git writeback: exact semantics

1. Local-workspace implementation is selected only when enabled **and the run is a child with `ParentRunId` and `SubtaskId`**. The branch must match the authoritative `agentweaver/<runId>` branch. Thus “every possible agent turn always runs LocalWritable” is too broad.
   `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/IRunAgentHostContextResolver.cs:57-98`.

2. AgentHost initializes a separate repository, sets `origin` to **`SourceRepositoryPath` on shared storage**, shallow-fetches the source ref, checks both SHA and tree hash, and checks out detached. This is not a GitHub network clone in the writeback protocol.
   `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-135`.

3. It uses a platform index, stages results, constructs a single-parent commit from the immutable base, and pushes `resultCommit:refs/agentweaver/writeback/...` to that shared origin with `--no-force`. An unchanged tree returns a no-change descriptor with no temporary ref.
   `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:240-265`; `sabbour/agentweaver:apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:306-348`; `sabbour/agentweaver:packages/Agentweaver.Domain/PodLocalExecutionWorkspace.cs:48-50`.

4. The orchestration-side apply checks run/branch identity, object IDs, clean authoritative worktree, unchanged base or already-applied result, temporary-ref namespace, result commit, single parent, and result tree. It then uses `git merge --ff-only` and rechecks HEAD/tree.
   `sabbour/agentweaver:apps/Agentweaver.Api/Git/WorktreeManager.cs:521-672`.

5. The integration test specifically asserts that **the authoritative branch has not moved after pod publication of the temporary ref**, then verifies the apply moves it, excludes agent scratch notes, and tolerates repeated apply.
   `sabbour/agentweaver:tests/Agentweaver.Tests/AgentHost/ImplementationWritebackTests.cs:119-168`.

**Diagram-level essentials:** verified fetch, local edits, temporary ref publication, receipt, verified authoritative apply.
**Keep in prose/tests:** staging index internals, nested repository flattening, UUID/hash naming, individual typed error strings.

## 1.5 Scaling/lease callouts

- **Run lease:** five-minute TTL, renewal at half-TTL; owner/fencing-token guarded renew and release.
  `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:36-36`; `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:85-110`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-91`.

- **Coordinator ownership already exists:** `WorkPlan.CoordinatorPodId` plus `UpdatedAt`, CAS acquisition, heartbeat renewal, and cancellation when a peer owns the plan. Do not imply that the proposed per-subtask schema is how the shipped coordinator lease works.
  `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorReconciler.cs:580-620`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:607-652`.

- **Do not assert universal write fencing.** The examined run lease store fences lease operations, while the run-watch renewal loop logs failed renewal; that is not proof that every subsequent domain write checks the token.
  `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:54-91`; `sabbour/agentweaver:apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:100-110`.

- **Disruption:** worker PDB is `minAvailable: 1`, not the suggested `maxUnavailable: 1`; termination grace is 120 seconds and worker preStop sleeps 30 seconds. **120 seconds is shorter than the five-minute lease TTL**—do not repeat the manifest comment or document claim that it exceeds TTL.
  `sabbour/agentweaver:k8s/base/worker-hpa.yaml:97-110`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:33-36`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:243-249`.

---

# 2. Preview retirement: verified replacement model

## Primary Gateway path

**Browser → shared Preview Gateway → HTTPRoute → ClusterIP Service → sandbox forwarder → app**

- API handles authorization, approval, pod discovery/labels, Service/HTTPRoute creation, and lifecycle.
- The browser data path does **not** traverse the API or API-host loopback.
- The Service is actually created per preview token, with a **run-scoped pod selector**, `port: 80`, and `targetPort` set to the requested/public-forwarder port. “Per-run Service” is an oversimplification when multiple previews exist.
- API does not probe the protected pod app port. Publication checks the **generated public HTTPS Gateway URL**.
- Gateway preview-port ingress is limited to gateway-labelled pods and TCP 3000–9000.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:34-60`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:237-308`; `sabbour/agentweaver:k8s/base/networkpolicy-sandbox.yaml:50-60`.

## Disabled-preview fallback

| Property | Verified behavior |
|---|---|
| Selection | Only when `previewService.Enabled` is false, not automatic fallback after a failed Gateway publication. |
| Target | Kubernetes pod obtained from this process’s `PodNameRegistry`; local execution without a bound Kubernetes pod has nothing to forward. |
| Command | `kubectl port-forward --address 127.0.0.1 pod/{podName} :{targetPort} -n {namespace}`. |
| Returned address | `local_port` on the **API host**, not the remote browser’s localhost; no public `preview_url`. |
| Port range | 1–65535; Gateway path separately enforces configured allowed range. |
| Caps | Defaults 3 per run and 20 per process/global service instance. |
| Durability | Process-local sessions, no route-annotation persistence and no session TTL. |
| End | Explicit stop, process exit, service disposal, or run/pod cleanup; not a persistent Gateway preview. |

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:16-22`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/PortForwardService.cs:66-115`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/PortForwardService.cs:127-175`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/PortForwardService.cs:208-223`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/PortForwardService.cs:329-377`.

## Permission model

| Operation | Required access |
|---|---|
| Manual start, retry, keepalive, stop | **Project Contributor or Owner** |
| List | **Project Viewer or higher** |
| Agent-start callback | Contributor/Owner **or explicitly allowed trusted internal service** |
| Legacy run without ProjectId | Submitting-principal ownership, except the explicitly allowed internal callback |
| Keepalive / Gateway stop | Additionally verify HTTPRoute token belongs to the requested run |

Do not describe ordinary endpoints as owner-only. Also do not describe the helper as cryptographically verifying “this callback belongs only to this run”: it admits the trusted internal-service identity when the endpoint explicitly opts in; the native tool’s run binding is a separate boundary.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:38-44`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:85-94`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:232-232`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:320-329`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:367-404`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:116-154`.

Tests reject internal service on ordinary preview start/retry but allow the explicit agent callback through authorization: `sabbour/agentweaver:tests/Agentweaver.Tests/Auth/ProjectRunAuthorizationTests.cs:389-441`.

## Lifecycle model for the shared preview owners

| State | Evidence | Side effects |
|---|---|---|
| **PreviewActive** | At least one HTTPRoute for the run has both idle and hard-max deadlines in the future | Patch candidate backing claims to `LifetimeMinutes * 60 + 600`; set backing pod `safe-to-evict=false`; defer normal release/reaping |
| **Previewable** | No qualifying route, missing client/run identity, or non-cancellation lookup failure | Restore configured normal claim TTL; set `safe-to-evict=true`; normal release/reaping can proceed |

Additional precision:

- State lookup **does not require local preview creation to be enabled**.
- Stop deletes the HTTPRoute and Service, then reconciles; another live route keeps the run active.
- The active-claim TTL formula currently uses **service options’ `LifetimeMinutes`**, whereas route expiration uses effective project settings. Do not claim these are always derived from exactly the same setting.
- A 24-hour approval timeout and 24-hour published lifetime are separate controls.
- Approval timeout returns **408**, retains the private supervised process for retry, and does not itself publish a URL.
- Agent/deterministic publication is terminal-run guarded. **Manual operator preview may start after run completion** if a backing pod remains.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:272-273`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:461-474`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:788-869`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:100-139`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:454-460`; `sabbour/agentweaver:tests/Agentweaver.Tests/Preview/PreviewApprovalRetryEndpointsTests.cs:186-200`; `sabbour/agentweaver:tests/Agentweaver.Tests/Preview/PreviewApprovalRetryEndpointsTests.cs:587-610`.

---

# 3. Exact correction instructions for the six owned documents

These are replacement-ready excerpts. Where an entire obsolete section should go, I identify its exact opening/heading and replacement text rather than preserving contradictory fragments.

## A. `docs/reference/scaling-data-layer.md`

### A1. Historical migration text presented as unfinished implementation

**Old, §2:**

> “It is acceptable to ship Postgres for the EF set first (a config flip), then port the raw stores entity-by-entity into the same context.”

**Replace:**

> “The PostgreSQL path is implemented: operational stores, memory/orchestration entities, durable run events and workflow checkpoints use the shared PostgreSQL database. The SQLite idiom table below is migration/background guidance, not an outstanding staged-port plan. Changing providers does not transfer existing data.”

Also add `EfProjectStore` to §1a’s list of EF operational implementations.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:390-397`; `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:447-454`; `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:1027-1065`.

### A2. Coordinator leasing wrongly described as absent

**Old, line 82:**

> “Extending the same columns to `WorkPlans` and `Subtasks` for coordinator-level leasing is a **documented design target, not yet implemented**.”

**Replace:**

> “The columns below describe run leases. Coordinator plan ownership is also implemented, using `WorkPlan.CoordinatorPodId` and `UpdatedAt` rather than these same run-lease columns: CAS acquisition prevents takeover of a fresh owner, heartbeat renewal keeps ownership live, and loss to a peer fences the dispatch loop. The separate per-subtask fencing and dispatch-idempotency schema below remains design guidance.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorReconciler.cs:580-620`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:607-652`; test `sabbour/agentweaver:tests/Agentweaver.Tests/Coordinator/CoordinatorLeaseHeartbeatTests.cs:56-120`.

### A3. Overbroad subtask hazard and executable-looking proposal

**Old, line 106:**

> “The known gap is **subtask dispatch**, which is still a read-modify-write with no owner or state guard — two replicas observing the same `pending` subtask could both dispatch…”

**Replace:**

> “Do not confuse the shipped plan-level coordinator ownership protocol with a per-subtask dispatch-idempotency transaction. The following SQL illustrates a proposed stronger per-subtask contract; it is not the current schema, a migration, or an instruction to run against production.”

**Old, immediately after SQL:**

> “Until that conversion lands, every blind subtask-status writer remains a double-dispatch hazard under multiple replicas and must be audited before scaling workers past one.”

**Replace:**

> “The checked-in deployment already runs multiple replicas. Its current coordinator ownership and heartbeat protections are described above; the proposed per-subtask transaction must be evaluated separately rather than treating plan ownership as absent.”

Evidence: plan implementation above; deployed replicas `sabbour/agentweaver:k8s/base/worker-deployment.yaml:10-18`.

### A4. Fencing-token description

**Old, line 91:**

> “Workers present it on writes; a stale token is rejected…”

**Replace:**

> “Monotonic acquisition token. The lease store requires owner ID and fencing token for renewal and release, and exposes an ownership check; consumers must explicitly use these guards where required.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:54-91`.

### A5. Event allocation and duplicate behavior

**Old, line 127:**

> “Caller sequences are idempotent on duplicate; auto sequences use `MAX(Sequence)+1` under a serializable transaction and retry update conflicts up to three times.”

**Replace:**

> “PostgreSQL appends use a per-run advisory transaction lock and allocate `MAX(Sequence)+1` inside a ReadCommitted transaction. Writes have at most four attempts for supported transient/conflict failures. An explicit sequence is idempotent only when the existing event matches; a different event at that sequence is rejected.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:29-37`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:232-299`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:311-345`; PostgreSQL tests `sabbour/agentweaver:tests/Agentweaver.Tests/PostgresIntegration/RunEventStreamPostgresTests.cs:84-134`.

**Old, operational notes:**

> “Terminal events stop a subscription…”

**Replace:**

> “A subscriber yields the full loaded replay batch before closing on a terminal event. This preserves persisted diagnostics following a terminal entry; retryable `coordinator.assembly_blocked` does not itself close the stream.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:143-179`; `sabbour/agentweaver:tests/Agentweaver.Tests/EfRunEventStreamTests.cs:74-154`.

Add shared canonical link below `## 4. Run-event stream`:

> `[Durable run-event architecture](../diagrams/canonical-durable-event-stream.png) — [implementation deep dive](../run-event-stream.md).`

### A6. Replace §5’s proposed role/scaling paragraphs

**Old excerpts:**

> “Does **not** claim runs or run the orchestration graph…”

> “the web tier may drop sandbox RBAC…”

> “Autoscales on **run/queue depth**, not CPU.”

**Replace those role bullets with:**

> “The checked-in deployments are `agentweaver-api` and `agentweaver-worker`, using the same application image. Both start at two replicas with RollingUpdate. The worker sets `App:Role=worker`; the API keeps public HTTP/auth/SSE surfaces and sandbox/preview control. Shared heartbeat services still perform pickup and reconciliation, so this is not a fully isolated ‘API only enqueues, worker alone orchestrates’ contract.”

> “The worker currently uses an HPA with a 2–3 replica range, CPU target 70%, and memory target 80%. Queue-depth KEDA scaling and an API HPA are future guidance, not active resources in these manifests.”

Evidence: `sabbour/agentweaver:k8s/base/api-deployment.yaml:10-18`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:111-140`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:58-82`; `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:515-521`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:90-121`.

### A7. Disruption and volumes

**Old:**

> “workers prefer `maxUnavailable: 1`…”

> “This needs a `preStop` hook / `terminationGracePeriodSeconds` tuned above the lease TTL.”

**Replace:**

> “The worker PDB currently uses `minAvailable: 1`. The worker has a 30-second preStop delay and a 120-second termination grace period. The run lease TTL is five minutes, so the configured grace does not exceed TTL; orderly shutdown and lease-expiry recovery are distinct mechanisms.”

**Old:**

> “Once Postgres is the store, drop that PVC and its mounts, remove the data-path/HOME env…”

**Replace:**

> “The current API and worker no longer mount the SQLite data PVC. The `agentweaver-data` RWO Azure Disk claim is retained as a rollback safety resource, not an active PostgreSQL dependency. Both deployments retain the RWX Azure Files workspace at `/workspace`; the worker’s HOME remains `/workspace/.home`. Implementation children execute in pod-local scratch and publish verified Git writeback to authoritative worktrees on the shared volume.”

Evidence: `sabbour/agentweaver:k8s/base/worker-deployment.yaml:107-124`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:243-260`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:404-409`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:97-110`.

### A8. Wrong callback/database topology

**Old, line 170:**

> “sandbox pods talk to the worker tier, never directly to Postgres. All run-state reads and writes flow through the worker's leasing/orchestration path.”

**Replace:**

> “Sandbox pods do not connect directly to PostgreSQL. API and worker processes mediate durable state. Workers and the API can call AgentHost control/A2A endpoints, and AgentHost tools call the API’s run-scoped callback endpoints; the API also retains preview and sandbox lifecycle responsibilities.”

Evidence: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost.yaml:35-73`; `sabbour/agentweaver:k8s/base/networkpolicy-agenthost-egress.yaml:63-74`.

### A9. Provisioning and rollback must be labelled guidance

**Old, line 178:**

> “The app exchanges its federated service-account token for an Entra access token … so no DB password ever lives in Key Vault or a pod env var.”

**Replace:**

> “Passwordless Entra database authentication is a migration recommendation, not the current deployment contract. The provisioning script currently generates an administrator password and the API/worker consume the `agentweaver-postgres` Secret’s connection string. Introducing passwordless authentication requires explicit provider/token-refresh and database-role work.”

Evidence: `sabbour/agentweaver:scripts/azure/steps/17-provision-postgres.mjs:402-424`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:338-349`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:149-160`.

**Old:**

> “Generate a **separate Postgres migrations set**…”

**Replace:**

> “The PostgreSQL migrations assembly and init-container bundle path already exist. API and worker init containers invoke `efbundle --postgres-migrations`; treat migration sequencing and backfill as deployment operations, not work implied by changing the provider flag.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:891-895`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:45-76`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:42-76`.

**Old, provider flag table:**

> “Flip to `postgres` to cut over; flip back to `sqlite` to roll back.”

**Replace:**

> “Selects the persistence provider. A provider change does not copy state. SQLite rollback requires an explicit data/restore plan and single-writer deployment; it does not preserve PostgreSQL run leasing.”

**Old, §8 opening:**

> “The phases map onto a flag-reversible AKS rollout.”

**Replace:**

> “The following sequence is historical migration guidance, not a checklist of unshipped features or a claim that every phase is reversible by configuration alone. Current manifests already include PostgreSQL, two API and worker replicas, RollingUpdate, and worker HPA. Preserve or deliberately discard state through an explicit cutover/restore plan.”

For rollout item 5, remove:

> “web falls back to the in-process path behind the role flag.”

Replace with:

> “Role selection and `Sandbox:AgentExecutionMode` are separate controls; scaling workers to zero is not by itself a verified rollback of execution topology.”

### A10. Links and diagram consumer

- **Exact bad anchor:**
  `../deep-dive/agent-framework.md#checkpointing-durable-resume`
  → `../deep-dive/agent-framework.md#checkpointing--durable-resume`.
  Heading is `## Checkpointing & durable resume`: `sabbour/agentweaver:docs/deep-dive/agent-framework.md:66-66`.

- **Missing path:**
  `[AKS architecture](../architecture-aks.md)`
  → remove this redundant item; keep
  `[Infrastructure & deployment deep dive](../deep-dive/infra-deployment.md)` and `[AKS deployment guide](../guide/deployment-aks.md)`.
  Stable destination scope: `sabbour/agentweaver:docs/deep-dive/infra-deployment.md:1-15`.

- Keep `../diagrams/reference-scaling-data-layer-fig1.png`.

- Replace existing alt text containing `agentweaver-web` and `x` with:

  > “API and worker replicas share PostgreSQL and Azure Files; per-run AgentHost sandboxes execute in pod-local checkouts and publish verified Git writeback without direct database access.”

- Remove legacy “Edit the JSON” provenance when authoring establishes the new canonical source. **Do not claim a `.drawio` file already exists**; only PNG/hash/JSON were present for the inspected IDs.

---

## B. `docs/reference/sandbox-pods.md`

### B1. Warm-pool release and lifetime

**Old, line 19:**

> “**releases** the pod back to the warm pool”

**Replace:**

> “releases the claim, deleting the used pod so the warm pool can replenish capacity; an active preview can defer release”

**Old, line 59:**

> “pods never persist past the run.”

**Replace:**

> “A pod can outlive execution while a live preview retains it. Normal release and orphan cleanup resume after the preview no longer supplies positive durable retention evidence.”

Evidence: `sabbour/agentweaver:docs/reference/sandbox-setup.md:30-37`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:957-986`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/AgentHostReaperService.cs:90-113`.

### B2. Stale readiness context

**Old, readiness row:**

> “with run/user/token/KV secret context plus the workspace descriptor”

**Replace:**

> “with run identity, a live snapshot-bound Copilot capability or BYOK configuration, separately supplied repository/turn/preview credentials as applicable, and the execution-workspace descriptor”

Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:258-287`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/IRunAgentHostContextResolver.cs:87-98`.

### B3. Pod-attribution fallback

**Old, lines 149–154:**

> “the global fallback”

> “The frontend resolves `node.executionPodName ?? globalPodName`…”

**Replace the fallback explanation with:**

> “`GET /api/system/runtime` reports the host/API pod. This is distinct from a coordinator graph node’s execution attribution. Coordinator child and workflow nodes use topology `executionPodName` or null; an unbound child must not be labelled as executing on the API pod.”

Replace table meanings:

- `podName`: “API/host pod identity; not a fallback execution attribution for coordinator child nodes.”
- `executionPodName`: “Bound execution pod for this run/node, or null when no authoritative binding is available.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorGraphDescriptor.cs:189-195`; `sabbour/agentweaver:apps/web/src/pages/CoordinatorRunPage.tsx:3076-3076`; `sabbour/agentweaver:apps/web/src/pages/CoordinatorRunPage.tsx:3100-3100`.

### B4. Replace historical preview section

Replace the block starting:

> “A **preview port-forward** exposes a port … back through the API…”

through the old diagram/provenance with:

> “With `Sandbox:Preview:Enabled=true`, the preview API creates a Gateway-direct HTTPS route to the run’s sandbox and returns `preview_url` and `keepalive_url`. Browser traffic bypasses the API. See the [sandbox browser preview reference](./sandbox-browser-preview.md) for permissions, DTOs, approval, publication, keepalive and stop semantics.”

> “When preview creation is disabled, the same operator start route uses the legacy `kubectl port-forward` implementation. This still requires a bound Kubernetes sandbox pod. Its returned `local_port` binds only to loopback on the API host and is not a URL reachable at a remote user’s `127.0.0.1`.”

Then retain the compact fallback table from §2 above.

**Consumer replacement:**

```markdown
![Gateway-direct browser preview with API control, HTTPRoute, Service, sandbox forwarder, and app](../diagrams/sandbox-browser-preview-fig1.png)
```

Remove the old `reference-sandbox-pods-fig1.json` generated-provenance comment. Preserve the section heading if avoiding inbound-anchor churn.

**Bad anchor:**

`./sandbox-setup.md#kubernetes-in-cluster`
→ `./sandbox-setup.md#agenthost-pod-behavior`.

### B5. Central broker contradiction

**Old, line 243:**

> “There is **no `CapabilityTokenService`** and no central token broker.”

**Replace:**

> “The API’s `GitHubCapabilityBroker` fences immutable purpose-bound snapshots before and after credential redemption. AgentHost receives the resulting bounded run capability through `/configure`; it does not redeem user secrets through an ambient Key Vault or filesystem fallback.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:15-31`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:40-75`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:87-112`.

### B6. Additional current-code mismatches found while verifying

These go beyond the audit’s headline corrections but should not survive a “current deployment” rewrite:

- **Old resource row:** `500m`, `1Gi`, `1Gi`; limits `2000m`, `4Gi`, `8Gi`.
  **Replace:** “The base template has an AgentHost container requesting 300m CPU/1Gi memory and an execution sidecar requesting 700m/2Gi; limits are 800m/2Gi and 1200m/4Gi respectively. Each requests 1Gi and limits 4Gi ephemeral storage; shared execution scratch has an 8Gi size limit.”
  Evidence: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:218-227`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:332-371`.

- **Old:** “The pod does not automatically receive Kubernetes API credentials.”
  **Replace:** “The current template enables service-account automount on the pod for its infrastructure requirements, while the model-execution sidecar masks `/var/run/secrets/kubernetes.io/serviceaccount`. Do not describe the whole pod as universally tokenless.”
  Evidence: `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:94-96`; `sabbour/agentweaver:k8s/base/sandbox-template-agenthost.yaml:352-357`.

- **Old egress claim:** “a narrow allowlist … the run’s legitimate git remote(s).”
  **Replace:** “Default-deny policies admit explicit internal API/MCP/DNS paths and public HTTPS with private/link-local exclusions. This is not a per-run Git-host-only egress allowlist; direct PostgreSQL access is not permitted.”
  Evidence: `sabbour/agentweaver:k8s/base/networkpolicy-agenthost-egress.yaml:48-107`.

- **Old:** “flipping … restores in-process execution with no redeploy” / “immediately.”
  **Replace:** “Changing `Sandbox:AgentExecutionMode` selects in-process execution when configuration is applied; for the checked-in Kubernetes environment-variable configuration, apply the deployment change through the normal rollout.”
  Evidence: `sabbour/agentweaver:k8s/base/api-deployment.yaml:141-150`; `sabbour/agentweaver:k8s/base/worker-deployment.yaml:134-145`.

---

## C. `docs/reference/sandbox-setup.md`

### C1. Remove AgentHost Key Vault credential requirement

**Old, line 52:**

```markdown
| Key Vault URI | `https://${KEYVAULT_NAME}.vault.azure.net/` |
```

**Replace:**

```markdown
| Run capability delivery | One-time `/configure` with a live snapshot-bound `copilotCredential` in Copilot mode, or BYOK provider configuration; a repository capability is supplied separately when required. |
```

Add:

> “The sandbox identity is not permission to read user secrets. Key Vault configuration on the API/control-plane side is separate from AgentHost’s run credential delivery.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:258-287`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:78-112`.

### C2. Troubleshooting obsolete field

**Old, line 84:**

> “the run owner's GitHub token is brokered by the API in `/configure` (`gitHubAccessToken`)”

**Replace:**

> “Verify the immutable run capability snapshot is live and has the required purpose, that API-side redemption succeeds, and that `/configure` receives `copilotCredential` in Copilot mode plus the separate `repositoryAccessToken` when needed. BYOK uses its provider configuration. Missing or expired Copilot credentials fail before readiness; sandbox identity federation does not provide a user-secret fallback.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.AgentHost/Program.cs:263-287`; `sabbour/agentweaver:apps/Agentweaver.Api/Auth/GitHubCapabilityBroker.cs:40-75`.

### C3. Lifecycle wording

Keep the existing correct:

> “Releasing the claim deletes the used pod; the warm pool replenishes it.”

Qualify the Components table’s “releases it on completion/suspend” as:

> “releases it on completion/suspend unless preview-aware retention defers cleanup.”

The existing `../guide/deployment-aks.md#running-an-individual-step` target is valid: `sabbour/agentweaver:docs/guide/deployment-aks.md:162-162`.

---

## D. `docs/reference/sandbox-browser-preview.md`

### D1. Ownership wording

Replace:

> “Every call verifies the run exists and the caller owns it”

with:

> “Every call resolves the persisted run and authorizes access from that run’s project. Mutations require Contributor or Owner; listing requires Viewer or higher. Legacy non-project runs use submitting-principal ownership. The agent-start endpoint additionally permits its explicitly trusted internal callback.”

In route rows:

- **“owner-only” manual start/retry** → “Project Contributor/Owner; legacy ownership rules apply.”
- **“owner OR its own agent callback”** → “Project Contributor/Owner or the explicitly permitted trusted internal callback.”
- Add Viewer access to list.
- In the retry paragraph, **“guarded by ownership”** → “guarded by run authorization”.

Keep **project settings are Owner-configured** distinct; do not globally replace settings ownership with Contributor.

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:38-44`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:85-94`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:232-232`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:404-404`; `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:116-154`.

### D2. Conflicting defaults

**Old, line 82:**

> “(30 minutes by default; configurable per project from 1–1440 minutes).”

**Replace:**

> “(1440 minutes / 24 hours by default; configurable per project from 1–1440 minutes).”

**Old, line 102:**

> “defaults migration-safely to 30 minutes.”

**Replace:**

> “defaults to 1440 minutes (24 hours).”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs:57-59`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/SqliteProjectStore.cs:449-451`.

### D3. Status table

**Old 403 row:**

> “…approval was denied / timed out…”

**Replace:**

```markdown
| `403 Forbidden` | Insufficient run/project access, or preview approval was denied. |
| `408 Request Timeout` | Agent-preview approval expired; the response identifies the expired request and whether retry is available. |
```

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:100-139`; `sabbour/agentweaver:tests/Agentweaver.Tests/Preview/PreviewApprovalRetryEndpointsTests.cs:186-200`.

### D4. Obsolete Build & Test prompt explanation

**Old paragraph beginning:**

> “The platform-owned **Build & Test** step can use the same preview surface. Its canned prompt tells the agent to build, run all tests, start the web/service preview server…”

**Replace:**

> “Build & Test evaluates the build and tests only. Coordinator assembly subsequently invokes the platform-owned `PreviewStep` on the retained coordinator sandbox for APPROVED and REQUEST_CHANGES results, but not DECLINED. That step resolves a command, maps the source cwd to the effective execution checkout, starts a supervised process, observes its port, obtains approval and publishes the Gateway URL.”

Evidence: `sabbour/agentweaver:packages/Agentweaver.AgentRuntime/Workflow/BuildTestTurnExecutor.cs:10-16`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1046-1065`; `sabbour/agentweaver:apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs:120-172`.

### D5. Service/lifetime/deployment language

- **“per-run ClusterIP Service”** → “per-preview ClusterIP Service selecting the run’s pod”.
- **KeepAfterRun:** “Retain the preview after the run completes / pod is released” →
  “Do not remove preview routing solely because the run completes; a live preview also defers backing-pod release. Expiry, explicit stop and missing-pod reconciliation still bound cleanup.”
- **“Production value: `6a41…`”** →
  “Set from deployment runtime configuration; do not treat an example managed-zone hostname as the current cluster value.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:237-273`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:963-986`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:326-336`.

Add near the introduction:

> `[Gateway routing diagram](../diagrams/sandbox-browser-preview-fig1.png) — [routing deep dive](../deep-dive/sandbox-browser-preview.md#end-to-end-flow).`

---

## E. `docs/reference/live-preview-provisioning.md`

### E1. Replace obsolete retention method contract, lines 31–47

**Old opening:**

> “Both teardown paths now consult `ISandboxPreviewService.HasActivePreviewAsync(runId)`…”

**Replace that explanation and its method-specific paragraphs with:**

> “Release, orphan reaping and preview activity use `ReconcilePreviewLifecycleAsync(runId)`. The service derives a run-level state from durable HTTPRoute annotations and applies the associated retention changes idempotently.”

```markdown
| State | Durable evidence | Sandbox effects |
|---|---|---|
| `PreviewActive` | At least one route has both idle and maximum expiry in the future. | Extend candidate backing-claim TTLs and set the bound pod `safe-to-evict=false`; release/reaping defer. |
| `Previewable` | No qualifying route, unavailable client/run identity, or lookup failure. | Restore normal claim TTL and `safe-to-evict=true`; normal cleanup can proceed. |
```

> “Cleanup reads cluster preview state even if preview creation is disabled in that process. It does not require the backing pod to exist for the retention decision. Protection patches are best-effort; the application cannot guarantee survival after infrastructure loss.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:788-850`; test `sabbour/agentweaver:tests/Agentweaver.Tests/SandboxPreviewServiceClusterTests.cs:394-515`.

### E2. Replace `RenewBackingClaimTtlAsync` table and prose

**Old, lines 84–95:**

> “`RenewBackingClaimTtlAsync(runId)`…”

> “It is only invoked while a preview is demonstrably active…”

**Replace:**

> “The reconciliation transition patches both supported claim-name candidates using a merge patch, preserving sibling lifecycle fields. `PreviewActive` uses the service-level `LifetimeMinutes * 60 + 600` backstop; `Previewable` restores `Sandbox:Kubernetes:TimeoutSeconds` (600 seconds by default). It also applies or removes the pod’s autoscaler eviction pin. Stop and expiry reconcile after route deletion, so another live route retains the pod and deletion of the final live route releases the protections.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:556-600`; `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:788-869`; default wiring `sabbour/agentweaver:apps/Agentweaver.Api/Program.cs:827-832`.

**Old timeout configuration name:** `Sandbox:TimeoutSeconds`
→ `Sandbox:Kubernetes:TimeoutSeconds`.

### E3. Historical controller explanation

The lengthy “verified against upstream v0.5.3” paragraph is **historical investigation**, not evidence reverified by this local task. If retained, prefix:

> “Historical controller investigation (v0.5.3): the following explains the original TTL-renewal rationale. Reverify against the deployed controller version before relying on these upstream implementation details.”

Do not retain its earlier causal narrative as the current API contract; current local code evidence establishes reconciliation and patches, not the upstream controller’s complete behavior.

### E4. Timeout reason

**Old, line 154:**

> “project timeout (30 minutes by default)”

**Replace:**

> “project timeout (1440 minutes / 24 hours by default)”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Sandbox/Preview/AgentPreviewGate.cs:57-59`; `sabbour/agentweaver:apps/Agentweaver.Api/Infrastructure/SqliteProjectStore.cs:449-451`.

### E5. Canonical link

Add before Runtime contract:

> `[Live-preview sequence](../diagrams/live-preview-provisioning-fig1.png) — [end-to-end deep dive](../deep-dive/live-preview-provisioning.md#end-to-end-flow).`

Keep process/approval/port/credential/retention details here; ask the canonical owner to incorporate the two-state retention model rather than making another local lifecycle image.

---

## F. `docs/reference/cluster-diagnostics.md`

### F1. Remove obsolete response field and schema

**Old JSON, lines 29–34:**

```json
"resource_graph": {
  "nodes": [],
  "edges": [],
  "layers": []
}
```

**Replacement:** remove that member and its preceding comma; the minimal sample can end with:

```json
"warm_pools": [],
"sandbox_claims": []
```

Delete the table row:

> “`resource_graph` | `ClusterTopologyResourceGraphDto` …”

Replace with:

```markdown
| `details` | `TopologyResourceDetailsDto` | Optional bounded metadata for the cluster root. |
```

Delete the section beginning:

> `## Kubernetes resource graph contract`

through its old `traffic/security/scaling/agentweaver` layer/status schema.

Replace with:

> “The relationship graph is returned by the separate `/api/diagnostics/cluster/topology` endpoint, not as `resource_graph` on the runtime snapshot. Its six supported layers are `runtime`, `networking`, `workloads`, `storage`, `autoscaling` and `availability`. Requested layers report `available`, `partial` or `unavailable`; omitted layers report `not_requested`.”

Evidence: `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/SystemDiagnosticsDto.cs:108-130`; `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/KubernetesTopologyService.cs:11-16`; `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/KubernetesTopologyService.cs:65-86`.

Keep existing 100 objects/type, 250 nodes, 500 edges bounded-discovery text. Tests exercise truncation and independent layer degradation: `sabbour/agentweaver:tests/Agentweaver.Tests/Diagnostics/KubernetesTopologyServiceTests.cs:223-260`.

### F2. Additional verified correction: non-AKS 404 claim

**Old, line 7:**

> “Non-AKS deployments return `404 Not Found`.”

**Replace:**

> “The endpoint remains available without a Kubernetes client: Kubernetes-specific checks report unknown/unavailable conditions and inventories can be empty. The topology endpoint returns an unavailable graph/layer result rather than requiring a Kubernetes deployment.”

Remove the status-table claim:

> “`404 Not Found` | Cluster diagnostics are unavailable in this deployment.”

Evidence: endpoints return `Results.Ok` unconditionally for these service results: `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/DiagnosticsEndpoints.cs:64-76`; service constructs a snapshot: `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:254-270`; no-client cases: `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:511-518`; `sabbour/agentweaver:apps/Agentweaver.Api/Diagnostics/KubernetesTopologyService.cs:27-35`.

---

# 4. Stable links and cross-owner handoff

| Consumer | Required stable destination | Notes |
|---|---|---|
| Scaling figure | `../diagrams/reference-scaling-data-layer-fig1.png` | Preserve existing asset path; redraw from this model. |
| Sandbox-pods old preview consumer | `../diagrams/sandbox-browser-preview-fig1.png` | Replace old embed; retain explicit disabled-preview fallback prose. |
| Browser-preview reference | `../diagrams/sandbox-browser-preview-fig1.png` | Reuse, do not edit foreign asset. |
| Browser-preview implementation context | `../deep-dive/sandbox-browser-preview.md#end-to-end-flow` | Current heading/consumer: `sabbour/agentweaver:docs/deep-dive/sandbox-browser-preview.md:18-25`. |
| Live-preview reference | `../diagrams/live-preview-provisioning-fig1.png` | Reuse; owner should absorb lifecycle correction. |
| Live-preview implementation context | `../deep-dive/live-preview-provisioning.md#end-to-end-flow` | Current heading/consumer: `sabbour/agentweaver:docs/deep-dive/live-preview-provisioning.md:7-13`. |
| Scaling event-stream section | `../diagrams/canonical-durable-event-stream.png` and `../run-event-stream.md` | Canonical owner consumer: `sabbour/agentweaver:docs/run-event-stream.md:9-15`. |
| Checkpoint deep link | `../deep-dive/agent-framework.md#checkpointing--durable-resume` | Correct doubled hyphen. |
| Sandbox setup deep link | `./sandbox-setup.md#agenthost-pod-behavior` | Replaces absent `#kubernetes-in-cluster`. |
| Missing AKS architecture link | Use existing `../deep-dive/infra-deployment.md` / `../guide/deployment-aks.md` | No `docs/architecture-aks.md` present. |

**Retirement sequencing recommendation:** update the sandbox-pods consumer and remove its obsolete provenance first; coordinate the canonical Gateway owner; only then retire the old `reference-sandbox-pods-fig1` source/image/hash with the audit’s merge record. No target asset needs modification to make the owned consumer accurate.

---

# 5. Deployment facts versus guidance—final guardrails

**Safe as checked-in deployment facts**

- API and worker each start at two replicas with RollingUpdate.
- Worker HPA 2–3, CPU 70%, memory 80%.
- PostgreSQL connection strings arrive from a Kubernetes Secret.
- Azure Files RWX remains mounted; RWO SQLite PVC remains defined but unmounted.
- Production overlay enables matching AgentHost/server and API/worker client mTLS patches.
- Implementation-child pod-local workspace flag is enabled in both base deployments.

Evidence: `sabbour/agentweaver:k8s/base/api-deployment.yaml:10-18`; `sabbour/agentweaver:k8s/base/worker-hpa.yaml:65-82`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:338-349`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:404-409`; `sabbour/agentweaver:k8s/overlays/production/kustomization.yaml:29-39`; `sabbour/agentweaver:k8s/base/api-deployment.yaml:143-150`.

**Must remain recommendations, conditional configuration, or historical migration**

- KEDA queue-based worker scaling; API HPA.
- Passwordless PostgreSQL authentication.
- Proposed per-subtask dispatch-idempotency table/fencing SQL.
- Provider-flip rollback without state migration.
- Dropping workspace mounts/HOME.
- A pure API-enqueue-only role split.
- Claims that termination grace exceeds lease TTL.
- The actual currently deployed Azure region, DNS zone, HA result, or controller version: repository defaults/patches are not live-cluster verification.

The production overlay is the configured render/apply target, but runtime values are rewritten during deployment; the PostgreSQL script supports both private and public access. Thus label the figure **“checked-in AKS deployment topology”**, not “observed live cluster.” Evidence: `sabbour/agentweaver:k8s/overlays/production/kustomization.yaml:1-10`; `sabbour/agentweaver:scripts/azure/steps/17-provision-postgres.mjs:432-438`.

## Remaining uncertainties

- No live controller inspection: upstream v0.5.3 expiry-reconcile claims were not independently reverified.
- No asset visual inspection or authoring: this is a semantic/evidence model, not a layout review.
- No tests executed. Inspected tests strongly cover writeback, coordinator ownership, event sequencing/replay, preview state reversal, callback authorization, timeout retry, and bounded topology, but do not certify the current checkout passes.
- Do not promote the sandbox’s shared PVC mount to a hard per-run isolation claim; the manifest explicitly distinguishes shared storage from execution containment.
- The broad “all run writes are fenced” and “API never orchestrates” claims are not supported by the examined implementation and should be removed rather than redrawn.
