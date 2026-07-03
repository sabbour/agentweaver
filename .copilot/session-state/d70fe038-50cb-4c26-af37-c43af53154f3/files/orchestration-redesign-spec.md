# Orchestration Page Redesign — UX / Component Spec

**Author:** Trinity (Frontend) · **For:** Ahmed Sabbour · **Date:** 2026-07-02
**Reference screenshots:** `workflow view 1.png` (pipeline view), `workflow view 2.png` (step detail panel)
**Tracking issues:** #160 (View 1), #161 (View 2), #162 (Expand-button cleanup), #159 (agentic preview)

---

## 0. Current implementation (what exists today)

Everything lives in one very large page component:

- **`apps/web/src/pages/CoordinatorRunPage.tsx`** (~2600 lines) — the orchestration page.
  - Header: breadcrumb + run id + status badge + "Preview Sandbox" dialog (`previewDialogOpen`).
  - `graphBand` section (styles at ~L783): a **`Title3` "Coordinator Graph"** heading, a
    descriptive `hint` paragraph ("Live view of the coordinator…", L2052), and a **steer bar**
    (`steerBar`, L2059) with Send/Redirect/Amend/Stop. **These three are the elements Ahmed crossed
    out in red and wants removed from the pipeline view.**
  - The pipeline itself is a **React Flow** canvas (`<ReactFlow>` at L2119) using
    `coordinatorNodeTypes = { ...workflowNodeTypes, subtask: SubtaskNode }` (L743).
  - **`SubtaskNode`** (L577) is the step card. It renders `CostChip` (AICs/tokens), `StatusBadge`,
    `PodIndicator`, agent avatar/name/role/model, and — critically — the **Expand/Collapse pipeline**
    button (L681–688) plus an inline vertical child-pipeline strip (`ChildStepRow`, L521).
  - Below the graph: an `AgentRail` band, then a two-column layout with `OutcomeSpecPanel` and the
    coordinator's own **`Timeline`** (L2418) — the live session view (tool calls, messages, reasoning).
  - `viewAssemblyExecution` (L1785) is the current "click a card" hook: it either opens a child-run
    **`Dialog`** (`viewRunId`, L2435) or scrolls to the review timeline. This is the seam we repurpose
    for the View 2 slide-in panel.
- **`apps/web/src/components/WorkflowGraphPanel.tsx`** — exports `workflowNodeTypes`, `StatusBadge`,
  `ElapsedTimer`, `useNodeStyles`, and the shared contexts (`ExecutionModalContext`,
  `CoordinatorSessionContext`, `BrowseFilesContext`, `ActiveEdgeContext`). Node card chrome + status
  styling live here — this is where the **top status bar** and rename of card affordances belong.
