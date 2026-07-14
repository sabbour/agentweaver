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

## 2026-07-12T06:33:29-07:00: Blueprint catalog evaluation recommends a lifecycle DAG and centralized platform gates

**Author:** Morpheus; recorded by Scribe  
**Status:** PROPOSED — evaluation finding, not yet an accepted catalog redesign

**Decision record:** The shipped catalog has moderate-to-high redundancy at the composition layer. Default runs achieve a strong lifecycle because coordinator decomposition patches beyond the selected workflow at runtime; the default blueprint itself does not encode the advertised lifecycle. Proposed direction: a purpose-built product-lifecycle DAG with an eight-role core roster; conditional prototype/product-marketing/security/DevOps casting; one centralized platform policy for build/test, RAI, automated review, human review, merge, and Scribe; explicit PM and AI workflows; and blueprints/groupings derived from canonical team profiles. Preserve live behavior while making ownership deterministic and reducing roster drift.

**References:** `files/eval-shipped-blueprints.md`, `decisions/inbox/morpheus-blueprint-eval.md`

---

## 2026-07-12T06:33:29-07:00: #207 final-Scribe recovery scope remains durable, fenced, bounded, and eligibility-aware

**Authors:** Coordinator, Seraph, Morpheus; consolidated by Scribe  
**Status:** ACCEPTED SCOPE

**Decision record:** #207 is frozen to remote-only final assembly execution; semantic-generation stable work identity; cluster/project bounds before construction; durable fenced attempts; exact current eligibility entry points; bounded retry, cleanup, and visible failure; and stale-effect invalidation through publication. Cancellation, deletion, or loss of eligibility invalidates every queued, claimed, running, retrying, cleanup, and replayed incarnation. Earlier process-local semaphore/concurrency-gate guidance is historical and does not supersede the accepted durable-fencing scope. Broader tenancy, universal event sealing, general revocation, cross-provider equivalence, and universal exactly-once semantics remain linked work unless implementation evidence proves direct coupling. Docs use only terse coordinator-internals/reference coverage.

**Merged inputs:** `issue-207-frozen-scope-2026-07-10T07-08-00-07-00.md`, `Seraph-approve-scope-with-root-cause-coupled-stale-work-i.md`, `morpheus-bound-final-scribe-recovery-with-a-per-process-gat.md`, `link-docs-207-210.md`

---

## 2026-07-12T06:33:29-07:00: #210 AgentHost reaper grace uses shared claim creation time

**Author:** Link; consolidated by Scribe  
**Status:** SHIPPED

**Decision record:** Protect inactive-map AgentHost claims younger than `Sandbox:Kubernetes:AgentHostClaimCreationGraceSeconds` (default 300 seconds), with effective grace floored at `AgentHostReadyTimeoutSeconds + 30 seconds`. Null or unparseable creation timestamps remain reapable. PostgreSQL/shared claim creation time is the cross-replica authority; no lease or ownership subsystem is introduced. Documentation stays in sandbox-pod reference with a short deep-dive cross-reference.

**Merged inputs:** `Link-issue-210-reaper-grace-uses-shared-claim-creation-.md`, `link-docs-207-210.md`

---

## 2026-07-12T06:33:29-07:00: #217 Kubernetes owns sandbox admission, scheduling, queueing, and autoscaling

**Authors:** Squad, Tank, Link; consolidated by Scribe  
**Status:** SHIPPED

**Decision record:** Remove the app-side pre-flight capacity/quota scheduler. Submit SandboxClaims and allow pods to remain Pending while Kubernetes schedules or autoscales. Emit the non-terminal child-run `sandbox.provisioning_pending` heartbeat while unbound; the coordinator stall detector exempts the child only while that heartbeat is latest and clears the exemption on the next real event. Keep `PendingCapacity` surfaces only for historical compatibility. ResourceQuota retains object/storage bounds rather than CPU/memory admission caps. Documentation marks old app-side capacity states as legacy.

**Merged inputs:** `Squad-remove-app-side-pre-flight-capacity-gate-rely-on-k.md`, `Tank-217-removed-app-side-pre-flight-capacity-quota-sch.md`, `Link-docs-sync-for-217-documented-kubernetes-owned-pod-.md`

---

## 2026-07-12T06:33:29-07:00: #218 coordinator ownership uses lease heartbeat, fencing, and a per-project integration-build lock

**Author:** Tank; recorded by Scribe  
**Status:** SHIPPED

**Decision record:** Renew the coordinator lease from an independent scoped heartbeat (default 30 seconds against a 120-second stale TTL), fence only when a reread proves another pod owns the lease, and cancel the per-run loop on true ownership loss. Serialize shared repository integration builds with a database-backed per-project lock, token-fenced release, and bounded acquisition/stale TTLs. Retain existing stale git-lock retry and repair a missing integration ref. Dedicated assembly heartbeat and broader follow-up remain deferred to #219.

**Merged input:** `tank-fix-218-coordinator-double-dispatch-lease-heartbea.md`

---

## 2026-07-12T06:33:29-07:00: #221 per-run AutoApproveTools is propagated through AgentHost configuration

**Author:** Tank; recorded by Scribe  
**Status:** SHIPPED

**Decision record:** Carry `AutoApproveTools` in the warm-pool AgentHost `/configure` contract and seed the pod-local run-options store before setup. Default false when no store is available. Do not propagate unused `Autopilot` data. The non-warm environment-variable launch path is deferred because it requires a new AgentHost option and pod-spec environment variable.

**Merged input:** `Tank-bug-221-propagate-per-run-autoapprovetools-to-agen.md`

---

## 2026-07-12T06:33:29-07:00: #222 staging is scope-independent and scratch is excluded structurally

**Authors:** Scribe and Coordinator; consolidated by Scribe  
**Status:** SHIPPED PRINCIPLE

**Decision record:** Commit capture stages every non-ignored worktree change, independent of scope prose or output-path classifiers. Blank projects seed a baseline `.gitignore`; nested repositories are excluded to prevent invalid gitlinks. Code owns mechanism, invariants, and safety nets; LLMs own fuzzy judgment through structured output, never prose scraping. Agent scratch belongs outside the project worktree so it is never a commit candidate by construction.

**Merged inputs:** `Scribe-staging-is-scope-independent-commit-every-non-igno.md`, `Squad-Coordinator-code-vs-prompt-boundary-code-owns-mechanism-invari.md`, `Squad-Coordinator-agents-get-an-out-of-worktree-scratch-directory-en.md`

---

## 2026-07-12T06:33:29-07:00: #223 reviewer attribution uses structured target files and distinct lockout/re-dispatch sets

**Authors:** Coordinator and Link; consolidated by Scribe  
**Status:** SHIPPED

**Decision record:** Reviewers emit structured target files. One deterministic reverse-map helper derives implicated subtasks with observable broad fallbacks. Lockout applies only to implicated authors; re-dispatch applies to implicated subtasks plus transitive dependents, with dependents re-run without lockout. Human request-changes always resets the autonomous steering budget; `HumanReviewRoundTrips` remains telemetry only and no human round-trip cap is enforced. Code owns graph expansion and fallback behavior; reviewer prose is never parsed for orchestration.

**Merged inputs:** `Squad-Coordinator-223-fix-design-reviewer-emits-structured-targetfil.md`, `Squad-Coordinator-drop-defaultmaxhumanreviewroundtrips-cap-human-req.md`, `link-docs-223-capdrop.md`

---

## 2026-07-12T06:33:29-07:00: #225 decomposition is outcome-complete while remaining lean for simple outcomes

**Authors:** Tank and Dozer; consolidated by Scribe  
**Status:** SHIPPED

**Decision record:** Coordinator decomposition must cover every lifecycle stage and deliverable implied by the desired outcome. The selected workflow is guidance, not a cap; missing earlier stages are added explicitly. Simple outcomes remain lean. Unit proof is anchored on the testable `BuildWorkflowHint` contract rather than a brittle reflection harness over a local prompt string. Documentation replaces the prior “minimum set” framing with outcome completeness.

**Merged inputs:** `Tank-github-225-item-1-unit-level-proof-for-the-decompo.md`, `Dozer-docs-synced-two-internal-behavior-changes-decompos.md`

---

## 2026-07-12T06:33:29-07:00: #231 RAI verdicts use an authoritative machine-readable sentinel

**Authors:** Neo, Smith, Dozer; consolidated by Scribe  
**Status:** SHIPPED; REVIEWED APPROVE-WITH-NITS

**Decision record:** Require the last decision line to be `VERDICT: <GREEN|YELLOW|REVISE|RED>`. When a sentinel exists, prose is never scanned; the last sentinel wins and same-line ambiguity resolves to the most severe token. Without a sentinel, only an unambiguous supported emoji may decide. One unparseable response triggers one bounded re-ask; a second unparseable response fails safe to RED with `unparseable_after_reask`. Sentinel text is excluded from human-facing rationale. Exception-path fail-open behavior remains a noted pre-existing caveat outside the returned-but-unparseable contract.

**Merged inputs:** `Neo-github-231-defect-a-replace-rai-verdict-heuristic-.md`, `Smith-231-rai-sentinel-verdict-fix-approve-with-nits-all.md`, `Dozer-docs-synced-two-internal-behavior-changes-decompos.md`

---

## 2026-07-12T06:33:29-07:00: #233 single-eligible-agent lockout deadlocks degrade to bounded same-author re-dispatch when context exists

**Authors:** Coordinator, Morpheus, Tank, Smith, Trinity; consolidated by Scribe  
**Status:** SHIPPED; REVIEWED APPROVE-WITH-NITS

