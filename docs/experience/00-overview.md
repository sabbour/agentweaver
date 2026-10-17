# Experience overview

Agentweaver v1 has a browser app for people and an MCP host for clients that call
tools. Both use the Gateway. Owning services validate access and store state.

## What you can do

1. [Sign in](./onboarding-auth.md) through the Identity Broker.
2. [Choose or create a project](./projects.md).
3. [Edit configuration](./configuration.md), [manage skills](./skills.md), and
   [connect accounts](./connections.md).
4. Open an existing run by its exact ID.
5. [Inspect sessions and respond to requests](./runs-sessions.md).
6. [Review Knowledge](./knowledge.md) for an exact project, run, and agent.

The browser does not provide a run creation or scheduling form. An existing run
needs accepted owner state before the run page can display it.

## Navigation

| Location | What you see |
| --- | --- |
| `/projects` | Visible project list and project creation form. |
| `/projects/:projectId` | Project lifecycle, record revision, configuration revision, and an existing-run opener. |
| `/projects/:projectId/settings` | Configuration JSON, account connections, and Skills and marketplace. |
| `/projects/:projectId/knowledge` | Exact-scope record search, writes, review, revision history, and transfer. |
| `/projects/:projectId/runs/:runId` | Topology, Chat, Outcomes & approvals, Activity, Selection, and Usage. |

The shell shows **Run chat** only as an enabled control when the current Broker
session has an exact project/run binding. The older
`/projects/:projectId/orchestrations/:runId` route redirects to the run route.

## Access follows the operation

Project navigation and configuration use ordinary Broker authorization. Run and
Knowledge views request a token bound to the exact project and run. The app reads
current tenant context from `GET /api/v1/authorization/context`; it does not
derive a tenant from an ID in the URL.

Project creation does not assign an Owner. A tenant administrator grants access
through the owner authority. Browser buttons do not grant permissions.

## Read state, then act

The run view reads owner snapshots and the committed event journal. These serve
different purposes:

- **Snapshots** report current execution, lifecycle, blockers, and decision versions.
- **Journal events** report ordered, persisted activity.
- **Receipts** identify accepted requests. Acceptance alone does not report
  message delivery or completed execution.

After a write, the app reloads owner state. A stale request returns a conflict
instead of changing the visible state optimistically.

## MCP and the browser

The [MCP host](../reference/mcp-tools.md) turns nonstreaming Gateway OpenAPI
operations into `agentweaver_` tools. This includes owner operations that have no
browser control. It excludes browser connection callbacks and live SSE.

Do not use the retained 0.x files under `apps/web/src/pages` as the v1 navigation
map. The stable app entry exports `apps/web/src/v1/App.tsx`.

For implementation details, see the [web reference](../reference/web.md),
[Gateway guide](../guide/gateway.md), and
[architecture overview](../architecture/overview.md).
