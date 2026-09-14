# Experience revamp — independent orchestration research

## Scope and method

Researcher 3 of the parent's three bounded research threads. Distinct question: orchestration start modes, atomic Ready pickup, review/merge truth, and observability. All repository work was confined to `.worktrees/drawio-diagram-authoring`. No nested agents, product/config/test edits, inventory changes, shared-asset edits, captures, or commits.

Read `CONTRIBUTING.md`, `.github/skills/docs-diagram-pitch/SKILL.md`, `.github/skills/docs-diagram-iterate/SKILL.md`, and the `plan-experience.json` / `experience.json` audit reports directly. Existing uncommitted changes were present before this task, including JSON-to-draw.io migration wording in several owned pages; no git reset, checkout, stash, or history changes were made. Audit evidence guided investigation but did not override current implementation. Legacy diagrams and screenshots were not used as factual sources.

Only these documents and this report were edited:

- `docs/experience/coordinator-orchestration.md`
- `docs/experience/resilient-assembly-review.md`
- `docs/experience/review-workspace-merge.md`
- `docs/experience/runs-board-watch.md`
- `docs/experience/workflows-backlog.md`
- `docs/experience/token-usage-monitoring.md`
- `docs/experience/transaction-traces.md`
- `docs/experience/unified-steering.md`

## Evidence-backed conclusions

