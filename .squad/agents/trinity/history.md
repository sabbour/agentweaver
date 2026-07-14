# Trinity — History (Summarized)

## 2026-06-07 through 2026-06-18 — ARCHIVED SUMMARY

**Phase 0 (June 7-9):** Delivered CLI (Spectre.Console) and web (React 19 + Fluent 2) clients as thin pass-throughs over backend API. Built run timeline redesign with reducer-based turn grouping, streaming agent bubbles, safe Markdown rendering (react-markdown + rehype-sanitize), and live/replay cursor behavior. Polished UI: fixed accordion state, code span legibility (overrode foreign index.css), Markdown heading type-ramp, spinner settlement, and orphaned bubble closure. Validated at 49/49 web tests passing.

**Phase 1 (June 10-12):** Implemented artifact browser frontend (ArtifactBrowser.tsx, RunDetail integration, artifact pill status). Delivered full web parity for 003-projects: ProjectsPage, CreateProjectDialog (blank + device-flow GitHub), DeleteProjectDialog, project routes. Built web UI tier for Feature 005 (TeamPage, CastingWizardPage, SyncPanel) with 14 new ScaffolderApiClient methods; feature shipped at 112 Vitest tests passing. Windows LF normalization learned and applied.

**Phase 2 (June 17-18):** Added Feature 008 Coordinator Agent visualization: dynamic topology graph (React Flow + dagre) with live snapshot+delta reducer, node_type-driven shape mapping, drill-in child graphs, and inline steering (stop/redirect/amend). Extracted shared WorkflowGraphPanel renderer. Unified CoordinatorRunPage to use single graph view (eliminates three-separate-view problem). 24 new test coverage, committed with backward compatibility. Merged 6 decisions from inbox.

---

## 2026-06-19: Coordinator header dedup + AgentRail Phase 1/2 shipped

Removed redundant run ID and "Outcome spec" section headers from CoordinatorRunPage. Delivered AgentRail with per-agent click-to-filter on homepage Kanban board (agent_queues backend support, agent_rail-filter-active sentinel for test verification). Phase 1 and 2 completed and shipped together. Web suite: 239 passed, 0 failed.

📌 Team update (2026-06-22): Added SteerPanel controls to coordinator blocked/failed panel, restyled Kanban board, and landed topology graph zoom controls; web 267 passed.

## 2026-06-26T09:27:55-07:00: Project folder auto-fill feature

Created `slugify` helper; added `folderEdited` state to CreateBlankDialog and CreateFromGitHubDialog. Folder auto-fills from project Name (slugified) and stops syncing on manual edit; resets on dialog close. tsc -b --noEmit clean. Frontend suite 209 passed, 0 failed.

## 2026-06-26T11-44-02-07:00 — UI enhancements batch (3 commits)

**Cascade picker (0841a25):** Two-stage Account → Repository picker for CreateFromGitHubDialog. Stage 1 calls `GET /api/github/accounts` (user + orgs, avatar, badge). Stage 2 calls `GET /api/github/repos?account={login}` with freeform typing and auto-fill on selection. New `useGitHubData` hook. Tests: 3/3 + 8/8 passing.

**Zoom controls (5ac32916):** Optional `maxZoom` parameter on `useCtrlScrollZoom`. WorkflowRunPage and CoordinatorRunPage use max 200%; Kanban board capped at 100%. ZoomControls respects passed maxZoom. 72/72 tests passed.

**Workflows page layout:** Grouped into Active (default), Available, Invalid sections with badge rename ("Default" → "Active"). WorkflowsPage.test.tsx 5/5 passed.

## 2026-06-26T12:18:19-07:00 — Auth UX + collapsible tool headers

**Collapsible tool-cluster headers (b2dff07):** Intent text or agent-message bubble toggle preceding each tool cluster; defaults collapsed, auto-expands on errors.

**401 sign-in affordance (741b707):** ProjectSwitcher + ProjectGalleryPage show "Sign in with GitHub" on 401 errors (not generic disabled state); both route to /auth/github/authorize. 4 tests + 6 tests passing.

## 2026-06-27T02:44:51Z — Fleet deep-dive documentation effort complete

Coordinated parallel 12-agent fleet (background mode) to author deep-dive documentation. Trinity (trinity-4) contributed frontend.md deep-dive alongside 11 other agents. All 13 files verified complete; all 12 todos marked done; no source modifications. Cross-agent decision processing: 4 inbox entries merged. Scribe logs written. See .squad/log/2026-06-27T02-44-51Z-fleet-deep-dive-docs.md.