**Decision record:** Split lockout deadlocks by context. With no context, retain `lockout_no_context` human escalation. With accumulated context and no eligible alternate author, degrade to same-author fresh re-dispatch without mutating lockout state, carrying prior feedback/worktree and re-running dependents without lockout. Mixed directives degrade the full target set without silently dropping work. `RecoveryAttempts` is not reset, so the existing maximum recovery budget bounds repeated revision before human escalation. Reviewer severity calibration remains separate (#225).

**Merged inputs:** `coordinator-233-rubberduck.md`, `tank-233-lockout-degrade.md`, `smith-233-codereview.md`, `trinity-233-docs.md`

---

## 2026-07-12T06:33:29-07:00: PostgreSQL remains the durable run-event log

**Author:** Coordinator; recorded by Scribe  
**Status:** ACCEPTED

**Decision record:** Keep PostgreSQL as the durable, ordered, replayable, transactional source of truth for run events. SignalR/Web PubSub may provide transient push, LISTEN/NOTIFY may reduce polling, and Event Hubs may become justified only at substantially higher scale with an outbox and durable capture. Event Grid and Service Bus do not satisfy the ordered replay contract. Producer flush defects such as #212 must be fixed at the producer rather than replacing the downstream log.

**Merged input:** `Squad-Coordinator-keep-postgresql-as-the-durable-run-event-log-do-no.md`

---

## 2026-07-12T06:33:29-07:00: v0.9.33 reviewer fidelity and worktree provisioning use direct integration-state materialization

**Authors:** Tank and Link; consolidated by Scribe  
**Status:** SHIPPED DESIGN, TESTED

**Decision record:** Provision subtask worktrees in one git-CLI step directly from the resolved integration commit, avoiding an intermediate primary-HEAD checkout and preserving case-insensitive branch resolution. Assembly RAI and rubber-duck reviewers receive a shared detached reviewer worktree containing assembled integration files, created only when changes exist and recreated before build/test to avoid mutation bleed. Existing cleanup owns the shared reviewer/build-test worktree lifecycle.

**Merged inputs:** `tank-v0933-worktree-reviewer-fidelity.md`, `link-v0933-docs.md`

---

## 2026-07-12T06:33:29-07:00: Mid-run coordinator steering queue-to-drain wiring is verified

**Author:** Trinity; recorded by Scribe  
**Status:** VERIFIED; NO BUG FILED

**Decision record:** Mid-run redirect/amend/send directives are durably queued, atomically drained at the next child boundary, and relayed into child revision handling. Deterministic tests now exercise the live dispatch-loop queue-to-drain seam. Full relayed-to-applied execution remains a heavier live-agent integration concern, but the #226-style silent-queue void is not present in the mid-run path.

**Merged input:** `Trinity-mid-run-coordinator-steering-works-queue-drain-act.md`

---

## 2026-07-12T06:33:29-07:00: #196 documentation stays on existing approval and sandbox execution surfaces

**Author:** Link; recorded by Scribe  
**Status:** DOCUMENTATION SCOPE

**Decision record:** #196 is a backend reliability correction, not a new discoverable feature. Document the public run approval/denial endpoints and the internal AgentHost return routes on existing approval, API, and sandbox-pod pages; add only the architectural sequence diagram needed to explain the missing return leg. Do not add feature pages, landing cards, navigation, screenshots, or experience surfaces.

**Merged input:** `link-196-docs.md`

# Link — #253 Seraph revision

- Merged `main` at `329b5397` into `squad/253-impl-writeback` and resolved shared runtime conflicts by union: #254 worker A2A deadlines, per-iteration linked-CTS timer cleanup, and structured failure propagation remain alongside #253 descriptor decoding and write-back application.
- LocalWritable implementation turns now require an explicit publication envelope. Missing descriptors fail as `writeback_missing`; malformed, empty, duplicate, or structurally invalid descriptors fail as `writeback_invalid`. Neither path can fall through to committing the unchanged shared worktree.
- Nested repositories are flattened into the parent result tree by temporarily moving their `.git` metadata outside the checkout, staging their contents, then restoring metadata. Failure to flatten safely is emitted as structured `writeback_nested_repository_failed` evidence over A2A.
- Added coverage for absent and malformed envelopes, nested-only deliverables, mixed top-level+nested deliverables, and retained the descriptor-present happy path.
- Validation: Release build succeeded with 0 warnings / 0 errors; write-back filter passed 79/79; #254 resiliency filter passed 33/33.
- Commit: `f0586676b0f2cd42c0afd969324989eb553aac5b`.
- No push, deployment, or merge to main was performed.

# Issue #254 worker A2A idle and timer lifetime

- **Author:** Link
- **Date:** 2026-07-12
- **Branch:** `squad/254-resiliency`

## Decision

Use the worker read-idle deadline as a transport-death backstop, not as the primary agent-turn watchdog. Its default is derived from the authoritative in-pod `CopilotAIAgent.DefaultStreamIdleTimeout` (15 minutes) plus a 5-minute safety margin, yielding 20 minutes. This is strictly looser than the pod's shell-aware idle watchdog, allowing the pod time to emit its structured timeout before the worker concludes the A2A transport is dead. `apps/Agentweaver.Api/appsettings.json` is aligned to 20 minutes; the 70-minute worker total deadline remains unchanged.

For timer lifetime, each `MoveNextAsync` race owns a linked per-iteration cancellation token source. After `Task.WhenAny` chooses progress, idle, or total timeout, that source is cancelled and disposed, both deadline tasks are observed, and expected cancellation is swallowed. This immediately tears down losing `Task.Delay` timers instead of accumulating up to 70-minute timers for every streamed update.

## Validation contract

Tests prove: the default worker idle is strictly greater than the in-pod idle; quiet gaps longer than the former scaled idle window succeed and reset the idle clock; a blackholed stream still throws `a2a_stream_idle_timeout`; and all per-iteration deadline tasks are cancelled after rapid progress.

# Issue 257 planning-doc routing revision

- Planning classification is deterministic as well as prompt-driven: recognized research, market-analysis, business-plan, user-story, PRD/product-requirements, design-spec, and requirements-document terms force `phase: planning` for both model and fallback decomposition.
- Planning Markdown output paths are canonicalized in `Subtask.Scope` before persistence, prefixing recognized bare deliverable filenames with `docs/planning/` while leaving already-qualified paths unchanged. This makes output-conflict serialization operate on the real destination path.
- Dispatch instructions remain defensive: agents are explicitly told not to double-prefix existing `docs/planning/...` paths.

# Link — Issue #257 revision 5

## Decision
Scope planning-path canonicalization per Markdown occurrence to the nearest preceding file-intent verb. Output intents are `write`, `draft`, `create`, `produce`, `author`, `prepare`, `revise`, `update`, `save`, `emit`, `generate`, `publish`, and `into`; input/reference intents are `read`, `review`, `inspect`, `consult`, `use`, `using`, `from`, and `based on`. Only occurrences classified as declared outputs are rewritten under `docs/planning/`; input/reference paths remain unchanged.

## Regression coverage
Added `Read README.md, then write launch-marketing.md.` coverage. It asserts `README.md` remains root-relative and only `launch-marketing.md` becomes `docs/planning/launch-marketing.md`, including dispatched task text.

## Validation stabilization
The required second suite run exposed the real accepted-review race enabled by the existing separate-connection SQLite fixture: the assembly loop can consume a locally accepted decision and clear its durable gate before the best-effort persistence update commits. `PersistDecisionAsync` now treats a concurrency exception as success only when the gate row is already absent; it still rethrows if the row remains. The two affected tests then passed five consecutive probes.

## Final validation
- Deleted `%TEMP%\memory.db*` before the final build.
- `dotnet build Agentweaver.sln -c Release`: succeeded, 0 warnings, 0 errors.
- Required targeted suite final pass 1: 645 passed, 0 failed, 0 skipped.
- Required targeted suite final pass 2: 645 passed, 0 failed, 0 skipped.
- `git diff --check`: clean.
- No commit created.

### 2026-07-13: Defer Copilot SDK upgrade; ship Reject decisions only
**By:** Link
**Issue:** #264

NuGet has no `Microsoft.Agents.AI.GitHub.Copilot` release newer than `1.13.0-rc1`. A clean build with the current adapter (`1.11.1-rc1`) and an explicit unified `GitHub.Copilot.SDK` `1.0.6` reference still fails with CS0012 because the adapter requires the unsigned `GitHub.Copilot.SDK, Version=1.0.0.0, PublicKeyToken=null` identity. The newest available combination was also tested (`Microsoft.Agents.AI.GitHub.Copilot` `1.13.0-rc1` plus `GitHub.Copilot.SDK` `1.0.7-preview.2`, with its required Microsoft.Extensions abstractions at `10.0.9`) and fails with the same CS0012 errors.

Do not vendor or source-compile the MAF adapter. Keep the repository's working `GitHub.Copilot.SDK` `1.0.2` and the AgentHost CLI pin unchanged in issue #264. Ship only the permission-handler correction: governance, policy, fail-closed, and operator denials return `PermissionDecision.Reject(feedback)`.

SDK/CLI alignment (`GitHub.Copilot.SDK` `1.0.2` versus CLI `1.0.67` in AgentHost) remains a separate, lower-urgency follow-up. Revisit it when Microsoft publishes `Microsoft.Agents.AI.GitHub.Copilot` against a newer, strong-named `GitHub.Copilot.SDK` compatible with CLI `1.0.67` or later.

### 2026-07-13T17-38-13: Frontend AKS/release image builds now use local Docker Buildx with BuildKit npm secret mounts; API, MCP, and AgentHost stay on az acr build.
**By:** link
**What:** Frontend AKS/release image builds now use local Docker Buildx with BuildKit npm secret mounts; API, MCP, and AgentHost stay on az acr build.
**References:** issue #265, commit 6d919484, apps/web/Dockerfile, scripts/aks/20-build-push-images.sh, scripts/release.sh, docs/guide/deployment-aks.md
**Why:** Issue #265 follow-up: restored apps/web/Dockerfile to the pre-523c18b7 multi-stage design with a real BuildKit secret mount (`RUN --mount=type=secret,id=npmrc,target=/root/.npmrc,required=false`) and changed both scripts/aks/20-build-push-images.sh and scripts/release.sh so only `agentweaver-frontend` builds via `docker buildx build --platform linux/amd64 --secret id=npmrc,src=... --push` after `az acr login`. Azure Artifacts credentials are still resolved through artifacts-npm-credprovider / ado-npm-auth, but the scripts now extract only the feed auth lines into a transient local secret file instead of prebuilding dist outside Docker. This keeps secrets out of image layers/history while preserving ACR remote builds for api/mcp/agent-host.

### 2026-07-13T17-10-37: Use ACR secret build args instead of BuildKit mounts for frontend npm auth
**By:** link
**What:** Use ACR secret build args instead of BuildKit mounts for frontend npm auth
**References:** issue #265, commit 993520cf, apps/web/Dockerfile, scripts/aks/20-build-push-images.sh, docs/guide/deployment-aks.md
**Why:** Issue #265 showed that ACR Tasks ignores BuildKit-only `RUN --mount=type=secret` even with the Dockerfile syntax directive. I changed `apps/web/Dockerfile` to accept `AZURE_ARTIFACTS_NPM_PASSWORD_B64` as a build arg, append Azure Artifacts auth lines to a temporary `.npmrc.build` only for `npm ci`, and delete that file in the same `RUN` step so the token is not retained in any committed layer. I also updated `scripts/aks/20-build-push-images.sh` to pass the credential with `az acr build --secret-build-arg`, preferring an explicit `AZURE_ARTIFACTS_NPM_PASSWORD_B64` override and otherwise extracting the feed password from the caller's `~/.npmrc` after `vsts-npm-auth`. Commit: `993520cf`.

### 2026-07-13T00-27-14: Use PID/process-tree socket inode attribution for preview ports
**By:** Link
**What:** Use PID/process-tree socket inode attribution for preview ports
**References:** GitHub issue #258, apps/Agentweaver.AgentHost/PreviewRunner.cs, packages/Agentweaver.SandboxFs/SandboxPolicyBackend.cs, tests/Agentweaver.Tests/SandboxPolicyBackendTests.cs
**Why:** For issue #258 rev3, preview port discovery no longer compares pod-wide before/after port sets. PreviewRunner traverses the supervised root PID and descendants through /proc/<pid>/task/<pid>/children, collects socket inodes from each /proc/<pid>/fd symlink, and cross-references only those inodes against LISTEN entries in /proc/net/tcp and /proc/net/tcp6. Log-reported ports are accepted only when present in that owned set. Health checks resolve either the observed app port or its session-specific forwarder back to the app port and revalidate current process-tree ownership before probing, preventing stale or unrelated port reuse. SandboxPathValidator cwd containment and the 120-second timeout cap remain intact.

### 2026-07-14T06-57-14: v0.9.46-rc1 BookClub regression is blocked; no working preview
**By:** Link
**What:** v0.9.46-rc1 BookClub regression is blocked; no working preview
**References:** run:f498e1bb-5614-4b95-b3b1-98e7b318bf75, issue:269, project:b1122801-42b6-479e-9cb9-6283377e7e49
**Why:** Verified /api/version = v0.9.46-rc1 at 2026-07-13 23:38 PDT. Existing FitTrackE2E/BookClubE2E/TrailMixE2E runs predate the 23:11 PDT v0.9.46 deployment, so none qualified as a run on this version. Launched BookClub regression coordinator run f498e1bb-5614-4b95-b3b1-98e7b318bf75 at 2026-07-13 23:41:00 PDT (project BookClubE2E-v9) with direct/autopilot/auto-approve. It reached assembly, then preview start failed at 23:52:49 PDT: Node exited 1 because server.js could not resolve module 'express'. The coordinator became assembly_blocked at 23:53 PDT after recovery spawned child runs 542ec561-b6c9-4513-a9ad-c9b8c46aa58a and 2aed3f8a-e6b5-4eef-b39f-2ffafc57b45d, both immediately agenthost_launch_failed. No live preview URL was produced. Cross-checked the initially bound run pod agentweaver-agent-host-6lrjh before cleanup: the coordinator's run_command calls were policy-allowed and no bwrap-missing error was logged. The pod was deleted before post-failure retrieval. This does not reproduce known issue #269; it exposes preview dependency installation / agenthost launch recovery failures instead.

# Issue #252 revision — Seraph blocking fixes

Owner: Morpheus (Tank author-lockout observed)
Date: 2026-07-12
Branch: `squad/252-buildtest-pod-local`

## Blocking #1 — controlled package-cache access

- Relocated npm/yarn/pnpm/XDG caches beneath each verified pod-local checkout at `.agentweaver-cache/` and configured portable checkout-relative environment paths.
- Kept the checkout as `SandboxToolContext.SandboxRoot`, so `RunCommandTool`/`SandboxFsPolicyBuilder` grant the cache through the sole read-write root.
- Added the targeted `/usr/share/nodejs` read-only bubblewrap mount needed for distro npm installations.
- Added a real controlled Linux executor regression test that runs `npm install`, verifies the cache is writable, and records/asserts the filesystem policy. It runs through WSL bubblewrap on the Ahmed-verified Windows environment and native bubblewrap on Linux.

## Blocking #2 — effective preview workspace

- `KubernetesSandboxExecutor` now reads the successful AgentHost `/configure` response and extracts `effectiveWorkingDirectory`.
- `PodNameRegistry` stores and clears the run-scoped effective working directory; compatibility lifecycle implementations inherit no-op/default-null methods.
- `CoordinatorAssemblyService` and `PreviewStep` consume the reported path. When no provider reported one, preview uses the shared source working directory instead of synthesizing `/local-workspace/...`.
- Added configure-response persistence, effective-path preview mapping, and compatibility fallback preview tests.

## Verification

- `dotnet build Agentweaver.sln -c Release`: succeeded, 0 warnings / 0 errors.
- Required targeted tests: 1038 passed, 0 failed, 0 skipped.
- New blocker regression tests: 4 passed, including the real-executor npm cache test and both effective-path/fallback preview tests.
- No push, deployment, issue closure, #253 write-back, or #254 heartbeat work performed.

# Morpheus decision: #252 pod-local scratch

- **Author:** Morpheus (Lead/Architect)
- **Decision:** Keep the merged #252 full-checkout model on disk-backed pod-local `execution-scratch` (`emptyDir`, 1 GiB request, 8 GiB container limit and volume sizeLimit). Assembly `LocalReadOnly` never syncs back; scratch is discarded.
- **Write-back:** #253 should use `LocalWritable` and publish a local commit through git to `agentweaver/<childRunId>`, never recursively copy files or `node_modules` to Azure Files.
- **Simplification:** File a new follow-up issue to replace npm/Yarn/pnpm-specific cache env plumbing with one sandbox-visible scratch-local home/cache root. Keep targeted bwrap Node runtime mounts.
- **References:** #252, #253; `design-252-scratch.md` in the requesting session artifacts.

# #253 revision 3 — nested repository write-back

**Author:** Morpheus (Lead/Architect)  
**Date:** 2026-07-12  
**Commit:** `36df1bc6be35b8fb76a43a1920b798efa139e04a`

## Decision

Nested repositories are now discovered by walking the pod-local checkout filesystem for `.git` directory/file roots, independent of the staged parent diff. Roots are ordered deepest-first. All discovered metadata is moved aside deepest-first, parent-index gitlinks are removed, and repository contents are staged bottom-up before metadata is restored.

The generated tree is inspected for mode `160000`; any residual gitlink fails write-back with structured reason `writeback_invalid` rather than publishing a partial tree.

## Coverage and verification

Added integration coverage for a dirty existing submodule whose HEAD is unchanged and for recursively nested repositories. Existing nested-only and mixed top-level/nested cases remain covered.

- Release build: succeeded, 0 warnings / 0 errors.
- Write-back targeted filter: 81 passed, 0 failed.
- Resiliency filter (`RemoteAgentProxy|AsyncStreamIdleTimeout|A2A`): 33 passed, 0 failed.

No push, deploy, or merge was performed.

# Issue #254 — turn resiliency implementation

Implemented on `squad/254-resiliency` in `C:\Users\asabbour\Git\agentweaver-254`.

## Changes
- Reused and extended `ShellExecutionTracker` with observable SDK-shell lifecycle, keyed by tool call ID, from approved `PermissionRequestShell` through matching `ToolExecutionCompleteEvent`.
- Added `tool.execution_pending` heartbeats every 25 seconds while a shell is active. These flow through the existing RunEvent/A2A channel and reset coordinator stall observation.
- Replaced the blunt stream-idle watchdog with a tool-aware watchdog: configurable 15-minute no-tool idle timeout, configurable 30-minute active-shell hard deadline, and configurable 60-minute total-turn deadline.
- On shell hard timeout, force-stops the Copilot CLI process tree via `CopilotClient.ForceStopAsync`, emits structured `run.failed` with `errorCode=shell_execution_timeout`, and returns typed failure.
- Set streaming A2A `HttpClient.Timeout` to `Timeout.InfiniteTimeSpan`.
- `RemoteAgentProxy` now retains the last structured `run.failed` and uses its error code, message, and retryability if the A2A stream faults.
- Generalized `AgentTurnOutput` / `ChildTurnFailedOutput` failure fields and propagated structured reasons through `AgentTurnExecutor`, child graph routing, and `RunWatchLoopService`.

## Verification
- Confirmed cited real seams before editing: coordinator per-event stall timer, Copilot stream watchdog, A2A named client, A2A RunEvent bridge, proxy fault wrapping, child executor failure handling, and watcher terminalization.
- Release build: succeeded, 0 warnings / 0 errors.
- Mandatory targeted tests: 994 passed, 21 skipped, 0 failed (1015 total).
- Added 9 resiliency tests covering shell lifecycle, watchdog mode selection, heartbeats/stall reset, hard-timeout termination + structured failure, infinite A2A transport timeout, proxy evidence retention, executor propagation, and watcher preservation.

Commit: `f15429c4edf882fdcf2c8cca5363a61e83e6da96`

No push, deploy, issue close, branch switch, or agent-host image rebuild performed.

# Issue #257 rev6 — structured declared output paths

## Decision
Use the structural-field approach, not another prose heuristic.

The decomposition JSON schema now requires `declared_output_paths`, an outputs-only array authored by the coordinator in the same LLM planning response as title/scope/phase. `SubtaskDraft` carries the array and `Subtask` persists it as `DeclaredOutputPathsJson` (with SQLite and PostgreSQL migrations).

For planning subtasks, only entries in that structured array are canonicalized: bare Markdown filenames become `docs/planning/<filename>`; already-qualified paths are preserved. Scope prose is never scanned or rewritten. Dispatch renders the authoritative output list separately and explicitly treats every other prose path as an input/reference. Output-conflict serialization also consumes only this structured metadata.

This directly avoids both reviewer failure classes: `Write launch-marketing.md and refer to README.md` leaves `README.md` untouched, while label-only prose such as `Deliverable: launch-marketing.md` and `Output: PRD.md` works because output identity comes from the structured array rather than a nearby verb.

## Validation
- Deleted `%TEMP%\memory.db*` before Release build.
- `dotnet build Agentweaver.sln -c Release`: succeeded, 0 warnings, 0 errors.
- Targeted run 1: 648 passed, 0 failed, 0 skipped (1m55s).
- Targeted run 2: 648 passed, 0 failed, 0 skipped (1m54s).
- Both SQLite and PostgreSQL EF migration assemblies discover `20260713020000_AddSubtaskDeclaredOutputPaths`.

No commit was created.

# Issue #263 design review — preview origin lookup timeout must not fail assembly

**Author:** Morpheus  
**Issue:** `sabbour/agentweaver#263`  
**Verdict:** Approve the two-layer fix, with cancellation classification enforced at the preview boundary and a narrowly bounded/retried pod read. Do not limit the fix to only the observed call site.

## Findings

### Confirmed failure chain

`KubernetesAgentHostOriginResolver.TryResolveOriginAsync` passes the assembly/run token directly to `ReadNamespacedPodAsync` (`apps/Agentweaver.Api/Sandbox/IAgentHostOriginResolver.cs:60-63`). The resolver excludes every `OperationCanceledException` from its normal degradation path (`:76`), so the Kubernetes client's approximately 100-second `HttpClient.Timeout` escapes. `PreviewRunnerHttpClient.ResolveOriginOrThrowAsync` does not classify it (`apps/Agentweaver.Api/Sandbox/Preview/PreviewRunnerHttpClient.cs:131-138`). `PreviewStep` unconditionally rethrows all cancellation (`apps/Agentweaver.Api/Coordinator/Preview/PreviewStep.cs:222-226`), and the preview-specific caller does the same (`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1017-1022`). The outer assembly boundary correctly distinguishes caller cancellation with `when (ct.IsCancellationRequested)` (`CoordinatorAssemblyService.cs:783-788`), but by then an unrelated timeout is handled as an unexpected assembly error and terminalized.

### Is `ct.IsCancellationRequested` sufficient?

Yes, for distinguishing the caller/run token from an internal HTTP timeout. Today `ct` is passed directly into `ReadNamespacedPodAsync`. `HttpClient`/the Kubernetes client may create an internal linked timeout CTS, but cancellation of a linked **child** token does not propagate backward and cancel the supplied parent token. Therefore an internal timeout can throw `TaskCanceledException` while `ct.IsCancellationRequested == false`.

For the new per-attempt timeout, create a linked CTS from `ct`, call `CancelAfter`, and pass the linked token to the pod read. When that child timeout fires, the original `ct` remains false. If the run/app token fires, both are canceled and the original `ct` is true.

There is a small race if shutdown arrives immediately after a catch filter observes `ct == false`. Before retrying or emitting `preview_failed`, call `ct.ThrowIfCancellationRequested()`. Backoff must use `Task.Delay(delay, ct)`. This makes real shutdown win and prevents stale retries/events.

## Scope recommendation

Do **not** fix only the origin lookup and leave the unconditional cancellation policy unchanged. The same class of bug exists elsewhere:

- `PreviewRunnerHttpClient.SendAsync` deliberately excludes `OperationCanceledException` from typed transport errors (`PreviewRunnerHttpClient.cs:151-159`). The named `a2a-sandbox-pod` client has a finite default/configured timeout (two minutes in `Program.cs:421-438`), so start/observe/stop calls can also throw an internal `TaskCanceledException`.
- `PreviewStep.RunAsync` also awaits secret-store lookup, approval, registration, and cleanup. Some components already use the correct conditional pattern, but the step's contract boundary should not assume every dependency's `OperationCanceledException` means run shutdown.

Use layered protection:

1. **Resolver-level resilience:** bound and retry the idempotent pod GET.
2. **PreviewRunnerHttpClient classification:** convert non-caller origin/runner timeouts into the existing typed preview exception.
3. **PreviewStep contract boundary:** rethrow only when its own `ct` is canceled; degrade any remaining unrelated cancellation to one terminal `preview_failed`.
4. **Coordinator defensive boundary:** change the preview-specific catch to rethrow only when `ct.IsCancellationRequested`; otherwise log and proceed. The outer assembly catch already uses the correct filter.

This is broader than the single stack trace but remains scoped to the deterministic preview path and its HTTP client; do not globally change cancellation handling throughout assembly.

## Concrete implementation plan

### 1. Bound and retry `ReadNamespacedPodAsync`

In `KubernetesAgentHostOriginResolver`:

- Use **3 total attempts** (initial + 2 retries), matching `KubernetesSandboxExecutor.MaxK8sAttempts` from issue #230 (`KubernetesSandboxExecutor.cs:158-165`, `:693-710`).
- Use a **5-second per-attempt timeout** via `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter`. A pod GET is a small, in-cluster control-plane read; 5 seconds also matches `HttpAgentHostReadinessProbe.AttemptTimeout` (`AgentHostReadinessProbe.cs:31-33`). Three attempts bound the normal worst case to roughly 16 seconds rather than 100 seconds.
- Retry only transient faults: 429/5xx, `HttpRequestException`, socket/IO failures, and `OperationCanceledException` when the original `ct` is not canceled. Do not retry 4xx such as 403/404.
- Use exponential backoff with jitter, echoing #230: approximately 250 ms then 500 ms plus 0-250 ms jitter. Fixed delay is less desirable during API-server contention because replicas synchronize.
- Use `ct` for backoff and check `ct.ThrowIfCancellationRequested()` before classifying an attempt as transient.
- After the final internal timeout, allow the non-caller `OperationCanceledException` to reach `ResolveOriginOrThrowAsync` for typed classification. Preserve the current null return for ordinary no-pod/no-IP/non-timeout resolution failures.

Prefer a per-operation linked timeout over mutating a shared Kubernetes client's global `HttpClient.Timeout`: it is explicitly per attempt, composes correctly with caller cancellation, and is deterministic in tests.

### 2. Reuse `PreviewRunnerHttpException`; add reasons, not a subtype

No new exception subtype is needed. Extend the existing reason vocabulary:

- `preview_origin_lookup_timeout` for exhausted Kubernetes pod-origin lookup timeout.
- `preview_runner_timeout` for a non-caller timeout in the AgentHost preview-runner HTTP call.

In `ResolveOriginOrThrowAsync`, catch `OperationCanceledException` only when the supplied caller token is not canceled and throw `PreviewRunnerHttpException("preview_origin_lookup_timeout", ...)`. Preserve true caller cancellation.

In `SendAsync`, use the same pattern: true caller cancellation rethrows; an internal client timeout becomes `PreviewRunnerHttpException("preview_runner_timeout", ...)`. This routes start failures through the existing start failure handling and observe failures through the existing observe handling, including best-effort process stop after a session has started. That is safer than relying only on the top-level catch, which otherwise could emit failure without releasing a known running session.

In `PreviewStep`'s start catch, preserve `preview_origin_lookup_timeout` as the emitted reason rather than collapsing it to `process_exited`. Other start transport failures may retain existing behavior. The distinct reason is operationally useful and requested by the issue.

### 3. Correct cancellation boundaries

In `PreviewStep.RunAsync`:

- Replace unconditional cancellation rethrow with `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`.
- Add a following non-caller cancellation catch that first calls `ct.ThrowIfCancellationRequested()`, logs, emits a single `preview_failed` (fallback reason `preview_internal_timeout`), and returns.
- Keep stage-specific typed catches so post-start failures still stop the process.

In the preview block in `CoordinatorAssemblyService.RunAssemblyCoreAsync`:

- Change its unconditional cancellation catch to the same `when (ct.IsCancellationRequested)` filter.
- Let a non-caller cancellation fall into the existing defensive `catch (Exception)` so review continues. This is defense-in-depth; normally `PreviewStep` will already have emitted a failure.

### 4. Regression tests

There is currently no direct test class for `KubernetesAgentHostOriginResolver`. Existing coverage includes preview paths and #230 retry behavior, but not cancellation classification:

- `tests/Agentweaver.Tests/Preview/PreviewRunnerHttpClientTests.cs` covers URLs, bearer, parsing, and 401 only.
- `tests/Agentweaver.Tests/Preview/PreviewStepTests.cs` covers typed runner failures, cleanup, and terminal emission, but no `OperationCanceledException` cases.
- `tests/Agentweaver.Tests/KubernetesSandboxExecutorClaimTests.cs:483-588` is the pattern to echo: transient retry, bounded attempts, and prompt caller cancellation.

Add tests for:

1. Resolver retries two internal timeouts/transient faults and succeeds on attempt 3.
2. Resolver exhausts 3 internal timeouts within a bounded test-controlled interval and does not cancel the caller token.
3. Resolver with a pre-canceled/run-canceled token throws promptly and performs no retry/backoff.
4. `PreviewRunnerHttpClient` maps origin internal cancellation to `preview_origin_lookup_timeout`, while propagating cancellation when the supplied token is canceled.
5. Runner `SendAsync` maps its own timeout to `preview_runner_timeout`, while preserving caller cancellation.
6. `PreviewStep` given an unrelated `TaskCanceledException` emits exactly one `SandboxPreviewFailed` and completes normally.
7. `PreviewStep` given a canceled supplied token rethrows and emits no misleading terminal preview failure.
8. An observe-stage timeout after process start performs one best-effort stop and emits one failure.
9. Coordinator-level coverage, if its harness permits, proves a leaked non-caller cancellation from the preview step does not produce `assembly_failed`, while real run cancellation still propagates.

Inject timeout/backoff durations or expose internal constants for tests; do not make unit tests sleep for real five-second windows.

## Regression-risk assessment

The principal risk is swallowing real shutdown and continuing retries. The linked timeout design plus checks against the **original** `ct`, cancellation-aware backoff, and conditional catches prevent that. When app/run cancellation is active, it propagates immediately through resolver, client, preview step, and coordinator.

A secondary risk is leaking a process when a timeout occurs after start. Mapping runner timeouts to `PreviewRunnerHttpException` keeps the existing observe-stage cleanup path. The top-level non-caller cancellation catch is only the final contract safety net.

A third risk is retrying non-idempotent operations. Retry only the Kubernetes pod GET; do not add retries around preview-runner process start or registration.

## Assignment

**Recommend Link (Platform Engineer)** to implement next. The change is centered on Kubernetes transport timeouts/retries, linked cancellation semantics, and in-cluster control-plane behavior. Smith should review the cancellation/retry regression tests, but Link is the best primary owner.

### 2026-07-13T18-02-59: Bypass broken Linux ado-npm-auth fallback in frontend image builds and fail fast on sibling image-job failure.
**By:** morpheus
**What:** Bypass broken Linux ado-npm-auth fallback in frontend image builds and fail fast on sibling image-job failure.
**References:** issue #265, commit a1ad5c4b, scripts/aks/20-build-push-images.sh, scripts/release.sh
**Why:** Diagnosed the frontend auth regression as an upstream ado-npm-auth packaging bug rather than a host-architecture mismatch: ado-npm-auth 0.11.0 bundles artifacts-credprovider v1.4.1 but constructs RID-specific Linux asset URLs like Microsoft.Net8.<rid>.NuGet.CredentialProvider.tar.gz, which GitHub serves as a non-gzip error page for that release line. To keep the approved prebuild-outside-Docker security model intact, the build/release scripts now prefer a temporary host-side .npmrc.build generated from AZURE_ARTIFACTS_NPM_PAT or AZURE_ARTIFACTS_NPM_PASSWORD_B64 (or an existing authenticated ~/.npmrc), and only attempt interactive helper auth on supported non-Linux hosts. On Linux/WSL the scripts now fail fast with an actionable explanation instead of proceeding into the opaque gzip/auth failure. Separately, the image-job wait loop now polls for completed children and terminates remaining siblings on first failure so one broken build does not leave the parent script apparently hung while waiting on unrelated jobs.

### 2026-07-12T21-08-36: Collapse pod-local package caches into one sandbox-local HOME/XDG root
**By:** Morpheus
**What:** Collapse pod-local package caches into one sandbox-local HOME/XDG root
**References:** #255, #252, apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs, packages/Agentweaver.SandboxExec/WslMxcSandboxExecutor.cs
**Why:** Issue #255 removes the package-manager-specific environment matrix from PodLocalWorkspaceManager: npm_config_cache, YARN_CACHE_FOLDER, PNPM_HOME, PNPM_STORE_DIR, and npm_config_store_dir are no longer configured; the previous cache-only XDG_CACHE_HOME value is replaced. The manager now creates one ignored `.agentweaver-home` directory inside the pod-local emptyDir-backed checkout and sets HOME=.agentweaver-home, XDG_CACHE_HOME=.agentweaver-home/.cache, XDG_DATA_HOME=.agentweaver-home/.local/share, and XDG_CONFIG_HOME=.agentweaver-home/.config. PreparedWorkspace.CacheRoot is removed. Existing Node runtime read-only mounts remain intact, and WSL bubblewrap explicitly preserves the derived sandbox HOME because WSL login startup rewrites HOME.

### 2026-07-14T06-56-16: Final #269 decision: AgentHost uses conditional direct execution inside Kata
**By:** Morpheus
**What:** Final #269 decision: AgentHost uses conditional direct execution inside Kata
**References:** Ahmed directive: "drop to passthrough sandbox when running in kata.", GitHub issue #269, GitHub issue #252, apps/Agentweaver.AgentHost/Program.cs, apps/Agentweaver.AgentHost/AgentHostOptions.cs, k8s/sandbox-template-agenthost.yaml, packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs:84, docs/deep-dive/sandbox-pod-execution.md
**Why:** Supersedes decisions 68512cd4-629f-4e4a-80cf-c64a72f1f60a and 42ee0297-0b01-44ee-b0b0-d8ab9e059c1b. Ahmed Sabbour explicitly overrode the seccomp-profile alternative with: “drop to passthrough sandbox when running in kata.”

Accepted implementation: keep the shared SandboxExecutorFactory and bubblewrap installation unchanged for API-worker, native Linux, and local development. In `apps/Agentweaver.AgentHost/Program.cs`, immediately after `AddAgentRuntime()`, add a last-registration-wins AgentHost-only `ISandboxExecutor` override to `PassthroughExecutor` only when both `SandboxExecutorFactory.IsInCluster` is true and `AgentHost:SandboxMode` equals `kata`. The Kata SandboxTemplate sets `AgentHost__SandboxMode=kata`. This prevents nested bwrap inside the already-isolated per-run Kata VM and leaves every non-Kata executor-selection path untouched.

Security tradeoff explicitly accepted: direct execution removes bwrap’s PID namespace and minimal bind-mount filesystem view. `RunCommandTool` currently constructs `SandboxCommand` with `Environment: null` (`packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs:84`), so it does not explicitly inject credentials into the command environment. `PassthroughExecutor` still launches the child with inherited process environment, and arbitrary same-UID commands may inspect in-pod `/proc` metadata, AgentHost/Copilot process environments, or projected workload-identity credentials. The team accepts this residual risk under Ahmed’s directive because the disposable per-run Kata VM is the primary isolation boundary. This risk is documented in `docs/deep-dive/sandbox-pod-execution.md` and must not be represented as equivalent to bwrap’s in-VM containment.

Validation: AgentHost Release build succeeds with 0 warnings/errors. Staging validation should deploy the updated AgentHost image/template, launch a fresh `software-delivery` run in the `validate-256-269` project (the prior reproduction was run `5a8c3004-e7b6-41ed-8a65-b671816b8dab`), and inspect the `planned:assembly-build-test` / `qa-engineer` gate. Its controlled `run_command` must successfully execute `echo hello` and the repository build/tests with no `bwrap not installed` or `Can't mount proc` output. Confirm the selected executor reports backend `direct` with the Kata reason, while API-worker/non-AgentHost execution continues selecting its existing backend.

### 2026-07-14T07-08-26: Fix #305: in-place steering revision children now get their own authoritative worktree branch
**By:** Morpheus
**What:** Fix #305: in-place steering revision children now get their own authoritative worktree branch
**References:** #305, #290, #291
**Why:** ## Issue #305 — revision children retained prior worktree branch, failing AgentHost launch

### Root cause (file:line)
`RunOrchestrator.StartChildRevisionHandoffAsync` (apps/Agentweaver.Api/Runs/RunOrchestrator.cs), the `priorWorktreeUsable` ("reused_prior") strategy at the original line ~497-499, assigned:

```
worktreeBranch = priorChild.WorktreeBranch ?? feedback.PriorWorktreeBranch;
```

When a human-review request-changes directive rotates a subtask to a different agent, a NEW revision child run is minted (new RunId, WorktreeBranch=null) in CoordinatorAssemblyService.cs (~2633-2646). The handoff physically REUSES the prior child's worktree — which is checked out on the PRIOR child's branch `agentweaver/<priorChild.Id>` — and copied that prior branch onto the new child. At launch, `RunAgentHostContextResolver.ResolveAsync` (apps/Agentweaver.Api/Sandbox/IRunAgentHostContextResolver.cs:66-71) requires the child's WorktreeBranch == `agentweaver/<newChild.Id>`, so it threw "Implementation child '<id>' must use its authoritative branch 'agentweaver/<id>'." → `agenthost_launch_failed` / WorkflowAgentInfrastructureException. This matches the staging evidence: child `0acd2bac...` carried `agentweaver/3764995a...` (the prior sibling's branch).