| Concern | Current implementation truth | Grounding |
|---|---|---|
| Interactive starts | Define Outcome and Direct are different start modes. Only the former waits for human outcome confirmation. Later review/tool/assembly/merge gates remain. | `apps/web/src/components/StartOrchestrationDialog.tsx:93–103`, `:145–148`, `:230–247` |
| MCP defaults | `coordinator_start` defaults to `defineOutcome` and accepts `direct`; `run_task` defaults to direct. An assistant must not universally require a separate confirmation call. | `apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14–22`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:137–147` |
| Unattended start | Won pickup activates an already reserved coordinator and schedules confirmation attributed to `CapturedBy`; it is not a second interactive start. | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:231–249` |
| Ready selection | Heartbeat checks active project/workspace availability, reads top-N Ready candidates using project settings, and passes them to pickup. | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:101–121` |
| Atomic boundary | Claim, run row, backlog-pickup origin, and approval-policy snapshot are transactional. An unmet dependency or lost claim creates no orphan run; inactive project rolls back. | `apps/Agentweaver.Api/Infrastructure/Ef/EfBacklogTaskStore.cs:370–459`; SQLite equivalent `apps/Agentweaver.Api/Infrastructure/SqliteBacklogTaskStore.cs:547–682` |
| Post-commit failure | Activation failures attempt `Failed/coordinator_start_failed`; task stays Claimed, never silently Ready. Team/provider failures may reserve a failed run before activation. | `CoordinatorPickupService.cs:148–180`, `:202–229`, `:251–268` |
| Board presentation | Six logical buckets, four main lanes, and a separate Human Review/Problems attention section. Intake drag is restricted to Backlog/Ready. | `apps/web/src/components/board/KanbanBoard.tsx:253–255`, `:308–309`, `:553–569` |
| Board status precedence | Failed/Declined/MergeFailed → Problems; Completed/Merged/AssembleReady → Done; AwaitingReview → Human Review. Otherwise use plan status/stage, default Active. Merging does not have its own column. | `apps/Agentweaver.Api/Runs/WorkflowStageProjector.cs:53–79` |
| Conditional retry | Eligible coordinator retry may resume the same id; fresh retry creates linked run/history. UI follows returned id. | `apps/web/src/components/board/RunCard.tsx:170–177`; `tests/Agentweaver.Tests/Coordinator/RunRetryTests.cs:58`, `:255`, `:336`, `:491` |
| Current run navigation | Orchestration and selected-task Agent session surfaces are current. Do not revive standalone Workflow/Execution pages or obsolete modal guidance. | `apps/web/src/App.tsx:109`, `:125`; `apps/web/src/pages/CoordinatorRunPage.tsx:5011` |
| Pod attribution | Each topology card reads its own `executionPodName`, not an unrelated child/API fallback. | `apps/web/src/components/CoordinatorTopologyGraph.tsx:293` |
| Review authorization | Review endpoint requires run access at Contributor level, validates awaiting-review, and consumes a pending decision. It does not establish a universal different-human-from-author identity rule. Matching terminal decisions can be idempotent. | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:875–889`, `:985` |
| MCP review limit | `run_review` sends only `{ approved }` to `/review`. It has no feedback/request-changes parameter. | `apps/Agentweaver.Mcp/Tools/RunTools.cs:276–299` |
| Candidate integrity | Worktree candidate hash must equal approved hash before merge. Hash identity is not a promise that the final destination tree is byte-identical to the candidate. | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1902–1951` |
| Local repository mutation | Repository lock and run CAS precede merge. Checked-out target can auto-commit tracked changes; three-way merge preserves target-side Squad state specially. Ref-only merge is used when the target is not checked out. | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:33–61`, `:84–104`; `WorktreeManager.cs:1968–2059` |
| Merge failure/retention | MergeCoordinator's conflict branch preserves the worktree; terminal workflow cleanup may subsequently remove it. Do not promise universal retention. | `MergeCoordinator.cs:146–160`; `tests/Agentweaver.Tests/ReviewEndpointHybridMergeTests.cs:658` |
| Normal versus collective revisions | Normal `/request-changes` uses `Runs:MaxRevisions` default 10, then CAS/revision audit/restart. Human collective-assembly rounds are uncapped and reset the autonomous budget. | `RunEndpoints.cs:1319–1330`, `:1367–1437`; `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2266–2285`; `CoordinatorSteeringDecider.cs:440–453` |
| Escalation | Exhausted autonomous steering escalates reviewable assembly to a durable human review request, carrying reason and accumulated feedback. It is not necessarily blocked/terminal. | `CoordinatorAssemblyService.cs:2374–2386`, `:2875–2955` |
| Review-gate steering | `send` is advisory/applied without resetting or waking the gate; redirect/amend submit request-changes, with relayed or cross-replica deferred status. | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringService.cs:1087–1178`, `:1235–1252` |
| Resource retention | Automated Build & Test/Rubberduck request-changes can retain the gate pod and detached worktree. Suspension does not always release resources. | `CoordinatorAssemblyService.cs:2117`, `:2693`; conditional normal suspension in `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:394` |
| Preview separation | Deterministic PreviewStep runs after approved OR request-changes Build & Test, skips declined, and isolates failures from review. Gateway, not API data-plane proxy, carries preview traffic. | `CoordinatorAssemblyService.cs:223–264`; `apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:34–38`; `tests/Agentweaver.Tests/Preview/PreviewStepDeclinedSkipTests.cs:19`, `:42–66` |
| Lock cleanup correction beyond audit | Current stale index-lock cleanup uses age as its sole guard, not a live-git-process ownership test. | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1178–1232` |
| Usage correction beyond audit | Board RunCard currently has no CostChip/use-run-usage request. CostChip delegates to costChipLabel; AiCredits shares formatting. Dashboard uses selected-range project metrics, not per-run-usage fan-out. | `apps/web/src/components/board/RunCard.tsx:1–14`, `:146–210`; `CostChip.tsx:13`; `AiCredits.tsx:109`; `apps/web/src/pages/DashboardPage.tsx:675–688` |
| Overview scope | AI usage & performance rolls up recent projects shown on the page, not every possible project/billing record. | `apps/web/src/pages/OverviewPage.tsx:299–303`, `:382` |
| Trace hierarchy | Parent links build a forest; presentation-only LLM leaves can be synthesized when an agent span carries model usage. Tool details correlate persisted events by callId. | `apps/web/src/components/runs/traceTree.ts:102–120`, `:171–184`, `:271–277` |
| Trace identity/errors | Optional typed `sessionId` exists. Missing dimensions are Not recorded. queryError is different from empty spans. Failed tool attempts can be recovered without terminal run failure. | `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:137`, `:175`; `apps/web/src/components/runs/TransactionTracePanel.tsx:511`, `:743`, `:768–771`, `:1153–1167` |
| Trace cost scope | Sum loaded model leaves, not arbitrary agent/tool raw costs; pagination/incomplete telemetry means this is not guaranteed identical to the durable whole-run aggregate. | `traceTree.ts:308–327`; `TransactionTracePanel.tsx:1081`, `:1110–1111` |

