# Workflows and backlog experience

Workflows and backlog are the operating model for Agentweaver work. Workflows define how a run moves through nodes, edges, triggers, gates, and completion; backlog defines which tasks are not yet committed, which tasks are Ready, and what the coordinator heartbeat may pick up next. The web UI gives humans a visual control room, while MCP exposes the same state and actions as tools.

Scope: this page covers workflow definition management, backlog intake, Ready pickup, autopilot defaults, spec decomposition, board stages, and the MCP tools for those experiences.

Related docs: [Overview](./00-overview.md), [Runs & board](./runs-board-watch.md), [Coordinator & orchestration](./coordinator-orchestration.md), [Operations](./operations.md), [Workflow generation](../workflow-generation.md), [Workflow selection](../workflow-selection.md), [Workflow library](../workflow-library.md), [Workflow binder](../workflow-binder.md), and [Workflow engine](../deep-dive/workflow-engine.md).

```mermaid
flowchart LR
    Capture[Capture task] --> Backlog[Backlog]
    Backlog -->|promote / Send all to Ready| Ready[Ready]
    Ready -->|heartbeat pickup| Active[Active]
    Active --> HumanReview[Human Review]
    HumanReview -->|approve| Done[Done]
    HumanReview -->|request changes| Active
    Active -->|fail / block / decline| Problems[Problems]
    Problems -->|retry or fix| Active
```

## The mental model

Agentweaver separates **process definition** from **work intake**.

- A **workflow** is a reusable pipeline. It has an id, name, description, version, trigger, start node, nodes, edges, and optional stage definitions.
- A **node** is a step in the pipeline: agent work, peer review, RAI check, human review, merge, scribe, terminal, or another supported workflow shape.
- An **edge** connects nodes and may carry a verdict such as `approved`, `request-changes`, `declined`, `pass`, `fail`, `review`, or `revise`.
- A **trigger** describes how the workflow starts. The Workflows page shows it as **Trigger: event (...)**, **Trigger: manual**, or **Trigger: unknown**.
- A **default** workflow is the project fallback for new work when no better or explicit workflow is selected.
- A **backlog task** is work that has not been claimed yet. It sits in **Backlog** or **Ready**.
- A **pickup** is the coordinator heartbeat claiming a Ready task and turning it into an unattended coordinator run.
- The **board** is the shared visual state: Backlog, Ready, Problems, Human Review, Active, and Done.

The user-facing promise is simple: workflows answer **how should this run?** Backlog answers **what should run next?** The heartbeat connects them.

## Workflows in the web UI

The project **Workflows** page is reached from a project at **Workflows**. It is titled **Workflows** with the subtitle **Reusable pipeline definitions.** It shows discovered workflow definitions, validation status, source, trigger, and the effective default.

The page groups cards into three sections:

| Section | What the user sees | What it means |
|---|---|---|
| **Active workflow** | The workflow card marked **Active**. | This is the effective default for new project runs. |
| **Available workflows** | Valid workflows that can be chosen through **Set as default**. | These are runnable definitions discovered by the project registry. |
| **Invalid workflows** | Workflows marked **Invalid** with an error message. | These cannot run or become active until fixed. |

Each card shows the workflow name, id, badges, **Trigger**, and **Source**. Built-in workflows carry **Built-in**. Valid non-default workflows carry **Valid**. Invalid workflows carry **Invalid** and display the validation error. The active card carries **Active** because it is already the project default.

Page actions are operational:

- **New workflow** opens a YAML editor with a blank template.
- **Generate workflow** opens a plain-language generation dialog.
- **Set as default** opens a picker with **Project workflows**, **Built-in workflows**, and **Reset to built-in default**.
- **Sync** re-reads `.agentweaver/workflows/`.
- **View graph** expands an inline read-only graph for a valid workflow.
- **Edit** opens the YAML editor for project-authored workflows.
- **Edit visually** opens the visual workflow editor for project-authored workflows.

Built-in workflows are inspectable and selectable. Project workflows are editable because they live in the project's workspace.

### `workflows_list`: viewing discovered workflows

MCP mirrors the Workflows page with `workflows_list`. It returns the discovered workflow set for a project, including validation status and which workflow is the effective default.

Use `workflows_list` when an assistant needs to:

