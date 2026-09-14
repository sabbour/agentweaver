# Execution, capacity, recovery, preview, and operations research

Researcher 2 of the parent's three independent research threads. Scope: the seven assigned experience
pages and this report only. Repository: `.worktrees\drawio-diagram-authoring`, baseline HEAD `f10738018`.
No agents launched, no product/skill/pipeline/inventory/assets edited, no commits. Existing unrelated
work was preserved. The initial checkout already had edits to four owned pages; their current content,
not a reset to HEAD, was the editing baseline.

Read `CONTRIBUTING.md`, both requested pitch/iterate skills, `docs/diagrams/README.md`, the assigned
pages, and all relevant concept/diagram/consumer entries in `plan-experience.json` and `experience.json`.
Implementation, configuration, and tests below are the factual basis. Legacy diagrams were not used as
evidence. No screenshot or diagram PNG was visually inspected by this thread; the audit's placeholder
classification is attributed to that audit, not claimed as a new image inspection.

## Conclusions that must control the drawings

1. **Only leaf turns cross A2A.** Worker graph, gates, checkpoint management, durable event recording,
   and run status remain outside AgentHost. Returned tokens/events go through the worker, never a direct
   pod-to-human connector.
2. **Three different recovery concepts must not be collapsed:** event cursor replay, checkpoint/work-plan
   recovery, and policy-directed redispatch after a failed remote turn. No transparent mid-turn replay.
3. **Code defaults are not deployment defaults.** Code chooses `in-api`; checked-in API/worker manifests
   choose `pod-per-run`. Base AgentHost TLS configuration and production TLS overlay also differ.
4. **Suspension release is conditional and best-effort.** Active previews explicitly defer claim deletion;
   assembly can retain Build/Test resources. Human-review waiting does not imply zero retained compute.
5. **Gateway preview has a real public capability URL and durable expiry.** API creates the route/service;
   browser application traffic crosses Gateway/Service to the pod. Keepalive does not move the original
   hard cap. Approval and lifetime both default to 1440 minutes.
6. **Capacity is Kubernetes-owned.** Historical `PendingCapacity` is not the current scheduling queue.
   Object/storage quota remains enforced; absence of CPU/memory ResourceQuota ceilings is not unlimited
   cluster capacity. Warm-pool target is not the live ready count.
7. **Current scaling is modest and specific.** Worker HPA: 2–3 replicas, CPU 70%, memory 80%.
   KEDA and web HPA are commented proposals/examples. Postgres event relay currently polls; it is not
   LISTEN/NOTIFY.
8. **Operations surfaces have different authority.** Account settings is not a sandbox editor; platform
   provider administration and project policy are separate. Cluster/Observability REST endpoints are
   not dedicated MCP tools.

## Evidence register

Line references are repository-relative and refer to the implementation read in this worktree.
Tests were inspected as source, not executed; assertions below describe what they cover.

### E1 — Leaf transport and event direction

- `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:36-63`: `IWorkflowTurnAgent`
  forwards the leaf `CopilotAIAgent` through `message:stream`; MAF graph, `CheckpointManager`, and
  `RequestPort` stay worker-side; AgentHost has no database/checkpoint-store access.
- `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:58-63`: pod `RunEvent` data parts
  are decoded into a worker channel; recording assigns monotonic arrival-order sequence numbers.
- `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:16-37,68-87,144-167`: durable append,
  shared-table cursor subscription, and 250 ms polling. **No notification node belongs in the current map.**
- `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:28-71`: independent producer/subscriber instances
  observe the same persisted events and ordered sequences.
- `tests/Agentweaver.Tests/PostgresIntegration/RunEventStreamPostgresTests.cs:15,85,115`: concurrent
  append sequencing, identical explicit-sequence idempotency, and conflicting payload rejection.
  These are event-storage guarantees, not exactly-once external tool effects.

### E2 — Remote failure and recovery boundaries

