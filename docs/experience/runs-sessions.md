# Runs and sessions

Use this page to start or cancel a run, inspect an existing run, respond to
owner-reported requests, and read committed activity and usage. A **session**
is a node in the run's owner-reported tree.

## Prerequisites

- Know the exact project ID, and the run ID for an existing run.
- Have the Orchestrator role on the project. A tenant admin grants it through
  `/api/v1/projects/{projectId}/role-assignments`.
- Sign in with a Broker token that requests `api.read` and
  `projects.orchestrator`. Start, cancel, and run pages need a token bound to
  that exact project and run.

Open `/projects/:projectId/runs/:runId`, then select
**Sign in for this project and run** if the page requests it.

## Start a new chat

A new chat creates a run with a saved first message. Starting it is a
two-step flow, because the run ID does not exist until create returns.

1. Send `POST /api/v1/projects/{projectId}/runs` with an `Idempotency-Key`
   header and a token that is not bound to a run. The body has `firstMessage`,
   `expectedProjectConfigRevision`, and optional `title` and `workflowId`.
2. Read `runId`, `rootSessionId`, and `binding` from the `201` response. The
   run state is `awaiting_binding`.
3. Persist the pending `runId` and `rootSessionId` across the redirect. Use a
   top-level PKCE authorize redirect with `prompt=none`, `project_id` set to
   `binding.projectId`, and `run_id` set to `binding.runId`. With an existing
   Broker session and consent for the same scopes, this returns a code without
   UI. Exchange it for a run-bound token. On `login_required` or
   `consent_required`, fall back to interactive sign-in instead of retrying
   silently.
4. Send `POST /api/v1/projects/{projectId}/runs/{runId}/start` with no body.

**Expected result:** Start returns `started`. The Orchestrator accepts the root
session and delivers the saved first message once, using the idempotency key
`{runId}:first`. Retrying create with the same key returns the same run.
Retrying start returns the same result without sending the message again.

The first message is limited to 32,768 UTF-8 bytes. The default title is the
first 80 characters of the message.

## Cancel a run

Send `POST /api/v1/projects/{projectId}/runs/{runId}/cancel` with an
`Idempotency-Key` header, a run-bound token, and an optional `reason` of up to
2,048 characters.

**Expected result:** The response reports `cancelled` and `cancelledAt`. The
Orchestrator advances the run's execution fence, so later writes that use the
old fence fail. Active sessions become `cancelled`. A run that was created but
not started is cancelled without delivering its first message. Cancelling a
finished run returns `409 run_terminal`.

## Inspect an existing run

The **Authoritative run snapshot** shows the Orchestrator's current owner
state. The page refreshes owner snapshots every five seconds after each
polling pass. Select **Refresh owner snapshots** for an immediate read.

