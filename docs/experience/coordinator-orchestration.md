# Coordinator orchestration experience

Coordinator orchestration turns one plain-language goal into a dependency-aware work plan, specialist child runs, and one assembled result. **Define Outcome** drafts an OutcomeSpec and waits for human confirmation before dispatch. **Direct** starts from the goal without that confirmation step. **Ready pickup** reserves a coordinator run atomically and starts it unattended, with automatic outcome confirmation attributed to the task's capturing identity. These start modes retain later review, tool-approval, assembly, and merge gates; they do not all require an interactive confirmation.

Related experience docs: [Runs & board](./runs-board-watch.md), [MCP client](./mcp-client.md), [Projects](./projects.md), and [Review, workspace & merge](./review-workspace-merge.md). Related grounding references: [Coordinator reference](../reference/coordinator.md), [Coordinator internals](../deep-dive/coordinator-internals.md), [Orchestration engine](../deep-dive/orchestration.md), and [Team casting](../deep-dive/team-casting.md).

Open **Orchestrations** from a project to inspect its coordinator runs and their available **Open**, **Stop**, and **Delete** actions. The previous image was a placeholder, not a product capture; this page describes the implemented controls without presenting it as evidence.

## The experience in one sentence

The coordinator is a project-level team manager. The user describes the outcome, chooses whether to review a structured plan first, then watches subtasks move through specialists, dependencies, steering, assembly, and final review.

The experience is not a faster way to skip review. It makes the work larger and more parallel while keeping the user's intent, intervention points, and final accountability visible.

## Starting from a plain-language goal

### In the web UI

The user starts from a project by choosing **Start task**. The dialog asks for a **Goal** and an optional **Workflow**. The global **Start task** button offers the same entry point from pages without that action.

Before starting, the project must have a cast team with at least one active worker role. If the API returns `no_team`, the dialog and global **Start task** button show a warning with the backend message and a **Cast a team** call to action that routes to `/projects/{id}/team/cast` (`apps/web/src/api/errors.ts:35`, `apps/web/src/components/StartOrchestrationDialog.tsx:145`, `apps/web/src/components/StartOrchestrationFab.tsx:141`). Casting the team is now a prerequisite for orchestration, not an optional setup step.

When the user selects **Define Outcome**, the UI posts the goal and opens the coordinator run. The coordinator then drafts an OutcomeSpec. **Direct** also creates a coordinator run, but starts from the goal without the outcome-confirmation step.

That matters. A broad request such as "Add OAuth sign-in and update the docs and tests" can mean many things: provider choice, scope boundaries, migration expectations, documentation depth, and test coverage. **Define Outcome** first turns that request into a confirmable contract instead of launching child agents immediately.

What the drafted **Scope** covers tracks the breadth the goal asks for, filtered by the team you cast. A full-journey goal — "take this from the initial idea all the way to a working, previewable app" — yields an outcome whose scope enumerates the intermediate deliverables the goal implies (customer/market research, positioning/marketing, user stories, a PRD, UX design, and the built app), but only for deliverables some role on the team can actually produce. A narrow goal (a bug fix or a single document) stays lean and does not sprout extra planning stages. If the breadth is unclear, the coordinator surfaces it as a clarifying question rather than assuming the widest scope — so review the drafted scope before confirming and revise if it is broader or narrower than you intended.

### Over MCP

MCP starts the same experience with `coordinator_start`:

- `project_id` selects the project.
- `goal` is the plain-language outcome the user wants.
- `model_id` optionally overrides the coordinator model for the planning run.
- `start_mode` selects `defineOutcome` (the default) or `direct`; `workflow_id` can pin a workflow.

`coordinator_start` returns the created coordinator run. In its default `defineOutcome` mode, it drafts an OutcomeSpec and suspends before decomposition and child dispatch. With `start_mode: "direct"`, it skips that interactive planning gate. The convenience tool `run_task` defaults to direct mode and returns at completion, a gate, or its timeout.

Use MCP when the orchestration starts from an assistant workflow, a CLI session, or another tool that can hold the run id and continue with the coordinator tools. Use the web UI when the user wants the visual confirmation gate and live topology from the start.

## The confirmation gate in Define Outcome mode

In **Define Outcome** mode, the coordinator deliberately pauses before child work begins. The OutcomeSpec is persisted with an awaiting-confirmation state, the web page shows the **Outcome spec** panel, and the system tells the user: **No subagent work is dispatched until you confirm this outcome spec.**

This is the planning contract of **Define Outcome**, not a universal property of every start. It gives the user a moment to say "yes, that is what I meant" before fan-out. Direct mode treats the submitted goal as the instruction; unattended pickup supplies its own automatic confirmation.

### What suspension feels like

In the UI, the graph area remains intentionally quiet while the spec is being authored. The detail page may show the live coordinator node and a hint that the execution pipeline appears once the OutcomeSpec is confirmed. The coordinator session can stream planning activity, but the subtask graph and child pipelines are not active yet.

