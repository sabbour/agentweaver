<a id="experience-overview"></a>

# What the web app can do

Use this page to choose a browser task and understand how the checked-in
Agentweaver web app reads and changes project data. You need access to a
configured deployment to use the app. A route or component in source does not
confirm that a service is available in your environment.

## Terms used in this guide

| Term | Meaning |
| --- | --- |
| Gateway | The HTTP entry point for versioned product API requests. |
| Identity Broker | The OAuth service that signs you in and issues tokens after checking current grants. |
| Owner | The service that validates an operation and stores its authoritative state. |
| Project | The workspace scope for project configuration and access. |
| Run | One execution within one project. The browser can create, start, and stop runs. It does not schedule runs. |
| Session | A node in a run's owner-reported session tree. |
| Run-bound token | A Broker token authorized for one exact project and run pair. |

The browser sends sign-in and GitHub Repo App requests to the Identity Broker.
It sends project, run, Knowledge, and GitHub Copilot API requests to the
Gateway. The Gateway forwards each request to its owner.

## What you can do

1. [Sign in](./onboarding-auth.md) through the Identity Broker.
2. Read **Overview** for your projects, recent runs, metrics, and items that
   need your input.
3. [Choose or create a project](./projects.md).
4. [Edit project settings](./configuration.md), [manage Skills](./skills.md),
   and [connect accounts](./connections.md).
5. [Start a session](./runs-sessions.md#start-a-new-chat) with **New session**,
   or open a run from **Sessions** or a project's **Orchestrations** page.
6. [Inspect sessions and respond to requests](./runs-sessions.md).
7. [Find Memories](./knowledge.md) across a project, then manage records for
   a selected run and agent.

You do not type a run ID. The run lists read the service run list, newest
first, and load more rows with **Load more**.

## Browser routes

| Route | What you can do |
| --- | --- |
| `/` | Opens `/overview`. |
| `/overview` | Read project metrics, recent runs, and items that need your input. |
| `/sessions` | List runs across projects you can read and start a new session. |
| `/assistant` | Start a session by choosing a project and writing the first message. `/console` opens the same page. |
| `/projects` | Browse project tiles and create a project. |
| `/projects/:projectId` | Read the project Dashboard, including readiness, run metrics, usage, recent runs, team, and quick actions. |
| `/projects/:projectId/orchestrations` | List the project's Active and Recent runs and open a run. |
| `/projects/:projectId/board` | Read tasks in Backlog, Ready, Active, and Done; capture and edit tasks; change queue order; and send eligible backlog tasks to Ready. |
| `/projects/:projectId/team` | Review agents, edit a charter, add a member, and export or import the team. |
| `/projects/:projectId/team/cast` | Formulate a team from a project goal, analyze repository signals, or start from a template. Review and edit the proposal before confirming it. |
| `/projects/:projectId/skills` | Browse, preview, import, and assign Skills. |
| `/projects/:projectId/knowledge` and `/projects/:projectId/memories` | Search, write, review, version, and transfer Memories for a run and an agent. |
| `/projects/:projectId/settings?section=<key>` | Edit project settings by section. |
| `/settings` | Sign out and manage the GitHub Repo App account. |
| `/platform-settings` | Read the status of the 16 platform component areas. Access is limited to tenant and platform administrators. |
| `/projects/:projectId/runs/:runId` | Use the Chat, Topology, Approvals, Activity, Changes, Usage, and Run settings tabs. |

The run page's `view` query accepts `chat`, `topology`, `approvals`,
`activity`, `changes`, `usage`, or `selection` (**Run settings**). **Chat**
opens by default; an unknown value also opens **Chat**. The older
`/projects/:projectId/orchestrations/:runId` address redirects to the run
route.

Project pages also include Flow, Workflows, Workspace, Observability,
Diagnostics, Heartbeat, and Cluster. Some views report that data or
functionality is not available in this version. `/observability` without a
project shows that a cross-project summary is not available. The old project
sessions address redirects to **Sessions**. Unknown addresses show **Page
not found**. See [Web UI parity](../reference/web-ui-parity.md) for the
current view limits.

The notification bell lists notifications across projects. Select a question
or approval to open its run's **Approvals** tab. A backlog-promotion
notification links to the project Board. **Dismiss** hides a notification
for you; it does not answer a request.

On project pages, **Start task** adds a task to the project's Backlog and
opens the Board. It appears only when you can change the project. It does not
start a run. See [Projects](./projects.md#use-the-project-board).

## Model defaults

The **Models** section of Project settings controls the default model and
task-specific models. Credential fields, model eligibility, and reasoning
choices depend on the current owner-provided choices. For the complete
behavior and save results, see [Project configuration](./configuration.md#change-the-default-model).

## Access follows the operation

Project navigation and configuration use the current Broker session.
Project-wide Knowledge search requests a token that is not bound to a run.
Run-specific Knowledge changes request a token bound to the exact project and
run. The app reads current tenant context from
`GET /api/v1/authorization/context`; it does not infer a tenant from an ID in
the browser URL.

Creating a project does not make you its owner. A tenant administrator grants
project access. In **Project settings > Access**, a tenant administrator can
add and remove project members when the service supports role changes.

## Read state before you act

The run page reads owner snapshots and the committed event journal. These
responses answer different questions:

| Response | What it tells you |
| --- | --- |
| Snapshot | Current execution, lifecycle, blockers, and decision versions. |
| Journal event | Persisted activity in owner order. |
| Receipt | The owner accepted a request. This does not mean a message was delivered or execution finished. |

After a write, the app reloads owner state. A stale request returns a conflict;
the app does not assume that the requested change succeeded.

## Source and deployment status

These pages describe checked-in source behavior, not service availability in
your environment. The supported client paths are Gateway/API, browser, and
the native .NET CLI. The checked-in release/Kustomize composition also
includes MCP compatibility image, workload, and ingress resources. Their
presence does not make MCP a supported product or verification path.
Outbound Remote MCP connections are separate. See the [architecture
overview](../architecture/overview.md) for component boundaries.

## MCP and the browser

Agentweaver does not provide a supported product MCP endpoint. Do not use
`/mcp` as a product entry point. The browser's outbound Remote MCP connection
configuration is separate and does not change this boundary.

For browser configuration, see the [Web integration reference](../reference/web.md)
and [Gateway guide](../guide/gateway.md).
