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