The left-side **Outcome spec** panel carries the action from the start of the run. It no longer disappears when `GET /api/runs/{id}/outcome-spec` returns an expected early `404`; instead it stays visible with a **Drafting** badge and polls until REST or SSE delivers the draft (`apps/web/src/components/OutcomeSpecPanel.tsx:160`, `:233`, `:328`). It displays the goal, desired outcome, scope, assumptions, and clarifying questions once they arrive. While the coordinator is still drafting, the panel shows a spinner with **Drafting the outcome spec...**. During revision, it shows **Coordinator is incorporating your changes and re-drafting the spec...**. If the run fails, is declined, or merge-fails before any draft content lands, the panel shows **The run failed before the outcome spec could be drafted.** rather than hiding the gate (`OutcomeSpecPanel.tsx:162`, `:401`, `:537`).

### What the gate prevents

The gate prevents accidental fan-out. Without it, each child agent could receive an ambiguous version of the original request and independently choose scope. With it, every downstream subtask is grounded in the same confirmed OutcomeSpec.

The gate lets a user inspect scope, exclusions, and assumptions before child execution. Drafting itself can consume model usage, and confirmation is not a cost ceiling or a guarantee that execution will need no further judgment.

## OutcomeSpec experience

### Viewing the drafted spec

The web **Outcome spec** panel is the user's readable view of the current spec. MCP reads the same persisted artifact with `coordinator_outcome_spec_get`.

The spec presents:

- **Goal** — the original user request.
- **Desired outcome** — the coordinator's concise description of successful completion.
- **Scope** — what is included and excluded.
- **Assumptions** — decisions the coordinator is making unless corrected.
- **Clarifying questions** — targeted questions the coordinator wants answered before dispatch.
- **Status** — drafting, awaiting confirmation, confirmed, or declined.
- **Confirmed by** — shown after confirmation when present.

The panel renders the server-authored fields as the source of truth. If the coordinator returns a single paragraph, the UI shows a paragraph. If it returns lists, the UI shows lists. The user is evaluating the coordinator's stated intent, not a client-side rewrite.

Over MCP, `coordinator_outcome_spec_get` is the snapshot tool. It is useful after `coordinator_start`, after a web user asks for changes, or after a stream reconnect when the client wants the latest persisted spec without replaying events.

### Confirming the spec

When the status is **Awaiting confirmation**, the UI offers **Confirm**. Clicking it confirms the spec and resumes the suspended coordinator run. While the request is in flight, both actions are disabled, the button label changes to **Confirming...**, and a spinner appears so a double-click cannot submit the gate twice (`apps/web/src/components/OutcomeSpecPanel.tsx:237`, `:338`, `:578`, `:588`). On success, the panel switches to **Confirmed** and shows **Outcome spec confirmed... Dispatch is unblocked.** The detail page then makes room for the coordinator graph and child execution experience.

Over MCP, `coordinator_outcome_spec_confirm` performs the same action. It confirms the current drafted OutcomeSpec for the coordinator run and resumes the run past the gate. This is the transition that allows the coordinator to select a workflow shape, decompose the work plan, persist subtasks and dependencies, and begin dispatching ready children.

Confirmation is not just an acknowledgement. It is the user's approval that the coordinator's interpretation is the execution contract for the rest of the orchestration.

After the user clicks **Confirm**, the web UI automatically reconnects the live stream. The coordinator stream closed at the `awaiting_confirmation` gate; confirmation resumes the run and reopens the stream so the topology, subtask graph, and coordinator session update in real time without a manual page refresh. If the confirming SSE event arrives slightly later, the panel still keeps the terminal **Confirmed** state instead of briefly flipping back to an older drafting or awaiting-confirmation snapshot. A fast click that reaches the backend before the in-memory gate is fully armed can return `409 no_pending_gate`; the UI retries that short race, refreshes the spec on 409 conflicts, and shows a clear error if the run is no longer active (`OutcomeSpecPanel.tsx:345`, `:360`, `:186`).

### Requesting a revision

If the spec is close but not right, the user chooses **Clarify and request changes**. The dialog asks the user to describe what should change. If the coordinator included clarifying questions, the dialog shows those questions as answer fields and includes an **Additional feedback** field. The dialog explains the loop clearly: after sending feedback, the coordinator re-drafts and re-presents the spec for confirmation; no subagent work is dispatched until the user confirms.

Over MCP, `coordinator_outcome_spec_revise` takes `feedback`. The coordinator uses that feedback to produce a fresh draft and suspends again at the same confirmation gate. The user can repeat this loop until the OutcomeSpec is accurate.

Revision is the right action when the desired outcome is correct in spirit but wrong in boundary. Examples: narrow the scope to docs only, require a specific API version, exclude migration work, add tests as a success condition, or answer a clarifying question that changes the plan.

### Why revision re-suspends

Revision does not resume work. It returns the run to the awaiting-confirmation state because the artifact that controls execution has changed. The user gets a fresh chance to inspect the new desired outcome, scope, assumptions, and questions before the coordinator creates the work plan.

Within this planning mode, a revised interpretation is presented for confirmation again before dispatch.

## Choosing the workflow

Before decomposition, the coordinator selects **which workflow** the work should follow. For Define Outcome this follows confirmation; Direct and unattended starts reach selection without waiting for the same human gate. Projects with a single eligible workflow do not need a selection prompt.

### What decides the workflow

You influence the choice in four ways, from strongest to softest:

