# Runs and sessions

Use this page to start or cancel a run, inspect an existing run, respond to
owner-reported requests, and read committed activity and usage. A **session**
is a node in the run's owner-reported tree.

## Prerequisites

- Open the run once in this browser, for example from a link or a
  notification. After that, it appears in **Recent runs in this browser**.
- Sign in with a Broker token bound to that project and run.
- To create a chat, have the project Orchestrator role (`acceptRunSelection`).
  Owner or Contributor alone receives `403`. A tenant admin grants the role through
  `/api/v1/projects/{projectId}/role-assignments`.

Open the run from **Overview**, the project **Dashboard**, or
**Orchestrations**. The app signs in to the run silently. You don't type a run
ID. If the silent sign-in fails, select **Sign in for this project and run**.

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
first 80 characters of the message. Start has a 30-second deadline, including
database lock waits and downstream calls. A timeout returns `504 run_start_timeout`;
retry start with the same run ID.

## Cancel a run

Send `POST /api/v1/projects/{projectId}/runs/{runId}/cancel` with an
`Idempotency-Key` header, a token that is not bound to a run, and an optional
`reason` of up to 2,048 characters. The caller needs a current project
Contributor or Owner role, or the tenant administrator role. The caller does
not need to have started the run. A Viewer receives `403`.

**Expected result:** The response reports `cancelled` and `cancelledAt`. The
Orchestrator advances the run's execution fence, so later writes that use the
old fence fail. Active sessions become `cancelled`. A run that was created but
not started is cancelled without delivering its first message. Cancelling a
finished run returns `200` with `alreadyTerminal: true` without changing its
state or sending another event. Idempotency receipts are scoped to each caller.

## Inspect an existing run

The **Orchestration** page uses the 0.x two-pane layout. A status strip shows
the run state, the session count, the elapsed time, and the live update state
(**Live** or **Reconnecting**). **Run details** expands the owner state. The
**Run tree** pane lists the sessions; select one to open it in the session
panel. The page reads owner snapshots again five seconds after each polling
pass. Select **Refresh** for an immediate read.

| Tab | What you can read or do |
| --- | --- |
| **Chat** | Read the run's **Activity** and message the coordinator, or the session you select in the run tree. This tab opens by default. |
| **Topology** | Inspect the root and child sessions in an interactive graph. Select a node to read activity, lifecycle, blockers, and detach state. |
| **Approvals** | Answer questions and approve or reject pending outcome, plan, or step approvals. The tab label shows the number of pending decisions. |
| **Activity** | Read committed events, execution state, lifecycle, and blockers. |
| **Changes** | Manage GitHub App repository access for the run. If the project doesn't use a GitHub App, the tab shows **No repository changes**. |
| **Usage** | Read owner usage and accounting totals. Missing or unpriced usage is not a zero-cost measurement. |
| **Run settings** | Read the model, the revisions the run started with, and the platform components it can use. |

The `view` query selects `chat`, `topology`, `approvals`, `activity`,
`changes`, `usage`, or `selection` (**Run settings**). An invalid value
selects `topology`.

**Run settings** shows the settings the run started with. Changing project
settings doesn't change them. It lists the model reference, the project
configuration revision, and the platform defaults revision. The table
**Allowed implementations for this run** lists each platform component, its
choice (one, an ordered list, or several), and its implementations in order.
The table shows allowed implementations, not the implementation that is
running. Some service candidates in the repository are source-only or
unpublished; see the [provider and model reference](../architecture/providers-models.md).

In **Changes**, use **GitHub App repository access** to
[pin a repository to the run](./connections.md#pin-a-repository-to-an-existing-run).

## Send a message

**Prerequisites:** The run tree contains at least two active sessions.

1. Open **Chat**. **Message the coordinator** is selected by default.
2. To message another session, select it in the **Run tree**.
3. Enter a message.
4. Select **Send**, or press Ctrl+Enter.

The service accepts messages only between sessions of the run. The app sends
the message from another active session: the coordinator, or the first active
session when you message the coordinator. Direct messages from you to the
coordinator need a service API that doesn't exist yet.

**Expected result:** The page shows **Message accepted.** and lists the
message under the composer. The list is local to the page; reloading clears
it. The receipt does not prove delivery or completion. If a send fails and you
retry the same text, the app reuses the same request, so the message isn't
sent twice. The journal contains typed events and opaque object references;
the page has no message-content reader.

You can't start a new chat or run from the browser yet. That needs a service
API that doesn't exist yet (gap G1).

## Answer a gate

1. Open **Approvals**.
2. Read the prompt, session, and request ID.
3. Select an offered answer, or enter text in **Your answer** when the request
   allows it.
4. Select **Send answer**, or approve or reject the request.
5. Read the refreshed owner snapshot.

The blocker and decision snapshot must report the same pending request ID. The
action includes that ID and the expected decision-state version. When the
snapshots don't match, the app disables the action and shows **This request is
no longer the pending one.** A `409` response causes the app to reload owner
state without assuming that the decision was applied.

You can also open a pending request from the notification bell or from
**Needs attention** on **Overview**. Both list requests from the run you have
open.

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
It deduplicates events by event ID and orders them by owner position. When the
stream closes or a retryable failure occurs, it reconnects from its cursor with
backoff, and the status strip shows **Reconnecting**. The page shows an error
only after three failures in a row. Scope mismatches and nonretryable client
errors stop the stream and show an error at once.

The **Usage** tab preserves missing measurements as **not reported**. A
partially priced usage summary does not mean that unpriced usage cost zero.

## Understand errors

- Snapshot errors appear on the affected area, such as run, run settings,
  usage, or session. A failed topology read does not fabricate a tree.
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
