# Runs and sessions

Open `/projects/:projectId/runs/:runId` for an existing run, then select
**Sign in for this project and run**. The page requires an exact Broker binding.
It does not create or schedule runs.

## Inspect a run

The **Authoritative run snapshot** shows owner state. The app refreshes snapshots
five seconds after each polling pass. Select **Refresh owner snapshots** for an
immediate read.

| Tab | What you see and do |
| --- | --- |
| **Topology** | Inspect the root and children in an interactive graph. Select a node to read activity, lifecycle, blockers, and detach state. |
| **Chat** | Read journal summaries and send an addressed message between two existing active sessions. |
| **Outcomes & approvals** | Answer questions and approve or reject exact pending outcome, plan, or approval gates. |
| **Activity** | Follow committed events, execution state, lifecycle, and blockers. |
| **Selection** | Read accepted configuration and provider selections. Manage the [run repository pin](./connections.md#pin-a-repository-for-an-existing-run) for GitHub App mode. |
| **Usage** | Read owner usage and accounting totals. Missing or unpriced usage is not a zero-cost measurement. |

The `view` query selects `topology`, `chat`, `approvals`, `activity`, `selection`,
or `usage`. Invalid values normalize to `topology`.

## Send a message

1. Open **Chat**.
2. Choose different **From session** and **To session** values.
3. Enter a message.
4. Select **Send immediately**.

At least two active sessions are required. The owner returns a message ID and
status. The displayed text is a browser-local echo, not a restored transcript.
The journal contains typed events and opaque object references; this page has
no object-content reader.

## Answer a gate

1. Open **Outcomes & approvals**.
2. Read the blocker prompt and request ID.
3. Choose an advertised answer, or enter freeform text when allowed.
4. Submit the answer, approval, or rejection.
5. Read the refreshed owner snapshot.

The blocker and decision snapshot must contain the same pending request ID.
Actions include that ID and the expected decision-state version. A missing match
disables the action. A `409` reloads owner state without assuming the decision
was applied.

## Detach or archive a child

In **Topology**, select an attached child:

- **Detach child** appears for an active child.
- **Archive completed child** appears when both its lifecycle and run execution
  are completed.

The owner validates the current execution fence. The app reloads state after the
request. These controls do not stop a pod or infer AgentHost health.

## States and errors

- Snapshot failures appear by surface, such as run, selection, usage, or session.
  A failed topology read does not fabricate a tree.
- An empty tree or empty pending-request list reflects the owner response.
- Activity shows loading, live, or reconnecting journal state.
- Replay loads all available pages before live streaming. Events are deduplicated
  by event ID and sorted by owner position.
- A live stream reconnects from its cursor after retryable failures. Scope
  mismatches and nonretryable client errors stop it.
- Requests for another project, run, or session are rejected by the client.

## Requests

All paths use the Gateway. In this table, the common prefix is
`/api/v1/projects/{projectId}/runs/{runId}`.

| Step | Method and path after the prefix | Owner |
| --- | --- | --- |
| Run snapshot | `GET /coordination/status` | Orchestrator |
| Session tree | `GET /coordination/sessions/{sessionId}/tree` | Orchestrator |
| Session status | `GET /coordination/sessions/{sessionId}/status` | Orchestrator |
| Decision state | `GET /coordination/sessions/{sessionId}/decisions` | Orchestrator |
| Send message | `POST /coordination/sessions/{sessionId}/messages` | Orchestrator |
| Answer question | `POST /coordination/sessions/{sessionId}/decisions/gates/{requestId}/answer` | Orchestrator |
| Approve or reject | `POST /coordination/sessions/{sessionId}/decisions/gates/{requestId}/approve` or `/reject` | Orchestrator |
| Detach | `POST /coordination/sessions/{sessionId}/detach` | Orchestrator |
| Archive | `POST /coordination/sessions/{parentSessionId}/children/{childSessionId}/archive` | Orchestrator |
| Accepted selection | `GET /selection` | Projects & Config |
| Usage | `GET /usage` | Events & Sessions |
| Replay | `GET /events?cursor=...&limit=100` | Events & Sessions |
| Follow live | `GET /events/live?cursor=...` | Gateway SSE over Events replay |

See [Events reference](../reference/events.md) for ordering and replay, and
[Events & Sessions architecture](../architecture/events-sessions.md) for owner
and runtime boundaries.
