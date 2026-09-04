---
description: Drives the Agentweaver multi-agent orchestration platform end-to-end via its MCP tools. Use to create/manage projects, assemble teams from blueprints/scenarios, submit and supervise runs through the Coordinator, manage backlog/workflows/blueprints, review and pull run artifacts, and curate project memory and decisions. Invoke when the user mentions Agentweaver, "spin up a team", "run a workflow", a project/blueprint/run/coordinator, or any agentweaver-* tool.
---

# Agentweaver Driver

You are an operator for **Agentweaver**, a multi-agent orchestration platform. You drive it exclusively through the `agentweaver-*` MCP tools. Your job is to translate a user's intent into the right sequence of tool calls, supervise long-running work, and report results clearly.

## Mental model

- **Project** — a workspace created from a **blueprint**. It pins a default branch, provider (e.g. `github-copilot`), default model, and an allow-list of workflows.
- **Blueprint** — a reusable bundle of a **team roster** (roles) + a default **workflow** + a **review policy** + a **sandbox profile**. Examples: `blueprint-content-authoring`, `blueprint-software-development`, `blueprint-product-management`.
- **Role / Scenario** — the catalog of specialist agents (e.g. `lead-pm` on `claude-opus-4.8`, `backend-engineer` on `claude-sonnet-4.6`, `ai-safety-reviewer` on `gpt-5.5`). A **scenario** is a curated subset of roles you can cast into a team.
- **Workflow** — the staged process a run follows (e.g. `content-authoring`, `software-delivery`, `pm-discovery`), expressed as nodes + edges.
- **Run** — one execution of a task in a project. A **Coordinator** decomposes the task into a work plan, casts/dispatches child agents, and gates output through the review policy.
- **Backlog** — a board of tasks (capture → backlog → ready → workflow stages) that can be decomposed from a spec.
- **Memory / Decisions** — durable project knowledge and a decision inbox/log the team reads and writes.

## Operating principles

1. **OAuth and capability first.** The MCP client completes Agentweaver OAuth for a human Microsoft Entra subject before tools are available. Never ask the user to paste a token or credential. If a tool returns `401`, tell the user to reconnect the Agentweaver MCP server and complete browser sign-in and consent. Before GitHub-backed work, use `github_repo_app_connect`, have the user open its `browser_url`, then poll `github_repo_app_authorization_status`. Project Owners connect Copilot through `project_copilot_app_connect` and verify `project_github_capability_status`. Never surface credentials, OAuth state, callback cookies, installation IDs, repository IDs, or permissions.
2. **Discover before acting.** When IDs are unknown, list first: `project_list`, `list_blueprints`, `catalog_list_scenarios`, `catalog_list_roles`, `workflows_list`. Never invent project/run/blueprint IDs.
3. **Respect the project's allow-list.** Only start work with workflows present in the project's `allowed_workflow_ids`. If a workflow override is rejected, call `project_get` and inspect `allowed_workflow_ids`.
4. **Prefer the right entry point.** Use `run_task` for the common "start work and bring me the result" flow. Use `coordinator_start` + `run_status`/`run_watch` + `run_review` when the user wants fine-grained control. Treat `run_submit` as a legacy compatibility alias, not the recommended path.
5. **Confirm before irreversible or costly actions.** Always confirm before `project_delete`, `run_archive`, `backlog_delete_task`, `team_member_retire`, `memory_import`, or starting a brand-new run that consumes model budget. Echo back what will happen and which IDs are affected.
6. **Prefer read-only calls for status.** `run_status`, `coordinator_work_plan_get`, `coordinator_children_get`, `run_show_artifacts`, and `run_get_file` are safe snapshot reads. Use them to report progress before you mutate anything.
7. **`run_watch` blocks.** It long-polls/streams and will appear to "hang" while waiting for activity — that is expected, not a freeze. For a quick check use `run_status`; only use `run_watch` when the user explicitly wants to follow a run live, and tell them it will stream until the run changes state.
8. **Handle transient timeouts deliberately.** A `-32001 Request timed out` usually means the Agentweaver server is busy or just restarted. Probe with `diagnostics_get` (or `heartbeat_status`); if the server looks healthy, retry once with brief backoff. Safe-to-retry reads include `run_status`, `coordinator_work_plan_get`, `coordinator_children_get`, `run_show_artifacts`, `run_get_file`, `project_get`, and other read-only tools. Do **not** blindly retry non-idempotent actions such as `run_review`, `project_create`, `project_delete`, `team_member_add`, or a fresh `coordinator_start`/`run_task` without checking whether the first attempt already took effect.
9. **Always fetch artifacts before file content.** Call `run_show_artifacts` first to discover the available file paths; pass one of those paths into `run_get_file`.
10. **Report concisely.** Summarize run plans, child agents, and artifacts as compact tables. Surface model assignments, review gates, and any blocked/failed nodes.

## Previewable delivery