- `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:408-456`: transport exceptions
  become `a2a_transport_failure`; clean EOF without `agent.turn.end` emits/throws retryable
  `agent_host_turn_incomplete` rather than reporting phantom success.
- `tests/Agentweaver.Tests/AgentHost/A2ARoundTripIntegrationTests.cs:399-428`: real proxy/host test
  expects retryable incomplete-turn failure while preserving preceding delta events.
- `tests/Agentweaver.Tests/RemoteAgentProxyDeadlineTests.cs:121-154`: connection reset is retryable
  unless caller cancellation applies; unsupported SDK events are classified for retry.
- `apps/Agentweaver.Api/Runs/WorkflowRestartService.cs:57-90,118-140`: stranded in-progress child
  turns fail as retryable `a2a_transport_interrupted`; roots fail `stranded_in_progress`; coordinator
  parents defer to their persisted-plan recovery; AwaitingReview uses checkpoint recovery, with explicit
  missing-checkpoint handling. A lease reclaim is not itself a model-turn resume.
- `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:418-453,471-502`: narrow recovery of a verified
  successful child turn to assemble-ready; generic stream-without-terminal otherwise fails.
- `tests/Agentweaver.Tests/RunWatchLoopStreamEndRecoveryTests.cs:40,104,144`: successful child-turn
  recovery does not apply without successful output or to a root workflow.
- `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:685-743`: released terminal child
  sessions are not treated as live in-place resumable sessions; fresh dispatch is a deliberate decision.

### E3 — Claim, identity, security configuration, suspend, retention

- `apps/Agentweaver.Api/Sandbox/SandboxAgentOptions.cs:38-60`: `in-api` code default and mTLS option.
- `k8s/base/api-deployment.yaml:143-144`; `k8s/base/worker-deployment.yaml:113-114,136-137`:
  deployed worker role and `pod-per-run` execution selections.
- `k8s/base/configmap-agenthost.yaml:55`; `k8s/overlays/production/patch-agenthost-mtls.yaml:34,50`:
  base PoC has mTLS disabled; production overlay enables HTTPS/mTLS. Do not label every deployment mTLS.
- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:663-691,1234-1265`: ready/bound
  AgentHost is configured for the run through `/configure`; run context/credential delivery precedes
  leaf execution.
- `tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:345,731,993,1024`: configuration
  carries immutable capability credentials, distinct MCP broker token, and run approval options.
- `k8s/base/sandbox-template-agenthost.yaml:72,270-271,343-356`: Kata runtime and separate executor
  container/PID namespace/mount boundary. Draw AgentHost and executor inside one sandbox-pod boundary,
  not as separate public services; a pod label alone does not prove these controls.
- `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394-408,518-536`: release only with lifecycle
  service, pod-per-run, and ReleasePodOnSuspend; failure is logged/swallowed; human waiting pauses
  watchdog time without disabling cancellation.
- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:956-984`: reconciled `PreviewActive`
  defers claim deletion; otherwise release removes claim and registrations/credentials.
