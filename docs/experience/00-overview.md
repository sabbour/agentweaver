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
| Run | An existing execution within one project. The browser does not create or schedule runs yet. |
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
2. Start on **Overview**. It shows your projects, the runs you opened in this
   browser, and the items that need your input.
3. [Choose or create a project](./projects.md).
4. [Edit project settings](./configuration.md), [manage skills](./skills.md),
   and [connect accounts](./connections.md).
5. Open a run from **Recent runs in this browser**.
6. [Inspect sessions and respond to requests](./runs-sessions.md).
7. [Review memories](./knowledge.md) for a run and an agent.

You don't type a run ID. A run appears in **Recent runs in this browser** after
you open it once in this browser, for example from a link or a notification.
The browser can't start a new chat or run, because the service has no API to
start one. The browser also can't list every run in a project, because the
service has no run list API. Use **Recent runs in this browser** instead.

## Browser routes

| Route | What you can do |
| --- | --- |
| `/` | Opens `/overview`. |
| `/overview` | Read a summary of your projects, **Recent runs in this browser**, and **Needs attention**. |
| `/projects` | Browse project tiles and create a project. |
| `/projects/:projectId` | Read the project **Dashboard**: setup readiness, recent runs, team, and quick actions. |
| `/projects/:projectId/orchestrations` | Read this project's runs from this browser, grouped by state, and open a run. |
| `/projects/:projectId/team` | Review the project's agents and edit an agent's charter. |
| `/projects/:projectId/skills` | Browse, preview, import, and assign Skills. |
| `/projects/:projectId/memories` | Search, write, review, version, and transfer memories for a run and an agent. `/knowledge` opens the same page. |
| `/projects/:projectId/settings?section=<key>` | Edit project settings by section. |
| `/settings` | Sign out and manage the GitHub Repo App account. |
| `/projects/:projectId/runs/:runId` | Use the **Chat**, **Topology**, **Approvals**, **Activity**, **Changes**, **Usage**, and **Run settings** tabs. |

The run page's `view` query parameter accepts `chat`, `topology`,
`approvals`, `activity`, `changes`, `usage`, or `selection` (**Run settings**).
**Chat** opens by default and for an unknown `view` value. The older
`/projects/:projectId/orchestrations/:runId` route redirects to the run route.

Navigation hides 0.x destinations that have no v1 API: Board, Flow,
Workspace, Workflows, Observability, Diagnostics, Heartbeat, Cluster,
Assistant, Platform settings, and Sessions. Their old addresses open one
**Not available in this version** page that explains why and links back.
Unknown addresses show **Page not found**.

The shell has a notification bell. It lists the questions and approvals that
need your input in the run you have open. Select an item to open that run's
**Approvals** tab.

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

Project navigation and configuration use the current Broker session. Run and
Knowledge views request a token bound to the exact project and run. The app
reads current tenant context from `GET /api/v1/authorization/context`; it does
not infer a tenant from an ID in the browser URL.

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
