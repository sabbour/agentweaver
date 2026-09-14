---
title: Example walkthroughs
---

# Example walkthroughs

This page walks through Agentweaver from project creation to reviewed work. Collective
gates depend on the selected workflow. For built-in software workflows:

```
children: Agent → Assemble-ready
combined output: RAI → Build & Test → Human review → Merge → Scribe
```

The shared `canonical-default-workflow` image is awaiting owner reconciliation of
collective inputs, applicable Build & Test, and coordinator-driven revision. Until
promotion, the textual pipeline and [review guide](./review) describe current behavior.
Children do not run their own RAI, human review, merge, or Scribe.

---

## Scenario 1 — Create a project, cast a team, run the default workflow, review and merge

This is the **define-outcome path with autopilot off** for software delivery, not
the direct-start or unattended-confirmation path.

### 1. Create a project

From the **Project Gallery** (`/projects`), choose a creation path:

- **Create blank project** — enter a name and a repository folder. Agentweaver initializes an empty git repository (`POST /api/projects` with `origin: blank`).
- **Create from GitHub** — enter a name, select a Repo App-authorized repository, and a folder. Agentweaver clones it after consuming an opaque `repository_selection_code` (`POST /api/projects` with `origin: github`).

You land on the project **Dashboard** (`/projects/{id}`).

### 2. Cast a team

Open **Team → Cast** (the Casting Wizard at `/projects/{id}/team/cast`). The wizard offers three strategies:

| Strategy (UI tab) | What it does | API |
|---|---|---|
| **Formulate** | Describe the goal in plain language; the wizard proposes a roster | `POST /api/projects/{id}/casting/proposals` (`mode: free_text`, `goal`) |
| **Template** | Pick a predefined team template (e.g. Quick Software Development) | `GET /api/casting/templates`, then `POST .../casting/proposals` (`mode: scenario`, `template_id`) |
| **Analyze** | The wizard reads the project files and suggests a best-fit team | `POST .../casting/proposals` (`mode: analysis`) |

(A `manual` mode also exists, which takes an explicit `role_ids` list.)

Review the proposed members, amend if needed (`PATCH .../casting/proposals/{proposalId}`), then **Confirm** (`POST .../casting/proposals/{proposalId}/confirm`). The casting algorithm assigns named personas from a thematic universe (The Matrix, Star Wars, and others) to each role, and the team is recorded. See [Agent Teams & Blueprints](./teams).

### 3. Start the orchestration

From the project **Board** (`/projects/{id}/board`), click **Start orchestration** and enter a goal:

> "Add input validation to the signup form and cover it with unit tests."

This calls `POST /api/projects/{id}/orchestrations` and takes you to the coordinator run page (`/projects/{id}/orchestrations/{runId}`).

### 4. Confirm the OutcomeSpec

The coordinator drafts an **OutcomeSpec** — goal, desired outcome, scope, and assumptions — and emits a `coordinator.outcome_spec` event. **No agent work starts until you confirm it.**

- Confirm: `POST /api/runs/{runId}/outcome-spec/confirm`
- Revise with feedback: `POST /api/runs/{runId}/outcome-spec/revise`

### 5. Watch the WorkPlan and topology

On confirmation, the coordinator decomposes the spec into a **WorkPlan** (`coordinator.work_plan`) — a dependency graph of subtasks. It dispatches independent subtasks in parallel, each in its own isolated git worktree. The topology view streams live over SSE (`GET /api/runs/{runId}/stream`); you can also fetch the plan (`GET /api/runs/{runId}/work-plan`) and child runs (`GET /api/runs/{runId}/children`).

Each child emits `subtask.dispatched → subtask.running → subtask.assemble_ready`. While the orchestration is active you can **steer** it (`POST /api/runs/{runId}/steer`) — send a directive, redirect a child, or stop the run.

### 6. Review and merge the assembled diff

When children reach assemble-ready, the coordinator combines their output and runs
the selected collective gates, including RAI and applicable Build & Test. When the
run reaches **Human Review**, open the file panel:

- **Changes** lists modified files (`GET /api/runs/{runId}/files`); click any file for a diff (`GET /api/runs/{runId}/files/{path}`).
- **Commit and Merge** approves and merges to the originating branch: `POST /api/runs/{runId}/review` with `approved: true` (or `POST /api/runs/{runId}/commit`).
- **Change** requests a revision: `POST /api/runs/{runId}/review` with `request_changes` and `feedback` — the coordinator decides how to steer or dispatch the required work.
- **Decline** discards the work: `POST /api/runs/{runId}/review` with `approved: false`.

If the target branch has moved, the merge may report conflicting files (`merge.conflicted`); the worktree is preserved for manual resolution.

### 7. Scribe records what the team learned

After successful merge, **Scribe** records the session and promotes only low-risk
`learning`, `pattern`, and `update` entries attributed to that completed run.
`architectural`/`scope` entries remain for owner/coordinator review. Structured trust
records are authoritative; files are inspectable mirrors. See [Team Memory](./teams#team-memory).

---

## Scenario 2 — Pick up a backlog task with the board and heartbeat

Use the Kanban board to queue work and let the heartbeat dispatch it.

1. **Capture a task** in the Backlog column (`POST /api/projects/{id}/backlog/tasks`). Add a description for context — sharper descriptions produce sharper OutcomeSpecs.
2. **Rank** the backlog by dragging cards (`POST .../backlog/tasks/{taskId}/reorder`), and optionally pin a workflow per card (`PUT .../backlog/tasks/{taskId}/workflow-override`).
3. **Move to Ready** when the task is ready to run (`POST .../backlog/tasks/{taskId}/ready`), or send everything at once (`POST .../backlog/ready-all`).
4. The **heartbeat** claims Ready tasks up to the concurrency limit and starts a coordinator orchestration for each, moving the card to **Active**. Inspect status on the **Heartbeat** page (`GET /api/diagnostics/heartbeat`).
5. When approval is needed, open **Human Review** as in Scenario 1. Failed runs land
   in **Problems**; inspect diagnostics and use explicit retry/recovery controls.
   Dragging is supported only between Backlog and Ready, not Problems to Ready.

See [Board and Backlog](./board).

---

## Scenario 3 — Decompose a specification into backlog tasks

Turn a PRD, design doc, or feature spec already in the repository into queued work.

1. Open the **Workspace** page (`/projects/{id}/workspace`) and browse the repository (`GET /api/projects/{id}/workspace`, `GET /api/projects/{id}/workspace/files`).
2. Select a Markdown spec file and choose **Decompose into tasks** (`POST /api/projects/{id}/backlog/decompose` with the `file_path`). The response lists proposed backlog items (and flags any that already exist).
3. Review the preview and explicitly confirm (`confirm: true`) to persist new items.
   Edit saved tasks, then move selected items to Ready for heartbeat pickup. See
   [Decomposition](./board#decomposing-a-spec-into-tasks).

---

## Scenario 4 — Drive the full lifecycle from an MCP client (Copilot CLI)

Everything above is available programmatically through the [MCP server](/reference/mcp). Any MCP-compatible client can run the complete lifecycle. The tool names below are exact.

1. **Sign in and connect repository access if needed** — complete MCP OAuth with Agentweaver's Entra-backed identity. When
   GitHub access is needed, use `github_repo_app_connect`, open its `browser_url`, and poll
   `github_repo_app_authorization_status`.
2. **Choose or create a project** — `project_list` / `project_get`, or `project_create`.
   GitHub-origin creation first uses `github_repository_selections_list` and
   `github_repository_selection_issue` for its opaque selection code. Once the project
   exists, a Project Owner can use `project_copilot_app_connect` and
   `project_copilot_app_authorization_status` when a project binding is needed.
   Check `project_github_capability_status`; an eligible platform provider can supply
   project work without a project Copilot binding.
3. **Confirm a team** — `team_cast` defaults to a proposal. Confirm via
   `team_cast(confirm_proposal_id=...)` or `confirm=true`; inspect with `team_get`.
4. **Choose one launch path** — queue via `backlog_capture_task` and
   `backlog_move_to_ready` for heartbeat pickup, **or** explicitly start a new run.
   Do not queue the task and then start it again with `run_task`.
5. **For manual define-outcome control** — use `coordinator_start` with
   `start_mode="defineOutcome"`, `autopilot=false`, and optional `workflow_id`.
   Inspect `coordinator_outcome_spec_get`, then use
   `coordinator_outcome_spec_confirm` or `coordinator_outcome_spec_revise`.
   Alternatively, `run_task` defaults to direct mode and starts/polls a new run.
6. **Observe and steer** — `coordinator_work_plan_get`, `coordinator_children_get`, `orchestration_topology`, `coordinator_steer`; monitor child runs with `run_status`, `run_watch` (SSE), `run_show_artifacts`, `run_get_file`.
7. **Review** — list artifacts before `run_get_file`. `run_review(approved=true|false)`
   supports approve/decline only. Request-changes feedback is available through the
   web/REST review surface; `coordinator_steer` is a separate steering control.
8. **Optionally curate/export knowledge** — `decision_inbox_submit`, `memory_record`,
   `memory_search`, or `memory_export`. Export is not a mandatory final run stage.

See the [MCP reference](/reference/mcp) for full tool parameters and the [API reference](/reference/api) for the underlying endpoints.

<details id="diagram-context-canonical-board-lifecycle" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>The board is a projection</td></tr>
<tr><td>subtitle</td><td>Columns reflect persisted task and run state—not a separate workflow engine.</td></tr>
<tr><td>group-title0</td><td>Before and during execution</td></tr>
<tr><td>group-title1</td><td>Review / terminal outcomes</td></tr>
<tr><td>Backlog</td><td>Backlog</td></tr>
<tr><td>Backlog</td><td>Captured task; not queued</td></tr>
<tr><td>Backlog</td><td>task: backlog</td></tr>
<tr><td>Ready</td><td>Ready</td></tr>
<tr><td>Ready</td><td>Queued task</td></tr>
<tr><td>Ready</td><td>task: ready</td></tr>
<tr><td>Active</td><td>Active</td></tr>
<tr><td>Active</td><td>Work is in progress</td></tr>
<tr><td>Active</td><td>default non-review bucket</td></tr>
<tr><td>Problems</td><td>Problems</td></tr>
<tr><td>Problems</td><td>Failed / declined / merge failed</td></tr>
<tr><td>Problems</td><td>or assembly blocked / failed</td></tr>
<tr><td>Human Review</td><td>Human Review</td></tr>
<tr><td>Human Review</td><td>AwaitingReview / InReview</td></tr>
<tr><td>Human Review</td><td>or assembly stage: Review</td></tr>
<tr><td>Done</td><td>Done</td></tr>
<tr><td>Done</td><td>Completed / Merged / AssembleReady</td></tr>
<tr><td>Done</td><td>or plan Complete / stage Done</td></tr>
<tr><td>e1</td><td>queue</td></tr>
<tr><td>e2</td><td>claim + start</td></tr>
<tr><td>e3</td><td>await review</td></tr>
<tr><td>e4</td><td>finish</td></tr>
<tr><td>e5</td><td>problem</td></tr>
<tr><td>assurance-title</td><td>DEFAULT BUCKETS · NOT A NEW STATE MACHINE</td></tr>
<tr><td>assurance-line1</td><td>Configured workflow stages may replace the default run columns. Arrows summarize typical changes, not every path.</td></tr>
<tr><td>assurance-line2</td><td>The board polls persisted state. Ready tasks with unmet dependencies stay Ready; blocked is a flag.</td></tr>
<tr><td>Backlog</td><td>Entity</td></tr>
<tr><td>Backlog</td><td>BacklogTask</td></tr>
<tr><td>Backlog</td><td>State</td></tr>
<tr><td>Backlog</td><td>Run</td></tr>
<tr><td>Backlog</td><td>Not required</td></tr>
<tr><td>Backlog</td><td>Action</td></tr>
<tr><td>Backlog</td><td>Move to Ready</td></tr>
<tr><td>Ready</td><td>Blocked</td></tr>
<tr><td>Ready</td><td>Dependency metadata</td></tr>
<tr><td>Ready</td><td>Pickup</td></tr>
<tr><td>Ready</td><td>Atomic claim</td></tr>
<tr><td>Active</td><td>Input</td></tr>
<tr><td>Active</td><td>Coordinator run</td></tr>
<tr><td>Active</td><td>Status</td></tr>
<tr><td>Active</td><td>Non-review default</td></tr>
<tr><td>Active</td><td>Plan</td></tr>
<tr><td>Active</td><td>Dispatch / assembly</td></tr>
<tr><td>Active</td><td>Approval</td></tr>
<tr><td>Active</td><td>Separate pending flag</td></tr>
<tr><td>Problems</td><td>Failed / Declined</td></tr>
<tr><td>Problems</td><td>Merge</td></tr>
<tr><td>Problems</td><td>MergeFailed</td></tr>
<tr><td>Problems</td><td>Assembly</td></tr>
<tr><td>Problems</td><td>Blocked / Failed</td></tr>
<tr><td>Problems</td><td>Also</td></tr>
<tr><td>Problems</td><td>AssemblyDeclined</td></tr>
<tr><td>Human Review</td><td>AwaitingReview</td></tr>
<tr><td>Human Review</td><td>InReview</td></tr>
<tr><td>Human Review</td><td>Review stage</td></tr>
<tr><td>Human Review</td><td>Approve</td></tr>
<tr><td>Human Review</td><td>Resumes execution</td></tr>
<tr><td>Done</td><td>Completed / Merged</td></tr>
<tr><td>Done</td><td>AssembleReady</td></tr>
<tr><td>Done</td><td>Complete</td></tr>
<tr><td>Done</td><td>Done stage</td></tr>
<tr><td>backlog</td><td>No run is required yet; Move to Ready to queue work</td></tr>
<tr><td>ready</td><td>Unresolved dependencies stay here; Blocked is a flag, not a column</td></tr>
<tr><td>progress</td><td>Claimed tasks link to their run; Pending approval is a separate flag</td></tr>
<tr><td>failed</td><td>Blocked assembly is recoverable; Not every problem is terminal</td></tr>
<tr><td>review</td><td>Approval resumes the workflow; Approval alone is not Done</td></tr>
<tr><td>done</td><td>Persisted-state mapping; Not merely a clicked approval</td></tr>
<tr><td>notes</td><td>DEFAULT BUCKETS · NOT A NEW STATE MACHINE; Configured workflow stages may replace the default run columns. Arrows summarize typical changes, not every path.; The board polls persisted state. Ready tasks with unmet dependencies stay Ready; blocked is a flag.</td></tr>
<tr><td>groups</td><td>Before and during execution; Review and terminal outcomes</td></tr>
</tbody></table>
</details>

<details id="diagram-context-guide-example-scenarios-fig3" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>MCP lifecycle: choose one launch path</td></tr>
<tr><td>takeaway</td><td>Queue pickup and explicit starts are alternatives; inspection and approvals remain explicit.</td></tr>
<tr><td>m-prep-title</td><td>PREPARE AUTHORITY AND A CONFIRMED TEAM</td></tr>
<tr><td>m-launch-title</td><td>CHOOSE ONE • DO NOT QUEUE AND EXPLICITLY START THE SAME TASK</td></tr>
<tr><td>m-observe-title</td><td>OBSERVE, INSPECT AND DECIDE • NOT AUTOMATIC SUCCESS</td></tr>
<tr><td>Authorize</td><td>Authorize</td></tr>
<tr><td>Authorize</td><td>MCP OAuth / Entra identity</td></tr>
<tr><td>Authorize</td><td>Repo capability is separate</td></tr>
<tr><td>Choose project</td><td>Choose project</td></tr>
<tr><td>Choose project</td><td>project_list / project_create</td></tr>
<tr><td>Choose project</td><td>Check provider + repo readiness</td></tr>
<tr><td>Confirm team</td><td>Confirm team</td></tr>
<tr><td>Confirm team</td><td>team_cast: proposal first</td></tr>
<tr><td>Confirm team</td><td>confirm_proposal_id or confirm</td></tr>
<tr><td>Define outcome</td><td>Define outcome</td></tr>
<tr><td>Define outcome</td><td>coordinator_start</td></tr>
<tr><td>Define outcome</td><td>defineOutcome; autopilot=false</td></tr>
<tr><td>Start and poll</td><td>Start and poll</td></tr>
<tr><td>Start and poll</td><td>run_task</td></tr>
<tr><td>Start and poll</td><td>Default direct; creates NEW run</td></tr>
<tr><td>Queue for heartbeat</td><td>Queue for heartbeat</td></tr>
<tr><td>Queue for heartbeat</td><td>backlog_capture_task</td></tr>
<tr><td>Queue for heartbeat</td><td>then backlog_move_to_ready</td></tr>
<tr><td>Observe / steer</td><td>Observe / steer</td></tr>
<tr><td>Observe / steer</td><td>coordinator_work_plan_get</td></tr>
<tr><td>Observe / steer</td><td>coordinator_children_get</td></tr>
<tr><td>Inspect files</td><td>Inspect files</td></tr>
<tr><td>Inspect files</td><td>run_show_artifacts</td></tr>
<tr><td>Inspect files</td><td>then run_get_file</td></tr>
<tr><td>Review when gated</td><td>Review when gated</td></tr>
<tr><td>Review when gated</td><td>run_review(approved: bool)</td></tr>
<tr><td>Review when gated</td><td>true approves; false declines</td></tr>
<tr><td>m1</td><td>authorize</td></tr>
<tr><td>m2</td><td>prepare</td></tr>
<tr><td>m3</td><td>confirm</td></tr>
<tr><td>m4</td><td>new run</td></tr>
<tr><td>m5</td><td>reserved run</td></tr>
<tr><td>m6</td><td>list</td></tr>
<tr><td>m7</td><td>inspect</td></tr>
<tr><td>m-confirm-heading</td><td>MANUAL GATE</td></tr>
<tr><td>m-confirm-body</td><td>coordinator_outcome_spec_get → coordinator_outcome_spec_confirm (or coordinator_outcome_spec_revise).</td></tr>
<tr><td>m-pickup-heading</td><td>HEARTBEAT</td></tr>
<tr><td>m-pickup-body</td><td>Atomically claims Ready item. Pickup autopilot controls confirmation, not tool or merge approval.</td></tr>
<tr><td>m-direct-heading</td><td>BOUNDED WAIT</td></tr>
<tr><td>m-direct-body</td><td>Returns artifacts, a gate, a next action, a timeout, or a failure. Never assume completion.</td></tr>
<tr><td>m-outcomes-heading</td><td>REVIEW PARITY</td></tr>
<tr><td>m-outcomes-body</td><td>Request changes: web/REST review, not run_review. coordinator_steer is separate. memory_export is optional.</td></tr>
<tr><td>m-gates-heading</td><td>COLLECTIVE GATES</td></tr>
<tr><td>m-gates-body</td><td>Children: Agent → Assemble-ready. Selected workflow gates apply to combined output, not per child.</td></tr>
<tr><td>notes</td><td>[object Object]; [object Object]; [object Object]; [object Object]; [object Object]</td></tr>
<tr><td>groups</td><td>[object Object]; [object Object]; [object Object]</td></tr>
</tbody></table>
</details>