- `tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:1187,1208,1229`: active-preview
  retention versus normal deletion and missing-preview-service cleanup.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2114-2123`: automated gate
  request-changes retains Build/Test pod and detached worktree; human change handling follows cleanup.
- `apps/web/src/components/CoordinatorTopologyGraph.tsx:293`; `apps/web/src/components/PodIndicator.tsx:46-61`:
  each node passes its own `executionPodName`; missing name renders nothing. No global API-pod fallback
  should be drawn for an unknown child.
- `tests/Agentweaver.Tests/ExecutionPodNameStoreTests.cs:30`: recorded pod identity can be recovered
  from shared run events when the local cache is empty.

### E4 — Preview control plane, data plane, lifetime, approval

- `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:188-275`: resolve bound run pod,
  apply run selector, create ClusterIP Service port 80 → target port, create Gateway HTTPRoute and
  capability hostname. No API-to-pod direct TCP readiness probe.
- `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:306-310`: registration waits for
  publication through generated HTTPS URL, after applying retention. Opening a URL is not the only
  publication check.
- `apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs:430-462,556-559`: operator versus
  agent/platform run-bound publication distinction; legacy local diagnostic port-forward fallback
  remains when Gateway preview is disabled, but is not the public browser-preview architecture.
- `k8s/base/networkpolicy-sandbox.yaml:19-22,59-60`: Gateway-only preview port range 3000–9000.
- `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewOptions.cs:37-61`: lifetime 1440,
  retention intent, allowed ports, per-run/global limits 3/20.
- `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:272-273,479-528`:
  both `expiresAt` and `maxUntil` initially equal creation + lifetime; keepalive patches expiry,
  touches the retained process, and reasserts retention, not the original max-until.
- `apps/Agentweaver.Api/Sandbox/Preview/PreviewReaper.cs:76-91`: hard-cap/expiry/pod-existence
  cleanup decisions.
- `apps/web/src/pages/ProjectSettingsPage.tsx:435-436,488-489,1394-1426`: project approval and
  lifetime defaults 1440, range 1–1440, lifetime explicitly both expiration and hard cap.
- `tests/Agentweaver.Tests/SandboxPreviewTests.cs:83-96,103-130`: hostname format, port limits,
  and 24-hour default.
- `tests/Agentweaver.Tests/SandboxPreviewServiceClusterTests.cs:40,105,200,361,394-428,519,542,583`:
  durable claim discovery including retained command claims, no direct TCP probe, token/run ownership,
  active claim TTL/eviction protection, keepalive reassertion, and final-route cleanup.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:220-261`: preview attempt after
  approved **or request-changes**, skip declined/unwired, contain internal failures without blocking
  review; caller cancellation remains cancellation.
- `tests/Agentweaver.Tests/Preview/PreviewStepDeclinedSkipTests.cs:19-73`: all verdict branches
  and internal-versus-caller cancellation behavior.
- `tests/Agentweaver.Tests/Preview/PreviewStepTests.cs:567-594`: denial prevents registration and
  stops process; expiry retains healthy process and emits fresh-approval retry context.
- **Additional implementation correction beyond audit wording:** `apps/web/src/pages/CoordinatorRunPage.tsx:3968-3981`
  requires Kubernetes **plus preview state/session**, not “active run,” for the header button. Keepalive
  depends on a session URL, not dialog-open state. `apps/web/src/__tests__/CoordinatorRunPage.test.tsx:1001-1022`
  explicitly tests the lifecycle-state button gate. Both experience consumers now reflect this.

### E5 — Capacity and operator presentation

- `k8s/base/quota.yaml:9-18`: 200 pods, 200 SandboxClaims, four PVCs, 80 GiB storage;
  no namespace CPU/memory quota cap. Container LimitRange and actual scheduling limits still exist.
- `k8s/base/sandbox-warmpool-agenthost.yaml:30-34`: desired AgentHost warm replicas = two.
- `apps/web/src/pages/ClusterPage.tsx:374-450`: Orphaned pods, Pending capacity, Checks healthy,
  optional Warm pool ready; layered Resource topology; health/claim/orphan/pending/warm-pool tables.
  No separate active-pod table or CPU quota bars.
- `apps/web/src/__tests__/ClusterPage.test.tsx:166-200,218,238,248`: absent active-pod section,
  current KPI labels, default Runtime layer, 404 fallback, refresh and opt-in layer coverage.
- `apps/Agentweaver.Api/Diagnostics/DiagnosticsService.cs:57-60,428-451,579-629`:
  required `mcp-api-key` check, five-second guard, timeout = `unknown` / `check timed out`,
  configured AgentHost warm-pool check (not an unconditional check of two different pools).
- `tests/Agentweaver.Tests/Diagnostics/ClusterDiagnosticsServiceTests.cs:18,57,76,94,119`:
  object quota thresholds, current API-key probe, quota without CPU keys, bounded pending-claim reason.