- **`apps/web/src/components/WorkflowStepCard.tsx`** — the step card used by the (non-coordinator)
  workflow run page; also carries an Expand affordance to clean up (#162).
- Reusable pieces for View 2: **`Timeline.tsx`** (session), **`ToolCallCard.tsx`**, **`TurnGroup.tsx`**,
  **`DiffViewer.tsx`**, **`FileViewer.tsx`/`FileViewerModal.tsx`**, `useTimelineItems`.
- Preview contract already exists (relevant to #159): agent calls the MCP tool
  **`start_preview(port=PORT)`** (see `RunOrchestrator.BrowserPreviewCapability`), routed through
  `AgentPreviewGate` → `SandboxEndpoints` port-forward. There is **no** `agents.yaml` port config.

> Terminology note: the "Release / Stages" labels in Screenshot 1 are from the **GitHub Actions
> reference**. In agentweaver the equivalents are the coordinator card ("Release" → **Coordinator**)
> and the step grid ("Stages" → **Steps**). Today neither panel is explicitly labelled; the whole area
> is just "Coordinator Graph".

---

## 1. View 1 — Pipeline layout

### 1.1 Component breakdown

| Component | Change | Notes |
|---|---|---|
| `CoordinatorRunPage` | **Modify** | Remove `Title3 "Coordinator Graph"`, the `hint` paragraph, and the inline `steerBar`/`SteeringLegend` from the `graphBand`. Introduce the two-panel layout (Coordinator \| Steps). Steering moves to an overflow/"…" menu on the header or is dropped from this band per Ahmed's red-X. |
| `CoordinatorPanel` *(new)* | **Create** | Left panel titled **"Coordinator"** (was "Release"). Dashed-border card showing trigger type ("Manually triggered"), triggering user (`AgentAvatar` + name), timestamp, project, and an **Artifacts** section (package icon + artifact name/version/tag). Sources from run metadata. Visually separated from Steps; **no connector arrow** to the first step. |
| `StepsPanel` *(new wrapper)* | **Create** | Right panel titled **"Steps"** (was "Stages"). Hosts the React Flow canvas (kept) but with the new heading and horizontal-scroll affordance (▶). |
| `SubtaskNode` (step card) | **Modify** | Add **top colored status bar** (green=succeeded, blue=in-progress, gray=not-started, red=failed). **Remove** Expand/Collapse pipeline button + inline `ChildStepRow` strip (#162). Card becomes **clickable** → opens View 2. Keep: AICs (`CostChip`), **elapsed time** (`ElapsedTimer`), **pod name** (`PodIndicator`), step/agent name, status label, timestamps, warnings count. **In-progress cards show state + elapsed time but NO task count** (we can't know it — drop the "2/2 tasks" / progress-ring semantics from the reference). |
| `StatusBar` *(new, in WorkflowGraphPanel)* | **Create** | Thin bar across card top; color from `StepStatus`. Reuse the existing `StepStatus`→color mapping used by `StatusBadge`. |
| Edges | **Keep** | React Flow arrow connectors already express left-to-right flow; keep horizontal (`LR`) layout + the horizontal scroll. |

### 1.2 Data requirements

- **Coordinator panel:** trigger type, triggering user (display name + avatar), created-at timestamp,
  project id/name, artifacts list (name, version, tag). Most already available on the run/work-plan
  response; artifacts may need a field if not already surfaced (confirm with API — likely
  `WorkPlanResponse` / run metadata).
- **Step cards:** already have `topoStatus`, `label`, `agent`, `agentRole`, `model`,
  `executionPodName`, `totalNanoAiu`/`totalTokens`, elapsed timing, warnings. No new fields required
  for the top status bar (derived from `topoStatus`). Warnings count field should be confirmed to exist;
  if not, omit until backend provides it.

### 1.3 State model

- Card click sets page-level `selectedStepId` (new state on `CoordinatorRunPage`) → drives View 2
  panel `open`. Replaces the current `viewAssemblyExecution` scroll/child-dialog behavior.
- Remove `CoordExpandContext` / `expanded` set (inline expansion is deleted with #162).
- Status bar is pure-derived (`topoStatus` → color); no new state.

### 1.4 Responsive behavior

- Steps grid scrolls **horizontally** inside `StepsPanel` (▶ affordance). Below ~900px the Coordinator
  panel stacks **above** the Steps panel (single column). React Flow `fitView` retained; keep
  `panOnDrag`, disable zoom-on-scroll (already configured).

### 1.5 Interactions not covered by #160 as filed

- **Steering removal fallback:** #160 says remove the helper text but does not say where steering goes.
  Recommend moving Send/Redirect/Amend/Stop into a header overflow menu (or a per-Coordinator-card
  action), so the capability is not lost. Needs a decision — flagged in the issue update.
- **Warnings count** field availability needs backend confirmation.

---

## 2. View 2 — Step detail slide-in panel

### 2.1 Component breakdown

| Component | Change | Notes |
|---|---|---|
| `StepDetailPanel` *(new)* | **Create** | Slide-in overlay from the right, on top of a dimmed View 1 (page does **not** navigate). Header: **← back arrow** (hierarchy up), truncated run/job title, task-description subtitle, **X close** (top-right). Dismiss on X or click-outside. |
| `StepDetailNav` *(new, left rail)* | **Create** | Nested, collapsible step list. Section header shows run title. Each row: status icon (✅/⏳/⭕/❌), step name (truncate), duration (`1m 46s`, `<1s`). Parent/child indentation. Selected row highlighted; clicking updates the right content. |
| `StepDetailContent` *(new, right)* | **Create** | Two tabs: **Changes** and **Files**. |
| `Changes` tab | **Reuse** | The existing **`Timeline`** (+ `TurnGroup`, `ToolCallCard`, message/reasoning rows) repositioned here, scoped to the selected step's session. This is the current coordinator session view moved into the panel. |
| `Files` tab | **Reuse/assemble** | Changed-files list with per-file diff stats (`+8 -17`, `[M]` modified badge) and total (`+1 -21`), backed by `DiffViewer`/`FileViewer`. Use existing `BrowseFilesContext`/`ArtifactBrowser` adapter for the file source. |

### 2.2 Data requirements

- Per-step **session events** (already streamed via SSE / `useTimelineItems`) scoped to the selected
  step/child run id.
- Per-step **nested step/job list** with name, status, duration — from the step's child graph /
  job breakdown. Confirm the API exposes per-step sub-steps with durations; if only top-level exists,
  render single-level list initially.
- **File diffs** for the selected step: changed file paths, `+adds/-dels`, change kind (M/A/D). Reuse
  the artifact/diff endpoints already used by `DiffViewer`/`ArtifactBrowser`.

### 2.3 State model

- `selectedStepId` (opens panel) + `selectedSubStepId` (row highlight in left nav) + `activeTab`
  (`'changes' | 'files'`) all page-level. Closing clears `selectedStepId`.
- Panel reads the same event stream the page already has — no duplicate fetch; filter by step.

### 2.4 Responsive behavior

- Panel width ~ 60–70% on desktop, full-width on narrow screens. Left nav collapses to a top
  breadcrumb/dropdown under ~768px. Uses Fluent `Drawer`/overlay semantics (dimmed scrim over View 1).

### 2.5 Interactions not covered by #161 as filed

- **Deep-link / back-arrow semantics:** the ← back arrow implies a hierarchy (job → run). Define
  whether back navigates the nested list up a level vs. closes the panel. Recommend: ← moves up the
  nested hierarchy; X closes entirely.
- **Live updates while open:** the Changes tab must keep streaming if the step is in-progress
  (reuse existing SSE subscription rather than a snapshot).

---

## 3. Agentic preview (#157 / #159) — corrected approach

### 3.1 Why the old instruction was wrong

The build-test node previously instructed the agent to read `.agentweaver/agents.yaml` for a declared
`preview.port` / `sandbox.port` and fall back to `8080`. Ahmed's objection is correct: **the port is
not known ahead of time and varies per execution**, so a static config lookup is the wrong model.
There is also no such `agents.yaml` port config in the codebase — the real contract is the
`start_preview(port=PORT)` MCP tool (`RunOrchestrator.BrowserPreviewCapability`).

### 3.2 Correct agentic instruction (what the build-test step should say)

The **agent** (peer_review node, `qa-engineer`) should, after tests pass and if the project is a web
app/service:

1. **Discover** how to start a dev/preview server by inspecting the project — `package.json` scripts,
   `Dockerfile`, `Makefile`, `README`, framework defaults, etc. Do **not** assume a hardcoded port.
2. **Start** the server as a long-lived process, binding to `0.0.0.0`.
3. **Observe** the actual port it binds to from the process stdout/logs (and verify it responds, e.g.
   with `curl`).
4. **Register** it by calling `start_preview(port=PORT)` with the exact bound/verified port, so the
   preview sandbox attaches to the running process.

This is implemented (committed) in `bug_fix.yaml`, `software_delivery.yaml`, and
`CopilotWorkflowGenerator.cs`. It intentionally mirrors the existing `BrowserPreviewCapability`
contract so both paths use the same mechanism.

---

## 4. Issue updates summary

- **#160** — added concrete component names (`CoordinatorPanel`, `StepsPanel`, `SubtaskNode`,
  `StatusBar` in `WorkflowGraphPanel`), data fields, and the open question about where steering goes.
- **#161** — added component names (`StepDetailPanel`, `StepDetailNav`, `StepDetailContent`), reuse of
  `Timeline`/`DiffViewer`, state model, and back-arrow/live-update clarifications.
- **#162** — confirmed the two removal sites: `SubtaskNode` (CoordinatorRunPage L681–688 + inline
  `ChildStepRow`) and `WorkflowStepCard`; depends on #161.
- **#159** — rewritten to the agentic `start_preview(port=PORT)` approach; removed `agents.yaml`/8080.
