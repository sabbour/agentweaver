# Web UI reference

The Agentweaver web UI is a React 19 and Fluent 2 client over the backend API. It submits runs, streams live events, shows run details, and records your review decision before anything merges. The browser client keeps all run logic in the API layer.

## Configuration

Copy `.env.example` to `.env` in `apps/web`, then set the Vite variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `VITE_API_URL` | `http://localhost:5000` | API base URL |
| `VITE_API_KEY` | empty | Bearer key sent on every request |

## Develop and build

```powershell
cd apps/web
npm install
npm run dev
npm run build
npm run lint
```

`npm run build` type-checks the app and produces a production bundle. `npm run lint` runs ESLint.

## Routes

| Path | Page | Purpose |
| --- | --- | --- |
| `/` | Project gallery | Card grid of all projects; create-blank and create-from-GitHub dialogs |
| `/projects/:projectId` | Project | Project info, run list, and start-run dialog |
| `/projects/:projectId/settings` | Project settings | Provider/model defaults, rename, relink, delete |
| `/projects/:projectId/team` | Team | Current team roster, member management, charter editor, and sync panel |
| `/projects/:projectId/team/cast` | Casting wizard | Single-page casting wizard with Formulate, Template, and Analyze tabs |
| `/projects/:projectId/runs/:runId/workflow` | Workflow run | Live workflow diagram with executor stage cards, execution modal, and status |
| `/projects/:projectId/orchestrations/:runId` | Coordinator run | Live outcome-spec review and confirm/revise gate for a coordinator run |
| `/projects/:projectId/memories` | Team Memory | Decisions and RAI audit trail recorded across runs |
| `/settings` | Settings | API connection settings |

## Flows

### Project gallery

The home page (`/`) shows all projects as a card grid. Each card displays the project name, origin (blank or GitHub), working directory, and availability. An unavailable project — one whose working directory cannot be found on the server — renders with a warning indicator.

Two dialogs let you create a project:

**Create blank project** — collects a name and a local working directory path. The directory must already exist and be a git repository.

**Create from GitHub** — collects a name, GitHub repository URL, and a local path. The server clones the repository into that path.

Clicking a project card navigates to the project page.

### Project page

The project page (`/projects/:projectId`) shows project details, a list of past runs, and a start-run dialog.

The details section shows the project name, origin, source repository (for GitHub projects), working directory, default branch, and provider settings.

The run list shows each run's id, status, and start time. Status badges show human-friendly labels: `No Changes`, `Completed`, `Merged`, `Failed`, `Merge Failed`, `Declined`, `Running`, and `Awaiting Review`. The `No Changes` label uses an informative (blue) badge to distinguish it from a full merge. Clicking a run navigates to the workflow run page for that run.

Coordinator runs (detected via `isCoordinatorRun`) instead show their **orchestration status** label when the optional `coordinator_status` field is present — `Dispatching`, `Awaiting assembly`, `In review`, `Assembling`, `Complete`, `Failed`, `Blocked`, or `Declined` (a `Failed` badge appends `coordinator_status_reason` when available). When the field is absent the bare run status is shown. Coordinator rows link to the orchestration topology page via the **Topology** button; other runs link to the **Workflow** page.

The start-run dialog collects:

- **Task** — required description for the agent
- **Model** — optional override; falls back to the project default
- **Base branch** — optional; falls back to the project's default branch

