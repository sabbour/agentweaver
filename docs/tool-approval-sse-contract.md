# Tool Approval SSE Contract (issue #174)

This document describes the backend changes introduced to fix the 409 "No pending approval
found" bug. Trinity (frontend) must consume these events to keep approval cards in sync with
server state.

---

## Root causes fixed

1. **Primary — silent expiry.** `WaitForApprovalAsync` appended `RequestResolved(false)` when its
   5-minute timeout elapsed but emitted no SSE event. The UI kept showing live buttons. Any click
   after timeout returned 409. Fix: the gate now emits `tool.approval_resolved` (with
   `expired: true`) on the child run's stream whenever a timeout fires.

2. **Explicit API state.** The endpoint now distinguishes resolved/expired requests from unknown
   request IDs. A second click or late click returns HTTP 200 with `resolved: true`,
   `state: "approved" | "denied" | "expired"`, and `expired`; a wrong run id or bad request id
   returns HTTP 404 with `state: "unknown"`.

---

## New SSE events

### `tool.approval_resolved`

Emitted on the **child subtask run's own event stream** when a HITL approval request is resolved
by any means: operator grant, operator deny, or server-side timeout expiry.

**Payload:**
```json
{
  "requestId": "a1b2c3d4e5f6...",
  "runId": "<child-subtask-run-id>",
  "approved": false,
  "expired": true
}
```

| Field | Type | Description |
|---|---|---|
| `requestId` | string | Exact request_id from the `tool.approval_required` card |
| `runId` | string | The child run id (same stream the event is on) |
| `approved` | bool | `true` if an operator granted; `false` for deny or timeout |
| `expired` | bool | `true` only when the server timeout fired (no operator action taken) |

**Action:** On receipt, the frontend must disable and remove the approval card with this
`requestId`. Do not show the card or allow any further interaction.

---

### `coordinator.child_approval_resolved`

Emitted on the **coordinator run's event stream** when a child run's approval is resolved.
This mirrors the child's `tool.approval_resolved` so coordinator-stream consumers also update.

**Payload:**
```json
{
  "childRunId": "<child-subtask-run-id>",
  "subtaskId": 3,
  "requestId": "a1b2c3d4e5f6...",
  "approved": false,
  "expired": true
}
```

---

## Posting approvals — which run id to use

**Always POST to the child subtask run id** — not the coordinator run id.

The `coordinator.child_approval_required` event (emitted on the coordinator stream) carries:
```json
{
  "childRunId": "<this is the id to POST to>",
  "subtaskId": 3,
  "requestId": "a1b2c3d4e5f6...",
  "toolName": "web_fetch",
  "url": "https://api.github.com/search/issues?..."
}
```

Endpoints:
```
POST /api/runs/{childRunId}/tool-approvals   { "request_id": "...", "scope": "once|run|tool|always" }
POST /api/runs/{childRunId}/tool-denials     { "request_id": "..." }
```

The `request_id` must match exactly what the `tool.approval_required` (and
`coordinator.child_approval_required`) events carry. It is a full UUID — do not truncate it.

---

## Existing event: `tool.approval_required`

Emitted on the child run's stream when the HITL gate arms. Already consumed by the frontend.
Payload is unchanged:
```json
{
  "requestId": "...",
  "displayId": "a1b2c3d4",
  "toolName": "web_fetch",
  "url": "https://...",
  "intention": "...",
  "message": "The agent wants to fetch a URL. Operator approval required."
}
```

---

## Frontend card lifecycle

```
tool.approval_required  →  show card with live buttons
       (5-minute server-side timeout running)
tool.approval_resolved  →  disable / remove card
   (arrived before operator acts: expired=true)
   (arrived after operator acts: approved=true or approved=false, expired=false)
```

If the client receives a resolved response from `POST /tool-approvals` or `POST /tool-denials`
with `state: "approved" | "denied" | "expired"`, it means a `tool.approval_resolved` event is
in-flight or was missed on reconnect. The card should be hidden. HTTP 404 with `state: "unknown"`
means the client posted to the wrong run id or used a request id that was never registered there.
