# Tool Approval SSE Contract (issues #174 and #196)

This document describes the backend contract for resolving tool approvals and keeping approval
cards in sync with server state.

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

3. **Pod-local approval return path (#196).** In `pod-per-run` mode, the pending approval is held by
   the AgentHost pod's in-memory `IToolApprovalGate`, not the API process's
   `DurableToolApprovalGate`. If the durable gate reports `Unknown`, the API forwards the decision
   to the owning pod's authenticated `POST /tool-approvals` or `POST /tool-denials` endpoint. A
   terminal pod response is returned as HTTP 200 and the API emits `tool.approval_resolved`;
   an unreachable AgentHost returns HTTP 503 with `state: "agenthost_unreachable"`.

4. **Coordinator-to-child routing (#196).** A request posted to a coordinator run is resolved to
   the child run that raised it. The API first checks the child gates, then scans persisted
   `coordinator.child_approval_required` events for the matching `requestId` and `childRunId`.

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

POSTing the **child subtask run id** remains the most direct form. A client operating from the
coordinator view may instead POST the coordinator run id; the API resolves the owning child from
the matching persisted `coordinator.child_approval_required` event and returns that child id as
`run_id`.

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
POST /api/runs/{runId}/tool-approvals   { "request_id": "...", "scope": "once|run|tool|always" }
POST /api/runs/{runId}/tool-denials     { "request_id": "..." }
```

The `request_id` must match exactly what the `tool.approval_required` (and
`coordinator.child_approval_required`) events carry. It is a full UUID — do not truncate it.

---

## Pod-per-run forwarding

The public API routes and request bodies are unchanged for callers. Internally, when the resolved
child run is `pod-per-run` and the API-side durable gate returns `Unknown`:

1. The API resolves the AgentHost origin with `IAgentHostOriginResolver`.
2. It reads the per-run credential using `PreviewRunnerCredential.SecretKey(runId)`.
3. `AgentHostApprovalHttpClient` sends the decision through the `a2a-sandbox-pod` HTTP client to
   the pod-root `/tool-approvals` or `/tool-denials` route.
4. AgentHost authenticates the bearer and resolves its in-memory `IToolApprovalGate`.
5. A terminal result causes the API to emit `tool.approval_resolved` on the owning child run.

Forwarded outcome states:

| HTTP | `state` | Meaning |
|---|---|---|
| `200` | `approved`, `denied`, or `expired` | Terminal decision; `resolved: true` |
| `404` | `unknown` | Neither the API nor the owning pod knows the request |
| `409` | `pending` | The request remains pending and should be retried |
| `503` | `agenthost_unreachable` | The pod origin, call, or response was unavailable |

Sources: `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1594-1625`,
`apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:2590-2718`,
`apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs:43-98`,
`apps/Agentweaver.Api/Sandbox/AgentHostApprovalHttpClient.cs:28-112`, and
`apps/Agentweaver.AgentHost/Program.cs:287-288,486-588`.

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
means the request was not found after coordinator-child resolution and any pod forward. HTTP 503
with `state: "agenthost_unreachable"` is retryable while the run and pod remain active.

## Related reading

- [API reference](./reference/api.md#post-api-runs-id-tool-approvals) — public request and status contract.
- [Sandbox pod execution deep dive](./deep-dive/sandbox-pod-execution.md#returning-tool-approval-decisions-to-agenthost) — end-to-end API-to-pod flow.
- [Sandbox pods reference](./reference/sandbox-pods.md#tool-approval-forwarding-endpoints) — internal AgentHost routes and authentication.