A **Start orchestration** button sits alongside the start-run controls. It opens the start-orchestration dialog and, on success, navigates to the coordinator run page (`/projects/:projectId/orchestrations/:runId`). See [Start an orchestration](#start-an-orchestration) below.

### Project settings

The project settings page (`/projects/:projectId/settings`) has three sections:

**Provider defaults** — select `github-copilot` as the default provider and enter an optional model override. Changes are saved immediately on submit.

**Rename** — enter a new display name for the project.

**Relink** — enter a new working directory path. Use this after moving the repository to a different location.

**Delete** — permanently deletes the project record after confirmation. The working directory and git history are not affected.

### GitHub sign-in

The `GitHubSignIn` component is mounted in the application header and is visible on every page.

When signed out or never signed in, it shows a **Sign in with GitHub** button. Clicking the button starts the device authorization flow: the component displays the verification URL and one-time code. The component polls the API automatically and updates to show the authenticated GitHub username once the flow completes.

When signed in, it shows the GitHub username and a **Sign out** button.

### Submit a run

The home page collects the repository path, originating branch, task description, and model source. Submit stays disabled until the path, branch, and task are filled in. On success the app navigates to the watch screen for the new run.

### Start an orchestration

The start-orchestration dialog (`StartOrchestrationDialog`) is opened from the **Start orchestration** button on the project page. It collects a single **Goal** field — a plain-language description of the outcome to achieve. Submit stays disabled until the goal is non-empty. Submitting calls `POST /api/projects/{id}/orchestrations`, which starts a coordinator run and returns its `runId`; the app then navigates to the coordinator run page at `/projects/:projectId/orchestrations/:runId`.

### Coordinator run and outcome-spec gate

The coordinator run page (`/projects/:projectId/orchestrations/:runId`) streams the coordinator run live and hosts the outcome-spec review-and-confirm gate. The page header shows the shortened run id and the submitted goal (read from the `coordinator.started` event). The outcome-spec panel (`OutcomeSpecPanel`) renders below it.

The panel derives the spec from two sources, with no spec logic in the client: it seeds from `GET /api/runs/{id}/outcome-spec` and overlays the live `coordinator.outcome_spec` and `coordinator.outcome_spec.confirmed` events from the run stream (ordered and deduplicated by `sequence`). A 404 from the snapshot before the coordinator drafts is expected — the stream fills it in.

The panel shows:

- A **status badge**: `Drafting`, `Awaiting confirmation`, `Confirmed`, or `Declined`.
- A **dispatch-gate notice**: while drafting or awaiting confirmation, an info bar states that no subagent work is dispatched until the outcome spec is confirmed. Once confirmed, a success bar notes that dispatch is unblocked (and who confirmed); if declined, a warning bar notes that no work was dispatched.
- The drafted **Goal**, **Desired outcome**, **Scope**, **Assumptions**, and any **Clarifying questions**. While the coordinator is still drafting and no content has arrived, a spinner with "Coordinator is drafting the outcome spec..." is shown.

When the spec is awaiting confirmation, two actions appear:

- **Confirm** — calls `POST /api/runs/{id}/outcome-spec/confirm`, resuming the run past the gate.
- **Request changes** — opens a dialog with a required **Feedback** field and calls `POST /api/runs/{id}/outcome-spec/revise`. The coordinator re-drafts and re-presents the spec without dispatching any work.

The confirm/revise gate is the safety property of the Phase 1 flow: no dispatch occurs before a human confirms.

### Coordinator orchestration and unified graph view

Once the outcome spec is confirmed, the coordinator run page renders a **unified dynamic graph** using the same generic `WorkflowNode` renderer as the workflow run page. This single graph shows the coordinator node, all subtask nodes, and the planned assembly pipeline in one React Flow canvas — replacing the previous separate topology view.

#### Page layout

The coordinator run page uses a **two-column layout** (Fluent `makeStyles` grid, `minmax(320px, 420px) 1fr`, stacking to a single column below 980px):

- **Left column** — the **Outcome spec** in its own scroll container (bounded height, `overflow-y: auto`), so a long spec scrolls independently of the topology.
- **Right column** — the **execution topology** (React Flow canvas), the **assembly-review affordance**, and the **coordinator session** panel with the steering chat box.

#### Graph data flow

The graph seeds from `GET /api/runs/{coordinatorRunId}/graph`, which returns a `GraphDescriptor` with `variant: "coordinator"`. Live `coordinator.graph` SSE snapshots (highest `seq` wins) are applied on top; the REST snapshot is used as-is for finished/parked runs where the SSE stream is closed.

The coordinator-variant descriptor contains:
- **Coordinator node** (`id: "coordinator"`, `node_type: "agent"`, `role: "coordinator"`) — the orchestrator itself
- **Subtask nodes** (`id: "plan:subtask-{n}"`, `node_type: "subtask"`) — one per dispatched subtask; carries optional `agent`, `model`, `phase`, `child_graph_ref`, and `child_run_id` fields
- **Planned assembly nodes** (`id: "planned:assembly-{rai|review|merge|scribe}"`, `kind: "planned"`) — the fixed post-subtask pipeline; always rendered muted/dashed, never show a running or pending spinner

Subtask status is projected from topology and run events by mapping the subtask node id (`plan:subtask-{n}`) to the topology node id (`subtask-{n}`) by stripping the `plan:` prefix.

#### Coordinator loopback edges

The coordinator descriptor may include **loopback back-edges** (`loopback: true`) from the assembly RAI gate and Human Review gate back to the coordinator node — representing a re-dispatch when the collective output is flagged or changes are requested. `GraphEdge` has no `label` field, so the renderer derives a visible label from the **source node's role** (falling back to its id) via `coordinatorLoopbackLabel`: a RAI source is labelled **"RAI flags"** and a review source **"Request changes"** (unknown sources get a generic **"Rework"** so the back-edge is never unlabelled). These render with the same dashed/curved back-edge styling as the per-run loopbacks. The logic is robust to descriptors with zero loopbacks (older runs simply have no back-edges).


#### Subtask node expansion

Subtask nodes (`node_type: "subtask"`) are expandable cards. Each shows the assigned agent, selected model, phase, and a status badge. When a subtask has a `child_graph_ref` (i.e. the coordinator has dispatched that subtask to a child run), clicking **Expand pipeline** fetches the child run's `GraphDescriptor` from `GET /api/runs/{childRunId}/graph` and simultaneously subscribes to the child run's live SSE stream. The inline panel then renders the child pipeline as a horizontal row of node cards — one per node in the child descriptor — connected by arrow separators. Each inline card shows the same status badge, elapsed timer, role text, and optional status message as the full workflow graph. If the descriptor is not yet available (fetch in-flight), a hardcoded fallback pipeline (Agent → Rai → Assemble-ready) is shown immediately while the fetch completes.

The SSE subscription for each inline child graph is scoped to the expansion: it starts when the subtask is expanded and tears down when collapsed. At most one child run is subscribed per open panel; no background subscriptions are held for collapsed subtasks.

While a subtask is expanded, the parent subtask card header also shows an **aggregate elapsed time** — the sum of the child pipeline steps' durations (each step's `completedAt − startedAt`, or `now − startedAt` while still running). It ticks live (1s) when any child step is in progress and is labelled `aria-label="Total child elapsed"`.