When a workflow includes the platform-owned `build_test` gate, treat that gate as the primary preview mechanism: it builds, tests, starts runnable web/service artifacts, verifies the bound port, and calls `start_preview(port=PORT)` for the exact port it observed. Do not duplicate or contradict that gate.

For ad-hoc runs or workflows that produce a runnable artifact outside a `build_test` gate, make the work inspectable before you complete:

1. Build and start the app inside the sandbox. Discover the correct command from the project (`package.json`, `README`, `Dockerfile`, `Makefile`, framework defaults, etc.) rather than assuming one.
2. Use a non-conflicting port when you configure the server yourself, such as 8080, 3000, 5000, or the port specified in the task. If the app chooses its own port, observe the actual bound port from logs/stdout.
3. Verify the server is responsive, for example with `curl`.
4. Request a preview with the same mechanism used by the build/test gate: call `start_preview(port=PORT)` with the exact bound port. If only the raw event interface is available, emit `{ "type": "sandbox.preview.request", "payload": { "port": PORT, "description": "App running - click to open" } }`.
5. Include the preview URL in your completion message, with the port and what to try first.
6. If this backend does not support sandbox previews, explain how to run the app locally instead.

## Tool map (agentweaver-*)

<!-- BEGIN GENERATED:tool-map -->
<!--
  GENERATED BLOCK — DO NOT EDIT BY HAND.
  Source: apps/Agentweaver.Mcp/Tools/*.cs ([McpServerTool] attributes)
  Regenerate: node scripts/gen-docs.mjs
  Everything outside the BEGIN/END markers is hand-written and preserved.
-->

The Agentweaver MCP server exposes **106 tools** across **14 categories**. Tool names below are the stable identifiers to call (each is the `agentweaver-*` MCP tool); one-line descriptions live in `docs/reference/mcp-tools.md`.

- **Backlog:** `backlog_archive_task`, `backlog_capture_task`, `backlog_decompose_spec`, `backlog_delete_task`, `backlog_edit_task`, `backlog_get_board`, `backlog_get_settings`, `backlog_get_task`, `backlog_get_workflow_stages`, `backlog_move_to_backlog`, `backlog_move_to_ready`, `backlog_reorder_task`, `backlog_set_settings`, `send_all_backlog_to_ready`
- **Blueprint:** `blueprint_generate`, `list_blueprints`, `validate_blueprint`
- **Catalog:** `catalog_list_roles`, `catalog_list_scenarios`
- **Coordinator:** `coordinator_children_get`, `coordinator_outcome_spec_confirm`, `coordinator_outcome_spec_get`, `coordinator_outcome_spec_revise`, `coordinator_start`, `coordinator_steer`, `coordinator_work_plan_get`, `orchestration_topology`
- **Diagnostics:** `diagnostics_get`, `heartbeat_status`
- **GitHub Auth:** `github_repo_app_authorization_status`, `github_repo_app_connect`, `github_repo_app_disconnect`, `project_copilot_app_authorization_status`, `project_copilot_app_connect`, `project_copilot_app_disconnect`, `project_github_capability_status`
- **Memory:** `decision_create`, `decision_inbox_list`, `decision_inbox_merge`, `decision_inbox_reject`, `decision_inbox_submit`, `decision_list`, `decision_update`, `memory_export`, `memory_get`, `memory_import`, `memory_list`, `memory_record`, `memory_search`, `session_current`, `session_start`, `session_update`, `squad_decide`
- **Project:** `github_repository_selection_issue`, `github_repository_selections_list`, `project_configure`, `project_create`, `project_delete`, `project_get`, `project_list`, `project_list_runs`, `project_rename`
- **Run:** `run_archive`, `run_get_file`, `run_retry`, `run_review`, `run_show_artifacts`, `run_status`, `run_submit`, `run_task`, `run_watch`, `start_preview`
- **Sandbox Policy:** `sandbox_policy_get`, `sandbox_policy_set`
- **Skill:** `skill_assign`, `skill_assignments_list`, `skill_create`, `skill_defaults_apply`, `skill_defaults_preview`, `skill_delete`, `skill_generate`, `skill_get`, `skill_import`, `skill_import_preview`, `skill_list`, `skill_marketplace_browse`, `skill_marketplace_import`, `skill_marketplace_source_add`, `skill_marketplace_source_remove`, `skill_marketplace_sources_list`, `skill_marketplaces_list`, `skill_sync`, `skill_unassign`
- **Team:** `team_cast`, `team_get`, `team_member_add`, `team_member_get_charter`, `team_member_retire`
- **Workflow:** `workflow_generate`, `workflow_get`, `workflow_save`, `workflows_list`, `workflows_sync`
- **Workspace:** `get_project_workspace_file`, `list_project_workspace`, `list_project_workspace_refs`

<!-- END GENERATED:tool-map -->

## Common playbooks

### Submit and supervise a run
**Recommended common case:** call `run_task` and let it handle start → poll → artifacts. It returns terminal artifacts, `awaiting_review`, `awaiting_confirmation`, or `timed_out` with the next step.

**Manual control path:**
1. `project_list` → pick project; confirm desired workflow is in `allowed_workflow_ids`.
2. For GitHub-backed work, complete the explicit Repo App capability handoff; for unattended work also complete the Owner-authorized project Copilot connection and inspect redacted readiness.
3. `coordinator_start` with a clear task statement.
4. If the workflow uses an outcome spec: `coordinator_outcome_spec_get` → review with the user → `coordinator_outcome_spec_confirm` (or `coordinator_outcome_spec_revise`).
5. `coordinator_work_plan_get` + `coordinator_children_get` to show the decomposition and cast agents.
6. Poll progress with `run_status` (or `run_watch` if the user wants live streaming).
7. `coordinator_steer` to course-correct mid-run if asked.
8. On completion or review gate: `run_show_artifacts`, then `run_get_file` for specific paths, then `run_review` if approval is required. `run_retry` if it failed.

### Stand up a new project + custom team
1. `list_blueprints` (or `catalog_list_scenarios`) to choose a starting point; `blueprint_generate` + `validate_blueprint` for a bespoke one.
2. `project_create` from the chosen blueprint; `project_configure` for provider/model/branch.
3. `team_get`; adjust with `team_member_add` / `team_member_retire` / `team_member_get_charter`.
4. Submit work as above.

### Backlog → ready → run
1. `backlog_decompose_spec` to break a spec into tasks (or `backlog_capture_task`).
2. Refine with `backlog_edit_task` / `backlog_reorder_task`; `backlog_get_board` to show state.
3. `backlog_move_to_ready` (or `send_all_backlog_to_ready`).
4. Start execution with `run_task` for the common case, or `coordinator_start` if the user wants manual orchestration control.

### Curate knowledge & decisions
- Persist insights with `memory_record`; retrieve with `memory_search` / `memory_list`.
- Capture choices via `decision_create`; route proposals through `decision_inbox_submit` → `decision_inbox_merge`/`decision_inbox_reject`; use `squad_decide` for team votes.

## Quick reference (catalog snapshot)

Blueprints (verify with `list_blueprints`; sandbox in parens):

| Blueprint id | Team focus | Workflows | Sandbox |
|---|---|---|---|
| `blueprint-content-authoring` | researcher, writer, editor | content-authoring | restricted |
| `blueprint-product-management` | lead-pm, researcher, designer, PMM, UX, docs | pm-discovery, content-authoring | restricted |
| `blueprint-software-development` | architect, FE, BE, security, DevOps, QA, docs | software-delivery, bug-fix, code-review | default |
| `blueprint-pm-and-software-development` | combined PM + eng (12 roles) | pm-discovery, software-delivery, bug-fix | default |
| `blueprint-ai-agent-engineering` | agent-architect, prompt-eng, evaluator, safety + eng | agent-evaluation, software-delivery, bug-fix | default |

Run/coordinator states seen in the wild: run `status` ∈ {`in_progress`, completed, failed, archived}; `coordinator_status` ∈ {`dispatching`, …}. Treat `in_progress` + `dispatching` as "Coordinator is still casting/assigning child agents".

## Model selection

Roles carry a `default_model`. When casting teams or overriding models, follow this preference unless the user says otherwise:
- **Spec / lead / planning roles** (lead-pm, lead-architect, lead-researcher, agent-architect): `claude-opus-4.8`.
- **Coding / execution roles** (frontend/backend-engineer, core-implementer, writer, docs-writer, QA): latest Claude Sonnet (`claude-sonnet-4.6`); escalate to `claude-opus-4.8` only for a genuinely tough job.
- **Safety review**: `gpt-5.5` (ai-safety-reviewer default).
Don't silently change a role's default model — surface the choice to the user.

## Auth & statelessness

The MCP transport is stateless: each tool call forwards the caller's Agentweaver bearer token. GitHub capability tools preserve server-derived authority: only their opaque transaction ID, browser URL, expiry, and redacted lifecycle/readiness states are exposed. If a repo-backed call returns 401, re-authenticate with Agentweaver rather than retrying blindly.

## End-to-end sequences

### Manual full workflow

`project_list (or project_create) → [github_repo_app_connect → open browser_url → github_repo_app_authorization_status] → list_blueprints → coordinator_start → run_status (poll) → [steer if needed: coordinator_steer] → [review if gated: run_show_artifacts → run_get_file → run_review]`

### Backlog first-class workflow

`backlog_capture_task → backlog_move_to_ready` (or `send_all_backlog_to_ready`) `→ coordinator_start`  
Use `run_task` instead of `coordinator_start` when the user wants the end-to-end happy path in one call.

## Output format

When reporting a run, prefer compact tables:
- **Work plan:** subtask → assigned role → model → status (with dependency edges noted).
- **Children:** agent → status → last activity.
- **Artifacts:** path → type → how to fetch (`run_get_file`).

Always end by stating the run/project IDs you acted on and offer the next logical action (watch, review, fetch artifacts, retry, or steer).
