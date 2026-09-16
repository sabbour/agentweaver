# Web UI reference

See [Coordinator entry, outcome gate, embedded child inspection and review](../diagrams/flagship/canonical-coordinator-journey.png) for the shared visual model.

The Agentweaver web UI is a TypeScript React 19 SPA built with Vite. It uses React Router for routing, Fluent UI React Components (Fluent 2) for styling, and React Flow for workflow diagrams. It submits runs, streams live events, shows run details, and records your review decision before anything merges. The browser client keeps all run logic in the API layer.

## Configuration

`API_URL` is the API origin without `/api`. Runtime `window.__AGENTWEAVER_CONFIG__.API_URL` precedes `VITE_API_URL`; an explicit empty runtime value means same-origin. The client adds one `/api` to XHR paths, while auth redirects use origin-root `/auth/entra/*`.

| Setting | Meaning |
|---|---|
| `VITE_API_URL` | Build-time origin fallback. Runtime configuration takes precedence. |
| Browser authentication | Session authentication; the current client does not consume `VITE_API_KEY`. |

## Develop and build

```powershell
cd apps/web
npm install
npm run dev
npm run build
npm run lint
```

`npm run build` type-checks the app and produces a production bundle. `npm run lint` runs ESLint.

For production hosting, the Vite build output is served by the ASP.NET Core static web host in `apps/Agentweaver.Web` (default files, static files, and SPA fallback to `index.html`). `apps/Agentweaver.Api` maps API endpoints only; it does not serve the SPA.

## Routes

| Path | Page | Purpose |
| --- | --- | --- |
| `/`, `/overview` | Overview | Fleet activity, active projects, and recent activity |
| `/projects` | Project gallery | Card grid of all projects; create-blank and create-from-GitHub dialogs |
| `/projects/:projectId` | Dashboard | Project counters, throughput chart, and agent leaderboard |
| `/projects/:projectId/board` | Board | Project info, Kanban board, run list, and orchestration entry |
| `/projects/:projectId/flow` | Flow | Live view of what each agent is working on |
| `/projects/:projectId/orchestrations` | Orchestrations | Coordinator orchestration run list |
| `/projects/:projectId/workspace` | Workspace | Project repository and run worktree browser |
| `/projects/:projectId/settings` | Project settings | Provider/model defaults, rename, and delete |
| `/projects/:projectId/team` | Team | Current team roster, member management, charter editor, and sync panel |
| `/projects/:projectId/team/cast` | Casting wizard | Single-page casting wizard with Formulate, Template, and Analyze tabs |
| `/projects/:projectId/memories` | Team Memory | Decisions, the decision inbox, and agent memory recorded across runs |
| `/projects/:projectId/workflows` | Workflows | Workflow definitions and editing |
| `/projects/:projectId/diagnostics` | Diagnostics | Project diagnostics |
| `/projects/:projectId/heartbeat` | Heartbeat | Coordinator heartbeat status |
| `/projects/:projectId/orchestrations/:runId` | Coordinator run | Live outcome-spec review and confirm/revise gate for a coordinator run |

## Flows

### Project gallery

The project gallery (`/projects`) shows all projects as a card grid. Each card displays the project name, origin (blank or GitHub), working directory, and availability. An unavailable project — one whose working directory cannot be found on the server — renders with a warning indicator.

Two dialogs let you create a project. Both use the shared `CreateProjectDialogShell` with project/repository fields on the left, a height-bounded scrollable **Blueprint** panel on the right, and one footer **No blueprint** action (`apps/web/src/pages/ProjectGalleryPage.tsx:119`, `:264`, `:310`):

**Create blank project** — collects a name and a local working directory path, then opens the shared Blueprint panel on **Generated | Templates** (`ProjectGalleryPage.tsx:405`). The directory must already exist and be a git repository.

**Create from GitHub** — browses repositories available through the caller's Repo App capability, obtains a caller-bound, expiring, single-use repository selection code, and submits that code with project creation. The server resolves clone metadata and credentials; a typed repository URL used for blueprint suggestions is not clone authority. See the [repository-selection contract](./repo-blueprint-suggestions.md).

Clicking a project card navigates to the project dashboard.

### Board page

The board page (`/projects/:projectId/board`) shows project details, the Kanban board, past runs, and orchestration entry.

The details section shows the project name, origin, source repository (for GitHub projects), working directory, default branch, and provider settings.

The run list shows each run's id, status, and start time. Status badges show human-friendly labels: `No Changes`, `Completed`, `Merged`, `Failed`, `Merge Failed`, `Declined`, `Running`, and `Awaiting Review`. The `No Changes` label uses an informative (blue) badge to distinguish it from a full merge.