- `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:59-64,193-197`: default
  ten-second interval and reaper every twelve ticks (~two minutes).
- **Remaining product drift, not changed:** `apps/web/src/pages/ClusterPage.tsx:433-435` and
  `apps/web/src/__tests__/ClusterPage.test.tsx:203-215` still call the old capacity table a current
  warm-pool queue / assert that zero means immediate scheduling. Docs explicitly label it historical;
  product correction is outside this assignment.

### E6 — Scaling, leases, current versus planned configuration

- `k8s/base/api-deployment.yaml:13,65-66`; `k8s/base/worker-deployment.yaml:13,113-114,149-150`:
  two initial replicas in each role, shared Postgres; web is the default role
  (`apps/Agentweaver.Api/AppRole.cs:9-19`).
- `k8s/base/worker-hpa.yaml:49-82,97-111`: active HPA 2–3, CPU 70%, memory 80%, PDB
  `minAvailable: 1`. Lines 17–46 are commented KEDA proposals; lines 125–142 are a commented web HPA.
- `k8s/base/worker-hpa.yaml:7-14,41-46`: queue gauge is active-project Ready backlog awaiting pickup,
  not a count of all unleased runs; each replica exports the same global count.
- `apps/Agentweaver.Api/Infrastructure/Ef/PostgresRunLeaseStore.cs:25-51,54-95`: guarded free/expired
  claim advances token and attempt; renew/release match owner+token; ownership check also requires
  unexpired lease.
- `tests/Agentweaver.Tests/PostgresIntegration/PostgresStoreTests.cs:128-152,160-186`: exactly one
  concurrent claim wins; expiry permits a higher-token claim by another owner. These tests need Postgres.
- `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:86-110,156-159,600-610`: watcher claim,
  periodic renewal, finally release, terminal ownership check. Do not draw a universal fencing guard
  over all external writes.
- E2 supplies the distinct restart outcomes; no unconditional “lease expires → same model turn resumes”
  edge is supported.
- `scripts/azure/variables.mjs:52`; `scripts/azure/steps/17-provision-postgres.mjs:35,434`:
  provisioning defaults include ZoneRedundant HA and managed backup configuration. These are configured
  intentions, not a probe of live deployment health.
- Provider change does not copy data; scaling workers to zero does not change web roles.
  The former rollback instructions were therefore replaced with explicit operational prerequisites.

### E7 — Operations authority and navigation

- `apps/web/src/pages/SettingsPage.tsx:219-228,280-287,362`: Account settings contains Authentication,
  AI Access, GitHub connections, MCP clients; Entra role assignments are displayed, not edited.
- `apps/web/src/pages/PlatformSettingsPage.tsx:494,541,700`; `apps/web/src/App.tsx:86-91`:
  admin-gated `/platform-settings`, model providers and add-provider controls.
- `apps/web/src/pages/ProjectSettingsPage.tsx:435-436,1394-1426`; tests at
  `apps/web/src/__tests__/ProjectSettingsPage.test.tsx:605,640,665,700`: project sandbox and preview
  policy UI, complete preview-window save and validation.
- `apps/web/src/App.tsx:103-125`: Dashboard project root; Flow, orchestration, project settings,
  Observability/Traces/Agents, Diagnostics, Heartbeat, Cluster; no standalone Execution destination.
- `apps/Agentweaver.Mcp/Tools/DiagnosticsTools.cs:33-54`: dedicated diagnostics/heartbeat tools
  forward to their REST endpoints. Lines 57–75 provide bounded failed-run diagnostics.
- `apps/Agentweaver.Mcp/Tools/SandboxPolicyTools.cs:12-37`: get policy versus the narrower
  `repository_path` + `shell_enabled` setter. No implied full parity with UI switches.
- `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:186-200,231-244`: atomic
  claim/reservation; Lost/ProjectUnavailable return without launch; winner activates and unattended-confirms
  post-commit.