1. see every workflow the project can use,
2. identify the default,
3. avoid invalid workflows,
4. decide whether a task should use the default or an override,
5. explain why the UI shows a workflow as Active, Available, or Invalid.

The discovered set includes the built-in default, catalog library workflows allowed for the project, and project-authored YAML files from `.agentweaver/workflows/`. Discovery and validation are server-side. The UI and MCP render the result; they do not invent workflow validity.

### `workflow_get`: inspecting nodes, edges, and trigger

`workflow_get` returns a single workflow by id, including its nodes, edges, and trigger. It is the assistant equivalent of opening a workflow and reading the pipeline.

Use it to answer:

- which trigger starts the workflow,
- which node is first,
- which agents, reviews, checks, merges, and terminal paths exist,
- which edge sends failed review back to implementation,
- whether the workflow is event-driven, manual, or otherwise configured.

In the UI, **View graph** gives the human a compact structural preview. Nodes are laid out left to right. Forward edges show normal progression. Loopback edges show rework paths such as revise or request changes. Card shape follows node type: agent nodes read as main work, gates read as decisions, action nodes read as execution steps, and terminal nodes read as endpoints.

### Defaults, sync, and runtime readiness

**Set as default** changes the project default. Choosing a valid workflow makes it active. Choosing **Reset to built-in default** clears the project-specific default and returns the project to the built-in fallback.

Default selection is safe by design. A workflow must be valid and bindable before it can become default. Loader-valid definitions that cannot execute are rejected before they can become the project default.

**Sync** is explicit. It re-reads `.agentweaver/workflows/` and refreshes the registry. MCP uses `workflows_sync` for the same operation. Use it after a workflow file changes on disk. A successful UI sync shows a message like **Synced 3 workflows from .agentweaver/workflows/.**

If sync finds invalid workflows, they remain visible under **Invalid workflows** with errors. The experience turns broken definitions into visible operational work.

## Generating and saving workflows

Workflow generation is draft-first. The user clicks **Generate workflow**, describes the pipeline, and receives YAML for review. Nothing is saved to `.agentweaver/workflows/` until the user saves.

```mermaid
flowchart TD
    Intent[Describe the workflow you need] --> Generate[workflow_generate]
    Generate --> Draft[YAML draft]
    Draft --> Review[Review in editor]
    Review --> Save[workflow_save]
    Save --> Registry[Registry refresh]
    Registry --> Workflows[Workflow appears on Workflows page]
```

### Web UI generation flow

The **Generate workflow** dialog has one field:

| UI label | Hint | User action |
|---|---|---|
| **Describe the workflow you need** | **A complete YAML draft will be generated for you to review and edit before saving.** | Describe the process and click **Generate**. |

While generation runs, the primary button reads **Generating…**. On success, the dialog closes and the editor opens with the YAML draft. The success message says **Workflow generated. Review and save the draft.** If the generator needed its one correction pass, the message says **Workflow generated (one correction pass applied). Review and save the draft.**

### MCP generation and save

`workflow_generate` takes `project_id` and `description`. It returns:

- `yaml` — the generated workflow YAML draft,
- `workflow_id` — the id declared or derived for the draft,
- `was_corrected` — whether one correction pass was needed.

The generated YAML is constrained to the project's castable roles when the project has a team, so generated agent roles stay aligned with the roster.

`workflow_save` persists YAML into the project workspace. It validates YAML, verifies the declared `id` matches the `workflow_id`, dry-run binds the definition to the runtime graph, writes it under `.agentweaver/workflows/`, syncs the registry, and returns the parsed workflow definition.

The boundary is deliberate: generation can be creative, editing can be iterative, and saving is strict. If validation or binding fails, the user sees an error instead of a half-saved workflow.

## Workflow selection during pickup

A Ready task may run with:

1. a task-specific workflow override,
2. the coordinator's best-fit selection among available workflows,
3. the project default as fallback.

The task card workflow menu is the human pre-run override. It opens from the flow icon on a task card and lists valid workflows. Selecting a workflow stores the override. Selecting **Use project default** clears it.

Once a task is claimed, the workflow override can no longer be changed. If a user or assistant races with pickup, the backend returns a conflict and the UI explains that the task was just claimed.