File basenames in this table refer to the fully qualified path established in the same row or previous rows.

## Audit concept dispositions applied

| Audit concept(s) | Consumer/document change |
|---|---|
| `experience-coordinator-start-confirm` | Reused declared `canonical-coordinator-journey.png`; updated alt/provenance; qualified confirmation throughout start, mental-model, MCP, and limits sections. |
| `experience-coordinator-live-inspection` | Kept interaction prose; removed obsolete child modal/standalone workflow guidance; selected-task panel, per-node pod attribution, and conditional retry are explicit. |
| `experience-coordinator-review-feedback` | Linked existing resilient-review canonical through its deep dive; distinguished automated retained-resource revisions from human decisions. |
| `experience-coordinator-preview-delivery` | Linked existing live-preview canonical through its deep dive; PreviewStep, verdict alternatives, and nonblocking failure are explicit. |
| `experience-orchestrations-list-capture` | Removed placeholder embed/capture-brief caption; retained source-grounded navigation and action prose. |
| `experience-resilient-review-loop` | Linked existing state canonical; retained escalation, scoped rotation, single-eligible-author fallback, accumulated feedback, and uncapped human rounds. |
| `experience-resilient-review-steering` | Retained advisory send versus redirect/amend and relayed/deferred semantics. |
| `experience-resilient-review-capture` | Removed explicitly placeholder image and fictional capture brief per task override. Also removed unsupported “Why are you seeing this?” named-panel assertion; described durable reason/events instead. |
| `experience-reviewed-content-merge` | Retained fig1; clarified candidate versus final merge content and authenticated-review versus independent-human identity. |
| `experience-mcp-review-sequence` | Removed fig3 embed and provenance; replaced with in-page link to fig1 and adjacent six-row MCP tool/limit table. |
| `experience-workspace-read-journey` | Retained fig2 with draw.io provenance; ref selection is read-only, not checkout/edit/merge. |
| `experience-review-artifact-surfaces` | Relocated prose to orchestration/Agent session artifacts; retained files/diff/large/binary/empty states; normal cap versus collective rounds explicit. |
| `experience-review-workspace-captures` | Removed three placeholder embeds and their capture-brief captions. |
| `experience-board-lifecycle` | Replaced local fig1 with declared shared `canonical-board-lifecycle.png`; fixed four-lane plus attention layout and alt text. |
| `experience-inline-run-state` | Removed duplicate inline Mermaid lifecycle; shared canonical plus precise status table/projection prose are now authoritative. |
| `experience-live-watch-replay` | Linked existing durable stream canonical page; kept replay/tail/event-shape guidance and separated event replay from A2A re-execution. |
| `experience-run-management` | Retained tables; corrected conditional retry, Declined → Problems, Merging projection, and archive versus cancel/delete. |
| `experience-board-capture` | Retained real existing board capture with explicit four-main-lane crop caption; no claim to show the attention section. |
| `experience-run-inspection-captures` | Removed three placeholder embeds; replaced obsolete controls/watch/preview-caption claims with current panel and Gateway prose. |
| `experience-usage-scope-and-units` | Retained tables, rebuilt from current code: run aggregates versus selected-range metrics, recent-project Overview scope, formatting delegation, no current board cost chip. |
| `experience-usage-empty-captures` | Removed three 1×1 placeholders and captions; no claim of inspected widgets. |
| `experience-observability-captures` | Removed Agents placeholder and inconsistent Overview example; explained why missing/zero/partial telemetry must not be interpreted as coherent healthy totals. |
| `experience-trace-hierarchy` | Retained live-trace reading prose; optional recorded session identity, parent/model/tool relationships, and callId correlation explicit. |
| `experience-trace-truthful-empty-states` | Kept Not recorded/queryError distinctions; refreshed source table and recovered-tool versus terminal-failure guidance; loaded-trace cost scope clarified. |
| `experience-trace-captures` | Removed both placeholders/capture briefs; retained real route and Preview trace navigation. |
| `experience-steering-visible-decision` | Linked existing unified-steering canonical via deep dive; retained four-effects table. |
| `experience-steering-budget-recovery` | Qualified 3/6 as autonomous convergence-window budgets; reviewable assembly escalates to human review and human feedback resets budget. |
| `experience-workflow-intake-lifecycle` | Continued shared board reuse with draw.io provenance; six logical buckets versus physical layout corrected. |
| `experience-workflow-authoring` | Continued shared draft-before-save canonical reuse with draw.io provenance. Current YAML/visual editing/generation confirmation remain. |
| `experience-ready-pickup` | Retained fig3; atomic claim+reservation+policy snapshot, lost/unavailable alternatives, failed claimed activation, and no silent requeue explicit. |
| `experience-workflow-backlog-control` | Retained operational tables; definition graph versus live topology and intake ranking versus coordinator progression explicit. |
| `experience-workflow-backlog-captures` | Removed four placeholder embeds/capture briefs; retained honest view-graph, drag restriction, and preview-before-create instructions. |