(The alternate "fresh_from_prior_branch" fallback was already correct — it calls `AddWorktree(..., newAgentRun.Id)` which mints `agentweaver/<newChild.Id>`.)

### Fix
1. Added `WorktreeManager.RebrandWorktreeToAuthoritativeBranch(worktreePath, runId)` (apps/Agentweaver.Api/Git/WorktreeManager.cs) — runs `git -C <worktree> checkout -B agentweaver/<runId> HEAD`, creating the new child's OWN authoritative branch at the reused worktree's current HEAD and switching the worktree onto it in place. Same-commit switch preserves all committed + staged prior work. Idempotent (`-B`) so a crash re-drive is safe. This also ensures the branch is actually CREATED for the new child id before launch (addresses task item #4 — not just a naming fix).
2. Updated the reused_prior path in RunOrchestrator.StartChildRevisionHandoffAsync to call the new method instead of inheriting the prior branch, so the new child launches on `agentweaver/<newChild.Id>`.

### Tests
- Updated RunOrchestratorChildRevisionHandoffTests.Handoff_ReusesPriorWorktree_* to assert the reused worktree PATH is retained but the branch is the new child's authoritative branch and that branch exists in the repo.
- `dotnet build apps\Agentweaver.Api\Agentweaver.Api.csproj -c Release`: succeeded, 0 warnings/errors.
- Handoff tests: 2/2 pass. Related worktree tests (WorktreeRecovery/WorkerDeliverableCapture/ImplementationWriteback): 28/28 pass.

Scope limited strictly to #305 (no changes touching #290 outcome-plan UI or #291 resiliency epic).

### 2026-07-13T17-21-13: Frontend ACR builds must prebuild apps/web outside Docker instead of passing npm auth into az acr build
**By:** morpheus
**What:** Frontend ACR builds must prebuild apps/web outside Docker instead of passing npm auth into az acr build
**References:** issue #265, commit 993520cf, apps/web/Dockerfile, scripts/aks/20-build-push-images.sh, scripts/release.sh
**Why:** Superseding the rejected #265 approach from commit 993520cf: ACR Tasks uses the classic Docker builder, so any Azure Artifacts PAT consumed through ARG+RUN leaks into image history even when passed via --secret-build-arg. We moved the private-feed npm install/build for apps/web out of apps/web/Dockerfile and into the caller scripts (scripts/aks/20-build-push-images.sh and scripts/release.sh), which now build apps/web/dist locally using ~/.npmrc or a temporary ignored .npmrc.build synthesized from AZURE_ARTIFACTS_NPM_PASSWORD_B64. The Dockerfile now only COPYs prebuilt dist, so the token never enters the Docker build context, layer filesystem, RUN metadata, manifest/config, or ACR build logs.

### 2026-07-13T00-13-41: Preview tools require cwd containment and session-owned ports
**By:** Morpheus
**What:** Preview tools require cwd containment and session-owned ports
**References:** GitHub issue #258, Seraph rejection findings 1 and 2
**Why:** For issue #258, preview lifecycle tools now have a dedicated SandboxPolicyBackend category instead of being treated as pre-validated. start_preview_process defaults missing cwd to the sandbox root, rejects model-supplied absolute/traversal cwd values, and PreviewRunner resolves model cwd with SandboxPathValidator plus validates production runner cwd against the configured effective workspace. PreviewRunner records socket-diff port candidates per session, accepts log-derived ports only when they match those candidates, and permits public health_check only for the session's recorded app or forwarder port. Observation timeouts are capped at 120 seconds. Regression coverage includes cwd escapes, canonical tool-name enforcement, cross-session/unrelated-port health rejection, and log spoofing with an unrelated baseline listener.

### 2026-07-14T00-12-52: Recover rolling-worker A2A failures through existing coordinator retry
**By:** Morpheus
**What:** Recover rolling-worker A2A failures through existing coordinator retry
**References:** #259, #241, #242
**Why:** A child run stranded by API/worker restart is terminalized with `a2a_transport_interrupted` and `retryable:true`, rather than a generic unretryable `stranded_in_progress`. This uses the existing coordinator bounded redispatch pathway and does not alter stall-detection/redispatch policy. Root runs remain non-replayable because rerunning them could duplicate user-visible work.

### 2026-07-13T17-33-06: Replace frontend temp .npmrc auth plumbing with credential-provider-based flow and no committed temp-file ignores.
**By:** Morpheus
**What:** Replace frontend temp .npmrc auth plumbing with credential-provider-based flow and no committed temp-file ignores.
**References:** issue #265, commit 523c18b7, docs/guide/deployment-aks.md, scripts/aks/20-build-push-images.sh, scripts/release.sh
**Why:** For the AKS/release frontend prebuild path, stop generating apps/web/.npmrc.build with embedded _password lines. The scripts now keep apps/web/.npmrc token-free, prefer the Azure Artifacts npm credential provider flow, translate AZURE_ARTIFACTS_NPM_PAT or the legacy AZURE_ARTIFACTS_NPM_PASSWORD_B64 into the documented ARTIFACTS_CREDENTIALPROVIDER_EXTERNAL_FEED_ENDPOINTS / VSS_NUGET_EXTERNAL_FEED_ENDPOINTS JSON contract, and remove the temp-file ignore entries. Because the requested artifacts-npm-credprovider package was not resolvable from the public npm registry during implementation, the scripts retain an official Microsoft ado-npm-auth fallback for interactive local refreshes when no PAT-backed provider env is present.

### 2026-07-13T07-56-51: Use Reject for all host denials and source-compile the MAF adapter for SDK 1.0.5 compatibility
**By:** Morpheus
**What:** Use Reject for all host denials and source-compile the MAF adapter for SDK 1.0.5 compatibility
**References:** GitHub issue #264, decisions/inbox/trinity-264-design.md, github/copilot-sdk v1.0.5, microsoft/agent-framework dotnet-1.11.1
**Why:** For issue #264, all nine governance/fail-closed denials now return PermissionDecision.Reject(existingReason), and both operator URL denials return Reject("URL fetch was denied by the operator."). This follows GitHub.Copilot.SDK v1.0.5's documented handler factories and its real-CLI E2E tests, which exercise Reject/UserNotAvailable/NoResult and never use DeniedInteractivelyByUser as a host response.

Bumping GitHub.Copilot.SDK from 1.0.2 to 1.0.5 exposed an upstream binary-identity incompatibility: SDK 1.0.5 is strong-named, while released Microsoft.Agents.AI.GitHub.Copilot packages through 1.13.0-rc1 were compiled against the unsigned SDK 1.0.0 identity and fail compilation with CS0012. To retain SDK 1.0.5/CLI 1.0.67 alignment, AgentRuntime now source-compiles the MIT-licensed adapter files from Microsoft Agent Framework tag dotnet-1.11.1, replacing only its inaccessible internal Throw helper with ArgumentNullException.ThrowIfNull, and removes the incompatible binary adapter package. Full Release build and targeted tests pass.

Agentweaver.Tests has no existing fixture that launches a real copilot CLI process, so regression coverage exercises all deny branches at the handler boundary and asserts kind=reject plus exact feedback, but cannot prove deny-then-subsequent-tool behavior across the process boundary.

# Pod-local execution uses one materialization mechanism

- **Author:** Tank
- **Issues:** #252, seam for #253
- **Status:** Proposed

AgentHost uses a single `PodLocalWorkspaceManager` for verified local execution workspaces. `ExecutionWorkspaceMode` applies policy over that workspace: `Shared`, `LocalReadOnly`, or `LocalWritable`. Assembly Build/Test is wired end-to-end as `AssemblyBuildTest` + `LocalReadOnly`; `ImplementationTurn` and `LocalWritable` are defined for #253 but rejected by the configure path until that cycle wires implementation finalization.

The launch descriptor carries only API-visible/shared inputs: `SharedWorkingDirectory`, `SourceRepositoryPath`, `SourceRef`, `BaseCommitSha`, `ExpectedTreeHash`, `WorkspaceMode`, `Purpose`, and `ScratchRoot`. AgentHost derives `/local-workspace/<run-hash>/<tree-hash>` inside the pod, verifies commit and tree identities, and exposes the effective path through runtime state. Compatibility fallbacks always use `SharedWorkingDirectory`; pod-local paths are never treated as API-visible worktrees.

`PrepareWritebackAsync` rejects read-only workspaces and returns finalization inputs for writable workspaces without performing commit/push; #253 owns that write-back. `CleanupAsync` removes the prepared run workspace. Assembly keeps the controlled shell and timeout policies separate from workspace materialization. `ShellExecutionTracker` provides single-flight execution plus observable command hash/start/deadline for future resiliency heartbeats without implementing them here.

The Kubernetes volume is named `execution-scratch`, remains disk-backed with an 8 GiB sizeLimit/limit, and requests 1 GiB so two warm standby replicas do not reserve the full active-workspace budget.

# Tank — issue #253 pod-local implementation write-back

Implemented on `squad/253-impl-writeback` in `C:\Users\asabbour\Git\agentweaver-253`.

- Coordinator child runs now resolve `SourceRef` from the authoritative `WorktreeManager.BranchNameFor(run.Id)` (`agentweaver/<childRunId>`) and launch AgentHost with `AgentHostPurpose.ImplementationTurn` + `ExecutionWorkspaceMode.LocalWritable`.
- Reused `PodLocalWorkspaceManager`: writable turns snapshot the final local filesystem through a platform-owned alternate Git index, exclude ignored/cache/nested-repository paths, preserve the existing `Agentweaver run <runId>` commit message and configured author, and push a unique `refs/agentweaver/writeback/...` ref.
- Added A2A `PreparedWriteback` data-part transport. `RemoteAgentProxy` exposes it through `IPreparedWritebackSource`; `AgentTurnExecutor` applies it before existing commit/diff bookkeeping.
- `WorktreeManager.ApplyPreparedWriteback` validates run/branch/ref/commit parent/tree, rejects dirty or moved shared branches with structured reasons, fast-forwards only, verifies the result, and supports no-op/idempotent replay.
- Updated coordinator workspace guidance to describe isolated execution with automatic publication.

Verification:
- `dotnet build Agentweaver.sln -c Release`: 0 warnings, 0 errors.
- Mandatory filtered tests: 1302 passed, 21 skipped, 0 failed.
- New coverage includes local commit publication/read-after-write, no-op, conflict/base mismatch, SourceRef resolution, A2A codec, apply-before-commit, and structured executor failure.

Commit: `77939dacb0a23615f99834a0ce16c16f16331018`.

No push or deployment performed.

# Tank — issue #254 revision

Date: 2026-07-12
Branch/worktree: `squad/254-resiliency` in `C:\Users\asabbour\Git\agentweaver-254`
Base: Morpheus `f15429c4`
Revision commit: `fdb0e129e3997e23560001f4c58a43710f6b76a3`

## Seraph blockers resolved

1. **Worker-side A2A stream deadline**
   - `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:15,232,293` adds configurable 70-minute total / 5-minute read-idle defaults, a linked stream cancellation token, deadline races around `MoveNextAsync`, typed retryable `a2a_turn_timeout` / `a2a_stream_idle_timeout`, and bounded abandoned-stream cleanup.
   - `apps/Agentweaver.Api/appsettings.json` exposes `Sandbox:AgentHost:A2AStreaming:{TotalTurnTimeout,ReadIdleTimeout}`.
   - `RemoteAgentProxyDeadlineTests.ReadIdleDeadline_CancelsBlackholedProxyStream` proves the worker cancels the underlying blackholed stream and returns in under two seconds with a tiny configured deadline.

2. **Streaming/control HttpClient split**
   - `apps/Agentweaver.Api/Program.cs:407-433` keeps `a2a-sandbox-pod` finite (2 minutes) and creates `a2a-sandbox-pod-streaming` with infinite transport timeout only for `RemoteAgentProxy`.
   - Verified real shared callers: `PreviewRunnerHttpClient`, `AgentHostApprovalHttpClient`, and `HttpAgentHostReadinessProbe` use the finite `a2a-sandbox-pod`; only `RemoteAgentProxy` uses the streaming client.
   - Covered by `StreamingA2AHttpClient_HasNoCompetingTransportTimeout` and `NonStreamingAgentHostHttpClient_RetainsFiniteTimeout`.

3. **Structured root failure propagation**
   - `AgentTurnExecutor.cs` now emits structured terminal output in production root and child graphs.
   - `WorkflowMessages.cs:111`, `RunWorkflowFactory.cs:390,491`, `RunWorkflowGraphBinder.cs:117-131`, and `RunWatchLoopService.cs:619` add the root `AgentTurnFailedOutput` path and persist the original `errorCode` instead of `watch_stream_completed_without_terminal_event`.
   - End-to-end test `RootRun_StructuredAgentFailure_PreservesErrorCode_NotStreamFallback` drives the real full workflow/watch loop and persists `shell_execution_timeout`.

4. **Bounded watchdog cleanup / force-stop**
   - `AsyncStreamIdleTimeout.cs:14,75-160,241-326` force-stops an active shell for every watchdog-owned deadline, adds configurable cleanup bounds, and bounds both pending `MoveNextAsync` observation and enumerator disposal.
   - `TotalTurnTimeout_WithActiveShell_ForceStopsProcessTree` covers total-timeout process termination.
   - `WatchdogTimeout_CancellationIgnoringSource_ReturnsAfterBoundedCleanup` uses a cancellation-ignoring source and proves timeout return plus forced disposal without indefinite cleanup.

## Verification

- Build: `dotnet build Agentweaver.sln -c Release` — **0 warnings, 0 errors**.
- Mandated targeted tests: **1131 passed, 21 skipped, 0 failed** (`1152` total). One unrelated coordinator telemetry test transiently failed once, passed in isolation, then the full mandated filter passed on retry.
- Focused changed-seam suite: **53 passed, 0 failed**.
- No push, deploy, branch switch, issue close, or changes outside the `agentweaver-254` worktree.

# Issue 257 revision 4

Planning deliverable routing uses semantic phase classification rather than filename-only handling. Launch-marketing and marketing-plan terminology are explicit planning signals, so model and deterministic decomposition both normalize those tasks to `planning`; bare Markdown outputs are then canonicalized under `docs/planning/` and dispatch reinforces the location convention.

The pre-existing assembly steering test fixture now anchors a uniquely named shared in-memory SQLite database while each scoped `MemoryDbContext` opens its own connection. This preserves per-test isolation without sharing one mutable `SqliteConnection` across the concurrently running assembly loop, steering request, and deferred-review poller—the source of the order/timing-dependent round-trip assertion.

# Tank code review — #257 revision 8

## Verdict
APPROVE

## Findings
No line-level correctness issues found.

- `CoordinatorOrchestratorExecutor.DeserializeDeclaredOutputPaths` correctly deserializes `List<string?>`, filters null/empty/whitespace entries, preserves valid entries in order, and maps malformed or non-string JSON to an empty list by catching the expected `JsonException`. The null-forgiving projection is safe after the `IsNullOrWhiteSpace` predicate.
- Regression tests assert both exact deserialized contents and scheduling behavior: malformed, blank-only, null-only, and numeric arrays become empty and conflict conservatively; mixed null/valid metadata retains only `docs/real.md`, conflicts with the matching writer, and remains parallel-safe with an unrelated writer.
- All production reads of `DeclaredOutputPathsJson` route through `DeserializeDeclaredOutputPaths` (`CoordinatorAssemblyService.DoSubtasksConflict`, `CoordinatorDispatchService.FindDeclaredOutputConflictEdges`, and `BuildCanonicalSubtaskTask`). No bypassing raw JSON parse was found.
- Rev7 behavior remains intact: `DoSubtasksConflict` is structured-output-only and conservative for empty metadata; explicit phase metadata takes precedence over heuristic inference; deterministic fallback still extracts produced files without treating references as outputs.

## Verification
- Verified `decisions/inbox/seraph-257-rev8.md` via `squad_state_read`.
- Deleted `%TEMP%\memory.db*` before validation.
- `dotnet build Agentweaver.sln -c Release`: succeeded, 0 warnings, 0 errors.
- `dotnet test tests\Agentweaver.Tests\Agentweaver.Tests.csproj --filter "Assembly|Coordinator|Steer|Revision" -c Release`: 663 passed, 0 failed, 0 skipped (single run).

# Reuse KnownPreValidatedTools for supervised preview tools

For issue #258, add `start_preview_process`, `stop_preview_process`, `observe_bound_port`, and `health_check` to `SandboxPolicyBackend.KnownPreValidatedTools` rather than introducing a new category. These four functions are the complete tool set registered by `PreviewRunnerToolProvider`; their execution and lifecycle are supervised by `PreviewRunner`, and sandbox path containment does not apply at the policy-backend boundary. This keeps the fix purely additive and follows the existing `report_intent`/`apply_patch` prevalidated pattern. The separately gated `start_preview` API tool remains untouched.

# Tank — #260 revision 2

## Decision

Approved implementation: bounded infrastructure retries remain capped at 2, but are now delayed, isolated, and resource-safe.

1. **Backoff and jitter**
   - `Subtask.InfrastructureRetryEligibleAt` persists the next eligible UTC dispatch time (`apps/Agentweaver.Api.Data/Memory/Subtask.cs:81`).
   - Retry 1 defaults to a uniformly jittered 30–60 seconds; retry 2 defaults to 120–240 seconds (`apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1000-1024`).
   - The frontier skips ineligible retries and, when otherwise quiescent, waits until the earliest persisted eligibility timestamp rather than assembling or relaunching immediately (`CoordinatorDispatchService.cs:370-371,402-411,1026-1055`).
   - SQLite and PostgreSQL migrations plus the SQLite model snapshot persist the field (`apps/Agentweaver.Api/Migrations/20260713030000_AddSubtaskInfrastructureRetryCount.cs:24-27`; `apps/Agentweaver.Api.Migrations.Postgres/Migrations/20260713030000_AddSubtaskInfrastructureRetryCount.cs:24-27`; `MemoryDbContextModelSnapshot.cs:757-758`).

2. **Shell-timeout retry safety / idempotency**
   - A retry receives a new run ID and goes through `RunOrchestrator.StartChildRunAsync`, which provisions a new per-child worktree from `Run.OriginatingBranch` (the coordinator integration base). It does not call `StartRevisionAsync` and does not resume the killed shell session or reuse its worktree.
   - The failed child is only recorded as `PriorChildRunId`; its uncommitted filesystem effects are not merged into the integration branch. Therefore shell-timeout retry is idempotent by construction with respect to repository state: clean checkoutable base, fresh workspace, fresh workflow.
   - This invariant is documented directly on the retry path and in recovery guidance (`apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:915-923,953-959`).

3. **Pod leak fixed**
   - The old child pod/SandboxClaim is released immediately after the retry state is durably persisted and before the loop can dispatch the next child (`CoordinatorDispatchService.cs:965`).
   - Regression test injects a recording `IAgentHostPodLifecycle` and verifies the failed run ID is released (`tests/Agentweaver.Tests/Coordinator/StallCascadeAndLockRetryTests.cs:620-638`).

4. **Decision persistence**
   - This file was written with `squad_state_write` and successfully read back with `squad_state_read` before completion.

5. **Genuinely fresh child test**
   - The success test no longer pre-seeds an active child. It records the `StartChildRunAsync` seam, persists/completes the launched run, and asserts the resulting run ID differs from the failed run ID (`StallCascadeAndLockRetryTests.cs:537-571`).
   - Backoff-range tests cover both configured production windows (`StallCascadeAndLockRetryTests.cs:590-617`).

## Validation

- Deleted `%TEMP%\memory.db*` before build.
- `dotnet build Agentweaver.sln -c Release`: succeeded, **0 warnings, 0 errors**.
- Filtered test run 1: **658 passed, 0 failed, 0 skipped**, duration 1m58s.
- Filtered test run 2: **658 passed, 0 failed, 0 skipped**, duration 2m02s.
- `git diff --check`: clean.
- No commit created.

## #257 overlap

The working tree already contained Trinity's unrelated #257 changes, including conflict detection in `CoordinatorAssemblyService.cs`, canonicalization in `CoordinatorOrchestratorExecutor.cs`, structured-output changes in `Subtask.cs`, and shared additions in `CoordinatorDispatchService.cs`/tests/snapshot. I did not edit #257's assembly conflict-detection or orchestrator canonicalization logic. My edits in shared files are additive and scoped to #260 retry eligibility, clean fresh dispatch testing, and old-pod release.

# Issue #262 design — Coordinator child-activity rollup

**Owner recommendation:** Trinity (frontend). This is a small presentation/derivation change in `CoordinatorRunPage.tsx`; no backend or reducer protocol change is needed.

## Decision

Show one compact, live counts-by-status line on the Coordinator in both places where it currently reads as inert:

- run-tree root row: `Running · 3 running · 5 done · 1 blocked`
- topology `WorkflowNode` face: `3 running · 5 done · 1 blocked`

Render only non-zero buckets, in this order: **running, waiting, blocked, failed, pending, done**. Keep the coordinator's own status icon/accent as the orchestration-level state; the rollup describes children and must not replace that state. Do not add a generated natural-language sentence in this pass: it would be less stable, harder to scan, and would require rules for choosing one “current” activity. The compact count line matches the redesign's small secondary text/pill language; the existing hover popover can repeat the full rollup as a `Child activity` row if desired, without adding more face chrome.

## 1. Existing frontend data is sufficient

The canonical live source is `CoordinatorTopologyState`: it already stores stable `nodeOrder`, a node map, and the work plan (`apps/web/src/state/topologyReducer.ts:28-37`). Each `TopologyNodeState` has `kind` and live `status`; `subtask.*` events update existing subtask nodes (`topologyReducer.ts:202-230`). Initial page load is also covered without a new endpoint: `seedTopologyFromWorkPlan` creates the coordinator and every work-plan subtask (`topologyReducer.ts:287-316`), then overlays `/children` live status/child-run details (`topologyReducer.ts:318-330`). The `coordinator.work_plan` SSE payload is retained too (`topologyReducer.ts:143-150`).

Derive the rollup in `CoordinatorRunPage.tsx` with a small pure helper/memo:

1. Read `topology.nodeOrder` and map through `topology.nodes`.
2. Include only `node.kind === 'subtask'` so collective assembly stages (RAI, review, merge, scribe) are not mislabeled as dispatched child agents.
3. Normalize raw statuses into the existing UI buckets. Reuse the status families already defined at `CoordinatorRunPage.tsx:1849-1853` rather than introducing competing semantics:
   - running: `EXECUTING_TASK_STATUSES`
   - waiting: `WAITING_TASK_STATUSES`, plus `assemble_ready` (waiting for coordinator assembly)
   - blocked: `BLOCKED_TASK_STATUSES`
   - failed: `FAILED_TASK_STATUSES`
   - pending: `PENDING_TASK_STATUSES`
   - done: `completed`, `merged` (and any explicitly terminal-success subtask status)
4. If topology has not populated but a work plan exists, its subtasks are already represented by the REST seed; no separate API call is required. A defensive fallback may count `workPlan.subtasks`, but it should not double-count when topology nodes exist.

The page already constructs a flat coordinator-rooted session tree (`CoordinatorRunPage.tsx:3068-3100`) and already reduces non-root rows for header counts (`CoordinatorRunPage.tsx:3106-3127`). That proves the data is present, but that reducer includes assembly stages; use topology `kind === 'subtask'` for this child-agent-specific rollup.

## 2. Recommended presentation

**Primary face format:** one ellipsized secondary line, e.g. `3 running · 5 done · 1 blocked`.

- Omit zero buckets; never show a noisy `0 running · 0 blocked ...` string.
- Keep status words lower-case to match existing compact metadata.
- Prefer counts only over both counts and prose. A sentence such as “Dispatching agents and waiting for assembly” duplicates orchestration status and becomes ambiguous during mixed waves.
- Optional hover detail: add one `NodeDetailRow` labeled `Child activity` containing the same rollup, and optionally `9 child tasks` when space allows. Do not add several colored badges to the narrow node face; the existing one-line `pillSub` treatment is designed for exactly this information (`WorkflowGraphPanel.tsx:923-924, 1051-1054`).

For the run-tree root, use the same rollup after its coordinator status and suppress the redundant `Coordinator (Coordinator)` identity only for that root row. Child rows remain unchanged.

## 3. Exact injection points and minimal change

The coordinator topology card is built inside the `planningDescriptor.nodes.map(...)` branch in `CoordinatorRunPage.tsx`. The coordinator is detected at `CoordinatorRunPage.tsx:2758`; its generic `WorkflowNode` object is returned at `CoordinatorRunPage.tsx:2846-2869`. Minimal graph change: compute `coordinatorChildSummary` once and, only when `isCoordinatorNode`, set `st.message = coordinatorChildSummary` before returning the node (or include it in the coordinator's `ExecutorState`). `WorkflowNode` already prioritizes `state.message` for `subText` (`WorkflowGraphPanel.tsx:923-924`) and renders it on the compact face (`WorkflowGraphPanel.tsx:1051-1054`), so no new node type, layout algorithm, or backend field is needed. If the popover row is included, add an optional `childActivitySummary` field to `WorkflowNodeData` (`WorkflowGraphPanel.tsx:102-130`) and one conditional row near `WorkflowGraphPanel.tsx:938-947`.

The visible run-tree row is built separately by `renderTreeItems` at `CoordinatorRunPage.tsx:3814-3879`. Its root is identified by `item.nodeId === defaultSessionNodeId` (`CoordinatorRunPage.tsx:3821-3826`). Minimal tree change: for that root only, render the shared summary in the existing secondary metadata line after `statusLabel`, instead of the redundant coordinator identity. Do not alter `flattenRunTree` (`CoordinatorRunPage.tsx:1695-1700`), sibling sorting, `sessionTree` construction, or `TreeItem itemType="leaf"`; therefore flat ordering remains intact.

Nothing changes in the staircase topology layout or edge construction: node identity, size hints, positions, and graph edges remain untouched. Nothing changes in session selection or message scoping, so the single-Messages-thread pattern remains intact.

## 4. Edge cases

- **Outcome-spec drafting / no children:** show the coordinator's existing phase/status only (`Drafting outcome plan`); omit the child rollup entirely. Do not show `0 tasks`.
- **Plan exists but all children are pending:** show `N pending`.
- **All children done:** show `N done` (not `0 running · N done`). Coordinator status may independently say assembling, awaiting review, or complete.
- **Mixed `assemble_ready`:** bucket as `waiting` for the compact line; the exact raw status remains visible on each child row/card.
- **Failed/blocked terminal run:** freeze and show the last child counts, including `failed`/`blocked`; do not terminalize every unfinished child into a misleading success/failure count solely because the parent ended. The coordinator's own failed/blocked icon communicates parent outcome.
- **Awaiting review:** keep child rollup (commonly `N done`) while the coordinator status says `Awaiting review`; review is an orchestration gate, not a child-agent status.
- **Late/stale events:** derive from reducer state, not the raw event list; reducer sequence handling already prevents stale topology regression (`topologyReducer.ts:153-199`).
- **Unknown future status:** include it in `waiting` only if it is explicitly nonterminal; otherwise omit it from the compact line and retain exact status on the child itself. Avoid silently counting unknown as done.

## Scope guardrails

This change must not reintroduce nested run-tree indentation, split Messages by node, or replace the staircase graph. It is a derived label attached to the existing coordinator representations only.

# Issue #264 wire-payload regression coverage

GitHub.Copilot.SDK 1.0.2 documents `PermissionDecision` as a polymorphic type discriminated by the JSON property `kind`, and `PermissionDecisionReject` as the `reject` variant. Its transport serialization contract is represented by the SDK's source-generated `RpcJsonContext` (internal to the SDK); standard `System.Text.Json` serialization of the statically typed `PermissionDecision` honors the same SDK-declared polymorphic contract.

Updated the shared `AssertRejected` helper in `PermissionDecisionRegressionTests.cs`, which is reached by every denial-path test case, to serialize the returned decision and parse the payload. It now asserts exact wire fields `"kind":"reject"` and a non-empty `feedback` matching the expected reason.

Validation: `dotnet build Agentweaver.sln -c Release` succeeded with 0 warnings/errors; filtered regression suite passed 10/10. Commit: `838a4a5f`.

### 2026-07-14T00-11-39: Assembly RAI now revises through steering and parks RED at human review
**By:** tank
**What:** Assembly RAI now revises through steering and parks RED at human review
**References:** #232, #236, #209, #226
**Why:** Collective RAI REVISE is propagated from RaiTurnExecutor into CollectiveRaiResult and normalized through the existing bounded coordinator steering path. RED no longer terminalizes RaiBlocked; it writes the normal durable InReview request and waits for an accountable human decision, preserving recovery semantics. All collective RAI, Rubberduck, and Scribe executors receive the workflow agent factory; remote endpoint lookup uses the parent coordinator run ID because the subrun suffix is only an event stream identity.

### 2026-07-14T06-40-13: Issue #213 is stale on v0.9.46-rc1: validated multi-subtask coordinator run renders in dependency/topological order; ready for Ahmed/coordinator closure approval.
**By:** Tank
**What:** Issue #213 is stale on v0.9.46-rc1: validated multi-subtask coordinator run renders in dependency/topological order; ready for Ahmed/coordinator closure approval.
**References:** GitHub issue #213, run:5a8c3004-e7b6-41ed-8a65-b671816b8dab, commit:f7c7d207
**Why:** Validation completed 2026-07-13 against staging https://agentweaver.6a528e9e153d92000129afcb.westus2.staging.aksapp.io, /api/version => 0.9.46-rc1. Existing multi-subtask coordinator run 5a8c3004-e7b6-41ed-8a65-b671816b8dab (project validate-256-269) provided the evidence. GET /api/runs/{id}/graph lists subtasks [plan:subtask-350, plan:subtask-351] with direct graph edges coordinator->350, 350->351, 351->assembly-rai. GET /api/runs/{id}/work-plan lists [350 execution assemble_ready, 351 validation assemble_ready] and explicit dependency {subtaskId:351, dependsOnSubtaskId:350}; GET /children returns the same [350,351] order. Thus actual API/tree descriptor ordering agrees with expected topological order 350 -> 351. Current main (34c1dc99) contains f7c7d207 (2026-07-08, 'Run page UX fixes: deterministic tree order...'), which replaced arrival/time/layout ordering with canonical stage + numeric subtask ordering. Targeted regression validation passed: `npm run test -- src/__tests__/runTreeSort.test.ts` => 5/5. No change made; do not close issue without Ahmed/coordinator approval.

### 2026-07-14T06-59-42: Request-changes transiently marked subtasks failed because all in-place steering revision children hit agenthost_launch_failed from an authoritative-worktree-branch mismatch; this is distinct from outcome-plan UI staleness and tracked as #305.
**By:** Tank
**What:** Request-changes transiently marked subtasks failed because all in-place steering revision children hit agenthost_launch_failed from an authoritative-worktree-branch mismatch; this is distinct from outcome-plan UI staleness and tracked as #305.
**References:** GitHub issue #305, GitHub issue #291, GitHub issue #290, run:18cdc7ce-6649-4b60-b001-17c317bcd281, directive:55
**Why:** Follow-up live investigation, staging v0.9.46-rc1 run 18cdc7ce-6649-4b60-b001-17c317bcd281. Ahmed's human-review request-changes ('Add airbnb too') was accepted at 23:55:46-07:00 (events 234–240; directive 55) and selected in-place steering. The initial revision children failed immediately, producing the UI's failed states: subtask 356 -> child 0acd2bac-b695-4cfe-ba36-1e458cafc061 (event 246, 23:55:46.844); 357 -> a4ce77df-ea81-4716-a457-cceb64359258 (event 248); 358 -> 3bf8418b-0c76-4cc8-a170-18e81cc23486 (event 250). Their run rows all have result agenthost_launch_failed. Each persisted run.failed error says: 'Implementation child <id> must use its authoritative branch agentweaver/<id>.' API log proves KubernetesPodAgentEndpointResolver failed before a pod launch because RunAgentHostContextResolver (line 70) rejected the branch. The failing revision children had inherited their predecessor's worktree branch (e.g., 0acd child row worktree_branch=agentweaver/3764995a...) rather than agentweaver/0acd....

This is NOT caused by the #290 outcome-plan stale UI event; the backend review request/change transition was valid. It is the same agenthost_launch_failed infrastructure failure class Link reported, with a specific root cause: invalid branch propagation for in-place steering/revision children. The coordinator recognized failed in-place revision and fell back to fresh dispatch. Replacement children fdb75887 (356) and e48e11c1 (357) reached assemble_ready; a358a2d7 (358) is running, all launched successfully on new AgentHost pods. Filed distinct runtime bug #305 with complete evidence, related to #291. No code fix made.

### 2026-07-12T21-18-46: Restore real bwrap npm-install E2E coverage for #255
**By:** Tank
**What:** Restore real bwrap npm-install E2E coverage for #255
**References:** #255, Seraph review, tests/Agentweaver.Tests/Sandbox/AssemblyBuildTestShellGuardTests.cs
**Why:** Seraph review identified that commit 5cd5e3cc replaced the only real npm install sandbox test with shell-only HOME/XDG assertions. Restored the test to execute npm install through the real Linux bubblewrap or Windows WSL-bwrap backend, using a local file dependency to avoid network reliance. The test sets HOME and XDG roots to .agentweaver-home, verifies the dependency is installed, verifies npm writes cache files under .agentweaver-home/.npm, and conditionally skips only when no real bwrap backend is available. The HOME/XDG production scheme and mount wiring remain unchanged.

### 2026-07-14T06-52-19: Run 18cdc7ce investigation: parallel fan-out is functioning; Outcome plan's awaiting-confirmation UI is stale because its persisted confirmation event is missing after the cross-replica deferred-confirm path.
**By:** Tank
**What:** Run 18cdc7ce investigation: parallel fan-out is functioning; Outcome plan's awaiting-confirmation UI is stale because its persisted confirmation event is missing after the cross-replica deferred-confirm path.
**References:** run:18cdc7ce-6649-4b60-b001-17c317bcd281, child-run:3764995a-65c8-4b87-bf6b-d1f6559dbf84, child-run:2f54cb31-4b6b-41dd-a59d-38a8406ba2eb, GitHub issue #290, https://github.com/sabbour/agentweaver/issues/290#issuecomment-4966209942
**Why:** Investigated staging v0.9.46-rc1 run 18cdc7ce-6649-4b60-b001-17c317bcd281. Serial-execution report is disproved by persisted coordinator events: seq 196 subtask 356 dispatched 2026-07-13T23:44:48.9143495-07:00; seq 199 marked running 23:44:48.9604105; seq 202 subtask 357 dispatched 23:44:51.6294344; seq 205 marked running 23:44:51.6718166. Child run rows confirm 356 ran 23:44:48.798144–23:47:23.849971 and 357 began 23:44:51.540199 (overlap ~152 s). Graph/topology bind them to distinct Kubernetes agent-host pods: 356 -> agentweaver-agent-host-2zcbp, 357 -> agentweaver-agent-host-m8sp8. Both share the logical role/name Spock and model claude-opus-4.8 but do not share an instance/pod; no per-agent concurrency-one limiter is involved. CoordinatorDispatchService explicitly dispatches independent ready frontier tasks in parallel and only serializes conflicting file scopes; these are independent fan-out siblings.

Outcome-plan finding is a UI/event-projection bug, not a backend coordinator invariant failure. GET /api/runs/{id}/outcome-spec returns status=confirmed and confirmedBy=sabbour; GET /work-plan is dispatching, consistent with legitimate post-confirm work. Persisted event history has seq 154 coordinator.outcome_spec with status awaiting_confirmation but contains ZERO coordinator.outcome_spec.confirmed events. CoordinatorRunPage's specConfirmed is solely `events.some(e => e.type === 'coordinator.outcome_spec.confirmed')`, so the UI necessarily displays awaiting confirmation despite authoritative confirmed state/work-plan. API logs at 2026-07-14 06:44:07Z show the confirmation routed to a non-owning replica, on-demand checkpoint restoration failed due the MAF JSON $type ordering exception, and decision was deferred to DB. This deferred path allowed work to progress but did not leave the required confirmation event in persisted history. Related existing epic #290; evidence posted there at issuecomment-4966209942. No code change/new issue created.

### 2026-07-12T20-49-50: #253 Seraph round 5: preserve nested repos in junk-named directories and test mid-walk cancellation
**By:** Trinity
**What:** #253 Seraph round 5: preserve nested repos in junk-named directories and test mid-walk cancellation
**References:** #253, commit 6e44de24, Seraph 5th re-review
**Why:** Updated PodLocalWorkspaceManager's bounded nested-repository scan so basename exclusions remain fast for ordinary junk trees, but are bypassed when the excluded directory itself has a root .git file or directory. This prevents legitimate nested repositories named dist/build/bin/etc. from being silently skipped. Expanded the pruning test with a dist nested repo and replaced pre-cancellation coverage with cancellation after 25 visits in a 512-branch tree, proving traversal aborts before completion. Release build succeeded with 0 errors; targeted tests passed 77/77. Commit: 6e44de24.

# Issue 257: Planning deliverable location

Use `docs/planning/` as the repository-wide location for prose and Markdown deliverables produced by coordinator subtasks whose normalized phase is `planning`.

The convention is enforced in both decomposition guidance (new plans declare full paths) and canonical child-task composition (legacy/workflow scopes with bare filenames are redirected). The same canonical composition is used for lockout handoffs, and all child prompts tell downstream consumers where to find upstream planning artifacts.

No production code hardcodes specific planning filenames or reads them from the repository root, so no file-discovery compatibility code is required.

# Decision: #257 revision 7 — structured output conflict safety

**Author:** Trinity  
**Date:** 2026-07-12  
**Status:** Implemented, uncommitted

## Decisions

1. Live dispatch conflict scheduling now reads only `Subtask.DeclaredOutputPathsJson`. Missing, blank, malformed, or empty structured metadata is treated as conflicting with every in-flight subtask; scope prose is never used as a fallback.
2. Explicit structural phases (`planning`, `execution`, `validation`) are authoritative. Planning producer-text inference runs only when phase metadata is absent/invalid (`none`), preventing execution tasks such as “Update the PRD parser” from relocating `parser.md`.
3. Chose option **(b)** for omitted outputs: deterministic fallback now infers structured produced-file paths from explicit producer/output phrases and canonicalizes planning Markdown paths. If no reliable produced path can be inferred, metadata remains empty and the conservative dispatch gate serializes the task. Legacy migration rows remain `[]` and therefore receive the same safe serialization behavior.
4. Preserved rev6 write-and-reference and label-only canonicalization tests; added direct empty-metadata conflict coverage, explicit execution-phase precedence coverage, and deterministic fallback output inference coverage.
5. The `SqliteRunEventStreamTests` flake reproduced in isolation (1 failure in 10 runs). Root cause was order-dependent background subscriber startup plus polling. The test now drives the async enumerator directly, establishing subscription before each append and awaiting each event without scheduler/poll timing.

## Verification

- Deleted `%TEMP%\memory.db*` before build.
- `dotnet build Agentweaver.sln -c Release`: succeeded, 0 warnings, 0 errors.
- Filtered tests pass 1: 651/651, 0 failed, 0 skipped, 1m52s.
- Filtered tests pass 2: 651/651, 0 failed, 0 skipped, 1m52s.
- No commit created.

### 2026-07-12T23:50:15-07:00: Trinity's root-cause findings for #264
**What:** The immediate defect is use of the wrong permission-response variant, with an additional SDK/CLI pin mismatch that should be corrected separately. All nine `PermissionDecisionDeniedByRules` constructions send exactly `{"kind":"denied-by-rules","rules":[]}`; none populate `Rules`. `DeniedByRules` represents a rules-engine result and requires an `IList<PermissionRule>` describing the rules that denied the request. It is exposed in the generated polymorphic RPC model, but the SDK's supported factories for `OnPermissionRequest` handlers are `PermissionDecision.ApproveOnce()`, `PermissionDecision.Reject(feedback)`, `PermissionDecision.UserNotAvailable()`, and `PermissionDecision.NoResult()`. The SDK's real-CLI E2E denial tests use `Reject()` and `UserNotAvailable()`, not `DeniedByRules`. Recommended minimal-risk fix: replace programmatic governance/policy/fail-closed denials with `PermissionDecision.Reject(denyReason)` (or `UserNotAvailable()` only where that is the actual semantic), preserve the existing audit/degraded events, and add a real CLI round-trip regression test proving a denied request does not poison subsequent tool calls. Also audit the two `PermissionDecisionDeniedInteractivelyByUser` returns because they are from the same result-oriented generated family; prefer `Reject("URL fetch was denied by the operator.")` unless a real-CLI test proves that variant is accepted as a host response. Independently align the image pin with the NuGet package's bundled CLI version rather than maintaining a hand-written divergent pin: either pin CLI `1.0.64-0` for SDK `1.0.2`, or upgrade SDK to `1.0.5` if retaining CLI `1.0.67`, then validate through the real process boundary. Do not attempt to fix this by merely adding a non-empty synthetic rule: the CLI rejects the discriminator itself (`unknown variant "denied-by-rules"`), before rule content can matter.
**Why:** Call sites are `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1288,1474,1485,1529,1539` and `packages/Agentweaver.AgentRuntime/GitHubCopilotAgentRunner.cs:690,701,747,757`; every construction is `new PermissionDecisionDeniedByRules { Rules = [] }`. Reflection and serialization of the resolved assembly produce `{"kind":"denied-by-rules","rules":[]}`. `Rules` is a required `IList<GitHub.Copilot.PermissionRule>`; each rule has required `kind` (documented examples include Shell and GitHubMCP) and optional `argument`. The type comes directly from `GitHub.Copilot.SDK` `1.0.2` at `packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj:11`; `Microsoft.Agents.AI.GitHub.Copilot` `1.11.1-rc1` also depends on `GitHub.Copilot.SDK` `1.0.0`, but the direct `1.0.2` reference wins. No `packages.lock.json` exists, so the csproj reference is the effective exact SDK pin. The package's `build/GitHub.Copilot.SDK.props` declares its matching runtime as `CopilotCliVersion=1.0.64-0`. AgentHost instead downloads and bakes `@github/copilot-linux-x64` `1.0.67` at `apps/Agentweaver.AgentHost/Dockerfile:21-25`; NuGet `GitHub.Copilot.SDK` `1.0.5`, not `1.0.2`, is the release whose props pin CLI `1.0.67` (`1.0.3→1.0.64-1`, `1.0.4→1.0.65`, `1.0.5→1.0.67`). Git history shows the SDK moved beta.2→1.0.0 in `c5ca0d37`, where old `PermissionRequestResultKind.Rejected` branches were mechanically mapped to empty `DeniedByRules`; SDK moved 1.0.0→1.0.2 in `f0293b71`; the image CLI was introduced as `1.0.57` in `937aa184` and independently bumped to `1.0.67` in `ea0e2304`. Thus skew is real, but it is not sufficient to explain this exact failure: CLI `1.0.67` is the runtime paired with SDK `1.0.5`, whose generated model still contains `DeniedByRules`, while SDK source explicitly documents `Reject()` as the handler rejection and includes a real-CLI regression test asserting the `{"kind":"reject"}` discriminator round-trips. The best-supported root cause is therefore misuse of an output/result variant as an input decision, made easier by an over-broad generated union; the empty `Rules` payload is additionally semantically invalid, and independent pin drift increases protocol risk.

# #265 ACR BuildKit validation

- Added `# syntax=docker/dockerfile:1` as the literal first line of `apps/web/Dockerfile`.
- Committed locally on `main`: `eea9a93a` (`fix(web): enable BuildKit Dockerfile frontend (#265)`). Nothing was pushed.
- ACR validation run `ccq` for `agentweaver-frontend:v0.9.40-rc1` still failed at `RUN --mount=type=secret` with `the --mount option requires BuildKit`.
- The syntax directive selects a Dockerfile frontend when BuildKit is available; it does not enable BuildKit in ACR Tasks' classic builder.
- The requested tag was not created. Latest frontend tags remain `v0.9.38-rc1`, `v0.9.37-rc1`, and `v0.9.36-rc1`.
- GitHub issue #265 was updated with the failed validation and recommended next directions: replace the BuildKit-only secret mount for the ACR path (using ACR secret build arguments safely) or build on a BuildKit-capable runner.

### 2026-07-14T00-08-05: Enable pod-local implementation workspaces in the deployment manifests.
**By:** Trinity
**What:** Enable pod-local implementation workspaces in the deployment manifests.
**References:** #243, #252, #253, #255, #300, #293
**Why:** The implementation write-back path is complete in main and defaults enabled, but staging explicitly set Sandbox__PodLocalWorkspace__ImplementationEnabled=false on both API and worker pods. This left implementation npm/build commands on Azure Files SMB despite the pod-local checkout design. Set the flag true in both manifests so future deployments configure ImplementationTurn as LocalWritable on /local-workspace. This also gives native npm postinstall tools a normal emptyDir filesystem with chmod support, addressing the mechanism in #300.

### 2026-07-14T06-39-30: Issue #215 is STALE: step selection is locally cached and only first-view step detail is lazily fetched.
**By:** Trinity
**What:** Issue #215 is STALE: step selection is locally cached and only first-view step detail is lazily fetched.
**References:** GitHub issue #215, apps/web/src/pages/CoordinatorRunPage.tsx:2145-2344, apps/web/src/pages/CoordinatorRunPage.tsx:3249-3258, apps/web/src/pages/CoordinatorRunPage.tsx:3941, apps/web/src/components/AgentSessionPanel.tsx:1809-1905, apps/web/src/components/AgentSessionPanel.tsx:1996-2106, commit da675816
**Why:** Validated issue #215 against current main source (no live browser session available; code-level trace only). CoordinatorRunPage's tree handler only calls openPanelForNode (apps/web/src/pages/CoordinatorRunPage.tsx:3249-3258, 3941); it updates panelNodeId/session visibility and does not change runId or invoke a page-level refetch. The coordinator page's REST seed and lifecycle poll effects depend on runId (lines 2145-2212 and 2262-2344), not selected step state. AgentSessionPanel maps a selected child node to its childRunId (apps/web/src/components/AgentSessionPanel.tsx:1834-1847). It maintains an LRU per-run cache of merged events and run detail (lines 1809-1818, 1893-1905); selection restores cache immediately (1996-2012), and its loader bypasses GET when cached (2050-2077). Thus re-selecting a viewed step does not refetch/reload it. A first-ever child selection does issue getRun/getRunEvents (2080-2106), which is the expected lazy fetch for genuinely missing child detail, then starts its scoped live SSE stream (1889-1894) and caches it. Recent commit da675816 explicitly introduced this #287 session-switch cache. Therefore the reported whole-view refetch/flicker is fixed; no implementation action is warranted.