The coordinator selection model is process-fit oriented. It selects the workflow whose steps and outputs fit the task, not the workflow whose name shares words with the task. If selection fails, the default remains the safe fallback. See [Workflow selection](../workflow-selection.md) for the deeper selection contract.

## The per-run workflow graph

The per-run workflow graph is the live execution version of the workflow. It is reached at a workflow run page titled **Run** with the short run id beside it. Unlike the Workflows page graph, this graph carries status.

The graph is seeded from persisted run state and then updated from the run event stream. If a graph descriptor is available, the UI renders the actual workflow nodes and edges. If not, it falls back to the standard pipeline shape.

Node cards show:

- status badges: **Pending**, **In Progress**, **Complete**, **Skipped**, **Failed**, **Revise**, or **Awaiting**,
- role label, such as AI Assistant, RAI Reviewer, Human Review, Merge Coordinator, or Session Logger,
- agent avatar and name when attached,
- model id for the agent node,
- current intent or status message,
- elapsed time while running,
- actions such as **View execution**, **Review now**, **Browse files**, and **View memories**.

Loopback edges light up when the run is actively revising. RAI can send work back to the agent with **Revise**. Human review can send work back with **Request changes**. Rework is visible in the graph instead of hidden in the timeline.

When review waits for the user, the review node shows **Awaiting** and exposes **Review now**. When a running agent needs tool approval, the page shows **Tool approval required — scroll down to respond** and a **Jump to approval** button. The running node also carries a tool-approval badge.

Coordinator child runs use a trimmed graph: Agent → RAI → Assemble-ready. Human Review, Merge, and Scribe happen once at the coordinator level after child output is assembled.

## The backlog board experience

The board is the user's work queue. It presents intake tasks and run cards in one place.

| Column | Stage kind | Description shown in the UI | Who moves work there |
|---|---|---|---|
| **Backlog** | intake | **Captured but not yet committed to. Things you're considering.** | User or MCP client. |
| **Ready** | intake | **Committed work that the coordinator and Ralph monitor may pick up next.** | User or MCP client, then heartbeat. |
| **Problems** | workflow | **Blocked, failed, declined, or otherwise needs attention.** | Coordinator and run lifecycle. |
| **Human Review** | workflow | **Work waiting for a person to review or approve.** | Coordinator and review gates. |
| **Active** | workflow | **Work currently moving through the coordinator workflow.** | Heartbeat and coordinator. |
| **Done** | workflow | **Completed or merged work.** | Coordinator and terminal lifecycle. |

Backlog and Ready are intake columns. They contain draggable task cards. Problems, Human Review, Active, and Done are run-bucket stages. They contain run cards and are owned by the coordinator. Dragging a task into a workflow column is rejected with **Only the coordinator moves work into the workflow.**

### Capturing and editing tasks

The capture bar says **Capture a task into Backlog**. The user enters a title and clicks **Add** or presses Enter. Empty titles are blocked in the UI and by the backend.

MCP uses `backlog_capture_task` with `project_id`, required `title`, and optional `description`. Captured tasks start in Backlog. The card shows the title, optional description, captured-by identity, workflow menu, edit action, and archive action.

**Edit task** opens **Title** and **Description** fields with **Cancel** and **Save**. MCP uses `backlog_edit_task` for the same operation.

`backlog_delete_task` removes an unclaimed backlog task through MCP. If the task has already been claimed, deletion fails with 409 `task_claimed`. Once work becomes a run, the run is the accountable record.

**Archive task** and `backlog_archive_task` remove the task from the active board. If the task is claimed, archiving also archives the linked coordinator run card.

### Promoting Backlog to Ready

Ready is the commitment boundary. A task in Backlog is being considered. A task in Ready is eligible for heartbeat pickup.

The user can promote by dragging Backlog → Ready, using **Add to Ready**, or clicking **Send all to Ready** on a non-empty Backlog column.

| Tool | UI equivalent | Notes |
|---|---|---|
| `backlog_move_to_ready` | Drag Backlog → Ready. | Optional zero-based `target_index`; null appends. |
| `backlog_move_to_backlog` | Drag Ready → Backlog. | Optional zero-based `target_index`; null appends. |
| `backlog_reorder_task` | Drag within Backlog or Ready. | Reorders within the current intake bucket. |
| `send_all_backlog_to_ready` | **Send all to Ready**. | Bulk-promotes all Backlog tasks, preserving relative order and appending after existing Ready tasks. |