Coordinator runs (detected via `isCoordinatorRun`) instead show their **orchestration status** label when the optional `coordinator_status` field is present — `Dispatching`, `Awaiting assembly`, `In review`, `Assembling`, `Complete`, `Failed`, `Blocked`, or `Declined` (a `Failed` badge appends `coordinator_status_reason` when available). When the field is absent the bare run status is shown. Coordinator rows link to the orchestration topology page via the **Topology** button; non-coordinator rows no longer open standalone workflow run pages.

Start work through [Start an orchestration](#start-an-orchestration) or backlog pickup, not the retired standalone project-run submission endpoint.

### Project settings

The project settings page (`/projects/:projectId/settings`) has three sections:

**Provider defaults** — select `github-copilot` as the default provider and enter an optional model override. Changes are saved immediately on submit.

**Rename** — enter a new display name for the project.

**Delete** — permanently deletes the project record after confirmation. The working directory and git history are not affected.

### Product sign-in

Product sign-in uses Microsoft Entra ID through `SignInPage` and `AuthGate`; the client sends session authentication. GitHub Repo App and Copilot App are purpose-specific capabilities, not alternative product identities. Start work through Coordinator orchestration or backlog pickup.

### Submit a run

Use [Start an orchestration](#start-an-orchestration). Standalone project-run submission is retired.

### Start an orchestration

The start dialog accepts a goal, optional workflow and launch automation. Direct starts planning from the prompt; outcome definition uses draft/confirm. Both call orchestration and navigate to the Coordinator run.

### Coordinator run and outcome-spec gate

The coordinator run page (`/projects/:projectId/orchestrations/:runId`) streams the coordinator run live and hosts the outcome-spec review-and-confirm gate. The page header shows the shortened run id and the submitted goal (read from the `coordinator.started` event). The outcome-spec panel (`OutcomeSpecPanel`) renders below it.

The panel derives the spec from two sources, with no spec logic in the client: it seeds from `GET /api/runs/{id}/outcome-spec` and overlays the live `coordinator.outcome_spec` and `coordinator.outcome_spec.confirmed` events from the run stream (ordered and deduplicated by `sequence`). A 404 from the snapshot before the coordinator drafts is expected. The panel stays visible in **Drafting** state and polls every 2 seconds until REST or SSE provides the spec; if the run reaches a failure/decline terminal status before content arrives, it shows a terminal error instead of disappearing (`apps/web/src/components/OutcomeSpecPanel.tsx:160`, `:233`, `:328`, `:401`, `:537`).

The panel shows:

- A **status badge**: `Drafting`, `Awaiting confirmation`, `Confirmed`, or `Declined`.
- A **dispatch-gate notice**: while drafting or awaiting confirmation, an info bar states that no subagent work is dispatched until the outcome spec is confirmed. Once confirmed, a success bar notes that dispatch is unblocked (and who confirmed); if declined, a warning bar notes that no work was dispatched.
- The drafted **Goal**, **Desired outcome**, **Scope**, **Assumptions**, and any **Clarifying questions**. While the coordinator is still drafting and no content has arrived, a spinner with "Drafting the outcome spec..." is shown.

When the spec is awaiting confirmation, two actions appear:

- **Confirm** — calls `POST /api/runs/{id}/outcome-spec/confirm`, resuming the run past the gate. During submit it disables both gate actions, changes the label to **Confirming...**, shows a spinner, guards against double-click re-entry, retries the short-lived `409 no_pending_gate` gate-arming race, refreshes the spec on 409, and surfaces terminal/non-active errors as panel feedback (`OutcomeSpecPanel.tsx:237`, `:338`, `:345`, `:360`, `:578`, `:588`).
- **Request changes** — opens a dialog with a required **Feedback** field and calls `POST /api/runs/{id}/outcome-spec/revise`. The coordinator re-drafts and re-presents the spec without dispatching any work.

Interactive defineOutcome waits for confirm/revise; Direct skips the drafted-spec gate and launch Autopilot can confirm unattended. Review/merge and tool permissions remain separate boundaries.

### Coordinator orchestration and unified graph view

Once the outcome spec is confirmed, the coordinator run page renders a **unified dynamic graph** using the generic `WorkflowNode` renderer. This single graph shows the coordinator node, all subtask nodes, and the planned assembly pipeline in one React Flow canvas — replacing the previous separate topology view.

#### Page layout

The coordinator run page uses a **two-column layout** (Fluent `makeStyles` grid, `minmax(320px, 420px) 1fr`, stacking to a single column below 980px):

- **Left column** — the **Outcome spec** in its own scroll container (bounded height, `overflow-y: auto`), so a long spec scrolls independently of the topology.
- **Right column** — the **execution topology** (React Flow canvas), the **assembly-review affordance**, and the **coordinator session** panel with the steering chat box.

#### Graph data flow

The graph seeds from `GET /api/runs/{coordinatorRunId}/graph`, which returns a `GraphDescriptor` with `variant: "coordinator"`. Live `coordinator.graph` SSE snapshots (highest `seq` wins) are applied on top; the REST snapshot is used as-is for finished/parked runs where the SSE stream is closed.

The coordinator-variant descriptor contains:
- **Coordinator node** (`id: "coordinator"`, `node_type: "agent"`, `role: "coordinator"`) — the orchestrator itself
- **Subtask nodes** (`id: "plan:subtask-{n}"`, `node_type: "subtask"`) — one per dispatched subtask; carries optional `agent`, `model`, `phase`, `child_graph_ref`, and `child_run_id` fields
- **Assembly nodes** use stable `planned:assembly-*` IDs, become live when reached, and display persisted status/reason/terminal-stage. The selected workflow determines the gate chain rather than a fixed muted RAI-only pipeline.

Subtask status is projected from topology and run events by mapping the subtask node id (`plan:subtask-{n}`) to the topology node id (`subtask-{n}`) by stripping the `plan:` prefix.

#### Coordinator loopback edges

Each selected assembly gate can have a **loopback back-edge** (`loopback: true`) to the Coordinator. These edges represent rework, not forward pipeline progression, and are excluded from forward-degree/cardinality calculations. The renderer derives labels from the source role, including **RAI flags**, **Request changes**, and a generic **Rework** fallback, and uses dashed/curved back-edge styling. The selected workflow determines the gates; do not assume exactly two loopbacks.

#### Subtask node expansion

Subtask nodes (`node_type: "subtask"`) are expandable cards. Each shows the assigned agent, selected model, phase, and a status badge. When a subtask has a `child_graph_ref` (i.e. the coordinator has dispatched that subtask to a child run), clicking **Expand pipeline** fetches the child run's `GraphDescriptor` from `GET /api/runs/{childRunId}/graph` and simultaneously subscribes to the child run's live SSE stream. The inline panel then renders the child pipeline as a horizontal row of node cards — one per node in the child descriptor — connected by arrow separators. Each inline card shows the same status badge, elapsed timer, role text, and optional status message as the full workflow graph. If the descriptor is not yet available (fetch in-flight), a hardcoded fallback pipeline (Agent → Assemble-ready) is shown immediately while the fetch completes.

The SSE subscription for each inline child graph is scoped to the expansion: it starts when the subtask is expanded and tears down when collapsed. At most one child run is subscribed per open panel; no background subscriptions are held for collapsed subtasks.

While a subtask is expanded, the parent subtask card header also shows an **aggregate elapsed time** — the sum of the child pipeline steps' durations (each step's `completedAt − startedAt`, or `now − startedAt` while still running). It ticks live (1s) when any child step is in progress and is labelled `aria-label="Total child elapsed"`.

Child run inspection stays inside the orchestration context: expand the child pipeline or select the child in the Agent Sessions panel to inspect messages, approvals/questions, files, and status.

#### Coordinator node and orchestration status

The coordinator node's status reflects the **orchestration lifecycle** rather than a stale `pending`. The lifecycle phase is derived (in priority order) from live `coordinator.assembly_*` events, then the optional `coordinator_status` field on the run summary / run detail, then the work-plan status — all read defensively so the page degrades gracefully whether or not those backend fields are present. The phase is mapped to `running` / `completed` / `failed` for the node badge and shown as a label (`Dispatching`, `Awaiting assembly`, `Assembling`, `In review`, `Complete`, `Failed`, `Blocked`, `Declined`) next to the graph title.

The coordinator node also carries a **View session** button that scrolls to the coordinator session panel (provided via `CoordinatorSessionContext`).

#### Build & Test preview status

When the run emits durable preview events, the coordinator run page reads them from `GET /api/runs/{id}/events` and uses the latest preview event as the UI source of truth. `sandbox.preview_ready` and `coordinator.preview_ready` show an **Open preview** button on the **Build & Test** row and in the human-review artifacts panel. `sandbox.preview_pending` shows **Preview pending approval**. `sandbox.preview_failed` shows **Preview unavailable** with the backend reason while leaving human review actionable. `sandbox.preview_skipped_not_applicable` is treated as an intentional skip, not a blocking error.

See [Decoupled live-preview provisioning](./live-preview-provisioning.md) for the event contract and [the user guide](../experience/live-preview-provisioning.md) for the review workflow.

#### Graph rendering affordances (edges, cards, minimap, zoom)

The graph's visual layer is shared across the coordinator run page and inline editor previews via `WorkflowGraphPanel.tsx`:

- Forward edges use `SpineEdge`: stepped orthogonal routes from `buildSteppedConnectorRoute` and `buildBridgedOrthogonalPath`, with arrow markers, crossing bridges and computed junctions. This is not a two-Bezier-segment router.
- **Status accent bar** — both `WorkflowNode` and the coordinator `SubtaskNode` cards render a colored top-accent bar keyed to status (via the exported `accentClass` helper) with the status badge moved to the top-left of the card header.
- **Node dimensions** — `layoutDag` / `layoutDagColumns` in `dagLayout.ts` seed each node's `initialWidth` / `initialHeight` from its size hint (falling back to `NODE_W` / `NODE_H`), so the minimap has authoritative geometry even before React Flow measures the DOM. These are `initial*` hints (not fixed `width` / `height`), so expandable cards are never clipped.
- **MiniMap** — the coordinator run page renders a React Flow `MiniMap` in the bottom-right (172×116, rounded container with a subtle border and shadow). Each node is colored by its `topoStatus` (green complete, blue running/dispatching, amber waiting/awaiting-assembly, red failed/declined, neutral otherwise) with rounded corners, a light mask, and a brand-blue viewport outline.
- **Zoom control** — `ZoomControls` (from `useCtrlScrollZoom.tsx`) is a compact segmented bar: an optional **fit-to-view** button (shown only when an `onFit` handler is passed; the graph pages wire it to `resetZoom`, which returns to 100% — the natural fitted size), minus/plus buttons, and a live percentage readout. The "Ctrl + Scroll to zoom" hint is a `title` tooltip rather than always-visible text. The hook applies zoom via CSS `zoom` (bounds: 50%–100% on the board, 50%–200% on the graph pages).

#### Agent Sessions panel

The **Agent Sessions** slide-in (`AgentSessionPanel.tsx`) opens from the graph and hosts a left sidebar tree of the coordinator plus its subtasks, alongside the selected node's live session stream. The tree is built in `CoordinatorRunPage.tsx` and includes **every subtask node** — both dispatched subtasks (with a `childRunId` and a streamable conversation) and still-planned/pending ones — rather than only dispatched children, so the full plan is visible from the moment it is confirmed. The tree is **flat**: every subtask renders as a direct child of the Coordinator (one indentation level), matching the graph — data dependencies between subtasks are shown as edges in the graph, not as nested indentation in the tree.

Rows are sorted deterministically by stage rank, then numeric subtask id, then label/position tie-breakers: **Work plan**, **Outcome plan**, subtasks, **RAI**, **Build & Test**, **Human Review**, **Merge**, and **Scribe** (`apps/web/src/pages/CoordinatorRunPage.tsx:1933`, `:1951`, `:3155`). Each row renders an indentation guide line (vertical connector and elbow), a circular **status glyph** that is color-coded to match the graph — filled green check for complete/merged/assemble-ready, filled red dismiss for failed/declined, a marigold clock for awaiting/waiting/RAI-flagged/revising, a blue spinner for running/dispatched, and a neutral hollow circle for pending — the node label with a role subline, and a right-aligned duration derived from the node's `startedAt` / `completedAt`. Selecting a row streams that node's session; planned nodes with no child run simply show no conversation yet.

#### Coordinator session panel and steering chat box

The right column hosts an all-up **Coordinator session** panel:

- A **timeline** derived from the coordinator's own event stream — `coordinator.started` (goal), outcome spec confirmed, work plan ready, each `subtask.*` transition, `coordinator.children_complete`, and the `coordinator.assembly_*` milestones — each with a relative elapsed offset from the first timestamped milestone. For `coordinator.assembly_changes_requested`, the timeline text is `🔁 {Gate} requested changes → revising N subtasks` and includes feedback when present (`apps/web/src/components/AgentSessionPanel.tsx:1599`).
- An **Action required** block (above the timeline) that surfaces bubbled child questions and tool-approval requests re-projected onto the coordinator stream (see below).
- A persistent **steering chat box** (a text area + **Send** button, plus quick **Redirect** and **Stop** affordances) that submits free-form steering via `POST /api/runs/{id}/steer` (default `kind: "amend"`) **without** opening a dialog. Queued/applied steering directives from `coordinator.steering` events are listed below the box.

Outcome-spec JSON rows in the message stream are parsed client-side into an **Outcome plan** message and attributed to **Coordinator (Outcome plan)** (`apps/web/src/components/AgentSessionPanel.tsx:777`, `:791`, `:1438`). RAI verdict rationales that are only placeholders (`-`, `---`, `—`) normalize to empty and are omitted from the verdict row (`AgentSessionPanel.tsx:805`, `:1245`). With technical details hidden, system-prompt scaffolding and internal assembly-gate prompt rows are hidden from the user-facing transcript (`AgentSessionPanel.tsx:740`, `:2388`).

##### Automation toggles (Autopilot + auto-approve tools)

Above the timeline the coordinator session panel hosts two automation switches, seeded from the coordinator run detail (`GET /api/runs/{id}`) booleans `autopilot` and `auto_approve_tools` (both optional, default `false`). The seed is applied once on the first successful poll (a ref guard prevents the 4-second poll from clobbering an in-flight user toggle):

- **Autopilot (auto-answer questions)** — flips via `apiClient.setAutopilot(runId, enabled)` → `POST /api/runs/{id}/autopilot` `{ enabled }`. Copy: *auto-answer clarifying questions using the coordinator model; permission requests still require approval.* Coordinator-only.
- **Auto-approve tools (cascades to children)** — flips via `apiClient.setAutoApprove(runId, enabled)` → `POST /api/runs/{id}/auto-approve` `{ enabled }`. Copy: *auto-approve tool permission requests; dangerous tools remain blocked by policy.*

Both toggles are **optimistic** (the switch flips immediately, then reconciles to the server's returned boolean, reverting on error) and target the **coordinator run id**; the cascade to child runs is applied server-side. Both are disabled when the orchestration is in a terminal/parked phase (`complete`/`failed`/`blocked`/`declined`), because the endpoints return `409` for non-active runs. The tooltips note that both settings cascade to children and that policy-denied tools stay blocked.

Two muted **audit milestones** appear in the session timeline when automation acts:

- `tool.auto_approved` `{ requestId, toolName, url? }` → *Tool auto-approved: {toolName} {url?}*.
- `coordinator.autopilot_answered` `{ runId, childRunId?, requestId, question, answer }` → *Autopilot answered (child {id})?: {question} → {answer}* — the child suffix appears only when `childRunId` is present.

##### Bubbled child questions and approvals (routing)

A child run can ask a question or request tool approval; the coordinator re-projects these onto its own stream as `coordinator.child_question` `{ childRunId, subtaskId, requestId, question }` and `coordinator.child_approval_required` `{ childRunId, subtaskId, requestId, toolName, url?, message? }`. The **Action required** block renders each as an actionable item labelled with its source subtask (`Subtask {n}`):

- A child **question** renders the embedded answer card, but the answer is POSTed against the **`childRunId`** from the payload (`apiClient.answerQuestion(childRunId, requestId, value)`), **not** the coordinator run id — the child is the run that is blocked.
- A child **approval** appears in the child session panel targeted at the **`childRunId`**, so Allow/Deny POST against the child's `tool-approvals`/`tool-denials` endpoints. The tool name, URL, and message are shown.
- Each item collapses once resolved (a question on `agent.question_answered` for the same `requestId`, or optimistically on submit; an approval on the card's own allow/deny action).

#### Assembly-review affordance

When the orchestration reaches the collective human-review stage, the page presents a clear next action instead of a bare status:

- **`awaiting_assembly` / `assembling`** — an "Assembling collective output…" panel with a spinner.
- **`in_review`** (or a `coordinator.assembly_review_requested` event) — an **Assembly review** panel that surfaces the integration diff/summary (read from the event payload's `diff` / `summary` / `treeHash` fields) and **Approve** / **Request changes** / **Decline** buttons. These POST to `POST /api/runs/{coordinatorRunId}/assembly/review` via `apiClient.reviewAssembly(runId, { decision, comment? })`. A comment is required for request-changes and decline.
- **`coordinator.steering_received` / `coordinator.steering_decision`** — correction feedback and the coordinator's decision remain available in the run event stream for inspection and recovery.
- **`failed` / `blocked` / `declined`** — the human-readable **reason** (from the `coordinator.assembly_failed`/`blocked`/`declined` event payload or `coordinator_status_reason`) plus guidance that the subtasks are parked and can be redirected/amended via the steering chat box. The stuck state never renders a bare "Failed" with no explanation.

#### Steering bar

A page-level steering bar sits above the React Flow canvas. Three buttons are always available while the coordinator run is active:

- **Stop** — sends `{ kind: "stop" }` to `POST /api/runs/{id}/steer`
- **Redirect** — opens a dialog to enter an instruction; sends `{ kind: "redirect", instruction: "..." }`
- **Amend** — opens a dialog to enter an instruction; sends `{ kind: "amend", instruction: "..." }`

The steering bar is always visible on the coordinator run page even for finished runs (buttons remain rendered; the API will reject the call if the run is not active). The same steering actions are also available inline on the page via the steering chat box (no dialog required) — see [Coordinator session panel and steering chat box](#coordinator-session-panel-and-steering-chat-box).

### Embedded run inspection

Standalone workflow and execution run pages have been retired. The web UI keeps operators in the coordinator context instead:

- The coordinator graph shows coordinator/subtask status and lets users expand a child pipeline inline from `GET /api/runs/{childRunId}/graph` plus the child run SSE stream.
- The **Agent Sessions** panel streams the selected coordinator or child run, including agent messages, tool calls, approvals, questions, lifecycle events, and status.
- The panel's **Changes** and **Files** tabs use the same `/api/runs/{id}/files`, `/workspace`, and assembly artifact endpoints that embedded review flows use.
- Questions and approvals remain routed to the run that is blocked (`childRunId` for child work, coordinator id for coordinator work).

### Team page

The team page (`/projects/:projectId/team`) shows the current cast team as a card grid. Each card displays the agent's name, role, and a status badge (**Active** or **Retired**).

Clicking a card opens a slide-in panel with three tabs:

- **Overview** — member summary, role, status, and charter timestamps (created and last updated)
- **Charter** — the agent's full charter text in a read-only view
- **Capabilities** — role capabilities pulled from the catalog

Filter tabs at the top of the grid narrow the view: **All**, **Active**, **Retired**.

Two action buttons appear in the page header:

**Add member** — opens a dialog to select a role from the full catalog and cast a new team member directly, without going through the casting wizard.

A **Cast team** button navigates to the casting wizard at `/projects/:projectId/team/cast`.

The sync panel at the bottom of the page shows the pending uncommitted changes fetched from `GET /api/projects/{id}/team/sync`. Each changed file is listed with its status (`added`, `modified`, or `deleted`). A **Commit** button opens a dialog to enter an optional commit message and then calls `POST /api/projects/{id}/team/sync` with the change set hash. If the change set shifts between the panel load and the commit, the server returns a conflict and the panel shows an error with a prompt to refresh.

### Team Memory page

The team memory page (`/projects/:projectId/memories`) surfaces the durable knowledge recorded by the team across all runs.

Two tabs:

**Decisions** — finalized decisions alongside proposed Decision Inbox entries pending
review. Each entry shows title, type badge (architectural, scope, process, technical),
agent badge, and creation time. Proposed entries expose merge, promote, and reject
actions, but the API accepts those authority-changing operations only from a project
owner or verified Coordinator run.

**Agent Memory** — project memory entries (via `record_memory`). Each entry shows importance badge (high/medium/low), type, and content. You can create new entries and edit existing ones.

Both tabs fetch live from the API; data is cached for the session tab switch.

The underlying decision and memory responses also carry `sourceKind`,
`sourceIdentity`, `sourceRunId`, `trustState`, `approvedBy`, and `approvedAt`. The
current Team Memory cards do not render those fields, so use the API or MCP memory
tools when auditing provenance. MCP exposes decision inbox submit/list/merge/reject,
decision create/list/update, memory record/list/get/search, session management, and
memory import/export tools; it does not expose legacy memory promotion or decision
approval. Use the REST-only memory `promote` and decision `approve` endpoints for those
trust upgrades. A record can therefore appear in the list while remaining inactive for
prompt compilation: `legacy` records are excluded until approved, and `pending` memory
cannot cross to another agent. Only active, `approved` architectural and scope
decisions compile as team boundaries.

### Casting wizard

The casting wizard (`/projects/:projectId/team/cast`) is a single-page form with three strategy tabs:

**Formulate** — describe the goal in natural language. The AI analyzes the description and proposes a set of roles with a team rationale sentence.

**Template** — pick from pre-built team templates (Quick Software Development, Product Feature Delivery, Azure Feature Delivery, Content Authoring & Research). The template description and pre-selected roles are shown.

**Analyze** — the AI scans the project repository (README, package files, source structure) to detect the tech stack and team shape automatically.

All three tabs share:

- **Team size** — SpinButton to specify the exact number of roles
- **Roles** — checkbox grid of all available catalog roles; two-way bound with the AI proposal
- **Universe** — collapsible accordion to select the character universe for agent names (15 available)

After proposing, a **Why this team** sentence explains the rationale. Clicking **Confirm** writes the team to `.squad/` and navigates back to the team page. At any point, clicking **Reject** discards the proposal and returns to the team page.

When an existing team is detected, a choice of intent is presented before confirming: replace (`new`), augment (`augment`), or recast (`recast`).

### New Run dialog

Use [Start an orchestration](#start-an-orchestration). Standalone project-run submission is retired.

### Recent Runs section

Below the team member grid on the team page, a collapsible **Recent Runs** section lists all runs for the project fetched from `GET /api/projects/{id}/runs`. Each entry shows:

- Agent name (which team member ran it)
- Task description (truncated)
- Status badge (color-coded: warning for in-progress, success for completed, danger for failed)
- Started time

Coordinator run entries open the orchestration detail page; standalone workflow/watch pages are no longer routed.

## Structure

```text
apps/web/src/
  App.tsx                    route declarations and access gates
  config.ts                  runtime/build-time API origin
  components/
    StartOrchestrationDialog.tsx
    OutcomeSpecPanel.tsx
    WorkflowGraphPanel.tsx
    AgentSessionPanel.tsx
  pages/
    SignInPage.tsx
    SessionsPage.tsx
    SettingsPage.tsx
    SkillsPage.tsx
    ClusterPage.tsx
```

Coordinator and Assistant route boundaries, plus project observability views, are wired from `App.tsx`.

## Current navigation and action boundaries

Routes include `/sessions`, `/settings`, `/assistant`, platform-admin-only `/platform-settings`, project skills/cluster/observability, and `/projects/:projectId/team/:agentName/memory`. Legacy project-session and global-observability URLs redirect.

Project creation uses Repo App browse → selection code → server-authorized creation, not arbitrary repository-URL authority. Start through Coordinator; `/api/projects/{id}/runs` is retired (`410`), not an agent-specific submission path.

Safe-tool auto-approval covers `web_fetch` and `start_preview`; Autopilot handles clarifying questions and launch-time outcome confirmation. Human assembly buttons require a human-review gate, not every assembly-review-requested event. Steer only server-reported steerable runs; terminal failed/declined runs are not universally amendable in place. Child questions/approvals target the actual child. Preview failure remains visible independently of the Build/Test verdict.

<details id="diagram-context-canonical-coordinator-journey" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>One goal, one collective review</td></tr>
<tr><td>subtitle</td><td>Confirm intent, dispatch bounded work, then integrate and review the whole result.</td></tr>
<tr><td>group-title0</td><td>Plan and execute</td></tr>
<tr><td>group-title1</td><td>Integrate, review, finish</td></tr>
<tr><td>Confirm intent</td><td>Confirm intent</td></tr>
<tr><td>Confirm intent</td><td>Draft the OutcomeSpec</td></tr>
<tr><td>Confirm intent</td><td>human confirmation</td></tr>
<tr><td>Plan the work</td><td>Plan the work</td></tr>
<tr><td>Plan the work</td><td>Persist a WorkPlan DAG</td></tr>
<tr><td>Plan the work</td><td>subtasks + dependencies</td></tr>
<tr><td>Dispatch children</td><td>Dispatch children</td></tr>
<tr><td>Dispatch children</td><td>Run the eligible frontier</td></tr>
<tr><td>Dispatch children</td><td>per-child worktrees</td></tr>
<tr><td>Merge + Scribe</td><td>Merge + Scribe</td></tr>
<tr><td>Merge + Scribe</td><td>Approved integration path</td></tr>
<tr><td>Merge + Scribe</td><td>MergeWorktree → Scribe</td></tr>
<tr><td>Collective review</td><td>Collective review</td></tr>
<tr><td>Collective review</td><td>One human decision</td></tr>
<tr><td>Collective review</td><td>approve / revise / decline</td></tr>
<tr><td>Integrate + gates</td><td>Integrate + gates</td></tr>
<tr><td>Integrate + gates</td><td>Assemble child branches</td></tr>
<tr><td>Integrate + gates</td><td>configured checks / review</td></tr>
<tr><td>e1</td><td>confirm</td></tr>
<tr><td>e2</td><td>dispatch</td></tr>
<tr><td>e3</td><td>settled work</td></tr>
<tr><td>e4</td><td>request review</td></tr>
<tr><td>e5</td><td>approve</td></tr>
<tr><td>assurance-title</td><td>DO NOT CONFUSE ASSEMBLY WITH PUBLICATION</td></tr>
<tr><td>assurance-line1</td><td>The collective workflow reaches MergeWorktree and Scribe; this graphic does not promise PR creation.</td></tr>
<tr><td>assurance-line2</td><td>A blocked assembly can be recovered. Review approval does not itself mark the run complete.</td></tr>
<tr><td>Confirm intent</td><td>Input</td></tr>
<tr><td>Confirm intent</td><td>Human goal</td></tr>
<tr><td>Confirm intent</td><td>Artifact</td></tr>
<tr><td>Confirm intent</td><td>OutcomeSpec</td></tr>
<tr><td>Confirm intent</td><td>Gate</td></tr>
<tr><td>Confirm intent</td><td>Confirm or revise</td></tr>
<tr><td>Confirm intent</td><td>Scope</td></tr>
<tr><td>Confirm intent</td><td>Explicit assumptions</td></tr>
<tr><td>Plan the work</td><td>Select</td></tr>
<tr><td>Plan the work</td><td>Workflow choice</td></tr>
<tr><td>Plan the work</td><td>WorkPlan DAG</td></tr>
<tr><td>Plan the work</td><td>Owners</td></tr>
<tr><td>Plan the work</td><td>Named subtasks</td></tr>
<tr><td>Plan the work</td><td>Store</td></tr>
<tr><td>Plan the work</td><td>Persist dependencies</td></tr>
<tr><td>Dispatch children</td><td>Ready</td></tr>
<tr><td>Dispatch children</td><td>Satisfied dependencies</td></tr>
<tr><td>Dispatch children</td><td>Files</td></tr>
<tr><td>Dispatch children</td><td>Child-owned worktree</td></tr>
<tr><td>Dispatch children</td><td>Observe</td></tr>
<tr><td>Dispatch children</td><td>Child status / results</td></tr>
<tr><td>Dispatch children</td><td>Failure</td></tr>
<tr><td>Dispatch children</td><td>Blocks dependents</td></tr>
<tr><td>Merge + Scribe</td><td>Merge</td></tr>
<tr><td>Merge + Scribe</td><td>Reviewed integration</td></tr>
<tr><td>Merge + Scribe</td><td>Then</td></tr>
<tr><td>Merge + Scribe</td><td>Collective Scribe</td></tr>
<tr><td>Merge + Scribe</td><td>Record</td></tr>
<tr><td>Merge + Scribe</td><td>Promote decisions</td></tr>
<tr><td>Merge + Scribe</td><td>Decline</td></tr>
<tr><td>Merge + Scribe</td><td>Skips Scribe</td></tr>
<tr><td>Collective review</td><td>Approve</td></tr>
<tr><td>Collective review</td><td>Proceed to merge</td></tr>
<tr><td>Collective review</td><td>Revise</td></tr>
<tr><td>Collective review</td><td>Steer / redispatch</td></tr>
<tr><td>Collective review</td><td>No Scribe path</td></tr>
<tr><td>Collective review</td><td>Blocked</td></tr>
<tr><td>Collective review</td><td>Recoverable state</td></tr>
<tr><td>Integrate + gates</td><td>Child branches</td></tr>
<tr><td>Integrate + gates</td><td>Target</td></tr>
<tr><td>Integrate + gates</td><td>Integration branch</td></tr>
<tr><td>Integrate + gates</td><td>Gates</td></tr>
<tr><td>Integrate + gates</td><td>Selected checks</td></tr>
<tr><td>Integrate + gates</td><td>Output</td></tr>
<tr><td>intent</td><td>Scope and assumptions are explicit; Revision reopens the intent gate</td></tr>
<tr><td>plan</td><td>Outcome-complete decomposition; Bounded work with named owners</td></tr>
<tr><td>dispatch</td><td>Observe child status and results; Failure / RAI blocks dependents</td></tr>
<tr><td>finish</td><td>Decline skips Scribe; No automatic PR claim here</td></tr>
<tr><td>review</td><td>Changes can redispatch work; Blocked is recoverable, not terminal</td></tr>
<tr><td>integrate</td><td>Collective—not per-child delivery; Merge failure may still run Scribe</td></tr>
<tr><td>notes</td><td>DO NOT CONFUSE ASSEMBLY WITH PUBLICATION; The collective workflow reaches MergeWorktree and Scribe; this graphic does not promise PR creation.; A blocked assembly can be recovered. Review approval does not itself mark the run complete.</td></tr>
<tr><td>groups</td><td>Plan and execute; Integrate, review, finish</td></tr>
</tbody></table>
</details>