## 2026-06-28 — Docs IA restructure (docs reconciliation fleet)

Completed docs information-architecture restructure per @sabbour request. Renamed Guide→Getting Started, Experience→User Guide. Removed /architecture/ section entirely (rolled into deep-dive equivalents). Fixed 404 on User Guide nav link. Generated new deep-dive/sandboxed-execution.md (unique content, suggested for sidebar under "Execution & integration" group). VitePress config validated; build advanced to bundling. Decision logged in decisions.md:2026-06-28T08-19-00.
## 2026-06-28T02-48-00Z: trinity-10 — Dark-mode Mermaid SEQUENCE diagram text legibility fix

**Agent:** trinity-10 (opus-4.8, background)  
**Scope:** docs/.vitepress/theme/custom.css, docs/.vitepress/config.ts  
**Outcome:** COMPLETE  

---

## 2026-06-29: Full documentation pass (Trinity-docs background task — in progress)

**Timeline:** 2026-06-29T17:30–ongoing

**Scope:** Post-deployment security documentation synchronization

**Deliverables (in progress):**
- A2A bearer token path documentation (integration with per-run token registry)
- Dockerfile fix documentation (`--runtime linux-x64` runtime specification)
- oauth-key provisioning documentation (Key Vault secret wiring)
- Architecture docs updated with per-pod CSI SPC model
- Auth docs updated with per-user GitHub token scoping
- K8s deployment docs updated with three-pool layout and run-scoped resources

**Documentation Standards Applied:**
- Describe what the user can do right now with what is shipped
- No legacy details or references to removed behavior
- No marketing terms; clear technical description of capabilities
- Written at the level of the user who will read it (not the developer who built it)

**Key learnings:**
- Documentation must reflect actual deployed architecture (not planned/aspirational)
- Per-run resource isolation (SPC, SandboxTemplate, SandboxWarmPool) is user-visible in troubleshooting/inspection
- Token scoping model must be clearly documented for auth debugging (per-user, per-run, OAuth callback)

