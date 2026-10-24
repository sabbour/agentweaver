# Experience overview

Use this page to understand how the Agentweaver v1 browser app is organized,
how it requests access, and what its views can show. The page describes the
checked-in web implementation; it does not confirm that a service is deployed.

## Terms used in this guide

| Term | Meaning |
| --- | --- |
| Gateway | The HTTP entry point that forwards versioned product requests to their owner services. |
| Identity Broker | The OAuth service that signs you in and issues access tokens after checking current grants. |
| Owner | The service responsible for validating an operation and storing its authoritative state. |
| Project | The workspace scope for project configuration and access. |
| Run | One execution within one project. The browser can create, start, and stop runs. It does not schedule runs. |
| Session | A node in a run's owner-reported session tree. |
| Run-bound token | A Broker token authorized for one exact project and run pair. |

The app sends sign-in and GitHub Repo App requests directly to the Identity
Broker. It sends project, run, Knowledge, and GitHub Copilot API requests to
the Gateway. The Gateway forwards each request to its owner.

During sign-in, the consent screen explains each scope requested by the
registered client and links to its documentation. See
[Identity Broker scopes](../guide/identity-scopes.md) for the scope meanings
and the authorization checks behind Agentweaver scopes.

## What you can do

1. [Sign in](./onboarding-auth.md) through the Identity Broker.
2. Start on **Overview**. It shows your projects, metrics for one project,
   recent runs, and the items that need your input.
3. [Choose or create a project](./projects.md).
4. [Edit project settings](./configuration.md), [manage skills](./skills.md),
   and [connect accounts](./connections.md).
5. [Start a session](./runs-sessions.md#start-a-new-chat) with **New session**,
   or open a run from **Sessions** or a project's **Orchestrations**.
6. [Inspect sessions and respond to requests](./runs-sessions.md).
7. [Discover memories](./knowledge.md) across a project, then manage records
   for a selected run and agent.

You don't type a run ID. The run lists read the service run list, newest
first, and load more rows with **Load more**.

## Browser routes

| Route | What you can do |
| --- | --- |
| `/` | Opens `/overview`. |
| `/overview` | Read a summary of your projects, metrics and usage for one project, **Recent runs**, and **Needs attention**. |
| `/sessions` | List runs across your projects, newest first, and start a new session. |
| `/assistant` | Start a new session: choose a project and write the first message. `/console` opens the same page. |
| `/projects` | Browse project tiles and create a project. |
| `/projects/:projectId` | Read the project **Dashboard**: setup readiness, run metrics, usage, recent runs, team, and quick actions. |
| `/projects/:projectId/orchestrations` | List this project's **Active** and **Recent** runs and open a run. |
| `/projects/:projectId/board` | Read the project backlog in **Backlog**, **Ready**, **Active**, and **Done** columns, capture a task, and edit a task's title and description. |
| `/projects/:projectId/team` | Review the project's agents, edit a charter, add a member, and export or import the team. |
| `/projects/:projectId/team/cast` | Formulate a team from a project goal, analyze repository signals, or start from a template. Review and edit the pending proposal before confirming it. |
| `/projects/:projectId/skills` | Browse, preview, import, and assign Skills. |
| `/projects/:projectId/memories` | Search, write, review, version, and transfer memories for a run and an agent. `/knowledge` opens the same page. |
| `/projects/:projectId/settings?section=<key>` | Edit project settings by section. |
| `/settings` | Sign out and manage the GitHub Repo App account. |
| `/platform-settings` | Read the status of the 16 platform component areas. Tenant and platform administrators only. |
| `/projects/:projectId/runs/:runId` | Use the **Chat**, **Topology**, **Approvals**, **Activity**, **Changes**, **Usage**, and **Run settings** tabs. |

The run page's `view` query parameter accepts `chat`, `topology`,
`approvals`, `activity`, `changes`, `usage`, or `selection` (**Run settings**).
**Chat** opens by default and for an unknown `view` value. The older
`/projects/:projectId/orchestrations/:runId` route redirects to the run route.

Each project also lists Flow, Workflows, Workspace, Observability,
Diagnostics, Heartbeat, and Cluster. Each page shows the parts that project
reads can drive, such as runs, attention items, task counts, metrics, usage,
pull requests, issues, and platform components. A **Not available in this
version** note on each page names what is still missing. `/observability`
without a project shows **Not available in this version**. The old project
sessions address opens **Sessions**. Unknown addresses show **Page not found**.
See [Web UI parity](../reference/web-ui-parity.md#endpoint-gaps) for each
missing endpoint.

The shell has a notification bell. It lists your notifications across all
projects:

- A question or approval that needs your input. Select it to open that run's
  **Approvals** tab.
- A backlog promotion, shown as **N subtasks created**. It links to the
  project Board.

Select **Dismiss** to hide a notification for you only.

On project pages, a floating **Start task** button adds a task to the
project's Backlog and opens the Board. It appears only when you can change
the project. See [Projects](./projects.md#use-the-project-board).

## Model defaults

In **Project settings > Models**, set the default model reference and
reasoning effort. If the saved default uses a credential, the form also shows
**Model credential ID** and **Model credential version**. A credential belongs
to one model. When you change the default model, the form clears the saved
credential and shows **Choose a credential for this model.** You can't save
until you enter both credential fields. To keep the saved credential, change
the model back to the saved reference. Hosted Copilot models don't use a
credential. The service still validates the complete configuration when you
save.

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
| Receipt | That the owner accepted a request. Acceptance alone does not prove message delivery or completed execution. |

After a write, the app reloads owner state. A stale request returns a conflict;
the app does not assume that the requested change succeeded.

## Source and deployment status

The route and owner names in these pages describe checked-in contracts. The
Identity Broker, Gateway, Projects & Config, Orchestrator, Events & Sessions,
and Knowledge sources include service candidates that are unpublished or
source-only. Do not treat their presence in this source tree as evidence of a
deployed service or available endpoint. See the
[architecture overview](../architecture/overview.md) and each linked service
reference for its status.

## MCP and the browser

The [MCP host](../reference/mcp-tools.md) turns nonstreaming Gateway OpenAPI
operations into `agentweaver_` tools. This includes owner operations that have
no browser control. It excludes browser connection callbacks and live SSE.

The v1 app reuses the 0.x shell, sign-in card, and shared components. It does
not reuse the 0.x page data hooks, because they call the 0.x API.

For implementation details, see the [web reference](../reference/web.md),
[Gateway guide](../guide/gateway.md), and
[architecture overview](../architecture/overview.md).