- **Backlog override.** A backlog task can pin a specific workflow. When the coordinator picks that task up, it uses the pinned workflow as long as it is valid and its trigger fits the pickup.
- **Conversational override.** In a revision, reply with `use <workflow-id>` (for example `use bug-fix`). This wins over the coordinator's own pick, as long as the named workflow is one of the eligible candidates.
- **Blueprint restriction.** A project's blueprint can limit the set of workflows available to the project (`AllowedWorkflowIds`) and set the default. The built-in `default` workflow always stays available as a safety net, so the project is never left with zero choices.
- **Automatic selection.** When two or more workflows are eligible and you haven't named one, a Copilot-backed selector picks the best **process fit** for the goal and team, by what steps each workflow runs and what it produces — not by name similarity.

### Manual vs backlog (heartbeat) invocation

How the run started narrows the candidates before anything else, so a workflow only appears if its declared trigger matches:

- **You started it (manual).** Only workflows with a `manual` trigger are considered.
- **The heartbeat picked up a Ready task.** Only `heartbeat`-trigger workflows, plus event workflows that fire on "task added to Ready", are considered.

A workflow whose trigger does not match the invocation is never selected, even if you name it. When nothing matches, the coordinator falls back to the project default rather than running a mismatched process.

### What you'll see when the coordinator selects

When a project has multiple eligible workflows, the coordinator surfaces its choice as a `coordinator.workflow_selected` event on the run stream. The orchestration header shows the chosen workflow as a **badge** next to the "Orchestration" title (workflow name, an `· auto` suffix when the coordinator picked it automatically, and the rationale on hover), so you can see at a glance which process this run is planned against. The badge is driven by the persisted event, so it survives page reloads. The event carries:

- `selectedId` and `selectedName` — the workflow it chose.
- `rationale` — a one- or two-sentence reason for the pick (or an explanation of why it fell back to the default).
- `wasAutoSelected` — `true` when the selector chose it, `false` when you named it with `use ...`.
- `overrideHint` — a reminder you can reply `use {other-id}` to change it, listing the available ids.
- `available` — the full list of workflows you could pick instead.

