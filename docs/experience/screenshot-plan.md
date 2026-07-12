# User Guide (Web) — Screenshot plan

This is the master index of every planned **Web** user-guide screenshot. It maps
each placeholder screenshot to the user-guide page it lands on, the route to
navigate to, the click-path to reach the captured state, and exactly what the
shot must show.

> Status: **AKS is not published yet.** No real screenshots have been captured
> yet. Each user-guide page carries a greppable placeholder callout (a
> `📸 **Screenshot**` line) plus an image reference such as
> `/screenshots/{name}.png`. Placeholder images are currently committed so the
> build stays green; replace them with real captures once AKS is live.

## How capture will work (later)

- Images live under `docs/public/screenshots/` and are referenced as
  `/screenshots/{name}.png` (VitePress serves `docs/public/` at the site root).
- The draft Playwright spec `tests/e2e/screenshots.spec.ts` automates capture
  against the **published AKS site** (`BASE_URL`), reusing an already
  signed-in browser context (`STORAGE_STATE`) with a best-effort GitHub
  sign-in fallback. It is DRAFT/skipped and never runs in CI.
- Replace the `{name}.png` placeholder images below by running that spec once AKS is live.


## Prerequisites — generate data first (avoid empty states)

Run the screenshot capture against a populated project. Empty galleries, empty boards,
and no telemetry make most captures useless. There is no committed demo-data generator
or seed endpoint in `apps/Agentweaver.Api` or `scripts/`; use the real product flows below.

1. **Create a project.** Open `/projects` and either:
   - click **Create blank project**, fill **Project name** (and **Repository folder** when shown), choose **Templates**, **Generate**, or **No blueprint**, then click **Create project**; or
   - click **Create from GitHub**, choose a repository from **Repository** or paste `owner/repo` under **Or paste any repository**, fill **Project name** / **Repository folder**, choose **Suggested**, **Templates**, **Generate**, or **No blueprint**, then click **Create project**.
   A selected blueprint applies the real backend blueprint path: it casts the roster and sets the default workflow; **No blueprint** starts empty.
2. **Cast a team when the project is empty.** Open `/projects/{PROJECT_ID}/team`, click **Cast team**, use **Formulate**, **Template**, or **Analyze**, click **Review**, inspect the proposal, click **Confirm**, then **Cast team**. Confirming a proposal writes the team and seeds initial `core_context` agent memories for non-built-in agents.
3. **Populate backlog and workflows.** On `/projects/{PROJECT_ID}/board`, type meaningful tasks into **Capture a task into Backlog** and click **Add**, then move at least one task to **Ready**. To populate workflow screens, open `/projects/{PROJECT_ID}/workflows`; use **Generate workflow** or **New workflow**, review/edit the YAML, save it, and use **Set as default** when needed. Workspace import screenshots need a Markdown/spec file in the project workspace, then **Workspace** → select Markdown → **Import to backlog** → **Create tasks**.
4. **Import or create a skill.** Open `/projects/{PROJECT_ID}/skills`, click **Import Skill**, drop `.md` skill files or a skill folder, or paste a raw `SKILL.md`/GitHub folder URL, click **Preview candidates**, select valid candidates, then **Import**. **Add Skill** and **Generate Skill** are also real ways to populate the catalog.
5. **Create memory and decision data.** Open `/projects/{PROJECT_ID}/memories`, switch to **Agent Memory**, fill **Agent name**, **Type**, and **Content**, then click **Create memory**. Decision rows are written by runs/agents through the real decisions API (`POST /api/projects/{PROJECT_ID}/decisions/inbox` for proposed decisions or `POST /api/projects/{PROJECT_ID}/decisions` for active decisions); merge/promote/reject pending entries in the **Decisions** tab before capture.
6. **Dispatch and let runs execute.** From `/projects/{PROJECT_ID}/board` click **Start task**, enter a goal, optionally choose a workflow, then click **Direct** for fast execution or **Define Outcome** and confirm the outcome plan. The browser console can also start one with `/orchestrate <goal>` and bind one with `/monitor <RUN_ID>`. Let at least one coordinator run create child work, timeline events, token/cost usage, changed files, and (for review shots) an assembly/review state.
7. **Use a live diagnostics environment for operations shots.** Observability, traces, agent token pages, cluster, pod chips, pending capacity, sandbox preview, heartbeat, and global diagnostics depend on executed runs plus the hosted App Insights/Kubernetes/heartbeat services. Capture these only after telemetry exists and the backend reports live data.
8. **Record IDs for the capture spec.** Set `PROJECT_ID` to the populated project, `RUN_ID` to a coordinator run with the needed state, and `EXECUTION_ID` when capturing execution-specific artifacts. Keep additional run IDs handy for pending-capacity, review, sandbox, and trace-preview variants if one run cannot satisfy every state.

