# Coordinator / collective review research

## Scope and provenance

Researcher 1; bounded to `coordinator-internals.md`, `review-merge.md`, and
`resilient-assembly-review.md`, their relevant implementation/tests, and their entries in
`plan-deep-dive-orchestration.json` and `deep-dive-orchestration.json`.

Worktree: `C:\Users\asabbour\Git\agentweaver\.worktrees\drawio-diagram-authoring`.
Implementation observed at HEAD `f10738018` with pre-existing uncommitted documentation work.
Read `CONTRIBUTING.md` AI contribution process. No checkout, reset, stash, commit, runtime,
pipeline, shared-asset, skill, plan, inventory, audit-report, or reconciliation edits.
No nested agents, diagram source authoring, rendering, or image inspection.

**Evidence hierarchy:** executing code and inspected tests are factual references. Existing image
names, JSON provenance comments, inline Mermaid, and audit descriptions are **legacy visual
references only**, never evidence that a behavior exists. Test assertions cited below were read,
not executed. This is documentation research, not a claim of production or runtime validation.

## Findings: facts, not proposed behavior

1. **RED and REVISE must not be conflated.** Collective RED (`SafetyFlagged`) directly opens durable
   human review, reason `rai_red`. REVISE (`RevisionRequested`) enters the explicit steering
   decision; `Proceed` escalates durably to a human. Neither directly terminalizes `RaiBlocked`.
   A diagram showing every REVISE immediately parked at human review would also be wrong.
2. **Human assembly review has no wall-clock timeout.** Shutdown cancellation leaves it recoverable.
   A valid persisted review resumes the recorded branch/tree without rebuilding; only missing or
   incomplete review metadata takes the rebuild fallback.
3. **No configurable review-policy overlay exists on the inspected runtime path.** Effective workflow
   resolution returns the selected definition unchanged; blueprint `review_policy` accepts only
   `default`. Historical policy-prefixed adapter comments are not a registry/composer.
4. **Aggregate gates are authored and applicability-aware, not a fixed universal chain.** Matching
   checks/build-test nodes sort by approval-path traversal rank, then declaration order.
   Unreachable matching gates sort last; they are not excluded by traversal. Canonical stage IDs
   deduplicate. Positively non-code plans omit Build & Test; unknown classification retains it.
5. **Resumable rejections can keep their author.** Direction owns the action: in-place revision,
   fresh-dispatch rotation, human escalation, or advisory continuation. Structured target files,
   not feedback prose inference, determine implicated contributors; dependent rebuilds carry no blame.
6. **Review arbitration is path-specific.** Local request-changes/decline CAS occurs before pending
   removal. Live approval does not perform the merge CAS in the endpoint. Merge takes the repository
   lock before its CAS. Deferred decisions persist before their request-changes/decline transitions.
   Matching terminal replays can be idempotent success, not always `409`.
7. **Standalone authorization is not universally owner-only.** `/review` requires project
   contributor access; the pending owner check additionally applies to projectless legacy runs.
   Collective assembly retains a distinct owner-scoped delivery contract. Neither establishes a
   general human-author-cannot-approve rule.
8. **Children have no per-child RAI executor.** Their typed output selects assemble-ready or failure.
   Pod-per-run execution checkouts are isolated; published branches share the authoritative repository.
   The child provider and collective reviewer provider boundary are resolved rather than universally
   hardcoded to GitHub Copilot.

## Source-key index

Each reference below expands to this exact repository-relative file plus the cited line range:

| Key | File |
|---|---|
| A | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs` |
| S | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyStore.cs` |
| G | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyGateResolver.cs` |
| P | `apps/Agentweaver.Api/Coordinator/AssemblyPlanning.cs` |
| C | `apps/Agentweaver.Api/Coordinator/CollectiveAssemblyPipeline.cs` |
| D | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs` |
| F | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs` |
| W | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs` |
| O | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs` |
| R | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyReviewPersistence.cs` |
| E | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs` |
| M | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs` |
| RF | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` |
| RT | `packages/Agentweaver.AgentRuntime/Workflow/RaiTurnExecutor.cs` |
| AT | `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyServiceTests.cs` |

## Surviving diagram content models and edge evidence

These are **proposals for visual organization** grounded in the facts above, not authored diagrams.
Parent owns all drawing, layout, rendering, and final provenance updates.

### `coordinator-internals-fig4` — collective assembly canonical

**Question:** How does a settled child plan become a reviewed integrated result, and where can it
wait, revise, or fail? Use a main aggregate pipeline plus a bounded steering return lane; keep
state, artifact, and executable-gate nodes visually distinguishable.

| Node / directed relationship | Factual evidence |
|---|---|
| Durable `awaiting_assembly` → claim → `assembling`; losing claim exits | S:31–46; A:851–860 |
| Claim → terminal-ineligible check | A:873–902; P:33–45 |
| Terminal failed/blocked/rai-flagged children → assembly block, never partial assembly | A:875–902; F:40–46 |
| Nonterminal children → await more / re-arm dispatch, not permanent eligibility failure | A:904–908; P:33–45 |
| All eligible → dependency-ordered branch inputs | A:910–913; P:120–157 |
| Branch inputs → integration build → aggregate tree/diff persisted | A:916–981 |
| Unresolved integration conflict → needs resolution | A:945–956 |
| Auto-resolved overlap → diagnostic, then continue | A:958–968; label `accept_child`, not universally “conflicts fail” |
| Aggregate snapshot → applicable authored gate list | A:981–984; A:1586–1630; G:60–108,137–179 |
| Changed integration → detached reviewer worktree used by RAI/Rubberduck | A:986–997,1109–1118,1166–1176 |
| RAI GREEN/YELLOW → next resolved gate | RT:232–244; A:1131–1148 |
| RAI RED → durable human review (`rai_red`) | C:133–136; A:1131–1136,3757–3795 |
| RAI REVISE → explicit steering, not `RaiBlocked` | C:133–136; A:1139–1146 |
| Rubberduck request-changes → same steering decision | A:1178–1192 |
| Build & Test → deterministic preview on approve/request-changes; preview failure does not itself block review | A:1023–1084 |
| Build & Test infrastructure exception → classified park/failure, separate from authored code feedback | A:1039–1044; `ParkBuildTestInfrastructureFailureAsync` is the separate boundary |
| Human gate → persist request **before** normal `InReview` status → await | A:1198–1234; R:11–51 |
| Human waiting state: WorkPlan `in_review`, parent `awaiting_review` | A:1359–1385 |
| Human approve → continue remaining authored gates | A:1443–1464; not always “human approve immediately merges” |
| Gate list completes / recovered approval → complete-after-approval | A:1388–1409,1678–1698 |
| Request-changes → scoped signal → decision | A:1411–1419,1467–1482,2232–2397 |
| `Proceed` → durable human escalation, not terminal | A:2372–2388,2845–2958 |
| Human decline → `assembly_declined`, parent Declined | A:1423–1440,1485–1499 |
| Approved integration **source** → originating target branch under repository merge lock | A:1694–1698; C:439–470 |
| Merge conflict → NeedsResolution; nonconflict merge failure → AssemblyFailed / parent MergeFailed | A:1699–1744 |
| Merge success → Scribe → parent Completed / `assembly_complete` | A:1747–1775; AT:3013–3031 |
| Scribe failure is nonfatal to an already merged result | A:1818–1852; keep as side annotation, not rollback |
| Persisted `in_review` → apply deferred decision OR re-arm without rebuilding | A:1321–1357; AT:2356–2404 |
| Missing branch/tree metadata → exceptional rebuild fallback | A:1329–1337 |

**Important precision:** Normal human-gate setup persists the row before switching to InReview;
RED/escalation setup uses a guarded InReview CAS before upsert. Do not use one transactional-looking
arrow to imply all these writes are atomic together. R:11–51 stores owner/branch/tree; escalation
reason and accumulated feedback are in the event, not extra columns on that review row.

**Inspected regression anchors:** AT:3034–3055 asserts REVISE creates a persisted Rai steering
directive and is not RaiBlocked; AT:3058–3083 asserts RED yields InReview/AwaitingReview and no
`run.rai_blocked`; AT:2356–2404 asserts zero integration rebuilds on review recovery.

**Do not carry forward:** disconnected token nodes (`by`, `Test`, `detached`, `ready`), universal
fixed gate ordering, terminal RED/REVISE, feedback-inferred reset arrows, or a human-review timeout.

### `coordinator-internals-fig2` — draft/revise/finalize detail

**Question:** How is human intent confirmed before planning? Scope explicitly to DefineOutcome
mode; Direct mode is a nearby prose exception, not an arrow bypassing a gate in this graph.

| Node / edge | Factual evidence |
|---|---|
| Draft executor → request port `coordinator-confirmation-gate` | W:103–117,158–160 |
| Decision Revise → revise input → draft again | W:129–138,160–164 |
| Decision non-Revise (confirm OR decline) → finalize | W:164–168 |
| Finalize → persist confirmed/declined spec; confirmed-by only for confirm | W:612–631 |
| Finalize → orchestrate; only confirmed status performs decomposition | W:142–156,168–170 |
| Decline → pass-through terminal outcome, no decomposition | W:148–150 |
| Autopilot → same confirmation mechanism attributed to submitting user | `CoordinatorRunService.cs:204–208`, `:626` |
| Direct mode → prompt-backed confirmed spec and orchestration (prose-only exception) | W:173–188 |

Do not show decline entering real work-plan decomposition or autopilot bypassing review.

### `coordinator-internals-fig3` — frontier/observation detail

**Question:** When does the dispatcher start another child, keep watching, recover, or hand off?

| Node / edge | Factual evidence |
|---|---|
| Persisted status map + dependency edges → ready pending frontier | F:63–85 |
| Only assemble-ready/completed prerequisites satisfy dependencies | F:34–46 |
| Ready frontier → infrastructure retry eligibility → scope-conflict deferral or dispatch | D:387–427 |
| Dispatch → observe child | D:422–427; observation starts at D:1827 |
| Nonterminal child → continue observation; no unconditional “terminal” exit | D:1827 onward; D:514–531 explicitly re-observes retry/resume outcomes |
| Completed observation → apply result → recompute frontier | D:459–470,536–537 |
| Stalled → bounded fresh-child recovery → frontier | D:463–474; `TryRedispatchStalledSubtaskAsync` D:1537 |
| Retry window pending and no in-flight work → delay then frontier | D:439–451 |
| No in-flight work and no eligible retry/frontier → quiescent → finalize hand-off | D:430–455,540–543; F:94–104 |
| Hand-off → assembly eligibility check, NOT “all children succeeded” | D:736–761; A:873–908 |

Pending capacity is nonterminal and does not satisfy dependencies (F:18–23).
Represent legitimate approval/provisioning waits as an observation annotation rather than failures.
Keep dispatch/assembly lease details in the existing prose instead of another distributed-lock graphic.

### `resilient-assembly-review-fig1` — specialized convergence loop

**Question:** How does rejected aggregate work retain context and eventually reach a human?
Unlike the aggregate canonical, foreground direction, resumability, authorship, and budget.

| Node / edge | Factual evidence |
|---|---|
| Gate pass → next gate; normal human approval → remaining gates/completion | A:1148,1194,1454–1464 |
| Request-changes → structured implicated set + dependent closure → signal | A:2246–2302; P:257–324 |
| Human source → increment telemetry and reset autonomous budget for full closure | A:2271–2286 |
| Autonomous source → **no self-reset** | A:2271 conditional, same function |
| Signal → persisted inline decision → direction | A:2290–2321 |
| InPlaceSteer → same author/session revision; no target reset-to-pending | A:2334–2350; AT:1090–1128 |
| DispatchFresh → scoped author rotation with accumulated context | A:2352–2370,2590–2603 |
| Alternate eligible author → guarded author CAS → handoff | A:2590–2603 onward |
| No alternate + context → same-author conscious fresh dispatch, no new lockout mutation | A:2549–2560 |
| No context → durable human escalation | A:2563–2585 |
| Budget exhausted / Proceed → execute park; settle directive only after durable review opens | A:2372–2388,2845–2958 |
| Human wait → approve / request-changes / decline | A:1388–1440 |
| Human request-changes → fresh bounded autonomous budget, no human-round-trip cap | A:2264–2286 |
| Failed in-place effect → conscious fresh fallback, not a silent reset | AT:253–315; A recovery executor |

Tests read: AT:1090–1128 expressly asserts same author, no LockedOutAgents mutation and no handoff
for a resumable rejection; AT:949 and :1020 identify same-author degrade and eventual
recovery-budget escalation cases. Do not label in-place as “advisory only”.
Advisory is its own no-reset continuation (A:2392–2397).

Supporting prose/table retained: implicated vs dependent sets, structured TARGET_FILES, typed child
failure terminals, accumulated feedback, and degrade vs escalate. RT:200–241 and :342–424 distinguish
sentinel parse/re-ask/RED from REVISE; C:133–136 preserves the distinction at collective consumption.

### `review-merge-fig1` — review versus merge responsibilities

**Question:** What does review authorize, and what must merge still prove?
Scope label: **review-bearing standalone workflow**, not every authored workflow.

| Node / edge | Factual evidence |
|---|---|
| Selected workflow definition → concrete graph binding | RF:1495–1517,822–835 |
| Produced tree/diff → declared review gates → decision | RF full-graph binding; E:927–931 decision object,1049–1053 response delivery |
| Request-changes → producer revision path | E:969–975,1049–1053; dedicated path E:1367–1422 |
| Approval → continuation → merge guard | E:963–967,1049–1053; M:82–101 |
| Decline → declined terminal | E:977–980 |
| Guarded merge → merged / blocked / conflict / internal error | M:106–185 |
| No project review-policy injection node | RF:1495–1517; `BlueprintService.cs:111–115` |

Avoid claiming every custom workflow must contain a human gate or that human approval itself edits files.

### `review-merge-fig5` — canonical API arbitration sequence

Recommended lanes: caller, review endpoint, durable run/pending state, live workflow,
merge coordinator/repository. Use alternatives, not a single happy-path order.

| Sequence segment | Factual evidence |
|---|---|
| Caller → endpoint; fetch/access check before action | E:862–877 |
| Matching terminal replay → existing result; other non-review status → conflict | E:879–886 |
| Live workflow with no pending request → `409` | E:888–891 |
| Deferred path: no local workflow + pending request → persist decision | E:933–942 |
| Deferred request-changes/decline → status transition after persistence | E:944–957 |
| Local request-changes → CAS to InProgress → pending removal | E:969–984 |
| Local decline → CAS to Declined → pending removal | E:977–984 |
| Local approval → pending removal, **no HTTP merge CAS** | E:963–967,983–984 |
| Pending owner defense only for projectless runs; project run access already checked | E:875,1008–1011 |
| Consumed pending request → construct/send workflow response | E:1049–1053 |
| Workflow reaches merge → repository lock → merge CAS → Git operation | M:46–62,82–101 |
| Pending absent + no live workflow → direct fallback | E:985–1001 |
| Direct request-changes → restore awaiting_review, return `409` | E:2966–2979 |
| Direct approve → required artifacts/existence/tree check → shared merge coordinator | E:2988–3022 |

Do not equate response `status: merging` on deferred approval with a completed merge CAS.
Do not promise all replay losers return `409`: E:879–883 returns matching terminal state.
Do not silently fold collective assembly delivery into this sequence: R:122–162 validates the
durable assembly review, tries the owner-scoped local gate, then falls back to deferred persistence.

### `review-merge-fig4` — guarded standalone merge detail

| Node / edge | Factual evidence |
|---|---|
| Reviewed merge input → canonicalize/validate path | M:34–44 |
| Valid path → acquire repository lock (5-second wait) | M:46–48 |
| No lock → LockFailed/repository_busy | M:47–48,85–93 |
| Lock acquired → TryStartMerging CAS | M:50 |
| CAS loses → reload; if already Merging continue while lock held, otherwise release/fail | M:51–58 |
| Guard satisfied → merge reviewed source/tree into originating branch | M:98–102 |
| Merged → persist commit/status → best-effort worktree removal | M:106–136 |
| Blocked → revert merging → awaiting-review recovery | M:138–147 |
| Conflict → MergeFailed/details, preserve worktree | M:149–171 |
| Caught non-InvalidOperationException → revert/internal error | M:178–185 |
| All acquired-lock exits → release | M:187–190 |

The already-Merging continuation is a real nuance: “CAS false always fails” is inaccurate.
The generic internal-error branch must not imply every exception is caught; the filter excludes
InvalidOperationException. Standalone merge and collective merge are not the same status machine:
C:439–470 performs collective repository locking, while AT:3013–3031 verifies parent Completed.

### Existing inline standalone review state sketch

Retained, scoped as review-bearing standalone flow. InProgress → AwaitingReview → request-changes
InProgress/decline Declined/approval eventually Merging; merge results as M:106–185. Approval can
continue other authored nodes before reaching Merging. It is a summary, not the endpoint CAS order.

### Coordinator inline state sketch: parent authoring action

Removed the false legacy Mermaid state block (terminal RaiBlocked and direct request-changes reset)
and preserved its useful material as a corrected state table. The plan prefers corrected inline state
notation; the parent can replace or complement the table using the fig4 state model above.
This researcher deliberately did not author a replacement diagram.

## Consumer changes enacted

- `coordinator-internals-fig1` consumer now reuses read-only
  `canonical-coordinator-architecture.png`, with canonical `.drawio` provenance. No shared asset touched.
- Removed configurable policy-overlay prose/image from `review-merge.md`; retained declared-gate
  explanation and link to `workflow-engine.md#binding-declarative-nodes-to-runtime-execution`.