If anything goes wrong (the model is unavailable, returns an unusable answer, or names a workflow that isn't available), the coordinator deterministically falls back to the project default and says so in the rationale — you are never left without a workflow. Single-workflow projects emit no such event, because there was nothing to choose.

## Work plan experience

### From accepted intent to executable plan

After the selected start path accepts the intent, the coordinator creates a work plan. It decomposes that intent into bounded subtasks, assigns agents and models, records status, attaches child run ids after dispatch, and stores dependency edges.

MCP reads the persisted plan with `coordinator_work_plan_get`. The response includes the coordinator run id, the OutcomeSpec id, the work plan status, optional isolation summary, subtasks, and dependency rows.

Each subtask includes:

- `subtaskId` — the stable subtask identifier.
- `title` — the user-readable unit of work.
- `scope` — the owned work or output boundary.
- `assignedAgent` — the cast agent selected for the subtask.
- `selectedModelId` — the model selected for that subtask.
- `phase` — planning, execution, validation, or another server-authored phase.
- `isolation` — an advisory worktree/shared hint.
- `status` — pending, dispatched, running, assemble-ready, completed, failed, blocked, or RAI-flagged.
- `childRunId` — present once the subtask has a dispatched child run.

Dependency rows point from prerequisite to dependent. In the topology, edges mean "this must finish before that can run." The coordinator advances only the ready frontier: pending subtasks with all dependencies satisfied can dispatch; blocked predecessors keep dependents from running.

### What the user sees in the graph

The orchestration detail page titles the graph section **Coordinator Graph**. It shows the current orchestration phase next to the title when available, such as **Dispatching**, **Awaiting assembly**, **Assembling**, **In review**, **Complete**, **Failed**, **Blocked**, or **Declined**. A live spinner appears while the stream is connected.

The hint explains the graph's job: **Live view of the coordinator and its subtasks. Expand a subtask to see its pipeline, or use the steering controls to send a course-correction to the coordinator or stop the orchestration.**

Subtask cards show the subtask title, assigned agent, role label when available, selected model, phase, status badge, and elapsed time. Status labels are direct and operational: **Pending**, **Dispatched**, **Running**, **Awaiting assembly**, **RAI flagged**, **Completed**, **Failed**, and **Blocked**. A subtask that has finished its part but is waiting for the parent shows the note **Finished its part — waiting for collective assembly**.

The graph uses a top-down dependency layout. The coordinator sits at the top and subtasks flow
downward, each rank appearing below its prerequisites. Ranks are centered and aligned, so parallel
subtasks line up horizontally under their shared parent, and dependency edges are drawn as clean
vertical connectors rather than wavy S-curves. The result reads as a tidy spine: the user can see at
a glance which work is parallel, which work is serial, and which node is blocking the rest.

### Live topology updates

The graph is not computed from guesses in the browser. It is seeded from REST snapshots and then updated from coordinator run stream events.

The live stream events that matter are:

- `coordinator.work_plan` — records the persisted plan and subtask list.
- `coordinator.topology` — provides graph snapshots and deltas.
- `subtask.dispatched` — a child run has been launched for a subtask.
- `subtask.running` — the child is actively working.
- `subtask.assemble_ready` — the child finished its part and is ready for parent assembly.
- `subtask.rai_flagged` — the child hit a responsible-AI block.
- `subtask.completed` — the subtask completed.
- `subtask.failed` — the subtask failed.
- `coordinator.steering` — a user directive was queued, relayed, applied, or otherwise updated.

`coordinator.topology` carries a versioned graph. The initial snapshot includes one coordinator node, one node per subtask, and dependency edges. Later deltas carry changed nodes. Edges are stable after the snapshot because the dependency structure does not change casually while work is running.

Each node in the topology carries an `executionPodName` field. The UI renders a compact pod chip above the card when this field is non-null and non-empty:

- **Coordinator node** — shows the API pod name when the coordinator process is running inside Kubernetes; null otherwise.
- **Subtask node** — shows the pod name of the child run's bound AgentHost pod, populated by the backend from the pod registry (`IPodNameRegistry`) once the child run is dispatched. `null` before dispatch or on non-Kubernetes deployments.

A node with no assigned pod shows no chip. `PodIndicator` reads that node's own `executionPodName`; it does not use an unrelated child pod or the API pod as a global fallback (`apps/web/src/components/CoordinatorTopologyGraph.tsx:293`).

The UI also seeds the graph from `coordinator_work_plan_get` and `coordinator_children_get` equivalents so a finished run or a stream that connected after the first snapshot still renders immediately. Stream deltas reconcile on top of that seed.

### Topology over MCP

MCP offers two ways to inspect topology:

- `orchestration_topology` returns a one-shot snapshot by combining the current work plan and child runs.
- `run_watch` against the coordinator run id streams the live events that reconstruct the graph: `coordinator.topology`, `subtask.*`, and `coordinator.steering`.

Use `orchestration_topology` for a point-in-time view. Use `run_watch` when building an interactive client, a terminal progress display, or an automation that reacts to subtask state.

## Child-runs experience

### Children are real runs

Each dispatched subtask becomes a first-class child run. The coordinator does not invent a second execution system for children. A child run uses the existing run machinery: agent work, RAI/safety checks, timeline events, tool approvals, questions, and assemble-ready handoff.

The parent coordinator owns the collective result. Children perform their assigned pieces and stop at the boundary where the parent can assemble. The user reviews the combined output once, rather than approving a pile of unrelated child fragments.

### Seeing children in the UI

In the graph, a dispatched subtask gains a child run id. The subtask card can show **Expand pipeline**. Expanding the card reveals a compact child pipeline, typically showing steps like **Agent**, **Rai**, and **Assemble-ready**, with statuses and timers. If the child graph descriptor is available, the UI uses the actual child pipeline instead of the fallback.

Selecting a subtask focuses its **Agent session** panel inside the orchestration page. Inspect the selected task's messages, tool calls, questions, approvals, changes, and files without opening a retired standalone Workflow or Execution page.

### Selected-task Agent session panel

The coordinator page uses the **Agent session** panel for the selected coordinator, planned subtask, child, or assembly stage. It exposes **Messages**, **Changes**, and **Files** for the selected context. A planned subtask can be selected before dispatch; its artifact tabs explain why no child files exist yet.

**Messages** renders each turn as cards. System prompts and coordinator instructions are collapsed by default, while agent messages are open and rendered with `react-markdown`, `remark-gfm`, and `rehype-sanitize` so Markdown is useful but sanitized (`AgentSessionPanel.tsx:31`, `:819`, `:829`, `:1595`). Tool calls are grouped behind a **Tool calls** disclosure and labeled in human terms such as **Read file**, **Edit file**, **View**, or **Run command** (`AgentSessionPanel.tsx:794`, `:1647`). File references from tool arguments become file cards with **Preview** actions (`AgentSessionPanel.tsx:950`, `:1674`).

**Changes** shows changed files and unified diffs; **Files** shows output files. Both fetch `/files` data only when their tab is opened, normalize run worktree paths to workspace-relative paths, and preview content through the file viewer (`AgentSessionPanel.tsx:1177`, `:1219`, `:1437`, `:1534`). This makes paths readable even when backend artifacts include absolute sandbox/worktree prefixes (`AgentSessionPanel.tsx:756`).

### Children over MCP

`coordinator_children_get` lists the dispatched child runs for a coordinator run. Each row pairs the child run with subtask state:

- `subtaskId`
- `childRunId`
- `subtaskStatus`
- `assignedAgent`
- `selectedModelId`
- `childRunStatus`
- `worktreeBranch`
- `treeHash`
- `stepCount`

This is the MCP equivalent of clicking through the graph. It lets a tool identify active children, inspect which agent owns which subtask, target a specific child with steering, or jump into the child run's own stream.

## Steering a live orchestration

### Steering in the web UI

The graph toolbar shows **Steer coordinator:** followed by an input and the actions **Send**, **Redirect**, **Amend**, and **Stop** while the orchestration is active. The scope note says **Applies to all active subtasks.** This is whole-orchestration steering: it broadcasts to every active child unless the user targets a subtask from a node-level control.

Subtask cards can also show **Stop**, **Redirect**, and **Amend** when the subtask is active and has a child run. Those actions target that specific child run. The dialog title names the action and target, asks for an **Instruction** when required, and disables **Send** until required guidance is present.

Blocked assembly is a recoverable pause, not a dead end. The recovery panel stays live, explains why assembly paused, lists blocking subtasks or conflicting files when available, and lets the user **Send**, **Redirect**, **Amend**, or **Stop** without reloading the page. After the user sends steering, the panel immediately shows that the message was sent and the coordinator is being resumed. If no steering arrives before the blocked-wait timeout (currently 10 minutes by default), the coordinator settles as failed just as it did before.

### Steering over MCP

`coordinator_steer` is the MCP steering tool. It takes:

- `run_id` — the coordinator run id.
- `kind` — the steering verb.
- `instruction` — required for `redirect` and `amend`; optional for `stop` and recovery verbs.
- `target_child_run_id` — optional; when present, targets one child; when omitted, broadcasts to all active children.

The steering verbs are:

| Verb | User intent | Timing |
| --- | --- | --- |
| `send` | Add context or a note for the coordinator without changing the chosen direction. | Immediate. If assembly is blocked, this wakes the coordinator and retries assembly with the new context. |
| `stop` | Cancel active subagents now and stop further work for the target scope. | Immediate cancellation of active subagents. |
| `redirect` | Point the target at a changed direction. | Injected at the targeted subagent's next turn boundary, or immediately resumes a blocked coordinator by re-entering dispatch. |
| `amend` | Add guidance without discarding the whole in-flight context. | Injected at the targeted subagent's next turn boundary, or immediately resumes a blocked coordinator when there is a recoverable gate to unblock. |

Omitting `target_child_run_id` broadcasts the directive to every active child. Supplying it targets the one child run behind a specific subtask. `redirect` and `amend` need an instruction because the coordinator must know what guidance to relay. `send` and `stop` can work without instruction, though a short reason is still useful for the human timeline.

Pause is not supported.

### What steering changes on screen

A steering directive appears as `coordinator.steering` on the coordinator stream. The topology reducer attaches targeted directives to the matching subtask node by child run id. Broadcast directives attach to the coordinator node. The graph then shows a steering note with the verb and directive status, such as **Redirect · queued**.

The UX intentionally distinguishes immediate stop from next-boundary guidance. `stop` cancels active subagents now. `redirect` and `amend` are queued until the target reaches a turn boundary when work is already in flight; they do not interrupt an in-flight model turn mid-token. When collective assembly is blocked, the coordinator stays observable on the live stream and the same steering verbs resume it: `send` retries assembly with added context, while `redirect` and `amend` reset eligible blocked work and re-arm dispatch.

## Orchestrations list page

The project **Orchestrations** page is the user's project-level index of coordinator runs. It lists coordinator runs only, with each row showing:

- an orchestration status badge,
- the task or goal text,
- the start time,
- and **Open**, **Stop**, and **Delete** actions (see below).

The page header is **Orchestrations** with the subtitle **Coordinator runs across this project.** Breadcrumbs take the user back to **Projects** and the project page. **Refresh** reloads the list. While loading, the page shows **Loading orchestrations**. If no coordinator runs exist, the empty state says **No orchestrations yet** and tells the user to start an orchestration from the Board to coordinate a squad of agents.

Status labels are human-readable. Raw coordinator statuses are normalized into labels such as **Awaiting assembly**, **Assembling**, **In review**, **Dispatching**, **Complete**, **Declined**, **Blocked**, and **Failed**. The list uses those labels rather than forcing the user to interpret internal status strings.

### Stopping vs deleting an orchestration

Each row carries two destructive actions next to **Open**:

- **Stop** (a dismiss-circle icon) cancels a *running* orchestration but keeps the record so the user can still inspect what happened. It first asks for confirmation — *"Stop this orchestration? The running work will be cancelled, but the run is kept so you can inspect it."* — then calls the cancel endpoint and reloads the list so the row settles into a terminal status. Stop is **disabled for orchestrations that have already finished** (completed, failed, declined, merged, merge-failed); its tooltip then reads *"This orchestration has already finished."*
- **Delete** (a trash icon) removes the orchestration and its workspace entirely. It opens a confirmation dialog — **Delete orchestration** / *"Delete this orchestration? This removes the run and its workspace."* — with **Cancel** and **Delete** buttons. On confirm, the row is optimistically removed from the list. Delete is available for any run, running or finished; a running run is cancelled first as part of deletion.

Under the hood, **Stop** posts `POST /api/runs/{id}/cancel` (cancel-only, keeps the row) and **Delete** issues `DELETE /api/runs/{id}` (cancel-if-active, then remove). Both run the same server-side cancellation path — abandon the coordinator workflow (which also stops its child subtask runs), tear down the worktree, and force the run to a terminal state — so stopping or deleting a live orchestration always leaves the underlying work halted. See the [Runs reference](../reference/api.md#delete-api-runs-id) for the exact routes, status codes, and the `already_terminal` response shape.

## Orchestration detail page

The orchestration detail page is the main control room. It combines the confirmation gate, topology, agent load, coordinator session, bubbled child actions, assembly review, and child-run drill-in.

### Header and goal

The page title is **Orchestration**. It shows a short id in the breadcrumb, live connection spinner when the stream is connecting or streaming, and a **Retry** action for retryable failed or merge-failed coordinator runs. Retry immediately reports that it is reconnecting to coordinator progress. A retry that resumes the existing coordinator remains on the same page and refreshes its live state; a retry that starts a linked run navigates to that new run. A failure is displayed and leaves the action available when the run remains retryable. If the run is a retry, the header links back to the run it retried from. The original goal appears as **Goal:** once the `coordinator.started` event is available.

### Coordinator Graph

The **Coordinator Graph** is the full-width band at the top. It is built to be watched. The graph has zoom controls, auto-fit behavior, and expandable subtask cards. When the OutcomeSpec is still being authored, the graph avoids implying future work and shows a focused pre-dispatch state. Once confirmed, the execution pipeline appears.

Nodes are connected by clean **spine edges** — each fan-out and fan-in routes through a shared rounded junction dot, drawn as straight vertical connectors rather than wavy S-curves — and every card carries a **colored top-accent bar** keyed to its status (green complete, blue running, amber awaiting, red failed) with the status pill in the top-left corner. Ranks are centered and aligned, so parallel subtasks line up horizontally directly under their shared parent. A **minimap** in the bottom-right corner shows the whole graph at a glance, coloring each node by its status and outlining the visible viewport, so you can orient yourself in a large orchestration. The **zoom control** in the corner has a **fit-to-view** button (resets to 100%, the natural fitted size), minus/plus buttons, and a live percentage readout; hold **Ctrl** while scrolling to zoom (shown as a tooltip).

The graph includes the coordinator node, subtask nodes, dependency edges, and collective assembly stages when they are part of the descriptor. Assembly stages come from the selected workflow's happy path, so built-in software workflows show RAI before Build & Test and human review. Build & Test has its own status and elapsed-time projection; when it registers a sandbox preview, the preview URL is attached to the Build & Test node. Human review becomes actionable only during **In review**, merge and scribe light up from their own assembly events, and completion turns the flow green.

### Agent rail

When the work plan exists, the page shows an **Agents** rail below the graph. It summarizes agent load for this orchestration from the work plan and children snapshot. This gives the user a quick read on which cast members are active, queued, blocked, or done without inspecting every node.

### Outcome spec and coordinator session columns

Below the graph, the page uses two columns. The left column is the **Outcome spec** panel. It can collapse to a rail labeled **Outcome spec**, and it auto-collapses after confirmation to give more space to live execution. The right column is the **Coordinator session**, which can also collapse to a rail while the spec is still being authored.

The coordinator session reuses the standard run timeline. It shows the coordinator's own messages, lifecycle cards, tool activity, and stream state. It filters out raw serialized work-plan JSON when the structured plan is already visible in the graph and panels.

Outcome-spec JSON that reaches the agent message stream is rendered as readable **Outcome plan** fields instead of a raw blob, and the row is attributed to **Coordinator (Outcome plan)** (`apps/web/src/components/AgentSessionPanel.tsx:777`, `:791`, `:1438`). RAI verdict cards also suppress placeholder rationales such as `-`, `---`, and `—`, so empty rationales do not show as noisy punctuation (`AgentSessionPanel.tsx:805`, `:1245`).

The coordinator run also surfaces its assembled artifacts through the same **Artifact Browser** used
on child runs — a compact **Changes** list plus a **Files** tab with a real folder tree — so the
collective output of an orchestration is inspectable directly from the coordinator view rather than
only through per-child runs.

Coordinator-only artifacts are also intentionally quiet on misses. The page stops retrying `work-plan` or `outcome-spec` REST reads after the first `404`, because the live coordinator stream is enough to fill in those artifacts once they exist. Child runs skip those calls entirely: a child run never has its own work plan or outcome spec, so its page does not poll those coordinator-only endpoints at all.

### Reading the session log

The session log is tuned to read like a clean narrative rather than a machine trace. A **Show technical details** toggle sits above the timeline and is **OFF by default**. With it off, low-signal plumbing rows — system-prompt scaffolding, internal assembly-gate prompts, tool-call start/stop (shell, file view/edit, raw commands), and file-write rows — are collapsed, leaving agent and coordinator messages, instructions, narrative activity, and human-facing approvals. Nothing is deleted: flipping the toggle on reveals every technical row again. The classification is done entirely client-side from the shape of each event, so turning details on and off is instant (`apps/web/src/components/AgentSessionPanel.tsx:740`, `:2388`).

The session column is also wider than before so long tool output and messages have room to breathe, and the **Message coordinator** composer at the bottom of the panel is always visible — the input reserves clearance so the graph's minimap can never hide it. Use it to send a message to the coordinator (or the selected child) mid-run without leaving the page. The composer stays enabled while the coordinator is parked in **In review** when the run detail reports `coordinator_steerable: true`, so you can message the coordinator during collective review instead of waiting for the gate to close (`apps/web/src/pages/CoordinatorRunPage.tsx:2192`, `:3216`).

The **Coordinator Graph** band reflows responsively: a resize observer re-fits the topology to the available width as the panel or window changes size, so the graph stays readable whether the session column is expanded, collapsed, or the browser is resized.

### Bubbled child actions

When a child asks a question or needs a tool approval, the coordinator re-projects that action onto the coordinator stream. The detail page shows those items in the all-up view so the user does not have to hunt through every child run.

Question cards route answers to the child run that asked. Tool approval cards also target the child run. The coordinator view is the inbox; the child remains the owner of the request.

### Assembly and review

After child subtasks settle, the coordinator assembles the collective output. During **Awaiting assembly**, **Assembling**, **RAI review**, or **Build & Test**, the page shows progress through the assembly nodes and explains that subtasks are complete and the coordinator is integrating their outputs for collective review. The `coordinator.assembly_review_requested` event can appear for automated gates (`gateKind: "build-test"` or `"rubberduck"`) as well as human review; only `gateKind: "human-review"` (or an older event with no `gateKind`) is treated as action-required for the user.

When any correction feedback arrives, the revision loop is visible instead of looking like a stalled graph. The timeline first shows `coordinator.steering_received` with the source, then `coordinator.steering_decision` with the coordinator's chosen effect and rationale. **Steered in place** means the existing child session resumes with context preserved; **Fresh dispatch** means the coordinator deliberately chose a reset and new child work. See [Unified autonomous steering](./unified-steering.md).

For automated gate feedback, the retry is warmer than a fresh assembly. If Build & Test or Rubberduck asks
for changes, the coordinator keeps the Build & Test pod and detached integration worktree while it
re-dispatches the affected subtasks. When assembly reaches Build & Test again, it reuses that same
run-bound pod/worktree instead of releasing resources and cold-launching a replacement. As an operator, that
means previews and gate context survive the request-changes cycle, and a second pass reuses the warm
pod instead of waiting for Kubernetes to schedule a replacement AgentHost pod.

During **In review**, the page marks human review as pending and directs the user to the collective **Changes** and **Files** surface. Review actions go to the assembly review gate, not to individual children. For exhausted autonomous budgets, scoped author recovery, and fresh budgets after human request-changes, see the existing [resilient-review state model](../deep-dive/resilient-assembly-review.md); this is not a second assembly state machine.

If assembly blocks or fails, the page explains why. It can show conflict files, blocking subtasks, status
badges, and hints such as re-running affected subtasks or stopping the run. Build & Test infrastructure
blocks now include structured failure details in the event payload — `detail`, `exceptionMessage`,
`innerExceptionMessage`, `innerExceptionType`, and `infrastructureReason` — so the timeline and events API
can show the real AgentHost launch or transport root cause instead of a generic
`agenthost_launch_failed`. Retryable `coordinator.assembly_blocked` events keep the stream alive for
recovery; terminal `coordinator.assembly_failed` events still close the stream, but replay drains all
persisted diagnostics first. The important UX rule is that the user gets a reason and a recovery surface,
not a bare failed state.

### Stall diagnostics

If a child stops making progress long enough to hit the configured stall timeout, the coordinator records a `coordinator.child_stall_detected` event on the parent stream and marks the stalled path as ineligible to continue. In practice, the stalled child subtask shows a terminal failure with recovery guidance, while still-pending dependents can surface as **Blocked** because they never became runnable after their prerequisite stalled.

If the run later shows **Blocked** at assembly time, `assembly_blocked` means the coordinator refused partial assembly because one or more subtasks were ineligible, including blocked dependency paths. The recovery action is to re-run the affected stalled or blocked branch through the recovery or steering controls if the plan is still valid, or stop the run if you want to abandon that orchestration attempt.

## MCP flow patterns

### Start, confirm, and watch

A typical MCP client flow is:

1. Call `coordinator_start` with `project_id`, `goal`, and `start_mode: "defineOutcome"` (the default), plus optional `model_id`.
2. Watch the coordinator run stream or call `coordinator_outcome_spec_get` until the spec is available.
3. Present the OutcomeSpec to the user.
4. Call `coordinator_outcome_spec_confirm` when the user approves, or `coordinator_outcome_spec_revise` with feedback when the user wants changes.
5. After confirmation, call `coordinator_work_plan_get` for the plan and `coordinator_children_get` for dispatched children.
6. Use `orchestration_topology` for a one-shot graph or `run_watch` for live topology events.
7. Call `coordinator_steer` to stop, redirect, amend, or recover.

This follows the web **Define Outcome** path. Direct mode skips steps 2–4; queued Ready pickup is a separate unattended path, not a reason to start a second run for the same task. The MCP client owns presentation; the coordinator service owns state.

### Inspecting a run after reconnect

For a reconnect or audit view:

1. `coordinator_outcome_spec_get` gives the confirmed or pending intent.
2. `coordinator_work_plan_get` gives the persisted work plan, subtasks, statuses, and dependencies.
3. `coordinator_children_get` gives child run ids and child status.
4. `orchestration_topology` combines the current plan and children into a graph-shaped snapshot.
5. `run_watch` resumes live updates from the coordinator run stream when an active run needs continuous monitoring.

The persisted artifacts are enough to rebuild the user's mental model even if the live stream was disconnected during drafting, dispatch, or assembly.

## User mental model

### Coordinator

The coordinator is the visible parent run and accountable orchestrator. It accepts intent through the selected start mode, decomposes work, dispatches children, observes progress, accepts steering, recovers eligible parked work, and assembles the result.

### OutcomeSpec

The OutcomeSpec is the structured intent contract: desired outcome, scope, assumptions, and clarifications. Define Outcome requires interactive confirmation; Direct skips that drafting/confirmation step, and unattended pickup confirms automatically.

### Work plan

The work plan is the execution contract. It answers: what subtasks exist, who owns them, which model each uses, what their status is, which child run is attached, and which dependency edges control ordering?

### Subtask

A subtask is one bounded unit of work inside the work plan. It has an assigned agent, selected model, status, scope, phase, and dependency relationships. It may or may not have a child run yet.

### Child run

A child run is the actual execution for a dispatched subtask. It has its own run stream and pipeline, and it can ask questions or request tool approval. The coordinator view can surface those actions, but answers and approvals route back to the child.

### Topology

Topology is the live graph of coordinator plus subtasks and dependency edges. It updates from `coordinator.topology`, `subtask.*`, and `coordinator.steering` events, and it can be snapshotted through `orchestration_topology`.

### Steer and recover

Steer means the user intervenes while the orchestration is alive or parked. Stop cancels active work. Redirect and amend inject guidance at turn boundaries. Recover resets eligible blocked, failed, or parked subtasks and resumes dispatch.

## Scope and limits

- Pause is not supported.
- The interactive confirmation gate belongs to **Define Outcome**, not **Direct** or unattended pickup.
- `redirect` and `amend` apply at the next subagent turn boundary, not in the middle of an active model turn.
- Omitting `target_child_run_id` in `coordinator_steer` broadcasts to all active children.
- Child isolation is advisory from the user's perspective; the coordinator still relies on dependency edges, scoped subtasks, review, and assembly to manage conflicts.
- The parent coordinator owns collective assembly and final review; child runs do not each merge independently.

## Why this design works

The experience keeps intent, execution, and review distinct. Define Outcome adds a structured confirmation step; Direct starts from the submitted goal. The live topology explains dependencies and blockers, steering changes direction or recovers eligible work, and the user reviews the assembled outcome.

The web UI makes that lifecycle visual and action-oriented. MCP makes it scriptable. Both let the user choose the start mode and retain review and steering controls while the team executes.

## v0.9.5 run page updates

The run page now makes the whole work plan visible after decomposition. Once `coordinator.work_plan` or the persisted work-plan snapshot is available, the graph shows **Outcome plan** followed by **Work plan**, then the subtask graph (`apps/web/src/pages/CoordinatorRunPage.tsx:2224`, `:2255`, `:2272`). This means a user can see the full set of planned subtasks before every child has been dispatched.

The right-side Agents area is now a selected-task readout. It includes the coordinator root, the outcome/work-plan nodes, every planned subtask, and assembly stages. Planned subtasks are selectable even before they have a child run; their Changes and Files tabs explain that artifacts appear after dispatch (`CoordinatorRunPage.tsx:2702`; `apps/web/src/components/AgentSessionPanel.tsx:1640`).

The run tree order is deterministic. Rows sort by workflow stage rank, then by numeric subtask id, then by label/position as tie-breakers: **Work plan**, **Outcome plan**, subtasks, **RAI**, **Build & Test**, **Human Review**, **Merge**, and **Scribe** (`apps/web/src/pages/CoordinatorRunPage.tsx:1933`, `:1951`, `:3155`). This avoids apparent reordering caused by event arrival or graph layout.

The message stream is quieter by default. High-signal coordinator updates remain visible, while system prompt scaffolding, internal assembly-gate prompts, raw activity details, tool calls, and file rows sit behind technical-detail toggles (`apps/web/src/components/AgentSessionPanel.tsx:740`, `:2388`). Outcome-plan JSON in the stream is formatted as readable fields and attributed to **Coordinator (Outcome plan)**, and placeholder RAI rationales are hidden (`AgentSessionPanel.tsx:777`, `:805`, `:1438`).

Steering now happens from the selected-task panel. Selecting the outcome plan focuses the clarification composer; selecting a subtask opens that task's transcript, files, and follow-up surface (`CoordinatorRunPage.tsx:2866`, `:2880`, `:3758`).

## Assembly review no longer races completion

During collective assembly, the human review gate remains authoritative until it closes. The backend persists the review request and accepts a decision only while the WorkPlan is still `in_review` at the `review` assembly stage (`apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyReviewPersistence.cs:111`, `:196`). If the coordinator process fails before a human acts, the open review is marked as preserved instead of being cleared, so the assembled candidate remains inspectable (`CoordinatorAssemblyReviewPersistence.cs:167`).

The UI can also distinguish the terminal stage and reason for parked assembly states because `WorkPlan` now stores `AssemblyTerminalStage` and `AssemblyStatusReason` (`apps/Agentweaver.Api.Data/Memory/WorkPlan.cs:34`).

## Preview-first delivery

For runnable work, the platform-owned **PreviewStep** follows Build & Test on the retained coordinator pod and detached integration worktree. It may run after an **approved** or **request-changes** Build & Test verdict, but skips **declined**. Preview provisioning failures are isolated so they do not block review; an unavailable preview is not proof that the candidate passed its gates. See the existing [live-preview provisioning model](../deep-dive/live-preview-provisioning.md).

When a runnable subtask is outside that gate, the coordinator includes preview intent in the OutcomeSpec confirmation, dispatches the child with instructions to start and verify the app in its sandbox, and asks the child to include the preview URL in its completion message. The assembled review output should surface all reported URLs near the top in a `Live Previews` table with agent, URL, port, and description. If the sandbox backend cannot provide previews, the assembled output should include local run instructions instead.

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