## Compact content model — experience-review-workspace-merge-fig1

**Takeaway:** inspect a candidate, choose approve/change/decline, and integrate only after server/content checks. Approval identifies the candidate, not an immutable final destination tree.

**Stable artifacts:** `docs/diagrams/experience-review-workspace-merge-fig1.png`; editable source `docs/diagrams/src/experience-review-workspace-merge-fig1.drawio`. Retain the identity. Fig3 is consolidated into this consumer's adjacent MCP table, not redrawn as another sequence.

**Proposed composition:** one A5 landscape page, left-to-right main flow. Upper/main row is candidate → inspection → decision → guarded merge → merged. Lower row contains revision return, decline, and blocked/conflict outcomes. Native symbols express review decision and merge guards; custom Agentweaver cards express run/candidate/review state. No cloud/deployment symbols are justified.

| Node | Classification | Evidence |
|---|---|---|
| R1 Candidate run worktree + recorded tree hash | `custom:agentweaver` | `apps/Agentweaver.Api/Git/WorktreeManager.cs:685–701`; `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:372–386` |
| R2 Inspect Changes / Files / timeline | `custom:agentweaver` | `apps/web/src/components/ArtifactBrowser.tsx:673–707`; `apps/Agentweaver.Mcp/Tools/RunTools.cs:364–389` |
| R3 Authorized review decision | `native:flowchart` decision | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:875–930` |
| R4 Revise preserved run worktree | `custom:agentweaver` | `RunEndpoints.cs:1367–1437`; normal versus collective distinction `CoordinatorAssemblyService.cs:2279` |
| R5 Guarded local merge | `native:flowchart` process | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:84–104`; `WorktreeManager.cs:1902–1951` |
| R6 Merged destination/history | `custom:agentweaver` | `MergeCoordinator.cs:106–137`; `WorktreeManager.cs:2038–2059` |
| R7 Declined | `custom:agentweaver` | `RunEndpoints.cs:978–980` |
| R8 Blocked/review retry or merge conflict/failure | `custom:agentweaver` | `MergeCoordinator.cs:139–160`; `RunEndpoints.cs:1239–1250` |

