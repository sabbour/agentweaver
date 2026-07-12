# Squad Decisions

## 2026-07-09T12:00:00Z: In-place steering revisions fail visibly and apply only after confirmed effects

**Date:** 2026-07-09T12:00:00Z  
**Author:** Morpheus and Tank; recorded by Scribe  
**Status:** SHIPPED

**Decision:** In-place steering revisions preserve context on the same worktree on success. Transient post-turn `CommitChanges` failures retry with a bounded 3-attempt backoff; genuine persistent failures terminalize visibly as `child_executor_failed:{executorId}` and route through the coordinator's conscious `dispatch_fresh` fallback. AgentWeaver must never turn this path into fake no-change success, silent wedge, or `watch_stream_completed_without_terminal_event` hang.

**Reliability contract:** Child-run executor failures always terminalize through `RunWatchLoopService.FailRunSafeAsync`, so the stream cannot end without a terminal event. Steering directives advance to `applied` only after targets are both assembly-eligible and their per-child effect markers are confirmed; this closes the crash-before-launch silent-drop window.

**Implementation:** `AgentTurnExecutor` retries commit failures and rethrows visible failures; `RunWatchLoopService` terminalizes child `ExecutorFailedEvent`s; `CoordinatorAssemblyService` falls back from failed in-place revision targets to visible `dispatch_fresh` and waits for eligibility plus effect-marker confirmation before applying directives. The rejected fake-no-change-success degrade was removed.

**Validation:** Rubber-duck gate went NO-GO then GO; code review went GO-with-caveat then GO. Build was clean, 731 tests passed, and no migrations were required. Coverage included `CoordinatorAssemblyServiceTests`, new `AgentTurnExecutorRevisionTerminalTests`, new `RunWatchLoopChildExecutorFailureTests`, and `BuildTestWorkflowTests`.

**Sources:** `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs`; `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs`; `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs`; `.learnings/ERRORS.md` `ERR-20260709-STEER1`.

---

## 2026-07-09T04:23:58-07:00: No feature flags for alpha features

**Date:** 2026-07-09T04:23:58-07:00  
**Author:** Ahmed (@sabbour), recorded by Scribe  
**Status:** ACTIVE DIRECTIVE

**Decision:** AgentWeaver is alpha software; new features ship on by default. Do not add feature flags for new behavior. When a feature replaces an old path, replace/remove the legacy path outright instead of dual-pathing. `Sandbox:Preview:Enabled` remains a real infrastructure-capability toggle, not a feature flag.

**Applied this session:** Removed the `Coordinator:Preview:DeterministicStep` rollout flag / `!IsProduction` gate and kept the deterministic preview path as the only path. Unified steering also ships on by default, with old auto-reset/redispatch behavior replaced rather than retained behind `Coordinator:UnifiedSteering`.

**Sources:** `.squad/decisions/inbox/coordinator-no-feature-flags.md`; session directive from Ahmed.

---

## 2026-07-09T04:23:58-07:00: Decoupled preview from Build/Test verdict and removed preview feature flag

**Date:** 2026-07-09T04:23:58-07:00  
**Author:** Morpheus; reviewed by rubber-duck / code-review / security  
**Status:** SHIPPED

**Decision:** Live preview is a deterministic platform step that runs after Build/Test returns and before the Build/Test verdict is applied. Preview runs for applicable work whether Build/Test approved or requested changes; it is skipped only for declined/terminal runs or when preview infrastructure is unavailable. Preview outcomes do not become code `REQUEST_CHANGES`, do not trigger reset/redispatch, and do not block human review.

**Implementation contract:** `PreviewStep` owns exactly one terminal preview outcome per `{runId, workPlanId, treeHash}`: `sandbox.preview_ready`, `sandbox.preview_failed`, or `sandbox.preview_skipped_not_applicable`. The old approval-time guard remains as a safety net, while the model-mediated Build/Test prompt path is no longer the source of preview readiness. `Coordinator:Preview:DeterministicStep` was removed under the no-feature-flags directive.

**Sources:** `.squad/decisions/inbox/morpheus-decouple-preview-design.md`; `.squad/decisions/inbox/coordinator-no-feature-flags.md`; prior preview decision entries in `.squad/decisions.md`.

---

## 2026-07-09T04:23:58-07:00: Unified autonomous steering shipped as the only correction path

**Date:** 2026-07-09T04:23:58-07:00  
**Author:** Tank and Trinity; reviewed by rduck-integration, creview-integration, seraph-integration  
**Status:** SHIPPED

**Decision:** All correction sources normalize into one `SteeringSignal` path: Build/Test, RAI, Rubberduck, human review, agents, coordinator, and workflow steps submit source-agnostic feedback to the coordinator. The coordinator-agent is the sole decider. It chooses A/B/C/D: A = `in_place_steer` resume on the same child context/session/worktree, B = `dispatch_fresh` conscious logged fresh dispatch, C = `proceed` / terminal, D = `advisory` surfaced with no reset.

**Durability and liveness:** `coordinator.steering_received` records the incoming signal and `coordinator.steering_decision` records the chosen effect before execution. Direction A uses a per-child effect marker keyed by `(directiveId, attempt, runId)` so recovery proves the specific revision attempt ran before marking applied. Direction B is never automatic; resets happen only after a visible `dispatch_fresh` decision. Execution is bounded by `CoordinatorSteeringDecider.MaxExecutionAttempts = 3`; exhaustion parks the directive in visible `needs_attention` instead of looping.

**UI contract:** Trinity renders the canonical backend decision strings exactly: `in_place_steer`, `dispatch_fresh`, `proceed`, and `advisory`. `dispatch_fresh` is deliberately prominent, `in_place_steer` resolves pending review as context-preserving steering, `proceed` advances review/terminal state, and `advisory` remains visible without resolving pending review. The run tree now sorts siblings primarily by `startedAt` ascending, with pending/no-start items trailing and deterministic fallback ordering.

**Sources:** `.squad/decisions/inbox/tank-unified-steering-design.md`; `.squad/decisions/inbox/coordinator-unified-steering-directive.md`; `.squad/decisions/inbox/trinity-steering-ui.md`; `.squad/decisions/inbox/coordinator-no-feature-flags.md`.

---

## 2026-07-08T21:00:00-07:00: Preview provisioning implemented and shipped to staging as v0.9.11-rc1

**Date:** 2026-07-08T21:00:00-07:00  
**Author:** Scribe  
**Status:** SHIPPED TO STAGING — AWAITING AHMED VALIDATION

**Decision:** Preview provisioning is implemented and shipped to staging as `v0.9.11-rc1` at commit `4f314457`. Ahmed's three decisions are locked: preview approval reuses the existing `AgentPreviewGate` toggle with no auto-approve bypass; missing preview does not block human review; and the shippable path uses a proper AgentHost `PreviewRunner` rather than shell-backgrounding.

**Implementation summary:** Morpheus delivered the AgentHost `PreviewRunner`, managed process-group supervision, port discovery, health checks, teardown, runtime tool surface, and Build/Test wiring. Tank delivered deterministic preview-outcome events and guard enforcement, reused the existing preview approval seam, kept preview failures out of the reset/redispatch route, and fixed the stale null-keyed `sandbox.preview_pending` stall by threading `work_plan_id` and `tree_hash` through pending payloads and requiring a positive tree match. Trinity surfaced preview ready/pending/unavailable states from `/events` on the Build & Test step and human-review artifacts panel. Link published the live-preview provisioning documentation set and updated coordinator, events, sandbox, review, and web docs.

**Gates:** Design rubber-duck passed GO after three rounds. Seraph returned SHIP with no exploitable findings: execution remains inside the sandbox pod boundary, the approval seam is intact, the port range is enforced, token isolation is preserved, and reapers are present. Code review returned SHIP after the stale-pending stall was fixed and regression-tested.

**Validation:** Targeted implementation validation passed across the wave: Morpheus build plus 24 tests, Tank 764 targeted tests after the stale-pending fix, Trinity web build and tests, docs build plus drift-check, and full backend suite 1614 passed. Coordinator bumped `VERSION` to `0.9.11-rc1`, rebuilt api/agent-host/frontend, retagged mcp, deployed to staging `agentweaver-aks-2`, verified rollouts green, `/health=200`, and pods on `v0.9.11-rc1`.

**References:** commit `4f314457`; inbox sources `coordinator-preview-decisions.md`, `tank-preview-outcome-guard.md`, `tank-context-preserving-revision.md`, `morpheus-buildtest-pod-binding.md`, `morpheus-buildtest-preview-wiring.md`, `morpheus-previewrunner-tool-surface.md`, `trinity-preview-event-ui.md`.

**Next:** Await Ahmed staging validation before PR merge or issue close.

---

## 2026-07-05T20-29-22: Fix workflow save 500: extend AllowedWorkflowIds when saving a new workflow (#175)

**Date:** 2026-07-05T20-29-22  
**Author:** Tank  
**Status:** PR OPEN — APPROVED

**Decision:** When saving a new workflow for a project with restricted `AllowedWorkflowIds`, the PUT handler appends the new workflow id to the allowed set before syncing the workflow registry.

**Rationale:** `WorkflowRegistry.FilterByAllowedSet` correctly drops ids absent from `AllowedWorkflowIds`, but a freshly written workflow is not yet in that set. Updating the allowed set before `Sync` keeps blueprint constraints intact while allowing newly saved workflows to reload. The 500 path now distinguishes validation/discovery failures and logs resolved path, allowed ids, and validation diagnostics.

**References:** #175, PR #177, `apps/Agentweaver.Api/Workflows/WorkflowDefinitionEndpoints.cs`, `apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs`

---

## 2026-07-05T20-44-43: Emit tool approval resolved SSE events on all approval resolution paths (#174)

**Date:** 2026-07-05T20-44-43  
**Author:** Tank  
**Status:** PR OPEN — APPROVED

**Decision:** `DurableToolApprovalGate` emits `tool.approval_resolved` on the child run stream for timeout, grant, and deny, and coordinator streams re-project that as `coordinator.child_approval_resolved`.

**Rationale:** The server-side timeout previously resolved the approval request in storage without notifying the UI, leaving stale approval buttons whose late clicks returned 409. The fail-closed timeout remains; the fix is to notify clients and distinguish already-resolved/expired requests from unknown request ids via `IsKnownRequest`.

**References:** #174, PR #182, `DurableToolApprovalGate.cs`, `docs/tool-approval-sse-contract.md`

---

## 2026-07-05T21-02-37: Represent server-timeout approval resolution as `resolvedScope='expired'` (#174)

**Date:** 2026-07-05T21-02-37  
**Author:** Trinity  
**Status:** PR OPEN — APPROVED

**Decision:** The frontend represents server-driven approval expiry as `resolvedScope='expired'` on `ApprovalRequestItem` and syncs `isResolved` prop changes into `ToolApprovalCard` local state with `useEffect`.

**Rationale:** The card initialized local state from props but did not update after reducer-driven server resolution. Extending the existing string discriminant avoids a new boolean and lets the card disable/collapse consistently when expiry or another operator resolves the request.

**References:** #174, PR #182, `docs/tool-approval-sse-contract.md`

---

## 2026-07-05T20-37-37: Blueprint library matching requires full process coverage before suppressing generation (#176)

**Date:** 2026-07-05T20-37-37  
**Author:** Morpheus  
**Status:** PR OPEN — APPROVED

**Decision:** Blueprint library-first matching now treats output-artifact overlap as insufficient process fit and requires full-stage coverage; partial matches return no match so the generator can create a specialized workflow.

**Rationale:** A triage → dedupe → research/validate → PRD prompt under-selected the generic PM discovery workflow because both produced a PRD/spec. Library matching should be used only when the workflow covers the distinctive process stages; otherwise generation produces a more accurate topology. Prompt criteria were reconciled between `CopilotBlueprintGenerator` and `WorkflowSelector`, with deterministic prompt-content tests and an ADR.

**References:** #176, PR #178, `CopilotBlueprintGenerator.cs`, `WorkflowSelector.cs`, `.squad/decisions/007-blueprint-match-vs-workflow-gen.md`

---

## 2026-07-05T21-31-41: Workflow auto-selection uses a tool-less direct completion (#183)

**Date:** 2026-07-05T21-31-41  
**Author:** Morpheus  
**Status:** PR OPEN — REVISED AFTER REVIEWER REJECTION

**Decision:** `CopilotWorkflowSelectionModel` performs workflow-selection classification with a direct, tool-less Copilot completion using `SessionConfig.Tools = []` and installation-scope auth, instead of running the full tool-enabled agentic loop.

**Rationale:** The prior path called `CopilotAIAgent.SetupAsync` without a user id, swallowed the exception, returned null twice, and silently defaulted to Generic. Even when auth succeeded, the full agentic loop could emit prose or tool-loop output that the selector could not parse. Parsing was hardened by stripping think blocks, applying code-fence cleanup, and using a single-id last-resort match on stripped response text.

**References:** #183, #176, PR #184, `WorkflowSelector.cs`, `CopilotWorkflowSelectionModel.cs`

---

## 2026-07-05T22-03-56: Reviewer rejection locked out original author; Tank owned final-message workflow-selection revision (#183)

**Date:** 2026-07-05T22-03-56  
**Author:** Tank  
**Status:** PR OPEN — APPROVED AFTER RE-REVIEW

**Decision:** After Smith rejected PR #184, Morpheus was locked out under Reviewer Rejection Protocol and Tank owned the revision. `CopilotWorkflowSelectionModel` now captures both delta text and final-message-only `AssistantMessageEvent` content, mirroring `CopilotAIAgent` response extraction.

**Rationale:** Smith found that the initial streaming loop accumulated only `chunk.Text`; if the SDK returned a consolidated final assistant message with no delta text, the response stayed empty and the selector could fall back to Generic again. Tank added `CaptureResponseTextAsync`, final-message-only regression coverage, `InternalsVisibleTo` for tests, and applied Smith's optional stripped-text last-resort match. Build was clean and WorkflowSelect tests passed 41/41; Smith approved the re-review and Seraph had already approved security.

**References:** #183, PR #184, commit `1a9accc`, Smith review, Seraph review

---

## 2026-07-05: 2026-06-30T22-24-16: Shipped 5-stream session: backend cross-replica fixes deployed, frontend un-bun

**Source:** decisions/inbox/coordinator-shipped-5-stream-session-backend-cross-replica-fix.md  
**Merged by:** Scribe  

### 2026-06-30T22-24-16: Shipped 5-stream session: backend cross-replica fixes deployed, frontend un-bundled docs + cost UI deployed, 40 outcome-based issues filed, specs restructured
**By:** coordinator
**What:** Shipped 5-stream session: backend cross-replica fixes deployed, frontend un-bundled docs + cost UI deployed, 40 outcome-based issues filed, specs restructured
**References:** issues #1 #24-#41, commits f470aa4 3b844a9 900ff06 9413053, deploy agentweaver-api:5cb41c3 agentweaver-frontend:9413053
**Why:** Session for @sabbour landed and deployed across five parallel workstreams.

DEPLOYED TO AKS:
- api + worker -> agentweaver-api:5cb41c3 (SSE cross-replica streaming + execution pod-name persistence to shared RunEvents).
- frontend -> agentweaver-frontend:9413053 (cost chips on cards+DAG+board, token table alignment, leaderboard cost-per-agent + shared date filter, overview cost dashboard, DAG node-overlap fix via shared DAG_NODE_SEP=96 + height hints, docs un-bundled -> /docs redirects to https://sabbour.github.io/agentweaver/).

COMMITTED + PUSHED to main (5cb41c3..9413053):
- f470aa4 frontend stops building/serving docs (GitHub Pages canonical).
- 3b844a9 9 personas + Playwright self-improvement harness (issue #1).
- 900ff06 legacy speckit specs replaced with 12-area / 29-story concise specs (#2-#37), one story per file, cross-linked, no impl detail.
- 9413053 code-bloat sweep (unused NativeToolExclusion removed; opaque-token dedup byte-identical CSPRNG; stale docs aligned).

GITHUB ISSUES (40 open, all outcome-based, correct squad:* owners):
- Picard multi-replica state sweep filed 10 (4xP0): #24 HITL gates, #28 assembly review gate, #30 standard review registry, #39 repo merge lock (P0); #26, #32 (P1 squad:morpheus); #40, #34, #36, #38 (squad:tank). Flagged legitimately-in-memory items to avoid over-migration.
- Switch filed #41 (unused public RealPath API decision).

NOT redeployed (image-efficient): mcp + agent-host unchanged; api at 5cb41c3 omits only Switch behavior-neutral dedup (ships next backend deploy).

## 2026-07-05: 2026-07-01T01:35:00Z: User directive — autopilot standing orders

**Source:** decisions/inbox/copilot-directive-2026-07-01T01-35-00Z.md  
**Merged by:** Scribe  

### 2026-07-01T01:35:00Z: User directive — autopilot standing orders
**By:** Ahmed Sabbour (via Copilot)
**What:** Full autopilot. Treat root causes, not symptoms. Use Squad team for all execution — never inline. Keep reminding self to delegate. Update docs (guide, deep dive, experience), architecture diagrams, and screenshots plan for every issue that changes behavior.
**Why:** User request — captured for team memory and to govern every subsequent issue loop.

## 2026-07-05: 2026-07-01T01:47:00Z: Directive — skip PR gate, merge directly to main and ship

**Source:** decisions/inbox/copilot-directive-2026-07-01T01-47-00Z.md  
**Merged by:** Scribe  

### 2026-07-01T01:47:00Z: Directive — skip PR gate, merge directly to main and ship
**By:** Ahmed Sabbour (via Copilot)
**What:** No waiting on PRs. Workflow is: work in worktree → merge locally into main → build changed images → deploy to AKS → close issue. PRs are informational only, not a gate.
**Why:** Rapid prototyping mode. Speed over review ceremony.

## 2026-07-05: 2026-06-30T21-42-41: Document shared RunEvents streaming and AgentHost-only sandbox docs stance

**Source:** decisions/inbox/Crusher-document-shared-runevents-streaming-and-agenthost-.md  
**Merged by:** Scribe  

### 2026-06-30T21-42-41: Document shared RunEvents streaming and AgentHost-only sandbox docs stance
**By:** Crusher
**What:** Document shared RunEvents streaming and AgentHost-only sandbox docs stance
**References:** docs/run-event-stream.md, docs/deep-dive/distributed-execution-scaling.md, docs/reference/scaling-data-layer.md, docs/architecture-aks.md, README.md
**Why:** Docs now treat the shared RunEvents table as the source of truth for run streaming: each RunStreamEntry append is mirrored into IRunEventStream, EfRunEventStream polls the shared table by cursor, and SSE replicas can stream runs they do not own locally. For sandbox documentation, the live pod-per-run path is documented as AgentHost-only through the shared agentweaver-agent-host SandboxTemplate/WarmPool and post-bind /configure; legacy sleep-infinity agentweaver-sandbox template/image/pool references were removed from Crusher-owned docs. Build gate passed with `cd docs; npm run build`, and generated docs check passed with `node scripts/gen-docs.mjs --check`.

## 2026-07-05: 2026-06-29: Feature 019 documentation (all facets)

**Source:** decisions/inbox/link-019-docs.md  
**Merged by:** Scribe  

### 2026-06-29: Feature 019 documentation (all facets)
**By:** Link (DevRel)
**What:** Complete docs for AI credit and token usage monitoring across deep-dive, reference, user guide, screenshots, landing card, nav, cross-links, generated docs, and existing page updates. Build verified green.
**Why:** docs-feature skill definition of done requires all applicable facets before merge.

## 2026-07-05: App Insights observability wiring

**Source:** decisions/inbox/link-appinsights.md  
**Merged by:** Scribe  

# App Insights observability wiring

Date: 2026-07-05
Owner: Link

Decision: stop hardcoding the Log Analytics workspace customerId in Kubernetes manifests. `APPLICATIONINSIGHTS_WORKSPACE_ID` is now rendered from `APPINSIGHTS_WORKSPACE_ID`, derived from the live `agentweaver-logs` workspace during AKS variable/deploy flows. Monitoring provisioning also grants the workload user-assigned managed identity `Log Analytics Reader` on the workspace so `LogsQueryClient` can query workspace-based App Insights data.

Live read-only checks showed the current `agentweaver-aks-2` cluster domain is `*.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io`; current workspace customerId is `e09d6407-5c4c-4ebc-98db-10660f555507`, matching the previously hardcoded value at check time, but the identity had no direct workspace-scoped reader assignment. The dynamic render remains required so future reprovisioned workspaces do not silently point at stale IDs.

## 2026-07-05: 2026-06-30T11-35-39: Deploy 4974b87 - AgentHost dual-stack bind fix + lease TTL + sessionAffinity

**Source:** decisions/inbox/Link-deploy-4974b87-agenthost-dual-stack-bind-fix-lease.md  
**Merged by:** Scribe  

### 2026-06-30T11-35-39: Deploy 4974b87 - AgentHost dual-stack bind fix + lease TTL + sessionAffinity
**By:** Link
**What:** Deploy 4974b87 - AgentHost dual-stack bind fix + lease TTL + sessionAffinity
**References:** 4974b87, 4a774d3, k8s/api-service.yaml, apps/Agentweaver.AgentHost/Dockerfile
**Why:** # Deploy Report: 4974b87
**Date:** 2026-06-30T04:22:37 PDT  
**Requested by:** Ahmed (automated deploy during sleep)  
**Tag:** `4974b87`  
**Commits deployed:**
- `4974b87` — fix: AgentHost dual-stack bind, lease TTL > probe timeout, api sessionAffinity
- `4a774d3` — fix: routing.md signals from Role.Responsibilities, not hardcoded buckets

---

## Image Builds

| Image | Action | Result | Time |
|-------|--------|--------|------|
| `agentweaver-agent-host:4974b87` | ACR Build (cc2p) | SUCCESS | ~1m 27s |
| `agentweaver-api:4974b87` | ACR Build (cc2q) | SUCCESS | ~3m 43s |
| `agentweaver-frontend:4974b87` | Retag from 006457a | SUCCESS | instant |
| `agentweaver-mcp:4974b87` | Retag from 006457a | SUCCESS | instant |

Both builds ran in parallel as PowerShell background jobs.

---

## AKS Deploy

**Context:** agentweaver-aks-2  
**Namespace:** agentweaver

Deploy script (30-deploy.sh) ran successfully. All deployments rolled out:

- agentweaver-api -- successfully rolled out (2/2 replicas Ready)
- agentweaver-frontend -- successfully rolled out
- agentweaver-mcp -- successfully rolled out
- agentweaver-worker -- successfully rolled out

### sessionAffinity
```
kubectl get svc agentweaver-api -n agentweaver -o jsonpath='{.spec.sessionAffinity}'
=> ClientIP (timeoutSeconds: 10800)
```
VERIFIED: api-service.yaml applied with sessionAffinity: ClientIP, 3-hour timeout.

### Agent-Host IPv4 Dual-Stack Bind Fix
Old warm-pool pods (140m old) showed [::] (IPv6-only) from the previous image.
Deleted both old pods; warm pool controller created 2 new pods (855zc, snm6z) from updated 4974b87 template.

New pods verified:
```
Now listening on: http://0.0.0.0:8088
```
VERIFIED: Dual-stack bind confirmed -- no longer IPv6-only.

---

## Git Push
```
89b40e2..4974b87  main -> main
```
VERIFIED: Both commits pushed to origin/main.

---

## All Pods (post-deploy)
```
agentweaver-agent-host-855zc   1/1  Running  (new, image 4974b87)
agentweaver-agent-host-snm6z   1/1  Running  (new, image 4974b87)
agentweaver-api-*              2/2  Running
agentweaver-frontend-*         2/2  Running
agentweaver-mcp-*              1/1  Running
agentweaver-worker-*           1/1  Running
```

No errors encountered. Deployment fully complete.

## 2026-07-05: Deploy Report — commit cc7dd9d

**Source:** decisions/inbox/link-deploy-cc7dd9d.md  
**Merged by:** Scribe  

# Deploy Report — commit cc7dd9d

**Date:** 2026-06-30  
**Deployed by:** Link  
**Commit:** cc7dd9d  
**Changed component:** `apps/Agentweaver.Api/KubernetesSandboxExecutor.cs` — `CreateAgentHostClaimAsync` no longer injects `spec.env` into SandboxClaims.

---

## Step 0 — New image tag

`cc7dd9d`

---

## Step 1 — Retag unchanged images (ACR server-side)

| Image | Source tag | Target tag | Result |
|---|---|---|---|
| agentweaver-agent-host | 4974b87 | cc7dd9d | ✅ Success |
| agentweaver-frontend | 4974b87 | cc7dd9d | ✅ Success |
| agentweaver-mcp | 4974b87 | cc7dd9d | ✅ Success |

All three `az acr import` jobs completed with no error output.

---

## Step 2 — API image build

- **ACR Build ID:** cc2r  
- **Registry:** agentweaverregistry.azurecr.io  
- **Image:** `agentweaver-api:cc7dd9d`  
- **Digest:** `sha256:f49d1f81f1cc4ab399f350b87b58acf828d552efe734c4679459e58576be384b`  
- **Duration:** ~3m 40s  
- **Result:** ✅ Success — `Run ID: cc2r was successful after 3m40s`

---

## Step 3 — Deploy to AKS

All manifests applied via `scripts/aks/30-deploy.sh IMAGE_TAG=cc7dd9d`.  
Deployments rolled out: api, frontend, mcp, worker — all successful.

---

## Step 4 — Rollout verification

```
deployment "agentweaver-api" successfully rolled out
agentweaver-api-777f849885-269nw   1/1   Running   0   60s
agentweaver-api-777f849885-2tx5w   1/1   Running   0   97s
```

✅ Both API replicas Running, no CrashLoopBackOff.

---

## Step 5 — SandboxClaim env field check

No SandboxClaims were present in the cluster at time of check (`No resources found`). The warm pool had not yet issued any claims post-deploy. The API code change (`KubernetesSandboxExecutor.cs`) has been deployed; `spec.env` injection is removed — next created claims will not carry the field.

**Verdict:** ✅ Code change deployed; env-injection removed. Claim verification pending first run.

---

## Step 6 — Git push

```
4974b87..cc7dd9d  main -> main
```

✅ Pushed to origin successfully.

---

## Summary

| Item | Status |
|---|---|
| New image tag | `cc7dd9d` |
| Retag (agent-host, frontend, mcp) | ✅ All succeeded |
| API build (ACR run cc2r) | ✅ Success |
| Rollout (all deployments) | ✅ Healthy |
| spec.env absent from new claims | ✅ Code deployed; no claims in pool yet to inspect |
| Git push to origin/main | ✅ Success |

## 2026-07-05: Link Deploy Report — 2026-07-01T09:30:00Z

**Source:** decisions/inbox/link-deploy.md  
**Merged by:** Scribe  

# Link Deploy Report — 2026-07-01T09:30:00Z

---

## Batch 3 — 2026-07-01T10:48:00Z (Morpheus hotfix)

**Trigger:** Morpheus (Runtime Engineer) — SandboxClaim v1beta1 hotfix. Every SandboxClaim was rejected with HTTP 422 due to wrong body schema in commit a731f70; fix in 89b40e2 reverts to `spec.warmPoolRef.{name}` required by the deployed v0.5.0 controller.

### Commit

| Field | Value |
|-------|-------|
| Commit SHA | `89b40e2` |
| Author | Morpheus |
| Branch | `main` |
| Changed files | `KubernetesSandboxExecutor.cs`, `KubernetesSandboxExecutorClaimTests.cs` |

### Image Builds / Retags

| Image | Tag | Method | Result |
|-------|-----|--------|--------|
| `agentweaver-api` | `89b40e2` | `az acr build` (ACR Tasks) | SUCCESS — Run ID: cc2n, 3m35s |
| `agentweaver-frontend` | `89b40e2` | `az acr import` retag from `006457a` | SUCCESS |
| `agentweaver-mcp` | `89b40e2` | `az acr import` retag from `006457a` | SUCCESS |
| `agentweaver-agent-host` | `89b40e2` | `az acr import` retag from `006457a` | SUCCESS |

### AKS Deployment

| Deployment | Result |
|------------|--------|
| `agentweaver-api` | Successfully rolled out — 2/2 replicas Ready |
| `agentweaver-frontend` | Successfully rolled out |
| `agentweaver-mcp` | Successfully rolled out |
| `agentweaver-worker` | Successfully rolled out |

Running image: `agentweaverregistry.azurecr.io/agentweaver-api:89b40e2`

### Git Push

origin was already at `89b40e2` (Morpheus pushed directly) — no push needed.

---

## Batch 2 — 2026-07-01T10:18:00Z

### Commit

| Field | Value |
|-------|-------|
| Commit SHA | `006457a` |
| Branch | `main` |
| Files changed | 3 (1 backend, 2 frontend) |

Commit message: `fix: routing.md per-agent signals, auto-sync on team creation, bigger capture form`

### Image Builds

| Image | Tag | Method | Result |
|-------|-----|--------|--------|
| `agentweaver-api` | `006457a` | `az acr build` (ACR Tasks) | SUCCESS — Run ID: cc2m, 3m51s |
| `agentweaver-frontend` | `006457a` | `az acr build` (ACR Tasks) | SUCCESS — image pushed; az CLI log-streaming crashed (Windows cp1252/unicode checkmark), confirmed via `show-tags` |
| `agentweaver-mcp` | `006457a` | `az acr import` retag from `453cc0c` | SUCCESS |
| `agentweaver-agent-host` | `006457a` | `az acr import` retag from `453cc0c` | SUCCESS |

### AKS Deployment

All manifests applied. Rollout results:

| Deployment | Result |
|------------|--------|
| `agentweaver-api` | Successfully rolled out |
| `agentweaver-frontend` | Successfully rolled out |
| `agentweaver-mcp` | Successfully rolled out |
| `agentweaver-worker` | Successfully rolled out |

All pods `1/1 Running`. No ImagePullBackOff.

### Git Push

| Field | Value |
|-------|-------|
| Remote | `origin` (https://github.com/sabbour/agentweaver.git) |
| Branch | `main` |
| Previous SHA | `b00fae8` |
| Pushed SHA | `006457a` |
| Result | SUCCESS |

---

## Commit

| Field | Value |
|-------|-------|
| Commit SHA | `453cc0c` |
| Branch | `main` |
| Files changed | 9 (3 backend, 6 frontend) |

Commit message: `fix: autopilot gate on spec auto-confirm, SSE reconnect, cluster diagnostics UI`

## Image Builds

| Image | Tag | Method | Result |
|-------|-----|--------|--------|
| `agentweaver-api` | `453cc0c` | `az acr build` (ACR Tasks) | SUCCESS — Run ID: cc2j, 3m42s |
| `agentweaver-frontend` | `453cc0c` | `az acr build` (ACR Tasks) | SUCCESS — image pushed; az CLI log-streaming crashed on Windows cp1252/unicode (checkmark in vite output), but `az acr repository show-tags` confirmed push |
| `agentweaver-mcp` | `453cc0c` | `az acr import` retag from `a731f70` | SUCCESS |
| `agentweaver-agent-host` | `453cc0c` | `az acr import` retag from `a731f70` | SUCCESS |

### Frontend build note
The `az acr build` log streaming for the frontend threw a `UnicodeEncodeError` (`charmap` / cp1252 can't encode `\u2713`) on the Windows client side. This is a known az CLI + colorama issue on Windows. The image was confirmed pushed by checking `az acr repository show-tags --repository agentweaver-frontend --top 1` which returned `453cc0c`.

## AKS Deployment

Script: `scripts/aks/30-deploy.sh`

Variables used:
- `IMAGE_TAG=453cc0c`
- `TENANT_ID=72f988bf-86f1-41af-91ab-2d7cd011db47`
- `IDENTITY_CLIENT_ID=81bf7404-cd96-4d0c-8336-9316b847aefa` (agentweaver-api-identity)

All manifests applied. Rollout results:

| Deployment | Rollout |
|------------|---------|
| `agentweaver-api` | Successfully rolled out |
| `agentweaver-frontend` | Successfully rolled out |
| `agentweaver-mcp` | Successfully rolled out |
| `agentweaver-worker` | Successfully rolled out |

## Pod Status (post-deploy)

All pods Running/1/1. No ImagePullBackOff on agent-host (retag resolved the issue).

## Endpoints

- Frontend: `https://agentweaver.6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io/`
- API: `https://agentweaver.6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io/api/`
- MCP: `https://agentweaver.6a41f26c75d5cf00019ef7d7.westus2.staging.aksapp.io/mcp/`

## Git Push

| Field | Value |
|-------|-------|
| Remote | `origin` (https://github.com/sabbour/agentweaver.git) |
| Branch | `main` |
| Previous SHA | `8d97625` |
| Pushed SHA | `453cc0c` |
| Result | SUCCESS |

## Issues Encountered

1. **PowerShell → bash env var propagation**: `$env:X` in PowerShell does NOT propagate to bash subprocess in this environment. Workaround: pass variables inline via `bash -c "export X=...; bash script.sh"`.
2. **Frontend az CLI log streaming crash**: Windows cp1252 encoding cannot render `\u2713` (checkmark) emitted by vite. Build itself succeeded in ACR. Mitigation for future: set `PYTHONIOENCODING=utf-8` or use `--no-logs` flag if available.

## 2026-07-05: Decision: GitHub issue sync + PR-as-workflow-action feature placement

**Source:** decisions/inbox/link-github-sync-pr-features.md  
**Merged by:** Scribe  

# Decision: GitHub issue sync + PR-as-workflow-action feature placement

Author: Link (Platform Engineer)
Date: 2026-06-30
Status: Proposed (drafts handed to coordinator; issues not yet opened)

## Context
Drafting two outcome-based feature issues after investigating the codebase. No code edits.

## Key findings (file:line evidence)
- Connected repo coords: `packages/Agentweaver.Domain/ProjectOrigin.cs:5-14` (`SourceRepository` = "owner/repo"); persisted in `EfProjectStore.cs:155-221`.
- Auth: GitHub App + per-user token isolation. `IGitHubTokenStore` (`packages/Agentweaver.Domain/IGitHubTokenStore.cs:9-18`, scopes Installation / ForUser). Token provider `GitHubTokenRefreshService.cs:25-104` (per-scope SemaphoreSlim refresh serialization). OAuth scopes include `repo read:user read:org copilot` (`GitHubOAuthRedirectService.cs:21-22`).
- NO Octokit / GitHub issue/PR API client exists today. GitHub calls are raw HttpClient (auth only). Merge is LOCAL git via LibGit2Sharp (`WorktreeManager.cs:917-996`, `MergeCoordinator.cs:77-179`) — NOT GitHub PR API.
- Backlog model: `BacklogTask` (`packages/Agentweaver.Domain/BacklogTask.cs:3-43`) with Title/Description/State/OrderKey/`SourceFilePath` (idempotency field). Store `IBacklogTaskStore.cs`, EF/Sqlite stores. Endpoints `BacklogEndpoints.cs`. Capture accepts `external_id` -> stored in `SourceFilePath` (`BacklogEndpoints.cs:50-72`). No `source_url`/state/labels/assignee fields yet.
- Heartbeat infra: API and worker are the SAME assembly, role-switched (`AppRole.cs:7-24`). Existing `BackgroundService`s use `PeriodicTimer` (`CoordinatorHeartbeatService.cs:28-79`, `TokenUsageProjectionService.cs`, reapers). Multi-replica safety via atomic claim / pod-name lease (`CoordinatorReconciler.cs:41-118`, TTL 120s). k8s: API replicas:2, worker replicas:1 (HPA 1-3). Team rejects replicas:1/stickiness.
- Workflows: `WorkflowDefinition.cs:7-176` (`WorkflowNodeType`, `WorkflowNode`). Dispatch via `NodeExecutorRegistry.cs:29-90` switch on `NodeKind` and `RunWorkflowGraphBinder.cs:299-519`. Non-agent "plumbing"/"action" executors exist via `VisualFunctionExecutor<TIn,TOut>` (`RunWorkflowFactory.cs:390-510`, e.g. merge-adapter). No `ActionType` enum. PR action would be a new VisualFunctionExecutor wired into the binder/registry.

## Decisions
1. **Feature A home = work-intake-board** (sync target is the backlog). Connection layer (mcp-integrations/identity-access) is a dependency, not the home.
2. **Feature A Phase 1 = heartbeat poll** running in the **worker role** (or API BackgroundService guarded by the coordinator's atomic-claim/lease pattern) for multi-replica safety. Webhooks = future/out-of-scope.
3. **Feature B home = workflows-automation** (it's a new workflow action/step type). review-merge contributes the GitHub client need but is not the home.
4. **Shared prerequisite**: both features need a real GitHub REST client for issues/PRs (Octokit or HttpClient wrapper) authenticated via the existing `IGitHubTokenStore`/token-refresh stack — none exists today. Call this out in both drafts.

## Out of scope
Webhooks/real-time sync; bidirectional conflict-resolution UI; GitHub Projects (beta) sync.

## 2026-07-05: Link decision: correlate run traces through operation ids

**Source:** decisions/inbox/link-observability-trace-correlation.md  
**Merged by:** Scribe  

# Link decision: correlate run traces through operation ids

Date: 2026-07-02
Issue: #150

The run trace API now finds span rows by first locating App Insights `traces` records tagged with the run id, then expanding to `requests`/`dependencies` that share the same `operation_Id` (or parent operation id).

Rationale:
- AgentHost already emits Azure Monitor telemetry when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present.
- The warm-pool AgentHost manifest already injects that env var via `k8s/sandbox-template-agenthost.yaml`.
- The previous `/api/metrics/runs/{runId}/traces` query only matched span rows whose own `customDimensions` carried run id fields, which can miss AgentHost spans even when logs for the same trace contain `RunId`.
- Correlating through operation ids preserves the distributed-trace timeline without requiring new runtime instrumentation.

## 2026-07-05: Link — Observability follow-up

**Source:** decisions/inbox/link-observability.md  
**Merged by:** Scribe  

# Link — Observability follow-up

Date: 2026-07-05T19:01:52-07:00
Requested by: sabbour (Ahmed)
Worktree: C:\Users\asabbour\Git\aw-link-observability
Branch: squad/observability-overhaul

## Decision
Root-cause fix App Insights observability at the model-turn layer instead of relabeling UI fallbacks.

## Telemetry root cause
Copilot model turns only emitted durable `agent.turn.usage` run events. They did not emit an App Insights-exported `agentweaver.token.usage` metric from the model-turn code path, nor a client Activity/span tagged with run/project/agent/model dimensions, duration, usage, and TTFT. The queries were therefore either empty or fell back to persisted events, and model/agent dimensions degraded to `unknown`.

## Changes
- Added per-Copilot-turn Activity telemetry (`Agentweaver model turn`) tagged with `run_id`, `project.id`, `agent_name`, `gen_ai.agent.name`, `model`/`model_id`, `gen_ai.request.model`, `gen_ai.response.model`, token usage, nano-AIU, and TTFT.
- Added model-turn `agentweaver.token.usage` metric emission with matching App Insights dimensions.
- Updated App Insights queries to read non-empty model/agent dimensions, include TTFT from custom dimensions as well as measurements, restrict duration/TTFT queries to agentic/model spans, and add daily AI-credit trend data.
- Updated run trace querying to fetch only agentic/LLM spans, include child run IDs for coordinator traces, match coordinator synthetic run-id suffixes, and use run-store agent names as fallback labels.

## Layout changes
- Removed the duplicate Observability Overview `Run creation count` chart and kept the range-aware `Runs created over time` tile.
- Changed Overview metrics to a responsive compact tile grid.
- Added an `AI credit usage over time` tile.

## Commits
- 29f3170 fix(observability): emit App Insights model-turn telemetry
- f3f10bb chore(observability): compact overview metric tiles

## Validation
- `npm --prefix apps/web run build` passed.
- `npm --prefix apps/web test -- --run` passed.
- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release --no-restore` passed.
- `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj -c Release --filter "Observ|Metrics|AppInsights|Telemetry|Diagnostics" --no-restore` passed.

## 2026-07-05: RCA: Orchestration 872edc84 — mixed child outcomes + overall failure

**Source:** decisions/inbox/morpheus-872edc84-rca.md  
**Merged by:** Scribe  

# RCA: Orchestration 872edc84 — mixed child outcomes + overall failure

**Author:** Morpheus (Runtime Engineer)
**Date:** 2026-06-30
**Orchestration:** 872edc84-2972-4cfe-b136-659a9bfc9c91 (backlog_pickup, "Azure Bubblegum" launch plan)
**Image:** agentweaverregistry.azurecr.io/agentweaver-api:4f92936 (rollout overlap with 5cb41c3) — AKS prod, 2 api replicas

## Verdict
Infrastructure/state bug — NOT a legitimate task failure. The pivotal child (subtask 35) **completed its agent work** (research markdown created, `agent.turn.end` emitted with token usage) but was marked `failed` because its post-turn `git commit` collided on a **shared git worktree + shared branch** that all concurrent child runs reuse.

## Evidence timeline (children of 872edc84, all on pod agentweaver-api-...-bl78d)
| subtask | run id | status | reason | worktree_path | branch | end |
|--------|--------|--------|--------|---------------|--------|-----|
| 33 | 5a2b4ec2 | assemble_ready | (rai pod launch failed → defaulted Yellow) | .../worktrees/872edc84 | agentweaver/872edc84 | 22:16:01 |
| 34 | 441b057e | assemble_ready | (rai pod launch failed → defaulted Yellow) | .../worktrees/872edc84 | agentweaver/872edc84 | 22:16:01 |
| 35 | 4679a719 | **failed** | watch_stream_completed_without_terminal_event | .../worktrees/872edc84 | agentweaver/872edc84 | 22:16:00 |
| 36 | 6b020701 | assemble_ready | finished alone @22:15:18, no contention | .../worktrees/872edc84 | agentweaver/872edc84 | 22:15:18 |
| 37–42 | (none) | failed | never dispatched (depend on planning; cascaded) | — | — | — |
| parent | 872edc84 | **failed** | assembly_blocked: ineligible_subtasks | — | — | 22:16:01 |

All four `worktree`-isolation children were provisioned the **same** worktree_path and branch. Subtask 36 succeeded only because it finished alone; 33/34/35 finished in a 3-second cluster (22:15:58–22:16:01) and contended.

## Root cause (mechanism)
1. By design, every child of a coordinator orchestration **reuses one shared worktree + branch**, regardless of per-subtask IsolationStrategy:
   - `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:188` "Reuse the coordinator's shared worktree instead of provisioning a per-child worktree." (`StartChildRunAsync` → `GetOrProvisionOrchestrationWorktreeAsync`, lines 183-210, 797-845)
   - `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:126` "IsolationStrategy ('shared' vs 'worktree') has NO runtime enforcement — all child [runs share the worktree]"
   - `apps/Agentweaver.Api/Infrastructure/SqliteDb.cs:134` "One shared worktree per orchestration: all child runs share the coordinator's worktree path"
2. After the agent turn, `AgentTurnExecutor` calls `_worktreeOps.CommitChanges(sharedWorktreePath, runId)` at `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs:135` — **outside** the executor's try/catch (lines 73-117).
3. `WorktreeManager.CommitChanges` (`apps/Agentweaver.Api/Git/WorktreeManager.cs:144-170`) opens `new Repository(sharedWorktreePath)`, runs `Commands.Unstage(repo,"*")` (line 148) then `Stage` + `repo.Commit` on the shared index/branch. Concurrent children race on the shared `index.lock`/refs; `Unstage("*")` also wipes a sibling's staged files. libgit2 throws.
4. The exception is uncaught (outside try/catch) → MAF emits `ExecutorFailedEvent(agent)`. Because the agent node is a self-emitter, `RunWatchLoopService.EmitExecutorStep` **suppresses** it (see node-ownership list ~line 267). The MAF stream then ends with **no terminal `WorkflowOutputEvent`**, so `RunWatchLoopService` (`apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:226-229`) logs and fails the run with `watch_stream_completed_without_terminal_event`. The real git error is never surfaced — a silent, misleading failure.

## Why the orchestration failed overall
All-or-nothing assembly gate. Subtask 35 (planning) failing made downstream subtasks 37 (synthesis) and 38-42 (execution/validation) ineligible; they were marked `failed` without dispatch. Dispatch ended `assemble_ready=3, failed=7` → `CoordinatorAssemblyService.BlockAsync("ineligible_subtasks")` (`CoordinatorAssemblyService.cs:285`) → coordinator run set Failed `assembly_blocked: ineligible_subtasks` (line 683). One spurious child failure propagated to total orchestration failure.

## Secondary issues observed (not the primary cause; track separately)
- **Cross-replica coordinator checkpoint restore failure** (pfhs9, 22:11:09): "Multi-replica on-demand resume ... not in local registry; resuming from shared checkpoint" → `JsonException: metadata property ... $.workflow.edges.coordinator-draft[0].$type`. Recovered by deferring the decision to DB for the owning replica. Real multi-replica checkpoint-deserialization bug.
- **TokenUsageProjectionService duplicate-key** `23505 PK_token_usage_records` (`EfTokenUsageStore.cs:42`) — projection not idempotent across replicas/reprocessing. Logged, non-fatal.
- **RAI pod launch "without a submitting user"** for `<run>-rai` sub-runs (KubernetesSandboxExecutor.cs:317) → RAI defaulted to Yellow. Submitting user not propagated to rai pod launch.

## Duplicate check
NOT a duplicate. Open replica bugs (#24,#26,#28,#30,#32,#34,#36,#38,#39,#40) are all about cross-replica *consistency*. #39 (merge serialization) is the merge/assembly critical section, not concurrent child-run commits during dispatch. No open issue covers the shared-worktree concurrent-commit race.

## Proposed root-cause fix (shared store, not replicas:1)
Make child-run git state isolated/shared-safe rather than serializing replicas. Two complementary directions:
1. **Honor IsolationStrategy at runtime**: provision a per-child worktree + per-child branch for `worktree`-isolation subtasks (each child commits to its own branch off the orchestration base); assembly merges child branches. This removes the shared mutable index entirely.
2. If a shared worktree is intentionally retained for `shared` subtasks, guard `CommitChanges` (stage/unstage/commit) with the existing per-repository critical section (`RepositoryMergeLock`) so commits serialize safely, and move the catch boundary in `AgentTurnExecutor` to cover commit/diff so a commit failure is surfaced (ret* or explicit `run.failed` with the git error) instead of the silent `watch_stream_completed_without_terminal_event`.

## 2026-07-05: Run Trace: 9a8b1f70 — SandboxClaim v0.5.0 fix verification

**Source:** decisions/inbox/morpheus-9a8b1f70.md  
**Merged by:** Scribe  

# Run Trace: 9a8b1f70 — SandboxClaim v0.5.0 fix verification

- **Author:** Morpheus (Runtime Engineer)
- **Requested by:** Ahmed
- **Date:** 2026-06-30T04:00Z (UTC 11:00)
- **Run ID:** `9a8b1f70-53d5-4166-8bb2-1f313c27028e`
- **Fix under test:** commit `89b40e2` — `spec.warmPoolRef:{name}` in `KubernetesSandboxExecutor.cs` (both claim paths)
- **Prior failed run:** `6d18c07d` → `422 spec.warmPoolRef: Required value`

## Verdict

> ✅ **The warmPoolRef 422 fix WORKED.**
> ❌ **The run still FAILS — new, unrelated blocker:** the AgentHost pod binds **IPv6-only** (`[::]:8088`) and is unreachable on its IPv4 podIP, so the `/healthz` readiness probe times out forever. The executor kills the pod after the readiness timeout and retries → **infinite provisioning retry loop.**

## 1. Did the SandboxClaim 422 error disappear? — YES

The claim is now created successfully with the `warmPoolRef` populated. No `422` anywhere in API logs.

```
agent-9a8b1f7053d5: created=2026-06-30T11:00:41Z phase=... ready=None
  reason=None sandbox={'name':'agent-9a8b1f7053d5','podIPs':['10.244.6.185']}
  warmPoolRef={'name': 'agentweaver-agent-host'}   <-- fix present
  cond Ready=True DependenciesReady 2026-06-30T11:00:46Z
```

## 2. Was a SandboxClaim created and bound? — YES

- `SandboxProvisioned`: Created Sandbox `agent-9a8b1f7053d5`
- API: `AgentHost claim agent-9a8b1f7053d5 bound to pod agent-9a8b1f7053d5`
- Pod ran image `agentweaverregistry.azurecr.io/agentweaver-agent-host:89b40e2` (the fix commit) ✓

## 3. Were agent-host pods provisioned? — YES (then killed)

- Pod `agent-9a8b1f7053d5` Scheduled + Started, image pulled in 225ms, `1/1 Running`.
- After ~90s of failed readiness polling it was **Killed**; a fresh retry pod `agent-a82dfc5cc332` (same image) was provisioned and is repeating the same loop.

## 4. Did the run complete or fail? — FAIL (stuck in retry loop)

API repeatedly polls `GET http://10.244.6.185:8088/healthz` every 6s starting 11:00:47, never succeeding:

```
11:00:47 ... waiting for AgentHost readiness ... at http://10.244.6.185:8088/healthz
11:00:47..11:01:53 (and on) Sending HTTP request GET .../healthz   (every 6s, no success)
```

### Root cause (confirmed)

The AgentHost Kestrel binds **IPv6 wildcard only**:

```
Overriding address(es) 'http://*:8080'. Binding to endpoints defined via IConfiguration...
Now listening on: http://[::]:8088
AgentHost in standby mode — waiting for /configure (warm pool, no RunId injected).
```

Listening-socket dump on the live pod proves there is **no IPv4 listener**:

```
/proc/net/tcp  (IPv4):  <no entry for port 1F98/8088>
/proc/net/tcp6 (IPv6):  0000...0000:1F98  state 0A (LISTEN)   <-- IPv6 ANY only
```

The cluster is single-stack IPv4 (podIPs = `['10.244.6.185']` / `10.244.6.159`). The API's
`KubernetesPodAgentEndpointResolver` dials the pod's **IPv4 podIP**, so the probe gets connection-timeout:

```
curl http://10.244.6.185:8088/healthz  -> HTTP_000  (Connection timed out after 5002 ms)
curl http://10.244.6.159:8088/healthz  -> HTTP_000  (retry pod, same result)
```

Because `[::]` binds IPv6-only on this node, the IPv4 podIP is never reachable, `/configure` is
never called, readiness never flips, and the executor recycles the pod and retries indefinitely.

## 5. Provisioning time

Claim → bound was fast: **~5–6s** (created 11:00:41Z, Ready/bound 11:00:46Z; image pull 225ms).
Provisioning is healthy; the failure is purely the post-provision IPv4 readiness probe.

## 6. Warm pool status — healthy / replenishing

```
agentweaver-agent-host   READY 2   (28h)
agentweaver-sandbox      READY 3   (28h)
```

Warm pool maintained desired ready replicas across claim/kill/retry cycles; no warm-pool exhaustion.

---

# UPDATE 2 — UI-driven re-trace (multi-replica + empty session)

Re-traced after Ahmed's UI observations. **Final run outcome: FAILED / blocked at assembly.**
The `warmPoolRef` CRD fix is confirmed working; everything downstream fails on the IPv6 `/healthz`
bug, and the UI weirdness is the multi-replica in-memory state limitation.

## A. SandboxClaim — fix WORKED (confirmed again)

- Claim `agent-9a8b1f7053d5` created with `spec.warmPoolRef={name: agentweaver-agent-host}`
  (NOT `sandboxTemplateRef`), **accepted (no 422)**, bound to pod `agent-9a8b1f7053d5` (podIP `10.244.6.185`).
- The claim was deleted on release at 11:04:08 (cleanup logged a harmless `404 NotFound` — already gone).
- Claims are ephemeral → `kubectl get sandboxclaims` now shows none; that is expected post-run.

## B. Actual run timeline (it FAILED — UI "Running" is stale)

| Time (UTC) | Replica | Event |
|---|---|---|
| 11:00:41 | lgxj6 | Launch AgentHost for `…-coordinator-decompose`, claim bound (warmPoolRef ok) |
| 11:00:47–11:02:22 | lgxj6 | 16 `/healthz` attempts to `10.244.6.185:8088`, all fail → **TimeoutException (90s)** |
| 11:02:22 | lgxj6 | `RemoteAgentProxy: no A2A endpoint found` → decomposition model turn fails → **deterministic fallback** |
| 11:02:23 | lgxj6 | Persisted **work plan 15**, 1 pending subtask (id 17) |
| 11:03:30 | **j8xbs** | `CoordinatorReconciler: re-arming orphaned dispatch` — **lease stolen from lgxj6** |
| 11:00:47–11:04:03 | lgxj6 | Subtask pod `10.244.6.159:8088` `/healthz` polled, same IPv6 failure |
| 11:04:08 | lgxj6 | `dispatch complete: failed=1` → `Collective assembly blocked: ineligible_subtasks` |

Root failure is identical to UPDATE 1: **AgentHost binds IPv6-only (`[::]:8088`), unreachable on the
IPv4 podIP**, so `/healthz` never passes, the A2A endpoint never registers, and the subtask agent
(Deckard) never actually runs.

## C. Multi-replica graph divergence — root cause CONFIRMED

This is a real **per-replica in-memory state** problem, not a DB issue:

- **Shared state (consistent across replicas):** both `ConnectionStrings__MemoryDb` and `__Postgres`
  point to the same Azure Postgres (`agentweaver-pg2 / agentweaver`). Work plan + subtasks + status
  are identical on both pods. `GetWorkPlanAsync` reads this DB.
- **Per-replica in-memory state (divergent):** `RunStreamStore` is a process-local
  `ConcurrentDictionary` (`Infrastructure/RunStreamStore.cs:165`). The live SSE event stream, node
  live-status overlay ("Dispatching", Deckard "Running"), `topology snapshot`, and
  `ICoordinatorDispatch.IsDispatchActive` all live **only on the replica running the dispatch loop**
  (code comment `CoordinatorRunService.cs:868`: *"the coordinator MAF workflow is in-memory on
  whichever replica started it"*).
- **No stickiness:** `kubectl get svc agentweaver-api -o jsonpath={.spec.sessionAffinity}` = **`None`**.
  Every page refresh / SSE connect round-robins to either api pod.

➡️ **Refresh hitting the owning replica → full live graph (goal + planned subtasks + running nodes).
Refresh hitting the other replica → only the bare coordinator node / empty live overlay.** That is
exactly the alternating behavior Ahmed sees.

**Amplified this run:** lgxj6 blocked **90 s** inside the `/healthz` probe, exceeding the
`Coordinator:PodLeaseStaleTtlSeconds` = **60 s** lease window, so j8xbs's reconciler judged the lease
**stale and stole it** (re-arm at 11:03:30). Both replicas then held live in-memory dispatch state for
the same run — a genuine split-brain, not just full-vs-empty. The `CoordinatorPodId` lease
(migration `20260629100000_AddCoordinatorPodId`) is meant to prevent exactly this, but the lease TTL
(60 s) is **shorter than the worst-case healthz block (90 s)**, so a slow probe defeats it.

## D. "Waiting for agent" empty session — explained

Two compounding causes, both consistent with the above:
1. **Primary:** the subtask agent never started — its AgentHost pod's `/healthz` never came up, so
   `RemoteAgentProxy: no A2A endpoint found for …-coordinator-decompose`. No agent ⇒ no session
   stream ⇒ "Waiting for agent". Deckard showing "Running" in the graph is **stale in-memory status**;
   the subtask actually went `failed` (dispatch `failed=1`).
2. **Secondary:** even the live stream is replica-local, so an SSE connection landing on the
   non-owning api pod has no `RunStreamStore` entry to read and renders empty regardless.

## Answers to the three asks

1. **SandboxClaim:** created with `warmPoolRef` (name=`agentweaver-agent-host`), accepted (no 422),
   bound to pod `agent-9a8b1f7053d5` (`10.244.6.185`). ✅ Fix verified.
2. **Graph divergence:** YES — a known multi-replica limitation. Live run/coordinator state is
   in-memory per replica (`RunStreamStore`, dispatch loop, topology snapshot); DB is shared but the
   live overlay/SSE is not, and `sessionAffinity=None` round-robins refreshes. Worsened by lease theft
   (90 s healthz block > 60 s lease TTL).
3. **Empty session stream:** the subtask agent never started (IPv6 `/healthz` ⇒ no A2A endpoint), so
   there is nothing to stream; the replica-local SSE store makes it empty on the non-owning pod too.
   Deckard "Running" is stale; the subtask actually failed and the run is blocked at assembly.

## Recommendation (next fix)

Make the AgentHost listen on IPv4 (or dual-stack), not IPv6-only. The endpoint override is
producing `http://[::]:8088`; change it to bind `http://0.0.0.0:8088` (or `http://*:8088`, or
configure dual-stack `[::]` with IPv4-mapped support) so the API can reach `/healthz` on the
IPv4 podIP. Investigate the `IConfiguration`/`UseKestrel` value that resolves to `[::]:8088`
(it overrides the default `http://*:8080`). This is a runtime/networking bug, **separate from**
the warmPoolRef CRD fix, which is verified working.

### Secondary (multi-replica) recommendations
- **Stop-gap:** set the api Service `sessionAffinity: ClientIP` so a user's refreshes/SSE stick to
  one replica (hides the divergence; does not fix split-brain).
- **Lease vs probe TTL:** raise `Coordinator:PodLeaseStaleTtlSeconds` above the worst-case AgentHost
  readiness timeout (probe is 90 s; lease is 60 s) **or** have the dispatch loop heartbeat the
  `WorkPlan.UpdatedAt` lease while blocked on a long probe, so a slow `/healthz` can't cause lease
  theft / dual-dispatch.
- **Real fix:** serve the live run graph/SSE from shared state (or proxy SSE to the owning replica
  identified by `WorkPlan.CoordinatorPodId`) instead of the per-replica in-memory `RunStreamStore`.

---

# UPDATE 3 — Child run a82dfc5c "Streaming / No changes" (DB-confirmed)

`a82dfc5c-c332-473c-b75c-48a717e1e9e9` is the child (Deckard, subtask 17) for the parent run. The UI
shows SSE open ("Streaming") but zero agent output. **Confirmed cause: hypothesis (a) — the
agent-host pod was Running but NEVER received its task payload via A2A; the agent never executed.**

## Child timeline

| Time (UTC) | Event |
|---|---|
| 11:02:24 | `[workflow:a82dfc5c] agent(Deckard) → started` — workflow node starts, SSE stream opens |
| 11:02:24 | Launch AgentHost for `a82dfc5c` via claim `agent-a82dfc5cc332` |
| 11:02:32 | Claim bound to pod `agent-a82dfc5cc332` (`10.244.6.159`) |
| 11:02:33→ | `/healthz` polled on `10.244.6.159:8088` — never ready (IPv6-only bind, same bug) |
| 11:04:08 | lgxj6 finalizes: subtask `failed=1`; child `run.failed` persisted (seq 4) |
| 11:08:30 | **j8xbs** (lease-stealer) independently hits 5-min stall TTL: "child a82dfc5c emitted no event within stall TTL (00:05:00); treating as stalled" → `Run transition no-op` (already failed) |

## DB evidence (Postgres `"RunEvents"`)

Only **4** rows exist for `a82dfc5c…`, all lifecycle markers — **zero agent output**:

```
 RunId      | Sequence | EventType          | CreatedAt
 a82dfc5c…  | 1        | run.workflow_graph | 11:04:08   (graph descriptor)
 a82dfc5c…  | 2        | workflow.step      | 11:02:24   (Deckard → started)
 a82dfc5c…  | 3        | workflow.step      | 11:04:08   (step → failed)
 a82dfc5c…  | 4        | run.failed         | 11:04:08
```

No `agent.message`, no token deltas, no tool events — the agent (Deckard/Copilot) produced **nothing**.
(For contrast the parent `9a8b1f70…` stream has 236 events — coordinator drafting etc.)

## Answer to the hypothesis: (a), not (b)

- **(a) CONFIRMED:** the agent-host pod is in **standby waiting for `/configure`** (warm-pool pod, no
  RunId injected). The API never finished the `/healthz` readiness gate (IPv6 bind unreachable on the
  IPv4 podIP), so it **never POSTed `/configure`** with the task payload and never opened the A2A task
  channel. The agent had nothing to run → zero events. "Streaming/No changes" = SSE open + only the
  workflow `step started` marker, then a 4-minute gap to `run.failed`.
- **(b) RULED OUT:** output is not being produced-then-dropped. The DB has zero agent events; the
  event-write path is fine. There is simply no agent output because the agent never started.

## Note: redundant stall-fail = split-brain symptom

The child was failed twice: lgxj6's dispatch finalized it at 11:04:08, while j8xbs (which stole the
dispatch lease at 11:03:30 during lgxj6's 90 s probe block) kept its own in-memory observation of
subtask 17 and only gave up at the 5-min stall TTL (11:08:30), then found the run already terminal
(`Run transition no-op`). Same lease-TTL-vs-probe-timeout race called out in UPDATE 2 — harmless here
(CAS no-op) but it confirms both replicas were managing the same child.

## Net conclusion for the run

The `warmPoolRef` v0.5.0 fix is **verified working** (claims bind, no 422). The run still **fails**,
and all three UI symptoms (stale "Running"/"Dispatching", alternating graph, empty "Waiting for
agent"/"Streaming No changes" sessions) trace to **one runtime bug: AgentHost binds IPv6-only
(`[::]:8088`) and is unreachable on its IPv4 podIP**, compounded by the per-replica in-memory stream +
`sessionAffinity=None` + lease-theft. Fix the bind first; the rest are mitigations.

---

# UPDATE 4 — Definitive failure reasons (DB `runs.result`)

Both runs are now terminal. Queried the Postgres run store directly:

| run_id | agent | status | result | started → ended |
|---|---|---|---|---|
| `9a8b1f70…` | Coordinator (parent) | **failed** | **`assembly_blocked: ineligible_subtasks`** | 11:00:13 → 11:04:08 |
| `a82dfc5c…` | Deckard (child, subtask 17) | **failed** | **`watch_stream_completed_without_terminal_event`** | 11:02:24 → 11:04:08 |

Child `"RunEvents"` payloads (4 rows, zero agent output) confirm the mechanism:
```
seq2 workflow.step {"step":"agent","status":"started","agent_name":"Deckard"}   11:02:24
seq3 workflow.step {"step":"agent","status":"failed"}                            11:04:08
seq4 run.failed    {"reason":"watch_stream_completed_without_terminal_event"}    11:04:08
```
No `agent.message`, token, or tool events — the agent emitted nothing.

## Key question answered: the agent-host NEVER received its task

**The A2A connection was never established; the agent never started executing.** It did **not** crash,
OOM, or fail mid-run. Evidence:
- agent-host pod logs: stuck in `standby mode — waiting for /configure (warm pool, no RunId injected)`
  — it never received the configure/task call.
- API never cleared the `/healthz` readiness gate (IPv6-only `[::]:8088` bind unreachable on the IPv4
  podIP), so it never POSTed `/configure` and never opened the A2A task channel.
- `watch_stream_completed_without_terminal_event` = the child's watch loop saw its stream close
  (pod released after the readiness/stall failure) **without any terminal event from the agent**,
  because the agent never produced one.

## Failure chain (root → surface)

1. **Root:** AgentHost binds IPv6-only (`[::]:8088`) → `/healthz` unreachable on IPv4 podIP.
2. Readiness probe times out (90 s) → `/configure` never sent → **A2A task never delivered**.
3. Child watch stream closes with no terminal event → child fails
   `watch_stream_completed_without_terminal_event`.
4. The sole subtask (17) is now failed/ineligible → parent collective assembly has nothing to
   assemble → parent fails `assembly_blocked: ineligible_subtasks`.

Net: **run failed; single root cause is the IPv6-only AgentHost bind.** The `warmPoolRef` v0.5.0 fix
remains verified-working (claims bound, no 422) and is not implicated in this failure.

## 2026-07-05: 2026-06-30T02-09-43: AgentHost warm pool: deferred /configure, runtime KV token fetch, replicas:2

**Source:** decisions/inbox/Morpheus-agenthost-warm-pool-deferred-configure-runtime-kv-.md  
**Merged by:** Scribe  

### 2026-06-30T02-09-43: AgentHost warm pool: deferred /configure, runtime KV token fetch, replicas:2
**By:** Morpheus
**What:** AgentHost warm pool: deferred /configure, runtime KV token fetch, replicas:2
**References:** apps/Agentweaver.AgentHost/Program.cs, apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs, apps/Agentweaver.AgentHost/KeyVaultUserTokenProvider.cs, apps/Agentweaver.AgentHost/AgentHostStartupService.cs, apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs, k8s/sandbox-warmpool-agenthost.yaml, k8s/sandbox-template-agenthost.yaml
**Why:** ## Decision: AgentHost warm-pool with deferred /configure

AgentHost pods are now pre-warmed (replicas: 2) instead of cold-starting at run-launch. Per-run context (RunId/UserId/TurnBearerToken) and the user's GitHub token are delivered AFTER the pod is already running and Copilot-SDK-initialized, eliminating cold-start latency on the critical path.

### /configure contract
- Endpoint: `POST http://{podIp}:{port}/configure`
- Body: `{ runId, userId, turnBearerToken, kvUserSecretName? }`
- One-time semantics enforced via `Interlocked.CompareExchange` on an int flag (0=unconfigured -> 1=configured) in `AgentHostRuntimeState`.
- Responses: 400 (empty/missing RunId), 409 "Already configured" (second call), 409 "Already configured via env" (pod launched with RunId env var — non-warm path), 200 on success.
- NOT protected by TurnBearerToken (chicken-and-egg: the token is delivered BY this call). The guard is the existing NetworkPolicy restricting ingress to AgentHost pods to API/worker only.
- Excluded from the readiness gate so a standby pod (no RunId yet) can still accept /configure while reporting not-ready to A2A traffic.
- On success: mutates `AgentHostRuntimeState`, calls `AgentHostStartupService.ConfigureAsync(...)` (deferred SetupAsync), then flips `_ready=true`.

### Standby mode
`AgentHostStartupService` no longer throws when RunId is empty — it logs "AgentHost in standby mode — waiting for /configure", sets `_standby=true`, and returns. The env-launch path (RunId present) still runs SetupAsync at startup unchanged (backward compat for `AgentExecutionMode=in-api` / direct pod launch).

### Runtime KV token fetch (Option C)
- New `KeyVaultUserTokenProvider` fetches the run owner's GitHub token from Key Vault via `SecretClient` + `DefaultAzureCredential` (workload identity) at configure-time, caching it for the pod lifetime.
- Secret name `ghtok-user--{base32(userId)}` is derived by the executor (reusing `KeyVaultSecretStore.SanitizeKey`) and passed in the /configure body — the pod only ever fetches ITS configured user's secret.
- Token JSON format is identical to the CSI file mount (StoredCredential: Status/AccessToken/RefreshToken/ExpiresAt/Login/...), so `SharedHomeGitHubTokenStore` semantics are preserved.
- Wiring: when `AgentHost:KeyVaultUri` is set, Program.cs registers SecretClient + KeyVaultUserTokenProvider + KeyVaultGitHubTokenStore + RuntimeUserScopeProvider (highest priority). The legacy `KvTokenMountPath` CSI file-mount path still works for backward compat.

### Executor changes
- Removed per-run SecretProviderClass, per-run SandboxTemplate, and per-run WarmPool creation. The executor now submits a claim against the shared `agentweaver-agent-host` warm pool (replicas: 2).
- Claim env injection keeps only static config (WorkingDirectory/RepositoryPath/A2APath/RequireMtls/Port) + `AgentHost__KeyVaultUri`. RunId/UserId/TurnBearerToken are REMOVED from env (now via /configure).
- After claim binds + readiness probe passes, executor calls `CallAgentHostConfigureAsync` (PostAsJsonAsync via the existing "a2a-sandbox-pod" named HttpClient).
- `AgentHost__KeyVaultUri` is injected BOTH at claim time AND in the template YAML (`${AGENTHOST_KEYVAULT_URI}`) because a truly pre-warmed pod boots from the template, not the claim.

### Security properties maintained
- /configure is one-time (409 on repeat).
- NetworkPolicy ingress restriction remains the auth guard for /configure.
- TurnBearerToken still required on `message:stream` — the bearer middleware now reads it from `AgentHostRuntimeState.TurnBearerToken` (set by /configure) rather than the immutable options.
- Each pod fetches ONLY its configured user's KV secret.

### Trade-offs vs per-pod SPC (CSI mount)
- (+) No per-run K8s resource churn (SPC/template/warmpool create+reap eliminated) — less API-server load, simpler reaper.
- (+) Cold-start latency moved off the run-launch critical path (pods pre-warmed).
- (+) No CSI volume mount; token fetched on demand via workload identity.
- (-) Adds a synchronous HTTP /configure round-trip at launch (mitigated: pod already warm).
- (-) Pod holds the decrypted token in memory for its lifetime (same exposure as the file mount; bounded by pod lifetime).
- (-) Introduces mutable runtime state (`AgentHostRuntimeState`) alongside immutable `AgentHostOptions`.

### k8s
- `sandbox-warmpool-agenthost.yaml`: replicas 0 -> 2 (pre-warming now safe — no RunId crash-loop).
- `sandbox-template-agenthost.yaml`: removed per-run CSI SPC volume+mount; pod uses workload identity to reach KV; `AgentHost:KeyVaultUri` env retained.

## 2026-07-05: Morpheus: assembly_blocked latch recovery

**Source:** decisions/inbox/morpheus-assembly-blocked-latch.md  
**Merged by:** Scribe  

# Morpheus: assembly_blocked latch recovery

## Root cause
Coordinator assembly latched `WorkPlanStatus.AssemblyBlocked` from an `ineligible_subtasks` verdict while recovery/re-arm was still reconciling child state. Later, all children could reach `assemble_ready`/`completed`, but dispatch finalization treated `assembly_blocked` as already owned by Phase 3 and skipped the handoff. Reconciler did not scan `assembly_blocked`, so the plan and run stayed dead-ended.

## Decision
Do not treat not-yet-ready subtasks as a permanent assembly block. Assembly now only blocks on terminally ineligible subtasks (`failed`, `blocked`, `rai_flagged`). If a stale `assembly_blocked` plan is observed and durable subtask state is now all assembly-eligible, clear it via a guarded CAS back to `awaiting_assembly`, mark the coordinator in progress, and re-run assembly. Dispatch finalization also clears stale `assembly_blocked` when all terminal child state is eligible, and the reconciler now re-arms `assembly_blocked` plans so orphan recovery re-drives the decision.

## Safety
All state transitions are durable/CAS guarded. Fresh `assembling` and `in_review` ownership rules remain unchanged to avoid duplicate git integration/merge work across pods. Real terminal ineligible child states still enter the blocked steering path.

## 2026-07-05: Warm Pool Fix Monitor — cc7dd9d Verification

**Source:** decisions/inbox/morpheus-monitor-result.md  
**Merged by:** Scribe  

# Warm Pool Fix Monitor — cc7dd9d Verification
**Date:** 2026-06-30T16:55Z  
**Monitored by:** Morpheus (Runtime/Trace)  
**Fix commit:** cc7dd9d

---

## Summary

**cc7dd9d IS correctly deployed and routing claims through the warm pool.** However, both observed runs failed because the pre-warmed pods were already in `Terminating` state at the moment they were assigned — a warm-pool pod rotation race condition unrelated to the fix itself.

---

## Run 1 — 640c7d1a

| Signal | Value | Verdict |
|--------|-------|---------|
| Claim name | `agent-640c7d1af19d` | — |
| `spec.env` present | **No** | ✅ |
| `spec.warmPoolRef` | `{name: agentweaver-agent-host}` | ✅ |
| `status.sandbox.name` | `agentweaver-agent-host-snm6z` (pre-warmed) | ✅ |
| Claim created | 16:52:15Z | — |
| Claim bound | 16:52:17Z | ✅ **2s bind time** |
| New kata pod created | No new pod for this run | ✅ |
| `/healthz` outcome | **TIMEOUT** — 90s, 16 attempts | ❌ |
| Root cause | `snm6z` was already `Terminating` when assigned | ❌ |
| Run outcome | **Failed** — fell back to deterministic decomposition | ❌ |

---

## Run 2 — eba3c0d3 (child of 640c7d1a coordinator)

| Signal | Value | Verdict |
|--------|-------|---------|
| Claim name | `agent-eba3c0d37ee9` | — |
| `spec.env` present | **No** | ✅ |
| `spec.warmPoolRef` | `{name: agentweaver-agent-host}` | ✅ |
| `status.sandbox.name` | `agentweaver-agent-host-855zc` (pre-warmed) | ✅ |
| Claim created | 16:53:54Z | — |
| Claim bound | 16:53:56Z | ✅ **2s bind time** |
| New kata pod created | No new pod for this run | ✅ |
| `/healthz` outcome | **TIMEOUT** — 90s, 16 attempts | ❌ |
| Root cause | `855zc` was already `Terminating` when assigned | ❌ |
| Run outcome | **Failed** — agent turn failed, run → Failed state | ❌ |

---

## Root Cause of Failures

Both failures share the same root cause: **the warm pool pods were mid-rotation when the claims arrived.**

Timeline:
- `agentweaver-agent-host-4gnm6` created at ~16:52:00 (15s BEFORE first claim) — controller was already cycling out old pods
- `agentweaver-agent-host-snm6z` (5h18m old) was `Terminating` but still appeared in the sandbox list
- Claim `agent-640c7d1af19d` was assigned to the terminating `snm6z`
- Same pattern repeated for `855zc` → `agent-eba3c0d37ee9`

**This is a separate bug from cc7dd9d.** The warm pool controller assigns claims to sandboxes that are already terminating. The controller needs to filter out `Terminating` pods from the eligible pool.

---

## Warm Pool State After Runs

| Pod | Age at observation | Status |
|-----|-------------------|--------|
| `agentweaver-agent-host-4gnm6` | 3m27s | Running (new) |
| `agentweaver-agent-host-ffjbh` | 107s | Running (new) |
| `agentweaver-agent-host-snm6z` | terminated | Gone |
| `agentweaver-agent-host-855zc` | terminated | Gone |

The pool has replenished with two fresh pods. Future claims against these should succeed.

---

## Verdict on cc7dd9d Fix

| Check | Result |
|-------|--------|
| Claims have no `spec.env` | ✅ Fix deployed correctly |
| Claims use `warmPoolRef` | ✅ |
| Warm pool pods assigned (not new `agent-{runid}` pods) | ✅ |
| Bind time < 10s | ✅ 2s in both cases |
| `/healthz` succeeded | ❌ Both timed out (pods rotating) |
| Runs completed | ❌ Both failed |

**cc7dd9d fix: VERIFIED DEPLOYED.** The routing logic is correct. The failures are caused by a warm-pool pod rotation race condition that needs a separate fix.

---

## Recommended Action

**New bug:** Warm pool controller must skip sandboxes whose backing pod is in `Terminating` phase when evaluating claim candidates. A claim bound to a terminating pod will always time out on `/healthz`.

Consider adding a pre-bind check: only assign a claim if the sandbox pod `status.phase == Running` AND no `deletionTimestamp` is set on the pod.

## 2026-07-05: Morpheus null-user Copilot audit

**Source:** decisions/inbox/morpheus-null-user-audit.md  
**Merged by:** Scribe  

# Morpheus null-user Copilot audit

Date: 2026-07-05T18:26:05-07:00
Branch: squad/wf-selection-empty-response

Decision: Copilot model turns must fail closed when either the submitting user is missing or an IGitHubTokenScopeProvider resolves GitHubTokenScope.Installation. Installation tokens remain valid only for non-model GitHub app operations such as diagnostics/repository access checks.

Fixes recorded in this pass:
- Threaded AgentTurnInput.SubmittingUser into AgentTurnOutput so downstream RAI and Rubberduck model turns use the accountable human token.
- Threaded coordinator SubmittingUser through CollectiveRaiRequest for aggregate RAI.
- Added installation-scope guards in CopilotAIAgent, GitHubCopilotAgentRunner, and CopilotWorkflowSelectionModel.
- Changed AgentHost user-scope providers to fail closed instead of silently returning installation scope when no user identity is configured.

## 2026-07-05: Morpheus decision: preview liveness and collective review state

**Source:** decisions/inbox/morpheus-preview-and-review-gates.md  
**Merged by:** Scribe  

# Morpheus decision: preview liveness and collective review state

- Preview availability is now advertised to agents as an explicit `start_preview(PORT)` action when sandbox preview is enabled, and the API only reports a preview as active after a real TCP probe to the sandbox pod confirms the target port is accepting connections.
- Coordinator runs now move to `awaiting_review` while the collective assembly review gate is open, return to `in_progress` only after a decision is received, and resume from `awaiting_review` during restart recovery.

## Rationale

These changes align runtime behavior with the actual contracts:

1. The preview flow is agent-initiated via `start_preview`, so the run-start prompt must say that clearly.
2. Preview state must reflect a live listener, not just the existence of a route or pod.
3. The collective review gate is a human wait state, so the coordinator must remain non-terminal until the gate is resolved; otherwise approvals race a completed run and become ineffective.

## 2026-07-05: SandboxClaim v1beta1 `warmPoolRef` fix — applied

**Source:** decisions/inbox/morpheus-sandboxclaim-fix.md  
**Merged by:** Scribe  

### SandboxClaim v1beta1 `warmPoolRef` fix — applied

**Author:** Morpheus (Runtime Engineer) — 2026-06-30
**Commit:** `89b40e2` on `main`
**Fixes:** run `6d18c07d` failure (subtask-dispatch / AgentHost pod provisioning) — see
`morpheus-trace-6d18c07d.md`. Reverts the body change from `a731f70`.

#### Root cause (recap)
Deployed agent-sandbox controller is **v0.5.0** (latest). Its **v1beta1** SandboxClaim CRD
requires `spec.warmPoolRef.{name}`. Commit `a731f70` switched the claim body to the
**deprecated v0.4.x / v1alpha1** shape (`sandboxTemplateRef` + `warmpool` string) while still
posting to v1beta1. The API server pruned those unknown fields, leaving `warmPoolRef` missing
→ HTTP 422 `spec.warmPoolRef: Required value`. No AgentHost pod could be provisioned.

#### Changes

`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`
- `CreateAgentHostClaimAsync`: spec body now emits `warmPoolRef = new { name = warmPoolName }`
  (removed `sandboxTemplateRef` + `warmpool`).
- `CreateClaimAsync` (generic command-exec): spec body now emits
  `warmPoolRef = new { name = _options.WarmPoolRef }` (removed `sandboxTemplateRef` + `warmpool`).
- Removed the now-unused `KubernetesSandboxOptions.AgentHostTemplateRef` option (it was only ever
  used to populate the deprecated `sandboxTemplateRef` field; it is not config-bound anywhere).
  `TemplateRef` is retained — it is still config-bound and used as the default for `WarmPoolRef`.
- Replaced the inverted comments ("warmPoolRef was the wrong/old field name") with accurate
  context: "v0.5.0 v1beta1 SandboxClaimSpec: spec.warmPoolRef.name references the SandboxWarmPool
  to bind from. sandboxTemplateRef+warmpool were the v0.4.x/v1alpha1 deprecated fields."

`tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs`
- Reverted assertions: both claim paths now assert `spec.warmPoolRef.name` is present and that
  `sandboxTemplateRef` / `warmpool` are absent.
- Renamed `CreateClaim_generic_posts_v1beta1_sandboxTemplateRef_and_warmpool_body`
  → `CreateClaim_generic_posts_v1beta1_warmPoolRef_body`.
- Updated the class doc comment to describe the v0.5.0 v1beta1 contract accurately.

#### Field shape per version (reference)
| agent-sandbox | API version | SandboxClaim spec.required | fields |
|---|---|---|---|
| v0.4.6 (old) | v1alpha1 only | `sandboxTemplateRef` | `sandboxTemplateRef:{name}` + `warmpool:(string)` |
| **v0.5.0 (DEPLOYED)** | **v1beta1 (storage)** | **`warmPoolRef`** | **`warmPoolRef:{name}`** |
| v0.5.0 | v1alpha1 (deprecated) | `sandboxTemplateRef` | `sandboxTemplateRef` + `warmpool` |

#### Validation
- `dotnet test ... --filter SandboxExecutorClaim` → **4/4 passed**.
- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release` → **succeeded, 0 warnings / 0 errors**.

#### Deploy
Dispatched to **Link** (`link-1`): build the **api** image from `89b40e2`; retag the unchanged
images (frontend, mcp, agent-host) from `006457a`; deploy to AKS (agentweaver-aks-2 / namespace
agentweaver) and push; confirm api rollout (2 replicas Ready). Awaiting Link's report of the new
image tag/digest and rollout status.

#### Follow-up (separate, not in this commit)
- RE-INVESTIGATE `a731f70`'s original "cold kata pod / 90s healthz timeout" symptom. With v0.5.0,
  `warmPoolRef` is the valid binding mechanism, so that symptom was almost certainly a different
  root cause (warm pool not Ready, wrong `warmPoolRef.name`, or warm-pool selection) — NOT the
  field name. Both warm pools are currently Ready (`agentweaver-agent-host` READY=2,
  `agentweaver-sandbox` READY=3), so binding should now succeed; verify on a real run post-deploy.
- Add a CI integration test that creates a SandboxClaim against the served v1beta1 schema to catch
  CRD-vs-code drift before deploy.
- (Non-fatal) Fix MAF checkpoint marshalling so `$type` polymorphic metadata round-trips, removing
  the on-demand resume DB-deferral fallback observed during the 6d18c07d trace.

## 2026-07-05: Run 6d18c07d failure trace

**Source:** decisions/inbox/morpheus-trace-6d18c07d.md  
**Merged by:** Scribe  

### Run 6d18c07d failure trace

**Run:** `6d18c07d-1e47-4ec4-95aa-815f4332a008` (coordinator) — submitted by sabbour
**Traced by:** Morpheus (Runtime Engineer) — 2026-06-30
**Cluster:** agentweaver-aks-2 / namespace `agentweaver`

**Phase failed:** subtask-dispatch (AgentHost pod provisioning during `coordinator-decompose`)

**Root cause:**
Code/CRD schema mismatch on the `SandboxClaim` resource. The deployed CRD
`sandboxclaims.extensions.agents.x-k8s.io` (served version **v1beta1**) REQUIRES
`spec.warmPoolRef` (an object `{ name }`) and exposes only these spec properties:
`additionalPodMetadata, env, lifecycle, volumeClaimTemplates, warmPoolRef`.

The runtime executor `KubernetesSandboxExecutor.CreateAgentHostClaimAsync`
(`apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:478-490`) instead builds the
claim spec with `sandboxTemplateRef` + `warmpool` (a string), and does **not** set
`warmPoolRef`. An inline comment (lines 480-483) asserts "warmPoolRef was the wrong/old
field name" — that assumption is **false for the CRD version actually installed in this
cluster**. The unknown fields are pruned by the API server, leaving the spec with no
required `warmPoolRef`, so claim creation is rejected with HTTP 422.

Because the AgentHost claim is never created, no pod binds, `RemoteAgentProxy` finds no
A2A endpoint, model-based decomposition falls back to a deterministic plan, the lone
subtask is marked failed, and collective assembly is blocked.

**Error (exact):**
```
k8s.Autorest.HttpOperationException: Operation returned an invalid status code 'UnprocessableEntity'
{"status":"Failure","message":"SandboxClaim.extensions.agents.x-k8s.io \"agent-6d18c07d1e47\"
 is invalid: spec.warmPoolRef: Required value","reason":"Invalid",
 "details":{"causes":[{"reason":"FieldValueRequired","message":"Required value","field":"spec.warmPoolRef"}]},
 "code":422}
```
Cascading exception:
```
System.InvalidOperationException: RemoteAgentProxy: no A2A endpoint found for run
'6d18c07d-1e47-4ec4-95aa-815f4332a008-coordinator-decompose'. The sandbox pod may not yet be bound...
```

**Evidence:**
- Worker pod `agentweaver-worker-596f5f69ff-kx7cg` 10:30:55 —
  `KubernetesPodAgentEndpointResolver: failed to launch AgentHost pod ... 422 spec.warmPoolRef: Required value`
  (origin: `KubernetesSandboxExecutor.LaunchAgentHostPodAsync:337`).
- Same pod 10:30:55 — `Coordinator decomposition model turn failed ... using deterministic fallback`
  (`InvalidOperationException: RemoteAgentProxy: no A2A endpoint found`).
- 10:31:01 — `Coordinator dispatch complete for run ...: failed=1. Handing off to Phase 3 assembly.`
- 10:31:01 — `Collective assembly blocked for run ...: ineligible_subtasks`.
- 10:31:01 — best-effort claim delete `404 NotFound` for `agent-6d18c07d1e47` (proves the claim was never created).
- Cluster state: `kubectl get sandboxclaims -n agentweaver` → none; CRD `v1beta1` `spec.required = ["warmPoolRef"]`;
  spec has no `warmpool`/`sandboxTemplateRef` fields. Warm pool `agentweaver-agent-host` exists and is READY=2.
- Source: `KubernetesSandboxExecutor.cs:484-485` sends `sandboxTemplateRef` + `warmpool`, omits `warmPoolRef`.

**Secondary (non-fatal) issue — checkpoint restore:**
On-demand decision resume on the non-primary API replica threw, but was correctly deferred to the DB and
recovered (run still progressed), so it did **not** cause the failure:
```
System.Text.Json.JsonException: The metadata property is either not supported by the type or is not the
first property in the deserialized JSON object. Path: $.workflow.edges.coordinator-draft[0].$type
  at ...JsonMarshaller.Marshal ... CoordinatorWorkflowFactory.ResumeAsync:175
```
This indicates MAF workflow checkpoint JSON (polymorphic `$type` metadata ordering) cannot be re-hydrated
by `JsonMarshaller`. Worth a separate fix so multi-replica on-demand resume works without DB deferral,
but it is not the root cause of this run's failure.

**Recommended fix:** ⚠️ ON HOLD — version-mismatch decision required (do NOT blindly revert `a731f70`).

**RESOLVED via upstream version check — `a731f70` migrated the field in the WRONG direction.**

Deployed agent-sandbox controller: **`registry.k8s.io/agent-sandbox/agent-sandbox-controller:v0.5.0`**
(namespace `agent-sandbox-system`) — this is the **latest** upstream release (kubernetes-sigs/agent-sandbox,
published 2026-06-24). The cluster is NOT outdated.

v0.5.0 release notes — **Breaking Changes / Action Required**:
> **SandboxClaim `spec.templateRef` Replaced by `spec.warmpoolRef`** — The SandboxClaim API no longer uses
> `spec.templateRef` or the `warmpool` policy field. Instead, claims must explicitly point to a
> `SandboxWarmPool` using `spec.warmpoolRef`. For a cold start, create a `SandboxWarmPool` with `replicas: 0`.

(The CRD's authoritative camelCase field name is `warmPoolRef`; the release prose writes `warmpoolRef`.)

SandboxClaim spec schema per version (verified from upstream `extensions.yaml` AND the live CRD):

| agent-sandbox version | API version | SandboxClaim spec.required | fields |
|----|----|----|----|
| **v0.4.6** (previous) | `v1alpha1` only | `sandboxTemplateRef` | `sandboxTemplateRef:{name}` + `warmpool:(string)` |
| **v0.5.0** (DEPLOYED, latest) | `v1beta1` (storage) | **`warmPoolRef`** | **`warmPoolRef:{name}`** + volumeClaimTemplates |
| **v0.5.0** | `v1alpha1` (deprecated) | `sandboxTemplateRef` | `sandboxTemplateRef:{name}` + `warmpool:(string)` |

So the breaking change v0.4.6 → v0.5.0 is: SandboxClaim `sandboxTemplateRef + warmpool` (OLD/v1alpha1) →
`warmPoolRef` (NEW/v1beta1). The NEW, correct field is **`warmPoolRef`**.

Commit `a731f70` did the OPPOSITE of the migration: it changed the body FROM `warmPoolRef:{name}` (correct
for v0.5.0/v1beta1) TO `sandboxTemplateRef + warmpool` (the DEPRECATED v0.4.x/v1alpha1 shape), while still
posting to `v1beta1`. The commit message's premise ("`warmPoolRef` was the wrong/old field name") was based
on stale v0.4.x docs and is inverted for the deployed v0.5.0 cluster.

**Which version's schema matches the code's current body?** The deprecated v1alpha1 (v0.4.x). The
pre-`a731f70` body (`warmPoolRef:{name}`) matches the current v1beta1 (v0.5.0).

**Decision — change the CODE, not the cluster:**
1. The cluster is already on the latest, correctly-configured v0.5.0. Do **not** downgrade the controller or
   hand-edit the CRD.
2. Update `CreateAgentHostClaimAsync` (and the command-exec path ~line 679) to send the v1beta1 shape:
   ```csharp
   spec = new {
       warmPoolRef = new { name = warmPoolName },   // REQUIRED by v0.5.0 v1beta1
       lifecycle  = new { ttlSecondsAfterFinished = _options.TimeoutSeconds, shutdownPolicy = "Delete" },
       env        = env.ToArray(),
   }
   ```
   This is effectively reverting `a731f70`'s spec-body change. Remove the inverted comment at lines 480-483.
3. RE-INVESTIGATE `a731f70`'s original "cold kata pod / 90s healthz timeout" symptom SEPARATELY. With v0.5.0,
   `warmPoolRef` is the valid binding mechanism, so the cold-pod behavior was almost certainly a different
   root cause (warm pool not Ready, wrong `warmPoolRef.name`, or warm-pool selection) — NOT the field name.
   Both warm pools are currently Ready (`agentweaver-agent-host` READY=2), so binding should now succeed.
4. Add a CI integration test that creates an AgentHost SandboxClaim against the served v1beta1 schema to
   catch CRD-vs-code drift before deploy.

(Separate, non-fatal) Fix MAF checkpoint marshalling so `$type` polymorphic metadata round-trips, removing
the on-demand resume DB-deferral fallback.

## 2026-07-05: Workflow selection auth/capture investigation

**Source:** decisions/inbox/morpheus-wfselect.md  
**Merged by:** Scribe  

# Workflow selection auth/capture investigation

- Selection runs after spec confirmation in `CoordinatorOrchestratorExecutor.OrchestrateAsync` -> `SelectWorkflowAsync`; initial draft already passes `input.SubmittingUser` into `CopilotCoordinatorSpecDrafter`.
- Root cause for v0.7.10 selection defaulting: `CopilotWorkflowSelectionModel` resolved `_scopeProvider.Resolve(null)`, which selects installation scope. Copilot model turns require the submitting user's Copilot-entitled token; installation scope can produce no usable model response, making selection look like a parse failure and default to Generic.
- Local auth probe with a user-scoped token returned valid JSON: `{"selected": "bug-fix", "rationale": "This is a targeted defect where workflow selection incorrectly defaults to Generic due to auth/capture producing no parseable response. The bug-fix workflow's triage → minimal fix → regression coverage → validate stages directly match what's needed."}`. The missing-user/installation path returned null after the new fail-fast guard.
- Fixed identity flow by adding `SubmittingUser` to `WorkflowSelectionContext`, threading `input.SubmittingUser` from coordinator orchestration into selection, and resolving Copilot with `_scopeProvider.Resolve(context.SubmittingUser)` instead of null.
- The separate backlog/spec decompose 500 had the same auth-flow bug: `BacklogDecomposeService.DecomposeAsync` called `CopilotAIAgent.SetupAsync` without `userId`. The endpoint now passes the authenticated caller's user id into decomposition, and the service rejects missing identity rather than using installation auth.

## 2026-07-05: Edit workflows with the generation prompt

**Source:** decisions/inbox/neo-edit-workflows-prompt.md  
**Merged by:** Scribe  

# Edit workflows with the generation prompt

Neo specified a workflows-automation story for applying natural-language edits to existing project workflows and built-in/library workflows, with built-ins producing project-owned derived copies, preview/discard before save, iterative re-editing, and validation before use.

## 2026-07-05: Browser chat control console spec area

**Source:** decisions/inbox/neo-tui-console-spec.md  
**Merged by:** Scribe  

# Browser chat control console spec area

Decision: Place the new browser chat control console story under `specs/mcp-integrations/browser-chat-control-console.md`.

Rationale: The feature is a control-plane chat surface that drives existing Agentweaver project, backlog, workflow, orchestration, and monitoring capabilities through MCP/API-style product actions. It does not introduce a new execution mechanism, so `mcp-integrations` is the best area.

## 2026-07-05: 2026-06-30T20-55-38: Use Postgres RunEvents as the cross-replica run stream relay

**Source:** decisions/inbox/Picard-use-postgres-runevents-as-the-cross-replica-run-st.md  
**Merged by:** Scribe  

### 2026-06-30T20-55-38: Use Postgres RunEvents as the cross-replica run stream relay
**By:** Picard
**What:** Use Postgres RunEvents as the cross-replica run stream relay
**References:** #1 sse-postgres, #3 refresh-404-409, apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs, apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs, apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs
**Why:** The API runs with replicas:2, so live run/SSE events must not rely on per-pod RunStreamStore or per-pod channels. RunStreamEntry now mirrors every recorded event into IRunEventStream, and the Postgres EfRunEventStream subscriber tails the shared RunEvents table from the Last-Event-ID cursor with polling. This lets any API replica serve live/replay SSE for runs executing on any other replica without sticky sessions or scaling down. Outcome-spec/work-plan reads remain in the shared MemoryDbContext/Postgres store; endpoints now briefly read-through expected creation races and confirm retry is idempotent once the spec is already confirmed.

## 2026-07-05: 2026-07-01T00-28-15: Issue #28 assembly review decisions defer through durable shared state

**Source:** decisions/inbox/ralph-issue-28-assembly-review-decisions-defer-through-d.md  
**Merged by:** Scribe  

### 2026-07-01T00-28-15: Issue #28 assembly review decisions defer through durable shared state
**By:** ralph
**What:** Issue #28 assembly review decisions defer through durable shared state
**References:** https://github.com/sabbour/agentweaver/issues/28, https://github.com/sabbour/agentweaver/pull/65, squad/28-assembly-review-replica-decision
**Why:** For bug #28, collective assembly review POSTs can land on a non-owner API replica. If the work plan is durably in_review at assembly stage review, the endpoint now persists the AssemblyReviewDecision in shared DeferredDecisions state; the owner assembly pipeline polls that state, atomically consumes it, and submits it to the armed AssemblyReviewGate. The branch is stacked on PR #65 because both changes use the shared deferred-decision mechanism for cross-replica review gates.

## 2026-07-05: 2026-07-01T00-16-59: Issue #30 review decisions are deferred for owner-replica pickup

**Source:** decisions/inbox/ralph-issue-30-review-decisions-are-deferred-for-owner-r.md  
**Merged by:** Scribe  

### 2026-07-01T00-16-59: Issue #30 review decisions are deferred for owner-replica pickup
**By:** ralph
**What:** Issue #30 review decisions are deferred for owner-replica pickup
**References:** https://github.com/sabbour/agentweaver/issues/30, squad/30-standard-review-replica-resume
**Why:** For bug #30, when a standard run review POST lands on a replica without the live StreamingRun, the endpoint now records a single-use deferred WorkflowReviewDecision in the shared DeferredDecisions table instead of returning conflict. The owner watch loop polls that durable decision after arming the pending review gate, consumes the pending request at-most-once, emits the same review timeline events, and calls SendResponseAsync on the local StreamingRun.

## 2026-07-05: 2026-07-01T00-09-09: Issue #32 stop steering uses durable run state for cross-replica cancellation

**Source:** decisions/inbox/ralph-issue-32-stop-steering-uses-durable-run-state-for-.md  
**Merged by:** Scribe  

### 2026-07-01T00-09-09: Issue #32 stop steering uses durable run state for cross-replica cancellation
**By:** ralph
**What:** Issue #32 stop steering uses durable run state for cross-replica cancellation
**References:** https://github.com/sabbour/agentweaver/issues/32, squad/32-stop-steering-replica-cancel
**Why:** For bug #32, stop steering no longer treats RunWorkflowRegistry.Abandon=false as already terminal. The steering service records a durable run.cancelled event when possible and always CAS-terminalizes targeted child run rows as Failed with result steering_stop. The owning RunWatchLoopService polls for that durable steering_stop marker and abandons its local workflow token, while coordinator dispatch resolves store-terminalized children instead of classifying them as stalled.

## 2026-07-05: 2026-07-01T00-02-10: Issue #34 persists GitHub device-flow state outside replica memory

**Source:** decisions/inbox/Ralph-issue-34-persists-github-device-flow-state-outside.md  
**Merged by:** Scribe  

### 2026-07-01T00-02-10: Issue #34 persists GitHub device-flow state outside replica memory
**By:** Ralph
**What:** Issue #34 persists GitHub device-flow state outside replica memory
**References:** issue #34, Tank, apps/Agentweaver.Api/Auth/GitHubDeviceFlowAuthService.cs, apps/Agentweaver.Api/Auth/IGitHubDeviceFlowStore.cs
**Why:** Implemented issue #34 by replacing GitHubDeviceFlowAuthService's process-local in-flight device-code dictionary with IGitHubDeviceFlowStore. KeyVault deployments use SecretStoreGitHubDeviceFlowStore so start/poll can route across replicas and survive pod restart until expiry/denial/success; non-KeyVault/local deployments use InMemoryGitHubDeviceFlowStore. Terminal poll outcomes and success delete the shared state, while tokens continue to persist only through IGitHubTokenStore. Added a two-service-instance test proving start on one replica and poll on another returns pending via shared flow state.

## 2026-07-05: 2026-06-30T23-59-50: Issue #36 makes Gateway preview session listing and limits replica-safe

**Source:** decisions/inbox/Ralph-issue-36-makes-gateway-preview-session-listing-and.md  
**Merged by:** Scribe  

### 2026-06-30T23-59-50: Issue #36 makes Gateway preview session listing and limits replica-safe
**By:** Ralph
**What:** Issue #36 makes Gateway preview session listing and limits replica-safe
**References:** issue #36, Tank, apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs, apps/Agentweaver.Api/Endpoints/SandboxEndpoints.cs
**Why:** Implemented issue #36 by moving the production preview/list path to cluster-owned HTTPRoute annotations. ISandboxPreviewService now lists active previews for a run from HTTPRoute state, records pod/target-port/started-at annotations when creating previews, and enforces per-run/global preview limits by counting HTTPRoutes instead of process-local dictionaries. The sandbox port-forward list endpoint uses the replica-safe preview listing when Gateway preview is enabled; the legacy kubectl PortForwardService remains the local-dev fallback.

## 2026-07-05: 2026-06-30T23-54-55: Issue #38 invalidates workflow and review-policy registry caches by shared file

**Source:** decisions/inbox/Ralph-issue-38-invalidates-workflow-and-review-policy-re.md  
**Merged by:** Scribe  

### 2026-06-30T23-54-55: Issue #38 invalidates workflow and review-policy registry caches by shared file signature
**By:** Ralph
**What:** Issue #38 invalidates workflow and review-policy registry caches by shared file signature
**References:** issue #38, Tank, apps/Agentweaver.Api/Workflows/WorkflowRegistry.cs, apps/Agentweaver.Api/ReviewPolicies/ReviewPolicyRegistry.cs
**Why:** Implemented issue #38 by making WorkflowRegistry and ReviewPolicyRegistry cache entries versioned with a signature derived from the shared project definition directories. Each replica now rechecks the signature on Get/List/Resolve and reloads when workflow or review-policy files change; Sync also updates the versioned cache. Workflow signatures include AllowedWorkflowIds so blueprint/project policy changes do not reuse stale filtered workflow sets. Added replica-coherence tests with two registry instances observing shared file changes.

## 2026-07-05: 2026-06-30T23-51-05: Issue #39 uses Postgres advisory locks for repository merges

**Source:** decisions/inbox/Ralph-issue-39-uses-postgres-advisory-locks-for-reposito.md  
**Merged by:** Scribe  

### 2026-06-30T23-51-05: Issue #39 uses Postgres advisory locks for repository merges
**By:** Ralph
**What:** Issue #39 uses Postgres advisory locks for repository merges
**References:** issue #39, Morpheus, apps/Agentweaver.Api/Git/RepositoryMergeLock.cs, tests/Agentweaver.Tests/PostgresIntegration/RepositoryMergeLockPostgresTests.cs
**Why:** Implemented issue #39 by making RepositoryMergeLock use Postgres session advisory locks when Database:Provider is postgres/postgresql, while preserving the existing local SemaphoreSlim behavior for SQLite/dev. The advisory lock key is derived from the canonical repository path so merges for the same repo serialize across API replicas and different repos can proceed independently. Added Testcontainers-backed Postgres tests proving two lock instances serialize the same repository and allow different repositories concurrently.

## 2026-07-05: 2026-06-30T23-46-31: Issue #40 uses shared token-refresh leases

**Source:** decisions/inbox/Ralph-issue-40-uses-shared-token-refresh-leases.md  
**Merged by:** Scribe  

### 2026-06-30T23-46-31: Issue #40 uses shared token-refresh leases
**By:** Ralph
**What:** Issue #40 uses shared token-refresh leases
**References:** issue #40, Tank, apps/Agentweaver.Api/Auth/GitHubTokenRefreshService.cs, apps/Agentweaver.Api/Auth/KeyVaultGitHubTokenStore.cs
**Why:** Implemented issue #40 by routing GitHub token refresh through a per-scope refresh lease when the token store supports it. KeyVaultGitHubTokenStore now exposes a secret-backed lease, CachingGitHubTokenStore delegates lease acquisition and does not cache tokens past their refresh boundary, and GitHubTokenRefreshService waits for a replica winner before retrying or signing out. This prevents refresh-token double-use across API replicas and lets losing replicas observe the rotated token.

## 2026-07-05: 2026-07-01T01-01-08: Ralph Fleet lanes must use dedicated git worktrees

**Source:** decisions/inbox/Ralph-ralph-fleet-lanes-must-use-dedicated-git-worktrees.md  
**Merged by:** Scribe  

### 2026-07-01T01-01-08: Ralph Fleet lanes must use dedicated git worktrees
**By:** Ralph
**What:** Ralph Fleet lanes must use dedicated git worktrees
**References:** #65, #66, #67, #68, #69, #70, #71, #72
**Why:** Ahmed corrected the Ralph/Fleet procedure: every issue and Fleet lane must use a dedicated git worktree as the isolation unit. A branch is only the ref checked out inside that worktree; issue work must not be done by switching branches in the main checkout. Before parallelizing or continuing, Ralph must inspect open PR overlap plus worktree state and migrate any branch-only work out of the main checkout before further changes. Current audit found active PRs #65-#72 all have dedicated worktrees and the main checkout only has unrelated mutable .squad state.

## 2026-07-05: 2026-07-01T01-01-29: Release means main-to-AKS only

**Source:** decisions/inbox/Ralph-release-means-main-to-aks-only.md  
**Merged by:** Scribe  

### 2026-07-01T01-01-29: Release means main-to-AKS only
**By:** Ralph
**What:** Release means main-to-AKS only
**References:** Ralph procedure, AKS deployment
**Why:** Ahmed clarified release terminology for this project: RELEASE means building from main and releasing/deploying to AKS. PR/worktree validation and branch builds are not releases. Future Ralph dashboards must distinguish PR validation, local/AKS pre-release validation (if any), and actual release from main to AKS. Actual release should occur only after approved work lands on main, then build from main and deploy to AKS using the image-efficient process (rebuild only changed images, retag unchanged images server-side, build changed images in parallel).

## 2026-07-05: 2026-06-30T20-54-44: Remove legacy agentweaver-sandbox image and deployment wiring

**Source:** decisions/inbox/Scotty-remove-legacy-agentweaver-sandbox-image-and-deploy.md  
**Merged by:** Scribe  

### 2026-06-30T20-54-44: Remove legacy agentweaver-sandbox image and deployment wiring
**By:** Scotty
**What:** Remove legacy agentweaver-sandbox image and deployment wiring
**References:** apps/agentweaver-sandbox, k8s/sandbox-template.yaml, k8s/sandbox-warmpool.yaml, scripts/aks/20-build-push-images.sh, scripts/aks/30-deploy.sh, scripts/aks/40-verify.sh
**Why:** Removed the dead legacy sleep-infinity `agentweaver-sandbox` image/project and stopped deploying/building its `SandboxTemplate`/`SandboxWarmPool`. The live execution path is the pod-per-run `agentweaver-agent-host` warm pool (`k8s/sandbox-template-agenthost.yaml` + `k8s/sandbox-warmpool-agenthost.yaml`), so deploy/verify/build docs and scripts now target only AgentHost. Kept overloaded `sandbox` CRD names, KubernetesSandboxExecutor, network policies selecting `app=agentweaver-sandbox`, browser-preview plumbing, and local MXC temp path names because those remain part of the current model or are labels/contracts rather than the deleted image.

## 2026-07-05: Seraph decision: remove relink repository feature

**Source:** decisions/inbox/seraph-relink-path.md  
**Merged by:** Scribe  

# Seraph decision: remove relink repository feature

Date: 2026-07-05T19:12:07-07:00
Severity: High

## Decision
Per Ahmed's directive, the repository relink feature is removed rather than hardened. The safest resolution is to eliminate the externally reachable ability for a client to change a project's server-side repository path.

## Attack scenario that drove removal
The previous `POST /api/projects/{id}/relink` flow accepted a caller-supplied `working_directory` and updated the project record after path/git checks. A project owner could attempt to point their project at another project's workspace, the Agentweaver server data/config/home area, or another readable server path. Subsequent workspace reads, diffs, git operations, and agent runs would then operate on that target, creating cross-project/tenant data exposure and possible secret exposure.

## Resolution implemented
Removed the REST route `POST /api/projects/{id}/relink`, its request DTO, the `ProjectService.RelinkAsync` path mutation flow, and the now-unused `IProjectStore.UpdateWorkingDirectoryAsync` implementations. Removed the MCP `project_relink` tool and the web client's `relinkProject` method. Removed the Settings-page "Relink repository" form and updated unavailable-project messaging so the UI no longer directs users to relink.

## Internal path-setting retained vs removed
Initial project working-directory assignment remains in project creation/import flows because it is required to create a project and materialize its workspace before inserting the record. Post-creation working-directory mutation was only used by relink, so that store/service update path was removed.

## Validation
Added a regression test asserting `POST /api/projects/{id}/relink` now returns 404. Relevant project endpoint/create tests pass, API build passes, and web build/tests pass.

## 2026-07-05: 2026-06-29: Feature 019 test coverage

**Source:** decisions/inbox/smith-019-tests.md  
**Merged by:** Scribe  

### 2026-06-29: Feature 019 test coverage
**By:** Smith (QA)
**What:** Tests for SqliteTokenUsageStore (6 cases), TokenUsageProjectionService event processing, and usage API endpoints (3 integration tests).
**Why:** Verifies correctness of store aggregations, idempotent inserts, time-range filtering, and HTTP contract for the four new endpoints.

## 2026-07-05: 2026-07-01T05-24-12: Release validation must use authenticated feature checks, not generic AKS smoke

**Source:** decisions/inbox/Smith-release-validation-must-use-authenticated-feature-.md  
**Merged by:** Scribe  

### 2026-07-01T05-24-12: Release validation must use authenticated feature checks, not generic AKS smoke probes
**By:** Smith
**What:** Release validation must use authenticated feature checks, not generic AKS smoke probes
**References:** tests/e2e/release-validation.spec.ts, tests/e2e/oauth-e2e.spec.ts
**Why:** Removed the release-validation reliance on generic AKS probes for GET /, GET /api/health, and OAuth metadata-only reachability. The validation recommendation for this bundle is: keep auth-entry and unauthenticated-protection checks, then validate authenticated GitHub identity plus project-scoped memory and decision-inbox/decision APIs. OAuth validation should prove MCP/auth behavior (401 challenge, JWKS/token signing, PKCE, refresh, org denial, API-key compatibility) rather than treating .well-known metadata fetches as release proof. Do not delete product health endpoints; only remove these checks from release validation.

## 2026-07-05: 2026-07-01T00-08-31: Bundle fixes/features before local deploy, commit, build, and release

**Source:** decisions/inbox/Squad_Coordinator-bundle-fixes-features-before-local-deploy-commit-b.md  
**Merged by:** Scribe  

### 2026-07-01T00-08-31: Bundle fixes/features before local deploy, commit, build, and release
**By:** Squad_Coordinator
**What:** Bundle fixes/features before local deploy, commit, build, and release
**References:** User directive 2026-06-30T17:08:19.204-07:00
**Why:** User directive on 2026-06-30T17:08:19.204-07:00: deployment validation and release should generally be batched. Instead of deploying every individual fix immediately, accumulate a sensible bundle of fixes/features, then locally merge, validate, commit/build, and release/deploy the bundle. This complements the earlier rule that issue work uses worktrees, marks issues under testing, and only closes issues when the user explicitly instructs.

## 2026-07-05: 2026-07-01T03-07-24: Coordinator must surface Ralph status in the shell

**Source:** decisions/inbox/Squad_Coordinator-coordinator-must-surface-ralph-status-in-the-shell.md  
**Merged by:** Scribe  

### 2026-07-01T03-07-24: Coordinator must surface Ralph status in the shell
**By:** Squad_Coordinator
**What:** Coordinator must surface Ralph status in the shell
**References:** User correction 2026-06-30T20:03:35.844-07:00
**Why:** User correction on 2026-06-30T20:03:35.844-07:00: Ralph status updates must be surfaced to the user in this shell, not left inside Ralph's internal agent output. Scheduled status prompts should read Ralph and live GitHub state, then produce a visible dashboard response even when Ralph has no new completed turn.

## 2026-07-05: 2026-07-01T00-04-21: Issue PRs require local merged AKS validation before handoff

**Source:** decisions/inbox/Squad_Coordinator-issue-prs-require-local-merged-aks-validation-befo.md  
**Merged by:** Scribe  

### 2026-07-01T00-04-21: Issue PRs require local merged AKS validation before handoff
**By:** Squad_Coordinator
**What:** Issue PRs require local merged AKS validation before handoff
**References:** User directive 2026-06-30, PR #60 docs-sync reminder
**Why:** User directive on 2026-06-30T17:03:52.930-07:00: for this project, until told otherwise, issue work must use dedicated worktrees, merge locally with the relevant unmerged issue PRs before deployment validation, deploy/validate locally against AKS before PR handoff because GitHub workflow permissions cannot perform this Microsoft-side validation, mark GitHub issues as under testing while local validation is in progress, and only close issues after the user explicitly instructs pushing the final commits/closure. Docs must be fixed before Ralph proceeds.

## 2026-07-05: 2026-07-01T00-23-23: Issue work isolation requires worktrees, not plain branches

**Source:** decisions/inbox/Squad_Coordinator-issue-work-isolation-requires-worktrees-not-plain-.md  
**Merged by:** Scribe  

### 2026-07-01T00-23-23: Issue work isolation requires worktrees, not plain branches
**By:** Squad_Coordinator
**What:** Issue work isolation requires worktrees, not plain branches
**References:** User correction 2026-06-30T17:23:05.671-07:00
**Why:** User correction on 2026-06-30T17:23:05.671-07:00: Ralph/Squad issue work must use dedicated git worktrees, not just branches in the main checkout. Branches may exist as refs for those worktrees, but the isolation boundary and working directory for each issue/Fleet lane must be a separate worktree. This is required to avoid conflicts and preserve the main checkout while parallel work proceeds.

## 2026-07-05: 2026-07-01T02-39-20: Keep GitHub Issues as source of truth during rapid prototyping

**Source:** decisions/inbox/Squad_Coordinator-keep-github-issues-as-source-of-truth-during-rapid.md  
**Merged by:** Scribe  

### 2026-07-01T02-39-20: Keep GitHub Issues as source of truth during rapid prototyping
**By:** Squad_Coordinator
**What:** Keep GitHub Issues as source of truth during rapid prototyping
**References:** User clarification 2026-06-30T18:28:20.296-07:00
**Why:** User clarification on 2026-06-30T18:28:20.296-07:00: while using the simplified rapid-prototyping loop, GitHub Issues must remain the source of truth for work state. Issues should be kept updated with where the work stands and should not remain open indefinitely after the work is validated/released. The loop should pull from GitHub Issues, implement/validate/deploy locally, ask Ahmed to test, and then update or close issues promptly when done.

## 2026-07-05: 2026-07-01T02-43-57: Ralph coordinates work by launching Squad members

**Source:** decisions/inbox/Squad_Coordinator-ralph-coordinates-work-by-launching-squad-members.md  
**Merged by:** Scribe  

### 2026-07-01T02-43-57: Ralph coordinates work by launching Squad members
**By:** Squad_Coordinator
**What:** Ralph coordinates work by launching Squad members
**References:** User correction 2026-06-30T19:43:38.219-07:00
**Why:** User correction on 2026-06-30T19:43:38.219-07:00: Ralph should not do implementation work himself. Ralph owns queue management, prioritization, status, release coordination, and handoffs, but must launch the appropriate Squad members to perform domain work. This applies to the simplified rapid-prototyping loop as well: Ralph pulls from GitHub Issues, routes work to Squad members, coordinates local validation/deploy/test handoff, and keeps issue state current.

## 2026-07-05: 2026-07-01T00-19-23: Ralph issue loop priority and release procedure

**Source:** decisions/inbox/Squad_Coordinator-ralph-issue-loop-priority-and-release-procedure.md  
**Merged by:** Scribe  

### 2026-07-01T00-19-23: Ralph issue loop priority and release procedure
**By:** Squad_Coordinator
**What:** Ralph issue loop priority and release procedure
**References:** User directive 2026-06-30T17:19:02.061-07:00
**Why:** User directive on 2026-06-30T17:19:02.061-07:00: Ralph should continue working issues in priority order: bug fixes first, then chores, then features. Each issue/work item must be committed separately. Agents must be told about required docs work for anything they touch. When a meaningful bundle of work has landed, create a release by building the necessary images and deploying them to the cluster, following existing image-efficiency and AKS validation rules. Continue honoring prior rules: use issue worktrees, be conflict-aware with open/merged PRs, locally merge/validate related work before deployment, mark issues under testing as appropriate, and do not close issues until the user explicitly approves.

## 2026-07-05: 2026-07-01T00-01-00: Ralph loop PR handoff must include conflict-aware branching, local validation,

**Source:** decisions/inbox/Squad_Coordinator-ralph-loop-pr-handoff-must-include-conflict-aware-.md  
**Merged by:** Scribe  

### 2026-07-01T00-01-00: Ralph loop PR handoff must include conflict-aware branching, local validation, and docs disposition
**By:** Squad_Coordinator
**What:** Ralph loop PR handoff must include conflict-aware branching, local validation, and docs disposition
**References:** PR #57, PR #58, PR #60, User feedback 2026-06-30
**Why:** User feedback on 2026-06-30T16:59:00.499-07:00 identified process gaps in Ralph-loop GitHub bug work: open PRs were being created without enough evidence that later work accounts for unmerged PR changes, local validation evidence was not consistently visible, and docs-sync reminders were not acted on before PR handoff. Future Ralph-loop issue work must inspect existing open PRs for overlapping files/areas before starting, stack or rebase branches when fixes are related, report local validation commands/results for each PR branch/worktree, and either update docs or explicitly justify why no docs are needed when doc-relevant code changes or docs-sync reminders appear.

## 2026-07-05: 2026-07-01T03-56-19: Ralph must never implement domain work directly

**Source:** decisions/inbox/Squad_Coordinator-ralph-must-never-implement-domain-work-directly.md  
**Merged by:** Scribe  

### 2026-07-01T03-56-19: Ralph must never implement domain work directly
**By:** Squad_Coordinator
**What:** Ralph must never implement domain work directly
**References:** User input: "This is a complete failure. Ralph coded the entire thing. Wtf Squad.."
**Why:** User escalation on 2026-06-30T20:55:42.415-07:00: Ralph violated the Squad model by coding/implementing work directly instead of launching Squad members. This is considered a complete process failure. Ralph must be restricted to coordination: queue management, Fleet/conflict planning, issue-state updates, status reporting, and release orchestration. All domain implementation, docs, tests, and specialist edits must be assigned to the relevant Squad member(s) and surfaced in status by owner/lane.

## 2026-07-05: 2026-07-01T01-31-12: Ralph rapid-prototyping loop uses GitHub issues as the queue

**Source:** decisions/inbox/Squad_Coordinator-ralph-rapid-prototyping-loop-uses-github-issues-as.md  
**Merged by:** Scribe  

### 2026-07-01T01-31-12: Ralph rapid-prototyping loop uses GitHub issues as the queue
**By:** Squad_Coordinator
**What:** Ralph rapid-prototyping loop uses GitHub issues as the queue
**References:** User correction 2026-06-30T18:28:20.296-07:00
**Why:** User correction on 2026-06-30T18:28:20.296-07:00: the current Ralph process is not autonomous enough and should be simplified for the rapid prototyping phase. Desired loop: pull tasks from GitHub issues, do the work locally, validate locally, deploy to AKS, then ask Ahmed to test. Keep the workflow simple for now. GitHub issues replace the local task list as the source of work, but the execution/release cadence should resemble the previous local rapid-prototyping flow rather than heavy PR/process gating.

## 2026-07-05: 2026-07-01T00-50-35: Release means build from main and deploy to AKS

**Source:** decisions/inbox/Squad_Coordinator-release-means-build-from-main-and-deploy-to-aks.md  
**Merged by:** Scribe  

### 2026-07-01T00-50-35: Release means build from main and deploy to AKS
**By:** Squad_Coordinator
**What:** Release means build from main and deploy to AKS
**References:** User clarification 2026-06-30T17:50:14.743-07:00
**Why:** User clarification on 2026-06-30T17:50:14.743-07:00: in agentweaver, a release means building from `main` and releasing/deploying to AKS. Pre-merge PR validation, local worktree validation, or building from issue branches is not a release. Release should happen after the approved work is on main, using the repository's image-efficient build/deploy process.

## 2026-07-05: 2026-07-01T05-13-20: Remediate Ralph landed work through named Squad members

**Source:** decisions/inbox/Squad_Coordinator-remediate-ralph-landed-work-through-named-squad-me.md  
**Merged by:** Scribe  

### 2026-07-01T05-13-20: Remediate Ralph landed work through named Squad members
**By:** Squad_Coordinator
**What:** Remediate Ralph landed work through named Squad members
**References:** Morpheus review blockers, Smith validation blockers, Seraph security blockers, User directive 2026-06-30T22:12:39.073-07:00
**Why:** User directive on 2026-06-30T22:12:39.073-07:00: fix the mess Ralph introduced and delete the useless AKS smoke tests. Ralph must not perform the remediation. Named Squad members should own fixes directly: runtime/orchestration issues to Morpheus, security blockers to Seraph/Tank as appropriate, validation/test cleanup to Smith/Link. The prior AKS smoke checks (`/`, `/api/health`, OAuth metadata) are not acceptable validation for the landed features and should be removed from the release validation definition of done in favor of meaningful authenticated/feature-specific validation.

## 2026-07-05: 2026-07-01T00-22-13: Use Fleet for conflict-safe parallel issue work

**Source:** decisions/inbox/Squad_Coordinator-use-fleet-for-conflict-safe-parallel-issue-work.md  
**Merged by:** Scribe  

### 2026-07-01T00-22-13: Use Fleet for conflict-safe parallel issue work
**By:** Squad_Coordinator
**What:** Use Fleet for conflict-safe parallel issue work
**References:** User directive 2026-06-30T17:21:59.993-07:00
**Why:** User directive on 2026-06-30T17:21:59.993-07:00: Ralph should use Fleet to land as much parallel issue work as possible without conflicts. Apply this within the existing procedure: bugs first, then chores, then features; separate commits per work item; docs disposition required; issue worktrees; inspect open/merged PR overlap; stack/rebase or serialize related work; bundle meaningful landed work for local validation, image build, AKS deploy/release when approved.

## 2026-07-05: 2026-07-01T20-15-48: Mandatory rubber-duck review before every PR

**Source:** decisions/inbox/Squad-Coordinator-mandatory-rubber-duck-review-before-every-pr.md  
**Merged by:** Scribe  

### 2026-07-01T20-15-48: Mandatory rubber-duck review before every PR
**By:** Squad-Coordinator
**What:** Mandatory rubber-duck review before every PR
**References:** Squad-process, implementation-workflow, PR-gate
**Why:** ## Decision: Mandatory rubber-duck review before every PR

**Date:** 2026-07-01
**Requested by:** Ahmed (sabbour)

### Rule

No Squad agent may open a GitHub pull request without first completing a rubber-duck review of their implementation. This is a hard gate, not advisory.

### Process change

After an agent completes implementation and build validation, but BEFORE running gh pr create, it must:

1. Spawn a rubber-duck sub-agent (agent_type: "rubber-duck") pointing at the diff / changed files
2. Wait for the rubber-duck verdict
3. Address any bugs, logic errors, or design flaws the rubber duck surfaces
4. Only then open the PR

### Coordinator enforcement

Every implementation spawn prompt must include a Review gate section:

After implementing and validating the build, collect all changed files, spawn a rubber-duck review with those files as context, fix any issues raised, then and only then open the PR. Do NOT open the PR without completing this step.

### Rationale

Implementation agents catch compilation errors but miss logic errors, design flaws, and subtle bugs. The rubber-duck agent provides a second pair of eyes at negligible cost relative to the value of catching issues before they reach review.

## 2026-07-05: 2026-06-30T21-44-45: AKS install path uses only agent-host SandboxTemplate/warm pool after legacy sa

**Source:** decisions/inbox/Tank-aks-install-path-uses-only-agent-host-sandboxtempl.md  
**Merged by:** Scribe  

### 2026-06-30T21-44-45: AKS install path uses only agent-host SandboxTemplate/warm pool after legacy sandbox removal
**By:** Tank
**What:** AKS install path uses only agent-host SandboxTemplate/warm pool after legacy sandbox removal
**References:** install.sh, install.ps1, scripts/aks/00-variables.sh, scripts/aks/20-build-push-images.sh, scripts/aks/30-deploy.sh, scripts/aks/40-verify.sh, docs/guide/deployment-aks.md, docs/reference/sandbox-setup.md
**Why:** Audited and tightened the AKS installer path after removal of the legacy agentweaver-sandbox image/template/warmpool. The install/deploy/verify flow now treats agentweaver-agent-host as the only live SandboxTemplate and SandboxWarmPool, applies storageclass-workspace.yaml before pvc-workspace.yaml, applies AgentHost A2A/API egress NetworkPolicies, captures TENANT_ID and IDENTITY_CLIENT_ID across install.sh script steps, provisions Postgres with the public Flexible Server FQDN (<server>.postgres.database.azure.com) while relying on the linked privatelink private DNS zone, and verifies absence of legacy agentweaver-sandbox template/warm pool resources. 20-build-push-images.sh preserves the redeploy efficiency convention: changed images build in parallel, unchanged images are retagged via az acr import when a previous/current image tag is available.

## 2026-07-05: 2026-07-01T09:00:00Z: Tank backend review

**Source:** decisions/inbox/tank-autopilot-sse-review.md  
**Merged by:** Scribe  

### 2026-07-01T09:00:00Z: Tank backend review
**By:** Tank

---

## Autopilot fix verdict: NEEDS_CHANGE (secondary bug found and fixed)

The fix applied to `StartReservedCoordinatorRunAsync` is **correct**. The `if (autopilot)` gate at line 222 ensures `ScheduleUnattendedConfirm` is only scheduled when the project has autopilot on. The logic is complete: `ActivateAsync` always runs (so the run starts), and the unattended confirm loop only fires when the user has opted in.

**Secondary bug (fixed as part of this review):** `StartRetriedPickupCoordinatorRunAsync` had the same defect — `ScheduleUnattendedConfirm` was called unconditionally regardless of the `autopilot` flag. This is the retried-pickup path (POST `/api/runs/{id}/retry`). Ahmed turning off `PickupAutopilot` and then retrying a failed pickup run would have exhibited the same "always auto-confirms" behavior. Fixed in the same commit: the call is now gated on `if (autopilot)`.

**`PickupAutopilot` default:** `Project.PickupAutopilot { get; init; } = true` — confirmed in `packages/Agentweaver.Domain/Project.cs`. All existing projects default to autopilot-on (auto-confirm on pickup). New projects that explicitly set it to `false` will stop at `awaiting_confirmation` for human confirmation, which is the correct new behavior.

---

## SSE re-subscription verdict: WORKS

**How the stream closes at the spec gate:** The SSE loop in `RunEndpoints.cs` (line 482) breaks when `entry.IsAwaitingReview && (reviewRequestedSent || entry.HasEventType(EventTypes.ReviewRequested))`. `MarkAwaitingReview()` IS called for coordinator runs (in `CoordinatorWorkflowFactory.DraftAndPersistAsync`), but `ReviewRequested` is NEVER emitted for coordinator spec gate — the event is `coordinator.outcome_spec`. So the SSE loop does **not** close server-side for coordinator runs at the spec gate. The connection stays open polling via `WaitForChangeAsync`.

If the frontend disconnects voluntarily on seeing `coordinator.outcome_spec`, re-subscription works:

1. **Same process (entry in RunStreamStore):** `streamStore.Get(id)` returns the live entry (not completed, not evicted). The SSE loop replays events since `Last-Event-ID`, then polls. When the workflow resumes after confirmation, events arrive via `RecordNext` → `WaitForChangeAsync` wakes → delivered to client. No break condition fires for the coordinator spec gate (`ReviewRequested` never set). WORKS.

2. **After process restart (no in-memory entry, run still active):** `streamStore.Get(id)` is null → `eventStream.SubscribeAsync` path. `SqliteRunEventStream.SubscribeAsync` replays from DB, then tails the live channel. `CoordinatorOutcomeSpec` is not in `TerminalTypes`, so the channel stays open. New post-confirmation events publish to the channel and are delivered. WORKS.

3. **Completed run replay:** Channel is nil; `SubscribeAsync` replays DB history only. WORKS.

**Stream entry is NOT destroyed at review close.** `RunStreamStore.Complete()` is only called on run termination. The `MarkAwaitingReview()` flag sets in-memory state but does not evict the entry. A reconnecting client finds the entry intact.

---

## Help text updated: YES

`apps/web/src/components/automationHelp.ts` — `autopilotPickup` now reads:

> `${AUTOPILOT_BASE} Also auto-confirms the outcome spec so pickup runs proceed without waiting for manual confirmation. Applies to runs this project picks up automatically (and their child runs).`

`autopilotOrchestration` is unchanged — spec auto-confirmation is a pickup-only behavior (only `StartReservedCoordinatorRunAsync` and `StartRetriedPickupCoordinatorRunAsync` schedule the unattended confirm; interactive `StartCoordinatorRunAsync` does not).

---

## Test coverage note

`tests/Agentweaver.Tests/Coordinator/RunOptionsAndAutopilotTests.cs` contains no test for the `StartReservedCoordinatorRunAsync` conditional-confirm path. The existing tests cover toggle endpoints, cascade, and child-question autopilot behavior — all via `CoordinatorDispatchService`. A test for the new guard should be added:

- When `autopilot=false`, `StartReservedCoordinatorRunAsync` must not call `ScheduleUnattendedConfirm` (run stays at `awaiting_confirmation`).
- When `autopilot=true`, it must schedule the unattended confirm and the run advances.
- Same two cases for `StartRetriedPickupCoordinatorRunAsync`.

Not added here: `CoordinatorRunService` requires `ICoordinatorWorkflowFactory`, `IStreamingRunRegistry`, `IPendingRequestStore`, and an MAF workflow; the test infra would need a new factory or significant fake wiring. Not trivial.

---

## Changes made

- `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs` — gated `ScheduleUnattendedConfirm` on `if (autopilot)` in `StartRetriedPickupCoordinatorRunAsync` (missed in the original fix)
- `apps/web/src/components/automationHelp.ts` — updated `autopilotPickup` help text to include spec auto-confirmation behavior

## 2026-07-05: Tank — CastingService Bug Fixes

**Source:** decisions/inbox/tank-casting-fixes.md  
**Merged by:** Scribe  

# Tank — CastingService Bug Fixes

**Date:** 2026-06-30  
**Author:** Tank (Backend Engineer)  
**Status:** Complete

---

## Bug 1: routing.md duplicate/generic signals — FIXED

**Root cause:** `BuildRoutingMd` collapsed role titles to generic keyword-matched bucket strings, causing multiple agents to share the same routing signal (e.g. "Lead PM" and "Product Marketing Manager" both mapped to "Product decisions, scope, prioritization").

**Fix applied:** Replaced the switch/keyword matching with a direct per-agent signal using `member.Role.Title`. Built-in agents (Scribe, Ralph, Rai, Coordinator) are excluded from the Work Assignment table and remain in the Built-in Agents section. Each non-builtin agent now gets exactly one unique row whose signal is its role title.

**Result:** Every row in routing.md is unique and role-specific. The Coordinator can unambiguously route work to the correct agent.

---

## Bug 2: agents not committed to git on project creation — FIXED

**Root cause:** `ConfirmProposalAsync` wrote all `.squad/` files via `writer.*` calls but never called `CommitSyncAsync`. The files existed on disk but were not committed; users had to manually click Sync.

**Fix applied:** After `SeedInitialMemoriesAsync` completes, `ConfirmProposalAsync` now:
1. Creates a `SquadGitScribe` for the project.
2. Calls `GetStatus()` to obtain the current `ChangeSetHash`.
3. Calls `Commit(hash, "init: squad team for {project.Name}", "Agentweaver", "agentweaver@localhost")`.
4. Wraps the commit in a try/catch — on failure (e.g. nothing to commit, git not initialized) logs a warning and continues; the team was still created successfully.

**Result:** Squad files are committed to git immediately upon team creation. No manual Sync required.

---

## Build verification

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Test impact

No existing tests cover `BuildRoutingMd` or `ConfirmProposalAsync`. No tests added or modified.

## 2026-07-05: Tank investigation: confirm + SSE + work-plan polling

**Source:** decisions/inbox/tank-confirm-sse.md  
**Merged by:** Scribe  

# Tank investigation: confirm + SSE + work-plan polling

## Bottom line

The **"Confirm does nothing"** symptom is primarily a **backend event-propagation gap**, not a UI-only bug.

- `RunStreamStore` is **per-pod memory** (`apps/Agentweaver.Api/Program.cs:96`).
- `RunWatchLoopService` is a **singleton helper, not a hosted cross-pod relay** (`Program.cs:109`), and startup recovery only runs once on the elected leader (`Program.cs:687-706`).
- The immediate confirm/review events are often written with **`RunStreamEntry.RecordNext(...)` only**, which updates **this pod's** in-memory stream but does **not** durably publish to `IRunEventStream` / `RunEvents`.

So if the UI is waiting on SSE and the relevant event is emitted on a different in-memory stream (or never durably mirrored), the page appears stuck until a refresh re-reads persisted DB state.

---

## 1) Confirm flow

### Standard run review (`POST /api/runs/{id}/review`)

Relevant code:

- Endpoint: `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:701-860`
- Pending gate store: `apps/Agentweaver.Api/Runs/PendingRequestStore.cs:11-101`

What happens:

1. The review gate itself is **DB-backed and cross-pod safe** via `PendingRequestStore` (`PendingRequestStore.cs:12-24`, `34-101`).
2. For **approve**, the endpoint does **not** write a durable confirm event to `IRunEventStream`.
   - It only writes immediate UI events to the **local** `RunStreamStore`:
     - `workflow.step(review=completed)`
     - `merge.started`
   - See `RunEndpoints.cs:803-827`.
3. Then it resumes the paused workflow with `SendResponseAsync(...)` (`RunEndpoints.cs:829-839`).
4. The eventual merged/failed outcome is written later by the watch loop / merge path, not by the POST itself.

### Coordinator outcome-spec confirm (`POST /api/runs/{id}/outcome-spec/confirm`)

Relevant code:

- Endpoint: `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:59-92`
- Service: `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:322-425`
- Draft/confirm event emission: `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:216-229`, `320-327`

What happens:

1. The endpoint calls `CoordinatorRunService.ConfirmOutcomeSpecAsync(...)` (`CoordinatorEndpoints.cs:82-89`).
2. The service consumes the DB-backed pending gate and resumes the workflow (`CoordinatorRunService.cs:392-425`).
3. **No immediate confirmed SSE event is durably written by the POST path.**
4. The eventual `coordinator.outcome_spec.confirmed` event is emitted later in `FinalizeAsync(...)` via:
   - `entry?.RecordNext(EventTypes.CoordinatorOutcomeSpecConfirmed, ...)`
   - `CoordinatorWorkflowFactory.cs:320-327`
   - Again: **local in-memory stream only**.
5. The endpoint immediately returns `ReadOutcomeSpecAsync(...)` (`CoordinatorEndpoints.cs:87`, `562-566`), which can race **before** `FinalizeAsync(...)` flips DB status to `confirmed` (`CoordinatorWorkflowFactory.cs:308-315`).

That explains the exact UX: **click Confirm -> response may still look unchanged -> refresh later shows confirmed**.

---

## 2) Cross-pod gap

### Does Pod A's `RunWatchLoopService` replay confirm events from DB into Pod A's `RunStreamStore`?

**No.**

Relevant code:

- `RunWatchLoopService` watches only the local `StreamingRun`: `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:74-144`, `146-230`
- `RunStreamStore` is in-memory only: `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:159-200`
- `IRunEventStream.SubscribeAsync(...)` replays DB rows once, then tails a **pod-local channel**:
  - `apps/Agentweaver.Api/Infrastructure/IRunEventStream.cs:24-36`
  - `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:90-121`

Important detail:

- `EfRunEventStream.SubscribeAsync(...)` loads persisted rows once (`LoadFromSequenceAsync`) and then waits on the **local process channel**.
- It does **not** keep polling the DB for rows appended by another pod.

So there is **no eventual cross-pod relay** from DB -> `RunWatchLoopService` -> `RunStreamStore`.

**Delay:** effectively **never**. The SSE pod will not eventually "catch up" live via `RunWatchLoopService`.

---

## 3) If Pod A has SSE and Pod B receives Confirm POST

### Will Pod A wake its SSE clients?

Only if Pod A itself gets the in-memory write.

For the affected confirm/review paths, the code writes to:

- `RunStreamStore.Get(id)?.RecordNext(...)` in `RunEndpoints.cs:805-827`
- `RunStreamStore.Get(input.RunId)?.RecordNext(...)` in `CoordinatorWorkflowFactory.cs:218-229`, `322-327`

Those writes:

- wake SSE clients **on that same pod**
- do **not** wake SSE clients on another pod
- do **not** go through a durable multi-pod relay

So the bug is **not** "Pod A will eventually replay it"; the bug is **the live confirm event is pod-local**.

---

## 4) Missing `RunStreamStore` entry on another pod

Relevant code:

- Run startup creates the stream entry locally:
  - `apps/Agentweaver.Api/Runs/RunOrchestrator.cs:126-128`
  - `RunOrchestrator.cs:212-214`
  - `RunOrchestrator.cs:295-296`
- `/stream` waits briefly for a local entry, then falls back to `IRunEventStream.SubscribeAsync(...)` only if no entry exists:
  - `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:388-445`

Answer:

- If a run starts on **Pod A**, **Pod B** does **not** normally get a `RunStreamStore.Create(...)`.
- Pod B only gets live streaming if `/stream` falls back to `IRunEventStream.SubscribeAsync(...)`.
- But that fallback is **not a true cross-pod live tail**; it replays persisted history once, then tails Pod B's local channel.

So a new run on Pod A is **not seeded** into Pod B's `RunStreamStore` by `RunWatchLoopService`.

---

## 5) Why refresh fixes it

Because refresh re-reads **persisted DB state**, not because SSE recovered.

Examples:

- Outcome spec status is persisted in `OutcomeSpecs` (`CoordinatorWorkflowFactory.cs:308-315`).
- Run status / result are persisted via `IRunStore` transitions in the review / merge paths.

So after enough time, GET endpoints reflect reality even if the live SSE event never reached the browser.

---

## 6) Work-plan 404 polling

Current frontend source already contains the intended 404 backoff:

- Initial eager seed fetch: `apps/web/src/pages/CoordinatorRunPage.tsx:1037-1048`
- Poll loop with 404 detection and 30s backoff: `CoordinatorRunPage.tsx:1111-1147`

Specifically:

- `ApiError(404)` sets `wpMissing = true` (`1120-1123`)
- the next delay becomes `30000` ms (`1144-1147`)

So the **current code** does back off to 30s after a confirmed 404.

What still causes repeated 404s in current source:

1. one **eager** `/work-plan` fetch on mount (`1040`)
2. the separate poll loop (`1119`)

That means two independent call sites exist, but the **main "hammering" loop is frontend**, not `RunWatchLoopService`.

---

## Root cause call

### "Confirm does nothing"

**Primary root cause:** backend live-event propagation is pod-local.

More precisely:

- confirm/review synthetic events are emitted with `RecordNext(...)` into **local `RunStreamStore` only**
- there is **no multi-pod live relay**
- the confirm endpoint can return **stale persisted state** before the workflow finalizer updates DB

### Immediate cause in the UI

The UI currently depends on:

- the POST response being already updated **or**
- the follow-up SSE event arriving

When neither happens, the page looks unchanged until refresh.

---

## Fix recommendation

### Primary fix: **backend**

Minimal backend direction:

1. For confirm/review synthetic events, stop writing only to `RunStreamStore`.
2. Route them through a helper that writes to:
   - local `RunStreamStore` **and**
   - durable `IRunEventStream.AppendAsync(...)`
3. Apply that helper at least in:
   - `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:803-827`
   - `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:216-229`
   - `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:320-327`

Why this is the real fix:

- it addresses the actual propagation bug
- it benefits both standard run review and coordinator confirm
- frontend polling after confirm would only mask the missing relay

### Secondary hedge: **frontend**

If a small UX band-aid is wanted, add a post-confirm refresh/poll in:

- `apps/web/src/components/OutcomeSpecPanel.tsx:277-295`

Example behavior:

- after successful confirm, poll `getOutcomeSpec(runId)` (and/or `getWorkPlan(runId)`) until
  status changes or plan appears, instead of relying only on the immediate POST response + SSE

This would improve UX, but it is **not** the underlying fix.

### Work-plan 404 follow-up

If the 404 noise still matters, the frontend cleanup is:

- consolidate the eager mount fetch and the poll loop in
  `apps/web/src/pages/CoordinatorRunPage.tsx:1037-1048` and `1111-1147`
- or suppress the eager seed fetch once the page has already established `noWorkPlan=true`

---

## Final answer to the four questions

1. **Confirm flow:**  
   - DB-backed gate consumption: yes (`PendingRequestStore`)  
   - immediate confirm/review UI events: **local `RunStreamStore` only** in the affected paths  
   - those local writes wake SSE clients **only on that pod**

2. **Cross-pod gap:**  
   - Pod A does **not** eventually replay confirm events from DB into its `RunStreamStore`  
   - effective delay = **never**

3. **Missing `RunStreamStore` entry:**  
   - another pod does **not** normally get `Create()` for the run  
   - there is no steady-state cross-pod seeding by `RunWatchLoopService`

4. **Immediate cause:**  
   - for the observed symptom, the main issue is **backend: confirm event/state change is not reliably delivered to the live SSE consumer**
   - frontend poll-after-confirm would be a mitigation, not the root fix

## 2026-07-05: Tank — Dashboard RCA + Issue Drafts (INVESTIGATE/DRAFT only)

**Source:** decisions/inbox/tank-dashboard-rca.md  
**Merged by:** Scribe  

# Tank — Dashboard RCA + Issue Drafts (INVESTIGATE/DRAFT only)

Date: 2026-06-30T16:04:53-07:00
Author: Tank (Backend Engineer)
Repo: sabbour/agentweaver @ main (HEAD eda08f3). Live frontend image `9413053`.
Note: `git diff 9413053 HEAD -- DashboardPage.tsx TokenUsagePanel.tsx` is EMPTY → behavior confirmed in source, not a stale deploy.
Duplicate check: `gh issue list --state open` → none of these 3 exist.

Dashboard page: apps/web/src/pages/DashboardPage.tsx
Usage panel:    apps/web/src/components/TokenUsagePanel.tsx
API client:     apps/web/src/api/client.ts (getProjectDashboard:714, getProjectUsage:766, getRunUsage:758)
Backend:        Endpoints/MetricsEndpoints.cs (dashboard), Endpoints/UsageEndpoints.cs (usage),
                Metrics/MetricsService.cs (GetProjectDashboardAsync:96), Infrastructure/SqliteTokenUsageStore.cs

---

## Item 1 — BUG: numeric columns misaligned with headers (Agent leaderboard)

Root cause: `numericCell { textAlign: 'right' }` (DashboardPage.tsx:161-163) is applied to BOTH the
Fluent `TableHeaderCell` (lines 457-461 via mergeClasses) AND the body `TableCell`s (480 runs_this_week,
481 runs_total, 493 avg_duration). Fluent UI `TableHeaderCell` renders its label inside an internal flex
button (`.fui-TableHeaderCell__button`, justify-content flex-start), so `text-align: right` has NO effect on
the header → header stays visually LEFT. The plain body `TableCell` honors `text-align: right` → values go
RIGHT. Result: numbers don't sit under their headers.

Fix direction: left-align the numeric columns — drop `styles.numericCell` (or switch to left) on those
header + body cells so headers and values are both left-aligned. (Same TableHeaderCell flex caveat also
affects the per-model usage table in TokenUsagePanel.tsx:111-114/121-123, but that table is symmetric so
less visually broken — note only.)

## Item 2 — FEATURE: token + AIC usage PER AGENT

Current: TokenUsagePanel renders by_model only (TokenUsagePanel.tsx:106-129). DTO `TokenUsageSummary`
(types.ts:1187) has only `by_model: TokenUsageByModel[]` (types.ts:1179) — model_id, input/output tokens,
total_nano_aiu. NO agent dimension.

Feasibility — AGENT DIMENSION IS DERIVABLE, NO SCHEMA CHANGE REQUIRED:
- `token_usage_records` columns (SqliteTokenUsageStore.cs:22-27): id, run_id, workflow_run_id, project_id,
  model_id, input/output tokens, total_nano_aiu, recorded_at. No agent column.
- BUT every record carries `run_id`, and the `runs` table carries `agent_name` (SqliteRunStore.cs:604,
  index 18 → SqliteRunStore.cs:630; EF: EfRunStore.cs:394). Same DB → a JOIN
  `token_usage_records t JOIN runs r ON r.run_id=t.run_id GROUP BY r.agent_name` yields per-agent usage.
- Proof it already works client-side: DashboardPage.loadProjectUsage (lines 289-327) iterates project runs,
  calls getRunUsage(run.execution_id), groups by run.agent_name → agentCosts (powers leaderboard Cost col).

Classification: type:feature (new capability = per-agent usage table). Cleanest impl is a backend grouping
(`GetProjectUsageByAgentAsync` via run_id→agent_name JOIN + `by_agent` on the DTO), avoiding the current
N+1 per-run client calls. No schema/event change needed.

## Item 3 — CHORE: unify timeline filter across leaderboard AND usage

Current state: a shared "Range" Select already exists (DashboardPage.tsx:425-441, state `usageRange`:250).
But it only drives `loadProjectUsage` (329-331) which refetches the USAGE panel (filteredUsage) and the
leaderboard COST column (agentCosts). The leaderboard's core metrics (runs_this_week, runs_total,
success_rate, avg_duration) come from `getProjectDashboard` (client.ts:714) which takes NO from/to param.

Server side: `GET /api/projects/{id}/dashboard` (MetricsEndpoints.cs:20-37) takes no range; 
`GetProjectDashboardAsync` (MetricsService.cs:96) HARD-CODES `weekAgo = now.AddDays(-7)` (line 99) for
runs_this_week (used at MetricsService.cs:141 and leaderboard 208), and runs_total is all-time. So the
window is server-side hard-coded → unifying the filter is NOT frontend-only.

Fix direction: add `from`/`to` (or `range`) params to the dashboard endpoint + GetProjectDashboardAsync so
the leaderboard window respects the shared dropdown, then have the single Range select drive both the
dashboard fetch and the usage fetch. Frontend-only is insufficient because the leaderboard metrics are
computed server-side against a fixed 7-day window.

---
SUMMARY: Item1 = real bug (Fluent TableHeaderCell ignores text-align:right so headers stay left while
right-aligned values drift); Item2 = feasible feature, agent derivable via run_id→agent_name JOIN, no
schema change; Item3 = chore needs a backend range param on the dashboard endpoint (currently hard-coded
7-day), not frontend-only.


---

## Item 4 — FEATURE: AIC usage time-series graph on the project Dashboard (overview)

Same page confirmation: the "overview Dashboard" (breadcrumb Projects/{project}, header
"Dashboard — Delivery metrics and the agent leaderboard", Throughput last-30-days chart) IS the same
`apps/web/src/pages/DashboardPage.tsx` as Items 1-3. The global `/overview` page is a DIFFERENT component
(`OverviewPage.tsx:280` title "Overview" / "Fleet activity at a glance").

Existing Throughput chart (the pattern to mirror):
- Component: hand-rolled inline SVG `ThroughputChart` (DashboardPage.tsx:204-240), no chart lib. Rendered
  DashboardPage.tsx:405-421 from `data.throughput`.
- Data: ProjectDashboardDto.throughput → ThroughputPointDto { date, created, done } (types.ts:1156-1161).
- Source path: getProjectDashboard (client.ts:714) → MetricsService.GetProjectDashboardAsync:96 →
  ReadThroughput:161-194 (server-side, 30 daily buckets built from run StartedAt/EndedAt).

Feasibility — NO time-bucketed usage series exists today:
- Usage store returns ONLY aggregate by_model (SqliteTokenUsageStore.GetProjectUsageAsync:77-98 GROUPs by
  model_id, no day bucket). Same aggregate found in Item 2.
- token_usage_records carries `recorded_at` (SqliteTokenUsageStore.cs:24, written :37), so a daily group-by
  (`strftime('%Y-%m-%d', recorded_at)` / EF date trunc) summing total_nano_aiu (+tokens) per day is feasible.
- => A NEW time-bucketed usage endpoint/query + DTO (e.g. UsagePointDto { date, nano_aiu, tokens }) is
  needed; reuse the same token_usage_records source. Classification: type:feature (new data series + chart),
  not pure wiring.

Fix direction: add a daily usage series (new endpoint or extend the dashboard DTO with `usage_series`),
group token_usage_records by day, and render a second inline-SVG chart mirroring ThroughputChart.

## 2026-07-05: Tank Docs Update — 2026-06-30

**Source:** decisions/inbox/tank-docs.md  
**Merged by:** Scribe  

# Tank Docs Update — 2026-06-30

## Summary

Five doc edits across four files. Doc drift check passed (exit 0).

---

## Change 1: Autopilot now gates outcome spec auto-confirmation

### docs/reference/coordinator.md

**Section:** "Per-run options: Autopilot and auto-approve-tools"

**What changed:** Replaced the single-line Autopilot description. The old text said autopilot "auto-answers CLARIFYING QUESTIONS ONLY." The new text documents both behaviors as a numbered list:

1. Auto-answers clarifying questions (existing behavior, unchanged).
2. Auto-confirms the outcome spec for pickup runs when autopilot=true. When autopilot=false, the run pauses at `awaiting_confirmation` until the human confirms via the UI. Interactive runs always pause regardless.

Also documents that `PickupAutopilot` defaults to `true` so existing projects are unaffected.

### docs/deep-dive/coordinator-internals.md

**Section:** "Confirmation paths"

**What changed:** The old text said a "bounded unattended confirmation loop confirms the reversible plan on behalf of the accountable human" — no conditional. The new text makes the condition explicit: the loop fires only when autopilot is on. When autopilot is off, the run stays at `awaiting_confirmation` until manual confirmation. The clarification that this is not Autopilot bypassing safety is preserved.

### docs/guide/board.md

**Section:** "The heartbeat"

**What changed:** Added a new "Pickup settings" subsection inside the heartbeat section. It documents the three pickup-level controls (max ready per heartbeat, autopilot, auto-approve tools) as a table, with an explicit callout that autopilot controls both question auto-answering AND outcome-spec auto-confirmation for pickup runs. The concurrency-limit warning block was preserved, moved to the end of the expanded section.

---

## Change 2: SSE stream reconnects after confirmation gate

### docs/run-event-stream.md

**Section:** "SSE wire protocol (frozen)" — after the `done` frame block

**What changed:** Added a paragraph after the `done` frame code block explaining that `done` at the outcome-spec confirmation gate is NOT a permanent terminal. The run stays `in_progress`; after confirmation the frontend reopens the stream from the last sequence. True permanent terminals (`run.completed`, `run.failed`, `run.cancelled`) are contrasted.

### docs/reference/events.md

**Section:** `coordinator.outcome_spec` event detail

**What changed:** Extended the existing description with a paragraph covering the reconnect flow. When autopilot is off (or on any interactive run), the SSE stream closes with `done` after this event, but the run is still live. After the user confirms, the frontend reconnects. When autopilot is on for a pickup run, `ScheduleUnattendedConfirm` fires automatically so the stream typically stays live without a manual reconnect.

---

## Verification

```
node scripts/gen-docs.mjs --check
OK: docs/reference/mcp-tools.md is in sync.
OK: .github/agents/agentweaver.agent.md is in sync.
OK: apps/Agentweaver.Api/Projects/Templates/agentweaver.agent.md is in sync.
Exit 0 — no drift.
```

## 2026-07-05: Tank — IPv6 / Lease / SessionAffinity Fix Report

**Source:** decisions/inbox/tank-ipv6-fix.md  
**Merged by:** Scribe  

# Tank — IPv6 / Lease / SessionAffinity Fix Report
**Run:** `9a8b1f70` / child `a82dfc5c`  
**Commit:** `4974b87`  
**Date:** 2026-06-30

---

## Bug 1 — AgentHost IPv6-only bind ✅ FIXED

**Where the bind was set:** `apps/Agentweaver.AgentHost/Program.cs` — the PoC (non-mTLS)
branch at line 44–49. The original code called `kestrel.ListenAnyIP(a2aPort)` which Kestrel
resolves to `IPAddress.IPv6Any` → `http://[::]:8088`. On a single-stack IPv4 cluster the pod
podIP is `10.244.x.x`; dialling an IPv6-only listener on an IPv4 address times out.

**Fix applied:** Changed to `kestrel.Listen(IPAddress.Any, a2aPort)` → `http://0.0.0.0:8088`.
`System.Net` was already imported; no other changes needed. The mTLS path (`Kestrel:Endpoints`
from ConfigMap) is unaffected — it is driven entirely by the mounted `appsettings.k8s.json`.

---

## Bug 2 — PodLeaseStaleTtl too short ✅ FIXED

**Where TTL is defined:** `apps/Agentweaver.Api/Coordinator/CoordinatorReconciler.cs` line 71.
Configurable via `Coordinator:PodLeaseStaleTtlSeconds`; hardcoded default was **60 s**.

**Fix applied:** Default increased to **120 s** (`Math.Max(10, staleSecs)` still applies).
120 s > 90 s `/healthz` probe timeout + margin. Doc comment on `_staleLeaseTtl` updated.

**Heartbeat interval:** `CoordinatorHeartbeatService` ticks every **10 s** (configurable via
`Coordinator:HeartbeatIntervalSeconds`, default 10). The sweep (CoordinatorReconciler) runs on
the same tick via a reaper interval (default 12 ticks ≈ every 120 s). Heartbeat fires well
under 120 s / 4 = 30 s — no risk of the lease expiring between heartbeats.

---

## Bug 3 — UI graph divergence (sessionAffinity) ✅ FIXED (stop-gap)

**Manifest changed:** `k8s/api-service.yaml`

Added:
```yaml
sessionAffinity: ClientIP
sessionAffinityConfig:
  clientIP:
    timeoutSeconds: 10800  # 3 hours
```

`RunStreamStore` is per-replica in-memory. Without sticky routing each browser refresh could
land on the non-owning replica which has only the bare coordinator node. `ClientIP` affinity
pins each client to one replica for 3 hours. **Real fix:** move `RunStreamStore` to shared
state (Redis / DB-backed pub-sub) — tracked separately.

---

## Build Results

| Project | Result |
|---------|--------|
| `Agentweaver.AgentHost` | ✅ Build succeeded — 0 warnings, 0 errors |
| `Agentweaver.Api` | ✅ Build succeeded — 0 warnings, 0 errors |

---

## Image Rebuild Matrix (for Link)

| Image | Action | Reason |
|-------|--------|--------|
| `agentweaver-agent-host` | **MUST rebuild** | IPv6 → IPv4 bind fix in Program.cs |
| `agentweaver-api` | **MUST rebuild** | Lease TTL fix + `4a774d3` routing fix already on main |
| `agentweaver-frontend` | Retag from `006457a` | No changes |
| `agentweaver-mcp` | Retag from `006457a` | No changes |

## 2026-07-05: tank-kv-bypass — API pre-resolves GitHub token for /configure

**Source:** decisions/inbox/tank-kv-bypass.md  
**Merged by:** Scribe  

# tank-kv-bypass — API pre-resolves GitHub token for /configure

**Author:** Tank (Backend Engineer)  
**Date:** 2026-06-30  
**Requested by:** Ahmed

## Problem

Kata VM pods cannot call `login.microsoftonline.com` or `agentweaver-kv.vault.azure.net`.
Cilium FQDN policies use eBPF DNS interception, which does not cross the kata VM guest kernel
boundary. Every `/configure` call was hanging for 86s (the `DefaultAzureCredential` timeout)
then returning 500.

## Fix Applied

API resolves the user's GitHub OAuth token from Key Vault (the API already has KV access) and
forwards it in the `/configure` body as `GitHubAccessToken`. The pod uses it directly — no
outbound Azure AD or Key Vault calls from inside the guest kernel.

## Files Changed

### `apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs`
- Added `GitHubAccessToken` property (`public string? GitHubAccessToken { get; private set; }`)
- Updated `TryConfigure` signature to accept `string? gitHubAccessToken`; stores it (null if blank)
- Updated `InitializeFromOptions` to set `GitHubAccessToken = null` (env-var path never has it)

### `apps/Agentweaver.AgentHost/Program.cs`
- Added `GitHubAccessToken` field to `ConfigureRequest` record with doc comment
- Updated `/configure` handler: passes `body.GitHubAccessToken` to `TryConfigure` and `ConfigureAsync`

### `apps/Agentweaver.AgentHost/AgentHostStartupService.cs`
- Added `string? gitHubAccessToken` parameter to `ConfigureAsync` signature (value is already
  stored on `AgentHostRuntimeState` before this is called; parameter is accepted for API symmetry)

### `apps/Agentweaver.AgentHost/KeyVaultUserTokenProvider.cs`
- Added fast path in `GetStoredCredentialAsync`: checks `_runtimeState.GitHubAccessToken` first.
  When set, builds a `StoredCredential { Status="signed-in", AccessToken=... }` and returns it
  immediately — skipping `SecretClient.GetSecretAsync` entirely.

### `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`
- Added `using Agentweaver.Domain;`
- Added `IGitHubTokenStore? _tokenStore` field and ctor param (`tokenStore = null`)
- Added `ResolveGitHubAccessTokenAsync(userId, ct)` helper: calls `_tokenStore.GetAsync(ForUser(userId))`,
  returns `entry.AccessToken` when `SignedIn`, logs warning and returns null on failure (never throws)
- Updated `LaunchAgentHostPodAsync` to await `ResolveGitHubAccessTokenAsync` and pass result to
  `CallAgentHostConfigureAsync`
- Updated `CallAgentHostConfigureAsync` signature: added `string? gitHubAccessToken` param;
  includes it in the JSON body posted to the pod

### `apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs`
- Added `using Agentweaver.Domain;`
- Added `IGitHubTokenStore? _tokenStore` field and ctor param (`tokenStore = null`)
- Passes `_tokenStore` to `KubernetesSandboxExecutor` constructor
  (`IGitHubTokenStore` is registered as singleton in `Agentweaver.Api/Program.cs`, so DI will
  inject it automatically when the router is resolved)

## Build Result

```
Agentweaver.AgentHost → Build succeeded. 0 Warning(s), 0 Error(s)
Agentweaver.Api       → Build succeeded. 0 Warning(s), 0 Error(s)
```

## Test Results

337 tests run; **325 passed, 12 failed**. All 12 failures are pre-existing DB-connectivity
failures (`SandboxPolicyPreserveTests`, `Spec018PodReleaseTests`,
`CollectiveAssemblyScribeApiAuthTests`) requiring a live Postgres instance — confirmed identical
on `main` before this change via `git stash` verification.

## Commit

SHA: `4f92936`

```
fix: API resolves GitHub token and passes to /configure — remove pod KV dependency
```

## 2026-07-05: Tank — New project dialogs and suggested blueprint

**Source:** decisions/inbox/tank-new-project-dialogs.md  
**Merged by:** Scribe  

# Tank — New project dialogs and suggested blueprint

Date: 2026-07-05T19:48:55-07:00
Owner: Tank (Backend engineer, full-stack for task)
Branch/worktree: `squad/new-project-dialogs` in `C:\Users\asabbour\Git\aw-tank-newproject`
Commits: `6ac7bc7`, `dfee329`, `f082591`

## Decision

The Create blank project and Create project from GitHub surfaces were redesigned as two-column modals in `apps/web/src/pages/ProjectGalleryPage.tsx`. Shared blueprint/template rendering and flows were consolidated in `apps/web/src/components/BlueprintPicker.tsx` and reused by both dialogs.

## Suggested blueprint design

I did **not** reuse the recent blueprint-generation matcher directly. That matcher lives in the model-backed blueprint generation path (`CopilotBlueprintGenerator`) and chooses/generates workflows from a free-text operational prompt; it is not a lightweight repository-analysis endpoint for project creation.

I added a focused backend-backed feature:

- `POST /api/blueprints/suggest`
- DTOs in `BlueprintDtos.cs`
- service `GitHubRepoBlueprintSuggestionService`
- DI registration in `Program.cs`

The service analyzes real GitHub repository signals only:

- repository metadata from `/repos/{owner}/{repo}`: name, description, topics, `has_issues`
- language mix from `/languages`
- root structure from `/contents`

Deterministic mapping:

- AI/LLM/agent/prompt/RAG/OpenAI/Semantic Kernel/LangChain signals -> `blueprint-ai-agent-engineering`
- docs/blog/content/site with no code -> `blueprint-content-authoring`
- PRD/roadmap/product/prototype/UX/design without code -> `blueprint-product-management`
- code languages or app/API/service/devops/container/IaC signals -> `blueprint-software-development`
- otherwise low-confidence general `blueprint-software-development`

The endpoint uses the existing caller-scoped GitHub token provider when available, but can still analyze public repositories without fabricating data. If parsing or GitHub analysis fails, it returns `fallback: true` with rationale so the UI shows Templates instead of hard-failing.

## Data wiring

Real data reused:

- templates: existing `GET /api/blueprints`
- generation: existing `POST /api/blueprints/generate`
- GitHub accounts/orgs/repos: existing `/api/github/accounts` and `/api/github/repos`
- suggested: new `POST /api/blueprints/suggest`

Fallback states:

- malformed or unavailable templates keep the no-blueprint path usable
- suggestion failures fall back to template cards
- generation failures show inline errors and do not block creation

## Merge overlap

Potential overlap is limited to shared blueprint/template UI in `apps/web/src/components/BlueprintPicker.tsx`. No Overview or Observability pages were edited, but Trinity/Link may need to merge if they also changed shared blueprint card components.

## Validation

All requested validation passed before commit:

- `npm --prefix apps/web run build`
- `npm --prefix apps/web test -- --run` (62 files, 471 tests)
- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release --no-restore`
- `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj -c Release --filter "Blueprint|Project|GitHub|Suggest" --no-restore`

## 2026-07-05: 2026-06-30T16-40-01: Remove spec.env from AgentHost SandboxClaim to restore warm pool assignment

**Source:** decisions/inbox/Tank-remove-spec-env-from-agenthost-sandboxclaim-to-res.md  
**Merged by:** Scribe  

### 2026-06-30T16-40-01: Remove spec.env from AgentHost SandboxClaim to restore warm pool assignment
**By:** Tank
**What:** Remove spec.env from AgentHost SandboxClaim to restore warm pool assignment
**References:** KubernetesSandboxExecutor.cs, k8s/sandbox-template-agenthost.yaml, KubernetesSandboxExecutorClaimTests.cs
**Why:** ## Root Cause

The v0.5.0 agent-sandbox controller bypasses warm pool pod adoption whenever `spec.env` or `spec.volumeClaimTemplates` are present on a `SandboxClaim`. `CreateAgentHostClaimAsync` was injecting 5 static env vars, causing every run to cold-start a new kata VM (~90s) instead of binding to a pre-warmed pool pod (~instant).

Controller log: `"Bypassing warm pool adoption because custom configuration is provided (env or volume claim templates)"`

## Env Var Audit

| Var | Was in claim | Now lives in |
|-----|-------------|-------------|
| AgentHost__WorkingDirectory | injected | SandboxTemplate (/workspace) |
| AgentHost__RepositoryPath | injected | SandboxTemplate (/workspace) |
| AgentHost__A2APath | injected | agenthost-config ConfigMap (/a2a/agent) |
| AgentHost__RequireMtls | injected | agenthost-config ConfigMap (false) |
| AgentHost__Port | injected | agenthost-config ConfigMap (8088) |

## Changes Made

- k8s/sandbox-template-agenthost.yaml: Added WorkingDirectory and RepositoryPath env vars; applied to cluster (no image rebuild needed)
- apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs: Removed entire env block from CreateAgentHostClaimAsync
- tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs: Assert spec.env is absent (not present with static vars)

## Verification

- Cluster template confirmed with correct env vars
- Tests: 4/4 passed
- Build: succeeded, 0 warnings

## Commit

SHA: cc7dd9d
fix: remove spec.env from AgentHost SandboxClaim - restore warm pool assignment

## Expected Impact

Claims have no spec.env -> controller assigns pre-warmed pool pod immediately -> ~1s healthz instead of ~90s cold VM start.

## 2026-07-05: Decision: routing.md signal derivation

**Source:** decisions/inbox/tank-routing-signal.md  
**Merged by:** Scribe  

# Decision: routing.md signal derivation

**Date:** 2026-06-30  
**Author:** Tank (Backend Engineer)

## Context

`BuildRoutingMd` in `CastingService.cs` previously emitted raw role titles as routing signals. This caused ambiguity when two roles shared keywords (e.g., Lead PM vs Product Marketing Manager).

## Decision

Signal derivation order: **Responsibilities (first 2 items) → Summary → Title**

```csharp
var signal = member.Role.Responsibilities.Count > 0
    ? string.Join(", ", member.Role.Responsibilities.Take(2))
    : !string.IsNullOrWhiteSpace(member.Role.Summary)
        ? member.Role.Summary
        : member.Role.Title;
```

Used `Take(2)` — responsibilities in catalog roles are short single-line phrases (e.g., "Define product vision and feature scope", "Prioritize work against user value"). Two items fit comfortably in a routing signal without being verbose.

## Verification

- `Role.Responsibilities` is `IReadOnlyList<string>` ✓  
- `Role.Summary` is `string` ✓  
- Spot-checked `lead_pm.json` (3 responsibilities, all ≤6 words) and `product_marketing_manager.json` (5 responsibilities, all concise)  
- Build: succeeded, 0 warnings, 0 errors  
- 12 pre-existing test failures due to `no such column: w.CoordinatorPodId` SQLite schema mismatch — unrelated to this change  
- Commit: `4a774d3`

## 2026-07-05: RCA — Orchestration DAG agent-task cards missing Cost pill + Sandbox pod chip

**Source:** decisions/inbox/trinity-dag-pills-rca.md  
**Merged by:** Scribe  

# RCA — Orchestration DAG agent-task cards missing Cost pill + Sandbox pod chip

**Author:** Trinity (Frontend) · **Date:** 2026-06-30 · **For:** Coordinator (do not open issue)
**Surface:** CoordinatorRunPage orchestration graph (apps/web) · **Live frontend image:** `9413053`

## TL;DR
Two DIFFERENT root causes, both isolated to the **agent-task card renderer** `SubtaskNode`
(`apps/web/src/pages/CoordinatorRunPage.tsx:559-719`), which is a *custom* node type
(`coordinatorNodeTypes = { ...workflowNodeTypes, subtask: SubtaskNode }`, line 722). The
coordinator / RAI / merge / scribe cards use the generic `WorkflowNode`
(`apps/web/src/components/WorkflowGraphPanel.tsx:502`) which renders BOTH pills — that is why
only those cards show the pod chip in the screenshot.

### 1. Pod-name chip — RENDER GAP (deterministic, source-provable)
- `SubtaskNode` **never imports or renders `<PodIndicator>`**. `grep PodIndicator CoordinatorRunPage.tsx` = 0 hits at both `9413053` and `main` HEAD (identical → not a stale image).
- `SubtaskNodeData` (lines 448-466) carries **no `executionPodName`** field, even though the
  topology node it already holds (`d.topoNode`, `TopologyNodeState`) DOES expose `executionPodName`
  (`apps/web/src/api/types.ts:598`), and `useRuntimeInfo()` exposes the global API pod fallback.
- Generic `WorkflowNode` renders `<PodIndicator podName={nodeExecutionPodName ?? globalPodName}/>`
  (`WorkflowGraphPanel.tsx:573-524`) → coordinator/RAI cards show `agentweaver-ap1-...` (the API
  pod via global fallback). Subtask cards have no equivalent code path.

### 2. Cost pill — DATA-AVAILABILITY GAP (render + wiring are CORRECT)
- `SubtaskNode` **does** render `<CostChip totalNanoAiu={d.totalNanoAiu} totalTokens={d.totalTokens}/>`
  (`CoordinatorRunPage.tsx:640`), and the data is wired: `totalNanoAiu/totalTokens` come from
  `childUsageByRun[childRunId]` (lines 1527-1528), populated by `getRunUsage(childRunId)` for each
  `getCoordinatorChildren(runId)` child (lines 1112-1128, 1050-1056).
- `CostChip` returns `null` when both `total_nano_aiu` and `total_tokens` are null/0
  (`apps/web/src/components/CostChip.tsx:18-22,33-34`). So the pill is hidden because per-child
  usage resolves empty/zero at render time — NOT because the component is missing.
- Backend records usage per child `RunId` from `agent.turn.usage` events
  (`apps/Agentweaver.Api/Runs/TokenUsageProjectionService.cs:160-173`), served by
  `GET /api/runs/{id}/usage` (`UsageEndpoints.cs:16-23`).
- Likely concrete cause: `childUsageByRun[childRunId]` empty — either child runs emitted no
  usage yet / projection not caught up, or a **childRunId key mismatch** between
  `childrenData[].childRunId` (lookup key) and `topoNode?.childRunId` (node key, line 1506).
  Needs one runtime check: `GET /api/runs/{childRunId}/usage` for a finished child.

## Fix direction
1. **Pod chip (do first — guaranteed fix):** import `PodIndicator` + `useRuntimeInfo` in
   CoordinatorRunPage; in `SubtaskNode` render `<PodIndicator podName={d.topoNode?.executionPodName ?? globalPodName} />`
   above the card, mirroring `WorkflowNode`. (Optionally add `executionPodName` to `SubtaskNodeData`.)
2. **Cost pill:** verify `GET /api/runs/{childRunId}/usage` returns non-zero for completed children.
   If non-zero → fix the `childRunId` key matching so `childUsageByRun[childRunId]` resolves. If zero
   → backend gap: ensure child runs project `agent.turn.usage` (cross-replica) so per-child usage is
   persisted. Frontend render path needs no change.

## Duplicate check
No duplicate. All 10 open `type:bug` issues (#24-#40) are cross-replica state-consistency bugs;
none is a UI-render gap. (`gh issue list --repo sabbour/agentweaver --state open --label type:bug`)

## 2026-07-05: Trinity docs update — summary

**Source:** decisions/inbox/trinity-docs.md  
**Merged by:** Scribe  

# Trinity docs update — summary

## Change 1: Cluster diagnostics docs

### docs/reference/cluster-diagnostics.md

- Added `warm_pools`, `sandbox_objects`, and `sandbox_claims` arrays to the JSON example.
- Added all three to the top-level fields table with types and descriptions.
- Added new DTO sections: `WarmPoolStatusDto`, `SandboxObjectDto`, `SandboxClaimObjectDto` — each with a full field table sourced from `SystemDiagnosticsDto.cs`.

Key field details from the actual DTOs:
- `WarmPoolStatusDto`: name, desired_replicas, ready_replicas, available_replicas, status (healthy/warning/critical), age_seconds (nullable)
- `SandboxObjectDto`: name, phase (running/pending/standby/unknown), ready, pod_name (nullable), template_ref (nullable), warm_pool (nullable), age_seconds (nullable)
- `SandboxClaimObjectDto`: name, phase (bound/pending/unknown), ready, run_id (nullable), bound_sandbox (nullable), sandbox_template_ref (nullable), warm_pool (nullable), age_seconds (nullable)

Note: The existing JSON example shape in this doc did not match the actual DTO (uses `component_health`/`namespace_quota` keys that are not in `ClusterDiagnosticsDto`). I made surgical additions only — the three new arrays were appended to the example without restructuring the existing example content.

### docs/experience/cluster-page.md

- Added **Warm pool** row to the KPI cards table: shows N/M ready replicas across all SandboxWarmPool objects.
- Added three new table sections after **Pending-capacity runs table**:
  - **Warm pools table** — one row per SandboxWarmPool CRD, columns: Name, Desired, Ready, Available, Status
  - **Sandbox objects table** — all Sandbox objects, columns: Name, Phase, Ready, Pod, Warm pool, Age
  - **Sandbox claims table** — all SandboxClaim objects, columns: Name, Phase, Ready, Run, Bound sandbox, Warm pool, Age

## Change 2: Frontend SSE reconnect and spec refresh UX

### docs/deep-dive/frontend.md

Added a new subsection "Reconnect after coordinator confirmation" under the useRunStream streaming section. It describes:
- Stream closes with `done` when run enters `awaiting_confirmation`
- `OutcomeSpecPanel` calls `fetchSpec()` on stream close to load the persisted spec from REST
- `terminalRef` reset + `reconnectKey` increment causes `useRunStream` to reopen the stream
- User clicking Confirm triggers `onReconnect()` which increments `reconnectKey` again
- New coordinator events flow without a manual page refresh

### docs/experience/coordinator-orchestration.md

Added a paragraph after "Confirmation is not just an acknowledgement..." in the "Confirming the spec" section:
- After clicking Confirm, the UI automatically reconnects the live stream (no manual refresh needed)
- The coordinator stream closed at the awaiting_confirmation gate; confirmation reopens it
- "View session" works correctly from the spec authoring state: it expands the session column if collapsed before scrolling

## Change 3: Architecture diagram

### docs/aks-architecture.excalidraw

Updated element `warmpool-text`. The previous text was:
```
WarmPools
generic ×3
AgentHost ×2 standby
```
Updated to:
```
SandboxWarmPool CRD
generic x3
AgentHost x2 standby
```

This makes the label accurate — the warm pools are managed by the SandboxWarmPool CRD, not just named "WarmPools".

The coordinator confirmation gate is not shown in the diagram (the diagram covers infrastructure/deployment topology, not run lifecycle flow). No change was made for the confirmation gate — the diagram's scope does not include run orchestration flow, so adding it would be out of scope for an architecture diagram.

## 2026-07-05: 2026-07-01T09:00:00Z: Trinity frontend review

**Source:** decisions/inbox/trinity-frontend-fixes.md  
**Merged by:** Scribe  

### 2026-07-01T09:00:00Z: Trinity frontend review
**By:** Trinity

**Fix 1 (SSE reconnect):** COMPLETE
- `terminalRef.current = false` reset is placed at line 194, after the runId-change block and before the `if (!runId) return` guard. This means every effect re-run (runId change OR explicit `reconnect()` call incrementing `reconnectKey`) resets the terminal flag so a fresh connection can open.
- `reconnect` is correctly exported from `useRunStream`'s return value (line 307).
- The `done` SSE event sets `terminalRef.current = true` at line 252 inside `connectOnce`, which causes the outer `connect` loop to exit (`while (!signal.aborted && !terminalRef.current)`). The reset at line 194 is the only gate; since it runs at the top of the effect body, calling `reconnect()` → `setReconnectKey(k => k + 1)` → effect re-runs → `terminalRef.current = false` → fresh `connect()` loop starts. Correct.
- No other problematic `terminalRef.current = true` assignments. The one at line 270 (`TERMINAL_EVENT_TYPES.has(evtType)`) is correct — those events (run.completed, run.failed, merge.completed etc.) ARE permanent terminal states.

**Fix 2 (spec refresh):** COMPLETE
- `useEffect` at lines 232-236 correctly calls `void fetchSpec()` when `streamStatus === 'done'`, with deps `[streamStatus, fetchSpec]`. Correct.
- `onReconnect?.()` is called at line 294, which is after `if (updated) setSpecFromApi(updated); else await fetchSpec()` (lines 290-291), so the UI reflects the confirmed state before SSE reconnects. Correct.
- `handleDecline` is not present in `OutcomeSpecPanel.tsx` — decline is handled elsewhere. No `onReconnect` needed on decline since declining ends the run permanently.

**Fix 3 (scrollToSession):** COMPLETE
- `scrollToSession` at lines 1645-1652 calls `setSessionCollapseOverride(false)` then defers scroll via `requestAnimationFrame`. Correct.
- `sessionCollapsed = sessionCollapseOverride ?? inSpecAuthoring` (line 1232). Setting the override to `false` (not null/undefined) means `false ?? inSpecAuthoring` evaluates to `false` — the session column expands and stays expanded. No useEffect resets `sessionCollapseOverride` to null, so it remains `false` past the RAF.
- `reconnectStream` destructured at line 1013; passed as `onReconnect={reconnectStream}` to `<OutcomeSpecPanel>` at line 2050. Correct.
- Run-status auto-refresh: the polling loop at lines 1105-1153 continues while `awaiting_confirmation` (not in the TERMINAL set `['complete','failed','blocked','declined']`). It polls every 4s and updates `runLevelStatus`. The SSE reconnect via `onReconnect` delivers post-confirmation events. No explicit `fetchRun()` in the reconnect path is needed — existing polling is sufficient.

**Fix 4 (ClusterPage):** COMPLETE (with two fixes applied)

Issues found and fixed:
1. **Missing `export function ClusterPage() {` declaration** — the component body started at line 308 with `const styles = useStyles()` but had no enclosing function declaration. Added `export function ClusterPage() {` between the helper component definitions and the page body.
2. **`ClusterDiagnosticsDto` fields not optional** — `warm_pools`, `sandbox_objects`, `sandbox_claims` were typed as required arrays in `types.ts`. Changed to optional (`?`) so older API responses during rolling deploy do not cause runtime type mismatches.
3. **Warm pool KPI card** — Added a KPI card showing "Warm pool: N/M ready" (total `ready_replicas` / total `desired_replicas` across all pools) to the KPI row, rendered only when `data.warm_pools?.length > 0`. Follows the existing `KpiCard` pattern.
4. Verified: `WarmPoolsTable`, `SandboxObjectsTable`, `SandboxClaimsTable` all use `data.warm_pools ?? []` etc., which is safe with the now-optional types.

**Build result:** PASS
- `npx tsc --noEmit` — exit code 0, no errors.
- `npm run build` — exit code 0, built in 2.12s.

**Changes made:**
- `apps/web/src/pages/ClusterPage.tsx` — added `export function ClusterPage() {` declaration; added warm pool KPI card
- `apps/web/src/api/types.ts` — made `warm_pools`, `sandbox_objects`, `sandbox_claims` optional in `ClusterDiagnosticsDto`

## 2026-07-05: 2026-07-02T07-43-19: Run detail agent identity uses role-badged avatars with model-label fallback

**Source:** decisions/inbox/Trinity-run-detail-agent-identity-uses-role-badged-avatars.md  
**Merged by:** Scribe  

### 2026-07-02T07-43-19: Run detail agent identity uses role-badged avatars with model-label fallback
**By:** Trinity
**What:** Run detail agent identity uses role-badged avatars with model-label fallback
**References:** Issue #148, apps/web/src/components/AgentIdentity.tsx, apps/web/src/utils/agentIdentity.ts, apps/web/src/components/runs/AgentTokenBreakdown.tsx, apps/web/src/components/runs/TransactionTracePanel.tsx
**Why:** For run-detail observability surfaces, agent usage and trace rows now render a shared identity treatment: generated agent avatar plus a Fluent role badge when the cast name can be resolved from the team roster, and a role icon plus humanized model label when only raw model telemetry is available. This keeps the UI readable without adding new backend dependencies and avoids emojis while still giving each agent a recognizable visual identity.

## 2026-07-05: Trinity run-tabs bug fix

**Source:** decisions/inbox/trinity-run-tabs.md  
**Merged by:** Scribe  

# Trinity run-tabs bug fix

2026-07-05: Root cause had two backend layers. First, run artifact endpoints treated `assemble_ready` child runs as non-terminal, so per-agent Changes/Files could not read persisted child diffs. Second, coordinator dispatch published a `childRunId` in subtask/topology events before `StartChildRunAsync` inserted the child `Run` row and stream entry; the slide-up panel immediately followed that id via `/api/runs/{childRunId}` and `/stream`, causing transient 404s that left the panel empty. Fixed artifact endpoints for `RunStatus.AssembleReady` and moved childRunId publication until after the child row/stream exists.

---

## 2026-07-06T22:05:00Z: New project dialogs v2 shared dialog foundation (v0.7.12)

**Date:** 2026-07-06T22:05:00Z  
**Author:** Tank  
**Status:** IMPLEMENTED / DEPLOYED TO STAGING

**Decision:** Blank and From-GitHub project creation now share one dialog shell and shared Blueprint panel/tab implementation. The Templates tab is identical across Blank and GitHub flows, `View all templates` switches to the shared Templates tab, and the only no-blueprint affordance is the shared footer action.

**Rationale:** Shared shell/panel code removes duplicated divergent UI behavior while preserving flow-specific left-column content and tab sets.

**Notes:** Personal repositories are surfaced by rendering the authenticated user from `GET /api/github/accounts` first (`You` badge) and browsing repos through `GET /api/github/repos?account=<login>`. Suggested shows only the recommendation. Frontend-only commits: `112addc`, `b066eed`, `0e7d92f`.

---

## 2026-07-06T22:05:00Z: Outcome-spec gate keeps draft panel visible during transient 404s (v0.7.12)

**Date:** 2026-07-06T22:05:00Z  
**Author:** Trinity-7  
**Status:** IMPLEMENTED / DEPLOYED TO STAGING

**Decision:** The outcome-spec gate panel remains visible while polling after transient draft 404s and shows `Drafting…` instead of hiding. Confirm has pending, success, 409-conflict, and error states with a double-submit guard; pre-draft run failure is terminal.

**Rationale:** Transient or mis-targeted draft 404s should not look like the gate disappeared, and confirmation needs clear state transitions under contention.

**Notes:** Frontend-only commits: `a7e7645`, `de4bed6`.

---

## 2026-07-06T22:05:00Z: v0.7.12 documentation treats UI work as refinements

**Date:** 2026-07-06T22:05:00Z  
**Author:** Dozer-1  
**Status:** IMPLEMENTED

**Decision:** v0.7.12 docs update existing project, blueprint, coordinator, and experience documentation rather than creating new concept pages or regenerating MCP/reference docs.

**Rationale:** The iteration changed frontend behavior and staging UX, not backend contracts. Existing pages are the correct user-facing surface.

**Notes:** Docs cover shared Blueprint panel tabs, Templates parity, `View all templates`, single footer No blueprint action, bounded dialog scrolling, personal GitHub account/repo sourcing, persistent Drafting state, transient 404 polling, terminal pre-draft failure, Confirming/409/double-submit feedback, and screenshot coverage. Commit: `d3e9f81`.

---

## 2026-07-06T22:05:00Z: Live 404s were benign; duplicate cross-replica subtask dispatch remains a backend follow-up

**Date:** 2026-07-06T22:05:00Z  
**Author:** Morpheus-3  
**Status:** FORENSICS COMPLETE / FOLLOW-UP DEFERRED

**Decision:** Treat the observed live 404s for runs `f1f14868` and `1c18977c` as benign transient or mis-targeted reads, not a v0.7.11 identity or installation-scope regression. Separately track the newly found backend bug: duplicate cross-replica subtask dispatch plus a subtask reading a sibling subtask's isolated worktree caused sandbox denial and `assembly_blocked` on run `1c18977c`.

**Rationale:** Outcome-spec draft and confirm succeeded, and the work plan persisted. The problematic old child run was unrelated to the current flow. The duplicate dispatch/worktree isolation bug is real but outside the v0.7.12 UI iteration and was deferred by Ahmed.

## 2026-07-06T07-29-39Z: v0.8.0 staging release deployed; await Ahmed validation before close/merge/push

**Date:** 2026-07-06T07:29:39-07:00  
**Author:** Scribe  
**What:** Recorded the v0.8.0 staging release/deploy session and merged pending decision inbox entries.  
**References:** #50, #51, #56, #59, #112, #114, #116, #166, #195, #196, #197, #198, #199, #200, #201, v0.8.0

Coordinator completed a release/deploy session, not domain implementation. The integration branch `release/v0.7.0` absorbed the wave branches for Neo run-page polish (#195), Apoc approval-404 child-run routing (#196), Sparks child-output propagation (#197), Link trace hierarchy (#166; follow-up #200), Oracle lost coordinator messages (#199), Smith project skills catalog (#51/#56; 6ca3298), Trinity conversational browser TUI/console (#50; fdb2ad5), plus docs from Dozer (37793de). Cross-merge regression f375f96 fixed the timeline reducer `child_approval` case shadowing from #50/#196; 483 web tests passed. VERSION was bumped to 0.8.0 (5950071). Docs build passed. Four ACR images were built and pushed at `v0.8.0` (`api`, `frontend`, `mcp`, `agent-host`). Staging AKS `agentweaver-aks-2` rolled out `api`, `frontend`, `mcp`, and `worker` healthy; frontend returned HTTP 200 and `/api/health` was OK at https://agentweaver.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io/. Tags created locally: `v0.7.12` at 9beb937 and `v0.8.0` at 37793de. Nothing has been pushed to origin.

Pending until Ahmed validates: do not close issues #50/#51/#56/#166/#195/#196/#197/#199 or earlier #116/#59/#112/#114; do not merge to main; do not push branches or tags. Open backlog remains #198 (retire WorkflowRunPage/fold ArtifactBrowser), #200 (tool-span parenting), and #201 (backend conversational operator-agent run type = TUI Option B).

## 2026-07-06T07-29-39Z: Inbox merge — v0.7.12/v0.8.0 wave decisions

**Date:** 2026-07-06T07:29:39-07:00  
**Author:** Scribe  
**What:** Merged 17 decision inbox entries after the release/deploy handoff.  
**References:** decisions/inbox/*

- Cypher recorded workflow generation editing: `base_workflow_id` edits saved/built-in workflows, `base_yaml` supports iterative unsaved drafts, built-ins stay immutable via project-owned copies, and blueprint validation now includes reachability/bindability checks for generated/inline/predefined flows.
- Dozer recorded v0.7.12/v0.8.0 docs surface work: orchestration console/gates docs in deep dive, reference, experience, navigation, landing card, and cross-links; `docs` VitePress build passed.
- Link recorded v0.7.12 staging deploy: rebuilt api/frontend/agent-host, retagged mcp, deployed to `agentweaver-aks-2`, all rollouts healthy, `/api/version` returned 0.7.12.
- Morpheus recorded blueprint gate awareness (#176/#187): shared prompt guidance for `build_test`, `rai`, `rubberduck`, and human-review gates, with weak generic matches returning `[]` so the generator can author specialized gated workflows.
- Morpheus recorded first-class `build_test`: runtime loader accepts `build_test`, graph binding uses `BuildTestTurnExecutor`, default role is `qa-engineer`, catalog/generated workflows express build/test/preview structurally, and editor palette exposes special gates.
- Morpheus recorded workflow RAI/Scribe dedupe: standalone workflows keep YAML RAI/Scribe, while coordinator decomposition filters platform-owned stages because coordinator assembly runs RAI/review/merge/scribe once on combined child output.
- Oracle recorded the lost coordinator messages RCA/fix (#199): non-owner replicas dropped in-memory-only `coordinator.steering` events; `CoordinatorSteeringService` now falls back to durable `IRunEventStream.AppendAsync`, with regression coverage and steering tests passing.
- Squad-Coordinator recorded first-class Build & Test gate direction: replace duplicated peer-review build/test prompts with platform-owned `build_test` node and canonical build/test/preview prompt.
- Squad-Coordinator recorded release versioning: the next release is minor `v0.8.0`, not another `v0.7.x` patch, because the wave ships new features.
- Squad-Coordinator recorded four-zone orchestration run page direction (#185): top run summary, left task tree, center graph, right session pane, one Message coordinator composer, action cards for approvals/questions/reviews/blocks, and debug details hidden by default.
- Squad-Coordinator recorded Outcome Plan direction (#188): replace the modal with a first-class Outcome Plan phase inside the four-zone run page, with inline Confirm/Clarify actions and one composer.
- Tank recorded approval/save backend fixes: explicit tool approval lifecycle state, late/double resolutions return renderable 200s, wrong ids return 404 `unknown`, expired resolution persists, and workflow saves extend allowed ids before registry reload.
- Trinity recorded coordinator session messages: coordinator sessions now render coordinator-specific timeline rows, collapse prompt scaffolding, and surface child approvals inline with actionable cards.
- Trinity recorded graph viewport/agents fixes: coordinator graph uses readable natural baseline with scroll overflow and hover minimap; the duplicate AGENTS summary block was removed.
- Trinity recorded Outcome Plan implementation: graph nodes precede work nodes, confirmation surface lives in AgentSessionPanel, Clarify uses the single composer, backend event/API names stay unchanged while UI says Outcome plan.
- Trinity recorded run-page redesign implementation (#185): persistent four-zone operator console, docked AgentSessionPanel, Messages/Changes/Files tabs, sequence-ordered deduped events, sticky input needs, and one composer; web build and 446 tests passed.
- Trinity recorded child tool approval routing: approval cards post to `childRunId`/`child_run_id` when present, falling back to page/session run id; web build/tests passed.


---

# Decision: Fix HIGH SSRF + review findings in skill import (commit da28b18)

- **Owner:** Cypher (Backend)
- **Requested by:** Ahmed (@sabbour)
- **Branch:** integration
- **Fix commit:** fefe437
- **Reviewed commit:** da28b18 ("Improve skill acquisition UX")

## FIX 1 — HIGH SSRF (blocks v0.9.0)

Previously `SkillImportSource.Parse` special-cased only `raw.githubusercontent.com`
and `github.com`; ANY other absolute URI fell through to
`return new SkillImportSource(raw, ...)` with `CloneUrl = raw`, which
`ProjectGitInitializer.Clone` handed to LibGit2Sharp with no host allowlist.
An authenticated project owner could make the server clone from arbitrary internal
HTTPS hosts (`kubernetes.default.svc`, `localhost:PORT`, `10.x`, metadata endpoints).
Aggravators: the caller's GitHub token was offered as Basic-auth password to
whatever host was cloned (token leak on 401), and raw `ex.Message` was returned to
the caller (blind-SSRF oracle for internal recon).

### Resolution (all three parts)
1. **Strict https + host allowlist in `Parse`** using `System.Uri`:
   - Produce a `CloneUrl` ONLY when `uri.Host` ordinal-ignore-case equals `github.com`
     AND `uri.Scheme == https`; produce a `RawSkillUri` ONLY for `raw.githubusercontent.com` + https.
   - Reject non-https schemes explicitly (http/git/ssh/file/ftp fail the `Uri.UriSchemeHttps` check).
   - Reject userinfo tricks: `https://github.com@evil.com/...` has `uri.Host == evil.com` (host check fails); also reject any non-empty `uri.UserInfo`.
   - Reject non-default ports (`github.com:1234`) via `!uri.IsDefaultPort`.
   - Removed the old `git@` SSH branch and the raw-URL fall-through. Any other host now throws
     `SkillImportException` (new exception type), surfaced as an **Invalid** import result.
2. **Credential scoping:** `CloneToTempAsync` resolves and offers the GitHub token ONLY when
   `SkillImportSource.IsAllowedCloneHost(repoUrl)` is true (scheme https, no userinfo, default port,
   host == github.com). Otherwise no credentials are supplied. Defense in depth on top of the allowlist.
3. **Generic caller-facing errors:** clone/fetch failures now return
   "Could not access repository (check the URL is a public GitHub repo)." while the detail is logged
   via `_logger.LogWarning`. Validation rejections (SkillImportException) surface their safe message as Invalid.

## FIX 2 — slash-branch ambiguity (Medium)

GitHub tree/blob URLs previously assumed the ref was exactly `parts[3]` and the subpath `parts[4..]`,
so a slash-containing branch (`release/v2`) could silently resolve the WRONG ref when a shorter ref
(`release`) also existed. Now `Parse` defers resolution: for tree/blob it stores `RefSegments = parts[3..]`
with `CheckoutRef = null`. After clone, `ResolveRefAsync` enumerates the repo's ACTUAL refs
(local + `origin/`-stripped remote branches, and tags) and **greedily matches the LONGEST ref that is a
prefix of the segments**; the remainder becomes the subpath. If no ref matches it throws
`SkillImportException` (fails loudly) rather than importing the wrong ref.

## FIX 3 — MCP description accuracy (Low)

`SkillTools.cs` `skill_import` description reworded: locations are REQUIRED when a source contains
multiple skills; omitting works only for a single-skill source. Also dropped the now-inaccurate
"git@ SSH URLs" mention from both `skill_import` and `skill_import_preview`.

## FIX 4 — dropzone dedupe (Low) — DEFERRED

Skipped per Ahmed: the web build is currently red from another agent's unrelated WIP, so a web change
can't be cleanly validated. To be folded in later.

## Validation (backend, isolated from the red web build)
- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release --no-restore` → Build succeeded, 0 warnings/errors.
- `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --filter "Skill" -c Release` → Passed: 56, Failed: 0.

## Files changed
- `apps/Agentweaver.Api/Skills/SkillCatalogService.cs`
- `apps/Agentweaver.Mcp/Tools/SkillTools.cs`
- `tests/Agentweaver.Tests/Skills/SkillCatalogTests.cs`

## Note
The shared `integration` worktree had concurrent WIP from other agents; my uncommitted edits were
discarded once by another agent's git operation and had to be re-applied. Committed only my three files
(never `git add -A`).

---

# Decision: v0.9.0 docs wave

- **Author:** Dozer (DevRel/Technical Writer)
- **Requested by:** Ahmed (@sabbour)
- **Date:** 2026-07-06
- **Branch:** integration
- **Commit:** 8548875

## Summary
Ran the `docs-feature` skill to document the v0.9.0 wave. Since every shipped item is a refinement of an already-documented feature, I updated the existing experience/reference/deep-dive pages to keep the deployed state and docs in sync (no new pages, so no nav/landing-card changes needed). All claims are code-grounded.

## Pages updated
1. **Console true-TUI** (`/console`, Trinity 30f5c99) → `docs/experience/browser-console.md`: added a "Terminal interface" section (full-height scrollback, dark surface + monospace, bottom CLI prompt, blinking cursor); refreshed the Source table with `BrowserConsole.tsx` line refs.
2. **Skills acquisition UX + security allow-list** (Tank da28b18 + Cypher fefe437) → `docs/experience/project-skills.md`, `docs/reference/project-skills.md`, `docs/deep-dive/project-skills.md`: documented Add/Generate/Import + multi-skill discovery preview. **Doc-sync fix:** removed the now-stale `git@` SSH import claim and documented the SSRF allow-list — imports are restricted to `github.com` and `raw.githubusercontent.com` only (grounded in `SkillCatalogService.cs:772`).
3. **Run artifact browser reuse** (Switch 227f297) → `docs/experience/runs-board-watch.md` (new "Browsing artifacts" section) + `docs/experience/coordinator-orchestration.md`: shared Artifact Browser (compact Changes list + Files tab folder tree) reused per-run, per-agent session, and on the coordinator run.
4. **Coordinator graph polish** (Niobe 0b67f3e) → `docs/experience/coordinator-orchestration.md`: top-down layout with clean vertical connector edges (no wavy S-curves) and centered/aligned ranks so parallel subtasks align horizontally under their parent.
5. **Calmer tool-call rows** (Mouse 02583fa) → `docs/experience/runs-board-watch.md` Tool-call cards: single-line rows, action-specific FluentUI icons, muted metadata, and completed calls settle (no perpetual spinner/clock).
6. **Live coordinator send #199** (Morpheus 1cb5f2b) → `docs/reference/coordinator.md`: added the `send` verb to the steering table and fixed the stale "steering surface is stop/redirect/amend only" text; documented the DB-backed, replica-safe queued-send (queued→relayed CAS) delivered at a safe boundary, with `stop` as the only hard interrupt.

## Excluded
- Operator agent (#201) — designed but not implemented; left out per instruction (the pre-existing "planned" note in browser-console.md was left untouched, not expanded).

## Validation
- Constitution VIII respected: no emoji added; FluentUI icon references described by name.
- `cd docs; npm run build` → **green** (vitepress build complete, exit 0).
- Committed only docs files to `integration` (7 files); did not touch VERSION/deploy scripts (owned by Link).

---

# Deploy Decision: v0.9.0 → STAGING AKS (release candidate)

**Author:** Link (DevOps/Platform, Matrix Squad)
**Requested by:** Ahmed (@sabbour)
**Date:** 2026-07-06T11:15 PT
**Branch:** `integration` (do NOT merge to main / do NOT close issues — Ahmed validates on staging first)
**Cluster:** agentweaver-aks-2 (RG agentweaver-rg, westus2) · ACR agentweaverregistry

## Summary
Cut and deployed **v0.9.0** to the STAGING AKS cluster image-efficiently. All app tiers rolled out and healthy (HTTP 200). Awaiting Ahmed's validation on staging.

## Version bump
- `VERSION` 0.8.0 → 0.9.0; committed ONLY the VERSION file to `integration`.
- Commit SHA: **cc60ea1e467f4dbfdc68a6450ff97b1ff7cae68c** ("chore: bump version to 0.9.0")
- IMAGE_TAG is derived from VERSION by scripts/aks/00-variables.sh → **v0.9.0** (not passed manually).

## Image strategy (image-efficient)
Rebuilt only changed images, in parallel via `az acr build` (4 independent cloud builds):
- **REBUILT @ v0.9.0:** agentweaver-api, agentweaver-frontend, agentweaver-mcp, agentweaver-agent-host
- **agent-host Domain determination:** REBUILT (not retagged). Verified ProjectReference chain — `apps/Agentweaver.AgentHost` references `Agentweaver.Domain` directly AND transitively via `Agentweaver.AgentRuntime → Agentweaver.AgentTools → Agentweaver.Domain`. Since `packages/Agentweaver.Domain` changed in this wave, agent-host must be rebuilt.

## Sandbox retag — NOT APPLICABLE (deviation from task instructions)
The task called for retagging `agentweaver-sandbox` from v0.8.0 → v0.9.0. This is **stale**: there is NO `agentweaver-sandbox` image.
- No sandbox Dockerfile / no sandbox dir; the build script (20-build-push-images.sh) only builds 4 images.
- ACR has no `agentweaver-sandbox` repository (only api, frontend, mcp, agent-host).
- The legacy image was intentionally removed: `k8s/sandbox-template-agenthost.yaml` notes it "supersedes the removed legacy sleep-infinity agentweaver-sandbox template/image", and `scripts/aks/40-verify.sh` FAILS if a legacy `agentweaver-sandbox` template/warm pool still exists.
- The `app: agentweaver-sandbox` pod label is now carried by the agent-host pods (for network policy targeting), not a separate image.
- Therefore an `az acr import` retag would have failed (no source). **This deploy has 4 images, all at v0.9.0** — not 5.

## ACR tags confirmed @ v0.9.0
- agentweaverregistry.azurecr.io/agentweaver-api:v0.9.0
- agentweaverregistry.azurecr.io/agentweaver-frontend:v0.9.0
- agentweaverregistry.azurecr.io/agentweaver-mcp:v0.9.0
- agentweaverregistry.azurecr.io/agentweaver-agent-host:v0.9.0

## Deploy
- Ran `scripts/aks/30-deploy.sh` from WSL (envsubst-rendered k8s/*.yaml — no direct kubectl apply of raw manifests).
- Passed ONLY TENANT_ID (72f988bf-86f1-41af-91ab-2d7cd011db47) and IDENTITY_CLIENT_ID (af4fe49e-9952-4d7a-b8a1-75476584c777), sourced from the existing agentweaver-api service account annotation. IMAGE_TAG derived from VERSION.
- Pre-normalized CRLF→LF on scripts/aks/*.sh and k8s/*.yaml (Windows checkout) to avoid `$'\r'` bad-interpreter failures.

## Rollout status (all Ready)
- deployment/agentweaver-api → agentweaver-api:v0.9.0 — 2/2 Running
- deployment/agentweaver-frontend → agentweaver-frontend:v0.9.0 — 2/2 Running
- deployment/agentweaver-mcp → agentweaver-mcp:v0.9.0 — 1/1 Running
- deployment/agentweaver-worker → agentweaver-api:v0.9.0 (worker shares the API image) — 1/1 Running
- sandboxtemplate/agentweaver-agent-host → agentweaver-agent-host:v0.9.0 (pod-per-run)

## Health validation
- Gateway IP: 20.115.198.61
- `/health` → 200 · `/` → 200 · `/api/health` → 200

## Live staging URL (for Ahmed to validate)
**https://agentweaver.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io/**
- API: https://agentweaver.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io/api/
- MCP: https://agentweaver.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io/mcp/
(zone suffix `6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io` is per-deploy — read from the managed DefaultDomainCertificate.)

## Not done (by design)
- No merge to main, no issue closures, no origin tag pushes. This is a staging RC for Ahmed's validation.

---

# Morpheus decision: #199 live coordinator send consumption

Issue: #199 follow-up requires live coordinator `send` directives to be consumed by the owning coordinator loop, while avoiding the dispatch-to-assembly race identified in operator-agent design review.

Decision: use status-scoped routing for `send`.

- The steering HTTP surface persists `send` as `queued` and emits `coordinator.steering(status=queued)`; it no longer final-applies sends itself.
- While `WorkPlan.Status == dispatching`, `CoordinatorSteeringQueue.TryTakeForChildAsync` may atomically claim queued `send`/`redirect`/`amend` for dispatch child-boundary injection (`queued -> relayed`). Failed-child recovery remains redirect-only.
- While assembly is blocked, `CoordinatorAssemblyService` owns queued sends: it claims a queued `send`, marks it `applied`, emits `coordinator.steering(status=applied)`, and retries assembly.
- `stop` remains the only hard mid-turn interrupt.

Rationale: this preserves at-most-once durable cross-replica consumption while preventing a dispatch drain from stealing a send as the run transitions into `assembly_blocked`, which would otherwise starve the assembly retry loop.

References inline: apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs; apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs; tests/Agentweaver.Tests/Coordinator/CoordinatorSteeringServiceTests.cs; tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs; GitHub issue #199.

---

### 2026-07-06T16-06-59: Use true MCP-backed server-side Operator run for browser console
**By:** Morpheus
**What:** Use true MCP-backed server-side Operator run for browser console
**Why:** Decision: For GitHub issue #201, implement the browser console free-form prose path as a new server-side Operator run that uses the GitHub Copilot SDK/MAF agent loop and registers the Agentweaver MCP HTTP server via SessionConfig.McpServers. Do not add a new WorkflowNodeType; the operator is a control-plane conversation, not a project workflow node. Use server-minted per-user Agentweaver OAuth JWTs for the MCP Authorization header, and keep the browser as a thin stream/timeline client.

Rationale: Ahmed asked for an agent that literally drives MCP. The MCP server is already the authoritative catalog (apps\Agentweaver.Mcp\Program.cs:58,104; .github\agents\agentweaver.agent.md:89-100), and the Copilot SDK exposes MCP HTTP server config plus SessionConfigBase.McpServers (GitHub.Copilot.SDK.xml:31890-31904,32361-32364). In-process function tools would be simpler but duplicate the 79-tool MCP catalog and weaken the single-source-of-truth design. The operator run should reuse existing run-event streaming and Timeline handling for agent/tool/HITL events.

References: issue #201; design file C:\Users\asabbour\.copilot\session-state\674093ad-19e5-42cd-908a-74cda0c64342\files\operator-agent-design.md; #199 live-send fix section.

---

# Decision: Tool-row redesign + "perpetual clock" root-cause fix

Author: Mouse (Frontend) · Branch: integration · Date: 2026-07-06

## Problem
Ahmed flagged that completed tool calls in the session/timeline transcript keep
showing a perpetual pending "clock" that never resolves, and that ordinary tool
activity renders as bulky, tall cards instead of a calm, dense CLI-style list.

## Root cause of the unsettled "clocks"
The shared timeline reducer (`apps/web/src/timeline/reducer.ts`) settles a
`tool.call` when a `tool.result`/`tool.error` with a matching `callId` arrives
(pairing via `pendingToolCalls`). A tool stayed `settled:false` forever — and
therefore rendered a never-ending running indicator — in two cases:

1. **callId key mismatch.** The reducer only read `payload['callId']`
   (camelCase). Live SSE is camelCase, but some persisted/replayed sources use
   snake_case `call_id` (the backend's own `BoardProjectionService` already reads
   BOTH). If a completion carried `call_id`, it never matched the originating
   call → perpetual pending.
2. **Missing completion event.** When the agent SDK provides no `ToolCallId`,
   the runner mints a fresh random id for the completion
   (`GitHubCopilotAgentRunner`/`CopilotAIAgent`: `... ?? Guid.NewGuid()`), so the
   completion can never pair with the call. Such calls never settle even though
   the agent has moved on.

## Fix (surgical, reducer only)
- Added `extractCallId(payload)` = `callId ?? call_id`; used on `tool.call`,
  `tool.result`, `tool.error` so pairing is casing-agnostic (#1).
- Added `settlePendingCallsInTurn()` grace fallback: on `agent.turn.end` and on
  run termination (`run.completed`/`run.failed` via `closeOpenTurn`), any tool
  call still pending in that (now-closed) turn is marked `settled:true` with no
  error. Finished work resolves to a normal completed row instead of spinning
  forever (#2). Real completions still settle immediately and win (result kept).

## Row / icon / metadata redesign (`ToolCallCard.tsx`)
- Single-line, borderless rows (unchanged structure), now with a **leading
  action icon that varies by tool type** (FluentUI only, Constitution VIII):
  read/view = DocumentArrowDown, code file (.ts/.tsx/… path) = Code,
  search/find = Search, write/edit/patch = DocumentEdit, list = Folder,
  delete = Delete, run_command = Code, report_intent/outcome = Info,
  fallback = Wrench. (Replaces the previous one-size Wrench.)
- **Muted secondary metadata** after the label (dimmed `colorNeutralForeground4`):
  `N lines` (reads), `N matches` (search), `N results` (find), `N items` (list),
  derived defensively from result content; hidden when nothing meaningful.
- **Single-line ellipsis** on the title (`whiteSpace:nowrap; overflow:hidden;
  text-overflow:ellipsis; minWidth:0`) so long paths/queries never wrap.
- **Status icon semantics (no bare clock):** running = Spinner; stream errored +
  unsettled = Warning ("Result not received"); settled OK = CheckmarkCircle;
  error = ErrorCircle; sandbox/non-zero-exit/intent-not-fulfilled = Warning.

## Clustering
Existing `TurnGroup` clustering (consecutive non-`report_intent` tool calls fold
into a collapsible "Used N tools" / intent-header toggle) was left intact and now
benefits from the calmer rows. Not rewritten (per scope).

## Duration marker
Not added: the timeline `RunStreamEvent` stream carries no per-tool timestamps, so
a real duration cannot be computed without new backend data. Left as a follow-up;
did not fabricate a value.

## AgentSessionPanel duplicate clock logic — FOR SWITCH TO FOLD IN
`apps/web/src/components/AgentSessionPanel.tsx` has its OWN, duplicate tool model
and clock logic that I did NOT touch (Switch owns it, mid-refactor):
- Line ~1921: `tool.settled ? <CheckmarkCircleFilled/> : <ClockRegular/>` — this
  is the literal bare clock Ahmed sees. It should mirror the shared card
  (spinner while running / checkmark when done), never a bare clock.
- Lines ~1050-1076: its settling map is buggy — `tool.call` keys under
  `String(payload.callId ?? sequence)` but `tool.result/error` looks up
  `String(payload.callId ?? '')`. If `callId` is ever absent, the two keys differ
  → clock forever. Should use the same `callId ?? call_id` normalization AND a
  turn-end/run-end grace-settle like the reducer now does.
- `StatusGlyph` (line ~926): `kind === 'awaiting'` → `<ClockRegular/>`. For a
  genuine pending-human-approval state a clock is fine but should carry a tooltip
  explaining it's awaiting approval; it must not be used for finished tool work.
Once AgentSessionPanel adopts the shared reducer/`ToolCallCard`, these all resolve
automatically.

## Validation
Targeted (full web build is red due to Switch's unrelated AgentSessionPanel WIP):
- `npm test -- --run src/__tests__/ToolCallCard.test.tsx src/__tests__/timelineReducer.test.ts src/__tests__/TurnGroup.test.tsx` → all pass
  (added: snake_case call_id settles; grace-settle on turn.end and run.completed;
  real result preserved; settled row has no spinner; line/match metadata renders;
  live unsettled shows spinner; title ellipsis styling).
- `npx tsc --noEmit` → 0 errors in my files (ToolCallCard, reducer, tests).

## Files changed
- apps/web/src/timeline/reducer.ts
- apps/web/src/components/ToolCallCard.tsx
- apps/web/src/__tests__/timelineReducer.test.ts
- apps/web/src/__tests__/ToolCallCard.test.tsx

---

# Decision: Coordinator run-graph layout fixes (Niobe, Frontend)

Date: 2026-07-06
Branch: integration
Commit: 0b67f3e
Files: apps/web/src/components/WorkflowGraphPanel.tsx, apps/web/src/utils/dagLayout.ts, apps/web/src/__tests__/dagLayout.test.ts

## Problem
Coordinator run graph (TB/vertical) looked wonky: wavy S-curve connectors,
spine (Coordinator→Outcome→Work) not vertically centered, parallel subtasks
not centered under their parent.

## Bug 1 — SpineEdge hardcoded for LR → wavy edges in TB
`SpineEdge` used X-axis bezier control offsets and X-midpoint junctions, which
bend a top→bottom edge sideways (S-curve). Same edge type serves BOTH the LR
topology graph and the TB coordinator graph, so it must be orientation-aware.

Fix: detect orientation from geometry —
`const vertical = Math.abs(targetY - sourceY) >= Math.abs(targetX - sourceX);`
- Vertical (TB): bezier control points offset on Y axis
  (`dy = max(48, |ty-sy|*0.5)`, `M sx,sy C sx,sy+dy tx,ty-dy tx,ty`); junction
  anchored on X — fan-out → junctionX=sourceX, fan-in → junctionX=targetX,
  else midpoint; junctionY = (sourceY+targetY)/2. Mirror of the LR logic.
- Horizontal (LR): EXACTLY the original code path — unchanged. Junction dot +
  shared-segment bundling and label rendering preserved on both axes.
LR/topology graph appearance is therefore unchanged (verified: LR branch is the
original code, gated behind `!vertical`).

## Bug 2 — dagLayout TB rows left-aligned → spine zig-zag
TB branch packed every rank from `crossX = MARGIN` left→right, so single-node
spine ranks sat far left while fan-outs also started at left; nothing centered.

Fix: split the TB and LR branches. For TB, compute each rank's row width
(sum of node widths + CROSS_GAP between them), take the max across ranks as the
shared center axis `centerX = MARGIN + maxRowWidth/2`, and start each rank at
`crossX = round(centerX - rowWidth/2)`. Single-node ranks land on the axis;
fan-out rows are symmetric under their parent. Added a min-x guard that shifts
everything so no negative coordinates and graph starts at MARGIN. LR branch left untouched. Kept NODE_W/NODE_H/NODE_TYPE_* exports and all signatures.

## Tests
Added apps/web/src/__tests__/dagLayout.test.ts (3 tests):
single-node spine rank centered over multi-node fan-out on shared axis; linear
spine vertically aligned; no negative coordinates. All pass.

## Build/Test status
- My 2 source files + test: zero TS diagnostics (verified via IDE).
- dagLayout + topologyReducer + coordinatorPlanFilter suites: 23/23 pass.
- `npm --prefix apps/web run build` currently FAILS, but ONLY on
  ToolCallCard.tsx (unused-import TS6133 errors) — another agent's uncommitted
  WIP, out of my scope. None of my files contribute errors.

---

# SSRF Re-Review — skills-import (commit fefe437)

**Reviewer:** Seraph (security)
**Requested by:** Ahmed (@sabbour)
**Fix author:** Cypher (locked out of this verdict)
**Date:** 2026-07-06
**Verdict:** 🟢 GREEN — original HIGH SSRF fully closed; no new security issues found. Cleared for v0.9.0.

## Scope verified
`git show fefe437` on `apps/Agentweaver.Api/Skills/SkillCatalogService.cs`, `apps/Agentweaver.Mcp/Tools/SkillTools.cs`, plus surrounding methods (`Parse`, `CloneToTempAsync`, `IsAllowedCloneHost`, `ResolveRefAsync`, `CheckoutRef`, `DiscoverSkills`, `FetchRawSkillAsync`, `ProjectGitInitializer.Clone`, `SkillPaths.NormalizeRelative`).

## Original finding — CLOSED
The old `Parse` fall-through `return new SkillImportSource(raw, ...)` that cloned any URL verbatim is **removed**. Parse now throws `SkillImportException` for any host other than the two allowlisted GitHub hosts (SkillCatalogService.cs:823-825).

## Allowlist attacked — holds
- **Host check is on parsed `uri.Host`, not the raw string** — SkillCatalogService.cs:790,804 use `uri.Host` with `OrdinalIgnoreCase`. ✅
- **Userinfo trick** `https://github.com@evil.com/...` → `uri.Host == evil.com` → rejected; additionally any non-empty `uri.UserInfo` is rejected outright (SkillCatalogService.cs:787). ✅
- **Scheme gate**: non-https (http/git/ssh/file/ftp) rejected at SkillCatalogService.cs:785. ✅
- **Port gate**: `!uri.IsDefaultPort` rejected at :787. ✅
- **Case/IDN/trailing-dot**: `GitHub.com` handled by OrdinalIgnoreCase; `github.com.` (trailing dot) and `xn--` punycode do NOT equal `github.com` and are rejected. ✅
- **Clone URL is canonicalized**, not passed through: always rebuilt as `https://github.com/{owner}/{repo}.git` from parsed segments (:806-807). Attacker cannot inject an alternate host into the clone target even if Parse were bypassed. ✅
- **No other clone/fetch caller bypasses the allowlist.** Both `CloneToTempAsync` call sites (:273,:331) and both `FetchRawSkillAsync` call sites (:277,:335) flow from `SkillImportSource.Parse`. (`ProjectService.cs:169` clone is the unrelated project-creation feature, not in this diff/flow.) ✅

## Credential leak — CLOSED
- Token wired ONLY when `IsAllowedCloneHost(repoUrl)` (exactly https + github.com + default port + no userinfo) — SkillCatalogService.cs:643-645; otherwise `null` → passed as empty password. ✅
- Raw fetch path (`FetchRawSkillAsync`, :718) attaches **no credentials** at all. ✅
- Since the clone URL is hardcoded to github.com, libgit2 redirect-to-attacker-host-with-creds is not reachable (github will not redirect to an internal host). Noted as residual/theoretical only.

## Error oracle — CLOSED
Validation failures surface `SkillImportException.Message` (static, non-sensitive strings). Clone/checkout/fetch failures now return the generic `"Could not access repository (check the URL is a public GitHub repo)."` with the exception detail logged server-side only (:308-311, :374-378). Previous `$"Could not access repository: {ex.Message}"` recon oracle removed. ✅

## Ref resolution / injection — CLEAN
`ResolveRefAsync` (:657) enumerates the cloned repo's real branch/tag names and only checks out a `candidate` that `refNames.Contains(...)` — the checkout ref is always a genuine ref, via LibGit2Sharp API (no shell). No command/argument injection. If no ref matches it fails loudly. ✅

## Path traversal — CLEAN
Subpath remainder is run through `SkillPaths.NormalizeRelative` (rejects rooted, drive/UNC, empty, `.`/`..`, colon segments) before `Path.Combine`, with `IsReparsePoint` symlink guards in `DiscoverSkills` (:513-516, plus IsContained helper). System.Uri also normalizes dot-segments. ✅

## Advisory follow-ups (non-blocking)
1. (Info) Consider explicitly disabling HTTP redirect following on `RawHttp` (raw fetch) and documenting libgit2 redirect behavior — currently not exploitable because both hosts are github-owned, but it's cheap defense-in-depth.

No blocking issues. Skills-import counts toward v0.9.0.

---

# Decision: Reuse shared ArtifactBrowser in AgentSessionPanel Changes/Files tabs

**Author:** Switch (Frontend)
**Date:** 2026-07-06
**Branch:** integration
**Commit:** 227f297

## Problem
Ahmed: "We already have a button that shows Artifacts and it loads up the right
thing across the workspace. Why are we not using the same underlying component
for the individual runs? Why isn't the Coordinator run showing those?" Plus the
per-run session panel Changes/Files tabs were "way too much space" (bulky rows
with chevron + full path + "Unified" + "Preview" + copy icon), and the Files tab
"needs to show an artifact browser (with folders, etc.)."

## Root cause
`AgentSessionPanel.tsx` had a hand-rolled DUPLICATE of the Changes/Files
renderers that already exist (and look/work correctly) in the shared
`ArtifactBrowser.tsx`. Its Changes tab used bespoke `diffCard`/`diffHeader`/
`Unified`/`Preview`/copy markup; its Files tab just re-rendered the same flat
changed-files list (no folder tree). It also fetched only the flat
`getRunFiles` list and never wired the coordinator's assembly adapter, so the
coordinator run surfaced no artifacts.

## Decision
Eliminate the duplication by reusing the shared components instead of adding a
third implementation:

1. **Extracted** an exported `CompactChangesList` in `ArtifactBrowser.tsx`
   (header + `renderFlatChangesList`) as the single source of truth for the
   dense changed-files row look. `FileTreePanel` continues to use the same
   underlying `renderFlatChangesList`.
2. **Changes tab** in `AgentSessionPanel` now renders `CompactChangesList`
   (status icon + bold filename + right-aligned `+N -M` + status badge).
   Clicking a row opens the shared `FileViewerModal` diff view. Removed the
   per-row Unified label, always-visible Preview button, and copy clutter.
3. **Files tab** now renders the shared `FilesTabPanel` collapsible FOLDER TREE,
   wired to the run's full workspace (`getRunWorkspace` / assembly workspace),
   not just changed files.
4. Both tabs + preview are driven by the shared `useArtifactBrowser` hook.
   Coordinator-aggregate nodes (coordinator, work-plan, outcome-plan) route
   through the assembly `artifactAdapter` (getAssemblyFiles/Workspace/Diff/
   Content), threaded from `CoordinatorRunPage` (the existing `coordAdapter`,
   which was defined but never passed to the panel). Per-subtask runs use the
   standard per-run endpoints (undefined adapter). This is why the coordinator
   run now surfaces artifacts.
5. Deleted dead bespoke styles/state: `diffCard`, `diffHeader`,
   `diffHeaderToggle`, `diffPath`, `diffMode`, `diffContent`, `summaryRow`,
   `summaryText`, `diffList`, `filesList`, `filesListRow`, `footerLink`,
   `formatBytes`, and the manual getRunFiles/getRunFileContent/getRunFileDiff
   fetch effects + expandedPaths/diffs/loadingDiffs/previewPath state.

## Constraints honored
- FluentUI icons only (Constitution VIII); no emoji.
- Did not touch dagLayout.ts, run graph nodes, ToolCallCard, WorkflowGraphPanel,
  or console/*. Changes limited to AgentSessionPanel.tsx, ArtifactBrowser.tsx
  (small shared extraction), and CoordinatorRunPage.tsx (adapter wiring).
- Existing test IDs (`session-tab-changes`, `session-tab-files`) preserved.

## Validation
- `npm --prefix apps/web run build` — passes.
- `npm --prefix apps/web test -- --run` — AgentSessionPanel (5) and
  ArtifactBrowser (17) tests pass. 3 unrelated tests (debug_blueprint,
  OutcomePlanPanel, ProjectGalleryGitHub) flake only under full parallel load;
  all pass in isolation and are unrelated to these components.

---

# Decision: Browser console (/console) redesigned as a true terminal UI (TUI)

**Agent:** Trinity (Frontend) · **Date:** 2026-07-06 · **Branch:** integration
**Scope:** `apps/web/src/console/BrowserConsole.tsx` only (console shell). No shared
Timeline/TurnGroup/ToolCallCard/AgentSessionPanel/graph code touched.

## What changed (visual only)
- **Full-height column flex layout.** Removed the cramped `maxHeight: 42%` transcript
  (the empty void). Now: compact header (top) → scrollback (`flex:1`, scrolls) →
  CLI prompt line (pinned bottom). When a run is bound, the run panel shares the
  scrollback space with `flex: 2 1 0` so nothing collapses to a void.
- **Terminal surface.** Dedicated local color scope (`TERM` constant) — dark
  `#0b0f14` surface, monospace stack, dense 1.5 line spacing. Fluent theme tokens
  still used inside the bound-run panel (Timeline/gates) so it stays theme-aware.
- **CLI prompt line.** Leading context segment `~\Git\agentweaver [<project|integration>] ❯`
  with the input inline as a borderless, transparent Textarea that reads as part of
  the prompt line. A blinking block cursor (`▋`, CSS step-end animation) shows when
  idle/empty. Enter-to-send and all handlers (`parseInput`, `steerCoordinator`,
  pending-goal flow, gates) are unchanged — pure restyle.
- **Dense scrollback rows.** Small left gutter/bullet marker per row (`❯` for user,
  `·` for system), tight 1px gaps, no bulky cards for ordinary lines.
- **Compact terminal toolbar.** Header + run-panel controls (incl. "Full run
  (Changes / merge)" link) kept, made compact.

## How terminal styling reached the shared <Timeline> WITHOUT editing it
Applied a **scoped wrapper rule** on the console's `timelineScroll` container:
`'& *': { fontFamily: TERM.mono }`. This forces monospace on Timeline descendants
from the console scope only. The shared Timeline/row components are untouched, so
other pages that render Timeline keep their normal look.

## Seams left clean (constitution VII/III)
No browser-side LLM or tool routing added. `TurnSourceKind` remains `'coordinator'`;
the backend operator-agent source (#201) can drop in by binding `runId + kind`.
Constitution VIII respected: FluentUI icons only, no emoji (glyphs used are
typographic terminal symbols `❯ · ▋`, not emoji).

## Validation
- Console tests: `npm --prefix apps/web test -- --run src/console` → 16/16 pass.
- BrowserConsole.tsx compiles clean (no TS errors attributable to this file).
- NOTE: full `npm run build` currently fails due to unrelated uncommitted WIP in
  `apps/web/src/components/AgentSessionPanel.tsx` (another agent, out of my scope) —
  ~40 `TS2304 Cannot find name` errors. Downstream test failures all trace to that
  broken import. Not caused by and not fixable within this console-only scope.

# Decision: Show assigned skills on agent detail panel

**Author:** Trinity (Frontend Engineer)
**Date:** 2026-07-06
**Task:** Surface skills assigned to an agent on the Agents/Team page.

## Decisions

1. **Data source: `apiClient.listSkills(projectId)`** (not `listSkillAssignments`).
   `SkillDto` already carries `name`, `description`, `status`, and `assigned_agents: string[]`
   in a single call, so I filter client-side to `assigned_agents.includes(member.name)` —
   giving name + description + status without a second fetch. Matches the exact casing/field
   used by SkillsPage.tsx.

2. **Placement: both Overview and Capabilities tabs.** Overview is the primary home
   (below Charter path / Recent history). I also replaced the dead Capabilities placeholder
   ("Capabilities are defined in the agent's charter.") with the same assigned-skills section,
   since skills *are* the agent's capabilities. The shared `skillsSection` JSX is rendered in
   both tabs (DRY).

3. **Lazy-load on either skills tab.** Skills fetch fires when Overview OR Capabilities is
   active, mirroring the existing `historyLoaded` / `charterLoaded` lazy-load pattern with a
   `cancelled` guard. Loading/error states use `Spinner` + `MessageBar` like the other sections;
   a skill fetch failure only sets `skillsError` and never breaks the rest of the panel.

4. **Skill name links to the Skills page** (`/projects/:projectId/skills`). SkillsPage has no
   per-skill deep-link route, so I did not over-engineer a skill-specific anchor.

5. **Status badge only when not Active.** `FluentUI Badge` (tint) with warning/danger color for
   `missing`/`malformed`, using a local `skillStatusColor` helper mirroring SkillsPage. Empty
   state shows "No skills assigned". FluentUI `PuzzlePiece20Regular` icon (no emoji, per
   constitution VIII).

## Files changed
- apps/web/src/pages/TeamPage.tsx
- apps/web/src/__tests__/TeamPage.test.tsx

# Decision: Unify `/api` base-path convention (fix GitHub sign-in "unauthorized")

**Author:** Trinity (Frontend) · **Date:** 2026-07-06 · **Branch:** integration

## Root cause (verified against live staging)
GitHub sign-in returned "unauthorized" because the frontend built the authorize URL as
`${API_URL}/auth/github/authorize` with staging `API_URL="/api"`, producing
`/api/auth/github/authorize`. The API only serves the redirect endpoint at the origin root.

Live curl confirmation (host `agentweaver.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io`):
- `GET /api/auth/github/authorize` → **401** (no such route; auth middleware returns `{"error":"unauthorized"}` for all unknown `/api/*`)
- `GET /auth/github/authorize` → **302** → `https://github.com/login/oauth/authorize?...` (correct)

Secondary latent bug: the codebase was split-brained. `client.ts request()` OWNS a single `/api`
prefix and expects `baseUrl` = origin only, but several raw fetches (skills upload, review submit,
SSE stream) omitted `/api`. With staging `API_URL="/api"` the raw fetches happened to work while
`request()` would double-prefix to `/api/api/...`; on localhost the reverse. The two could never be
consistent under a single config.

## Decision
Adopt ONE convention everywhere: **`API_URL` / `baseUrl` = API ORIGIN ONLY (no `/api`)**, and every
XHR call site adds a single `/api` prefix. Browser-redirect endpoints (`/auth/github/*`) live at the
origin root.

- Deployed default `AGENTWEAVER_API_URL` changed from `/api` → `""` (same-origin) in
  `k8s/frontend-deployment.yaml`, `apps/web/Dockerfile`, and `apps/web/docker-entrypoint.sh`.
- `config.ts` now treats an empty string as a VALID value ("same origin") via an explicit
  `typeof === 'string'` check instead of `||` truthiness (which would have fallen through to the
  localhost dev default).
- Fixed the raw fetch call sites to add `/api` (client.ts skills-upload, review, health; sse.ts stream).

## Resulting on-the-wire behavior
| | localhost (`API_URL=http://localhost:5000`) | staging (`API_URL=""`) |
|---|---|---|
| GITHUB_AUTHORIZE_URL | `http://localhost:5000/auth/github/authorize` | `/auth/github/authorize` |
| XHR (request()) | `http://localhost:5000/api/...` | `/api/...` |
| session exchange | `http://localhost:5000/api/auth/session/exchange` | `/api/auth/session/exchange` |

No `/api/api/...` double-prefix in any case. Localhost dev (5173/5000) unchanged.

## Constraints honored
Thin client (no business logic moved). No FluentUI/emoji changes. Did not touch vite.config.ts,
start-dev.ps1, or appsettings.Development.json. Images NOT rebuilt — Link owns the frontend redeploy.

# Decision: Run-detail page UX fixes (Trinity, Frontend)

Date: 2026-07-06
Author: Trinity (Frontend Engineer)
Scope: Coordinator run-detail page — graph, session/event stream, composer. Did NOT touch OrchestrationsPage.tsx or backend (Neo's concurrent scope).

## Context
Ahmed raised four UX complaints on the Coordinator RUN DETAIL page
(`apps/web/src/pages/CoordinatorRunPage.tsx` + `apps/web/src/components/AgentSessionPanel.tsx`).

## Decisions

### 1. Responsive DAG reflow (fit-to-width via ResizeObserver)
- Added a `ResizeObserver` on the graph scroll viewport (`CoordinatorRunPage.tsx`).
- Measured container width drives `graphFitScale = clamp(containerWidth / naturalWidth, 0.5, 1.5)`.
- Combined with the existing Ctrl+Scroll `zoom` as `effectiveGraphZoom = zoom * graphFitScale`, applied via the existing CSS `zoom` mechanism. Removed the `minWidth:'100%'` hack.
- Rationale: the graph is a fixed-size xyflow canvas with `minZoom=maxZoom=1`; a true `fitView` would require unlocking zoom and risk regressing the tuned #185/#195 layout. Scaling the existing canvas to the measured width fills whitespace when wide and avoids horizontal scroll when narrow, while keeping vertical scroll for tall DAGs. Fit is width-driven (uniform zoom scales height proportionally). Built ON the existing layout rather than replacing it.

### 2. Wider session log panel
- Changed run-console `bodyGrid` columns from `300px / minmax(460px,1fr) / minmax(380px,440px)` to `280px / minmax(420px,1fr) / clamp(480px,34vw,640px)`.
- Chose a larger responsive default (`clamp`) over a draggable splitter to minimise risk in a shared worktree; the spec allowed "at least a larger default".

### 3. "Message coordinator" no longer hidden under the "Start task" FAB
- The global `StartOrchestrationFab` is `position:fixed` bottom-right (z-index 100) and overlapped the docked composer's send control.
- Fix is self-contained in `AgentSessionPanel.tsx`: added `dockedComposerStack` style with `paddingBottom:'84px'`, applied only in the docked variant, so the composer clears the FAB. Deliberately did NOT edit the shared `StartOrchestrationFab`/`AppShell` to avoid cross-agent conflicts.

### 4. Collapse low-signal technical events by default (#122)
- Added a "Show technical details" FluentUI `Switch`, OFF by default, in the messages-tab toolbar of `AgentSessionPanel.tsx`.
- Client-side classification only (thin client, constitution III): system-prompt scaffolding rows, tool-call plumbing (shell/file/command), and file-write rows are technical; agent/coordinator messages, instructions, narrative activity lines, and human-facing approvals are high-signal.
- Default view hides technical content (collapsed, NOT deleted); toggle reveals everything. Turns containing only technical content are filtered from the default view.
- Icons: reused FluentUI components only (no emoji), per constitution VIII.

## Validation
- `npm --prefix apps/web run build` — clean.
- `npm --prefix apps/web test -- --run` — 512/513 pass; the 1 failure is a flaky 5s timeout under full-suite parallel load (`coordUx` "run tree navigation" test passes in isolation at ~1s). Not caused by these changes.
- Added/adjusted tests in `AgentSessionPanel.test.tsx` and `CoordinatorRunPage.coordUx.test.tsx` for the default-hides-technical + toggle-reveals behavior.

## Files changed
- apps/web/src/pages/CoordinatorRunPage.tsx
- apps/web/src/components/AgentSessionPanel.tsx
- apps/web/src/__tests__/AgentSessionPanel.test.tsx
- apps/web/src/__tests__/CoordinatorRunPage.coordUx.test.tsx

# Decision: Skills UX fixes (agent role + folder drag-drop import)

**Author:** Cypher (Frontend)
**Date:** 2026-07-06
**Branch:** integration

## Context
Two bugs in the Skills feature UI (`SkillsPage.tsx`, issues #51/#56):
1. Assignment UI showed only agent NAME, no role — "Agent name without role isn't useful" (Ahmed).
2. Dragging a FOLDER onto the import dropzone failed with `net::ERR_ACCESS_DENIED`; single-file drop worked.

## Task 1 — Show agent role
- Reused the existing team members API (`apiClient.getTeam(projectId).members`, already loaded in the page) which carries `name` + `role_title`.
- Built a `name → role_title` map and a `labelForAgent(name)` helper that renders `Name — Role`, falling back to the bare name for unknown/system agents.
- Applied to both the assignment checkboxes and the assigned-agent chips in the catalog.

## Task 2 — Folder drag-drop root cause + fix
**Root cause:** the drop handler read `e.dataTransfer.files`. For a dropped FOLDER the browser puts a bogus directory entry there (not the contained files); reading/uploading that directory handle throws `net::ERR_ACCESS_DENIED`. Additionally, the previous `uploadSkills` appended every file under the SAME form field name `files`, and the backend pairs relative paths via `form["path:{fieldName}"].FirstOrDefault()` — so with a shared field name all files collapsed onto the first file's path.

**Fix:**
- New pure, unit-tested helper `apps/web/src/utils/skillDrop.ts` that walks the `webkitGetAsEntry()` / FileSystemEntry tree: recurses directories via `createReader().readEntries()` (looping until the batch is empty), captures entries synchronously before awaiting (DataTransferItemList is neutered after the handler yields), and collects files with their folder-relative paths. Skips oversized (>1 MiB) and known-binary files gracefully.
- `SkillsPage.onDropUpload` uses the entry API when available and falls back to plain `dataTransfer.files` for single-file drops (kept working).
- Extended `apiClient.uploadSkills` to accept `File | {file, relativePath}` items and to give each file a UNIQUE form field name (`files0`, `files1`, …) with a paired `path:{field}` field, so the backend correctly pairs each file with its own relative path.

## Constraints honored
- FluentUI icons only (no emoji); thin client (reused APIs, no business logic); root-caused (folder drop now works, not disabled).
- Stayed within skills-owned files; left Neo's `cancelRun` (client.ts) untouched.

## Files changed
- `apps/web/src/pages/SkillsPage.tsx`
- `apps/web/src/api/client.ts` (skills section only)
- `apps/web/src/utils/skillDrop.ts` (new)
- `apps/web/src/__tests__/SkillsPage.test.tsx`
- `apps/web/src/__tests__/skillDrop.test.ts` (new)

## Validation
- `npm --prefix apps/web test -- --run SkillsPage skillDrop` → 11 passed.
- `npm --prefix apps/web run build`: my files compile clean (0 diagnostics). Build currently fails only in `AgentSessionPanel.tsx` — Trinity's concurrent WIP, not touched by me.

# Decision: Document v0.9.2 orchestration + skills UX wave

- **Author:** Link (Platform Engineer)
- **Date:** 2026-07-06
- **Branch/commit basis:** `integration` == `main` == `388b993`
- **Scope:** docs-only (no app code touched)

## Context

The v0.9.2 orchestration + skills UX wave merged to `main`. Documented across the applicable doc facets per the `docs-feature` skill, grounding every claim in real `file:line` sources.

## What was documented

1. Stop & delete orchestrations in `docs/reference/api.md` and `docs/experience/coordinator-orchestration.md`.
2. Tool-approval routing to owning child run in `docs/reference/api.md`.
3. Run-page UX in `docs/experience/coordinator-orchestration.md`.
4. Skills UX in `docs/experience/project-skills.md`.

## Validation

- `cd docs; npm run build` → green (build complete, no errors).
- Cross-link anchor verified against generated HTML: `api.md#delete-api-runs-id`.

## Notes / non-goals

- No new pages created — reused existing gold-standard pages.
- No landing card / nav / screenshot facets needed for this incremental wave.
- Did not stage `apps/web/index.html` or any non-docs changes.

# Decision: Ship v0.9.1 sign-in fix to staging RC

- **Author:** Link (DevOps/deploy)
- **Date:** 2026-07-06T12:10:34-07:00
- **Requested by:** Ahmed (@sabbour)
- **Branch:** integration (shared worktree)
- **State backend:** local

## Context
v0.9.0 staging RC had a GitHub sign-in bug (`/api` base-path). Trinity fixed it in `65284b8e82526176cf4ca6d49e18e8e1ea292aff` (code-review APPROVE + security GREEN, build clean, 500/500 tests). Only the frontend changed since v0.9.0; the API/MCP/AgentHost/Domain/sandbox were untouched.

## Decision
Cut an immutable **v0.9.1** RC to staging so Ahmed can re-test sign-in. Image-efficient: rebuild ONLY the frontend; retag the other three server-side. Do NOT overwrite v0.9.0, do NOT merge to main, do NOT push origin tags, do NOT close issues. Staging RC only.

## Rollout result — all at v0.9.1
- agentweaver-api: v0.9.1, ready 2/2
- agentweaver-frontend: v0.9.1, ready 2/2
- agentweaver-mcp: v0.9.1, ready 1/1
- agentweaver-worker: agentweaver-api:v0.9.1, ready 1/1
- agent-host SandboxTemplate: agentweaver-agent-host:v0.9.1

## Fix verification (live cluster)
HOST: `agentweaver.6a4a0fdca7653f00012ffe86.westus2.staging.aksapp.io`
- `GET /auth/github/authorize` -> **302** ✓
- `GET /api/health` -> **200** ✓
- `GET /health` -> **200** ✓
- `GET /env-config.js` -> `window.__AGENTWEAVER_CONFIG__ = { API_URL: "" }` ✓

# Decision: Stop & Delete orchestrations from the Orchestrations list page

**Author:** Neo (Backend/Full-stack)
**Date:** 2026-07-06
**Requested by:** sabbour (Ahmed)

## Context
Ahmed wanted Stop and Delete actions directly on the Orchestrations page (`apps/web/src/pages/OrchestrationsPage.tsx`).

## Decisions

1. Delete reuses the existing `DELETE /api/runs/{id}` endpoint — no new delete endpoint.
2. Added a new `POST /api/runs/{id}/cancel` cancel-only endpoint because no cancel/stop endpoint existed.
3. Factored out shared cancel logic into `EndpointHelpers.CancelRunWorkAsync`; both DELETE and cancel call it.
4. Children handling matched DELETE's existing behavior; abandoning the coordinator workflow stops child subtask runs it drives.
5. Already-terminal runs: cancel returns `{ cancelled: false, already_terminal: true }` (HTTP 200) without acting.
6. Frontend added `cancelRun`, Stop/Delete FluentUI icon buttons, confirm flows, and existing MessageBar errors.

## Validation
- API build succeeded.
- OrchestrationsPage tests 5/5 pass.
- Full web suite had unrelated concurrent WIP failures in Trinity-owned files.

## Files changed
- `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs`
- `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs`
- `apps/web/src/api/client.ts`
- `apps/web/src/pages/OrchestrationsPage.tsx`
- `apps/web/src/__tests__/OrchestrationsPage.test.tsx`

# Decision: Route tool-approvals to the owning child subtask run (recurrence of #196)

**Author:** Neo (Backend/Full-stack)
**Date:** 2026-07-06
**Requested by:** sabbour (Ahmed)
**References:** issue #196, RunEndpoints.cs, EndpointHelpers.cs

## Context / Live bug
On a Coordinator orchestration, approving a tool call failed with `API error 404: {"error":"No approval request found for this request_id on this run..."}` because the client could post to the coordinator run while the pending approval was registered on the child subtask run.

## Root cause
`CoordinatorDispatchService.BubbleChildInteraction` re-emits the child's approval-required event onto the coordinator SSE stream, but `POST /api/runs/{id}/tool-approvals` and `/tool-denials` previously resolved only against the posted id. Posting to the parent coordinator returned Unknown and then 404.

## Fix
Added `EndpointHelpers.ResolveApprovalOwningRunIdAsync(gate, runStore, postedRunId, requestId)`:
1. If posted run owns the request, return it.
2. Else if posted run is a coordinator, inspect child runs and return the child that owns the request.
3. Else return null and preserve existing 404 behavior.

Wired into both `/tool-approvals` and `/tool-denials` so grant/deny/state checks operate on the owning child run.

## Validation
- API release build: 0 errors.
- New `ToolApprovalOwningRunResolutionTests.cs`: 3 tests pass; broader approval/bubbling filter: 44 passed.
- Web build passes; approval-card frontend tests pass.

## Files changed
- `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs`
- `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs`
- `tests/Agentweaver.Tests/ToolApprovalOwningRunResolutionTests.cs`

# Decision: Review now action opens the Assembly artifacts panel

**Author:** Trinity (Frontend Engineer)
**Date:** 2026-07-06
**Requested by:** Ahmed (@sabbour)

## Context
The Coordinator run page's "Review now" action in the Assembly and review section was a no-op. The previous implementation targeted dead `reviewRef` / `scrollToReview` plumbing rather than the visible review surface.

## Decision
Retarget `viewAssemblyExecution` to open the artifacts panel directly with `setArtifactsPanelOpen(true)` and remove the dead review ref/scroll code. This preserves the existing Assembly and review model while making the CTA land on the shipped review surface.

## Validation
Commit `388b993` shipped with the v0.9.2 wave; code-review returned a clean verdict and staging deployment is healthy on v0.9.2.

## 2026-07-08T00:57:28-07:00: Inbox merge — link v095 doc facets

# v0.9.5 docs facet split

- Date: 2026-07-08T05:24:00Z
- Decider: Link
- Context: v0.9.5 staging docs update requested coverage for coordinator run page rework, browser console, review-gate persistence, project generation model settings, and provider error surface.

## Decision

Documented the wave as:

1. **Coordinator run page + review-gate persistence** in existing coordinator docs (`docs/experience/coordinator-orchestration.md`, `docs/reference/coordinator.md`, `docs/deep-dive/coordinator-internals.md`) because those pages already own the three facets for coordinator lifecycle and assembly behavior.
2. **Browser console** as a new three-facet set (`docs/experience/browser-console.md`, `docs/reference/browser-console.md`, `docs/deep-dive/browser-console.md`) because the backend facade turns the console from a UI-only shell into a route/DTO/tooling feature.
3. **Project generation model settings** as a new three-facet set (`docs/experience/project-generation-model-settings.md`, `docs/reference/project-generation-model-settings.md`, `docs/deep-dive/project-generation-model-settings.md`) because it spans UI, persisted DTOs, migrations, and generation runtime consumers.
4. Folded the **provider error surface** into the console and generation-model references/deep dives rather than creating a standalone page, since the new classifier is currently consumed by those two operator-facing flows.

## Validation

- `cd docs; npm run build` passed.
- `node scripts/gen-docs.mjs --check` passed.
- Committed docs on `main` in `7feb8c2a` (`docs: cover v0.9.5 staging wave`).

## 2026-07-08T00:57:28-07:00: Inbox merge — smith screenshot review

# Smith screenshot review

Reviewed commit `9e6a243c` (`docs(screenshots): reconcile plan+spec to real app pages`).

Verdict: APPROVE.

Findings:
- CI guard is intact: `tests/e2e/screenshots.spec.ts` uses `BASE_URL` from env, file-level serial mode, optional `STORAGE_STATE`, `ensureSignedIn()`, and a `beforeEach` skip when `BASE_URL` is absent. An accidental Playwright run without `BASE_URL` skips this spec.
- Spot-checked routes against `apps/web/src/App.tsx`; reviewed project, orchestration, skills, workflows, observability, team, memories, board/settings, auth, and console routes are real.
- Spot-checked added selectors against app components: orchestrations list, skills catalog/import dialog, observability overview/agents/traces/trace preview, and workflow definition graph all map to real text/buttons/components. KEEP selectors sampled also exist.
- Plan/spec parity verified: 51 `shot('name')` entries, 51 plan rows, zero diff; row numbers are contiguous and per-page counts sum to 51.
- Removed names `cluster-page-quota-warning` and `per-run-workflow-graph` have no remaining references in spec, plan, or docs callouts. The old placeholder PNG files still exist but are unreferenced and not a blocking issue.
- Validation passed: `cd docs; npm run build` and `node scripts\gen-docs.mjs --check`.

No blocking issues found; ready to push.

## 2026-07-08T00:57:28-07:00: Inbox merge — trinity screenshot reconcile

# Trinity screenshot reconciliation

Decision: Reconciled the user-guide screenshot plan and Playwright capture spec against the real app routes and page components as of this task.

Rationale:
- Treat `apps/web/src/App.tsx` and concrete page/component selectors as source of truth, with docs only used to align guide ownership/names.
- Keep planned screenshots that map to real routes/states, rename stale workflow graph coverage to the actual Workflows page definition graph, remove stale/duplicate shots, and add missing real page/state coverage.
- Maintain draft/skipped-safe Playwright behavior using the existing BASE_URL guard and env variables while making plan/spec shot names 1:1.

Outcome:
- Final screenshot set: 51 rows.
- Added coverage: orchestrations-list, skills-catalog, skill-import-dialog, observability-overview, observability-agents, observability-traces, observability-trace-preview, workflow-definition-graph.
- Removed/renamed: duplicate project-board removed; cluster-page-quota-warning removed because ClusterPage does not currently expose quota bars; per-run-workflow-graph renamed to workflow-definition-graph because the real page exposes a workflow definition graph.
- Validation passed: docs build, gen-docs check, and plan/spec parity with 0 diff.

## 2026-07-08T00:57:28-07:00: Inbox merge — trinity screenshot seed data

# Screenshot plan seed-data guidance

Author: Trinity
Date: 2026-07-08T00:38:25-07:00

Updated `docs/experience/screenshot-plan.md` to require populated data before capturing the 51 web user-guide screenshots.

Decision:
- Do not invent a demo-data path: investigation found no committed demo-data generator or seed endpoint in `apps/Agentweaver.Api` or `scripts/`.
- Document the real manual setup flow: create a project, cast a team, populate backlog/workflows, import or create skills, create memory/decision entries, dispatch coordinator runs, wait for telemetry/live diagnostics, then capture with `PROJECT_ID`, `RUN_ID`, and `EXECUTION_ID`.
- Add a `Data needed` table column so every planned screenshot has an explicit prerequisite while preserving screenshot names and spec parity.

Validation:
- `cd docs; npm run build` passed.
- `node scripts\gen-docs.mjs --check` passed.
- Parity check remains 51 planned screenshot rows = 51 Playwright shot names.

## 2026-07-08T00:57:28-07:00: Cleanup audit verdict — keep OAuth token WIP, discard junk worktree dirt

**Author:** Scribe  
**Requested by:** Ahmed Sabbour (@sabbour)  
**References:** Smith PR #205; coordinator stash/worktree audit

Decision:
- Treat the current `k8s/*.yaml` working-tree churn as Windows LF→CRLF drift only; leave it untouched and do not include it in unrelated commits.
- Treat `stash@{0}` (`graph zoom-in button`) as stale/orphaned WIP because it adds `getRunUsage` mocks for an API absent from `main`; do not restore unless the unmerged run-usage feature returns.
- Treat `stash@{1}` (`OAuth pod shared store`) as potentially keepable substantive C# work because it introduces `AgentHostUserTokenSyncService` and `EnsureUserTokenInSpcAsync` for CSI SecretProviderClass per-user token sync; preserve for deliberate follow-up.
- Treat worktree `sabbour-ui-improvement-research` dirt as junk (`apps/web/index.html` live-preview script plus `.impeccable/` cache) because the worktree is 0 commits ahead of main.
- Treat worktree `sabbour-craft-overview` dirt as junk untracked tool caches (`.learnings/`, `.playwright-cli/`, `local-api-probe/`) because the worktree is 0 commits ahead of main.
- Smith's Playwright screenshot setup was isolated on `sabbour/playwright-screenshot-setup` and opened as PR https://github.com/sabbour/agentweaver/pull/205; repo was returned to `main` and no k8s/.squad/unrelated files were included.

Rationale: Keep only substantive unfinished OAuth SecretProviderClass work under explicit follow-up; avoid normalizing line-ending drift or tool-cache/live-preview junk into product history.

## 2026-07-08T13:57:00-07:00: Inbox merge — run page review UX, structured RAI verdicts, preview identity, and teamless-run block

### Teamless orchestration runs are blocked

**Decision:** Teamless coordinator execution is blocked. A project must have at least one dispatchable roster member before orchestration start or backlog pickup can execute.

**Rationale:** Blank API-created projects can lack `.squad/team.md`. The previous coordinator fallback silently selected `Core Implementer` and made the coordinator appear to do all work, which confused operators and hid the missing-team setup problem.

**Contract and implementation notes:**
- `POST /api/projects/{id}/orchestrations` rejects teamless projects before run creation with HTTP 409 and `{ "error": "no_team", "message": "This project has no team. Cast a team before starting an orchestration." }`.
- The roster source is `SquadReader.ReadTeam()` on the project working directory, filtered to active dispatchable members and excluding infrastructure roles such as Scribe, Ralph, Rai, and build-test.
- Backlog pickup refuses teamless projects before claim/reserve, so no fallback run is created and the task remains Ready for retry after casting a team.
- Defense-in-depth remains in `CoordinatorOrchestratorExecutor` to prevent unattended fallback execution.
- The frontend surfaces the block as a clear no-team error state with a `Cast a team` CTA to `/projects/{id}/team/cast`.

**Sources merged:** `coordinator-teamless-run-policy.md`, `tank-block-teamless.md`.

### RAI verdict events use a structured verdict enum

**Decision:** `rai.verdict` carries a structured `verdict` token as the source of truth. The payload is `{ verdict, runId, rationale }`, where `verdict` is one of `green | yellow | red | revise`.

**Rationale:** Review found a high-severity mismatch where the backend emitted emoji traffic lights while the frontend compared against word values, causing every verdict, including red, to render as success. Removing the redundant presentation field makes the contract single-source and lets the client derive message intent, label, and icon exhaustively from `verdict`.

**Contract and implementation notes:**
- `trafficLight` is removed from the event payload; any prior inbox note mentioning it is superseded by this decision.
- `rai.verdict` is visible on the parent run stream and the `{runId}-rai` sub-stream.
- The frontend treats unknown verdicts as neutral info / `Unknown`, never silently as success.
- Coordinator runs are operator-steerable while `in_progress` or `awaiting_review`; `awaiting_review` is the assembly human-review parking state, not terminal inactivity.

**Sources merged:** `coordinator-rai-verdict-structured.md`, `tank-review-steering-rai-verdict.md`.

### Build & Test preview uses the persisted run id

**Decision:** Build & Test agents register `start_preview` with the real persisted run id, not the synthetic `{runId}-build-test` sub-stream id.

**Rationale:** `start_preview` calls `/api/runs/{runId}/sandbox/preview`, the preview endpoint validates ownership via `IRunStore.GetAsync`, and sandbox claims are derived from the run id used for agent setup and command execution. The persisted run id must therefore be threaded through Build & Test setup/tool binding/sandbox claim paths, while `{runId}-build-test` remains only a UI/event sub-stream id.

**Additional runtime note:** `SandboxPreviewService` resolves both `agent-{runId}` and `run-{runId}` claim conventions, and sandbox claim creation is idempotent on 409.

**Sources merged:** `morpheus-preview-buildtest.md`.

### Orchestration reliability root-cause decisions retained

**Decision:** Keep the coordinator reliability fixes from the previous wave as active architecture history:
- Assembly review gates are keyed by canonical assembly stage, not raw workflow node id.
- Assembly review waits indefinitely through durable state and deferred polling rather than failing after a 60-minute wall-clock timeout.
- Pickup auto-approve defaults align with pickup autopilot so unattended backlog children can use allow-with-approval tools without stalling.
- Copilot streaming turns are bounded by an inactivity watchdog so a stalled SDK stream fails cleanly with a retryable provider error rather than hanging forever.

**Sources merged:** `Coordinator-root-caused-and-fixed-three-orchestration-reliabil.md`, `Coordinator-added-inactivity-watchdog-for-hung-copilot-streami.md`.

### Workflow generation editing and blueprint validation

**Decision:** Workflow generation supports editing from an existing workflow through `base_workflow_id` or from an unsaved draft through `base_yaml`; the generate endpoint returns an unsaved draft preview and saving remains the existing validated PUT path.

**Contract and implementation notes:**
- Built-in/library workflow edits are immutable-source edits. The generator must produce a project-owned customized copy, and validation rejects built-in edit output that keeps the reserved base id.
- Blueprint generation was hardened through prompt changes and structural validation in the existing blueprint validation path; generated blueprint failures return plain-language details with regenerate/edit options.
- Workflow graph reachability and bindability checks live in blueprint validation so generated, inline, and predefined blueprint flows share the same guard.
- No UI changes were required for this slice.

**Sources merged:** `cypher-wf-gen-editing.md`.

---

## 2026-07-09T03:30:00Z: Preview-provisioning design approved; deterministic preview readiness guard required

**Date:** 2026-07-09T03:30:00Z  
**Author:** Morpheus; reviewed by rubberduck-preview / rubberduck-preview-rev / rubberduck-preview-final  
**Status:** DESIGN APPROVED — IMPLEMENTATION ON HOLD pending Ahmed's approach decision

**Decision:** Preview-provisioning design is approved after design-review GO. The root cause is reframed: `BuildTestTurnExecutor` already instructs the agent to run the app, discover the actual bound port, and call `start_preview(port)`. The missing contract is that preview is currently model-mediated and best-effort rather than a first-class, enforced, evented artifact. A run can complete with only `coordinator.assembly_review_approved` and no `preview_url`.

**Approved Phase 1 smallest slice:** For preview-required projects, Build/Test approval must have a deterministic approval-time post-condition: a durable `sandbox.preview_ready` with non-empty `preview_url` must exist before approval is applied. The guard belongs between `RunBuildTestAsync` and `ApplyAuthoredGateDecisionAsync` in `CoordinatorAssemblyService` (around lines 689-710). If the guard fails, emit `sandbox.preview_failed` plus `workflow.step` preview failed, then return to Steering or park/block. Do **not** call `ApplyAuthoredGateDecisionAsync` with `RequestChanges=true`; that reset/redispatch route is legacy/manual-opt-in only and off the approved GO path.

**Contract additions:** Phase 1 adds durable preview applicability/result states: `preview_required`, `preview_skipped_not_applicable` with reason, `preview_ready`, and `preview_failed`. Executor-stage failure eventing must distinguish `port_not_found`, `app_exited`, `preview_not_requested`, and `preview_required_but_missing`. Reachable `preview_url` semantics require pod-per-run plus Gateway preview enabled.

**Open decisions:** Implementation remains on hold pending Ahmed's approach decision because this intersects the assembly-gate simplification / steering-only direction. Ahmed still needs to decide the preview applicability policy, deployment prerequisite semantics, and exact approval guard placement/policy before code starts.

**Source:** `.squad/decisions/inbox/morpheus-buildtest-preview-wiring.md` (kept in inbox as active design reference)

---

## 2026-07-09T03:30:00Z: Coordinator Build/Test must bind to a routable coordinator AgentHost pod for assembly preview

**Date:** 2026-07-09T03:30:00Z  
**Author:** Morpheus  
**Status:** DESIGN / TRACK A REFERENCE

**Decision:** Assembly Build/Test should explicitly acquire a dedicated AgentHost pod bound to the coordinator run id, configure it to the detached assembly worktree, and keep the pod/worktree alive until assembly is terminal or review/preview cleanup completes. Reusing a child pod is rejected because child pods are scoped to child run ids, tokens, worktrees, and lifecycle, and do not create the coordinator SandboxClaim required by preview routing.

**Rationale:** `start_preview` resolves preview routes through the run's sandbox claim. A coordinator-run claim makes the preview Gateway route to the pod that runs the integration build/test server, keeps files visible during Human Review, and avoids converting pod/A2A infrastructure failures into code feedback. The detached worktree must be under the shared `/workspace` PVC and passed to `/configure`; passing `WorktreePath` on an A2A turn is not sufficient.

**Source:** `.squad/decisions/inbox/morpheus-buildtest-pod-binding.md` (kept in inbox as design reference)

---

## 2026-07-09T03:30:00Z: Steering-only assembly revisions target same child run and same branch, not fresh redispatch

**Date:** 2026-07-09T03:30:00Z  
**Author:** Tank  
**Status:** DESIGN ONLY — DO NOT IMPLEMENT YET

**Decision:** Gate request-changes should use Steering as the only child revision mechanism. The revised target is same child run id and same branch revision, with explicit durable stream reopen and idempotent Steering directives. The design must not claim literal MAF/Copilot conversation preservation with current code; the honest MVP is same-child, same-branch revision, with checkpoint-resumed context only after a future proof point.

**Rationale:** Terminal child runs currently complete streams, discard later appends, stop subscribers, and delete checkpoints. Safe steering-only revision therefore requires durable stream lifecycle state, epoch-aware reopen/replay semantics, CAS/idempotency for gate decisions, bounded checkpoint/event retention, and durable observability for assembly blocked/failed states. Missing same-child steering support should block clearly rather than silently falling back to a fresh child run unless an explicit operator escape hatch requests context-losing retry.

**Source:** `.squad/decisions/inbox/tank-context-preserving-revision.md` (kept in inbox as active design reference)

---

## 2026-07-09T03:30:00Z: Assembly gate order follows workflow graph approval path

**Date:** 2026-07-09T03:30:00Z  
**Author:** Tank  
**Status:** MERGED

**Decision:** Collective assembly gate resolution orders workflow-derived gates by approval-path traversal from the workflow `start` node instead of YAML declaration order. The traversal follows unconditional and approval/happy-path verdict edges, canonicalizes platform stages, and dedupes duplicate canonical stages only after traversal ordering.

**Rationale:** Software-delivery graph semantics already route RAI before Build & Test. Declaration-order enumeration allowed misordered YAML to render or execute Build & Test before RAI. Built-in software workflow YAML and generation guidance were updated as defense in depth.

**Source:** `.squad/decisions/inbox/tank-assembly-gate-order.md`

---

## 2026-07-09T03:30:00Z: Run page and run tree preview/revision UX fixes merged

**Date:** 2026-07-09T03:30:00Z  
**Authors:** Trinity  
**Status:** MERGED

**Decision:** Coordinator run UI now distinguishes pending operator/review states, detects Build & Test as a `build_test` gate instead of human review, surfaces active preview links on the Build & Test node/selection, hydrates active previews from the sandbox port-forward API and `preview_url` stream payloads, and converts live gate/child statuses to failed for terminal failed runs. Run-tree ordering is deterministic by stage rank, outcome-spec JSON renders as Coordinator-authored outcome-plan fields, RAI placeholder rationales are treated as empty, assembly-gate system prompts are hidden, and assembly changes-requested events render as revision cycles.

**Validation:** `npm --prefix apps/web run build` passed; `npm --prefix apps/web test -- --run` passed.

**Sources:** `.squad/decisions/inbox/trinity-runtree-preview-ux.md`, `.squad/decisions/inbox/trinity-runpage-bugs.md`

---

## 2026-07-09T03:30:00Z: API image includes git CLI for assembly worktrees

**Date:** 2026-07-09T03:30:00Z  
**Author:** Link  
**Status:** MERGED

**Decision:** The API Docker image includes the `git` CLI.

**Rationale:** `WorktreeManager` shells out to `git worktree` during the Build/Test assembly gate. Libgit2 covers headless operations but not this worktree command path.

**Source:** `.squad/decisions/inbox/link-api-image-git.md`

## 2026-07-11T00:00:00Z: Release note — v0.9.19-rc1 → STAGING (image-efficient)

**Source:** `.squad/decisions/inbox/link-release-v0919rc1.md`

# Release note — v0.9.19-rc1 → STAGING (image-efficient)

- **Author:** Link (Platform Engineer)
- **Date:** 2026-07-11T00:00:00Z
- **Requested by:** Ahmed Sabbour
- **Environment:** AKS INT/Staging — ctx `agentweaver-aks-2`, ns `agentweaver`, ACR `agentweaverregistry`, RG `agentweaver-rg`
- **Previous deployed:** v0.9.18-rc1 (commit abde9585)

## HOLD status (per request)
- **NOT pushed to origin** — local `main` is ahead of `origin/main` by **17** commits (16 prior + this release commit). All held for Ahmed's validation.
- **No PRs merged. No issues closed. No `git push`. No tag pushed.** Annotated tag `v0.9.19-rc1` exists **locally only**.

## Release gate (both green — deploy proceeded)
- **Backend build:** `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release --no-restore` → **succeeded, 0 warnings, 0 errors**.
- **Backend tests:** `dotnet test ... --filter "Coordinator|Assembly|Steer|Worktree|IntegrationBranch" -c Release` → **537 passed / 0 failed / 0 skipped** (Total 537, ~3m06s).
- **Frontend build:** `npm --prefix apps/web run build` → **succeeded** (vite prod build).
- **Frontend FULL suite:** `npm --prefix apps/web test -- --run` → **71 files / 590 tests passed** (0 failed, ~145s). happy-dom `insertRule`/forced-colors stderr warnings are non-fatal (exit 0).

## Local commit + tag (NOT pushed)
- **Commit SHA:** `fdbe983250a881a67e0714d0b1e308d7ada7e246` (`fdbe9832`)
- **Message summary:** dependency-base propagation fix (branch-validity inclusion replacing `run.Diff` sentinel; mandatory integration-branch contains-check; final-assembly inclusion fix) + UI fixes (RAI node no longer echoes decomposition JSON; coordinator activity rows coalesced). Includes `Co-authored-by: Copilot`.
- **VERSION:** `0.9.18-rc1` → `0.9.19-rc1`
- **Tag:** annotated `v0.9.19-rc1` (local only)

## Images (IMAGE_TAG=v0.9.19-rc1)
Rebuilt (source changed) via `az acr build`, in parallel:
| Image | Action | Digest |
|-------|--------|--------|
| agentweaver-api | **rebuilt** (apps/Agentweaver.Api/** changed; worker uses same image) | `sha256:b2e99d3cd4c5915930ac072b89ad9cfb29d01d1f0ac1a96875f101b47c931c3c` |
| agentweaver-frontend | **rebuilt** (apps/web/** changed) | `sha256:27e8a9f96170e51b850fdda5a46fbd8524ee87e4125cf3daf549f5bdb09e33aa` |

Retagged server-side via `az acr import` from v0.9.18-rc1 (no rebuild — content byte-identical):
| Image | Action | Digest |
|-------|--------|--------|
| agentweaver-mcp | retagged | `sha256:f1c63117a1fdec5c9b4c513770feb341a2b3aeddcf24f2c2e3ccb888ee4308a4` |
| agentweaver-agent-host | retagged | `sha256:d5aa840bbdc948c243f217d64dffad2c46734332b06cf1f88e36432c2ff2d9b1` |

> agent-host safe to retag: verified `packages/Agentweaver.AgentRuntime`, `packages/Agentweaver.AgentTools`, and `apps/Agentweaver.AgentHost` were **NOT** changed (working tree + HEAD diff).

Note: the frontend `az acr build` initially crashed the local az **log stream** on the vite `✓` glyph (cp1252 `UnicodeEncodeError` — documented in 20-build-push-images.sh). Re-run through the UTF-8-safe interpreter (`python.exe -X utf8 -m azure.cli ... --output none`) succeeded (Run ID cc1c).

## Deploy (`scripts/aks/30-deploy.sh`)
Env: `IMAGE_TAG=v0.9.19-rc1 TENANT_ID=72f988bf-… IDENTITY_CLIENT_ID=bfd29d05-…` (KEYVAULT_NAME defaulted to agentweaver-kv). Line endings normalized (`sed -i 's/\r$//'`). All manifests applied; both gateways Programmed; all four rollouts reported success.

## Rollout verification
| Deployment | Image | Ready |
|------------|-------|-------|
| agentweaver-api | agentweaver-api:v0.9.19-rc1 | **2/2** |
| agentweaver-frontend | agentweaver-frontend:v0.9.19-rc1 | **2/2** |
| agentweaver-mcp | agentweaver-mcp:v0.9.19-rc1 | **1/1** |
| agentweaver-worker | agentweaver-api:v0.9.19-rc1 (worker uses api image) | **1/1** |
| agent-host SandboxTemplate | agentweaver-agent-host:v0.9.19-rc1 | template updated |
| agent-host warm pool | 2 pods recycled onto v0.9.19-rc1 (bqn8f, m8cwh) | **2/2** |

- **API serving:** `/api/health` → **200**, `/api/ping` → **200** (via ready pod); service endpoint bound.
- **Warm pool refreshed:** old pods (v0.9.18-rc1 tag, byte-identical) deleted; controller refilled 2 pods on v0.9.19-rc1.

## Caveat (pre-existing, not a code regression)
Both api replicas **OOMKilled during startup** (JIT/warmup peak vs 4Gi limit) and the readiness/liveness probes (1s timeout) failed transiently before the app warmed. Both recovered to 1/1 and the deployment settled at **2/2 ready**. This is a startup memory/probe-tightness characteristic of the api pod (unrelated to the coordinator/UI changes in this release). Suggest a follow-up to raise the api memory request/limit headroom and/or relax probe timeouts.

## Gateway / URLs
- Frontend: https://agentweaver.6a4e90c828ad2500015a1010.westus2.staging.aksapp.io/
- API: …/api/  · MCP: …/mcp/  · Gateway IP: 20.115.253.136

---

## 2026-07-11T00:00:00Z: Design: Fix-A(3a) — reliable terminal emission on in-place-revision (runtime/MAF)

**Source:** `.squad/decisions/inbox/morpheus-fixa-inplace-terminal-design.md`

# Design: Fix-A(3a) — reliable terminal emission on in-place-revision (runtime/MAF)

**Author:** Morpheus (Runtime / MAF)
**Date:** 2026-07-09T17:30:00-07:00
**Status:** DESIGN passed rubber-duck (GO-WITH-CHANGES). **Path-1 IMPLEMENTED (§8).** **Path-2 IMPLEMENTED + READY-FOR-CODE-REVIEW (§9)** — combined tree with Tank's Fix-B, build clean, 759 tests green.
**Requested by:** Ahmed (@sabbour)
**Priority:** fast-follow (lower than Tank's Fix-B; must NOT block Fix-B ship)
**Related:** Tank `tank-assembly-review-resilience-design.md` §3a (this is my half) + §3b (coordinator contract, Tank-owned); `.learnings/ERRORS.md` ERR-20260709-STEER1 (resolved v0.9.13-rc1, with the OPEN follow-up this design closes); **Reviewer Rejection Lockout Protocol** `.github/agents/squad.agent.md:788-809` (strict lockout — original author locked out on rejection, a DIFFERENT agent owns the revision); prior `morpheus-*` STEER1 work.
**Scope (Morpheus-owned):** `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs`, `.../IWorktreeOperations.cs`, `AgentTurnOutput` record, `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` (child graph), `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs`, `apps/Agentweaver.Api/Runs/RunOrchestrator.cs` (revision + child-dispatch context assembly), `apps/Agentweaver.Api/Git/WorktreeManager.cs` (+ `WorktreeOperationsAdapter`). **Do NOT touch** `CoordinatorAssemblyService.cs` (Tank/3b) or the coordinator contract — I only expose/guarantee the runtime handoff Tank drives.

---

## 0. Two context-complete paths (framing)

Per Ahmed's non-negotiable — **every** revision, whether same-agent or a lockout re-dispatch, MUST carry FULL context: the reviewer's change-requests, ALL accumulated prior-round feedback, and the prior subtask work/session/branch state ("the re-dispatch to fix should maintain the context and session details"). The Reviewer Rejection Lockout Protocol (`squad.agent.md:788-809`) means a **REJECTION** locks out the original author and hands the revision to a **DIFFERENT** agent — a conscious, visible dispatch. So Fix-A spans two paths, both context-complete:

- **Path 1 — in-place same-agent revision** (non-rejection STEER/guidance/request-changes): resume the same child on the same worktree/branch/session and **reliably emit the terminal `child-assemble-ready` event even when `CommitChanges` degrades** (the core 3a task; §1–§2 FIX 1 + FIX 2).
- **Path 2 — conscious dispatch to a DIFFERENT agent** (reviewer rejection / lockout; coordinator-owned by Tank): the new agent must **inherit the full context bundle** (feedback history + prior work/branch/session), NOT start blank. Runtime's job here is to make the handoff *carry* that bundle (§2.5); Tank owns *when/who* is dispatched and the bundle contract shape.

Both paths ultimately re-submit the revised work to the gate and consume the same steering/revision budget (Tank 3b). Path 1's reliability reduces how often we fall to Path 2; Path 2 guarantees that when we do, no context is lost.

---

## 1. Problem & root cause (verified in code)

**Goal (from Tank §3a + STEER1 follow-up):** raise the in-place-revision *clean-terminal* rate from ~1/3 toward the dominant path, so change-requests are applied **in-context** (same worktree/session) and re-submitted to the gate, converging within budget — instead of degrading to a conscious `dispatch_fresh` (context lost) 2/3 of the time.

### 1.1 What actually happens today (traced through the code)

`in_place_steer` resumes a subtask child via `RunOrchestrator.StartRevisionAsync` (`RunOrchestrator.cs:388`) — a **fresh** `RunStreamingAsync` with `isChild:true, IsRevision:true`, the **same** trimmed child graph and the **same** watch loop as a fresh dispatch. So the emission path is structurally identical to a fresh dispatch, with two revision-specific differences:

1. **Child graph is trimmed with NO failure→terminal edge** (`RunWorkflowFactory.cs:759-772`):
   `agentInputStorer → agentBinding → childAssembleReady`, `.WithOutputFrom(childAssembleReady)`. `childAssembleReady` (`:465-473`) is the **only** node that yields a terminal `WorkflowOutputEvent` (`AssembleReadyOutput`). There is no node/edge that can terminalize a post-turn *fault*.

2. **`AgentTurnExecutor` post-turn (post-STEER1)** (`AgentTurnExecutor.cs:60-190`): after `agent.turn.end`, the only throwing op is `CommitChangesWithRetryAsync` (`:195-220`). On a **persistent** throw it emits a `failed` step and **rethrows** → MAF `ExecutorFailedEvent`. The ONLY thing that then produces a terminal is the backstop in `RunWatchLoopService.WatchAsync` `ExecutorFailedEvent` case (`:250-311`): for a child run it calls `FailRunSafeAsync("child_executor_failed:{id}")` → child run **Failed** → subtask **Failed** → Tank's `failedTargets` → conscious `dispatch_fresh` (**context lost**).

### 1.2 The two coupled root causes of the 2/3 degrade

- **R1 — the bounded commit retry never clears the actual blocker (the rate killer).**
  `CommitChangesWithRetryAsync` retries 3× on a **time backoff only** (`:200-219`) — it re-runs the *same contended commit* without clearing what blocks it. The live failure signature (ERR-STEER1: a benign `tool.error 'kill needs PID'` immediately before `turn.end`, then the wedge) points at a **lingering worktree process** the turn spawned (e.g. a dev-server it started to self-test and failed to kill) and/or a **stale `.git/index.lock`** left behind. LibGit2 `WorktreeManager.CommitChanges` (`WorktreeManager.cs:226` — `new Repository()` + `Unstage` + `Stage` + `Commit`) then fails, and because the retry never removes the lock/reaps the process, **all three attempts fail the same way** → rethrow → `dispatch_fresh`. A fresh pod has no such lingering state, which is exactly why fresh dispatch terminates cleanly ~always and the *resumed revision* does not.

- **R2 — no graph-native failure→terminal path (the structural gap the STEER1 follow-up named).**
  Terminalization of a post-turn fault depends **entirely** on the watcher's `ExecutorFailedEvent` backstop. That backstop can only mark the child **Failed** (→ `dispatch_fresh`); there is **no** graph path that can terminalize a fault as **assemble_ready-on-the-same-worktree**, and any fault that is *not* surfaced as an `ExecutorFailedEvent` (or that races stream drain) still risks the fragile `watch_stream_completed_without_terminal_event` stream-end fallback.

### 1.3 Ruled out (so we fix the right layer)
- **No-op commit is NOT the failure.** `CommitChanges` returns `headTree.Sha` (a valid, non-empty SHA) when nothing staged (`WorktreeManager.cs:240-243`); it does **not** throw. So a legit no-op revision terminalizes `assemble_ready` (HasChanges=false) **cleanly** at the runtime layer. Whatever the coordinator then does with a no-change revision is **3b (Tank)**, not runtime.
- **`CopilotAIAgent.StreamTurnOnceAsync` / `ResumeSessionAsync` are not the seam.** `RunTurnAsync(isRevision:true)` resumes the deterministic SDK session (`CopilotAIAgent.cs:389-395`, `:366-383`) and the turn **ends cleanly** (`agent.turn.end` observed live). The fault is strictly **post-turn** (the commit), so the fix belongs in the post-turn/graph layer, not the streaming loop.

---

## 2. Design — two complementary fixes + one invariant

### FIX 1 (PRIMARY — the rate driver): context-preserving commit that clears the blocker

Root-cause R1: make the post-turn commit retry **clear the clearable blocker between attempts**, so a lock/lingering-process contention (the live signature) actually **succeeds on retry** and the revision commits its edits and terminalizes `assemble_ready` on the **SAME worktree** (context preserved — no fresh pod).

Concretely, in `AgentTurnExecutor.CommitChangesWithRetryAsync`, on a caught attempt (before the next try):
1. **Reap the run's lingering child process group** — the turn is over, so best-effort signal the run's own spawned process group (reuse the existing group-signal seam already used by preview `StopProcessTree`/`SendUnixProcessGroupSignal`; exposed to the executor via a narrow injected reaper or an `IWorktreeOperations` sibling seam). This removes the `'kill needs PID'` leak that holds worktree/index state.
2. **Remove a STALE `.git/index.lock`** — via a new narrow `IWorktreeOperations.TryClearStaleIndexLock(worktreePath)` (impl in `WorktreeManager`): delete `<gitdir>/index.lock` **only if** it exists AND is stale (mtime older than a short threshold, e.g. 2s) AND no live git process owns it. Scoped to the run's own worktree; single-writer per child run.
3. Retry the commit.

Classification stays **by bounded attempts** (the executor remains decoupled from LibGit2 types, per the existing comment at `:195-205`): we always *attempt* the clear+retry; a **genuinely persistent** failure (corrupt/missing repo) still surfaces after the final attempt → Fix 2's visible failure terminal (never a fake success).

> This is the actual conversion of the 2/3: lock-contention commit faults become clean `assemble_ready` on the same worktree instead of `dispatch_fresh`.

### FIX 2 (STRUCTURAL — the invariant): add the failure→terminal edge to the child graph

Root-cause R2 + Tank's explicit ask ("add the failure→terminal edge … or equivalently guarantee terminal emission on the revision post-turn path"). Make a terminalized fault **graph-native** — a real MAF `WorkflowOutputEvent` from a dedicated child terminal — instead of a bare rethrow that relies on the watcher stream-abort backstop.

1. **Discriminate the output.** Add a nullable `TerminalFailureReason` (+ short `TerminalFailureEvidence`) to `AgentTurnOutput` (default `null` = success). After Fix 1's clear+retry is exhausted, `AgentTurnExecutor` **RETURNS** an `AgentTurnOutput` with `TerminalFailureReason` set and the captured commit-exception summary — **instead of rethrowing**. (An in-turn agent/setup throw keeps today's rethrow → backstop; the handled, common post-turn commit fault becomes a typed return.)
2. **Route it in the child graph** (`RunWorkflowFactory.cs:759-772`), using the conditional-edge API that already exists (`GraphDescriptorBuilder.AddEdge<T>(src, tgt, Func<T?,bool>)`, `:51`), mirroring how the full pipeline routes verdicts:
   - `agentBinding → childAssembleReady` **WHEN** `o => o?.TerminalFailureReason is null` (happy path, existing behavior).
   - `agentBinding → childTurnFailed` (**new** terminal node) **WHEN** `o => o?.TerminalFailureReason is not null`. `childTurnFailed` yields a terminal `ChildTurnFailedOutput(RunId, Reason, Evidence)` → `WorkflowOutputEvent`.
3. **Map the terminal** in `RunWatchLoopService.HandleTerminalOutputAsync`: `ChildTurnFailedOutput` → child run **Failed(reason)** (VISIBLE, structured) → subtask Failed → Tank's `failedTargets` → conscious `dispatch_fresh` (**contract unchanged**). Keep the `ExecutorFailedEvent`→child-terminalize handler as **defense-in-depth** for unhandled throws (infra faults, in-turn throws), but the normal fault path is now typed & deterministic.

**Honors the prior gate (no fake success):** a persistent fault is a **VISIBLE FAILURE** terminal — never a fabricated no-change `assemble_ready` that would silently drop the revision's edits. Fix 2 only makes that failure graph-native, typed, testable, and glitch-free. Fix 1 is what reduces how often we reach it.

### FIX 3 / §2.5 (PATH 2 — lockout re-dispatch carries full context)

Reviewer **rejection** → the coordinator (Tank) consciously dispatches the revision to a **DIFFERENT** agent (lockout; `squad.agent.md:788-809`). Runtime's obligation: the new child run must **inherit the full context bundle**, not start blank. Grounding in code (verified):

- Same-agent revision today (`RunOrchestrator.StartRevisionAsync`, `:388-433`) already preserves context by (a) **reusing `run.WorktreePath` + `run.WorktreeBranch`** (prior commits/branch state — builds on top), (b) **reusing the same stream entry** (`_streamStore.Get` — full prior event history for replay), and (c) threading the accumulated feedback in via **`revisedTask`** → `BuildContextAsync(run with { Task = revisedTask })`.
- A DIFFERENT-agent dispatch, however, currently routes through fresh child launch (`StartChildRunAsync`, `:207`) which creates a **new** worktree and, for children, `BuildContextAsync` returns the **lean** child prompt keyed on the *new* agent's charter (`:534-576`) — it does **not** re-attach the rejected artifact's branch, prior session events, or the round-by-round review feedback. That is the "starts blank" gap.

**Runtime change (Path 2):** provide a context-complete child re-dispatch seam that Tank drives on lockout — a `StartChildRevisionHandoffAsync(newAgentRun, priorChild, feedbackBundle, ct)` (or an overload of `StartRevisionAsync` accepting a different `AgentName`/charter). It:
1. **Reuses the prior child's worktree + branch** (`priorChild.WorktreePath` / `WorktreeBranch`) so the new agent builds on the locked-out author's committed work — the session/branch state is preserved, not recreated. (The lockout is about *authorship*, not *discarding the work-in-progress*.)
2. **Carries the accumulated feedback bundle** — reviewer change-requests + ALL prior-round feedback — injected via the task/instruction path (`revisedTask` equivalent) so it reaches the new agent's turn, PLUS the prior stream entry retained for replay/history where the coordinator wants continuity.
3. Runs the **trimmed child pipeline** with the SAME terminal-emission guarantees as Path 1 (FIX 2 invariant applies identically — the re-dispatched turn also emits exactly one `child-assemble-ready` or `child-turn-failed`).

**Contract boundary (coordinate with Tank):** Tank owns *when* lockout triggers, *who* the new agent is (mechanical not-original-author enforcement, `squad.agent.md:788-809 #3`), and the **shape of the feedback bundle** (his §3b context-propagation root-cause audit). I own that the runtime handoff *accepts and applies* that bundle + the prior branch/session, and that the re-dispatched child terminalizes reliably. The bundle contract is a shared artifact — I will consume whatever structured feedback record Tank's audit standardizes (I do not invent a competing shape). **Open coordination item Q5 (below).**

> Net: Path 1 (same agent, reliable terminal) and Path 2 (different agent, full-context handoff) are symmetric — both reuse branch+session and both honor the FIX 2 single-terminal invariant. Neither ever starts the revision blank.

### The terminal-emission INVARIANT (the thing to get right; testable at the workflow layer)

For **every** child/revision run, after `agent.turn.end` the workflow yields **exactly one** terminal `WorkflowOutputEvent`:

| Case | Terminal | Context |
|---|---|---|
| Commit ok (incl. legit no-op → HEAD tree) | `AssembleReadyOutput` (assemble_ready) | preserved |
| Fault cleared by Fix 1 clear+retry | `AssembleReadyOutput` (assemble_ready, same worktree) | **preserved** |
| Persistent fault (clear+retry exhausted) | `ChildTurnFailedOutput` (visible failure + evidence) | dispatch_fresh (Tank) |
| Unhandled in-turn/infra throw | `ExecutorFailedEvent` → watcher child-terminalize (backstop) | dispatch_fresh (Tank) |

⇒ the child path can **never** reach `watch_stream_completed_without_terminal_event`.

### VISIBILITY
The captured commit-exception (type + message) rides in `ChildTurnFailedOutput.Evidence` **and** a visible run event, so the true persistent cause is finally observable (today only the downstream wedge is logged, not the LibGit2 throw).

---

## 3. Coordination with Tank (3b) — no contract change

- Authoritative success remains the **subtask STATUS** (Tank's `DriveOutstandingSteeringExecutionAsync`). A `ChildTurnFailedOutput` marks the subtask **Failed** → Tank's `failedTargets` → conscious **visible** `dispatch_fresh`; both in-place retries and the fallback consume the **same** steering budget; on exhaustion → Fix-B human-review escalation.
- **Reviewer-rejection lockout (Path 2) is Tank-owned:** Tank decides *when* a rejection triggers lockout, selects the DIFFERENT revision author (mechanical not-original-author check, `squad.agent.md:788-809`), and defines the **feedback-bundle shape** (his §3b context-propagation audit). I expose the runtime handoff seam (§2.5) that *accepts* that bundle + reuses the prior branch/session, and I guarantee the re-dispatched child terminalizes under the same FIX 2 invariant. I consume Tank's bundle contract — I do NOT define a competing one.
- I only (a) raise the **in-context `assemble_ready` rate** (Fix 1, Path 1) and (b) make the failure/handoff terminals **deterministic & typed** (Fix 2, both paths) and (c) ensure Path 2's dispatch **carries context** (§2.5). I do **not** modify `CoordinatorAssemblyService.cs` or the contract. Fix-B remains independently sufficient to remove the hang; 3a is a rate + context-fidelity layer on top.

---

## 4. Files & functions (before → after)

| File | Change |
|---|---|
| `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs` | Fix 1: `CommitChangesWithRetryAsync` reaps the run process group + `TryClearStaleIndexLock` between attempts. Fix 2: on exhausted persistent fault, **return** `AgentTurnOutput{ TerminalFailureReason, TerminalFailureEvidence }` instead of rethrow; capture the exception summary. |
| `packages/Agentweaver.AgentRuntime/Workflow/IWorktreeOperations.cs` (+ `WorktreeOperationsAdapter.cs`, `apps/.../Git/WorktreeManager.cs`) | Fix 1: add narrow `bool TryClearStaleIndexLock(string worktreePath)` (+ optional process-group reap seam). Executor stays decoupled from LibGit2 types. |
| `packages/Agentweaver.AgentRuntime/…/AgentTurnOutput` record | add `string? TerminalFailureReason = null`, `string? TerminalFailureEvidence = null` (nullable, default null — full-pipeline consumers unaffected). |
| `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` (child graph `759-772`) | Fix 2: new `childTurnFailed` terminal `ExecutorBinding` (`AgentTurnOutput → ChildTurnFailedOutput`); replace the single happy edge with two **conditional** edges keyed on `TerminalFailureReason`; `.WithOutputFrom` both terminals. |
| `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs` | Fix 2: `HandleTerminalOutputAsync` maps `ChildTurnFailedOutput` → Failed(reason) + visible event; keep `ExecutorFailedEvent` backstop. |
| *(new)* `ChildTurnFailedOutput` record (Domain/runtime outputs, next to `AssembleReadyOutput`) | `(RunId, Reason, Evidence)`. |
| `apps/Agentweaver.Api/Runs/RunOrchestrator.cs` | **Path 2 (§2.5):** add a context-complete child re-dispatch seam (`StartChildRevisionHandoffAsync` or a `StartRevisionAsync` overload accepting a different agent/charter) that reuses `priorChild.WorktreePath`/`WorktreeBranch` + retains the prior stream entry + injects the accumulated feedback bundle into the new agent's turn. No change to `StartChildRunAsync`'s fresh-launch path. |

No schema change. No feature flag. Full-pipeline golden-descriptor parity preserved (child graph is the only topology touched). Path-2 seam is additive (new method) — existing dispatch unaffected; Tank calls it on lockout.

---

## 5. Risks & mitigations

1. **Clearing `.git/index.lock` races a live git op** → only remove when **stale** (mtime threshold) AND no live owning process; scoped to the run's own worktree; single-writer per child run. If in doubt, don't remove (fall through to visible failure — no data loss).
2. **Reaping the process group kills something the next turn needs** → the turn is already over (post-`turn.end`); reap only the run's **own** spawned group via the existing `StopProcessTree` group-signal semantics. Fresh dispatch already starts a clean pod.
3. **`AgentTurnOutput` new field ripples to full-pipeline consumers** → nullable/default-null; full-pipeline predicates key on existing fields and are unchanged; add a golden-descriptor parity assertion.
4. **Double-terminalization (graph `ChildTurnFailedOutput` AND watcher `ExecutorFailedEvent`)** → the handled fault now **returns** (no throw ⇒ no `ExecutorFailedEvent`); the backstop fires only for unhandled throws; the watcher is idempotent on terminal (first terminal wins, `return`).
5. **Fix 1 masks a genuinely broken repo** → clear+retry is bounded; a persistent failure still surfaces as a **visible** `ChildTurnFailedOutput` with the captured exception — never a fake success. The prior gate's invariant is preserved.
6. **Over-preserving context on a truly poisoned worktree** → if the same fault recurs across in-place attempts, Tank's budget + conscious `dispatch_fresh` still bound it; Fix 1 does not remove that safety net, it just makes the common recoverable case recover.
7. **Path 2 handoff reuses a worktree the locked-out author left mid-edit** → the lockout is about *authorship*, not discarding work; reusing the branch/worktree is the intended context preservation. If Fix 1's post-turn clear didn't run (author's turn faulted hard), the new agent's own post-turn commit (with Fix 1's clear+reap) resolves any residual lock. Tank's not-original-author check is unaffected (runtime doesn't pick the agent).
8. **Bundle-shape drift between Tank and me** → I consume Tank's standardized feedback record; if it isn't finalized when I implement, I gate Path 2 behind an agreed interface stub and land Path 1 (the core 3a) first (see Q3/Q5). Path 1 has zero dependency on the bundle shape.

---

## 6. Test plan

**Unit — `tests/Agentweaver.Tests/Workflows/AgentTurnExecutorRevisionTerminalTests.cs` (extend):**
1. *Lock-contention commit fails-then-succeeds after blocker cleared* → `assemble_ready` on same worktree; assert the **clear/reap hook was invoked** between attempts; assert **no** HEAD-tree fake-fallback (extends the existing transient-retry test).
2. *Persistent commit fault (clear+retry exhausted)* → executor **RETURNS** `AgentTurnOutput{ TerminalFailureReason != null, TerminalFailureEvidence has the exception summary }` — **not** a throw, **not** a fake `assemble_ready`. (Replaces the current "rethrow" assertion; the visible terminal is now graph-native.)
3. *Legit no-op commit (HEAD tree, nothing staged)* → `assemble_ready`, `HasChanges=false` (unchanged).

**Workflow-level — new `tests/.../Workflows/ChildGraphTerminalEmissionTests.cs`:**
4. Build the trimmed child graph; drive an agent turn whose post-turn commit faults persistently → assert exactly **one** terminal `WorkflowOutputEvent` of type `ChildTurnFailedOutput` (**never** stream-end-without-terminal).
5. Success drive → exactly **one** `AssembleReadyOutput`. Asserts the invariant §2 directly.

**Watch-loop — `RunWatchLoopChildExecutorFailureTests` / `RunWatchLoopTerminalOutputTests` (extend):**
6. `ChildTurnFailedOutput` terminal → child run terminalized **Failed(reason)**, VISIBLE; assert **no** `watch_stream_completed_without_terminal_event`.
7. Unhandled in-turn throw still hits the `ExecutorFailedEvent` backstop → child terminalized Failed (defense-in-depth intact).

**Path 2 handoff — new `tests/.../Runs/RunOrchestratorChildRevisionHandoffTests.cs`:**
8. `StartChildRevisionHandoffAsync` with a DIFFERENT agent → the `AgentTurnInput` carries the **prior child's `WorktreePath` + `WorktreeBranch`** (not a fresh worktree) and the new agent's charter/name; assert no new worktree is created.
9. The accumulated feedback bundle (reviewer change-requests + prior-round feedback) is present in the composed task/instruction reaching the new agent's turn (assert the bundle text threads through, not dropped).
10. The re-dispatched child honors the FIX 2 invariant → exactly one terminal (`AssembleReadyOutput` on success / `ChildTurnFailedOutput` on persistent fault) — same as Path 1.

**Regression (must stay green):** existing STEER1 suite, fresh-dispatch `assemble_ready`, golden-descriptor parity for the full pipeline, InReview/approve/decline suites.

**Live proof:** re-run the non-trivial app (`ed53860d`/`02e337e5` class). Expect: repeated rubberduck request-changes → in-place revisions **converge in-context** (`assemble_ready` on the same worktree) as the **dominant** share; `dispatch_fresh` only on genuine persistent faults; the child **never** wedges on `watch_stream_completed_without_terminal_event`; the in-context terminal rate is materially above the prior ~1/3.

**Build/test gate (at implementation time):** `dotnet build Agentweaver.sln -c Release`; `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --filter "Assembly|Coordinator|Steer|Revision|Preview|WatchLoop|Terminal|Workflow" -c Release` (delete `%TEMP%\memory.db*` first). Green counts reported; no stubs.

---

## 7. Open questions for the gate
1. **Fix-1 scope of the process reap** — reuse the preview `SendUnixProcessGroupSignal` seam directly, or add a dedicated `IProcessGroupReaper` injected into `AgentTurnExecutor`? (Proposed: a narrow injected reaper so the runtime executor stays testable and decoupled from the AgentHost preview code.)
2. **`index.lock` staleness threshold** (proposed 2s) — constant or config? (Proposed: constant; it's a race-window guard, not a tuning knob.)
3. **Fix 1 vs Fix 2 sequencing** — land both together (they share the tests), or Fix 1 first (immediate rate win) then Fix 2 (structural invariant) as a stacked change? (Proposed: together — Fix 2's typed terminal is what the Fix-1 persistent-fault test asserts against.)
4. **Is the process-leak (`'kill needs PID'`) worth an independent fix** at the tool layer (why did the agent's kill tool have no PID)? (Proposed: out of scope here — flag to Ahmed as a separate tool-reliability item; Fix 1 makes preview/steer robust to it regardless.)
5. **Path 2 feedback-bundle contract (with Tank):** what is the canonical shape of the accumulated feedback record the lockout re-dispatch carries (reviewer change-requests + all prior rounds)? I will *consume* Tank's §3b standardized record rather than define my own. Needs a shared interface before Path 2 implementation; Path 1 can land first (no dependency). Also: does Tank want the prior child's **stream entry** replayed to the new agent (full event history) or only the distilled feedback text? (Proposed: distilled feedback text into the turn + branch/worktree reuse for the actual work state; full replay only if Tank's contract asks for it.)

---

**READY-FOR-RUBBERDUCK.**

---

## 8. Implementation status — Path-1 LANDED (gate GO-WITH-CHANGES applied), Path-2 pending Tank DTO

Rubber-duck greenlit implementation with 5 blocking changes; Path-1 (Fix 1 + Fix 2) is implemented per the mandated sequencing ("land Fix 1 + Fix 2 together for Path-1 first, tests green"). Path-2 (different-agent handoff) is deferred until the shared feedback DTO (#5) and new-session semantics (#1) are settled with Tank.

### How each blocking change was addressed
- **#1 (new SDK session on lockout handoff) — DEFERRED to Path-2.** Not on the Path-1 path. Recorded as a hard requirement for `StartChildRevisionHandoffAsync`: reuse prior branch/worktree state but mint a NEW SDK session/run identity (never resume `agentweaver-run-{priorRunId}`), prior stream preserved as coordinator-visible history / distilled prompt context only. Same-agent in-place `StartRevisionAsync` keeps resuming as-is.
- **#2 (conservative stale-lock clear) — DONE.** `WorktreeManager.ClearStaleIndexLock` resolves the ACTUAL gitdir (handles linked-worktree `.git` pointer files; the per-worktree `index.lock` lives under the resolved gitdir), uses the existing `Coordinator:StaleLockThresholdSeconds` (default 15s, NOT a 2s hammer), refuses when a live `git` process is detected, and is best-effort (never throws; on uncertainty it does NOT delete → the persistent fault surfaces as `child-turn-failed`). Mirrors the age-check pattern (`TryDeleteStaleLock`), not the direct-delete anti-pattern.
- **#3 (ownership-proven reap) — CORRECTLY SKIPPED.** `RunWorkflowRegistry.Abandon` only cancels the CTS; there is no run-owned PID/process-group tracking, so NO `IProcessGroupReaper` was added and NOTHING is killed by path/name. We rely on stale-lock handling + a visible failure, exactly as instructed. (Documented in `CommitChangesWithRetryAsync` xmldoc.)
- **#4 (don't overstate the invariant) — DONE.** Narrowed: *handled* post-turn commit faults in the child pipeline yield exactly one terminal `WorkflowOutputEvent` via the graph-native `agent -> child-turn-failed` edge; *unhandled* throws (in-turn agent throw, infra) still terminalize via the watcher `ExecutorFailedEvent` backstop (`RunWatchLoopService` child-terminalize). New watch-loop test asserts the `ChildTurnFailedOutput` path completes the stream with a visible `run.failed` (reason `commit_failed_persistent`) — never `watch_stream_completed_without_terminal_event`. The existing `RunWatchLoopChildExecutorFailureTests` (in-turn throw) is unchanged, proving the backstop still covers unhandled throws.
- **#5 (shared typed bundle) — BLOCKED ON TANK, signature below.** Path-2 does not read `SteeringDirective` rows or invent a shape; it will CONSUME Tank's named DTO/rendered-guidance string.

### Non-blocking items
- **Structured evidence — DONE.** `ChildTurnFailedOutput.Evidence` carries `exception={Type}: {message}` plus per-attempt `lock_present / cleared / age_s / live_git_proc / detail`. `TerminalFailureEvidence` threads from the executor.
- **`StartChildRevisionHandoffAsync` preflight (WorktreeExists / dirty diff / lock presence / last tree; visible fallback) — DEFERRED to Path-2** (it's part of that seam).

### Files changed (Path-1)
- `packages/Agentweaver.AgentRuntime/Workflow/WorkflowMessages.cs` — `AgentTurnOutput` gains `TerminalFailureReason` + `TerminalFailureEvidence` (nullable, default null); new `ChildTurnFailedOutput(RunId, Reason, Evidence)` terminal record.
- `packages/Agentweaver.AgentRuntime/Workflow/IWorktreeOperations.cs` — new `TryClearStaleIndexLock(worktreePath)` seam (no-op default) + `IndexLockClearResult` diagnostics record.
- `packages/Agentweaver.AgentRuntime/Workflow/AgentTurnExecutor.cs` — ctor flag `emitTerminalFailureOutput`; `CommitChangesWithRetryAsync` clears the stale lock between attempts + collects diagnostics (FIX 1); on persistent fault, child pipeline RETURNS a typed terminal-failure output (FIX 2), full pipeline still rethrows.
- `apps/Agentweaver.Api/Git/WorktreeManager.cs` — `ClearStaleIndexLock` + `ResolveGitDir` (linked-worktree aware) + `IsAnyGitProcessRunning`.
- `apps/Agentweaver.Api/Runs/WorktreeOperationsAdapter.cs` — forwards `TryClearStaleIndexLock` to `WorktreeManager`.
- `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` — child executor built with `emitTerminalFailureOutput: isChild`; new `child-turn-failed` terminal binding; child graph now routes `agent` via two conditional `AddEdge<AgentTurnOutput>` edges (success→assemble-ready, TerminalFailureReason→child-turn-failed).
- `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs` — `HandleTerminalOutputAsync` maps `ChildTurnFailedOutput` → visible Failed(reason) + `run.failed` event, worktree preserved.
- Tests: `AgentTurnExecutorRevisionTerminalTests` (clear-hook assertion + child-mode typed-failure test), `RunWatchLoopTerminalOutputTests` (new `ChildTurnFailed` terminal test), `CoordinatorWorkflowGraphDescriptorTests` + `RunWorkflowDefinitionBindingTests` (child graph now has the 2nd terminal + fan-out edges).

### Validation
- `dotnet build Agentweaver.sln -c Release`: **clean (0 warnings, 0 errors)** for the Path-1 change set. NOTE: the shared working tree currently also carries Tank's in-flight Fix-B (`CoordinatorAssemblyService.cs`), which does not yet compile (`CS0103 IsReviewerRejection / ExecuteLockoutRotationAsync`). To validate WITHOUT touching Tank's file, Path-1 was built+tested in an isolated `git worktree` at HEAD with ONLY my 11 files applied.
- `dotnet test --filter "Assembly|Coordinator|Steer|Revision|Preview|WatchLoop|Terminal|Workflow" -c Release`: **745 passed / 0 failed / 12 skipped** (Postgres integration skipped locally).

### §8 — Exact contract I need from Tank for Path-2 (#5)
Please expose a NAMED public DTO (replacing the private anonymous `IReadOnlyList<object>` at `CoordinatorAssemblyService.cs:1996-2013`) OR a single rendered guidance string. Proposed shape I will consume verbatim:

```csharp
// Owned by Tank (Coordinator). I consume it read-only as the handoff input.
public sealed record AccumulatedReviewFeedback(
    string SubtaskId,
    string CurrentChangeRequest,          // the latest reviewer rejection/change-request
    IReadOnlyList<ReviewFeedbackRound> PriorRounds,  // all accumulated prior-round feedback, oldest->newest
    string PriorWorktreeBranch,           // the locked-out author's branch (work to build on)
    string? RenderedGuidance = null);     // optional pre-rendered prompt block; if set I inject it verbatim

public sealed record ReviewFeedbackRound(int Round, string Reviewer, string Feedback, DateTimeOffset At);
```

Runtime seam I will add on my side once the DTO name is fixed:
```csharp
// RunOrchestrator (Morpheus). Reuses prior branch/worktree; mints a NEW SDK session for newAgentRun (lockout #1).
public Task StartChildRevisionHandoffAsync(
    Run newAgentRun,                 // the DIFFERENT (non-locked-out) agent Tank selected
    Run priorChild,                  // prior author's child run (branch/worktree/session source)
    AccumulatedReviewFeedback feedback,
    CancellationToken ct);
```

Please confirm the DTO type name + namespace (or that you'll hand me a single `RenderedGuidance` string instead). I will not wire Path-2 until this is fixed. — Morpheus

## 9. Path-2 IMPLEMENTED + READY-FOR-CODE-REVIEW (combined tree, Tank's Fix-B landed)

Tank confirmed the DTO at **`packages/Agentweaver.Domain/AccumulatedReviewFeedback.cs`**, namespace **`Agentweaver.Domain`** (NOT Api — the reference graph is Api→AgentRuntime→Domain, so the seam compiles). Producer: `internal CoordinatorAssemblyService.BuildAccumulatedReviewFeedbackAsync(...)`. I consume the DTO read-only; I do NOT read `SteeringDirective` rows.

### What Path-2 does (`RunOrchestrator.StartChildRevisionHandoffAsync(Run newAgentRun, Run priorChild, AccumulatedReviewFeedback feedback, CancellationToken ct)`)
- **BLOCKING #1 (lockout correctness) — SATISFIED two ways:** the new agent runs under **`newAgentRun.Id`** (→ a distinct deterministic session id `agentweaver-run-{newAgentRun.Id}`) AND the `AgentTurnInput` is built with **`IsRevision:false`** → `CreateSessionAsync` (NOT `ResumeSessionAsync`). The new agent therefore never inherits the locked-out author's Copilot conversation/instructions/charter state. The same-agent in-place `StartRevisionAsync` still resumes as before.
- **Prior work preserved:** if the prior child's worktree exists and is not held by a live git process / clearable stale lock, it is **REUSED** (`worktree_strategy=reused_prior`, commits stack on the prior branch). Otherwise a **VISIBLE fallback** branches a fresh worktree from `feedback.PriorWorktreeBranch` (`worktree_strategy=fresh_from_prior_branch`) so committed prior work is still inherited — never starts on a broken/locked worktree.
- **Full context injected:** `feedback.RenderedGuidance` (all prior rejection rounds; falls back to `feedback.RenderForRevisionPrompt()`) is appended to the new agent's task. Child runs use `input.Task` (no separate agent-node prompt), so the guidance reaches the agent even with `IsRevision:false`.
- **Preflight + visibility:** best-effort `ClearStaleIndexLock` on the prior worktree; a `coordinator.child_revision_handoff` stream event records prior-child id, subtask, branch, chosen worktree strategy, and lock diagnostics. A NEW stream is opened on the new run id (the locked-out author's stream is never reused).
- **Terminal invariant unchanged:** the handoff launches the SAME trimmed child pipeline via `StartWorkflowOrFailAsync(isChild:true)`, so Path-1's graph-native failure→terminal edge still governs its terminal emission.

### Files changed (Path-2)
- `apps/Agentweaver.Api/Runs/RunOrchestrator.cs` — new public `StartChildRevisionHandoffAsync` (consumes `Agentweaver.Domain.AccumulatedReviewFeedback`).
- `tests/Agentweaver.Tests/Coordinator/RunOrchestratorChildRevisionHandoffTests.cs` — new: reuse-prior-worktree + guidance-injection + new-run-identity test; missing-worktree → fresh-branch-from-prior-branch fallback test.
- Did NOT touch `CoordinatorAssemblyService.cs` (Tank's producer consumed via the public method / DI).

### Validation (COMBINED tree — Tank's Fix-B present and green)
- `dotnet build Agentweaver.sln -c Release`: **clean (0 warnings, 0 errors)**.
- `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --filter "Assembly|Coordinator|Steer|Revision|Preview|WatchLoop|Terminal|Workflow|Lockout" -c Release`: **759 passed / 0 failed / 12 skipped** (Postgres integration skipped locally).

## 10. Code-review follow-ups

### FIX-1 (Medium) — stale-lock clear must not be blocked by a host-global git process — DONE
Root cause: `WorktreeManager.ClearStaleIndexLock` gated deletion behind a host-global `IsAnyGitProcessRunning()` (`Process.GetProcessesByName("git")`). On a busy coordinator (our own `git worktree add/prune` subprocesses + agents invoking git as a tool) a `git` process is almost always present → the clear returned `live_git_process_detected` and refused → the stale-lock clear never fired in exactly the concurrent scenario Fix-A #1 targets → commit-retry exhausted → the wedge returned.

**Decision — option (b): drop the global process check; the AGE gate is the sole guard.** Cross-platform command-line scoping (option a) needs WMI/`Get-CimInstance Win32_Process` on Windows and `/proc` on Linux — no clean built-in .NET `Process.CommandLine`, so it's fragile. Our commits go through IN-PROCESS LibGit2Sharp (no git subprocess), so a lock older than the configurable `Coordinator:StaleLockThresholdSeconds` (default 15s) with no in-process git op is safe to clear. Removed `IsAnyGitProcessRunning`; `LiveGitProcessDetected` evidence field retained (always `false`) for contract stability. Documented in the method xmldoc.
- Files: `apps/Agentweaver.Api/Git/WorktreeManager.cs` (removed the global check + method, updated xmldoc/inline rationale).
- Tests: `StallCascadeAndLockRetryTests.ClearStaleIndexLock_ClearsStaleLock_WhenOnlyAgeGateApplies_NoFalseLiveProcessRefusal` (proves the clear FIRES on the age gate alone — would fail under the old guard on any host with a git process) + `..._RefusesFreshLock_WithinStaleThreshold` (age gate still refuses a fresh lock).
- Combined validation: build clean; `--filter "Assembly|Coordinator|Steer|Revision|Preview|WatchLoop|Terminal|Workflow|Lockout"` → **761 passed / 0 failed / 12 skipped**.

### FIX-2 — wire the handoff from Tank's lockout rotation — READY FOR TANK
`StartChildRevisionHandoffAsync` is implemented, tested, and DI-reachable; it is not yet invoked in production. Injection point Tank needs from the assembly service (already have `RunOrchestrator` via DI):
```csharp
// RunOrchestrator (public; call from ExecuteLockoutRotationAsync instead of a plain fresh dispatch)
public Task StartChildRevisionHandoffAsync(
    Run newAgentRun,                 // the DIFFERENT (non-locked-out) agent's child run — allocated by the coordinator
    Run priorChild,                  // the locked-out author's prior child run (worktree/branch source)
    Agentweaver.Domain.AccumulatedReviewFeedback feedback, // Tank's producer output (BuildAccumulatedReviewFeedbackAsync)
    CancellationToken ct);
```
Preconditions for Tank:
- **Who allocates `newAgentRun`:** the coordinator, exactly as it allocates a fresh child today (new `RunId` ⇒ new deterministic SDK session `agentweaver-run-{newAgentRun.Id}` ⇒ lockout-correct). Do NOT pre-insert the run row — the method calls `InsertAsync` itself (mirrors `StartChildRunAsync`). Set `ParentRunId`, `SubtaskId`, `AgentName`, `RepositoryPath`, `OriginatingBranch`, `Task` (base subtask text); the method appends `feedback.RenderedGuidance`.
- **Worktree safety is handled here:** reuses `priorChild`'s worktree when present + age-gate-clearable; otherwise VISIBLY falls back to a fresh worktree branched from `feedback.PriorWorktreeBranch` (records `coordinator.child_revision_handoff` with `worktree_strategy`). A different agent never inherits a poisoned/dirty/locked tree.
- **`feedback.PriorWorktreeBranch`** must be `priorChild.WorktreeBranch ?? IntegrationBranchName(coordinatorRunId)` (Tank's producer already does this).
- The launched run uses the SAME trimmed child pipeline (`isChild:true`), so Path-1's failure→terminal edge governs its terminal emission.

---

## 2026-07-11T00:00:00Z: Decision note — Deterministic preview: pod-local TCP forwarder for guaranteed pod-IP reachability

**Source:** `.squad/decisions/inbox/morpheus-preview-forwarder.md`

# Decision note — Deterministic preview: pod-local TCP forwarder for guaranteed pod-IP reachability

**Author:** Morpheus (Runtime)  •  **Requested by:** Ahmed (@sabbour)  •  **Spec:** spec-006 preview-forwarder
**Status:** implemented, build + preview/sandbox tests green (362 passed / 0 failed)

## Problem (live run d6f9b040)
The deterministic `PreviewStep` injected `HOST=0.0.0.0 PORT=3000` (a HARDCODED default port). The AgentHost
`PreviewRunner` discovered the bound port and health-checked `http://127.0.0.1:{port}` (LOOPBACK), but the
Gateway registration probe (`SandboxPreviewService.IsPreviewTargetReachableAsync`) TCP-connects to
`pod.Status.PodIP:{port}` (ROUTABLE). A loopback-only app therefore PASSED observe but FAILED registration:
`registration_failed` → "Nothing is listening on sandbox pod {pod} port 3000". No reachable URL was ever produced.

## Decision
Guarantee pod-IP reachability at the platform layer with a **pod-local TCP forwarder**, and stop pinning the
app's port entirely. Reachability no longer depends on how the app binds (loopback OR all-interfaces) or which
port it chose.

## Implementation
- **A. Forwarder (`apps/Agentweaver.AgentHost/TcpPortForwarder.cs`, new):** `TcpListener` on `0.0.0.0:0` — the OS
  assigns a FREE public port, always distinct from the app port (the app already holds `127.0.0.1:appPort`, so the
  OS won't hand it back). Accept loop bidirectionally pumps each connection to `127.0.0.1:appPort`. Defensive
  concurrency cap (256), full cancellation, no socket/thread leaks, non-blocking shutdown via `IAsyncDisposable`.
- **Lifecycle (`PreviewRunner.cs`):** one forwarder per session, started idempotently inside
  `ObserveBoundPortAsync` once a healthy app port is found (`PreviewProcessState.EnsureForwarder`). Torn down in
  `StopPreviewProcessAsync` (→ reaper idle/max-lifetime/exited paths, `StopAsync` shutdown) and best-effort in
  `PreviewProcessState.Dispose`.
- **B. Register the public port:** `PreviewPortObservation` gained `AppPort` + `Reason`; `ObserveBoundPortAsync`
  now returns `Port = publicPort` (what the Gateway registers) with the app's loopback port in `AppPort`/evidence.
  Threaded through the AgentHost `observe-bound-port` endpoint (`app_port`, `reason` fields) →
  `PreviewRunnerHttpClient.ObserveResponse` / `PreviewRunnerPortResult` → `PreviewStep`, which already registers
  `port.Port`. Single-terminal-emission contract preserved.
- **C. No hardcoded 3000 (`PreviewCommandResolver.cs`):** removed `DefaultPort=3000` and all `PORT=`/`--port`/`-p`
  injection and the `port` parameter. The app keeps its framework default; ASP.NET uses `:0` (OS-assigned, Kestrel
  logs the real port). All-interface HINTS (`--host 0.0.0.0`, `-H 0.0.0.0`, `HOST=0.0.0.0`, `ASPNETCORE_URLS`) kept
  (harmless; forwarder is the real guarantee). A busy 3000 can never break preview.
- **D. Observe/register consistency:** `ObserveBoundPortAsync` health-checks THROUGH the forwarder public port
  before returning success. A public-port health miss returns `Healthy=false` with the distinct reason
  `bound_unreachable` (never silent/empty); `PreviewStep` emits `preview_failed(bound_unreachable)`.
- **E. Removed the agent/user port burden:** `AgentBasePrompt.cs`, `agentweaver.agent.md`, and
  `CharterCompiler.cs` now tell the model NOT to pick/hardcode a host/port — honor the framework default /
  `process.env.PORT`; the platform discovers the port and guarantees reachability.

## Which port is registered / how threaded
The forwarder's **public port** (pod-IP reachable) is registered with the Gateway. Thread:
`PreviewRunner.ObserveBoundPortAsync` → AgentHost `observe-bound-port` (`port`=public, `app_port`, `reason`) →
`PreviewRunnerHttpClient` → `PreviewStep` gate + `SandboxEndpoints.TryRegisterPreviewAsync(port.Port, …)`.
`appPort` stays in evidence/logs only.

## Tests
- `TcpPortForwarderTests` (new): loopback-only echo app reachable through the public port (public ≠ app port);
  truly-unreachable app → connection closed (no fake success, no hang).
- `PreviewRunnerHttpClientTests`: observe parses `app_port` + `reason` (`bound_unreachable`).
- `PreviewStepTests`: `bound_unreachable` maps to a distinct single terminal `preview_failed`.
- `PreviewCommandResolverTests`: assert NO hardcoded port (`PORT=3000`/`--port 3000`/`-p 3000`/`:3000`) while host
  hints remain.
- `dotnet build Agentweaver.sln -c Release` clean; `dotnet test … --filter "Preview|Sandbox|StartPreview|PreviewRunner|PreviewStep|PreviewCommand"` → 362 passed, 0 failed.

## Post-gate revision (NO-GO → fixed)
- **BLOCKER #1 — public port MUST be in-range [3000,9000].** The forwarder previously bound
  `TcpListener(IPAddress.Any, 0)` → an OS-ephemeral port (~32768+), which the Gateway rejects
  (`SandboxPreviewOptions.AllowedPortMin/Max`) and the sandbox NetworkPolicy black-holes
  (`k8s/networkpolicy-sandbox.yaml` ingress `port 3000 endPort 9000`) → registration would still fail
  live. Fixed: `TcpPortForwarder` now SCANS `[rangeMin,rangeMax]` (random start offset for concurrency
  spread, skips the app port, retries on `AddressInUse`) and binds a free in-range port; exhaustion
  throws `NoPublicPortAvailableException` → distinct `preview_failed(no_public_port_available)`. New
  config `PreviewRunnerOptions.PublicPortRangeMin/Max` (default 3000/9000) with a comment that it
  MIRRORS `SandboxPreviewOptions.AllowedPortMin/Max` + `networkpolicy-sandbox.yaml` (keep the three in
  lockstep). Up to 3 concurrent previews/run each get a distinct in-range port via the scan.
- **SHOULD-FIX #2 — accept loop no longer dies on transient SocketException.** `AcceptLoopAsync` now
  breaks ONLY on cancellation / `ObjectDisposedException`; other `SocketException`s (e.g. ECONNABORTED
  on a client RST between SYN and accept) are logged at debug and the loop `continue`s.
- **SHOULD-FIX #3 — DisposeAsync drains in-flight pumps.** Pump tasks are tracked in a
  `ConcurrentDictionary`; `DisposeAsync` awaits `Task.WhenAll(outstanding).WaitAsync(5s)` BEFORE
  disposing `_connLimit`/`_cts`; the pump `finally` also swallows `ObjectDisposedException` on
  `_connLimit.Release()` as a belt-and-suspenders.
- **SHOULD-FIX #4 — no process/forwarder leak on failed PreviewStep paths.** After a successful
  `StartProcessAsync`, EVERY post-start terminal FAILURE (observe unauthorized/error, unhealthy /
  bound_unreachable / no_public_port_available, approval denied/timeout, registration failed/
  port_not_allowed) now best-effort calls `_httpClient.StopProcessAsync` (which disposes the
  forwarder). Only a SUCCESSFUL registration keeps them alive. Single-terminal-emission contract
  unchanged. Reachability (`!Healthy`) is now checked BEFORE the port-range check so the distinct
  reason wins.
- **SHOULD-FIX #5 — half-close-aware pump (no truncated responses).** Each direction copies then
  `Socket.Shutdown(Send)` on its destination so the peer sees a clean EOF and can flush trailing bytes
  (full HTML body); after `WhenAny`, the opposite direction gets a bounded 5s drain grace before
  teardown.

Tests added/updated: `TcpPortForwarderTests` — PublicPort ∈ [3000,9000] and ≠ appPort; range-exhaustion
throws `NoPublicPortAvailableException`; loopback-only app reachable via public port; dead app → closed.
`PreviewStepTests` — `no_public_port_available` distinct reason + process stopped; post-start failure
stops process while success does not. Build clean; filter `Preview|Sandbox|StartPreview|PreviewRunner|
PreviewStep|PreviewCommand|TcpPortForwarder` → 365 passed / 0 failed.

## Residual risk (post-revision)
- Range scan is O(range) worst case (6001 ports) only when the range is heavily saturated; typical bind
  is O(1). Concurrency is bounded to 3 previews/run so contention is minimal.
- Forwarder connects to `127.0.0.1:appPort` (loopback) which observe already proved healthy; an app
  binding a non-loopback-only interface is covered by the `--host 0.0.0.0` hints.
- L4 pass-through: no Host-header rewrite; HTTP/1.1 + WS upgrade pass unchanged.

---

## 2026-07-11T00:00:00Z: Design: Resilient assembly-review loop — follow-through on change requests + escalate-to-human instead of terminal `assembly_blocked`

**Source:** `.squad/decisions/inbox/tank-assembly-review-resilience-design.md`

# Design: Resilient assembly-review loop — follow-through on change requests + escalate-to-human instead of terminal `assembly_blocked`

**Author:** Tank (Backend / Coordinator)
**Date:** 2026-07-09T17:16:00-07:00
**Status:** §1–§11 IMPLEMENTED (escalation state-machine + 5 hardening changes + Req-1 context-propagation fix + Req-2 Strict Lockout rotation + rubber-duck RE-GATE's 6 additional changes) — build clean, targeted tests green (**653 passed / 0 failed / 6 skipped**). Ready for code-review. See §11 for the implemented lockout+context wave.
**Requested by:** Ahmed (@sabbour)
**Related:** `.learnings/ERRORS.md` ERR-20260709-STEER1 (resolved v0.9.13-rc1, with an explicit *follow-up*), prior `tank-unified-steering-design.md`, `coordinator-unified-steering-directive.md`

---

## 1. Problem & root cause (verified against current code)

Live-preview works end-to-end (v0.9.16-rc1). The **assembly-review loop is not resilient**. On a non-trivial app the internal rubberduck gate requested changes repeatedly, the autonomous steering budget exhausted, and the run **latched terminal `WorkPlanStatus.AssemblyBlocked` and hung** — preview never ran, and **no human could intervene**.

- Live repro: run `ed53860d-1f8e-4130-b3f2-6344fb160b25` (project `9d7569e0…`) → `assembling → assembly_blocked` in ~6s. Earlier: `02e337e5`, `ed53860d`.

Two coupled root causes, both confirmed in code:

### 1a. Budget-exhausted dead-ends at a terminal instead of escalating (Fix-B, the headline)
`CoordinatorAssemblyService.RouteAssemblyGateThroughSteeringAsync` (`CoordinatorAssemblyService.cs:1735`), **`Proceed` branch `~1809-1830`**:

```csharp
if (direction == SteeringDirection.Proceed)
{
    const string reason = "steering_budget_exhausted";
    await CleanupAssemblyBuildTestResourcesAsync(...);
    await _assemblyStore.SetTerminalStatusAsync(workPlanId, WorkPlanStatus.AssemblyBlocked, reason, ct);
    Emit(..., CoordinatorAssemblyBlocked, new { reason, retryable = true });
    await decider.MarkDirectiveAppliedAsync(view.Id, ct);
    return true;
}
```

The decider itself *says* the intent is human review — `CoordinatorSteeringDecider.BuildRationale` (`:571-585`): `Proceed => "budget/blocking — escalate to human review / terminal"` — but the code writes a **terminal** status. `WorkPlanStatus.AssemblyBlocked` is only recoverable by the reconciler if `CanRecoverBlockedAssemblyOnEligibility(reason)` is true; `steering_budget_exhausted` is **not eligibility-recoverable**, so `WaitForBlockedAssemblySteeringAsync` parks the run waiting for an *external* steer that autopilot never sends → hang. This violates Ahmed's standing directives ("all steering goes to the coordinator, which chooses how to direct subtasks"; "missing preview shouldn't block human review").

### 1b. In-place revision can fail to emit the terminal subtask event the watcher recognizes (Fix-A, follow-through)
ERR-STEER1 root cause (Morpheus, confirmed in code): the coordinator **child** pipeline is a **trimmed graph** `agent → child-assemble-ready` with **no failure→terminal edge** (`RunWorkflowFactory.cs:761-767`). `in_place_steer` resumes via `RunOrchestrator.StartRevisionAsync` (a fresh `RunStreamingAsync`, `isChild:true, IsRevision:true`). Post-turn `AgentTurnExecutor.CommitChanges` is the only throwing op; on throw the old code rethrew → MAF `ExecutorFailedEvent`, which `RunWatchLoopService.WatchAsync` records as a step but does **not** terminate on → stream ends → `FailRunSafeAsync("watch_stream_completed_without_terminal_event")` → subtask `failed` → ineligible → assembly wedged.

ERR-STEER1 was **resolved in v0.9.13-rc1** (AgentTurnExecutor transient-commit retry + visible rethrow; `RunWatchLoopService` child `ExecutorFailedEvent` terminalization; coordinator conscious-visible `dispatch_fresh` on in-place-no-terminal). **But the logged follow-up is the open gap for this task:**

> in-place revision produced a clean terminal only **1/3** times; the other 2 fell back to conscious `dispatch_fresh` (`in_place_revision_no_terminal`). Context-preservation works but is not yet the dominant path. Deeper in-place *resume* seam remains.

So today the loop *progresses* (no wedge from STEER1), but change-request **follow-through relies on losing context 2/3 of the time**, which burns the steering budget faster → hits 1a sooner. Fix-A raises in-place terminal reliability so the budget is spent converging, not thrashing.

---

## 2. Fix-B — escalate budget-exhausted → human review gate (state-machine change)

**Principle:** budget bounds *autonomous* convergence, not the run. When autonomy can't converge, hand the SAME assembled changes to the SAME human-review gate the normal happy path uses — the human then approves / declines / steers. Never a terminal dead-end.

### 2.1 New state transition
Replace the `Proceed` branch terminal write with an **escalation to the human-review gate**, reusing the existing D5 machinery verbatim (so recovery, StageId semantics, and the approve path all keep working):

| Before | After |
|---|---|
| `SetTerminalStatusAsync(AssemblyBlocked, "steering_budget_exhausted")` | `SetStatusAndStageAsync(InReview, <human-review StageId>)` |
| `Emit(CoordinatorAssemblyBlocked{retryable=true})` | `Emit(CoordinatorSteeringDecision{decision="proceed", rationale, escalation="human_review"})` **then** the standard `CoordinatorAssemblyReviewRequested{gateKind="human-review", reason="steering_budget_exhausted"}` |
| run hangs awaiting external steer | `UpsertReviewRequestAsync(...)` + `AwaitReviewDecisionAsync(...)` → `ApplyReviewDecisionAsync(...)` (human approve/decline/steer) |

Concretely: the `Proceed` branch calls a new private `EscalateToHumanReviewAsync(context, workPlanId, edges, aggregateTreeHash, touchedFilesBySubtask, reason, ct)` that mirrors the existing `gate.GateKind == "human-review"` block (`CoordinatorAssemblyService.cs:906-950`):

1. Resolve the human-review gate node from `ResolveAssemblyGatesAsync(workPlanId)` to reuse its **`StageId` (canonical `"review"`)** and `GraphNodeId` — keeps the graph/topology consistent, does **not** invent a new stage.
2. `SetStatusAndStageAsync(workPlanId, InReview, humanGate.StageId)`; `EmitGraphAsync`.
3. Emit `CoordinatorAssemblyReviewRequested{ gateKind="human-review", reason="steering_budget_exhausted", treeHash, integrationBranch, includedSubtaskIds }` so the UI opens the review card (with the exhaustion reason visible — no glitch).
4. `CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(...)` — durable so a crash mid-await recovers via the existing `planStatus == InReview → ResumeInReviewAsync` path (`:532`, `:987`). No new recovery code.
5. `AwaitReviewDecisionAsync(...)` → `ApplyReviewDecisionAsync(...)`.
6. `MarkDirectiveAppliedAsync(view.Id)` (the autonomous directive is settled; the human now owns the loop). `return true`.

### 2.2 What approve / decline / steer then do
- **Approve** → existing `ApplyReviewDecisionAsync`/`ApplyAuthoredGateDecisionAsync(Approved)` (`:1119`): clear review record, `SetStatusAndStage(Assembling)`, fall through to `CompleteAfterApprovalAsync` → **one merge → scribe → complete**. Preview/complete now reached. ✔
- **Decline** → existing `assembly_declined` terminal (`:1146`). A conscious human decision, not a glitch. ✔
- **Request-changes / steer** → routes through `RouteAssemblyGateThroughSteeringAsync` with `source = human-review` (already unified, `:1133`). **Key addition:** a human steer is a *fresh mandate* — it must **reset the autonomous steering budget** so the coordinator can converge again under human guidance. See §2.3.

### 2.3 Budget reset on human intervention (prevents immediate re-exhaustion)
`WorkPlan.SteeringIterations` and `Subtask.RecoveryAttempts` bound *autonomous* iteration (`CoordinatorSteeringDecider.DecideAsync` CAS, `:190-215`). When a **human** submits request-changes/steer after escalation, the coordinator resets those counters (guarded CAS, same transaction that relays the human directive) — the human is now in the loop, so autonomy gets a fresh budget window to act on the human's specific feedback. Without this, the human's very first steer would re-hit `Proceed` and bounce straight back to review (a livelock between review and steering).

- New: `CoordinatorSteeringDecider.ResetSteeringBudgetAsync(workPlanId, subtaskIds, ct)` — optimistic-concurrency CAS zeroing `SteeringIterations` (+ target `RecoveryAttempts`), called **only** for `source == human-review` request-changes, before `DecideAsync`. Emitted as part of the visible `steering_received`/`steering_decision` so the reset is auditable.
- Loop-prevention preserved: the reset is gated to *human-sourced* directives; autonomous gates can never reset their own budget (that would reintroduce the infinite loop the budget exists to stop). A configurable **max human-review round-trips** (default 3) backstops a human who keeps rejecting without converging → after N, the gate stays open (awaiting human) but autonomy stops re-steering — never terminal, never looping.

### 2.4 Why not just make `AssemblyBlocked` recoverable?
Because `AssemblyBlocked` semantics = "the *plan* can't proceed, wait for external input"; overloading it for "autonomy exhausted, ask the human" conflates two states and keeps the run in a status the reconciler treats as a passive park. `InReview` is the **existing** state whose entire machinery (gate arm, deferred-decision poll, crash recovery, gate preservation on failure) is built for exactly "a human must decide now." Reusing it is the root-cause fix; adding recovery to `AssemblyBlocked` is symptom-plastering.

---

## 3. Fix-A — reliable terminal emission on in-place revision (follow-through)

Goal: raise the in-place-steer clean-terminal rate from ~1/3 toward the dominant path, so change-requests are applied **in-context** and re-submitted to the gate, converging within budget. Two layers:

### 3a. Runtime/MAF (Morpheus-owned) — the structural root cause
The trimmed child graph `agent → child-assemble-ready` (`RunWorkflowFactory.cs:761-767`) needs a **failure→terminal edge** so a post-turn fault still yields a terminal `WorkflowOutputEvent` (mirroring the fresh-dispatch graph). Equivalently: the revision path must **emit the same terminal subtask event (`child-assemble-ready`) a fresh dispatch emits** after `agent.turn.end`, even when `CommitChanges` degrades (e.g. no-op commit → HEAD tree). This is the "deeper in-place resume seam" the STEER1 follow-up names. **Owner: Morpheus** (I will pair; `RunOrchestrator.StartRevisionAsync`, `RunWorkflowFactory`, `RunWatchLoopService`, `CopilotAIAgent.StreamTurnOnceAsync`). Tank does not modify these.

### 3b. Coordinator contract (Tank-owned) — the guarantee regardless of 3a
The coordinator must never depend on 3a being perfect. Contract, already partially in place from STEER1, hardened here:

1. **Authoritative success = target subtask STATUS, not the effect marker** (already shipped: `DriveOutstandingSteeringExecutionAsync` advances `applied` only when every target is `assemble_ready`/`completed` **AND** every per-child effect marker is confirmed).
2. **Non-clean terminal ⇒ CONSCIOUS visible `dispatch_fresh`** (already shipped: `failedTargets → ConsciousDispatchFreshFallbackAsync`, emits `in_place_revision_failed_terminal` + `dispatch_fresh`). This is what keeps the loop progressing today (the 2/3 fallback).
3. **New (this design):** the conscious `dispatch_fresh` fallback and the in-place retries both **consume the same steering budget**, and on budget exhaustion route to §2's human-review escalation — so "in-place kept failing → fell back to fresh → still failing" ends at the human, never at a terminal.

Net: Fix-A (3a) makes the *common* case converge in-context; Fix-B guarantees the *tail* (still can't converge) reaches a human. Both visible, both bounded.

---

## 4. Files & functions to change (before / after)

| File | Function | Before | After |
|---|---|---|---|
| `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` | `RouteAssemblyGateThroughSteeringAsync` `Proceed` branch (`~1809-1830`) | terminal `AssemblyBlocked` + `CoordinatorAssemblyBlocked` | call new `EscalateToHumanReviewAsync(...)`; emit `steering_decision{decision="proceed", escalation="human_review"}` |
| ″ | **new** `EscalateToHumanReviewAsync(...)` | — | mirror the `human-review` gate block (`:906-950`): resolve human gate node, `SetStatusAndStage(InReview, "review")`, emit `AssemblyReviewRequested{reason="steering_budget_exhausted"}`, `UpsertReviewRequestAsync`, `AwaitReviewDecisionAsync`, `ApplyReviewDecisionAsync`, settle directive |
| ″ | `ApplyAuthoredGateDecisionAsync` / `ApplyReviewDecisionAsync` request-changes (`:1133`) | routes human steer through steering (no budget reset) | for `source == human-review`, call `ResetSteeringBudgetAsync(...)` before routing; bounded by max human round-trips |
| `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs` | **new** `ResetSteeringBudgetAsync(workPlanId, subtaskIds, ct)` | — | guarded CAS zeroing `SteeringIterations` + target `RecoveryAttempts`, single transaction, emitted for audit |
| ″ | `BuildRationale` `Proceed` (`:582`) | "escalate to human review / terminal" | "escalate to human review" (drop "/ terminal" — code now honors it) |
| `packages/Agentweaver.AgentRuntime/Workflow/RunWorkflowFactory.cs` (`761-767`) + `RunOrchestrator.cs` (revision watch) | trimmed child graph terminal edge | **Morpheus** — add failure→terminal edge / emit `child-assemble-ready` on revision post-turn | (Tank pairs, does not own) |

No schema change (reuses `WorkPlanStatus.InReview`, existing review-request persistence, existing `SteeringIterations`/`RecoveryAttempts` columns). No feature flag.

---

## 5. Risks & mitigations

1. **Review ↔ steering livelock** (human rejects → reset budget → autonomy re-exhausts → back to review). *Mitigation:* §2.3 max-human-round-trips backstop; after N, gate stays open for the human but autonomy stops re-steering (never terminal, never looping).
2. **Crash while awaiting the escalated review.** *Mitigation:* durable `UpsertReviewRequestAsync` + existing `InReview → ResumeInReviewAsync` recovery (`:532`, `:987`) — no new recovery path; verified by an existing InReview recovery test pattern.
3. **Replica races on the escalation** (two pods both escalate). *Mitigation:* the escalation runs inside the existing AssemblySteering decision lease (`SetAssemblySteeringAsync` heartbeat, `:1752`); `SetStatusAndStage(InReview)` is a CAS from `AssemblySteering`/`Assembling`; the review gate arm is single-writer per coordinator run. Second pod no-ops (already-InReview).
4. **StageId / 409 regressions.** *Mitigation:* escalation reuses the resolved human-review gate's canonical `StageId ("review")` and `GraphNodeId`; rai stays `"rai"`; the approve path is unchanged (`ApplyAuthoredGateDecisionAsync(Approved)`), so no new 409 on approve.
5. **Budget reset weakens loop-prevention.** *Mitigation:* reset is strictly gated to `source == human-review`; autonomous gates can never reset their own budget. Human presence *is* the new bound.
6. **Fix-A depends on Morpheus.** *Mitigation:* Fix-B is independent and sufficient to eliminate the hang; Fix-A(3b) coordinator contract already guarantees progression today. 3a is an optimization of the in-context rate, not a correctness dependency.

---

## 6. Test plan

### Unit / integration (`tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs`)
1. **Budget-exhausted escalates to review, not terminal:** seed a plan where the decider returns `Proceed` (force `SteeringIterations >= max`); run assembly with a rubberduck request-changes gate → assert **no** `CoordinatorAssemblyBlocked`, **no** `WorkPlanStatus.AssemblyBlocked`; assert `WorkPlanStatus.InReview`, a `CoordinatorAssemblyReviewRequested{reason="steering_budget_exhausted"}`, a durable review-request row, and the coordinator run `AwaitingReview`.
2. **Escalated review → approve → merge/complete:** from state (1), submit approve → assert `Assembling` → merge → `assembly_complete`.
3. **Escalated review → human request-changes resets budget:** from state (1), submit request-changes → assert `ResetSteeringBudgetAsync` zeroed `SteeringIterations`, a visible `steering_received{source=human-review}` + `steering_decision`, and the loop can steer again (not immediate re-`Proceed`).
4. **Human round-trip backstop:** N+1 human rejects → assert gate remains open awaiting human, autonomy stops re-steering, still no terminal.
5. **Crash-mid-escalated-review recovery:** set `InReview` + review-request, drop the pod, re-run → `ResumeInReviewAsync` resumes the same gate (no new gate, no wedge).
6. **Regression guards (do not break):** existing steering tests — `RunAssembly_InPlaceSteer_TargetSubtaskFailed_ConsciouslyDispatchesFresh_NeverWedges`, `RunAssembly_InPlaceSteer_CrashBeforeLaunch_EffectUnconfirmed_DoesNotFalselyApply`, and the InReview/approve/decline suite — stay green.

### Live proof
- Re-run the non-trivial app (visually-stunning landing page + signup + database) that produced `ed53860d` / `02e337e5`. Expected: repeated rubberduck request-changes → in-place revisions converge (Fix-A) OR conscious `dispatch_fresh` fallbacks (visible) → on budget exhaustion the run opens **human review** (reachable review card + preview), a human approves → **merge/complete**. The run **never** latches `assembly_blocked`.
- Repro harness: the intended `files/preview-landing.ps1` live-orchestration script is **not present in-tree today** — this design assumes it is added (or the existing software-delivery orchestration path is used) as the live-repro driver; flag to Ahmed so the harness is provisioned for the re-gate/live proof.

### Build/test gate (at implementation time)
`dotnet build Agentweaver.sln -c Release` then `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --filter "Assembly|Coordinator|Steer|Revision|Preview" -c Release` (delete `%TEMP%\memory.db*` first). Green counts reported; no stubs.

---

## 7. Open questions for the gate
1. **Max human round-trips** default (proposed 3) — configurable via existing coordinator options, or constant?
2. Should the escalated review card **auto-attach the accumulated gate feedback** (all rubberduck rejections) so the human sees why autonomy gave up? (Proposed: yes — pass the aggregated feedback as the review-request context.)
3. Fix-A(3a) scheduling: pair now with Morpheus, or ship Fix-B first (eliminates the hang) and land 3a as a follow-up rate-improvement? (Proposed: Fix-B first, 3a fast-follow — Fix-B is the correctness fix.)

---

## 8. IMPLEMENTED — Fix-B (GO-WITH-CHANGES; rubber-duck's 5 required changes folded in)

**Status:** IMPLEMENTED — ship-first. Fix-A(3a) remains Morpheus's fast-follow (coordinate on the 3b contract; Fix-B stands alone). Build green (`Agentweaver.sln -c Release`, 0 warn/0 err); targeted tests green (`--filter "Assembly|Coordinator|Steer|Revision|Preview"` → **646 passed, 0 failed, 6 skipped** Postgres-integration).

### §7 locked decisions
- **Max human round-trips = 3** — `CoordinatorSteeringDecider.DefaultMaxHumanReviewRoundTrips = 3` (the configurable knob; no options-class exists yet, mirrors the existing `DefaultMaxPlanSteeringIterations` const pattern — no feature flag).
- **Persisted per-plan** — `WorkPlan.HumanReviewRoundTrips` (new column; SQLite + Postgres migrations `20260710004451_AddHumanReviewRoundTrips`).
- **Accumulated gate feedback attached** to the escalated review card, bounded/structured by gate source + round (`BuildAccumulatedGateFeedbackAsync`, cap 32 directives × 2000 chars).

### The 5 required changes (as implemented)
1. **Crash-safe/idempotent escalation as an executable effect.** The `Proceed` branch now `MarkDirectiveExecutingAsync` FIRST, then `EscalateToHumanReviewAsync`. Recovery (`DriveOutstandingSteeringExecutionAsync`, the `DecidedAction == Proceed` branch) verifies the escalation is **durably open** (`IsEscalationDurablyOpenAsync`: `WorkPlan.Status == InReview && stage == "review"` AND a durable review-request row exists) BEFORE `MarkDirectiveAppliedAsync`; if not open it **re-drives** `ParkAtHumanReviewAsync` (idempotent) — steering is never silently dropped. The CAS-lost branch of `ParkAtHumanReviewAsync` also completes a review-request that a crash-after-InReview/before-Upsert left missing.
2. **Settle the directive AFTER the review is durably OPEN, not after the human acts.** `ParkAtHumanReviewAsync` sequences `TryEscalateToInReviewAsync` (guarded CAS) → `UpsertReviewRequestAsync` → emit review-requested → `MarkCoordinatorAwaitingReviewAsync` → `MarkDirectiveAppliedAsync`, then returns. `EscalateToHumanReviewAsync` only afterwards live-awaits `AwaitReviewDecisionAsync` — the directive is never blocked on the human (which could wait forever).
3. **Real CAS for AssemblySteering→InReview.** New `CoordinatorAssemblyStore.TryEscalateToInReviewAsync` — guarded `ExecuteUpdateAsync` `WHERE Status ∈ {AssemblySteering, Assembling} → InReview, stage "review"`; a second replica that finds the plan already `InReview` gets `false` and NO-OPs (no double-escalation, no clobber of a submitted decision). Distinct from the unconditional `SetStatusAndStageAsync`.
4. **Persist human round-trip count.** New `CoordinatorAssemblyStore.IncrementHumanReviewRoundTripAsync` (atomic increment + read, single tx) → `WorkPlan.HumanReviewRoundTrips`. At the top of `RouteAssemblyGateThroughSteeringAsync`, `source == human-review` increments it; while `≤ 3` it calls the decider's new guarded `ResetSteeringBudgetAsync` (zero `SteeringIterations` + target `RecoveryAttempts`, single tx) so the coordinator converges again; past 3 it does NOT reset (decider → `Proceed` → re-park at review). Autonomous sources can never reset their own budget. A visible `coordinator.steering` event records the round-trip + reset decision.
5. **Autopilot/no-human = explicit park at review.** Escalation opens `awaiting_review` with the exhaustion reason + accumulated gate feedback attached; with no human, `AwaitReviewDecisionAsync` parks indefinitely (existing human-gate semantics) — NEVER auto-approve/decline, never terminal, never a hidden loop. For the live preview demo the run sits at the review gate showing the preview.

### Files changed
- `apps/Agentweaver.Api.Data/Memory/WorkPlan.cs` — `HumanReviewRoundTrips` column.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyStore.cs` — `TryEscalateToInReviewAsync`, `IncrementHumanReviewRoundTripAsync`.
- `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs` — `DefaultMaxHumanReviewRoundTrips`, `ResetSteeringBudgetAsync`, `BuildRationale` Proceed text ("escalate to human review").
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` — Proceed branch → escalate; `EscalateToHumanReviewAsync`, `ParkAtHumanReviewAsync`, `BuildAccumulatedGateFeedbackAsync`, `IsEscalationDurablyOpenAsync`; human round-trip/reset wiring; recovery branch.
- `apps/Agentweaver.Api/Migrations/20260710004451_AddHumanReviewRoundTrips.cs` (+ Designer + snapshot); `apps/Agentweaver.Api.Migrations.Postgres/Migrations/20260710004451_AddHumanReviewRoundTrips.cs`.
- `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs` — 6 new tests (below).

### Tests added (all green)
1. `RouteAssembly_BudgetExhausted_EscalatesToHumanReview_NotTerminal` — no `AssemblyBlocked`; plan `InReview`/stage "review"; durable review row; `escalated=true` event; run `AwaitingReview`; directive `Applied`.
2. `RouteAssembly_BudgetExhausted_Escalate_HumanApproves_Completes` — approve → merge → `assembly_complete`; run `Completed`.
3. `RouteAssembly_HumanRequestChanges_UnderCap_ResetsBudget_SteersAgain` — round-trip persisted =1, visible `budgetReset=true`, decision ≠ `Proceed`.
4. `RouteAssembly_HumanRequestChanges_OverCap_DoesNotReset_ReParksAtReview` — round-trip =4, `budgetReset=false`, re-parks `InReview`, no terminal, decision = `Proceed`.
5. `DriveOutstanding_ProceedDirective_CrashBeforeReviewOpen_ReDrivesEscalation` — recovery re-drives, plan `InReview`, durable review written, directive `Applied`, returns true.
6. `DriveOutstanding_ProceedDirective_ReviewAlreadyOpen_SettlesWithoutReDriving` — durably-open escalation settled (not re-driven), returns false.
Plus the two regression guards stay green.

---

## 9. DELTA — Reviewer Rejection Lockout: CONTEXT root-cause FIRST (Requirement 1, LOAD-BEARING)

Ahmed (verbatim): *"for fix B, also follow the lockout procedure (defined in the Squad agent definition). But be sure that the root cause is NOT the retrigger/steering missing the context."* Before any lockout/rotation, prove the repeated-rejection loop is (or isn't) caused by lost context on the re-trigger path. Audit below is **verified in code**.

### 9.1 Context-propagation audit (what each re-trigger hands the revising agent, today)

| Re-trigger path | Prior work (session / worktree) | Latest reviewer feedback | ACCUMULATED cross-round feedback | Verdict |
|---|---|---|---|---|
| **A — in_place_steer** `ExecuteInPlaceSteerAsync` → `RunOrchestrator.StartRevisionAsync` | **PRESERVED.** `StartRevisionAsync` reuses the SAME stream entry ("prior events preserved for replay"), the SAME `WorktreePath`/`WorktreeBranch` (`RunOrchestrator.cs:396,408-409`), flips the run back `InProgress`, injects a revision turn (`IsRevision:true`, line 417). | **CARRIED.** `guidance = BuildAssemblyFeedbackGuidance(feedback)` is the revision task (`CoordinatorAssemblyService.cs:2076`). | **IMPLICIT** via the preserved session history (prior turns' feedback is in the replayed stream), but NOT re-stated in the explicit task. | ✅ Context preserved — the good path. |
| **B — dispatch_fresh** `ConsciousDispatchFreshFallbackAsync` → `RequestChangesAsync` → `ResetSubtasksToPendingAsync` | **DROPPED.** Subtask reset to `Pending`, `ChildRunId = null` (`CoordinatorAssemblyService.cs:3296`) → new pod, fresh worktree, prior child session gone. | **CARRIED (latest only).** `RecoveryGuidance = BuildAssemblyFeedbackGuidance(feedback)`; `ComposeChildTaskAsync` appends it to the child task (`CoordinatorDispatchService.cs:1941-1942`). | **❌ LOST.** `RecoveryGuidance` is a SINGLE field OVERWRITTEN each reset (`ResetSubtasksToPendingAsync`, `CoordinatorAssemblyService.cs:3296`). Only the newest round survives; all prior rounds' feedback is discarded. | ⚠️ **Amnesia**: near-blank pod — no prior work, only the newest complaint. |

### 9.2 ROOT CAUSE (Requirement 1 finding)

**The dispatch_fresh (B) path is the amnesia source and a genuine root cause of the repeated-rejection loop.** When in_place cannot resume (GC'd child, no resumable session — the common case after pods are released under pod-per-run) the coordinator falls to conscious dispatch_fresh, which restarts the subtask from a blank pod carrying ONLY the latest round's feedback. The new agent cannot see (a) what prior attempts produced, or (b) the full accumulated set of requirements across rounds → it re-violates earlier feedback → gets rejected again → loops. **This is agent amnesia masquerading as a quality problem.** It MUST be fixed before any author-rotation, or rotation would just paper over a context bug (and rotate blame between amnesiac pods).

The in_place (A) path is NOT the amnesia source (session + worktree preserved). Its only weakness is that accumulated feedback is implicit (session history) rather than restated — a minor strengthening, not a root cause.

### 9.3 FIX (Requirement 1) — context-carrying revision (both paths), implement BEFORE lockout

1. **Accumulated, not latest.** Replace the single-round `BuildAssemblyFeedbackGuidance(latestFeedback)` used by BOTH `ResetSubtasksToPendingAsync` (B) and `ExecuteInPlaceSteerAsync` (A) with an ACCUMULATED, structured guidance built from `BuildAccumulatedGateFeedbackAsync` (already added in §8) — all prior rounds' feedback, structured by gate source + round. Durable source of truth = the per-run `SteeringDirective` rows (already one per source/round); **no new column** — `BuildAccumulatedGateFeedbackAsync` already reads them, so accumulation is crash/replica-safe by construction.
2. **Carry prior work on the fresh dispatch.** For B (new pod), the guidance must also hand the new agent the PRIOR CONTEXT that "fresh" otherwise drops: a pointer/summary of the prior child's produced diff + the integration-branch state, so it starts from prior work, not a blank slate. This is exactly Fix-A's *"make the conscious dispatch carry context reliably"* contract — **3b split with Morpheus:** Fix-A guarantees `StartRevisionAsync`/the conscious dispatch propagate the prior child's worktree+diff to the (possibly different) revision agent; Fix-B supplies the accumulated-feedback payload + the selected author.
3. **Outcome:** repeated rejections then reflect GENUINE quality problems, at which point author-rotation (§10) is the correct next lever — not before.

---

## 10. DELTA — Strict Lockout revision cycle (Requirement 2, DESIGN ONLY — HOLD until re-gate)

Layer `.github/agents/squad.agent.md:788-809` (Reviewer Rejection Protocol + Strict Lockout) into the assembly-gate rejection cycle. **No lockout/rotation code is written until this delta passes the rubber-duck re-gate.**

### 10.1 The boundary — REJECTION vs GUIDANCE (the key new semantic)

- **REJECTION → lockout + rotate to a DIFFERENT agent.** A Reviewer requests changes on an artifact: sources `rubberduck`, `build-test`, `human-review` (human REJECTS / requests-changes), or another agent acting as reviewer — severity `request-changes`. The artifact's CURRENT author is locked out and MAY NOT produce the next version (protocol steps 1–4). A different eligible agent owns the revision via a coordinator-owned, CONSCIOUS, VISIBLE dispatch carrying FULL context (§9 accumulated feedback + prior work). This is NOT in-place same-author.
- **GUIDANCE / STEER → in-place, SAME agent, context preserved.** A coordinator directive to refine, a human "steer/refine" (not a reject), an RAI `advisory`, or an `agent`/`step` advisory (severity `advisory`). No lockout — resume the same session in place (`StartRevisionAsync`). This is the path Fix-A's in-place-terminal work serves.

Disposition is derived from `(source, severity)`: `request-changes` from a reviewer ⇒ REJECTION; everything else ⇒ GUIDANCE.

### 10.2 Decision-policy change

Today `CoordinatorSteeringDecider` / `SteeringPolicy` pick in_place vs dispatch_fresh purely on resumability + budget. The lockout reframes this: **a REJECTION disposition FORCES a rotate-to-different-agent dispatch (never in_place same author), regardless of resumability.** A GUIDANCE disposition keeps the existing in_place preference.
- Add a `disposition` decision input (rejection | guidance).
- On REJECTION: `SelectRevisionAuthor(roster \ lockedOut)` — reuse `CoordinatorOrchestratorExecutor.ResolveRoster(repoPath)` + best-fit `SelectRosterMember`, filtered to EXCLUDE the subtask's locked-out set. Emit a visible `coordinator.steering_decision { decision = dispatch_fresh, disposition = rejection, rotatedFrom = <prevAuthor>, rotatedTo = <newAuthor>, lockedOut = [...] }` — the rotation is never a glitch.

### 10.3 Durable lockout roster — reuse the DORMANT `Subtask.LockedOutAgents` column

`Subtask.LockedOutAgents` already exists in the schema (since the initial `20260617224038_AddCoordinatorWorkPlan` migration) but is NEVER read/written anywhere in code — a reserved, dormant field. **No new migration needed.**
- On each REJECTION: atomically APPEND the just-rejected author to `Subtask.LockedOutAgents` (JSON string set), via a guarded/crash-safe update (mirror the §8 CAS patterns) so it is cross-replica correct.
- Select the next author from `roster \ LockedOutAgents`.
- Lockout scope = the specific artifact/subtask (protocol step 5). Duration = the revision cycle; each subsequent rejection locks that revision's author too (step 6).

### 10.4 Budget bounds the ROTATION, and deadlock/exhaustion → §1–§8 human-review escalation (protocol step 7)

- The steering budget (`SteeringIterations`) now counts DISTINCT-AGENT revision attempts (rotations) before escalation, not same-agent re-steers. The Fix-B human round-trip counter (change #4) is unchanged.
- **Deadlock** (`roster \ LockedOutAgents == ∅`, all eligible agents locked out) OR rotation budget exhausted OR human round-trips exhausted ⇒ **escalate to human review** via the already-implemented `EscalateToHumanReviewAsync` (§8) — NEVER terminal. This IS protocol step 7 ("escalate to the user").
- Extend the escalated review card / `BuildAccumulatedGateFeedbackAsync` payload with `lockedOutRoster` + per-round author so the human sees WHY autonomy handed off (which agents tried, what each rejection said). Attach alongside the accumulated gate feedback.

### 10.5 Interaction with Fix-A (Morpheus) — 3b contract

- **Fix-A owns:** reliable in-place terminal emission for the GUIDANCE path; AND making the CONSCIOUS dispatch carry context reliably (propagate the prior child's worktree + diff to the — possibly different — revision agent).
- **Fix-B / lockout owns:** the rejection→rotate decision, the durable `LockedOutAgents` roster, the accumulated-feedback payload (§9), and the deadlock→human-review escalation (§8).
- **3b contract:** Fix-A guarantees the dispatch propagates prior worktree+diff to the selected author; Fix-B passes the accumulated feedback + the non-locked author id. Fix-B does not wait on Fix-A (escalation stands alone); the lockout rotation's context-carry quality DEPENDS on Fix-A landing (documented ordering — §9 fix is the prerequisite).

### 10.6 Risks

- **Small roster / single implementer role.** First rejection locks the only eligible agent ⇒ immediate deadlock ⇒ escalate to human review after ONE autonomous attempt. Acceptable (never terminal, never wedge), but must be explicit: with a 1-eligible-agent artifact, strict lockout means autonomy cannot self-revise — it degrades to human review, NOT to re-admitting the locked author (protocol step 7 forbids re-admission). Surfaced as a visible escalation with the lockout reason.
- **Rotation thrash without Fix-A context-carry.** Rotating authors mid-artifact while the fresh dispatch is still amnesiac would make things worse — hence §9 (context fix) is a hard prerequisite and ordered first.
- **Mislabelled disposition.** If a coordinator/human "refine" steer is misclassified as a rejection it would wrongly lock the author; the `(source, severity)` mapping must be precise and unit-tested.

### 10.7 Test plan (add at implement time, POST re-gate)

1. **Context (Req 1):** dispatch_fresh carries ACCUMULATED feedback (all rounds), not just latest; in_place preserves session/worktree. (Assert child task / RecoveryGuidance contains prior-round markers.)
2. **Rejection → rotate:** reviewer rejects → a DIFFERENT agent owns the revision; original appended to durable `LockedOutAgents`; visible `rotatedTo` event.
3. **Second rejection → second lockout:** revision rejected → its author also locked; third agent selected.
4. **Deadlock → escalate:** all eligible agents locked ⇒ `EscalateToHumanReviewAsync` with `lockedOutRoster` + accumulated feedback attached; never terminal.
5. **Guidance/advisory → in-place SAME agent:** an advisory/coordinator-refine steer does NOT lock the author and resumes in place.
6. **Budget bounds rotations:** N distinct-agent attempts then escalate (not infinite rotation).
7. **Regression:** all §8 escalation tests + prior steering regressions stay green.

### 10.8 What is ALREADY safe to keep (no re-gate needed)

§1–§8 (escalation state-machine + 5 hardening changes) are implemented and independent of the lockout delta: they only change the budget-exhausted DEAD-END into a human-review escalation. The lockout delta ADDS author-rotation ABOVE that escalation and the §9 context fix BELOW it; neither invalidates the escalation. Escalation stays the terminal-avoidance backstop for BOTH "budget exhausted" and "deadlock (all agents locked)".

## 11. IMPLEMENTED — Req-1 context-propagation fix + Req-2 Strict Lockout (RE-GATE GO-WITH-CHANGES; 6 additional changes folded in)

**Status:** IMPLEMENTED. Build clean (`Agentweaver.sln -c Release`, 0 warn/0 err). Targeted tests green (`--filter "Assembly|Coordinator|Steer|Revision|Preview|Lockout"` → **653 passed, 0 failed, 6 skipped**). Implemented in strict order: (a) Req-1 context fix on BOTH paths + tests → (b) escalation + 5 hardening [already landed §8] → (c) Req-2 lockout gated on (a).

### 11.1 Req-1 — context-propagation root-cause fix (rubber-duck changes #1, #2, #6-in-place)

- **#1 — capture prior child pointer BEFORE clearing `ChildRunId`.** New `Subtask.PriorChildRunId` column (SQLite ef migration `20260710013137_AddSubtaskPriorChildRunId` + hand-written Postgres mirror). `ResetSubtasksToPendingAsync` (rewritten, new signature `(coordinatorRunId, subtaskIds, feedback, ct)`) now sets `PriorChildRunId = old ChildRunId` **before** nulling `ChildRunId`, so "prior diff + integration state" is mechanically recoverable by the fresh dispatch.
- **#2 — accumulated feedback is TARGET-scoped and REJECTION-scoped, exposed as a STABLE named contract.** The accumulated gate feedback is exposed as a **shared named DTO** (`AccumulatedReviewFeedback` / `ReviewFeedbackRound`) — see §11.6 for the finalized cross-assembly contract. It filters `SteeringDirective` rows by `Severity ∈ {RequestChanges, Blocking}` AND target-subtask overlap (parses `TargetScopeJson` in memory) — ALL prior rounds, not just the latest — and renders a deterministic revision prompt via `ReviewFeedbackRenderer.RenderForRevisionPrompt` (used by BOTH the in-place resume and the conscious fresh/rotated dispatch). Replaces the removed private `AccumulatedFeedbackEntry` record + `BuildContextCarryingRetryGuidance`.
- **#6 in-place carry.** `ExecuteInPlaceSteerAsync` now builds guidance from `BuildContextCarryingRetryGuidance(feedback, accumulated, priorChildRunId:null, ...)` — the stream is removed before restart, so accumulated feedback is threaded EXPLICITLY (never relies on stream history). `priorChildRunId:null` because the in-place resume preserves the child session (no fresh pod).

### 11.2 Req-2 — Strict Lockout rotation (rubber-duck changes #3, #4, #5, #6-discriminator)

- **#6 discriminator.** `IsReviewerRejection(severity) => severity is RequestChanges or Blocking`. In `RouteAssemblyGateThroughSteeringAsync`, after the decider decision: a reviewer REJECTION with an actionable direction (`InPlaceSteer`|`DispatchFresh`) → `MarkDirectiveExecutingAsync` then `ExecuteLockoutRotationAsync` (lockout/rotate). Advisory/refine/steer stays on the existing in-place A/D path (same agent, context preserved).
- **#3 gate rotation on Req-1.** `ExecuteLockoutRotationAsync` computes `hasContext = feedback≠∅ || accumulated.Count>0`; if false it does NOT rotate blind — escalates with reason `lockout_no_context`. The rotated re-dispatch reuses `RequestChangesAsync → ResetSubtasksToPendingAsync`, which threads the Req-1 accumulated guidance + prior pointer.
- **#5 real domain-eligibility.** New `IAssemblyAuthorRotationSelector` (+ default `SquadAuthorRotationSelector`, `CoordinatorAuthorRotation.cs`) reads the project team roster (`SquadReader`), filters to dispatchable members that are NOT locked-out and NOT the current author, and requires a **strictly-positive** domain-capability score. A single-eligible-agent domain → no positive candidate after excluding the author → `null` → deadlock → escalate to human review (never rotate to an unrelated agent).
- **#4 atomic CAS rotation.** `CoordinatorAssemblyStore.TryRotateSubtaskAuthorAsync` does a guarded `ExecuteUpdate WHERE Id==id AND AssignedAgent==expectedAuthor`: first replica wins (swaps `AssignedAgent`/`SelectedModelId`/`AgentCharter` + appends the rejected author to `LockedOutAgents` JSON), a concurrent second matches 0 rows → `Won=false` no-op (no double-append). `GetLockedOutAgentsAsync` reads the durable roster before each rotation.
- **Deadlock / no-context → escalate (protocol step 7).** `OverrideDecidedActionAsync(Proceed)` + a visible `coordinator_steering_decision` event (`disposition=rejection`, rationale `lockout_deadlock`/`lockout_no_context`, `lockedOutRoster`) + `EscalateToHumanReviewAsync` (§8). Never terminal, never blind rotation.
- **Visibility.** Every rotation emits a `coordinator_steering_decision` event: `{decision=dispatch_fresh, disposition=rejection, rotatedFrom, rotatedTo, lockedOutRoster, attempt}` — the conscious/visible dispatch Ahmed required (never a silent glitch).

### 11.3 Files changed

- `apps/Agentweaver.Api.Data/Memory/Subtask.cs` — new `PriorChildRunId` column.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` — discriminator + `ExecuteLockoutRotationAsync` + `IsReviewerRejection` + `RotationSelector`; rewritten `ResetSubtasksToPendingAsync`; new internal `BuildAccumulatedReviewFeedbackAsync` (per-subtask bundle producer, returns the named `AccumulatedReviewFeedback` contract) + `BuildPriorReviewRoundsAsync` (target+rejection-scoped rounds); in-place + rotation + reset all render via `ReviewFeedbackRenderer.RenderForRevisionPrompt`; removed private `AccumulatedFeedbackEntry`/`BuildContextCarryingRetryGuidance`/`BuildAssemblyFeedbackGuidance`.
- `packages/Agentweaver.Domain/AccumulatedReviewFeedback.cs` (NEW) — the STABLE cross-assembly `AccumulatedReviewFeedback` + `ReviewFeedbackRound` DTOs + `ReviewFeedbackRenderer.RenderForRevisionPrompt` (Morpheus Path-2 handoff contract). Lives in `Agentweaver.Domain` so BOTH `Agentweaver.Api` and `Agentweaver.AgentRuntime` can reference it.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAuthorRotation.cs` (NEW) — `IAssemblyAuthorRotationSelector`, `SquadAuthorRotationSelector`, `RotationSubtaskContext`, `RotationChoice`.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyStore.cs` — `TryRotateSubtaskAuthorAsync` + `GetLockedOutAgentsAsync` + `SubtaskRotationResult` (guarded CAS).
- `apps/Agentweaver.Api/Migrations/20260710013137_AddSubtaskPriorChildRunId.*` (SQLite ef) + `apps/Agentweaver.Api.Migrations.Postgres/Migrations/20260710013137_AddSubtaskPriorChildRunId.cs` (hand-written).
- `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs` — `ConfigurableRotationSelector` fake + 6 new tests (below).

### 11.4 Tests added (all green)

1. `RouteAssembly_Rejection_DispatchFresh_CarriesAccumulatedFeedbackAndPriorWork` — Req-1 #1/#2: fresh dispatch carries all prior rounds + prior-child pointer.
2. `InPlaceRetryGuidance_CarriesAccumulatedFeedback_PreservesSession_NoPriorPodPointer` — Req-1 #6: in-place threads accumulated feedback, omits the fresh-pod pointer.
3. `RouteAssembly_Rejection_LocksOutAuthor_RotatesToDifferentEligibleAgent` — Req-2: rotation to a different eligible agent, durable `LockedOutAgents`, visible `rotatedTo` event.
4. `RouteAssembly_Rejection_SingleEligibleDomain_EscalatesToHumanReview_NotTerminal` — Req-2 #5: single-eligible domain deadlocks → escalate (never terminal), `lockedOutRoster` on the card.
5. `IsReviewerRejection_Discriminates_RequestChangesAndBlocking_FromAdvisory` — Req-2 #6 discriminator.
6. `TryRotateSubtaskAuthor_ConcurrentReplicas_ExactlyOneWins_NoDoubleAppend` — Req-2 #4: guarded CAS, exactly one winner, no double-append.

Plus all prior §8 escalation + steering regression tests stay green.

### 11.5 Morpheus 3b/Path-2 contract (runtime handoff seam) — STABLE named DTO

See **§11.6** for the FINALIZED cross-assembly contract (`AccumulatedReviewFeedback` in `Agentweaver.Domain`). Tank owns the coordinator-side threading and the stable named contract Morpheus consumes; Morpheus's `StartChildRevisionHandoffAsync` MUST consume this DTO (or its `RenderedGuidance`) — it MUST NOT read `SteeringDirective` rows or define a parallel shape. Fix-B does not wait on Morpheus; the coordinator-side context-carry + rotation stand alone.

### 11.6 Contract addendum (FINALIZED) — shared handoff DTO moved to `Agentweaver.Domain`

The handoff DTO must be referenceable from BOTH the producer (`Agentweaver.Api` / `CoordinatorAssemblyService`) AND Morpheus's consumer seam (`Agentweaver.AgentRuntime`). Project graph: `Agentweaver.Api → Agentweaver.AgentRuntime → Agentweaver.Domain`; AgentRuntime does NOT reference Api. Therefore the shared contract lives in **`Agentweaver.Domain`** (the only project both reference). The earlier Api-local `AccumulatedGateFeedback` was unreferenceable from AgentRuntime and has been replaced.

- **DTOs (namespace `Agentweaver.Domain`, `packages/Agentweaver.Domain/AccumulatedReviewFeedback.cs`):**
  ```csharp
  public sealed record AccumulatedReviewFeedback(
      string SubtaskId,
      string CurrentChangeRequest,
      IReadOnlyList<ReviewFeedbackRound> PriorRounds,
      string PriorWorktreeBranch,
      string? RenderedGuidance = null);
  public sealed record ReviewFeedbackRound(int Round, string Reviewer, string Feedback, DateTimeOffset At);
  ```
- **Renderer:** `public static string ReviewFeedbackRenderer.RenderForRevisionPrompt(string? currentChangeRequest, IReadOnlyList<ReviewFeedbackRound> priorRounds, string? priorWorktreeBranch)` (+ a `this AccumulatedReviewFeedback` bundle overload). Deterministic, prompt-ready. The `PriorWorktreeBranch` line is emitted ONLY when a branch is supplied — the in-place resume renders with `priorWorktreeBranch: null` (session preserved, no fresh pod); the fresh/rotated dispatch always carries a branch (prior child `WorktreeBranch`, falling back to the integration branch).
- **Producer:** `internal Task<AccumulatedReviewFeedback> CoordinatorAssemblyService.BuildAccumulatedReviewFeedbackAsync(string coordinatorRunId, int subtaskId, string currentChangeRequest, string? priorChildRunId, CancellationToken ct)` — per-subtask bundle; resolves `PriorWorktreeBranch` from the prior child `Run.WorktreeBranch ?? IntegrationBranchName(coordinatorRunId)`; sets `RenderedGuidance`. Target+rejection-scoped rounds come from `internal Task<IReadOnlyList<ReviewFeedbackRound>> BuildPriorReviewRoundsAsync(string coordinatorRunId, IReadOnlyCollection<int> subtaskIds, CancellationToken ct)`.
- **Consumer (Morpheus Path-2):** `Task StartChildRevisionHandoffAsync(Run newAgentRun, Run priorChild, AccumulatedReviewFeedback feedback, CancellationToken ct)` — reuses `PriorWorktreeBranch` while minting a NEW SDK session for the non-locked-out agent (must NOT resume `agentweaver-run-{runId}`), injecting `RenderedGuidance` into the new agent's task prompt.
- **Validation:** build clean (`Agentweaver.sln -c Release`, 0/0); targeted tests green (`--filter "Assembly|Coordinator|Steer|Revision|Preview|Lockout"` → **653 passed, 0 failed, 6 skipped**).

### 11.7 Path-2 WIRING (code-review follow-up) — lockout rotation dispatches via the context-carrying handoff

Code review flagged that the lockout rotation (`ExecuteLockoutRotationAsync`) was allocating a fresh child (new session ⇒ lockout-correct) but going through the PLAIN fresh dispatch (`RequestChangesAsync → ResetSubtasksToPendingAsync → StartDispatch → StartChildRunAsync`), which provisions a BRAND-NEW worktree branched from the integration branch and DISCARDS the locked-out author's uncommitted/staged worktree work. `StartChildRevisionHandoffAsync` (reuse prior worktree/branch + NEW session + inject accumulated feedback, with a visible clean-worktree fallback for a poisoned tree) was tested-but-unwired dead code. Now wired:

- **New method `CoordinatorAssemblyService.DispatchLockoutHandoffAsync`** replaces the `RequestChangesAsync` call at the end of `ExecuteLockoutRotationAsync` (different-agent lockout path ONLY). For each rotated target it: (1) resolves the locked-out author's prior child run (`Subtask.ChildRunId`, still pointing at it here — the durable worktree/branch source), (2) builds the `AccumulatedReviewFeedback` bundle, (3) allocates a fresh child run (`priorChild with { Id = RunId.New(), AgentName/ModelId/AgentCharter = rotated author, ParentRunId, SubtaskId, base Task = prior child's task WITHOUT rendered guidance }`) — NOT pre-inserted (the handoff calls `InsertAsync` itself), (4) launches `IChildRevisionHandoff.StartChildRevisionHandoffAsync`, and (5) repoints the subtask at the new child (`SubtaskStatus.Running`, `PriorChildRunId` retained) via `SetSubtaskHandoffRunningAsync` so the re-armed dispatch loop RE-OBSERVES it (never re-dispatches a duplicate). Then the plan returns to `Dispatching` + `StartDispatch`. Guidance is injected by the handoff — the subtask's `RecoveryGuidance` is deliberately NOT set on this path (no double-carry).
- **Fallback:** a rotated target with NO resolvable prior child (nothing to reuse) falls through to the plain fresh dispatch (`ResetSubtasksToPendingAsync`, which threads the accumulated guidance via `RecoveryGuidance`; the dispatch engine composes a fresh child under the already-persisted rotated author).
- **DI seam `IChildRevisionHandoff`** (`apps/Agentweaver.Api/Coordinator/IChildRevisionHandoff.cs`, production impl `RunOrchestratorChildRevisionHandoff` registered in `Program.cs`) — a thin pass-through to `RunOrchestrator.StartChildRevisionHandoffAsync` so the coordinator consumes the handoff via an interface the orchestration unit tests can substitute. **`RunOrchestrator.cs` is UNCHANGED** (Morpheus owns it; consumed via DI). Worktree safety + the `coordinator.child_revision_handoff` strategy event stay entirely in his method.
- **Guardrails honored:** wiring is scoped to the different-agent lockout rotation (reviewer `RequestChanges`/`Blocking` ⇒ rotate); the same-agent advisory/steer in-place path (`StartRevisionAsync`) is untouched; a locked-out author is never selected (eligibility predicate); deadlock / budget-exhausted still escalates to human review (never a handoff, never terminal); the trimmed child pipeline (`isChild:true`) means Fix-A's failure→terminal edge governs terminal emission.
- **Tests:** `RouteAssembly_Rejection_Lockout_DispatchesToDifferentAgentViaContextCarryingHandoff` (new — asserts the handoff is invoked once with `NewAgentRun.Id ≠ priorChild`, rotated author ≠ locked-out author, target+rejection-scoped feedback + prior worktree branch threaded, prior pointer retained, no `RecoveryGuidance` double-carry, never terminal). `RouteAssembly_Rejection_DispatchFresh_CarriesAccumulatedFeedbackAndPriorWork` updated to assert the same via the handoff seam (a reusable prior child now routes through the handoff, not `RecoveryGuidance`).
- **Validation:** build clean (`Agentweaver.sln -c Release`, 0 warn/0 err); `dotnet test --filter "Assembly|Coordinator|Steer|Revision|Preview|WatchLoop|Terminal|Workflow|Lockout" -c Release` → **762 passed, 0 failed, 12 skipped**.

### 11.8 Delta code-review fixes — guidance-free handoff base + CAS-winner guard

A delta code review of the §11.7 wiring found one Medium/High bug and one cheap defensive nit:

- **BUG — compounding guidance duplication across repeated rotations.** `DispatchLockoutHandoffAsync` built the new agent's run as `priorChild with { … }`, which does NOT reset `Task`, so `newAgentRun.Task == priorChild.Task`. On the FIRST rotation `priorChild` is the original fresh child (guidance-free), so the handoff's single `RenderedGuidance` append was the only guidance — correct. But on the 2nd+ rotation `priorChild` is itself a PRIOR HANDOFF child whose persisted `Task` is already `base + guidance(round1…)` (RunOrchestrator persists `Task = newAgentRun.Task + "\n\n" + guidance`). Because `BuildPriorReviewRoundsAsync` re-renders ALL prior request-changes rounds (oldest→newest), `bundle.RenderedGuidance` already contains round-1's feedback; appending it onto a `Task` that ALREADY embeds round-1 duplicated round-1's guidance — compounding on every further rotation. Not a lockout bypass or crash, but unbounded prompt duplication that dilutes the new agent's task and burns tokens, violating the "never double-append" invariant.
  - **Root-cause fix:** the handoff base `Task` is now the GUIDANCE-FREE canonical subtask text derived from the `Subtask` definition via the new `BuildCanonicalSubtaskTask(subtask)` helper (`Title` + optional `\n\n` + `Scope`, mirroring `CoordinatorDispatchService.ComposeChildTaskAsync`'s pre-guidance base) — NOT `priorChild.Task`. The handoff's single `RenderedGuidance` append is therefore the ONLY guidance present on every rotation.
- **Nit — guard the handoff dispatch on the rotation CAS winner.** `ExecuteLockoutRotationAsync`'s rotation loop now collects only the targets whose `TryRotateSubtaskAuthorAsync` returned `result.Won` into a `rotated` list and passes THAT (not `planned`) to `DispatchLockoutHandoffAsync`. The directive-level single-writer lease already serializes replicas, so this is belt-and-suspenders — a CAS-loser can never launch a handoff child + repoint the subtask.
- **Test:** `RouteAssembly_Rejection_Lockout_TwoRotations_Round1GuidanceAppearsExactlyOnce` drives TWO consecutive lockout rotations on the same subtask (rotation 2's `priorChild` is rotation 1's handoff child, which embeds round-1 guidance) and asserts the round-1 marker appears EXACTLY ONCE in the final child's persisted `Task` — proving no compounding duplication. The test fake `FakeChildRevisionHandoff` now mirrors `RunOrchestrator` by persisting the inserted child's `Task = base + "\n\n" + RenderedGuidance`, so the regression is observable.
- **Validation:** build clean (`Agentweaver.sln -c Release`, 0 warn/0 err); `dotnet test --filter "Assembly|Coordinator|Steer|Revision|Preview|WatchLoop|Terminal|Workflow|Lockout" -c Release` → **763 passed, 0 failed, 12 skipped**. **`RunOrchestrator.cs` remains UNCHANGED.**

---

## 2026-07-11T00:00:00Z: Decision: Dependency-base propagation fix — IMPLEMENTATION

**Source:** `.squad/decisions/inbox/tank-depbase-impl.md`

# Decision: Dependency-base propagation fix — IMPLEMENTATION

- **Author:** Tank (Backend Engineer)
- **Date:** 2026-07-11
- **Status:** Implemented — pending code-review / release (Link owns release)
- **Requested by:** Ahmed Sabbour
- **Design gate:** Rubber-duck GO-WITH-CHANGES (5 BLOCKING required changes, all folded in)
- **Root-cause only** (Ahmed's hard rule): no retries/sleeps/symptom-plaster added.

## Root cause (confirmed)
`run.Diff` is a best-effort DISPLAY string. `WorktreeOperationsAdapter.GetDiff` swallows all
exceptions and can return EMPTY even after a real commit. Code used empty `run.Diff` as the sentinel
for "this branch has no artifacts," silently dropping committed child branches. The authoritative
artifact is the committed worktree branch (tip tree == `run.TreeHash`), not the diff string.

## Implementation (approach (c) + all 5 BLOCKING changes)

### New inclusion authority
`apps/Agentweaver.Api/Coordinator/DependencyBranchInclusion.cs` — single source of truth for the
inclusion predicate. `Evaluate(worktreeManager, repoPath, worktreeBranch, treeHash)` returns
`Include | ExcludeMissingBranch | ExcludeTreeMismatch`. Diff is **not** an input. Used by #1 and #2.

### WorktreeManager helpers (BLOCKING #2/#3)
`apps/Agentweaver.Api/Git/WorktreeManager.cs`:
- `BranchTipMatchesTree(repo, branch, expectedTreeSha)` — validity predicate (exists AND, when
  expectedTreeSha non-empty, tip.Tree.Sha == expectedTreeSha). Replaces weak `BranchExists`.
- `GetBranchTipCommitSha(repo, branch)` — tip commit sha for the contains-check.
- `BranchContains(repo, branch, candidateTipSha)` — merge-base ancestor check (FindMergeBase).

### #1 RebuildDependencyBaseBranchAsync (CoordinatorDispatchService)
Include a satisfied dependency by branch VALIDITY, not `run.Diff`. LOUD `LogError` (subtaskId,
childRunId, WorktreeBranch, TreeHash) when a satisfied dependency is excluded for a missing branch or
a tip-tree mismatch. No-op branches are Included (BuildIntegrationBranch no-ops them) — no deadlock.

### #2 (BLOCKING #1) BuildAssemblyInputsAsync (CoordinatorAssemblyService)
Same validity rule for FINAL collective assembly. `Diff` kept ONLY for touched-file extraction. LOUD
`LogError` on exclusion. Added optional `WorktreeManager` ctor param (DI singleton already
registered); when absent (pure unit contexts) it preserves legacy branch+diff behaviour.

### #4 (BLOCKING #3) ResolveChildBaseBranchAsync — mandatory verify + repair
Before returning the integration branch, verify it CONTAINS every satisfied transitive dependency
HEAD (`BranchContains`). If missing → repair once (`RebuildDependencyBaseBranchAsync`) → re-check. If
still incomplete → do NOT silently fall back to origin: LOUD `LogError` and return `null` (a
dispatch-BLOCKING sentinel; `DispatchOneAsync` leaves the subtask pending). Existing loud fallback log
for an ENTIRELY-ABSENT integration branch is preserved.

### #5 (BLOCKING #4) Replica/concurrency
Added a code comment documenting that the contains-check + repair in #4 is the authoritative guard
against a clobbered/incomplete integration ref (BuildIntegrationBranch deletes+recreates the ref).
No new cross-process lock added: the dispatch loop is single-writer per plan (StartDispatch `_active`
guard) and the rebuild is headless + idempotent, so a re-run re-derives the same branch. Documented
why this is sufficient.

### #6 (BLOCKING #5) Conflict behavior
Dependency-base rebuild now emits a LOUD `LogWarning` for EACH auto-resolution (naming branch +
files) so accepting a later child's version never silently overwrites earlier work at Information
level. The existing `IntegrationBranchOutcome.Conflict` warning path is retained.

## Tests
`tests/Agentweaver.Tests/Coordinator/DependencyBasePropagationTests.cs` (11 tests, real temp git repo +
real SqliteRunStore + real EF MemoryDbContext; private methods invoked via reflection):
- WorktreeManager helpers (validity, contains, stale mismatch).
- Inclusion authority: committed child with empty diff → Include; missing → ExcludeMissingBranch;
  stale tree → ExcludeTreeMismatch; no-op branch → Include.
- Rebuild: empty-diff committed dependency included and files reach the base; multi-dependency merged
  in topological order; stale/mismatched branch excluded.
- Resolve: repairs an incomplete integration branch before returning; no-op dependency proceeds (no
  hang); in-place-steer regression — after a re-commit the repair uses the NEW tip (asserts blob ==
  `v2 - steered`).
- Final assembly: BuildAssemblyInputsAsync includes the committed child with empty Diff.

These genuinely fail against pre-fix code (which required a non-empty `run.Diff` to include a branch).

## Validation
- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release --no-restore` → 0 warnings, 0 errors.
- `dotnet test ... --filter "Coordinator|Assembly|Steer|Worktree|IntegrationBranch" -c Release` →
  **537 passed, 0 failed** (includes the 11 new tests).

## Files changed
- `apps/Agentweaver.Api/Coordinator/DependencyBranchInclusion.cs` (new)
- `apps/Agentweaver.Api/Git/WorktreeManager.cs`
- `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs`
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs`
- `tests/Agentweaver.Tests/Coordinator/DependencyBasePropagationTests.cs` (new)

## Not done (per instructions)
No VERSION bump, no commit/push/deploy — coordinator (Link) handles release after code-review.

---

## 2026-07-11T00:00:00Z: Decision: Dependency-base propagation fix (integration-branch rebuild sentinel)

**Source:** `.squad/decisions/inbox/tank-depbase-propagation-fix.md`

# Decision: Dependency-base propagation fix (integration-branch rebuild sentinel)

- **Author:** Tank (Backend Engineer)
- **Date:** 2026-07-11
- **Status:** Proposed — pending rubber-duck design gate
- **Requested by:** Ahmed Sabbour
- **Scope:** DESIGN ONLY. No source changed.

## Problem
In a coordinator run with dependent subtasks (plan #35 -> impl #36 -> tests #37),
the validation subtask (#37) branched from an integration base that contained only
`plan.md` — the implemented app (#36) was missing — so QA re-implemented the app.

## Verification finding (race REFUTED, real root cause identified)
The literal "AssembleReady observed before run.Diff persisted" timing race is **refuted**:
`EfRunStore.SetAssembleReadyAsync` writes `Status=assemble_ready`, `TreeHash`, `WorktreeBranch`
and `Diff` in a **single atomic `ExecuteUpdateAsync`**, and `RunWatchLoopService` emits
`RunAssembleReady` only **after** that write returns. So whenever the coordinator observes
assemble_ready (store fast-path or event -> `GetAsync`), `run.Diff` is already whatever value it
will ever be.

The real defect: `run.Diff` is a **best-effort textual display artifact**. In
`AgentTurnExecutor` the worktree is committed first (`CommitChangesWithRetryAsync` -> real
`treeHash`, real branch commit = source of truth), then `diff = _worktreeOps.GetDiff(...)` is
computed best-effort (the surrounding contract says GetDiff/GetStepCount swallow their own
errors). A dependency can therefore have a **committed worktree branch with real files** but an
**empty `run.Diff`** string. `RebuildDependencyBaseBranchAsync` gates inclusion on
`!string.IsNullOrEmpty(run.Diff)`, so it silently drops that dependency's branch from the
integration branch -> dependents branch from a base missing the app.

## Chosen approach: (c) Source inclusion from the committed worktree branch
`RebuildDependencyBaseBranchAsync` will include a satisfied dependency's `run.WorktreeBranch`
whenever that branch **exists** in the repo (via `WorktreeManager.BranchExists`), NOT based on the
`run.Diff` string. `WorktreeManager.BuildIntegrationBranch` already no-ops unchanged branches
(merge-base == child tip) and fast-forwards, so passing a genuinely-empty dependency is safe and
cannot deadlock. When a satisfied dependency is excluded (branch name empty or branch missing) we
emit a **LOUD** `LogError` (today it is silent) — that is a real contract violation because a
satisfied child must have committed its branch.

This distinguishes the two cases correctly:
- committed changes but empty/unreliable Diff -> branch exists & ahead of base -> **INCLUDED** (bug fixed)
- intentionally no changes -> branch exists, no-op merge -> **PROCEEDS** (no deadlock)
- branch genuinely missing -> **LOUD error** (should never happen post-assemble_ready)

Secondary hardening (fold in): `ResolveChildBaseBranchAsync` verifies the integration branch tip
contains each satisfied dependency's HEAD and triggers a rebuild/repair if not (approach (d) as a
belt-and-suspenders guard before dispatching a dependent).

## Alternatives rejected
- **(a) Gate assemble_ready handoff on Diff availability** — treats a non-existent timing race as
  real; would deadlock/stall on legitimately empty-diff dependencies; Diff empty != no artifacts.
- **(b) Re-run rebuild once Diff "lands"** — symptom-plaster (retries/waits); Diff may never become
  non-empty (GetDiff swallowed an error / genuine no-op); Ahmed's hard rule forbids this.
- **(d) alone** — good defensiveness but leaves the wrong sentinel in the primary rebuild path;
  adopted only as secondary hardening on top of (c).

## Replica safety
Dispatch loop is single-writer per plan; the fix reads git branch state (source of truth) and the
run row, adds no new cross-writer state, and `BuildIntegrationBranch` is headless + idempotent
(reset-to-origin then re-merge), so a re-run or crash-mid-rebuild re-derives the same branch.

## Files touched (planned, not implemented)
- `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs`
  - `RebuildDependencyBaseBranchAsync`: replace `&& !string.IsNullOrEmpty(run.Diff)` with a
    branch-existence check; add LOUD LogError on exclusion of a satisfied dependency.
  - `ResolveChildBaseBranchAsync`: verify/repair integration branch contains each dependency HEAD.
- Tests under `tests/Agentweaver.Tests/Coordinator/` (see design doc test plan).

---

## 2026-07-11T00:00:00Z: Design: Decider-owned routing for the assembly-gate steering handler (in-place vs. lockout rotation)

**Source:** `.squad/decisions/inbox/tank-fixb-decider-owned-routing.md`

# Design: Decider-owned routing for the assembly-gate steering handler (in-place vs. lockout rotation)

**Author:** Tank (Backend / Coordinator)
**Date:** 2026-07-09T00:00:00Z
**Status:** IMPLEMENTED — build clean (API + tests, 0 warnings / 0 errors), targeted tests green (**508 passed / 0 failed / 0 skipped** on filter `Steer|Coordinator|Assembly`).
**Requested by:** Ahmed (@sabbour)
**Related:** run `19cec519` (live root-cause), rubber-duck gate (3 required changes), prior `tank-assembly-review-resilience-design.md` (Fix-B / Strict Lockout wave).

---

## 1. Problem & root cause

Iterative build-test / reviewer feedback at an assembly gate was ALWAYS being force-rotated to a
DIFFERENT agent, discarding the accumulated context of the current author, even when the decider had
correctly chosen `in_place_steer`. Root cause in `CoordinatorAssemblyService.RouteAssemblyGateThroughSteeringAsync`:

```csharp
var isRejection = IsReviewerRejection(SteeringSeverity.RequestChanges); // ALWAYS true (gate hard-codes RequestChanges)
if (isRejection && (direction == InPlaceSteer || direction == DispatchFresh))
    return ExecuteLockoutRotationAsync(...); // OVERRODE the decider's in_place_steer choice on EVERY gate
```

The decider (`CoordinatorSteeringDecider` / `SteeringPolicy`) was already authoritative and correct;
the blanket post-decision override defeated it.

## 2. The fix (decider-owned routing)

Deleted the blanket `isRejection` override. The gate now routes PURELY by `decision.Direction`:

- `InPlaceSteer`  → `ExecuteInPlaceSteerAsync` (SAME author, session/worktree resumed, context preserved).
- `DispatchFresh` → `ExecuteLockoutRotationAsync` (conscious lockout rotation to a DIFFERENT eligible
  agent, target-author only, full accumulated context; deadlock / no-context → escalate to human review).
- `Proceed`       → `EscalateToHumanReviewAsync` (budget exhausted / blocking — Fix-B preserved).
- `Advisory`      → restore assembling + mark applied.

The decider's decision logic and thresholds were NOT touched.

## 3. Rubber-duck required changes

1. **Crash-recoverability of DispatchFresh (BLOCKING).** `DriveOutstandingSteeringExecutionAsync`
   previously blindly marked any non-in-place, non-proceed directive `applied`. With DispatchFresh now
   mapping to the multi-step `ExecuteLockoutRotationAsync`, a crash after `MarkDirectiveExecutingAsync`
   but before the effect completed would silently drop the rotation/handoff. FIX: a DispatchFresh
   directive stuck `executing` is now RE-DRIVEN via `ExecuteLockoutRotationAsync` (mirroring how
   in-place and proceed are re-driven). Idempotency:
   - `TryRotateSubtaskAuthorAsync` writes the durable `(LastResetDirectiveId, LastResetAttempt)` stamp
     ATOMICALLY with the rotation CAS; the re-drive SKIPS re-selecting an author for an already-rotated
     target (never double-rotates off the rotated author), carrying it straight to the handoff.
   - `DispatchLockoutHandoffAsync` SKIPS a target whose current child already belongs to the rotated
     author (never double-dispatches).
   - Insufficient context (no target subtask ids on the directive) → escalate to human review rather
     than silently apply.
2. **Kept the pre-decision human-review budget reset** (`source == HumanReview` branch). Only the
   POST-decision override was deleted — Fix-B's "human request-changes after budget exhaustion gets a
   fresh autonomous convergence pass" is intact.
3. **Aligned dispatch_fresh comments/rationale** in `CoordinatorSteeringDecider` (policy comment #3 and
   `BuildRationale`) to say DispatchFresh at an assembly gate = conscious lockout rotation to a
   different eligible agent. Thresholds/logic unchanged.

## 4. Must-preserve (verified)

- Lockout deadlock (no eligible agent) → override to Proceed + escalate to human review (never terminal).
- Target-only lockout (per-subtask `TryRotateSubtaskAuthorAsync`, never whole-roster).
- In-place preserves accumulated feedback + resumes same child run/session.
- Proceed → human review on budget exhaustion (Fix-B).
- Per-subtask (`RecoveryAttempts < 3`) and per-plan (`SteeringIterations < 6`) budgets still route
  exhaustion to Proceed→human, not another in-place loop.

## 5. IsReviewerRejection disposition

The only production caller was the deleted override → the helper is production-dead. Removed the helper
and its unit test (`IsReviewerRejection_Discriminates_...`).

## 6. Tests

Updated the existing lockout tests (`DispatchFresh_CarriesAccumulatedFeedbackAndPriorWork`,
`Lockout_DispatchesToDifferentAgentViaContextCarryingHandoff`, `Lockout_TwoRotations_...`) to lapse
`SteeringRetentionUntil` so the decider judges the target UNRESUMABLE → DispatchFresh → lockout (keeping
the ChildRunId as the handoff source). Added:

- `RouteAssembly_Rejection_ResumableTarget_SteersInPlace_SameAuthor_NoLockoutRosterMutation`
- `DriveOutstanding_DispatchFreshExecuting_CrashBeforeEffect_ReDrivesRotation_NotSilentlyApplied`
- `RouteAssembly_Rejection_RotatesOnlyTargetSubtask_NeverWholeRoster`

Budget-exhausted→human review is already covered by `RouteAssembly_BudgetExhausted_EscalatesToHumanReview_NotTerminal`.

## 7. Validate

- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release --no-restore` → 0 errors / 0 warnings.
- `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --filter "Steer|Coordinator|Assembly" -c Release`
  → **508 passed / 0 failed / 0 skipped**.

## 8. Files changed

- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` — deleted override, rerouted
  DispatchFresh→lockout, idempotent re-drive of DispatchFresh, handoff idempotency, removed
  `IsReviewerRejection`.
- `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyStore.cs` — `TryRotateSubtaskAuthorAsync` writes
  the `(LastResetDirectiveId, LastResetAttempt)` idempotency stamp atomically with the rotation CAS.
- `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs` — dispatch_fresh comment/rationale alignment.
- `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs` — updated + new tests, removed dead-helper test.

---

## 2026-07-11T00:00:00Z: Trinity UI diagnosis: RAI output + collapsed activity rows

**Source:** `.squad/decisions/inbox/trinity-ui-rai-activity.md`

# Trinity UI diagnosis: RAI output + collapsed activity rows

Date: 2026-07-11T00:00:00Z
Requested by: Ahmed Sabbour

## Issue 1: RAI Reviewer shows raw decomposition JSON

Root cause: the RAI/assembly node is treated as an assembly aggregate and reads the coordinator run stream, not a distinct RAI response stream. In `AgentSessionPanel.tsx`, `selectedRunId` maps assembly aggregates to `coordinatorRunId`, while non-root RAI nodes use `buildTurns(events)`, so coordinator `agent.message` content can be rendered in the RAI panel. The raw decomposition JSON shape is already recognized by `timeline/coordinatorPlanFilter.ts` for other timelines, but that filter was not applied in this session panel path.

Applied frontend fix: import `isSerializedWorkPlan` into `AgentSessionPanel.tsx` and skip serialized work-plan JSON in `buildTurns` before appending agent text. Added a regression test ensuring RAI gate panels do not show coordinator decomposition JSON.

Longer-term proposal: if RAI/Rubberduck have persisted sub-run streams, pass their real child run id to the session panel; otherwise show only the explicit `rai.verdict` card/status fallback for RAI nodes.

## Issue 2: many empty `Activity collapsed · 1 update` rows

Root cause: `buildCoordinatorTurns` emitted every coordinator lifecycle line as a separate `ConversationTurn`. With docked panels defaulting technical details on and activity details collapsed, `ConversationTurnBlock` hid each activity row but still rendered one summary button per turn, producing many visually empty rows.

Applied frontend fix: `buildCoordinatorTurns` now appends coordinator activity rows into a single activity turn and accumulates approvals there, so collapsed technical activity renders as one `Activity collapsed · N updates` control.

Validation: `npm --prefix apps/web test -- --run src/__tests__/AgentSessionPanel.test.tsx` passed, and `npm --prefix apps/web run build` passed.

---


### 2026-07-10T05:55:00-07:00: Autonomous public-API staging validation contract and operating directives (consolidated)
**By:** Ahmed Sabbour, Morpheus
**What:** Validate the full Product Management and Software Development blueprint journey strictly through ordinary authenticated public HTTP APIs, using the existing GitHub OAuth bearer token without exposing it. kubectl and Application Insights are diagnosis-only. External Squad agents use GPT-5.6 Sol; project blueprint, workflow, and outcome-spec generation settings use `gpt-5.6-sol`, while Agentweaver execution-agent model selection remains coordinator-owned. Exercise outcome revision and confirmation, evidence-based human question/review/approval actions, steering, artifact handoffs, quality gates, build/test, dynamically discovered preview lifecycle, and terminalization. Use fresh projects and neutral goals; success requires three consecutive clean runs across at least two goals. Record every defect as a redacted GitHub issue before fixing, establish the earliest causal invariant violation before proposing code, rubber-duck the design, package and deploy the minimal root-cause fix, then restart validation from a fresh project. If broad failure follows periodic resource-group deletion, reprovision and resume from the last public-API checkpoint. Never lose the north star: issue work is bounded and validation resumes immediately toward the complete previewable application journey.
**Why:** This consolidates the user directives and Morpheus's overlapping success-contract decisions into one non-overfit, black-box operating contract. It preserves the public-surface evidence rule, failure taxonomy, hard gates, clean-run threshold, issue-first discipline, deployment loop, model-scope clarifications, recovery guidance, and the requirement not to substitute infrastructure observability or internal topology for customer-visible proof.

### 2026-07-10T05:55:00-07:00: Durable fenced finalizer redesign is mandatory before issue #207 implementation
**By:** Rubber-duck, Seraph; recorded by Scribe
**What:** The first issue #207 design is rejected. Any revision must use a terminal compare-and-swap plus outbox, explicit finalizer eligibility, durable work/attempt state, fencing, typed retries, a fair bounded cluster-wide queue, mandatory remote factories, persisted execution identity, and consistent finalizer ordering. Security controls are mandatory: tenant-scoped capabilities, fail-closed remote execution, bounded recovery, deletion cancellation/idempotency, audit redaction, and mTLS. Tank is locked out from revising #207; Morpheus owns the independent revision.
**Why:** Ralph exposed an OOM/finalizer defect and Tank traced it to 28 unbounded, non-idempotent final Scribes executing in the API. Rubber-duck rejected the initial design as insufficiently durable, and Seraph rated the direction YELLOW pending mandatory fencing, tenancy, recovery, deletion, audit, and transport controls.