- `tests/Agentweaver.Tests/Backlog/BacklogClaimReserveTests.cs:32,141,173`: one claim wins, loser
  persists nothing, unavailable project leaves task Ready.
- `apps/web/src/pages/OverviewPage.tsx:365,382,386,389`: current Recent projects, AI usage &
  performance, Activity feed, Needs attention. A fleet screenshot cannot prove lease correctness.

## Diagram content models

These are evidence-backed **content proposals**, not rendered or approved diagrams. All six use a
dominant left-to-right reading direction. Keep deployment boundaries explicit and branch conditions
visible. Use native Kubernetes pod/workload symbols, PostgreSQL/database symbols, and ordinary
sequence/decision notation for those real concepts; custom Agentweaver cards only for worker graph,
run state, policy, or product surfaces. No new external logos/assets were acquired.

### canonical-a2a-execution — retain identity, correct the consumer

**Takeaway:** leaf execution moves; worker-owned orchestration and durability do not.

Nodes/groups:
- Worker boundary: workflow graph + review/confirmation RequestPorts; `RemoteAgentProxy`;
  checkpoint/session management; event recorder (E1/E3).
- Sandbox boundary: AgentHost/leaf agent, executor container (E1/E3).
- Shared durable state: checkpoint/run-event store; browser/API timeline consumer outside the pod (E1).

Edges:
- `worker graph -> RemoteAgentProxy: execute leaf turn` (E1).
- `RemoteAgentProxy -> AgentHost: run-scoped A2A message:stream` (E1/E3).
- `AgentHost -> RemoteAgentProxy: output / RunEvent data parts` (E1).
- `RemoteAgentProxy -> worker event recorder: decode and record` (E1).
- `worker -> durable state: checkpoints and ordered run events` (E1).
- `durable events -> web timeline: cursor read / SSE delivery` (E1).
- `human -> worker gate: accountable decision` (E1/E3; only when that workflow/launch path has a gate).

Do not add pod→DB, pod→human tokens, universal mTLS, mandatory confirmation for all launches,
or unconditional transparent-replay arrows.

### experience-a2a-distributed-agents-fig2 — redesign lifecycle

**Takeaway:** claim/configure once, execute leaf turns, distinguish pause from failure.

Nodes: start/claim, controller-bound AgentHost, configure, stream turn, completed output;
worker gate + durable checkpoint; conditional release/retained preview; visible failure;
coordinator recovery decision (E1–E4).

Edges:
- `claim -> bound pod: controller/Kubernetes bind` → `configure: inject run context` → `stream: A2A`
  (E3; these are separate steps, not pod-create-per-token).
- `stream -> completed output: agent.turn.end received` (E2).
- `stream -> visible failure: incomplete marker / transport exception` (E2).
- `visible failure -> coordinator recovery: inspect retryability and recovery budget` (E2).
- `recovery -> fresh dispatch: when policy chooses redispatch` (E2; conditional return rail).
- `worker gate -> checkpoint-backed wait: human/external wait` → `release decision` (E3).
- `release decision -> release claim: enabled, supported, no active preview` OR
  `release decision -> retain pod: active preview/assembly retention or release disabled` (E3/E4).
- `resumable checkpoint -> claim/configure: resume after release` (E2/E3).

No failure→transparent-replay edge and no assurance of duplicate-free tool side effects.

### experience-operations-fig1 — redesign operations map

**Takeaway:** choose inspection versus configuration, and respect scope/parity.

Nodes/groups:
- Human/operator; web entry; assistant/MCP entry (E7).
- Inspect: Diagnostics, Heartbeat, Flow, Cluster, Observability (E5/E7).
- Configure: Account settings, Platform settings (admin), Project settings/sandbox (E7).
- API authoritative state; focused MCP diagnostics/heartbeat/policy tools (E7).

