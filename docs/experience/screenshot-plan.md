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

## To find a placeholder in the docs

```
rg "📸 \*\*Screenshot" docs/experience
```

## Planned screenshots

| # | Screenshot file | User-guide page | Route | Click-path | What it shows |
|---|---|---|---|---|---|
| 1 | `app-shell.png` | `00-overview.md` | `/overview` | Sign in → land on Overview | Signed-in shell: left nav rail (`aria-label="Primary navigation"`), top bar with project switcher / `Alpha` badge / API status dot / GitHub sign-in, and the main content area. |
| 2 | `overview-fleet.png` | `00-overview.md` | `/overview` | Sign in → **Overview** in left rail | Overview page "Fleet activity at a glance." with **Live sessions**, **Active workflow runs**, **Active projects**, **Recent activity**, and the **Refresh** button. |
| 3 | `signin-page.png` | `onboarding-auth.md` | `/` (signed out) | Open Agentweaver while signed out | `SignInPage`: logo, **Agentweaver** title, tagline "Build workflows from specialized agents", and the **Sign in with GitHub** button (→ `/auth/github/authorize`). |
| 4 | `signin-error.png` | `onboarding-auth.md` | `/?auth=error&reason=...` | Return from GitHub with a denied/expired auth | Sign-in page with red error text below the **Sign in with GitHub** button. |
| 5 | `signed-in-topbar.png` | `onboarding-auth.md` | `/overview` | Click the GitHub account trigger in the top bar | Top bar with the `Alpha` badge, project switcher, API status dot, and the opened account menu revealing avatar, login, and **Sign out**. |
| 6 | `projects-gallery.png` | `projects.md` | `/projects` | Sign in → navigate to Projects | Projects page "Your Agentweaver projects." with project cards (**Available**/**Unavailable** badge, **Open**), and the **Create blank project** / **Create from GitHub** buttons. |
| 7 | `create-blank-project-dialog.png` | `projects.md` | `/projects` | Click **Create blank project** | **Create blank project** dialog: **Name** field (placeholder "My project") and **Repository folder** auto-filled (slugified) from the name; **Cancel** / **Create**. |
| 8 | `create-from-github-dialog.png` | `projects.md` | `/projects` | Click **Create from GitHub** | **Create project from GitHub** dialog: **Name**, **Organization** (`aria-label="Organization"`, **Org** badge), searchable **Source repository** (`aria-label="Repository"`), auto-filled **Repository folder**; or **Connect GitHub** when unauthenticated. |
| 9 | `project-dashboard.png` | `projects.md` | `/projects/:projectId` | Open a project from the gallery | **Dashboard** "Delivery metrics and the agent leaderboard." with **Refresh**, **Throughput (last 30 days)**, and the **Agent leaderboard** table. |
| 10 | `project-settings.png` | `projects.md` | `/projects/:projectId/settings` | **Settings** in left rail | **Project settings** with left rail **General** / **Sandbox policy** / **Review policy** / **Danger Zone**; **General** selected. |
| 11 | `project-board.png` | `runs-board-watch.md` | `/projects/:projectId/board` | **Board** in left rail | Board with the six columns **Backlog**, **Ready**, **Problems**, **Human Review**, **Active**, **Done**, the **Runs** section, and the start-orchestration action. |
| 12 | `run-card-actions.png` | `runs-board-watch.md` | `/projects/:projectId/board` | Hover a run card in **Runs** | Run card with **Workflow** (or **Topology**) button, **Abandon** (`aria-label="Abandon run"` → "Abandon run?"), and delete icon (`aria-label="Delete run"` → "Delete run?"). |
| 13 | `workflow-run-graph.png` | `runs-board-watch.md` | `/projects/:projectId/runs/:runId/workflow` | Click a run card's **Workflow** | Run page with the live run graph, **Auto-approve tools** toggle, **Preview** button (k8s sandbox + active run), and **Jump to approval** when a question waits. |
| 14 | `sandbox-preview-dialog.png` | `runs-board-watch.md` | `/projects/:projectId/runs/:runId/workflow` | Click **Preview** (k8s sandbox, active run) | **Sandbox Preview** dialog: "Preview traffic is proxied through the Agentweaver API server.", "Preview active for port {port} on pod {pod_name}.", **Cancel** / **Start preview** / **Stop preview** / **Close**. |
| 15 | `watch-timeline.png` | `runs-board-watch.md` | `/projects/:projectId/runs/:runId/execution/:executionId` | Open an execution from a run | **Execution** watch page: breadcrumb (`aria-label="Breadcrumb"`), run header status (**Connecting**/**Streaming**/**done**/**error**), and the **Run timeline** with turn groups, message bubbles, tool-call and lifecycle cards. |
| 16 | `review-changes-tab.png` | `review-workspace-merge.md` | run artifact view (Changes tab) | Open a run awaiting review → **Changes** tab | **Changes** tab: **Branch Changes** header, changed-file rows with filename, `+12`/`-3` counts, and `A`/`M`/`D` status badges. |
| 17 | `review-file-viewer.png` | `review-workspace-merge.md` | run artifact view (file modal) | Click a changed-file row | File viewer modal "Execution {shortId}" defaulting to **Diff**, with **Preview** (Markdown) / **Source** toggles and close (`aria-label="Close"`) + footer **Close**. |
| 18 | `workspace-browser.png` | `review-workspace-merge.md` | `/projects/:projectId/workspace` | **Workspace** in left rail | **Workspace** "...read-only." with **Current branch** label, **Branch or worktree** dropdown, left file tree, right read-only viewer, and **Import to backlog** when Markdown is selected. |
| 19 | `team-roster.png` | `team-casting-memory.md` | `/projects/:projectId/team` | **Agents** in left rail | **Agents** "The cast working on this project." with roster cards (avatar, name, role, status, **System agent**/**Project agent** badge), **All**/**Active**/**Retired** filters, and **Add member** / **Sync** / **Cast team**. |
| 20 | `team-member-detail.png` | `team-casting-memory.md` | `/projects/:projectId/team` | Click a roster card | Agent detail drawer (opened via `aria-label="Open details for {member.name}"`) with **Overview** / **Charter** / **Capabilities** tabs; Overview shows **Model**, **Charter path**, **Recent history**. |
| 21 | `casting-wizard-cast.png` | `team-casting-memory.md` | `/projects/:projectId/team/cast` | Click **Cast team** | **Cast a team** step **1. Cast** with **Formulate** / **Template** / **Analyze** tabs, **Team size**, **Roles** checkboxes, **Universe** accordion, and the primary action (**Formulate →** / **Analyze →** / **Review**). |
| 22 | `casting-wizard-review.png` | `team-casting-memory.md` | `/projects/:projectId/team/cast` | Complete step 1 → **Review** | **2. Review proposal** with member cards (name, role, **View charter**/**Hide charter**, **Remove**); existing-team choice **Augment** vs **Recast**; **Back** / **Cancel** / **Confirm**. |
| 23 | `memories-decisions.png` | `team-casting-memory.md` | `/projects/:projectId/memories` | **Memories** in left rail | **Team Memory** with **Decisions** / **Agent Memory** tabs; Decisions tab shows finalized decisions and the inbox (`aria-label="Proposed decisions awaiting Coordinator"`) with **Merge** / **Promote** / **Reject**. |
| 24 | `memories-agent-memory.png` | `team-casting-memory.md` | `/projects/:projectId/memories` | Click the **Agent Memory** tab | **Agent Memory** entries (agent name, importance, type, time, content, **Update**) and the **Create memory entry** form (`aria-label="Create memory entry"`) with **Agent name** / **Type** / **Content** / **Create memory**. |
| 25 | `workflows-list.png` | `workflows-backlog.md` | `/projects/:projectId/workflows` | **Workflows** in left rail | **Workflows** "Reusable pipeline definitions." with **Active workflow** / **Available workflows** / **Invalid workflows** sections, **Active**/**Valid**/**Invalid**/**Built-in** badges, and **New workflow** / **Generate workflow** / **Set as default** / **Sync**. |
| 26 | `per-run-workflow-graph.png` | `workflows-backlog.md` | `/projects/:projectId/runs/:runId/workflow` | Open a run's **Workflow** view | Per-run graph with status badges (**Pending**/**In Progress**/**Complete**/**Skipped**/**Failed**/**Revise**/**Awaiting**), role labels, and node actions (**View execution** / **Review now** / **Browse files** / **View memories**); loopback edges on revision. |
| 27 | `backlog-ready.png` | `workflows-backlog.md` | `/projects/:projectId/board` | **Board** in left rail | Intake columns **Backlog** and **Ready** with draggable task cards, the **Capture a task into Backlog** bar and **Add** button, and a card being dragged Backlog → Ready. |
| 28 | `decompose-preview-dialog.png` | `workflows-backlog.md` | `/projects/:projectId/workspace` | Select a Markdown spec → **Import to backlog** | **Preview proposed backlog items** dialog: proposed titles/descriptions, **Already exists** badges, empty-state "No actionable items found in this file.", and **Create tasks**. |
| 29 | `diagnostics-checks.png` | `operations.md` | `/projects/:projectId/diagnostics` | **Diagnostics** in left rail | **Diagnostics** "System and project health checks." with **Global** / **This project** tabs (`aria-label="Diagnostics scope"`), **Auto-refresh**, **Re-run**, **Updated** time, and "Checks (n) · {ms} ms" with `pass`/`warn`/`fail` badges. |
| 30 | `heartbeat-status.png` | `operations.md` | `/projects/:projectId/heartbeat` | **Heartbeat** in left rail | **Heartbeat** "Background automation status and recent ticks." with **Auto-refresh** / **Refresh**, **Automations**, and **Recent activity** (`aria-label="Recent heartbeat ticks"`). |
| 31 | `flow-agents.png` | `operations.md` | `/projects/:projectId/flow` | **Flow** in left rail | **Flow** "What each agent is working on right now." with **Refresh** and per-agent cards (active → queued → blocked); with an agent selected, **Previous work archive** (`aria-label="Previous work archive"`). |
| 32 | `sandbox-policy.png` | `operations.md` | `/projects/:projectId/settings` (Sandbox policy) | **Settings** → **Sandbox policy** | **Sandbox policy** with **Shell execution** / **Sandbox enabled** / **Outbound network** switches and read-only **Allowed repository roots** / **Blocked command patterns** lists. |
| 33 | `overview-active-projects.png` | `scaling-operations.md` | `/overview` | **Overview** in left rail | Overview with **Active workflow runs** and **Active projects** populated — identical view whether served by one combined pod or a web/worker fleet. |
| 34 | `diagnostics-global-health.png` | `scaling-operations.md` | `/projects/:projectId/diagnostics` (Global tab) | **Diagnostics** → **Global** tab | Global diagnostics with **API version**, **Uptime**, **Total projects**, **Total runs**, **Active runs** and `pass`/`warn`/`fail` check cards — confirms a scaled deployment is healthy. |
| 35 | `cluster-page.png` | `cluster-page.md` | `/projects/:projectId/cluster` | **Cluster** in SYSTEM left rail | Cluster page with KPI cards (Active pods, Orphaned pods, CPU used/total, Pending runs), quota bars (CPU, memory), component health table (6 checks), and Active / Orphaned / Pending pods tables. |
| 36 | `cluster-page-quota-warning.png` | `cluster-page.md` | `/projects/:projectId/cluster` | **Cluster** → observe a red CPU bar | Cluster page with the CPU quota bar in red near-limit state and at least one entry in the Pending-capacity runs table. |
| 37 | `heartbeat-automation-column.png` | `operations.md` | `/projects/:projectId/heartbeat` | **Heartbeat** in left rail | Heartbeat page **Recent Activity** table showing the **Automation** column as the first column, with values such as `Coordinator Heartbeat` and `Checkpoint GC` alongside When, Acted, Errors, Duration. |
| 38 | `run-pending-capacity.png` | `operations.md` | `/projects/:projectId/runs/:runId/workflow` | Open an active coordinator run with at least one subtask in `PendingCapacity` | Coordinator topology graph with one or more subtask nodes showing the amber **Waiting for capacity** badge. |
| 43 | `coordinator-topology-pod-chips.png` | `coordinator-orchestration.md` | `/projects/:projectId/orchestrations/:runId` | Active coordinator run with at least one dispatched subtask on a Kubernetes cluster | Coordinator topology graph showing the coordinator node with its API pod chip and one or more dispatched subtask nodes each displaying their own AgentHost pod chip. Pending/undispatched subtasks show no pod chip. |
| 39 | `watch-token-counter.png` | `experience/token-usage-monitoring.md` | `/projects/:projectId/runs/:runId/execution/:executionId` | Run in progress | Shows live token counter below the watch timeline |
| 40 | `dashboard-token-usage.png` | `experience/token-usage-monitoring.md` | `/projects/:projectId/dashboard` | Default view → **Agent and usage metrics** range | Shows shared 7d/30d/90d range filter, **Agent leaderboard** with **Cost** column, and the Token/AIC usage panel. |
| 41 | `overview-token-usage.png` | `experience/token-usage-monitoring.md` | `/overview` | Admin user | Shows **Cost overview** tile, total AICs/tokens, top-project bars, per-model breakdown, and **Usage by project** table. |
| 42 | `project-board.png` | `experience/token-usage-monitoring.md` | `/projects/:projectId/board` | Board with runs populated | Shows run cards with compact AIC/token cost chips next to status badges; open a run graph to verify the same chip on DAG nodes. |

## Count per page

| User-guide page | Screenshots |
|---|---|
| `00-overview.md` | 2 |
| `onboarding-auth.md` | 3 |
| `projects.md` | 5 |
| `runs-board-watch.md` | 5 |
| `review-workspace-merge.md` | 3 |
| `team-casting-memory.md` | 6 |
| `workflows-backlog.md` | 4 |
| `operations.md` | 6 |
| `scaling-operations.md` | 2 |
| `cluster-page.md` | 2 |
| `token-usage-monitoring.md` | 4 |
| **Total** | **42** |