`send_all_backlog_to_ready` is idempotent. On an empty backlog, it returns **No backlog tasks to promote.** In the UI, the button only appears when Backlog has cards.

### Board snapshots and stages

`backlog_get_board` returns the full board: Backlog, Ready, Problems, Human Review, Active, and Done. It accepts `include_terminal_history`, which controls how much Done history appears.

The web board uses the same model. Done shows recent terminal cards by default and can reveal older cards with **Show older**. **Show less** collapses the terminal history again.

Run cards are read-only from a workflow-position perspective. They show title, status, current stage or work-plan status, coordinator or agent identity, **Approval needed** when tool approval is pending, **Retry** when failed or merge-failed, and archive.

`backlog_get_workflow_stages` returns the ordered canonical run buckets:

1. **Problems**
2. **Human Review**
3. **Active**
4. **Done**

These are board buckets, not necessarily workflow nodes. Failed, blocked, declined, or merge-failed work maps to Problems. Awaiting review maps to Human Review. In-progress planning, dispatch, and assembly maps to Active. Completed, merged, or terminal work maps to Done.

## Pickup and automation

Pickup turns Ready tasks into coordinator runs. The coordinator heartbeat scans eligible projects, reads top Ready tasks, claims them atomically, reserves a coordinator run, and starts that run unattended.

```mermaid
sequenceDiagram
    participant User
    participant Board
    participant Heartbeat
    participant Pickup
    participant Coordinator

    User->>Board: Move task to Ready
    Heartbeat->>Board: Read top Ready tasks
    Heartbeat->>Pickup: Try pickup task
    Pickup->>Board: Atomic claim + reserve run
    alt claim won
        Pickup->>Coordinator: Start unattended coordinator run
        Coordinator->>Board: Run appears in Active
    else claim lost or project unavailable
        Pickup-->>Board: Task remains or another run owns it
    end
```

A project is eligible when it is active and its workspace is available. If the workspace is unavailable, Ready tasks remain untouched and preserve priority for a later heartbeat. Atomic claim means two ticks or instances cannot create duplicate runs for the same Ready task.

### Pickup settings

The board toolbar includes **Pickup settings**. The dialog has three controls:

| UI control | Backing setting | Meaning |
|---|---|---|
| **Max Ready items per heartbeat** | `max_ready_per_heartbeat` | How many Ready tasks the coordinator may claim per tick. |
| **Autopilot** | `pickup_autopilot` | Auto-answer coordinator clarifying questions for automatically picked-up runs. |
| **Auto-approve tools** | `pickup_auto_approve_tools` | Automatically approve tool calls for automatically picked-up runs, except sandbox-blocked tools. |

`max_ready_per_heartbeat` is bounded from 1 to 20. The UI spin button clamps values into range; the backend rejects out-of-range values. MCP uses `backlog_get_settings` and `backlog_set_settings` for the same state.

### Autopilot UX

Autopilot keeps unattended pickup runs moving through clarifying questions that the coordinator can answer from context. The help text says it auto-answers the coordinator's clarifying questions using the coordinator model so the run does not pause, while tool and permission approvals are still asked, and every auto-answer is logged in the timeline.

The UX contract is:

- Autopilot applies to heartbeat-picked runs and their child runs.
- It auto-answers clarifying questions using the coordinator model.
- It does not silently grant tool or permission approvals.
- Every auto-answer is visible in the run timeline.
- If **Auto-approve tools** is also enabled, normal tool approvals can proceed automatically, but sandbox-blocked destructive shell or network actions still require explicit approval.

Use autopilot when Ready tasks are well-scoped and the user wants queue throughput. Leave it off when tasks require human judgment at the first clarification gate.

## Decomposing a spec into backlog tasks

Spec decomposition turns a markdown document in the project workspace into proposed backlog tasks. It is preview-first: the user sees proposed items before creation.

### Web UI flow

The board toolbar has **Import from workspace**. The user selects a workspace file and clicks **Preview tasks**. Agentweaver analyzes the markdown file and opens **Preview proposed backlog items**.

The preview dialog shows task titles, optional descriptions, **Already exists** badges for duplicates, a cap notice when extraction returns more than the cap, **No actionable items found in this file.** when empty, and **Create tasks** to confirm persistence.

