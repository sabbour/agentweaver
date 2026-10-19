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
| Run | An existing execution identified within one project. The browser does not create or schedule runs. |
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
2. [Choose or create a project](./projects.md).
3. [Edit project configuration](./configuration.md), [manage skills](./skills.md),
   and [connect accounts](./connections.md).
4. Open a run by its exact ID.
5. [Inspect sessions and respond to requests](./runs-sessions.md).
6. [Review Knowledge](./knowledge.md) for an exact project, run, and agent.

The browser has no run-creation or scheduling form. It needs accepted owner
state for an existing run before it can display that run.

## Browser routes

| Route | What you can do |
| --- | --- |
| `/projects` | List projects visible to your current identity and create a project. |
| `/projects/:projectId` | Read project lifecycle and revision details, then open an existing run by ID. |
| `/projects/:projectId/settings` | Edit configuration, manage account connections, and use Skills and marketplace. |
| `/projects/:projectId/knowledge` | Search, write, review, version, and transfer exact-scope records. |
| `/projects/:projectId/runs/:runId` | Inspect Topology, Chat, Outcomes & approvals, Activity, Selection, and Usage. |

The run page's `view` query parameter accepts `topology`, `chat`, `approvals`,
`activity`, `selection`, or `usage`. An invalid value selects `topology`. The
older `/projects/:projectId/orchestrations/:runId` route redirects to the run
route.

The shell enables **Run chat** only when your current Broker session is bound
to an exact project and run. Otherwise, the control is disabled.

## Access follows the operation

Project navigation and configuration use the current Broker session. Run and
Knowledge views request a token bound to the exact project and run. The app
reads current tenant context from `GET /api/v1/authorization/context`; it does
not infer a tenant from an ID in the browser URL.

Creating a project does not assign an Owner. A tenant administrator grants
project access through the owner authority. Browser controls do not grant
permissions.

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

The active app entry exports `apps/web/src/v1/App.tsx`. Retained 0.x files
under `apps/web/src/pages` do not define v1 navigation.

For implementation details, see the [web reference](../reference/web.md),
[Gateway guide](../guide/gateway.md), and
[architecture overview](../architecture/overview.md).