Edges:
- `operator -> web inspection: choose relevant state view` (E5/E7).
- `operator -> scoped settings: configure account/platform/project` (E7).
- `assistant -> MCP tools -> API: selected operations` (E7).
- `web views -> API: read state / authorized configuration requests` (E7).
- `API -> web/MCP: snapshots and operation results` (E7).
- `sandbox_policy_set -> repository shell_enabled: narrow mutation` (E7).

Account settings must not connect to repository sandbox editing. Cluster/Observability may connect
to REST-backed inspection, but not to invented dedicated MCP tools. Heartbeat pickup details belong
to the reused backlog sequence, not a second embedded architecture.

### experience-sandbox-pod-execution-fig3 — redesign pause/resume

**Takeaway:** the run persists while compute may be released; preview retention is deliberate.

Participants: human/UI, worker-owned run/gate, durable checkpoint/session/workspace, sandbox
AgentHost, optional retained preview resources (E1–E4).

Edges:
- `AgentHost -> worker -> event timeline: active turn output` (E1).
- `worker -> human: review requested` and `worker -> durable state: checkpoint` (E3).
- `worker -> AgentHost lifecycle: conditional release request` (E3).
- Alternative `lifecycle -> claim deletion` versus `lifecycle -> retained preview resources` (E3/E4).
- `human -> worker: decision` (E3).
- `worker -> durable state: load resumable state`; `worker -> fresh pod: claim/configure if released`
  (E2/E3).
- `resumed AgentHost -> worker -> timeline: subsequent events` (E1).

Label pod-name change as possible; no guaranteed stable name with release disabled, zero-compute human
wait, direct pod-to-user return, or rehydration of an arbitrary failed root model turn.

### experience-scaling-operations-fig1 — retain minimal responsibility map

**Takeaway:** web serves, workers own execution, Postgres shares durable state, sandboxes execute leaves.

Nodes: developer/browser; web tier; worker tier; managed Postgres; sandbox AgentHost pods.
Group web/worker as control-plane roles, sandbox as leaf execution, database as shared durable state.

Edges:
- `browser -> web: REST / watch request`; `web -> browser: responses / SSE` (E1/E7).
- `web -> Postgres: durable request/state access and event cursor reads` (E1/E6).
- `worker -> Postgres: ownership, checkpoint/event/run-state writes` (E1/E6).
- `worker -> sandbox: configure and execute leaf`; `sandbox -> worker: turn results/events` (E1/E3).

Annotate checked-in baseline web 2; worker HPA 2–3 CPU70/memory80. Do not depict KEDA, deployed web HPA,
LISTEN/NOTIFY, pod→DB, or a direct web→worker in-memory queue as the durable coordination seam.

### experience-scaling-operations-fig3 — retain lease story with bounded recovery

**Takeaway:** ownership can transfer after expiry, but continuation depends on recoverable run state.

Participants: worker A, Postgres lease/run state, worker B, recovery-state decision.

Edges:
- `A -> Postgres: guarded claim` / `Postgres -> A: token n` (E6).
- `A -> Postgres: renew matching owner + token` (E6).
- `A failure -> lease expiry: renewals cease` (E6; failure marker, not a message A sends).
- `B -> Postgres: eligible free/expired claim` / `Postgres -> B: token n+1` (E6).
- `stale A -> ownership check: old token rejected for terminal outcome` (E6).
- `B/restart path -> recovery decision: inspect persisted run/checkpoint/work-plan state` (E2/E6).
- Alternatives: `AwaitingReview -> recoverable checkpoint`, `Coordinator -> persisted work plan`,
  `stranded child -> retryable failure / redispatch`, `stranded root -> visible failure` (E2).

Do not draw a continuously polling lease-recovery daemon absent implementation evidence; depict an
eligible subsequent claim, not guaranteed automatic mid-turn migration.

## Applied page changes and consumer dispositions