- Removed duplicate `review-merge-fig3` image/comment; internal link targets `#approve-vs-request-changes`.
- Replaced `review-merge-fig6` consumer with `coordinator-internals-fig4`, aggregate-gate/steering alt text,
  and future surviving `.drawio` source pointer.
- Corrected local workflow-selection summary to trigger-agnostic selection and post-decomposition
  compatibility; linked the existing selection page instead of creating a selector visual.
- Corrected isolated child execution, absence of child RAI, provider boundaries, indefinite durable
  review recovery, structured scope, resumable rejection, escalation, merge source/target, and API order.
- Kept operational details as prose/tables rather than adding diagrams.

## Proposals and assumptions

- **Proposal:** use the graph structures above as the drawing models; layout, colors, routing, and
  grouping are entirely the parent's decision. No factual significance attaches to proposed lanes.
- **Assumption:** parent will author surviving `.drawio` assets and replace legacy JSON export comments
  for retained images. This researcher updated only the required consolidated consumer provenance.
- **Assumption:** shared canonical architecture and foreign-area binding/selection page reconciliation
  remain with their owners. Their contents were not used as ground truth or changed here.
- **Assumption:** code/test line numbers remain stable because this task forbids runtime edits.

## Remaining publication blockers / limits

1. Parent must replace the stale PNG/source semantics for fig4, fig3 frontier, resilient fig1,
   review fig1/fig5 and validate retained fig2/merge fig4 visually. Legacy PNGs were not inspected.
2. Parent must restore corrected inline coordinator state notation if retaining that plan preference;
   the corrected table avoids leaving a false terminal safety diagram visible in the meantime.
3. Shared canonical asset reconciliation and foreign consumer fixes are out of scope.
4. No runtime tests or docs build were run. Documentation-only validation is whitespace/diff checking
   and local-link/image existence checking; test citations are inspected evidence, not execution claims.

Validation completed: scoped `git diff --check` passed; all Markdown link/image destinations in the
three pages exist; consolidated/removed consumer assertions and the edited local anchors passed.

## Paths changed by this researcher

- `docs/deep-dive/coordinator-internals.md`
- `docs/deep-dive/review-merge.md`
- `docs/deep-dive/resilient-assembly-review.md`
- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
