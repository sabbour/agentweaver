# Workflow/orchestration research — researcher 2

## Scope and status

Research and consumer-prose corrections only, against worktree `drawio-diagram-authoring`, HEAD `f10738018`. Existing uncommitted changes were present in all four owned pages and preserved. No checkout, reset, stash, commit, runtime, workflow, pipeline, skill, report, inventory, or asset edits were made. Existing diagrams were not inspected or used as evidence. Read `CONTRIBUTING.md:311–446`, the assigned plan, and owned-page entries in `deep-dive-orchestration.json`.

Changed consumers:

- `docs\deep-dive\orchestration.md`
- `docs\deep-dive\workflow-engine.md`
- `docs\deep-dive\workflow-selection.md`
- `docs\deep-dive\unified-steering.md`

This evidence file is the only additional write. No agents launched. No diagram authored/rendered. Models below are **proposals for the parent author**, not descriptions or certifications of the current PNGs.

## Important verified facts beyond the audit

1. **The current default includes PR publication.** Its success path is `agent → rai → review → merge → push-pr → scribe → done`, not the old five-executor chain. `apps\Agentweaver.Api\Workflows\DefaultWorkflowTemplate.cs:42–161` declares the nodes and all edges. RAI `no-changes` bypasses review/merge; `revise` loops; `safety-failed` is a separate sink. The parent/shared owner must reconcile `canonical-default-workflow`; this researcher did not touch it.
2. **There are two distinct fallback algorithms.** The coordinator orders its resolved project default first (`CoordinatorOrchestratorExecutor.cs:288–295`) and uses it in its outer exception fallback (`:389–403`). The selector itself prefers available `default`/`standard`, then first non-`code-review`, then first candidate (`WorkflowSelector.cs:193–215`). A diagram saying “every failure uses first/configured default” is false.
3. **The selector is not JSON-object-only or one-shot.** One retry, normalized id/display-name matching, accepted string forms, and unambiguous prose recovery all stay within the supplied set (`WorkflowSelector.cs:83–174`, `:219–235`; tests `WorkflowSelectorTests.cs:129–245`, `:329–399`).
4. **Assembly traversal orders rather than filters all gates.** `ResolveAssemblyGates` selects known check/build-test nodes and sorts by happy-path BFS index, using `int.MaxValue` and declaration order for unvisited gates; it does not simply discard every off-path gate (`CoordinatorAssemblyService.cs:1586–1625`). BFS skips terminal nodes and follows unconditional/approved/pass/review edges (`:1628–1676`). Non-code plans omit Build & Test. Do not depict a stronger path-membership guarantee.
5. **Heartbeat has a deferred-spec drain too.** After per-project pickup, it runs reconciliation, orphaned OutcomeSpec-decision drain, then every-N-ticks optional pod reaper (`CoordinatorHeartbeatService.cs:151–210`). The report mentioned only reconciliation and reaper.
6. **Generic prompt binding does not pass `node.Agent`.** It passes prompt/charter to the worker executor; peer-review passes agent/charter, and Build & Test passes agent (`RunWorkflowFactory.cs:935–1006`). `role`/`kind` are render metadata (`WorkflowDefinition.cs:66–70`). Do not draw one universal agent/charter precedence pipeline.
7. **Standalone resolution is not WorkPlan-selection replay.** `RunWorkflowFactory.cs:1495–1558` resolves the backlog task's override, else configured/first-valid definition. A missing standalone override is invalid and throws at effective resolution. Coordinator selection instead ignores unavailable explicit choices and continues. The WorkPlan records coordinator topology (`CoordinatorOrchestratorExecutor.cs:230–249`); children use the trimmed graph.
8. **Automation is an authorized durable producer, not simply a timer/webhook-to-run arrow.** Event/schedule services obtain or recover an automation invocation before publishing the pinned Ready task (`WorkflowEventTriggerService.cs:59–116`; `WorkflowScheduleTriggerService.cs:145–199`). Rejected activation does not produce work.