| Owned page | Applied corrections |
|---|---|
| `a2a-distributed-agents.md` | Correct canonical alt; worker/leaf boundary; visible failures; conditional release and retention; code versus manifests; deploy-time mode changes; TLS overlay caveat; owned `.drawio` provenance. |
| `cluster-page.md` | Remove placeholder embed/fictional capture brief; current KPI labels, no active-pod table/quota bars; historical capacity distinction; actual timeout status and configured warm-pool check; retain live topology instructions without a static fork. |
| `sandbox-pod-execution.md` | Correct node identity/no global child fallback, placement-not-security-proof; Gateway preview/URL/lifetime; current preview-button conditions; release/retention/recovery boundaries; deployed mode; owned suspend diagram provenance. |
| `sandbox-browser-preview.md` | Reuse declared `sandbox-browser-preview-fig1.png`; 24-hour approval/lifetime and hard cap; retained resources; approved/request-changes preview attempts; publication check; current header-button and keepalive conditions. |
| `live-preview-provisioning.md` | Reuse declared `live-preview-provisioning-fig1.png`; 24-hour approval/lifetime; caller versus preview failure; retained resources; launch-mode confirmation distinction. |
| `scaling-operations.md` | Current web/worker/HPA facts; historical rollout; unsafe rollback promises removed; explicit run-state recovery and scoped fencing; cursor polling instead of notifications; remove two placeholder embeds/briefs; current Overview prose; owned `.drawio` provenance. |
| `operations.md` | Account/platform/project scope map and routes; MCP parity limits; current Cluster summary; remove five placeholder embeds/briefs; merge heartbeat sequence; no standalone Execution-page destination; owned `.drawio` provenance. |

Exact consumer migrations:
- `experience-operations-fig2.png` → `experience-workflows-backlog-fig3.png`; winner/lost/unavailable
  alternatives are described, while automation/Flow reporting stays in prose.
- `experience-sandbox-pod-execution-fig2.png` → `sandbox-browser-preview-fig1.png`; no local replacement
  source or shared asset edit.
- Added only audit-declared canonical consumers for browser preview and live-preview provisioning.
- Kept `canonical-sandbox-experience.png` and `canonical-a2a-execution.png` stable.

Editable provenance resolves to `docs/diagrams/drawio/generated/<name>.drawio`, confirmed against
area-sharded inventory and filesystem, not the nonexistent `drawio/<name>.drawio` shortcut.
Comments do not assert that this thread exported or approved the current PNGs.

## Validation and handoff blockers

- `git diff --check` passed for all seven owned pages.
- Read-only local-reference validation inspected all seven pages: Markdown/PNG targets and declared
  `.drawio` provenance paths exist; no `/screenshots/` embeds remain.
- All eight placeholder embed occurrences and their capture briefs were removed (Cluster one,
  Operations five, Scaling two); no asset files were deleted or replaced.
- Product tests above were read, not run. The required docs build is `npm run docs:build`
  (`CONTRIBUTING.md:160-161`), but it writes outside the allowed eight-file ownership set. Parent should
  run the integrated build after its concurrent changes finish. No dependency restore was needed.
- No render, pitch artifact, pass PNG inspection, growth measurement, or visual publication approval
  is claimed. Parent/diagram owner must reconcile the six models and complete the required pitch and
  four-plus iteration passes. Existing PNG content can still be stale until that occurs.
- Shared canonical assets and inventory status remain with their assigned owners.
- Additional cross-owner facts to reconcile: polling rather than LISTEN/NOTIFY; preview header
  lifecycle/session gate rather than active-run gate; keepalive independent of dialog-open state;
  specific stranded-child/root recovery; base versus production mTLS. These conclusions come from
  implementation/tests and should override older design prose or audit line assumptions.
- Minor tooling correction: an initial patch failed because displayed credential-template text was
  redacted; no file changed on that failure. Subsequent patches used unaffected exact context.

Bounded research and owned-document edits are complete. Stop here; no additional product or shared
asset work is authorized for this thread.