Fixed ~65 Mermaid SEQUENCE diagrams rendering with tiny text and dark-on-dark chrome in VitePress dark mode. Centralized approach: global flowchart config (useMaxWidth:false, htmlLabels:true, padding:12) + custom.css dark-mode overrides for messageText (#e6e6e6), loopText, labelText, labelBox, loopLine, actor-line, messageLine0/1, sequenceNumber. PowerShell replace-all: fontSize '14px' -> '15px' (65 occurrences). Light mode entirely unchanged. Commit: b83febe on sabbour/docs-reconciliation.

Decision merged: 2026-06-28T08-39-50 ✓



## 2026-06-28: Deploy smoke terminal finding

Coordinator + Link smoke found the stack live except for model credentials: custom Agentweaver GitHub App auth cannot mint Copilot-entitled tokens. Treat future AKS/UI failures after the first model turn as credential-provider blocked unless a Microsoft Foundry or blessed Copilot client credential path is in use.

## 2026-06-29T00:57:04-07:00 — Board layout: Active after Ready, Problems section

Reordered Kanban board columns to `Backlog → Ready → Active → Human Review → Done`. Extracted Problems column from the main scroll row into a separate labeled section ("Needs attention") below. `mainColumns` and `problemsEntry` memoized; `problemsSection`/`problemsSectionLabel`/`problemsColumns` style classes added. 2 KanbanBoard test assertions updated. All 12 board specs pass (8 KanbanBoard + 4 KanbanBoardDnd). Files changed: `columnMeta.ts`, `KanbanBoard.tsx`, `KanbanBoard.test.tsx`.


## 2026-06-29T14:30–17:00Z — Feature 019 frontend (Phase 4 delivery)

**Timeline:** Parallel to backend/security work

**Scope:** Token usage frontend visualization and display

**Deliverables:**

1. **TokenUsagePanel component (Feature 019, Phase 4):** Displays hierarchical AIC and token breakdowns.
   - **Hierarchy:** org → project → run → turn
   - **Display logic:** Pure presentation (no client aggregation)
   - **Backend contract:** Consumes /api/usage endpoints, renders flat/hierarchical views

2. **WatchPage live counter:** Real-time per-turn AIC counter on active run watch view.

3. **DashboardPage time-range section:** Aggregated token/AIC metrics with time-range filter for operator visibility.

4. **OverviewPage app-level section:** App-wide usage metrics (admin-gated, degrades gracefully on 403).

**Key learnings:**
- Pure presentation logic in UI (no aggregation) keeps components thin and testable.
- Hierarchical display helps operators understand cost allocation by project and run.
- Admin-gated sections must degrade gracefully (hide vs. error) on authorization failure.

**Testing & validation:**
- Build: 0 TypeScript errors
- Feature 019 frontend tests: all passing
- Component isolation: TokenUsagePanel tested independently

**Build:** 0 TypeScript errors.

## 2026-07-05T13:17:12-07:00 — Tool approval expiry UI

Trinity completed the frontend portion of #174 on PR #182: approval cards now react to server resolution events, represent timeout as `resolvedScope='expired'`, disable/collapse stale controls, and post decisions to the child run id with full request id. Web tests: 466. No merge yet; coordinator validates on staging first.


## 2026-07-05T20:40:00-07:00 — v0.7.11 release batch
Delivered slide-up run-tabs 404 fix, Messages redesign with sanitized markdown, and Overview redesign. Changes are in `release/v0.7.0`, image tag `v0.7.11`, and staging pending Ahmed validation.


## 2026-07-06T22:05:00Z — v0.7.12 outcome-spec gate UX

Delivered outcome-spec gate frontend fixes: draft panel remains visible during transient 404 polling with Drafting state; pre-draft run failure is terminal; Confirm has pending/success/409/error states and double-submit guard. Commits `a7e7645`, `de4bed6`; merged to `release/v0.7.0` and deployed to staging.


## 2026-07-06T07-29-39Z — v0.8.0 staging release

Trinity's conversational browser TUI/console work (#50, commit fdb2ad5) shipped in the v0.8.0 staging wave. The #50/#196 merge caused a timeline reducer `child_approval` case-shadowing regression that was fixed before deploy; 483 web tests passed. Staging is healthy; do not close #50 or push/merge until Ahmed validates.


## 2026-07-06 v0.9.0 staging wave
- Delivered the console TUI refresh and the calmer tool-row UI improvements.

## 2026-07-07T00:00:00Z — v0.9.2 staging ship

Trinity shipped the frontend Review now fix (`388b993`): the Assembly and review CTA now opens the artifacts panel directly with `setArtifactsPanelOpen(true)`, and dead `reviewRef` / `scrollToReview` plumbing was removed. The fix is included in v0.9.2 on staging AKS.

## 2026-07-11T00:00:00Z — RAI session panel and coordinator activity UI fixes

Fixed two frontend-only run-panel defects for the staging wave: RAI Reviewer panels filter serialized coordinator work-plan JSON, and coordinator activity updates coalesce into one collapsed activity control instead of many empty rows. The changes rode with Link's local-only v0.9.19-rc1 staging release.


## 2026-07-13T23:59:00-07:00 — #215 validation
Validated #215 as stale and closed.

## 2026-07-14T02:35:00-07:00 — Batch merge: #176 probes, #250 fix, #307 capacity overrun
Scribe merged inbox notes: #176 workflow editor probes underway; #250 fixed locally but staging E2E is blocked pending separate infra issue; #307 AgentHost capacity overrun filed as distinct from #242.

## 2026-07-14T02:35:00-07:00 — #250 live E2E validation, recommend closing
Fresh-session revalidation confirms #250 (token-breakdown case-insensitive grouping) fix is deployed in v0.9.49-rc1 and live-reachable; prior 401 was a stale/expired bearer token, not a deploy or endpoint issue. Unit test + 6 live run checks confirm no duplicate-cased agent buckets. Recommend closing #250.

## 2026-07-14T03:05:00-07:00 — #186 workflow editor gate palette already implemented, hardened tests added
Found #186's full acceptance criteria already shipped via commit 8dd8dbb3 (PR #190, merged 2026-07-06) bundled with #187; the GitHub issue was never closed despite the code satisfying every box. Added 3 new component tests covering the add-menu, unrouted-gate warning banner, and read-only legacy tail-node notice. Build and 692/693 tests pass (1 pre-existing unrelated flake). Recommend closing #186 pending peer review.

## 2026-07-14T10:15:00-07:00
No new Trinity (base, non-2 instance) write-up landed this batch; #186 gate-palette work from pass 2 remains the most recent status. Flagging for size-gate check this pass.


## 2026-07-14T10:15:00-07:00 (late arrival)
#306 phantom-edge fix confirmed already on main (commit ea090ab7) with passing regression test; no code change needed, recommends a final live click-through before closing.


## 2026-07-14T11:05:00-07:00
Investigated #271 retry-resume gap; confirmed real, deferred fixing it (existing epic-#293 architecture proposal, active file overlap with Tank's #242 work). No code changes.