## To find a placeholder in the docs

```
rg "📸 \*\*Screenshot" docs/experience
```

## Planned screenshots

| # | Screenshot file | User-guide page | Route | Click-path | What it shows | Data needed |
|---|---|---|---|---|---|---|
| 1 | `app-shell.png` | `00-overview.md` | `/overview` | Sign in → land on Overview | Signed-in shell with the Primary navigation rail, top bar project switcher, Alpha/API status/Console/GitHub account controls, and the routed main content area. | signed-in session |
| 2 | `overview-fleet.png` | `00-overview.md` | `/overview` | Sign in → Overview | Overview command center with Operations health, In flight/Queued work/Done today/Active projects, Recent Projects, AI Usage & Performance, Activity Feed, and Needs Attention. | ≥1 project; executed-run telemetry preferred |
| 3 | `signin-page.png` | `onboarding-auth.md` | `/ (signed out)` | Open Agentweaver while signed out | Sign-in page with logo, Agentweaver title, tagline, and Sign in with GitHub button. | none (signed out) |
| 4 | `signin-error.png` | `onboarding-auth.md` | `/?auth=error&reason=...` | Return from GitHub with auth=error | Sign-in page showing the returned authentication error below the Sign in with GitHub button. | auth-error URL only |
| 5 | `signed-in-topbar.png` | `onboarding-auth.md` | `/overview` | Open the GitHub account menu in the top bar | Top bar with Alpha/API status/Console/project switcher and the GitHub account menu exposing Sign out. | signed-in session |
| 6 | `projects-gallery.png` | `projects.md` | `/projects` | Open Projects | Projects page with project cards, availability badges, Open actions, and Create blank project / Create from GitHub buttons. | ≥1 project |
| 7 | `create-blank-project-dialog.png` | `projects.md` | `/projects` | Click Create blank project | Create blank project dialog with Project name, optional Repository folder, description, blueprint Templates/Generate panel, No blueprint, Cancel, and Create project. | signed-in; no saved data |
| 8 | `create-from-github-dialog.png` | `projects.md` | `/projects` | Click Create from GitHub | Create project from GitHub dialog with Repository combobox, Project name, GitHub sources, paste repository field, optional Repository folder, and Suggested/Templates/Generate blueprint panel. | signed-in; GitHub repo selectable/pasted |
| 9 | `repo-blueprint-suggestions.png` | `repo-blueprint-suggestions.md` | `/projects` | Create from GitHub → choose or paste a repository → Suggested | Suggested blueprint recommendation card with rationale, roster chips, confidence, and repository signals when the recommendation service has data. | GitHub repo plus blueprint suggestion data |
| 10 | `project-dashboard.png` | `projects.md` | `/projects/:projectId` | Open a project | Dashboard command center with live pressure summary, Decision guide, Operational signals range filter, Throughput, Run creation summary, Model performance, and Agent leaderboard. | PROJECT_ID; ≥1 run preferred |
| 11 | `dashboard-token-usage.png` | `token-usage-monitoring.md` | `/projects/:projectId` | Dashboard → Operational signals range | Project dashboard usage surfaces: range selector, Model performance panels, Agent leaderboard Cost column, and AI-credit/token diagnostics when telemetry exists. | PROJECT_ID; token/model telemetry |
| 12 | `project-settings.png` | `projects.md` | `/projects/:projectId/settings` | Click Settings | Project settings with Settings sections rail: General, Sandbox policy, and Danger Zone; General shows rename, default run model, and generation model controls. | PROJECT_ID |
| 13 | `project-generation-model-settings.png` | `project-generation-model-settings.md` | `/projects/:projectId/settings?section=general` | Settings → General → Generation models | Generation models section with Blueprint, Workflow, and Outcome spec generation model fields plus Save and Reset to inherit. | PROJECT_ID |
| 14 | `sandbox-policy.png` | `operations.md` | `/projects/:projectId/settings?section=sandbox` | Settings → Sandbox policy | Sandbox policy section with Shell execution, Sandbox enabled, Outbound network switches, allowed repository roots, blocked command patterns, and Save. | PROJECT_ID |
| 15 | `project-board.png` | `runs-board-watch.md` | `/projects/:projectId/board` | Click Board | Board page with Start task, Kanban columns Backlog, Ready, Problems, Human Review, Active, Done, and the Run audit trail section. | PROJECT_ID; backlog/ready/run data |
| 16 | `backlog-ready.png` | `workflows-backlog.md` | `/projects/:projectId/board` | Board → capture a task into Backlog | Backlog and Ready intake columns with task cards, the Capture a task into Backlog bar, Add button, workflow menu, and drag/reorder affordances. | PROJECT_ID; ≥1 Backlog and ≥1 Ready task |
| 17 | `run-card-actions.png` | `runs-board-watch.md` | `/projects/:projectId/board` | Board → open Run audit trail or inspect a run card | Run action surface with Topology/Open, Abandon run, Delete run, and their confirmation dialogs when a matching run exists. | PROJECT_ID; ≥1 run card |
| 18 | `orchestrations-list.png` | `coordinator-orchestration.md` | `/projects/:projectId/orchestrations` | Click Orchestrations | Project Orchestrations page with coordinator run summary, Active and Recent sections, Open/Stop/Delete actions, and status pills. | PROJECT_ID; ≥1 coordinator run |
| 19 | `workflow-run-graph.png` | `runs-board-watch.md` | `/projects/:projectId/orchestrations/:runId` | Open a coordinator run | Coordinator run console with run tree, selected task panel, execution indicator, topology controls, status/cost chips, and graph panel access. | PROJECT_ID + RUN_ID with work plan |
| 20 | `sandbox-preview-dialog.png` | `runs-board-watch.md` | `/projects/:projectId/orchestrations/:runId` | Coordinator run → Show controls → Preview Sandbox | Sandbox Preview dialog with target port, proxy explanation, Start/Stop/Close actions, and inline iframe when a preview URL exists. | active RUN_ID with sandbox-capable pod |
| 21 | `watch-timeline.png` | `runs-board-watch.md` | `/projects/:projectId/orchestrations/:runId` | Coordinator run → select a child/session detail | Selected task panel showing run messages/timeline, stream state, turn groups, tool calls, lifecycle events, changes, and files. | RUN_ID with child/session timeline events |
| 22 | `watch-token-counter.png` | `token-usage-monitoring.md` | `/projects/:projectId/orchestrations/:runId` | Coordinator run → click AI credits chip | Run token/cost popover with Agent token breakdown, total AI credits/tokens, and per-agent breakdown when usage data exists. | RUN_ID with token/cost usage |
| 23 | `run-pending-capacity.png` | `operations.md` | `/projects/:projectId/orchestrations/:runId` | Open an active coordinator run with a pending-capacity subtask | Coordinator topology/run tree showing Waiting for capacity state on blocked subtasks. | RUN_ID with pending-capacity subtask |
| 24 | `coordinator-topology-pod-chips.png` | `coordinator-orchestration.md` | `/projects/:projectId/orchestrations/:runId` | Active Kubernetes-backed coordinator run | Coordinator/subtask topology cards showing pod chips for dispatched work and no chip for undispatched subtasks. | Kubernetes-backed active RUN_ID with pod chips |
| 25 | `review-changes-tab.png` | `review-workspace-merge.md` | `/projects/:projectId/orchestrations/:runId` | Coordinator run → Artifacts → Changes | Artifact browser Changes tab with Branch Changes, changed-file rows, status badges, additions/deletions, and review actions when awaiting review. | RUN_ID awaiting review with changed files |
| 26 | `review-file-viewer.png` | `review-workspace-merge.md` | `/projects/:projectId/orchestrations/:runId` | Artifacts → Changes → click a changed file | File viewer modal for an execution/assembly file with Diff, Preview, Source toggles and Close controls. | RUN_ID with changed file artifact |
| 27 | `workspace-browser.png` | `review-workspace-merge.md` | `/projects/:projectId/workspace` | Click Workspace | Workspace browser with Current branch indicator, Branch or worktree dropdown, file tree, read-only viewer, and Import to backlog for Markdown files. | PROJECT_ID with workspace files |
| 28 | `decompose-preview-dialog.png` | `workflows-backlog.md` | `/projects/:projectId/workspace` | Workspace → select Markdown → Import to backlog | Preview proposed backlog items dialog with proposed tasks, Already exists badges, empty state, and Create tasks action. | Markdown/spec file in workspace |
| 29 | `team-roster.png` | `team-casting-memory.md` | `/projects/:projectId/team` | Click Team/Agents | Agents page with roster cards, System agent/Project agent badges, All/Active/Retired filters, Add member, Sync, and Cast team. | team cast |
| 30 | `team-member-detail.png` | `team-casting-memory.md` | `/projects/:projectId/team` | Click an agent card | Agent detail drawer with Overview, Charter, Capabilities tabs; Overview shows Model, Charter path, Recent history, and Assigned skills. | team cast; member selected |
| 31 | `casting-wizard-cast.png` | `team-casting-memory.md` | `/projects/:projectId/team/cast` | Team → Cast team | Cast a team step 1 with Formulate, Template, Analyze tabs, Team size, Roles checkboxes, Universe accordion, and Review action. | PROJECT_ID; casting templates/universes |
| 32 | `casting-wizard-review.png` | `team-casting-memory.md` | `/projects/:projectId/team/cast` | Generate/select a proposal → Review | Review proposal step with proposed member cards, View/Hide charter, Remove, Augment/Recast choice when applicable, Back, Cancel, and Confirm. | casting proposal generated/selected |
| 33 | `memories-decisions.png` | `team-casting-memory.md` | `/projects/:projectId/memories` | Click Memories | Team Memory Decisions tab with finalized decisions and Proposed decisions awaiting Coordinator inbox with Merge/Promote/Reject. | active or proposed decisions |
| 34 | `memories-agent-memory.png` | `team-casting-memory.md` | `/projects/:projectId/memories` | Click Agent Memory tab | Agent Memory tab with memory entries, importance/type/time badges, Update action, and Create memory entry form. | agent memory entries |
| 35 | `skills-catalog.png` | `project-skills.md` | `/projects/:projectId/skills` | Click Skills | Skills Catalog tab with Add Skill, Generate Skill, Import Skill, Sync connected repo, status/provenance badges, assigned agent chips, View, and Delete. | ≥1 skill imported/created; team for assignments |
| 36 | `skill-import-dialog.png` | `project-skills.md` | `/projects/:projectId/skills` | Skills → Import Skill | Import Skill dialog with trusted-source warning, file/folder dropzones, GitHub/raw URL field, Preview candidates, Import, and candidate selection results. | PROJECT_ID; import source ready |
| 37 | `workflows-list.png` | `workflows-backlog.md` | `/projects/:projectId/workflows` | Click Workflows | Workflows page with Active workflow, Available workflows, Invalid workflows, New workflow, Generate workflow, Set as default, Sync, Edit, Edit visually, and View graph. | ≥1 valid workflow |
| 38 | `workflow-definition-graph.png` | `workflows-backlog.md` | `/projects/:projectId/workflows` | Workflows → View graph on a valid workflow | Inline workflow definition graph expanded inside a workflow card, showing workflow nodes and edges for the selected definition. | valid workflow graph expanded |
| 39 | `flow-agents.png` | `operations.md` | `/projects/:projectId/flow` | Click Flow | Flow page with per-agent active/queued/blocked/done cards, View orchestration links, Refresh, and Previous work archive when an agent filter is selected. | agent queues from backlog/runs; history if filtering |
| 40 | `diagnostics-checks.png` | `operations.md` | `/projects/:projectId/diagnostics` | Click Diagnostics | Diagnostics page with Global/This project tabs, Auto-refresh, Re-run, updated time, summary cards, and pass/warn/fail check cards. | diagnostics endpoint returning checks |
| 41 | `diagnostics-global-health.png` | `scaling-operations.md` | `/projects/:projectId/diagnostics` | Diagnostics → Global tab | Global diagnostics summary with API version, Uptime, Total projects, Total runs, Active runs, and health check list. | global diagnostics with projects/runs |
| 42 | `heartbeat-status.png` | `operations.md` | `/projects/:projectId/heartbeat` | Click Heartbeat | Heartbeat page with service status, Auto-refresh/Refresh, Automations cards, last tick/error, and Recent activity table. | heartbeat service enabled; ticks preferred |
| 43 | `heartbeat-automation-column.png` | `operations.md` | `/projects/:projectId/heartbeat` | Heartbeat → Recent activity | Recent heartbeat ticks table with Automation as the first column followed by When, Acted, Errors, Duration, and Error. | recent heartbeat ticks |
| 44 | `cluster-page.png` | `cluster-page.md` | `/projects/:projectId/cluster` | Click Cluster | Cluster page with Orphaned, Pending capacity, Checks OK, Warm pool KPI cards, Health checks, Sandbox claims, orphaned pods, pending capacity, warm pools, and sandbox objects. | Kubernetes cluster diagnostics/live pods |
| 45 | `overview-active-projects.png` | `scaling-operations.md` | `/overview` | Overview with recent projects populated | Overview Recent Projects and Active projects signals as seen by a scaled deployment. | overview with recent/active projects |
| 46 | `overview-token-usage.png` | `token-usage-monitoring.md` | `/overview` | Overview → AI Usage & Performance | Overview AI Usage & Performance section with range selector, token consumption by model, model distribution, response duration, TTFT, and success rate tiles. | overview token telemetry from executed runs |
| 47 | `observability-overview.png` | `token-usage-monitoring.md` | `/projects/:projectId/observability` | Click Observability | Project Observability Overview tab with range selector, Refresh, and Model performance panels for invocation, AI credit, model usage, response duration, and TTFT metrics. | App Insights/model telemetry |
| 48 | `observability-agents.png` | `token-usage-monitoring.md` | `/projects/:projectId/observability/agents` | Observability → Agents | Observability Agents tab with Agent token breakdown aggregated by agent over the selected range. | agent token telemetry |
| 49 | `observability-traces.png` | `transaction-traces.md` | `/projects/:projectId/observability/traces` | Observability → Traces | Observability Traces tab listing recent coordinator runs with status badges, Open run, Preview trace, and Refresh. | ≥1 coordinator run |
| 50 | `observability-trace-preview.png` | `transaction-traces.md` | `/projects/:projectId/observability/traces` | Traces → Preview trace | Expanded trace preview with hierarchical transaction trace panel, span rows, and selected span details when AppInsights data is available. | App Insights trace data for run |
| 51 | `browser-console.png` | `browser-console.md` | `/overview or /console` | Click Console in top bar (or navigate /console) | Control console slide panel with Agentweaver Console header, context badges, shortcut buttons, response scrollback, and bottom prompt. | signed-in session; project/run context optional |
| 52 | `assembly-review-escalation.png` | `resilient-assembly-review.md` | `/projects/:projectId/orchestrations/:runId` | Let an assembly gate exhaust its steering budget → review card opens | Human-review card with `reason: steering_budget_exhausted`, accumulated autonomous gate feedback, and Approve / Decline / Request changes actions. | active RUN_ID whose assembly steering budget is exhausted |

## Count per page

| User-guide page | Screenshots |
|---|---|
| `00-overview.md` | 2 |
| `browser-console.md` | 1 |
| `cluster-page.md` | 1 |
| `coordinator-orchestration.md` | 2 |
| `onboarding-auth.md` | 3 |
| `operations.md` | 6 |
| `project-generation-model-settings.md` | 1 |
| `project-skills.md` | 2 |
| `projects.md` | 5 |
| `repo-blueprint-suggestions.md` | 1 |
| `review-workspace-merge.md` | 3 |
| `runs-board-watch.md` | 5 |
| `scaling-operations.md` | 2 |
| `team-casting-memory.md` | 6 |
| `token-usage-monitoring.md` | 5 |
| `transaction-traces.md` | 2 |
| `workflows-backlog.md` | 4 |
| `resilient-assembly-review.md` | 1 |
| **Total** | **52** |