A **View run** link navigates to the child run's full workflow page (see [Child run View-run resolution](#child-run-view-run-resolution)).

#### Coordinator node and orchestration status

The coordinator node's status reflects the **orchestration lifecycle** rather than a stale `pending`. The lifecycle phase is derived (in priority order) from live `coordinator.assembly_*` events, then the optional `coordinator_status` field on the run summary / run detail, then the work-plan status — all read defensively so the page degrades gracefully whether or not those backend fields are present. The phase is mapped to `running` / `completed` / `failed` for the node badge and shown as a label (`Dispatching`, `Awaiting assembly`, `Assembling`, `In review`, `Complete`, `Failed`, `Blocked`, `Declined`) next to the graph title.

The coordinator node also carries a **View session** button that scrolls to the coordinator session panel (provided via `CoordinatorSessionContext`).

#### Coordinator session panel and steering chat box

The right column hosts an all-up **Coordinator session** panel:

- A **timeline** derived from the coordinator's own event stream — `coordinator.started` (goal), outcome spec confirmed, work plan ready, each `subtask.*` transition, `coordinator.children_complete`, and the `coordinator.assembly_*` milestones — each with a relative elapsed offset from the first timestamped milestone.
- An **Action required** block (above the timeline) that surfaces bubbled child questions and tool-approval requests re-projected onto the coordinator stream (see below).
- A persistent **steering chat box** (a text area + **Send** button, plus quick **Redirect** and **Stop** affordances) that submits free-form steering via `POST /api/runs/{id}/steer` (default `kind: "amend"`) **without** opening a dialog. Queued/applied steering directives from `coordinator.steering` events are listed below the box.

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