When the user clicks **Create tasks**, Agentweaver creates non-duplicate items in Backlog, appends them after existing Backlog tasks, refreshes the board, and shows **Tasks imported successfully.**

### MCP flow with `backlog_decompose_spec`

`backlog_decompose_spec` takes `project_id`, workspace-relative `file_path`, and `confirm`. With `confirm=false`, it previews. With `confirm=true`, it creates tasks.

The response includes `proposed_items`, `was_capped`, and `total_found`. Each proposed item includes `title`, optional `description`, and `already_exists`.

Recommended MCP flow:

1. call `backlog_decompose_spec` with `confirm=false`,
2. inspect or present `proposed_items`,
3. call it again with `confirm=true` when creation is desired,
4. call `backlog_get_board` to show the new Backlog state.

Results are capped at 50 items. Duplicate detection is scoped to the same project and source file title, so repeated imports from the same spec are safe.

## Web UI and MCP parity

| Experience | Web UI | MCP tool |
|---|---|---|
| List workflows and default | **Workflows** page | `workflows_list` |
| Inspect one workflow | **View graph** and editor detail | `workflow_get` |
| Generate workflow draft | **Generate workflow** | `workflow_generate` |
| Save workflow YAML | Editor **Save** | `workflow_save` |
| Re-read workflows from disk | **Sync** | `workflows_sync` |
| Capture task | **Capture a task into Backlog** / **Add** | `backlog_capture_task` |
| Edit task | **Edit task** | `backlog_edit_task` |
| Delete task | MCP-only cleanup path | `backlog_delete_task` |
| Promote to Ready | Drag, quick-add, **Send all to Ready** | `backlog_move_to_ready`, `send_all_backlog_to_ready` |
| Move back to Backlog | Drag Ready → Backlog | `backlog_move_to_backlog` |
| Reorder intake | Drag within Backlog or Ready | `backlog_reorder_task` |
| Archive task | **Archive task** | `backlog_archive_task` |
| Read board | Board page | `backlog_get_board` |
| Read run buckets | Board columns | `backlog_get_workflow_stages` |
| Read pickup settings | **Pickup settings** | `backlog_get_settings` |
| Update pickup settings | **Pickup settings** → **Save** | `backlog_set_settings` |
| Decompose markdown spec | **Import from workspace** | `backlog_decompose_spec` |

## Edge cases and limits

- **Empty backlog**: Backlog can be empty. The UI shows a count of 0 and a drop zone. `send_all_backlog_to_ready` safely returns **No backlog tasks to promote.**
- **Empty workflow list**: The UI shows **No workflows found** and prompts **Sync**. In normal operation, the built-in default keeps a project from having no usable workflow.
- **Invalid workflows**: Invalid entries stay visible under **Invalid workflows** with errors and cannot become active until fixed.
- **Claimed task deletion**: `backlog_delete_task` returns 409 `task_claimed` for claimed tasks. Operate on the run card instead.
- **Claimed task movement**: moving back to Backlog, reordering, or changing workflow override conflicts after pickup wins the claim.
- **Settings bounds**: `max_ready_per_heartbeat` must be 1-20.
- **Project unavailable**: inactive projects or unavailable workspaces leave Ready tasks untouched for a later heartbeat.
- **Save versus sync**: `workflow_save` writes and refreshes the saved workflow; `workflows_sync` re-reads workflow files after out-of-band disk changes.

## Product principles

- **Preview before persistence**: workflow generation and spec decomposition return drafts or previews before saving or task creation.
- **Ready is explicit commitment**: heartbeat only picks up Ready tasks, never raw Backlog ideas.
- **The coordinator owns workflow movement**: humans rank intake; run lifecycle moves workflow-stage cards.
- **Defaults are visible**: the active workflow is explicit on the Workflows page and returned by `workflows_list`.
- **Automation is accountable**: pickup records the captured-by user, autopilot logs auto-answers, and tool approvals remain governed by sandbox policy.
- **Invalid state is visible**: invalid workflows, failed runs, blocked approvals, and problem cards are surfaced where users can act.

Workflows and backlog make Agentweaver predictable: define the process, queue the work, choose what is Ready, let the heartbeat pick up only committed tasks, and watch every run move through visible stages.
