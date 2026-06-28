<!--
  GENERATED FILE — DO NOT EDIT BY HAND.
  Source: apps/Agentweaver.Mcp/Tools/*.cs ([McpServerTool] attributes)
  Regenerate: node scripts/gen-docs.mjs
  CI verifies this file is in sync (.github/workflows/docs-drift.yml).
-->
# MCP tool index

::: warning Generated
This page is generated from the MCP server source. Do not edit it by hand — run `node scripts/gen-docs.mjs`. For the full parameter reference of each tool, see [MCP server reference](./mcp.md).
:::

The Agentweaver MCP server exposes **79 tools** across **13 categories**. This index is the authoritative list of tool names and one-line descriptions, derived directly from the `[McpServerTool]` attributes in the server source.

## Backlog

| Tool | Description |
| --- | --- |
| `backlog_archive_task` | Archive a backlog task off the active board. Claimed tasks also archive their linked coordinator run card. |
| `backlog_capture_task` | Capture a new task into the project backlog. |
| `backlog_decompose_spec` | Decompose a workspace spec file into proposed backlog tasks for a project. Reads a markdown file from the project's workspace, runs AI decomposition, and returns proposed items for review. Use confirm=true to create the tasks, confirm=false for preview only. Results are capped at 50 items. |
| `backlog_delete_task` | Delete a backlog task. Fails with 409 if the task has already been claimed. |
| `backlog_edit_task` | Edit the title and/or description of a backlog task. |
| `backlog_get_board` | Get the full Kanban board for a project: Backlog, Ready, Problems, Human Review, Active, and Done. |
| `backlog_get_settings` | Get the per-project backlog pickup settings (max_ready_per_heartbeat, pickup_autopilot, pickup_auto_approve_tools). |
| `backlog_get_workflow_stages` | Get the ordered canonical run-bucket definitions for a project (Problems, Human Review, Active, Done). |
| `backlog_move_to_backlog` | Move a task from Ready back to Backlog, optionally at a specific position. |
| `backlog_move_to_ready` | Move a task from Backlog to Ready, optionally at a specific position. |
| `backlog_reorder_task` | Reorder a task within its current bucket (Backlog or Ready) to a new zero-based position. |
| `backlog_set_settings` | Set the per-project backlog pickup settings. max_ready_per_heartbeat must be between 1 and 20. |
| `send_all_backlog_to_ready` | Bulk-promote all Backlog tasks to Ready in one atomic operation. Appends them after any existing Ready tasks, preserving relative order. Idempotent — safe to call on an empty backlog. |

## Blueprint

| Tool | Description |
| --- | --- |
| `blueprint_generate` | Generate a project blueprint from a natural language description of the team and goals. Returns the generated blueprint including roster and workflow assignments. The agent can inspect before creating a project. |
| `list_blueprints` | List the predefined Agentweaver blueprints. Each blueprint specifies a team roster, workflow, review policy, and sandbox profile ready to apply at project creation. |
| `validate_blueprint` | Validate a blueprint object against the schema and role constraints. Returns valid:true with an empty errors array on success, or valid:false with a list of validation errors. |

## Catalog

| Tool | Description |
| --- | --- |
| `catalog_list_roles` | List all available agent roles from the catalog. |
| `catalog_list_scenarios` | List all available casting scenario templates. |

## Coordinator

| Tool | Description |
| --- | --- |
| `coordinator_children_get` | List the child runs dispatched by a Coordinator run, each paired with its subtask status, assigned agent, selected model, and child run status. Empty when nothing has been dispatched. |
| `coordinator_outcome_spec_confirm` | Confirm the drafted outcome spec for a Coordinator run, resuming the suspended run past the confirmation gate. |
| `coordinator_outcome_spec_get` | Get the current persisted outcome spec for a Coordinator run. |
| `coordinator_outcome_spec_revise` | Request a revision of the drafted outcome spec for a Coordinator run. The coordinator re-drafts using the feedback and re-suspends at the confirmation gate. |
| `coordinator_start` | Start a Coordinator orchestration for a project from a plain-language goal. The coordinator drafts a confirmable outcome spec and suspends at the confirmation gate; no work is dispatched until the spec is confirmed. |
| `coordinator_steer` | Steer a Coordinator run. Use 'stop' to cancel active subagents immediately; 'redirect' or 'amend' to inject guidance at the targeted subagent's next turn boundary; or a recovery verb (e.g. 'recover') to reset blocked/failed/parked subtasks and auto-resume the dispatch loop. Omit target_child_run_id to broadcast to every active child. instruction is required for redirect/amend and optional for stop/recovery verbs. Pause is not supported. |
| `coordinator_work_plan_get` | Get the work plan for a Coordinator run: the decomposed subtasks with their assigned agent, selected model, status, child run id, and the dependency edges between subtasks. Returns null when no work plan has been drafted yet. |
| `orchestration_topology` | Get a one-shot topology snapshot for a Coordinator run by combining the work plan and child runs into a current view of subtasks, dependency edges, and dispatched children. For the live graph, point run_watch at the coordinator run id and consume its coordinator.topology, subtask.*, and coordinator.steering events. |

## Diagnostics

| Tool | Description |
| --- | --- |
| `diagnostics_get` | Get a real-time system diagnostics snapshot: API version, process uptime, project/run counts, heartbeat state, and checkpoint GC state. |
| `heartbeat_status` | Get the current coordinator heartbeat service status: enabled flag, interval, last tick time, and service state (running / waiting_first_tick / disabled). |

## GitHub Auth

| Tool | Description |
| --- | --- |
| `github_signin` | Sign in to GitHub using the device flow. Returns a user code and verification URL. The user must visit the URL and enter the code. Polls until authentication completes or times out. |
| `github_signout` | Sign out of GitHub authentication. |
| `github_status` | Check the current GitHub authentication status. |

## Memory

| Tool | Description |
| --- | --- |
| `decision_create` | Create a team decision directly (coordinator path). |
| `decision_inbox_list` | List inbox entries for a project. |
| `decision_inbox_merge` | Merge a pending inbox entry into team decisions. |
| `decision_inbox_reject` | Reject a pending inbox entry. |
| `decision_inbox_submit` | Submit a decision or learning to the agent inbox. |
| `decision_list` | List team decisions for a project. |
| `decision_update` | Update a decision's status, content, or rationale. |
| `memory_export` | Export project memory to .squad/ and .agentweaver/context/ files. |
| `memory_get` | Get a single memory entry. |
| `memory_import` | Import .squad/decisions/inbox/*.md files into the project memory DB. |
| `memory_list` | List memory entries for a specific agent. |
| `memory_record` | Add a memory entry for an agent. |
| `memory_search` | Cross-agent memory search across the whole project. |
| `session_current` | Get the current open session for a project. |
| `session_start` | Start a new work session for a project. |
| `session_update` | Update the current session's focus, summary, or end it. |
| `squad_decide` | Submit a team decision to the decision inbox from a squad agent. |

## Project

| Tool | Description |
| --- | --- |
| `project_configure` | Configure the AI model provider settings for a project. |
| `project_create` | Create a new Agentweaver project. Supply blueprint_id to apply a predefined blueprint, or supply blueprint to apply an inline blueprint; the two options are mutually exclusive. |
| `project_delete` | Delete a project by ID. |
| `project_get` | Get a project by ID. |
| `project_list` | List all Agentweaver projects. |
| `project_list_runs` | List all runs for a project. |
| `project_relink` | Relink a project to a new local working directory path. |
| `project_rename` | Rename an existing project. |

## Run

| Tool | Description |
| --- | --- |
| `run_archive` | Archive a run off active project board/list projections. |
| `run_get_file` | Get the content or diff of a specific file changed by a run. |
| `run_retry` | Retry a failed run by creating a fresh run from its original inputs. |
| `run_review` | Approve or reject a run that is awaiting review. |
| `run_show_artifacts` | List the files changed by a run. |
| `run_status` | Get the current status of a run. |
| `run_submit` | Submit a new agent run for a project. |
| `run_watch` | Watch a run live, streaming progress until completion. |

## Sandbox Policy

| Tool | Description |
| --- | --- |
| `sandbox_policy_get` | Get the sandbox policy for a repository. |
| `sandbox_policy_set` | Set the sandbox policy for a repository. |

## Team

| Tool | Description |
| --- | --- |
| `team_cast` | Cast the team for a project. Can create a proposal, confirm an existing proposal, or create+confirm in one step. If confirm_proposal_id is provided, confirms that proposal. Otherwise creates a new proposal with the given goal. Set confirm=true to automatically confirm the new proposal. |
| `team_get` | Get the current team composition for a project. |
| `team_member_add` | Add a new member to the project team. |
| `team_member_get_charter` | Get the charter document for a team member. |
| `team_member_retire` | Remove (retire) a member from the project team. |

## Workflow

| Tool | Description |
| --- | --- |
| `workflow_generate` | Generate a new workflow definition from a natural language description. Returns YAML draft — not yet saved. Use workflow_save to persist. The agent can inspect the YAML before saving. |
| `workflow_get` | Get the full definition of a single workflow by ID, including its nodes, edges, and trigger. |
| `workflow_save` | Save a workflow YAML to the project workspace. Validates and dry-run binds before saving. Returns the parsed workflow definition. |
| `workflows_list` | List all discovered workflow definitions for a project, including their validation status and which one is the effective default. |
| `workflows_sync` | Re-read the project's workflow definitions from disk, refreshing the in-memory registry. Returns the updated workflow list. |

## Workspace

| Tool | Description |
| --- | --- |
| `get_project_workspace_file` | Get the content of a file in a project workspace at a given ref. Defaults to the base branch when ref is omitted. |
| `list_project_workspace` | List the flat file tree for a project workspace at a given ref. Defaults to the base branch when ref is omitted. |
| `list_project_workspace_refs` | List the browsable git refs for a project workspace: the base branch and any active run worktrees. |