All unqualified code filenames below are under `apps\Agentweaver.Api\`; test filenames are under `tests\Agentweaver.Tests\`.

## Minimal content models per surviving owned diagram

### canonical-workflow-invocation — redesign

**Question:** Where does work enter, and does origin restrict the selected workflow?

Nodes: manual coordinator start; verified Repo App event; due schedule; matching trigger + authorized invocation claim; workflow-pinned Ready task; heartbeat pickup; coordinator selection from valid available definitions.

| Edge/annotation | Current implementation evidence |
|---|---|
| Manual request → coordinator run, approval policy applies | `Coordinator\CoordinatorRunService.cs:204–207` |
| Raw webhook → verify configured HMAC keys → parse and resolve projects | `Endpoints\GitHubWebhookEndpoints.cs:14–85`, `:88–118` |
| Event match → authorized claim/recovery → publish Ready | `Workflows\WorkflowEventTriggerService.cs:69–116` |
| Due schedule → authorized claim → publish Ready; recover outstanding occurrences separately | `Workflows\WorkflowScheduleTriggerService.cs:145–199` |
| Ready → atomic claim/run reservation → start reserved coordinator | `Coordinator\CoordinatorPickupService.cs:187`, `:240` |
| Pickup carries durable `RunOrigin.BacklogPickup` | `Coordinator\CoordinatorRunService.cs:339` |
| Approval/autopilot, not pickup origin alone, enables unattended confirmation | `Coordinator\CoordinatorRunService.cs:370–372`, `:403–408` |
| Selection uses all normally available definitions without invocation filtering | `Coordinator\CoordinatorOrchestratorExecutor.cs:288–300` |

Keep claims/dedupe as one narrow upstream boundary; put override details in the shared selector. No `WorkflowInvocationKind`, `ResolveInvocationKindAsync`, “Heartbeat eligibility,” or future-only event/schedule node.

### orchestration-fig2 — retain semantic identity

**Question:** How does persisted dependency order constrain parallel work?

Nodes: confirmed OutcomeSpec; persisted WorkPlan; illustrative A/B parallel roots; C/D dependents; aggregate handoff. A/B/C/D are explicitly hypothetical, not a fixed runtime topology.

Evidence: spec lookup and existing-plan reuse `Coordinator\CoordinatorOrchestratorExecutor.cs:119–150`; selection/decomposition `:157–160`; plan persistence with selected workflow `:230–249`. Dependency edges are `(subtaskId, dependsOnSubtaskId)` and readiness requires every predecessor satisfied (`Coordinator\SubtaskFrontier.cs:54–85`). Satisfaction is only `assemble_ready`/`completed`, not any terminal (`:34–45`). Do not imply plan count is always four or that dependency arrows are agent messages.

### orchestration-fig8 — retain semantic identity

**Question:** What is persisted before dispatch starts?

Minimal sequence: request → draft/confirmation boundary → select/decompose → persist WorkPlan → dispatch ready children → collective handoff.

Evidence: existing plan short-circuit `Coordinator\CoordinatorOrchestratorExecutor.cs:133–150`; selection/decomposition `:157–160`; compatibility validation `:179–182`; persistence `:230–249`; dispatcher readiness `Coordinator\CoordinatorDispatchService.cs:387`; aggregate handoff `:798–799`. Confirmation alternative: `Coordinator\CoordinatorRunService.cs:370–372`, `:403–408` gates unattended confirmation on autopilot. Do not draw both manual approval and unattended approval as mandatory sequential steps.

### orchestration-fig3 — redesign

**Question:** Which child results enable dependency progress and parent assembly?

Nodes: persisted pending subtasks → ready frontier → isolated child agent → either assemble-ready or typed turn-failed; successful branch publication → dependency integration base → newly ready dependents; quiescence → aggregate eligibility → parent assembly.

Evidence: readiness/satisfaction/quiescence `Coordinator\SubtaskFrontier.cs:34–45`, `:64–102`; launch `Coordinator\CoordinatorDispatchService.cs:906–929`; dependency-base rebuild `:989`, `:1327–1435`; integration contract `:2715–2731`; child conditional graph `Runs\RunWorkflowFactory.cs:788–814`.

Remove “child safety/RAI gate.” No child human review/merge/Scribe. Mark failure as **not** satisfying dependents. Quiescence is not aggregate eligibility. Treat the integration-base node as an artifact dependency contract, not a shared writable checkout. Parent collective details belong to the other researcher's canonical.

### orchestration-fig9 — retain semantic identity

**Question:** How do execution and observation interact in time?

Minimal lanes: orchestrator; factory/binder; MAF/agent executor; watch loop; durable run/pending state; event stream/client.

Edges: orchestrator → factory start (`Runs\RunOrchestrator.cs:960`); factory → effective definition/built graph and checkpointed stream (`Runs\RunWorkflowFactory.cs:1386–1439`); orchestrator → supervised watcher (`Runs\RunOrchestrator.cs:260`, `:364`); runtime stream → watch loop (`Runs\RunWatchLoopService.cs:313–335`); request → durable pending/review state (`:363–377`); typed terminal → status projection (`:409`, `:595–700`).

Do not imply the watcher calls an orchestrator method to persist each status. Do not duplicate the shared SSE replay/tail model. Checkpoints are provider-aware, not universally files.

### orchestration-fig10 — redesign

**Question:** What happens on one heartbeat tick?

Sequence: enumerate projects → **per active/available project** read deterministic capped Ready candidates → each candidate atomic claim/reservation + start → end project loop → record tick → one reconciliation sweep → one orphaned-spec-decision drain → optional every-N-ticks pod reaper.

Evidence: `Coordinator\CoordinatorHeartbeatService.cs:96–147` per-project loop; `:151–166` record/sweep; `:169–187` deferred-spec drain; `:190–210` optional throttled reaper. Pickup claim/start: `Coordinator\CoordinatorPickupService.cs:187`, `:240`; confirmation is autopilot-dependent (`CoordinatorRunService.cs:403–408`).

Retain failure-isolation as a note, not a maze of catch arrows. Reconciliation and reaper must not be inside the project loop.

### workflow-engine-fig1 — redesign

**Question:** Which components turn a definition into durable execution?

Nodes: definition → factory/binder → executable MAF graph; illustrative **default** executor chain; provider-aware checkpoint store; watch loop → durable state/pending decisions and workflow-step stream.

Definition/binding edges: `Runs\RunWorkflowFactory.cs:818–838`, `Workflows\RunWorkflowGraphBinder.cs:95–139`. Default chain including `push-pr`: `Workflows\DefaultWorkflowTemplate.cs:42–161`. Start/checkpoint edges: `Runs\RunWorkflowFactory.cs:1386–1439`; provider registration `Program.cs:1063–1065`, factory store construction `Runs\RunWorkflowFactory.cs:181–188`. Watch/pending projection: `Runs\RunWatchLoopService.cs:313–377`.

No project-policy composer; no universal filesystem-only store; no assertion that every workflow uses the default chain. Children are a distinct trimmed branch, not a chain with optional hidden gates.

### workflow-engine-fig2 — retain semantic identity

**Question:** What is declarative graph data, and what must runtime binding add?

Minimal nodes: YAML → parsed definition (start + typed nodes + edges + metadata) → structural validation → bindability validation → execution graph or error.

Fields/render metadata: `Workflows\WorkflowDefinition.cs:58–103`. Runtime classifier: `Workflows\NodeClassifier.cs:66–100`. Binder validate/wire: `Workflows\RunWorkflowGraphBinder.cs:95–156`. Tests: `Workflows\RunWorkflowGraphBinderTests.cs:49–89` renamed nodes and fail-closed unsupported type; `:92–124` loader-valid extension nodes and invalid verdict-style start. Node vocabulary remains prose, not individual boxes for every enum value.

### workflow-engine-fig4 — redesign

**Question:** Which node fields affect rendering versus runtime context?

Nodes/edges: node `role`/`kind` → renderer metadata only (`Workflows\WorkflowDefinition.cs:66–70`); generic prompt/publish `prompt`/`charter` → AgentTurnExecutor (`Runs\RunWorkflowFactory.cs:935–955`); peer-review `agent`/`charter` → RubberduckTurnExecutor (`:987–1006`); Build & Test `agent` → platform executor (`:961–980`). Show assigned run identity as context, not an implied lane-to-role conversion.

`publish → NodeKind.Agent`: `Workflows\NodeClassifier.cs:78`. No `role/kind → executing catalog agent` arrow; no generic-prompt `node.Agent → executor` arrow.

### workflow-engine-fig5 — retain semantic identity

**Question:** How are safe project candidates discovered and refreshed?

Nodes: embedded default + conformance-checked catalog + project YAML → loader/bindability → results (invalid diagnostics retained) → allowed-set filter (default retained) → signature-keyed per-project cache → available candidates.

Evidence: `Workflows\WorkflowRegistry.cs:18–25` results/availability; `:57–83` cache/sync; `:122–180` sources/load; `:183–203` silently skipped materialized default versus reserved catalog conflicts; `:211–242` signature and allowed-set filter; `:245–265` collision handling. Label sources as categories, not override precedence. Do not add a review-policy-file branch.

### workflow-engine-fig9 — retain semantic identity

**Question:** Why can a valid-looking YAML graph still fail before execution?

Nodes/edges: definition → type/gate classifier → executor bindings → transition predicates/adapters → terminal outputs → MAF graph; unsupported node/start/edge → WorkflowBindException.

Evidence: `Workflows\NodeClassifier.cs:66–105`; `Workflows\RunWorkflowGraphBinder.cs:95–151` wiring/start validation; `:156–205` dry-run checks; `Runs\RunWorkflowFactory.cs:818–850` binder integration. Tests `Workflows\RunWorkflowGraphBinderTests.cs:49–89`, `:122–135` cover rename parity, unsupported node, invalid verdict start. Legacy policy-prefixed bindings are internal wiring, not an external policy composer.

### workflow-engine-fig11 — retain semantic identity

**Question:** How does generation yield a validated but unsaved draft?

Sequence: user description → generation endpoint → server prompt/model → clean/ensure id → loader + binder; valid → draft response; invalid → exactly one correction using error → same loader + binder → draft or generation exception.

Evidence: `Workflows\CopilotWorkflowGenerator.cs:54–85` two passes/exception; `:88–112` cleanup/loader/binder and built-in edit new-id validation. Tests `Workflows\WorkflowGeneratorTests.cs:264–350`, `:622–663`. No direct generation → write project workflow arrow; saving/applying is separate.

### unified-steering-fig1 — redesign

**Question:** How does gate feedback become a visible, bounded action?

Nodes: implemented assembly gate feedback → SteeringSignal → persisted/queued directive + received event → decider → durable decision + decision event → four distinct effects: in-place revision; fresh dispatch; **durable human-review escalation**; advisory no-op.

Evidence: accepted source vocabulary with future-ready agent marker `Coordinator\SteeringSignal.cs:93–109`; deterministic policy `Coordinator\CoordinatorSteeringDecider.cs:31–52`; budget transaction then decision event `:201–259`; assembly signal submission `Coordinator\CoordinatorAssemblyService.cs:2289–2319`; A/B/C/D effects `:2333–2397`; durable escalation `:2845–2991`.

Annotate: human request-changes resets autonomous budgets (`CoordinatorAssemblyService.cs:2264–2287`); autonomous feedback cannot reset them. In-place proof is attempt-specific, not any existing checkpoint (`CoordinatorSteeringDecider.cs:265–276`; tests `Coordinator\UnifiedSteeringTests.cs:578–643`). Warm-pool/pod-released distinction: tests `:245–322`. Same-author bounded degradation: tests `Coordinator\CoordinatorAssemblyServiceTests.cs:949`, `:1020`.

Collective RAI RED is a **separate safety escalation**, not an ordinary request-changes loop or terminal `RaiBlocked`: `CoordinatorAssemblyService.cs:3752–3795`; actual test body `Coordinator\CoordinatorAssemblyServiceTests.cs:3058–3082` asserts plan `InReview`, run `AwaitingReview`, review requested, and no `run.rai_blocked`.

## Shared consumers, disposition enactment, and parent handoff

| Consumer/concept | Enacted action / remaining owner work |
|---|---|
| `canonical-review-policy` in workflow engine | Removed image and source comment; replaced with workflow-declared-gate prose under the binding section. |
| `orchestration-fig7` | Removed false overlay image/comment; linked `workflow-engine.md#binding-declarative-nodes-to-runtime-execution`. |
| `orchestration-fig11` | Consumer now uses `review-merge-fig5.png`, API-arbitration alt text, and canonical `review-merge-fig5.drawio` provenance. Corrected API diagram evidence/render remains parent/other researcher's responsibility; did not inspect review-merge code/docs. |
| Project review policies across four pages | Removed nonexistent registry, policy directory/cache, composer, configurable defaults, injected-gate and rebuilding claims. Only blueprint `review_policy: default` accepted (`Blueprints\BlueprintService.cs:113–115`); effective factory resolution returns workflow unchanged (`Runs\RunWorkflowFactory.cs:1495–1517`). |
| `canonical-workflow-selection` shared consumers | Reused unchanged asset; corrected both owned pages for override-before-singleton/events, unavailable override continuation, bounded parser/retry, distinct fallbacks, and post-decomposition compatibility. Parent must reconcile shared graphic. |
| `canonical-default-workflow` | Reused unchanged asset, added current `push-pr` path and standalone/collective distinction in prose. Parent must check/update shared graphic against current template. |
| Shared coordinator and event-replay canonicals | Reused; no asset inspection or edits. Their detailed evidence remains with their owners. |
| Backlog inline Mermaid | Removed redundant/inaccurate state diagram; prose distinguishes persisted Backlog/Ready/Claimed from linked-run card projections and links shared board explanation. Enum proof: `packages\Agentweaver.Domain\BacklogTaskState.cs:3–8`. |
| Outcome and run-state Mermaid | Retained compact medium; run state explicitly conceptual/non-exhaustive. |
| Role vocabulary, webhook trust boundary, trigger DSL, gate ordering | Kept prose/list/numbered pipeline; corrected current publish support, Repo App HMAC route, authorized trigger publication, and gate ordering/Build-Test omission. |
| Steering direction table and reliability prose | Kept text/table; corrected durable escalation, same-author bounded fallback, typed child failure and human-only budget reset. |