| Connector (`source -> target: verb`) | Evidence |
|---|---|
| R1 -> R2: expose candidate artifacts/hash | `RunWatchLoopService.cs:378–386`; `RunTools.cs:364–389` |
| R2 -> R3: inspect, then choose | `ArtifactBrowser.tsx` review controls; `RunTools.cs:276–299` |
| R3 -> R5: approve candidate | `RunEndpoints.cs:925–930`, `:962–968`; `MergeCoordinator.cs:84` |
| R3 -> R4: request changes with feedback | `RunEndpoints.cs:1319–1437` |
| R4 -> R1: produce a new candidate for review | `RunEndpoints.cs:1431`; `RunWatchLoopService.cs:372–386` |
| R3 -> R7: decline; do not merge | `RunEndpoints.cs:978–980` |
| R5 -> R6: content/merge guards pass | `WorktreeManager.cs:1916–1951`, `:2038–2059`; `MergeCoordinator.cs:106–137` |
| R5 -> R8: busy/unsafe/conflicting/mismatched candidate | `MergeCoordinator.cs:84–92`, `:139–160`; `WorktreeManager.cs:1916–1919` |

**Boundaries/annotations:** reviewer client versus server-authoritative gate versus local repository mutation. MCP names belong in adjacent prose/table, not extra actor lanes. Change is web feedback-bearing; run_review is binary. A changed candidate needs re-review. Repository-busy/blocked and terminal conflict are not one unconditional retry arrow. Final destination can include newer target content and special Squad-state handling. Ordinary revision cap is 10 by default; collective human rounds have no cap. Do not draw remote PR or push nodes, blanket different-human enforcement, or guaranteed worktree retention.

## Compact content model — experience-review-workspace-merge-fig2

**Takeaway:** choose an allowed project ref, list its files, and read content without mutating the repository.

**Stable artifacts:** `docs/diagrams/experience-review-workspace-merge-fig2.png`; `docs/diagrams/src/experience-review-workspace-merge-fig2.drawio`. Retain separately because this is browsing, not merge/review.

**Proposed composition:** one A5 landscape page, left-to-right reading/navigation. Client/navigation group above a read-only API/store boundary. No edit or merge arrow. Ref omission defaults to base branch; ref change clears selected file. File rendering is source/Markdown preview with honest binary/too-large/missing states.

| Node | Classification | Evidence |
|---|---|---|
| W1 Workspace page / authorized MCP reader | `custom:agentweaver` | `apps/web/src/pages/WorkspacePage.tsx:262–297`; `apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:13–24` |
| W2 Available refs | `native:flowchart` data | `apps/Agentweaver.Api/Endpoints/ProjectWorkspaceEndpoints.cs:18–33` |
| W3 Choose base / run / assembly ref | `native:flowchart` process | `WorkspacePage.tsx:277–282`; `tests/Agentweaver.Tests/Projects/ProjectWorkspaceServiceTests.cs:121–155` |
| W4 File tree at selected ref | `custom:agentweaver` | `WorkspaceTools.cs:29–42`; `ProjectWorkspaceEndpoints.cs:38–59` |
| W5 Select relative file path | `native:flowchart` process | `WorkspaceTools.cs:50–62`; `WorkspacePage.tsx:297` |
| W6 Read-only content / source / preview | `custom:agentweaver` | `WorkspaceTools.cs:48–63`; `ProjectWorkspaceEndpoints.cs:64–102` |

| Connector | Evidence |
|---|---|
| W1 -> W2: list_project_workspace_refs | `WorkspaceTools.cs:13–24` |
| W2 -> W3: choose available ref | `WorkspacePage.tsx:277–282`, `:372–380` |
| W3 -> W4: list_project_workspace(ref) | `WorkspaceTools.cs:29–42` |
| W4 -> W5: choose listed file | `WorkspacePage.tsx:297`; `WorkspaceTools.cs:50–52` |
| W5 -> W6: get_project_workspace_file(path, ref) | `WorkspaceTools.cs:48–63` |