| Tab | What you can read or do |
| --- | --- |
| **Topology** | Inspect the root and child sessions in an interactive graph. Select a node to read activity, lifecycle, blockers, and detach state. |
| **Chat** | Read journal summaries and send an addressed message between two existing active sessions. |
| **Outcomes & approvals** | Answer questions and approve or reject exact pending outcome, plan, or approval gates. |
| **Activity** | Read committed events, execution state, lifecycle, and blockers. |
| **Selection** | Read the accepted configuration and provider selection. Manage the [run repository pin](./connections.md#pin-a-repository-to-an-existing-run) when the run uses GitHub App mode. |
| **Usage** | Read owner usage and accounting totals. Missing or unpriced usage is not a zero-cost measurement. |

The `view` query selects `topology`, `chat`, `approvals`, `activity`,
`selection`, or `usage`. An invalid value normalizes to `topology`.

The Selection tab shows the immutable accepted selection and advertised
provider candidates. It does not report a runtime model ID or a provisioned
provider pin. A candidate is metadata, not proof of a provisioned resource or
provider binding. Some service candidates in the repository are source-only or
unpublished; see the [provider and model reference](../architecture/providers-models.md).

## Send a message

**Prerequisites:** The session tree contains at least two active sessions.

1. Open **Chat**.
2. Choose different **From session** and **To session** values.
3. Enter a message.
4. Select **Send immediately**.

**Expected result:** The owner returns a message ID and status. The app shows
the message text as a browser-local echo after it receives the receipt. The
receipt does not prove delivery or completion, and the page does not restore a
transcript. The journal contains typed events and opaque object references; the
page has no object-content reader.

## Answer a gate

1. Open **Outcomes & approvals**.
2. Read the blocker prompt and exact request ID.
3. Select an advertised answer, or enter freeform text when the blocker allows it.
4. Submit the answer, approval, or rejection.
5. Read the refreshed owner snapshot.

The blocker and decision snapshot must report the same pending request ID. The
action includes that ID and the expected decision-state version. The app
disables an action when the snapshots do not match. A `409` response causes
the app to reload owner state without assuming that the decision was applied.

**Expected result:** The owner reports whether it accepted the request. A
refreshed snapshot, not the receipt, shows the resulting gate state.

## Detach or archive a child session

In **Topology**, select an attached child:

- **Detach child** appears for an active child.
- **Archive completed child** appears when both the child lifecycle and run
  execution are completed.

The owner validates the current execution fence. After the request, the app
reloads state. The refreshed snapshot shows the resulting lifecycle when the
owner reports it. These controls do not stop a pod or infer AgentHost health.

## Read activity and usage

The app replays available journal pages before following the live event stream.
It deduplicates events by event ID and orders them by owner position. The stream
reconnects from its cursor after retryable failures. Scope mismatches and
nonretryable client errors stop the stream.

The **Usage** tab preserves missing measurements as **not reported**. A
partially priced usage summary does not mean that unpriced usage cost zero.

## Understand errors

- Snapshot errors appear on the affected surface, such as run, selection, usage,
  or session. A failed topology read does not fabricate a tree.
- An empty tree or pending-request list reflects the owner response.
- Activity displays loading, live, or reconnecting journal state.
- The app rejects responses for a different project, run, or session.

## Requests

All versioned routes use the Gateway. The common prefix is
`/api/v1/projects/{projectId}/runs`. Create uses the prefix alone.

| Operation | Method and path after the prefix | Owner in source |
| --- | --- | --- |
| Create run | `POST /` (empty suffix) | Orchestrator |
| Start run | `POST /{runId}/start` | Orchestrator |
| Cancel run | `POST /{runId}/cancel` | Orchestrator |
| Run snapshot | `GET /{runId}/coordination/status` | Orchestrator |
| Session tree | `GET /{runId}/coordination/sessions/{sessionId}/tree` | Orchestrator |
| Session status | `GET /{runId}/coordination/sessions/{sessionId}/status` | Orchestrator |
| Decision state | `GET /{runId}/coordination/sessions/{sessionId}/decisions` | Orchestrator |
| Send message | `POST /{runId}/coordination/sessions/{sessionId}/messages` | Orchestrator |
| Answer question | `POST /{runId}/coordination/sessions/{sessionId}/decisions/gates/{requestId}/answer` | Orchestrator |
| Approve or reject | `POST /{runId}/coordination/sessions/{sessionId}/decisions/gates/{requestId}/approve` or `/reject` | Orchestrator |
| Detach | `POST /{runId}/coordination/sessions/{sessionId}/detach` | Orchestrator |
| Archive child | `POST /{runId}/coordination/sessions/{parentSessionId}/children/{childSessionId}/archive` | Orchestrator |
| Read accepted selection | `GET /{runId}/selection` | Projects & Config |
| Read usage | `GET /{runId}/usage` | Events & Sessions |
| Replay events | `GET /{runId}/events?cursor=...&limit=100` | Events & Sessions |
| Follow live events | `GET /{runId}/events/live?cursor=...` | Gateway SSE over Events replay |

The Orchestrator, Events & Sessions, and Projects & Config entries identify
owners in checked-in source. These include source-only or unpublished service
candidates, not confirmed deployments. See the [Events reference](../reference/events.md)
for ordering and replay, and [Events & Sessions architecture](../architecture/events-sessions.md)
for owner and runtime boundaries.
