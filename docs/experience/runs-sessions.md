# Runs and sessions

Use this page to inspect an existing run, respond to owner-reported requests,
and read committed activity and usage. A **session** is a node in the run's
owner-reported tree. The browser does not create or schedule runs.

## Prerequisites

- Open the run once in this browser, for example from a link or a
  notification. After that, it appears in **Recent runs in this browser**.
- Sign in with a Broker token bound to that project and run.

Open the run from **Overview**, the project **Dashboard**, or
**Orchestrations**. If the page asks, select
**Sign in for this project and run**. You don't type a run ID.

## Inspect an existing run

The run page shows the run state badge and **Run details** with the
Orchestrator's current owner state. The page reads owner snapshots again five
seconds after each polling pass. Select **Refresh** for an immediate read.

| Tab | What you can read or do |
| --- | --- |
| **Chat** | Read the run's **Activity** and send a message between two active sessions. This tab opens by default. |
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

**Prerequisites:** The session tree contains at least two active sessions.

1. Open **Chat**.
2. Under **Send a message**, choose different **From** and **To** sessions.
3. Enter a message.
4. Select **Send**, or press Ctrl+Enter.

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
It deduplicates events by event ID and orders them by owner position. The stream
reconnects from its cursor after retryable failures. Scope mismatches and
nonretryable client errors stop the stream.

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
`/api/v1/projects/{projectId}/runs/{runId}`.

| Operation | Method and path after the prefix | Owner in source |
| --- | --- | --- |
| Run snapshot | `GET /coordination/status` | Orchestrator |
| Session tree | `GET /coordination/sessions/{sessionId}/tree` | Orchestrator |
| Session status | `GET /coordination/sessions/{sessionId}/status` | Orchestrator |
| Decision state | `GET /coordination/sessions/{sessionId}/decisions` | Orchestrator |
| Send message | `POST /coordination/sessions/{sessionId}/messages` | Orchestrator |
| Answer question | `POST /coordination/sessions/{sessionId}/decisions/gates/{requestId}/answer` | Orchestrator |
| Approve or reject | `POST /coordination/sessions/{sessionId}/decisions/gates/{requestId}/approve` or `/reject` | Orchestrator |
| Detach | `POST /coordination/sessions/{sessionId}/detach` | Orchestrator |
| Archive child | `POST /coordination/sessions/{parentSessionId}/children/{childSessionId}/archive` | Orchestrator |
| Read accepted selection | `GET /selection` | Projects & Config |
| Read usage | `GET /usage` | Events & Sessions |
| Replay events | `GET /events?cursor=...&limit=100` | Events & Sessions |
| Follow live events | `GET /events/live?cursor=...` | Gateway SSE over Events replay |

The Orchestrator, Events & Sessions, and Projects & Config entries identify
owners in checked-in source. These include source-only or unpublished service
candidates, not confirmed deployments. See the [Events reference](../reference/events.md)
for ordering and replay, and [Events & Sessions architecture](../architecture/events-sessions.md)
for owner and runtime boundaries.