## Assumptions and blockers

- Parent will author/render surviving assets and reconcile shared canonicals. No current bitmap has been certified by this report.
- Only `orchestration-fig11` consumer provenance was switched as explicitly requested. Other existing legacy JSON provenance comments remain for parent to replace when corresponding draw.io sources are actually authored. They are not evidence.
- The report's old current-code line numbers drift in several places; use this report's verified source spans. Stale implementation comments still mention project-policy composition and terminal steering exhaustion; executable branches and tests contradict those comments.
- The selector fallback preference and current `push-pr` default are additional shared-asset correctness blockers beyond the assigned audit. No runtime fix is proposed or implemented.
- Documentation-only validation should remain scoped: check owned Markdown links, removed-image references, and whitespace. Runtime tests were inspected, not run; no runtime behavior changed.

## Validation result

Scoped read-only checks passed for all four owned pages: every relative Markdown target exists, fenced blocks and HTML comments balance, and retired `canonical-review-policy.png`, `orchestration-fig7.png`, and `orchestration-fig11.png` references are absent. Scoped `git diff --check` passed. No runtime test or docs build was run because this worker changed only prose/consumer references; rendering and shared-asset validation belong to the parent.

Validation learning: the first ad-hoc comment-balancing check falsely counted Mermaid `-->` arrows as HTML closers. The corrected check strips fenced code before comparing `<!--`/`-->`; it passes. No documentation defect or file rewrite was needed for that check.
