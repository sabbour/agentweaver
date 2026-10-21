# Read notifications across runs

Notifications show persisted requests that need human input. The Orchestrator
derives them from pending decision gates. It does not copy gates into another
delivery queue or resolve a gate when you dismiss its notification.

## Read pending attention

Call `GET /api/v1/notifications` through the Gateway with an unbound browser
access token and optional `X-Agentweaver-Tenant` selector. You do not need to
open a run first. The caller needs `api.read` and both the Orchestrator and
Projects audiences. Unbound tokens are required: a token with a `project_id`
or `run_id` claim returns `403 caller_binding_mismatch`, and a Projects owner
response that reports a binding returns `502 projects_authorization_contract_invalid`.
Token bindings do not narrow this caller-wide endpoint; use `projectId` instead.

The owner checks current Projects authority before and after reading. Project
`readProjects` or `readRunSelection` permits a project; tenant `readProjects`
permits projects in that tenant. Losing access removes those items on the next
read.

The response contains:

- `items`: newest-first notifications, each with `notificationId`, `projectId`,
  `runId`, `sessionId`, `requestId`, `kind`, `prompt`, `gateKind`, `stateVersion`,
  `createdAt`, and `updatedAt`.
- `count`: the total matching pending items, not just the current page.
- `nextCursor`: an opaque cursor, or `null` when no more items remain.

Use `pageSize` from 1 to 100; the default is 25. Pass `nextCursor` as `cursor`
to read the next page without changing the query. Cursors bind the caller,
tenant, membership revision, filters and page size. When those
change, start a new first page. Unknown, empty or repeated query parameters
return `400 query_invalid`.

Optional `projectId` narrows the result. It never grants access to that project.
The default `includeDismissed=false` suits the existing notification bell and
attention list. Chief of Staff Flow uses
`projectId={projectId}&includeDismissed=true` so dismissal does not hide shared
work that still needs input. Both views use the same source.

All responses use `Cache-Control: no-store`. Polling is supported. Clients
should keep one poller for the existing bell and attention list rather than
create another notification surface.

## Dismiss a notification

Call `POST /api/v1/notifications/{notificationId}/dismiss` without a request body.
The operation is idempotent and returns `204`. A dismissal belongs to the
authenticated issuer, caller and tenant. It never affects another caller.
A later gate with a different request ID creates a new notification.

Resolved gates, terminal runs, inactive sessions and superseded execution
fences are not returned. The gate's `requestId` and `stateVersion` identify
the current decision; approval and answers still use the existing decision
routes, which enforce their own authority and stale-version checks.

## Compatibility with 0.x

The reference implementation is `NotificationsService` and
`NotificationsEndpointsTests` on `origin/dev`. It has three derived sources:

| Source | Trigger and identity | Removal |
| --- | --- | --- |
| Human review | A nonarchived run is awaiting review. The ID includes the run and latest assembly-review request timestamp, so another review of the same run can notify again. The title is the task text, trimmed to 120 characters, or a short fallback. | The run leaves review, is archived, loses project visibility, or the caller dismisses this occurrence. |
| Tool approval | The canonical durable approval query finds unresolved actionable approval requests. Child requests group under the top-level review run; context and approval cards are deduplicated. The ID is stable during a continuous pending lifecycle. | Resolution, expiry, terminal source, loss of visibility, or caller dismissal. Once the lifecycle clears, its dismissal no longer suppresses a later gate. |
| Backlog promotion | A delegated plan was updated within 24 hours and created at least one nonarchived task stamped with that coordinator run. One notice per run shows the actual singular or plural task count and links to the Board. | The 24-hour window expires, no live promoted tasks remain, visibility changes, or the caller dismisses the notice. |

Project visibility comes from explicit project roles or platform-admin access.
Items sort newest first. Dismissals are stored per user, not globally.
The only delivery channels are the in-app bell, toast and optional sound;
there is no email, Teams, operating-system push or webhook delivery.

The browser implementation polls every 20 seconds. The first successful read sets
the badge without showing a toast for every existing item. Newly observed IDs
produce a toast and optional sound. Opening the bell marks items seen in
browser memory. The sound preference uses browser storage, not server
preferences (`aw.notifications.muted`). Approval toasts remain until handled
or dismissed, and their source is checked again before navigation. Other
toasts expire after 12 seconds. A transient poll failure retries next cycle
without losing state. Dismissals are durable per caller. The v1 backend preserves that
division: browser seen and sound state do not become gate state.

0.x also derives recent delegated-work promotion notices from persisted plans
and the tasks they actually created. Those notices need the v1 task-promotion
owner contract; they must not be synthesized from unrelated task creation.
The pending-gate endpoint does not claim to implement that source. Follow
[#2187](https://github.com/sabbour/agentweaver/issues/2187) for the remaining
promotion-source integration and
[#2176](https://github.com/sabbour/agentweaver/issues/2176) for repository issue
synchronization. ADR 0001 does not drop these capabilities.