**Boundaries/annotations:** all three tools use GET. Ref selection is not a git checkout operation or frozen content approval. Unknown refs/files can return explicit 404; invalid paths return 400. Import to backlog is a separate preview-before-create action, not repository editing and not part of this core figure. Native document/data symbols are sufficient; no technology logos needed.

## Compact content model — experience-workflows-backlog-fig3

**Takeaway:** only a won atomic Ready claim/reservation activates one unattended coordinator; losing/unavailable/activation-failure outcomes are different.

**Stable artifacts:** `docs/diagrams/experience-workflows-backlog-fig3.png`; `docs/diagrams/src/experience-workflows-backlog-fig3.drawio`. Retain; Operations is an external consumer managed by its assigned researcher/parent.

**Proposed composition:** one A5 landscape page. Left-to-right main lane: Ready → heartbeat selection → atomic transaction → activation → coordinator. Transaction result alternatives branch below it; activation failure is a separate branch after commit. An outer data boundary surrounds claim + run reservation + approval-policy snapshot, not coordinator activation.

| Node | Classification | Evidence |
|---|---|---|
| P1 Ranked Ready tasks | `custom:agentweaver` | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:110–115` |
| P2 Heartbeat eligibility/top-N selection | `custom:agentweaver` | `CoordinatorHeartbeatService.cs:101–121` |
| P3 Atomic claim + run + policy snapshot | `native:database` framed transaction | `apps/Agentweaver.Api/Infrastructure/Ef/EfBacklogTaskStore.cs:378–459` |
| P4 Lost: no new reservation | `native:flowchart` outcome | `EfBacklogTaskStore.cs:382–401`; `CoordinatorPickupService.cs:186–195` |
| P5 Unavailable: preserve Ready/rank | `native:flowchart` outcome | `CoordinatorHeartbeatService.cs:101–107`; `EfBacklogTaskStore.cs:413–417` |
| P6 Activate won reservation | `custom:agentweaver` | `CoordinatorPickupService.cs:231–249` |
| P7 Unattended coordinator + attributed confirmation | `custom:agentweaver` | `CoordinatorPickupService.cs:240–245` |
| P8 Claimed failed run / visible reason | `custom:agentweaver` | `CoordinatorPickupService.cs:148–180`, `:202–229`, `:251–268` |

| Connector | Evidence |
|---|---|
| P1 -> P2: consider deterministic Ready candidates | `CoordinatorHeartbeatService.cs:110–121` |
| P2 -> P5: inactive or missing workspace; do not claim | `CoordinatorHeartbeatService.cs:101–107` |
| P2 -> P3: attempt task-scoped reservation | `CoordinatorPickupService.cs:183–186` |
| P3 -> P4: dependency unsatisfied or claim lost; rollback | `EfBacklogTaskStore.cs:382–401` |
| P3 -> P5: project unavailable; rollback | `EfBacklogTaskStore.cs:413–417` |
| P3 -> P6: won; commit before activation | `EfBacklogTaskStore.cs:457–459`; `CoordinatorPickupService.cs:231` |
| P6 -> P7: start reserved coordinator, schedule auto-confirm | `CoordinatorPickupService.cs:240–249` |
| P6 -> P8: activation fails; attempt Failed terminalization | `CoordinatorPickupService.cs:251–268` |
| P3 -> P8: won reservation already records preflight failure | `CoordinatorPickupService.cs:148–180`, `:202–212` |

**Required qualifications:** Lost does not always mean “still Ready”: another worker may already own it. Never loop failed activation back into Ready. Team/provider preflight refusal can be a committed failed run. If terminalization itself fails, a warning is logged; do not promise infallible failure recording. Exactly-once claim/reservation is not exactly-once model/tool execution. `MaxReadyPerHeartbeat` limits candidates per tick, not all-time concurrency. Claim-time settings are persisted snapshots rather than mutable global settings applied retroactively.

## Tests/configuration independently inspected

- `tests/Agentweaver.Tests/Backlog/BacklogClaimReserveTests.cs:32–96`: eight concurrent contenders, one winner, one run, losers persist nothing, policy snapshot asserted. `:141` second claim loses; `:173` deleting project preserves Ready; `:250` failed terminal reservation persists reason/timestamp atomically.
- `tests/Agentweaver.Tests/Coordinator/CoordinatorPickupRunIdTests.cs:52`, `:187`: run-id addressability and teamless claimed failure.
- `tests/Agentweaver.Tests/Coordinator/RunRetryTests.cs:58`, `:153`, `:255`, `:336`, `:491`: fresh, preserved Direct mode, eligible in-place modes, and normal fresh retry.
- `tests/Agentweaver.Tests/ReviewEndpointHybridMergeTests.cs:61`, `:102`, `:182`, `:623`, `:658`, `:763–789`, `:1013`: clean merge, tracked auto-commit, true conflict, ref-only, cleanup caveat, hash-mismatch 409, fail-closed missing hash. Test name alone is insufficient: the mismatch test's actual assertion is 409.
- `tests/Agentweaver.Tests/Coordinator/CoordinatorAssemblyReviewPersistenceTests.cs:31`, `:73`: replace prior gate decision and reject decisions outside persisted InReview/review stage.
- `tests/Agentweaver.Tests/Projects/ProjectWorkspaceServiceTests.cs:97`, `:121–155`: base/run refs and assembly branch browsing.
- `tests/Agentweaver.Tests/Preview/PreviewStepDeclinedSkipTests.cs:19`, `:42–66`: preview cancellation boundary and verdict conditions.
- `apps/web/src/__tests__/TransactionTracePanel.test.tsx:22`, `:35`, `:88`, `:129`, `:155–199`: parent links, synthetic leaf, call/result/error pairing, non-double-counted cost.
- `apps/web/src/__tests__/TransactionTraceDetail.test.tsx:119`, `:132`, `:155`, `:233`, `:325`, `:355`, `:374`: unavailable telemetry, paginated traces, retry cursor, recovered failure, terminal failure, bounded redaction.
- Configuration read in implementation: `Runs:MaxRevisions=10` (`RunEndpoints.cs:1320`), stale lock threshold default 15 seconds (`WorktreeManager.cs:73`, `:1185`), autonomous plan iterations default 6 (`CoordinatorSteeringDecider.cs:83–84`).

These tests were read as grounding, not executed or claimed green. This is a docs-only change.

## Validation and remaining handoff

- `git diff --check` on all eight owned pages: passed.
- Final read-only assertions passed for owned-page local Markdown link targets, removed consumers, absence of duplicate inline state diagrams/JSON provenance, and existence of this saved report.
- `npm run docs:build` from the assigned worktree: completed with exit code **0**. Existing build output included PromQL highlighter fallback and third-party `use client` bundling warnings.
- Scoped scan: no owned consumer references to removed `experience-review-workspace-merge-fig3` or `experience-runs-board-watch-fig1`; no duplicate `stateDiagram-v2`; no JSON diagram provenance; no screenshot capture-brief captions. The only surviving screenshot embed is the explicitly cropped real project-board capture.
- Read-only inventory query confirmed the three surviving local stable PNG identities and shared board/coordinator identities. No inventory ownership/disposition writes were made because the task forbids them.
- Local `.drawio` sources for the three researched figures were not present at the final source-availability probe. Provenance now names the requested editable paths; the parent owns authoring/export/publication. No PNG has been rendered, inspected, approved, or fabricated by this researcher.
- Shared canonical sources/PNGs remain untouched. Relevant external owners must preserve the corrected start alternatives, logical board buckets, durable replay distinction, resilient review escalation, and decoupled preview semantics at the already declared stable paths.
- Parent remains responsible for draw.io pitch, meaningful 9× first-pass XML gate, four-plus per-pass PNG inspections, final every-arrow trace, canonical promotion, inventory state, and final integrated validation. This report is a source-backed content model, not visual acceptance.