- A child **question** renders the same answer card used on the run page, but the answer is POSTed against the **`childRunId`** from the payload (`apiClient.answerQuestion(childRunId, requestId, value)`), **not** the coordinator run id — the child is the run that is blocked.
- A child **approval** reuses the existing HITL tool-approval card (`LifecycleEventCard` with a synthetic `tool.approval_required` event) targeted at the **`childRunId`**, so Allow/Deny POST against the child's `tool-approvals`/`tool-denials` endpoints. The tool name, URL, and message are shown.
- Each item collapses once resolved (a question on `agent.question_answered` for the same `requestId`, or optimistically on submit; an approval on the card's own allow/deny action).

#### Assembly-review affordance

When the orchestration reaches the collective human-review stage, the page presents a clear next action instead of a bare status:

- **`awaiting_assembly` / `assembling`** — an "Assembling collective output…" panel with a spinner.
- **`in_review`** (or a `coordinator.assembly_review_requested` event) — an **Assembly review** panel that surfaces the integration diff/summary (read from the event payload's `diff` / `summary` / `treeHash` fields) and **Approve** / **Request changes** / **Decline** buttons. These POST to `POST /api/runs/{coordinatorRunId}/assembly/review` via `apiClient.reviewAssembly(runId, { decision, comment? })`. A comment is required for request-changes and decline.
- **`failed` / `blocked` / `declined`** — the human-readable **reason** (from the `coordinator.assembly_failed`/`blocked`/`declined` event payload or `coordinator_status_reason`) plus guidance that the subtasks are parked and can be redirected/amended via the steering chat box. The stuck state never renders a bare "Failed" with no explanation.

#### Steering bar

A page-level steering bar sits above the React Flow canvas. Three buttons are always available while the coordinator run is active:

- **Stop** — sends `{ kind: "stop" }` to `POST /api/runs/{id}/steer`
- **Redirect** — opens a dialog to enter an instruction; sends `{ kind: "redirect", instruction: "..." }`
- **Amend** — opens a dialog to enter an instruction; sends `{ kind: "amend", instruction: "..." }`

The steering bar is always visible on the coordinator run page even for finished runs (buttons remain rendered; the API will reject the call if the run is not active). The same steering actions are also available inline on the page via the steering chat box (no dialog required) — see [Coordinator session panel and steering chat box](#coordinator-session-panel-and-steering-chat-box).

### Watch a run

The watch screen streams events with `fetch`, not `EventSource`, so it can send the bearer key and `Last-Event-ID`. The stream reconnects after a drop and deduplicates by `sequence`. Reconnection replays from the in-memory buffer while the run's entry is retained on the server.

#### Workflow graph

The workflow run page (`/projects/:projectId/runs/:runId/workflow`) shows a live graph of the executor pipeline. Each pipeline node card displays:

- A **status badge** reflecting the current `workflow.step` event status (`Pending`, `In Progress`, `Complete`, `Skipped`, `Failed`, `Revise`)
- An **elapsed timer** that ticks live from the `started` event's `timestamp_utc` until the corresponding `completed`/`failed` event
- An optional **status message line** — when the backend emits a `workflow.step` event with a `message` field, that text is rendered below the role description in a muted colour. It takes priority over the hardcoded fallback description; omitting `message` restores the default text.

For coordinator child runs (runs with a non-null `parent_run_id`), the page renders a trimmed three-node pipeline: Agent → Rai → Assemble-ready. Human Review, Merge, and Scribe are never shown on a child run — they execute once on the collective output at the coordinator level. This trimming is enforced defensively in two ways: (1) the page renders a **loading spinner** (not any graph) until the run detail resolves and child-ness is known, so a child run never flashes the full Agent → … → Scribe placeholder before the trimmed pipeline is selected; and (2) if a full-variant `GraphDescriptor` somehow arrives for a child run (e.g., a stale cache entry), the page discards it and falls back to the hardcoded child pipeline.

#### Child run View-run resolution

The workflow run page resolves a run by first looking it up in `GET /api/projects/{id}/runs`. That list **excludes coordinator child runs** (the server filters it to parent runs, `parent_run_id IS NULL`). When the route's run id is not found in the list, the page falls back to `GET /api/runs/{id}` (`apiClient.getRun`), which **does** return child runs, and resolves:

- `executionId = runId` directly — for a child run the child RunId is itself the stream/graph key (the same key the inline subtask expansion uses), so it drives `getRunGraph(runId)` and `useRunStream(runId)`;
- `parentRunId` from `parent_run_id` (so `isChild` trims the pipeline), `runStatus` from `status`, `agentName` from `agent_name`, and the model from `model_source`.

Without this fallback the child "View run" link previously left `executionId` unset, so the page spun forever on an all-"Pending" full pipeline. The trimmed child pipeline plus the persisted-events seed (`SEED_STATUSES`) then render the child's live/terminal status.

#### Run header

A header bar shows the shortened run ID alongside a status indicator: a spinner while connecting or streaming, a success badge when done, or an error badge on failure.

For an active, non-child run the header also hosts an **Auto-approve tools** switch, seeded from the run detail's `auto_approve_tools` boolean (optional, default `false`) and flipped via `apiClient.setAutoApprove(runId, enabled)` → `POST /api/runs/{id}/auto-approve` `{ enabled }`. It is optimistic (flips immediately, reconciles to the server boolean, reverts on error) and targets the resolved execution/run id. It is hidden for coordinator **child** runs (they inherit the coordinator's cascade) and for terminal/parked runs (the endpoint returns `409` when the run is not active). Autopilot is **coordinator-only** and is not shown here. Tooltip copy notes that dangerous tools remain blocked by policy.

When automation acts, two **muted audit lines** (the same `size={100}` / `colorNeutralForeground3` treatment as `agent.intent` and the "Used N tools" rows — not prominent cards) appear in the timeline:

- `tool.auto_approved` `{ requestId, toolName, url? }` → *Tool auto-approved: {toolName} {url?}*.
- `coordinator.autopilot_answered` `{ runId, childRunId?, requestId, question, answer }` → *Autopilot answered{ (child {id})}: {question} → {answer}*.

Both event types are routed through the timeline reducer's lifecycle group and rendered by `LifecycleEventCard`; payload keys are read defensively (`toolName`/`tool_name`, `childRunId`/`child_run_id`).

#### Bubbled questions (answer affordance)

When a worker (or any agent) blocks awaiting an answer, the backend emits `agent.question_asked` `{ requestId, question }` on the run stream (and persists it). Below the run header the page renders one **answer card** per `requestId` that has no matching `agent.question_answered`:

- An unanswered question shows a prominent, brand-stroked card (matching the HITL tool-approval card treatment) with the question text, a textarea, and a **Submit answer** button that calls `apiClient.answerQuestion(runId, requestId, value)` → `POST /api/runs/{id}/questions/{requestId}/answer`.
- On submit the card optimistically collapses to a muted answered state; it also collapses when the matching `agent.question_answered` `{ answer, timedOut }` arrives, showing the answer (or a **"Question timed out"** hint with the auto-resolved value when `timedOut` is true).

Payload keys are read defensively (`requestId`/`request_id`, `timedOut`/`timed_out`) so minor backend casing differences degrade gracefully.

#### Timeline

Events are grouped into turns. Each turn opens with a divider that reads **Turn N · X steps** and shows a live spinner while the turn is in progress or a checkmark once it closes. A completed turn that received no steps is not shown — it produces no divider or content in the timeline.

Inside each turn, two kinds of steps appear:

**Agent message bubbles** — the agent's text output, rendered with a bot icon on the left. On a live in-progress run, text arrives token-by-token and a blinking cursor follows the end of the accumulated text. Once the server confirms the message is complete, the content is rendered as Markdown: headings, lists, inline code, fenced code blocks, block quotes, and tables. Headings use the Fluent type-ramp scale (h1 → Base500, h2 → Base400, h3/h4 → Base300) so they stay visually consistent with the rest of the UI. Links open in a new tab with `rel="noopener noreferrer"`. When opening a completed run after the fact, the full message content is replayed at once with no cursor — this is expected behaviour, not a bug.

Markdown is sanitized using rehype-sanitize with the default allowlist schema. `rehype-raw` is not included, so any raw HTML in agent output is neutralised rather than rendered. All text fields are React text nodes; `dangerouslySetInnerHTML` is not used anywhere in the rendering pipeline.

**Tool call cards** — each tool call renders as a collapsible accordion card with a wrench icon. The header shows a status indicator and a human-readable title derived from the tool name and key argument, for example **Read file · src/index.js** or **Run command · npm test**. Inside the card, the arguments are shown as formatted JSON, and the result or error appears once settled.

A tool call with no result yet shows a spinner in the header. A regular error shows a red error badge; a sandbox or path-restriction violation shows a yellow warning badge and the card **auto-expands** (so the error is visible without a click). Expanding a tool cluster shows only the first level (individual tool rows) collapsed — click a row to expand its detail pane. Tool clusters with no errors default to collapsed. Both the arguments and the output are plain escaped text — no HTML is interpreted.

**Inline approval cards** — when the agent calls a tool that requires human approval, an **approval card** appears inline in the current turn's timeline (not at the bottom). The card shows:
- The tool name as a badge
- The resource URL (scrollable, monospace)
- Four action buttons: **Allow once**, **Allow this run**, **Always allow (session)**, **Deny**
- An optional intention description

After taking action, the card collapses to a single line: `✓ Allowed (once) · web_fetch` or `✗ Denied · web_fetch`. On page reload, already-actioned cards remain collapsed.

**Lifecycle event cards** — events such as `run.completed`, `run.failed`, `review.requested`, and the merge outcome are shown as flat cards outside any turn group, with a colour-coded icon and badge. When the agent reports `run.outcome(achieved: false, ...)`, the `run.completed` lifecycle card renders with an amber warning indicator and the reason text instead of the normal green success badge.

#### Review gate

When the run reaches the review gate, a diff viewer and an inline review panel appear below the timeline. See [Review a run](#review-a-run) below.

### Review a run

The review panel is embedded in the watch screen. When the agent emits a `review.requested` event, the watch screen fetches the run, shows the diff, and renders a details table alongside the review panel. The panel shows the tree hash and two buttons: Approve and Decline.

**Approve** can have three outcomes:

- **Merge succeeds** — the run transitions to `merged` and a green success badge appears showing the commit hash. The transition happens live via the `merge.completed` event on the SSE stream; you do not need to refresh the page.
- **Retriable block** — the server returns a 409 with an error message (for example, because there are uncommitted local changes). The panel shows the server message as a warning bar and keeps Approve and Decline active so you can fix your working tree — commit or stash the changes — and approve again.
- **Terminal merge failure** — if the merge fails in a way that cannot be retried, a red `merge_failed` badge appears with the failure reason. The review panel re-appears so you can attempt another approve after resolving the conflict manually, or decline the run.

**Decline** records the decision and the run transitions to `declined`.

### Artifact browser

A resizable split-panel layout divides the watch screen: the timeline occupies the left panel and the artifact browser occupies the right panel. The browser is available whenever the run has a worktree — from the moment the run starts through any awaiting-review state.

The browser has two tabs:

**Files tab** — shows the worktree's file tree. Files modified by the agent appear with a colour-coded status badge (`A` added, `M` modified, `D` deleted). Clicking a file opens it in a read-only Monaco editor or CommonMark preview (for `.md` files). The editor shows a diff view highlighting agent changes against the originating branch baseline. The file list polls every few seconds on in-progress runs to pick up new changes as the agent writes them. On 409 (worktree unavailable), polling stops automatically.

**Changes tab** — shows a flat list of all changed files with `+N / -N` line counts. Clicking a file opens the same Monaco diff view.

### Team page

The team page (`/projects/:projectId/team`) shows the current cast team as a card grid. Each card displays the agent's name, role, and a status badge (**Active** or **Retired**).

Clicking a card opens a slide-in panel with three tabs:

- **Overview** — member summary, role, status, and charter timestamps (created and last updated)
- **Charter** — the agent's full charter text in a read-only view
- **Capabilities** — role capabilities pulled from the catalog

Filter tabs at the top of the grid narrow the view: **All**, **Active**, **Retired**.

Two action buttons appear in the page header:

**Add member** — opens a dialog to select a role from the full catalog and cast a new team member directly, without going through the casting wizard.

**New Run** — opens the New Run dialog (see below).

A **Cast team** button navigates to the casting wizard at `/projects/:projectId/team/cast`.

The sync panel at the bottom of the page shows the pending uncommitted changes fetched from `GET /api/projects/{id}/team/sync`. Each changed file is listed with its status (`added`, `modified`, or `deleted`). A **Commit** button opens a dialog to enter an optional commit message and then calls `POST /api/projects/{id}/team/sync` with the change set hash. If the change set shifts between the panel load and the commit, the server returns a conflict and the panel shows an error with a prompt to refresh.

### Workflow run page

The workflow run page (`/projects/:projectId/runs/:runId/workflow`) is the primary screen for monitoring a run. It renders the pipeline stages as a horizontal React Flow diagram.

#### Dynamic graph descriptor

The page fetches a graph descriptor from `GET /api/runs/{id}/graph` on load and applies any `run.workflow_graph` SSE snapshot (highest `seq` wins). When the descriptor is available it drives all rendering; when the endpoint returns 404 or is not yet deployed, the page falls back to the hardcoded executor lists below.

The descriptor shape (snake_case JSON from the backend):

| Field | Type | Description |
| --- | --- | --- |
| `graph_id` | string | Opaque identifier |
| `variant` | `"full"` \| `"child"` \| `"coordinator"` | Graph variant |
| `start_node_id` | string | Id of the first node |
| `nodes` | `GraphNode[]` | Ordered list of pipeline stage nodes |
| `edges` | `GraphEdge[]` | Directed edges (forward and loopback) |

**`GraphNode`**

| Field | Type | Description |
| --- | --- | --- |
| `id` | string | Logical step key matching the status reducer (`agent`, `rai`, `review`, `merge`, `scribe`, `assemble-ready`, `coordinator`, `plan:subtask-{n}`, `planned:assembly-*`) |
| `label` | string | Card title shown in the UI |
| `role` | string | Drives icon and color (`agent`/`rai`/`review`/`merge`/`scribe`/`coordinator`/`subtask`/`assembly`) |
| `kind` | `"live"` \| `"planned"` | `planned` nodes render with a dashed border and muted opacity; they never show a pending spinner |
| `node_type` | `"agent"` \| `"action"` \| `"gate"` \| `"terminal"` \| `"subtask"` (optional) | Structural category that drives card shape and size (see below) |
| `child_graph_ref` | string? | Reference to child descriptor in the form `"run:{childRunId}"` (subtask nodes) |
| `child_run_id` | string? | Child run ID (flat optional field, same value as the id suffix in `child_graph_ref`) |
| `agent` | string? | Assigned agent name (subtask nodes) |
| `model` | string? | Selected model ID (subtask nodes) |
| `phase` | string? | Execution phase (subtask nodes) |

**`GraphEdge`**

| Field | Type | Description |
| --- | --- | --- |
| `from` | string | Source node id |
| `to` | string | Target node id |
| `cardinality` | `"direct"` \| `"fanout"` \| `"fanin"` | Edge multiplicity |
| `loopback` | boolean | `true` = back-edge excluded from dagre layout, drawn as a loopback arc above/below the row |

**Status projection** — node `id` equals the logical step key the existing status reducer uses, so status is a direct lookup by id. `planned` nodes are always rendered as "Planned" regardless of any events.

#### node_type → card shape and size

The `node_type` field on `GraphNode` drives card dimensions and visual shape in the `WorkflowNode` renderer. Color remains driven by `role`; `node_type` only controls size and shape.

| `node_type` | Card width | Card height | Visual treatment |
| --- | --- | --- | --- |
| `agent` | 220 px | 160 px | Largest card; primary importance |
| `subtask` | 220 px | 180 px | Expandable card (see coordinator view); taller to show agent/model/phase |
| `gate` | 180 px | 130 px | Decision-point shape with dashed border |
| `action` | 170 px | 130 px | Smaller secondary card (Merge, Scribe) |
| `terminal` | 150 px | 110 px | Smallest card; endpoint/checkpoint (Assemble-ready) |
| *(absent)* | 200 px | 145 px | Default size |

`planned` nodes always use the default width class regardless of `node_type`, since planned assembly nodes are intentionally muted.

The `data-node-type` HTML attribute on each rendered card card reflects the node's `node_type` value (or `"default"` when absent), enabling CSS-based targeting in tests and tooling.

**Fallback** — when the descriptor endpoint returns 404 or is unavailable, the page falls back to the hardcoded five-stage pipeline (`Agent → Rai → Review → Merge → Scribe`) for a normal run or the trimmed three-stage pipeline (`Agent → Rai → Assemble-ready`) for a coordinator child run, so nothing regresses until the backend ships.

#### Pipeline stages (hardcoded fallback / full variant)

**Agent → Rai → Review → Merge → Scribe**

Each card shows:
- **Stage name and role description** — Agent (AI Assistant), Rai (RAI Reviewer), Review (Human Review), Merge (Merge Coordinator), Scribe (Session Logger)
- **Status badge** — Pending, Planned, In Progress, Awaiting, Complete, Skipped, Failed, or Revise (Rai only)
- **Elapsed timer** — running clock while the stage is active; freezes on completion
- **Description text** — current activity (e.g. "Working on task...", the latest `agent.intent` text, "Passed", "Skipped")
- **Agent identicon** — circular avatar for the agent executor, matching the identicon on the team page
- **Model name** — displayed on the agent card when a model is known
- **Reviewer avatar** — on the Review card, once a human has reviewed, shows the reviewer's GitHub profile picture and username

Loop-back arcs (Rai → Agent for revision, Review → Agent for request-changes) are highlighted in blue while the loop is active.

Clicking **View Execution** on any completed card opens the execution modal.

### Execution modal

The execution modal shows the full event timeline for an individual executor's run — agent messages, tool call cards, approval cards, and lifecycle events. The modal is non-scrollable at the outer level; the inner timeline panel has its own scrollbar. Close with the × button or click outside.

### Team Memory page

The team memory page (`/projects/:projectId/memories`) surfaces the durable knowledge recorded by the team across all runs.

Two tabs:

**Decisions & Memory** — decisions recorded by agents (via `submit_decision` / `decision_create`). Each entry shows title, type badge (architectural, process, scope, technical), agent badge, and creation time. Decisions are ordered newest-first.

**RAI Audit** — memory entries recorded by Rai (via `record_memory`). Each entry shows importance badge (high/medium/low), type, and content.

Both tabs fetch live from the API; data is cached for the session tab switch.

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

On the team page, clicking **New Run** opens a dialog with:

- **Agent** — dropdown of active team members showing name and role
- **Task** — multi-line text area describing what to do
- **Branch** — branch to run against (defaults to the project's default branch)

Submitting starts a project-scoped run via `POST /api/projects/{id}/runs` with the selected agent's name in `agent_name`. The agent's charter is injected as their system prompt. The new run appears immediately in the Recent Runs section at the bottom of the team page.

### Recent Runs section

Below the team member grid on the team page, a collapsible **Recent Runs** section lists all runs for the project fetched from `GET /api/projects/{id}/runs`. Each entry shows:

- Agent name (which team member ran it)
- Task description (truncated)
- Status badge (color-coded: warning for in-progress, success for completed, danger for failed)
- Started time

Clicking a run navigates to the watch screen for that run.

## Structure

```text
src/
  api/
    types.ts            API shapes
    client.ts           fetch-based API client
    apiClient.ts        shared client built from config
    sse.ts              run-stream hook
  components/
    RunSubmitForm.tsx
    RunWatcher.tsx      orchestrates the watch screen
    RunHeader.tsx       run ID + stream status indicator
    Timeline.tsx        renders the ordered list of timeline items
    TurnGroup.tsx       one agent turn: divider + steps
    TurnDivider.tsx     "Turn N · X steps" header with active/done indicator
    AgentMessageBubble.tsx  streaming plain-text or settled Markdown bubble
    ToolCallCard.tsx    collapsible card: icon + title + args + result/error
    LifecycleEventCard.tsx  flat card for run/review/merge lifecycle events
    ReviewPanel.tsx
    RunDetail.tsx
    DiffViewer.tsx      syntax-highlighted unified diff component
    ArtifactBrowser.tsx resizable split-panel file tree + Monaco/markdown viewer
    FileViewerModal.tsx read-only Monaco diff viewer and CommonMark preview modal
    GitHubSignIn.tsx    header component: device-flow sign-in, polling, sign-out
    StartOrchestrationDialog.tsx  goal entry that starts a coordinator run
    OutcomeSpecPanel.tsx  outcome-spec review with confirm/revise gate
    WorkflowGraphPanel.tsx  shared generic graph renderer: WorkflowNode, LoopbackEdge,
                            styles (node_type → card size), helpers, contexts
  timeline/
    types.ts            discriminated union types for reducer state
    reducer.ts          pure grouping reducer (turns, steps, streaming state)
    useTimelineItems.ts hook that feeds the SSE event list into the reducer
  pages/
    ProjectGalleryPage.tsx  home: project card grid, create-blank and create-from-GitHub dialogs
    ProjectPage.tsx         project detail, run list, start-run dialog
    ProjectSettingsPage.tsx provider defaults, rename, relink, delete
    TeamPage.tsx            team roster, member management, charter dialogs, sync panel
    CastingWizardPage.tsx   Single-page casting wizard (Formulate / Template / Analyze tabs)
    WatchPage.tsx
    WorkflowRunPage.tsx     live workflow graph (descriptor-driven or fallback hardcoded)
    CoordinatorRunPage.tsx  coordinator run page: outcome-spec gate + unified graph + steering
    SettingsPage.tsx        API connection settings
    HomePage.tsx            legacy submit form (not the default route)
  App.tsx               Fluent provider and routing
  main.tsx              entry point
  config.ts             reads VITE_API_URL and VITE_API_KEY
```

