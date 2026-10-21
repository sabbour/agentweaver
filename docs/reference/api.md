# HTTP API reference

Use this reference to find v1 HTTP routes, their owning services, and their
authorization requirements. An **owner service** handles an operation's
authoritative state and authorization. A **native route** is mapped by an owner
service; it is not public through the Gateway unless the table lists a Gateway
path. These tables describe source mappings, not deployed addresses.

For request and response contracts, see [service contracts](./contracts.md).
For provider setup, see [Configure providers](../guide/configure-providers.md).
For runtime settings, see [Runtime configuration](./runtime-configuration.md).
The Gateway's `GET /openapi/v1.json` returns the versioned public schemas, which
the [MCP tools](./mcp-tools.md) reference uses.

## How to read this reference

Some tables list a route prefix and its suffixes. Append a suffix to the prefix
to form the full path. A route constraint such as `:guid` or `:long` describes
the accepted value; it is not part of the value you send. An empty suffix adds
no slash.

The **Gateway** column shows whether the Gateway exposes the same suffix under
the versioned prefix. `No` means the route is native-only. Where a table notes
that the Gateway omits a trailing slash, use the path shown in the Gateway
column.

### Authorization key

All protected routes validate the configured issuer, audience, and token
lifetime. Scopes do not replace current roles, owner state, or immutable bindings.

| Key | Requirement |
| --- | --- |
| Public | No bearer required. Callback routes still validate their protocol transaction. |
| Bearer | Authenticated Broker bearer; additional owner checks are described by the operation. |
| Read | `api.read`, active tenant membership, and current project read authority: TenantAdmin, Owner, Contributor, or Viewer. Token bindings must match the resource. |
| Admin | `api.read` and `projects.admin`, active membership, and TenantAdmin or project Owner authority. |
| Selection | `api.read` and `projects.orchestrator`, current project Orchestrator assignment, and matching project/run binding. |
| Coordination | Exact project/run bearer with `api.read` and `projects.orchestrator`, matching owner audience, and a current accepted Projects selection. |
| Journal | Authenticated signed project/run scope, current Sessions provider binding, and owner-authorized journal access. Writes also require the appropriate owner admission. |
| Private Knowledge | Selection authority plus current `AccessPrivateKnowledge` permission and accepted run Memory selection. The permission comes from Projects, not record content. |
| Runtime | Authenticated bearer plus exact current runtime registration, placement, purpose, proof, or receipt required by that operation. These are not ordinary user write APIs. |
| Environment read | Bearer plus current `ReadProjects` authority and matching tenant/project/run binding. |
| Environment write | Bearer plus current `WriteProjects` authority, matching owner binding, and operation-specific fences. Operations that read accepted selections also require `ReadRunSelection`. Projects does not grant `WriteProjects` to run-bound callers. |
| Browser | Broker local session cookie; mutations require antiforgery protection. |

The optional `X-Agentweaver-Tenant` header is a selector. Owners validate it;
clients cannot use it to grant membership or change identity.

## Gateway

Source: `services/gateway/Agentweaver.Gateway/GatewayEndpoints.cs` and
`GatewayRouteCatalog.cs`.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/openapi/v1.json` | Public | OpenAPI 3.1 route and schema catalog. |
| GET | `/api/v1/authorization/context` | Bearer; `api.read`, non-purpose token | Fresh Projects actor, tenant, binding, and effective authority. |
| GET | `/health/live` | Public | Process liveness. |
| GET | `/health/ready` | Public | Gateway host readiness. |

The public Gateway surface is an explicit route catalog. The tables below list
the routes owned by Projects & Config, Orchestrator, Knowledge, and Events.
Unmapped `/api/v1/{**path}` requests return `404 route_not_found`; the Gateway
does not provide a generic upstream proxy.

The Gateway preserves owner statuses and response bodies. `202` reports
acceptance, not completion. Unavailable or invalid owner responses return `502`;
owner request timeouts return `504`. See [Gateway](../guide/gateway.md) for CORS,
body bounds, and selector forwarding.

### Identity connection BFF

These routes are outside the OpenAPI/MCP catalog. Repo App BFF mappings forward
the same path to Identity. They do not establish that the matching Identity
producer route is registered in the current host.

Prefix: `/api/auth/github/repo-app`.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/authorizations` | Bearer; no tenant selector | Begin user Repo App authorization. |
| GET | `/authorization/status` | Bearer; no tenant selector | Read redacted authorization status. |
| GET | `/authorizations/{transactionId}` | Bearer; no tenant selector | Read transaction status. |
| POST | `/authorization/refresh` | Bearer; no tenant selector | Refresh user authorization. |
| DELETE | `/authorization` | Bearer; no tenant selector | Revoke user authorization. |

| Method | Full path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/github/repository-selections` | Bearer; no tenant selector | Read safe repository metadata. |
| POST | `/api/github/repository-selections` | Bearer; no tenant selector | Exchange a repository name for a selection code. |
| GET | `/auth/github/repo-app/callback` | Public; exact `__Host-agentweaver-repo-app-auth` transaction cookie | Forward only `code`, `state`, and `error` to Identity. |
| GET | `/auth/github/repo-app/installation/callback` | Public; exact `__Host-agentweaver-repo-app-install-auth` transaction cookie | Forward only installation callback fields. |

Prefix: `/api/connections/copilot-user/v1`. Native Identity prefix:
`/internal/connections/copilot-user`.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/begin` | Bearer; current connection-scope authority | Start Copilot connection. |
| POST | `/complete` | Bearer and exact link cookie | Complete state-matched authorization. |
| POST | `/refresh` | Bearer; expected revision | Refresh the connection. |
| POST | `/revoke` | Bearer; expected revision | Revoke the connection. |
| GET | `/{connectionId:guid}` | Bearer; current connection authority | Read status. |

Every Copilot operation requires an unbound bearer and current
`WriteProjects` for project scope, or `WritePlatformRuntimeDefaults` for platform
scope. These permissions require `api.read` and `projects.admin`. Existing
connections also require the original owner issuer and actor.

Prefix: `/api/connections/remote-mcp/v1`. Native Identity prefix:
`/internal/connections/remote-mcp`.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/register` | Admin; unbound bearer | Register an OAuth binding. |
| GET | `/{connectionId:guid}` | Read; unbound bearer, current owner binding | Read redacted status. |
| POST | `/{connectionId:guid}/consent` | Admin; unbound bearer, current binding and revisions | Prepare provider consent. |
| POST | `/{connectionId:guid}/refresh` | Admin; unbound bearer, expected connection/credential/configuration revisions | Refresh owner-held credentials. |
| POST | `/callback` | Admin; unbound bearer, pending state-matched binding | Complete code or denied-consent callback. |
| POST | `/{connectionId:guid}/disconnect` | Read; unbound bearer, current owner binding and revisions | Clear protected token references. |

Copilot and Remote MCP BFF calls forward the optional explicit tenant selector.
They are distinct from the direct Broker browser Repo App surface below.

## Projects & Config

Source: `services/projects-config/Agentweaver.Projects.Config/`.
Native project prefix: `/api/projects`.
Gateway project prefix: `/api/v1/projects`.

Use [Configure providers](../guide/configure-providers.md) to set up providers
before you accept a run selection. The routes below show where the Gateway
exposes project configuration and run-selection operations.

| Method | Native suffix | Auth | Purpose | Gateway |
| --- | --- | --- | --- | --- |
| POST | `/` | Admin; unbound token, TenantAdmin | Create a project without assigning an Owner. | Yes, empty suffix |
| GET | `/` | Read; unbound token | List visible tenant projects. | Yes, empty suffix |
| GET | `/{projectId}` | Read; exact signed run when `runId` is supplied | Read project metadata. | Yes |
| PATCH | `/{projectId}` | Admin | Update name and lifecycle at `expectedRevision`. | Yes |
| GET | `/{projectId}/role-assignments` | Read; unbound token, TenantAdmin or project Owner | List active project role assignments. | Yes |
| POST | `/{projectId}/role-assignments` | Admin; unbound token, TenantAdmin | Assign a project role to a tenant member. | Yes |
| DELETE | `/{projectId}/role-assignments/{assignmentId:guid}` | Admin; unbound token, TenantAdmin | Revoke an assignment at `expectedRevision`. | Yes |
| GET | `/{projectId}/configuration` | Read | Read current or requested `revision`. | Yes |
| PUT | `/{projectId}/configuration` | Admin | Append validated configuration. | Yes |
| POST | `/{projectId}/casting/proposals` | Admin | Stage a complete configuration draft for review. | Yes |
| POST | `/{projectId}/casting/proposals/scenario` | Admin | Create a scenario-based proposal. | Yes |
| POST | `/{projectId}/casting/proposals/manual` | Admin | Create a manual proposal. | Yes |
| GET | `/{projectId}/casting/proposals` | Read | List proposals with optional `limit` (1–100, default 50). | Yes |
| GET | `/{projectId}/casting/proposals/{proposalId:guid}` | Read | Read a proposal. | Yes |
| PATCH | `/{projectId}/casting/proposals/{proposalId:guid}` | Admin | Revise a proposal. | Yes |
| POST | `/{projectId}/casting/proposals/{proposalId:guid}/confirm` | Admin | Confirm reviewed casting. | Yes |
| POST | `/{projectId}/casting/proposals/{proposalId:guid}/reject` | Admin | Reject a proposal. | Yes |
| GET | `/{projectId}/casting/export` | Admin | Export current casting or optional positive `revision`. | Yes |
| POST | `/{projectId}/casting/import` | Admin | Stage a digest-checked transfer; request body limit 1 MiB. | Yes |
| PUT | `/{projectId}/runs/{runId}/selection` | Selection | Accept an immutable run selection. | Yes |
| GET | `/{projectId}/runs/{runId}/selection` | Selection | Read the accepted selection. | Yes |
| PUT | `/{projectId}/runs/{runId}/interactive-selection` | Selection | Accept a new chat run selection from current server configuration. The Orchestrator calls this route during run creation. | No |
| GET | `/{projectId}/runs/{runId}/cancellation-selection` | `api.read`, `projects.orchestrator`, unbound project cancellation authority | Read the saved selection after a fresh Contributor, Owner, or tenant-admin check. Used by Orchestrator cancellation, not run admission. | No |

### Project role assignments

These routes manage who holds the Owner, Contributor, Viewer, and
Orchestrator roles on one project. Gateway operation IDs are
`listProjectRoleAssignments`, `assignProjectRole`, and
`revokeProjectRoleAssignment`. Every response sets `Cache-Control: no-store`.
None of the routes accept idempotency keys. A repeated assign request returns
`409 role_assignment_exists`, and a repeated revoke request returns
`409 revision_conflict`.

**Authorization.** The request token must be unbound: no project, run, or
purpose binding.

- To list, the caller needs the `api.read` scope and either the current
  TenantAdmin role for the tenant or the Owner role on the project.
- To assign or revoke, the caller needs the `api.read` and `projects.admin`
  scopes and the current TenantAdmin role. A project Owner can't change
  roles. The service share-locks the caller's membership and checks the
  exact TenantAdmin assignment and revision in the same serializable
  transaction as the change. Each change writes an `authority_audit` row in
  that transaction.

**List.** `GET /api/v1/projects/{projectId}/role-assignments` accepts no query
parameters and returns `200` with only active assignments:

```json
{
  "projectId": "string",
  "assignments": [
    {
      "assignmentId": "uuid",
      "principalId": "string",
      "role": "owner | contributor | viewer | orchestrator",
      "revision": 1,
      "createdBy": "string",
      "createdAt": "date-time"
    }
  ]
}
```

`principalId` is the member's subject under the caller's issuer.
`createdBy` is the subject of the tenant admin who made the assignment.
Assignments are sorted by role, then by `principalId`.

**Assign.** `POST /api/v1/projects/{projectId}/role-assignments` accepts a
strict JSON body and no query parameters:

```json
{ "principalId": "string", "role": "owner | contributor | viewer | orchestrator" }
```

The principal must have an active membership in the caller's tenant. The
route returns `201` with a `Location` header and this body:

```json
{
  "assignmentId": "uuid",
  "projectId": "string",
  "principalId": "string",
  "role": "orchestrator",
  "state": "active",
  "revision": 1,
  "grantedBy": "string",
  "grantedAt": "date-time",
  "revokedBy": null,
  "revokedAt": null
}
```

**Revoke.** `DELETE /api/v1/projects/{projectId}/role-assignments/{assignmentId}?expectedRevision=N`
requires one unsigned integer `expectedRevision`. It returns `200` with the
same assignment shape, with `state` set to `revoked` and the revision
increased by one. The last active Owner of an active project can't be
revoked. After the project is archived, a tenant admin can revoke its last
Owner.

| Status | Problem code | Meaning |
| --- | --- | --- |
| `400` | `invalid_request`, `invalid_project_role`, `invalid_principal_id`, `invalid_expected_revision` | The query, role, principal ID, or revision isn't valid. |
| `403` | `forbidden` | The token or current role doesn't allow the operation. |
| `404` | `not_found`, `principal_not_found` | The project, assignment, or tenant member doesn't exist for the caller. |
| `409` | `role_assignment_exists`, `project_archived`, `last_project_owner`, `revision_conflict` | The current state doesn't allow the change. |
| `503` | `role_assignment_unavailable` | The runtime database grants for assign and revoke aren't applied. See [Runtime authority writes](runtime-configuration.md#runtime-authority-writes). |

> [!NOTE]
> The web client's existing `assignProjectRole` and `revokeProjectRoleAssignment`
> calls in `apps/web/src/api/client.ts` send snake_case fields and read a
> `GET /projects/{projectId}/access` route that doesn't exist. The web client
> must use the contract on this page.

### Catalog and platform routes

| Method | Native full path | Auth | Purpose | Gateway full path |
| --- | --- | --- | --- | --- |
| GET | `/api/authorization/context` | Bearer; `api.read`, non-purpose token | Read current authority. | `/api/v1/authorization/context` |
| GET | `/api/casting/templates` | Bearer | Read built-in casting templates. | `/api/v1/casting/templates` |
| GET | `/api/catalog/roles` | Bearer | Read built-in roles. | `/api/v1/catalog/roles` |
| GET | `/api/platform/runtime-defaults/` | `api.read`, `projects.admin`, unbound PlatformAdmin | Read platform runtime defaults. | `/api/v1/platform/runtime-defaults` |
| PUT | `/api/platform/runtime-defaults/` | Same PlatformAdmin authority | Append runtime defaults. | `/api/v1/platform/runtime-defaults` |
| POST | `/internal/projects/{projectId}/runs/{runId}/broker-run-binding` | Trusted Broker TLS and current Projects proof checks | Validate a Broker run-binding request. Registered only when enabled. | No |

### Casting through the Gateway

All casting routes forward the original bearer and optional
`X-Agentweaver-Tenant` selector to Projects & Config. The owner enforces
`ReadProjects` for proposal reads and `WriteProjects` for mutations **and
export**. Writes and export require `api.read`, `projects.admin`, and current
TenantAdmin or project Owner authority. Catalog reads require authentication.
No new roles or idempotency headers are introduced.

Create requests carry `expectedConfigurationRevision` and either `draft`,
`templateId`, or `roleIds`. Patch carries `expectedDraftRevision` and `draft`.
Confirm and reject carry `expectedDraftRevision`. Confirmation also checks the
proposal's base configuration revision. A stale revision returns `409`; rejection
does not change configuration. Import accepts `expectedConfigurationRevision`
and `transfer` and creates a pending proposal, not an applied configuration.
Gateway and owner import request bodies are limited to 1,048,576 bytes.
Unsupported transfer versions or incompatible teams return `422`.

See [casting contracts](./contracts.md#casting-proposals-and-transfers) for
response fields and [Use the Gateway](../guide/gateway.md#review-project-casting)
for the review sequence.

### Skills

Native prefix: `/api/projects/{projectId}/skills`.
Gateway prefix: `/api/v1/projects/{projectId}/skills`.

| Method | Native suffix | Auth | Purpose | Gateway |
| --- | --- | --- | --- | --- |
| GET | `/` | Admin; archived projects allowed | Read revision inventory with `cursor` and `pageSize`. | Yes, empty suffix |
| POST | `/import` | Admin | Import validated content with idempotency and digest checks. | Yes |
| PUT | `/{skillId}/assignment` | Admin | Save pinned skill assignment at the expected configuration revision. | Yes |
| POST | `/{skillId}/revisions/{revision:long}/revoke` | Admin | Revoke an imported revision. | No |

| Method | Native full path | Auth | Purpose | Gateway full path |
| --- | --- | --- | --- | --- |
| POST | `/api/skills/preview` | `api.read`; unbound request | Validate local skill files without saving. | `/api/v1/skills/preview` |
| GET | `/api/projects/{projectId}/runs/{runId}/agents/{agentId}/skills` | Selection; exact agent and accepted pins | Read effective run skills. | No |

### Marketplace sources

Native prefix: `/api/projects/{projectId}/skill-marketplaces/sources`.
Gateway prefix: `/api/v1/projects/{projectId}/skill-marketplaces/sources`.

List, browse, and preview use Read authority. Create, update, remove, and import
use Admin authority. Upstream content reads recheck current authority.

| Method | Native suffix | Purpose | Gateway |
| --- | --- | --- | --- |
| GET | `/` | List project sources. | Yes, empty suffix |
| POST | `/` | Create a public source. | Yes, empty suffix |
| PUT | `/{sourceId}` | Append a source revision. | Yes |
| DELETE | `/{sourceId}` | Tombstone using `expectedRevision`. | Yes |
| GET | `/{sourceId}/browse` | Browse `expectedSourceRevision`, optional `query`, `page`, and `pageSize`. | Yes |
| POST | `/{sourceId}/preview` | Validate selected content at a resolved commit. | Yes |
| POST | `/{sourceId}/import` | Import selected content with digest and idempotency checks. | Yes |

See [Projects & Config](../architecture/projects-config.md) and
[Skill marketplace sources](../architecture/skill-marketplace.md).

## Orchestrator

Source: `services/orchestrator/Agentweaver.Orchestrator/`.

### Runs

Native prefix: `/api/projects/{projectId}/runs`.
Gateway prefix: `/api/v1/projects/{projectId}/runs`.

These routes start a new chat run, deliver its first message, and cancel it.
Create and start require the project Orchestrator role (`acceptRunSelection`).
Owner or Contributor alone receives `403` when creating a chat.
A tenant admin grants the caller the project
Orchestrator role through
`/api/v1/projects/{projectId}/role-assignments`.

| Method | Suffix | Token | Purpose | Gateway |
| --- | --- | --- | --- | --- |
| POST | `/` | Not bound to a run | Save the first message, mint `runId` and `rootSessionId`, and accept the run selection. Returns `201` with `awaiting_binding`. | Yes |
| POST | `/{runId}/start` | Bound to the run | Accept the root session and deliver the saved first message once. Returns `200` with `started`. | Yes |
| POST | `/{runId}/cancel` | Not bound to a run | Cancel any run as a current project Contributor, Owner, or tenant admin. Viewer receives `403`. Returns `200`, including terminal no-ops. | Yes |

Create and cancel require exactly one `Idempotency-Key` header. A retry with
the same key and body returns the original result. A retry with the same key
and a different body returns `409 idempotency_conflict`. Start takes no body
and no key; a repeated start returns the same result without sending the first
message again. Cancel on a finished run returns `200` with `alreadyTerminal: true`
and does not change state. Cancellation receipts are scoped to each caller.
Start has a 30-second deadline covering its request lock and downstream calls;
a timeout returns `504 run_start_timeout`. Start after cancellation returns
`409 run_terminal`.

The create request body has `firstMessage` (1–32,768 UTF-8 bytes),
`expectedProjectConfigRevision`, and optional `title` and `workflowId`. The
Gateway accepts create bodies up to 256 KiB. Responses never include the first
message.

### Run list

Native path: `/api/projects/{projectId}/runs`.
Gateway path: `/api/v1/projects/{projectId}/runs`.

| Method | Query | Auth | Purpose | Gateway |
| --- | --- | --- | --- | --- |
| GET | `state`, `cursor`, `pageSize` | Read | List accepted runs, newest first. | Yes, `listProjectRuns` |

The caller needs the `api.read` scope and project or tenant read permission.
A run-bound token sees only its own run. `pageSize` defaults to 25 and accepts
1–100. `state` takes a comma-separated list of `idle`, `active`, `blocked`,
`completed`, `failed`, `indeterminate`, and `cancelled`, or the `open` and
`terminal` aliases. Unknown or repeated query parameters return
`400 query_invalid`. A bad filter, cursor, or page size returns
`400 run_state_filter_invalid`, `cursor_invalid`, or `page_size_invalid`.
Pass `nextCursor` back with the same filter and page size.
`title` and `goal` are `null` until a run start request stores them.

### Project metrics and usage

Use these read-only routes for one project's dashboard:

| Gateway path (GET) | Owner path | Operation ID |
| --- | --- | --- |
| `/api/v1/projects/{projectId}/metrics?from=&to=&bucket=day` | Orchestrator `/api/projects/{projectId}/metrics` | `readProjectMetrics` |
| `/api/v1/projects/{projectId}/usage?from=&to=` | Events & Sessions `/api/projects/{projectId}/usage` | `readProjectUsage` |

Both owners require `api.read`, their own audience and the Projects audience,
and a fresh Projects authorization context. The caller needs project
`readProjects` or `readRunSelection`, or tenant `readProjects`. Unbound and
project-bound callers can read the project. A run-bound caller sees only its
own run. Both responses use `Cache-Control: no-store`.

`from` is inclusive and `to` is exclusive. Supply ISO dates (`2026-10-01`)
or timestamps with an explicit offset (`2026-10-01T00:00:00Z`). Dates start
at UTC midnight. When `to` is omitted, it defaults to the current UTC time.
When `from` is omitted, it defaults to 30 days before `to`. An interval can
span at most 92 days. Equal bounds are valid and return no interval activity.
Only metrics accepts `bucket`; its only value is `day`. Unknown or repeated
query parameters, invalid dates, reversed bounds, and unsupported buckets
return a `400` problem response with `title` and `code` set to `query_invalid`.
The response does not include exception text. A default start date earlier
than the supported date range is also invalid.

Metrics returns `projectId`, normalized `from` and `to`, the counts below,
and `series`. Current state and lifetime counts are not limited by the date
range. They still respect tenant, project, and run-bound scope.

| Field | v1 definition | 0.x dashboard mapping |
| --- | --- | --- |
| `activeRuns` | Runs whose derived state is `active`, including presenting root turns. | Active runs tile; does not include the separate active-agent count. |
| `blockedRuns` | Runs whose derived state is `blocked`. | Additional v1 state detail, not a summary tile in 0.x. |
| `runsInRange` | Runs with `created_at` in `[from,to)`. | Runs this week when callers select that week; Created total for the selected range. |
| `runsTotal` | All accepted runs in the authorized scope. | Total run count. |
| `completedRuns` | Terminal runs (`completed`, `failed`, `cancelled`, `indeterminate`) with `completed_at` in `[from,to)`. | Completed total and Done series; not the 0.x completed-task count. |
| `succeededRuns` | Runs in `completedRuns` whose derived state is `completed`. | Numerator for v1 run success; not the 0.x agent-dependency success measure. |
| `series[].date` | UTC calendar date, ascending, for each day intersecting the interval. | Runs over time and daily activity. |
| `series[].created` | Runs created within the interval on that UTC day. | Created series. |
| `series[].completed` | Terminal runs completed within the interval on that UTC day. | Done series. |

Days with no activity have zero counts. An equal-bound interval has an empty
series. A run created before the interval can contribute to its completion
count. Terminal rows without `completed_at` do not contribute to completion
metrics.

These definitions map to the 0.x dashboard controls. v1 uses persisted
owner data, not Application Insights, and cannot reproduce historic 0.x
telemetry. v1 run success is `succeededRuns / completedRuns`; display it as
unknown when the denominator is zero. 0.x used per-agent dependency success,
so the two percentages are not equivalent.

Usage returns `projectId`, normalized `from` and `to`, `usageByModel`, and
`usageByAgent`. Each array item has `id` (persisted `model_id` or `agent_id`),
`requestCount`, `inputTokens`, `outputTokens`, `cachedTokens`,
`cacheWriteTokens`, `reasoningTokens`, `avgDurationMs`, `runs`, `cost`, and
`unpricedRequestCount`. Empty intervals or projects have empty usage arrays.
`runs` counts distinct `run_id` values represented in that group and interval.
These groups replace the model metrics and usage portion of the agent
leaderboard, not agent-role or agent-success telemetry.

Token and request counts are sums of recorded measurements. `avgDurationMs`
is the arithmetic mean of ledger-event durations, not a request-weighted mean.
If any included row lacks a measurement, that aggregate is `null`, not zero.
TTFT is not returned because the ledger does not record it.

`cost[]` keeps a separate subtotal for each `(priceUnit, disposition)` pair.
Disposition is `estimate`, `reconciled`, or `unpriced`. Never sum across
units. An unpriced amount remains `null`, including a missing unit.
`unpricedRequestCount` sums request counts on unpriced rows; it is `null`
when any such row has an unknown request count, and zero when no rows are
unpriced. Reads do not price, reprice, reserve, or change budgets.

Cross-project overview is out of scope. The Gateway proxies each owner; it
does not combine metrics and usage into one response. Clients that need
aligned intervals should pass the same explicit bounds to both routes.

### Coordination

Native prefix: `/api/projects/{projectId}/runs/{runId}/coordination`.
Gateway prefix: `/api/v1/projects/{projectId}/runs/{runId}/coordination`.

All routes use Coordination authority, then validate operation-specific decision
versions, execution fences, session relations, and receipts. Registering or
accepting owner state does not start an automatic scheduler.

| Method | Suffix | Purpose | Gateway |
| --- | --- | --- | --- |
| POST | `/root` | Accept root owner state. | Yes |
| GET | `/status` | Read owner run status. | Yes |
| POST | `/recovery` | Recover owner execution state. | Yes |
| POST | `/sessions/{sessionId}/decisions/outcome` | Propose an outcome decision. | Yes |
| POST | `/sessions/{sessionId}/actions/propose_outcome_spec` | Propose outcome specification. | Yes |
| POST | `/sessions/{sessionId}/actions/select_workflow` | Select workflow. | Yes |
| POST | `/sessions/{sessionId}/actions/propose_work_plan` | Propose work plan. | Yes |
| POST | `/sessions/{sessionId}/actions/revise_work_plan` | Revise work plan. | Yes |
| POST | `/sessions/{sessionId}/actions/dispatch` | Dispatch an accepted MAF checkpoint through configured owner integrations. | No |
| POST | `/sessions/{sessionId}/actions/request_assembly` | Request assembly. | Yes |
| GET | `/sessions/{sessionId}/decisions` | Read current decision state. | Yes |
| GET | `/sessions/{sessionId}/tree` | Read session tree. | Yes |
| GET | `/sessions/{sessionId}/status` | Read activity, lifecycle, and blockers. | Yes |
| GET | `/sessions/{sessionId}/runtime-observation/v1` | Read current runtime evidence; native registration is conditional. | Yes |
| POST | `/sessions/{sessionId}/turn-failure` | Report failed turn. | Yes |
| POST | `/sessions/{sessionId}/decisions/gates/{requestId}/answer` | Answer exact pending question. | Yes |
| POST | `/sessions/{sessionId}/decisions/questions` | Request a question gate. | Yes |
| POST | `/sessions/{sessionId}/decisions/outcome/questions/next` | Request the next outcome question. | Yes |
| POST | `/sessions/{sessionId}/decisions/approvals` | Request approval. | Yes |
| POST | `/sessions/{sessionId}/decisions/gates/{requestId}/acknowledge` | Acknowledge gate. | Yes |
| POST | `/sessions/{sessionId}/decisions/gates/{requestId}/approve` | Approve exact gate. | Yes |
| POST | `/sessions/{sessionId}/decisions/gates/{requestId}/reject` | Reject exact gate. | Yes |
| POST | `/sessions/{parentSessionId}/children` | Register child owner state. | Yes |
| POST | `/sessions/{parentSessionId}/spawn` | Accept a spawn request. | Yes |
| POST | `/sessions/{sessionId}/fork` | Admit an explicit-event fork. | Yes |
| POST | `/sessions/{sessionId}/detach` | Detach eligible child. | Yes |
| POST | `/sessions/{parentSessionId}/children/{childSessionId}/archive` | Archive eligible completed child. | Yes |
| POST | `/sessions/{sessionId}/idle-subscriptions` | Accept idle subscription. | Yes |
| POST | `/sessions/{sessionId}/steering` | Record steering. | Yes |
| POST | `/sessions/{sessionId}/messages` | Accept addressed message. | Yes |
| POST | `/sessions/{sessionId}/turn-boundary` | Advance turn boundary. | Yes |
| POST | `/sessions/{sessionId}/turn-completion` | Record turn completion. | Yes |
| GET | `/sessions/{sessionId}/notifications` | Read owner notifications. | Yes |
| POST | `/sessions/{sessionId}/notifications/{notificationId:guid}/acknowledge` | Acknowledge notification. | Yes |
| POST | `/sessions/{sessionId}/messages/{messageId:guid}/acknowledge` | Acknowledge message. | Yes |
| GET | `/policy-evaluations/{receiptId:guid}` | Read immutable policy receipt. | Yes |
| GET | `/policy-evaluations/{receiptId:guid}/admission` | Recheck policy receipt admission. | Yes |
| GET | `/sessions/{sessionId}/build-test/commands/{checkpointId}/{stepId}` | Read accepted BuildTest command. | No |
| GET | `/sessions/{sessionId}/build-test/commands/{checkpointId}/{stepId}/workspace-seed` | Read pinned repository seed. | No |
| POST | `/sessions/{sessionId}/build-test/commands/{checkpointId}/{stepId}/workspace-capture/verify` | Verify produced Workspace capture. | No |
| POST | `/sessions/{sessionId}/suspend` | Request fenced suspend. | No |
| POST | `/sessions/{sessionId}/resume` | Request retained-Workspace resume. | No |

### Backlog

Native-only prefix: `/api/projects/{projectId}/runs/{runId}/backlog`.
All routes use Coordination authority and current owner task/graph revisions.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/` | Read backlog and readiness. |
| POST | `/tasks` | Add task. |
| PUT | `/tasks/{taskId}/state` | Change task state. |
| POST | `/tasks/{taskId}/archive` | Archive task. |
| PUT | `/tasks/{taskId}/dependencies/{prerequisiteTaskId}` | Add dependency. |
| DELETE | `/tasks/{taskId}/dependencies/{prerequisiteTaskId}` | Remove dependency. |
| POST | `/tasks/{taskId}/claim` | Atomically claim with current prerequisite evidence. |

### Source Control

Native prefix:
`/api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}`.
Gateway prefix:
`/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}`.

These routes require Coordination authority and current Source Control
selection/pins. Gateway routes require an explicit `X-Agentweaver-Tenant`.
Merge operations also validate the current merge gate and intent.

| Method | Suffix | Purpose | Gateway |
| --- | --- | --- | --- |
| POST | `/pin` | Pin repository; optional App `selectionCode`, or legacy secret mode with no body. | Yes |
| POST | `/issues` | Create repository issue. | Yes |
| POST | `/pull-requests` | Create or reuse pull request. | Yes |
| GET | `/pull-requests/{pullRequestNumber:long}/reviews` | Read reviews. | Yes |
| POST | `/workspaces` | Prepare pinned repository workspace. | Yes |
| POST | `/workspaces/{workspaceId}/diff` | Read repository diff. | Yes |
| POST | `/workspaces/{workspaceId}/output-captures` | Capture verified produced output. | No |
| GET | `/output-captures` | Read capture page. | No |
| GET | `/output-captures/{captureId}` | Read capture metadata. | No |
| GET | `/output-captures/{captureId}/diff` | Read captured diff. | No |
| GET | `/output-captures/{captureId}/files` | Read exact captured file. | No |
| POST | `/merge-intents` | Request approval for merge intent. | Yes |
| GET | `/merge-intents/{intentId}` | Read merge intent. | Yes |
| POST | `/merge-intents/{intentId}/execute` | Execute approved intent. | Yes |
| POST | `/webhook-relay` | Admit owner-authenticated relayed webhook. | No |

| Method | Full native path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/internal/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/output-captures/{captureId}/proof` | Coordination; exact capture and selection | Provide proof to Events. |
| POST | `/api/source-control/github/webhook` | Bearer | Explicitly reject direct GitHub webhook delivery. |

### Private coordination and runtime

Native-only prefix:
`/internal/projects/{projectId}/runs/{runId}/coordination`.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/message-route` | Coordination; Events audience | Validate message route. |
| GET | `/sessions/{sessionId}/owner-binding` | Coordination; Events audience | Read current session binding. |
| POST | `/sessions/{sessionId}/fork-admission` | Coordination; Events audience | Validate fork admission. |
| GET | `/sessions/{sessionId}/runtime-owner-context` | Runtime | Read current mapped runtime owner context. |
| GET | `/sessions/{sessionId}/runtime-execution-observation` | Runtime | Read current execution observation. |
| GET | `/sessions/{sessionId}/runtime-execution-observation/v2` | Runtime | Read version 2 observation. |
| POST | `/sessions/{sessionId}/runtime-registrations` | Runtime | Register accepted placement and runtime binding. |
| GET | `/sessions/{sessionId}/runtime-registrations` | Runtime | Read session registration. |
| GET | `/sessions/{sessionId}/usage-receipts/{receiptId:guid}` | Runtime | Read immutable native usage source receipt. |
| GET | `/sessions/{sessionId}/usage-dispatch-completions/{dispatchId:guid}` | Runtime | Read dispatch completion evidence. |

Native-only prefix: `/internal/runtime`. All routes below use Runtime authority
and the exact current registration, proof, action, turn, or receipt binding.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/registrations/{runtimeInstanceId:guid}` | Read current registration. |
| GET | `/registrations/{runtimeInstanceId:guid}/skills` | Read accepted runtime skill material. |
| POST | `/actions/authorize` | Authorize action and commit guarded admission. |
| POST | `/actions/verify` | Revalidate action proof. |
| POST | `/actions/credit-denials/read` | Read action credit denial. |
| POST | `/remote-tool-results/verify` | Verify reviewed remote result proof. |
| GET | `/sources/{runtimeInstanceId:guid}` | Read current native usage source. |
| POST | `/sources/{runtimeInstanceId:guid}` | Save source facts. |
| POST | `/observations` | Record runtime observation. |
| POST | `/turns/begin` | Admit turn. |
| POST | `/turns/denials/read` | Read native credit denial. |
| POST | `/turns/fixed-credit-denials/record` | Record fixed-credit denial. |
| POST | `/turns/fixed-credit-denials/read` | Read fixed-credit denial. |
| POST | `/credit-guards/read` | Read current credit guard. |
| POST | `/turns/fixed-credit-denials/no-send` | Record fixed-credit no-send evidence. |
| POST | `/turns/no-send` | Record no-send evidence. |
| POST | `/turns/observations` | Save turn observation. |
| POST | `/turns/accounting` | Record accounting acknowledgment. |
| POST | `/turns/recorded` | Record durable turn completion evidence. |
| POST | `/suspend/require-current` | Validate current suspend operation. |
| POST | `/suspend/release/require-current` | Validate placement release. |
| POST | `/resume/require-current` | Validate current resume. |
| POST | `/resume/require-bootstrap-current` | Validate current resume bootstrap. |

Runtime registration, usage, and suspend/resume helper mappings depend on the
host's enabled runtime composition. A route mapping is not proof of a registered
native SDK session or an available provider.

## Knowledge

Source: `services/knowledge/Agentweaver.Knowledge/KnowledgeEndpoints.cs`.
Native prefix: `/api/projects/{projectId}/runs/{runId}/agents/{agentId}`.
Gateway prefix: `/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}`.
Every route in this table is in the Gateway catalog.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/records` | Private Knowledge | Create record. |
| GET | `/records` | Private Knowledge | Search by `kind`, `q`, `includeInactive`, `page`, and `pageSize`. |
| GET | `/records/{recordId:guid}` | Private Knowledge | Read exact agent record. |
| PUT | `/records/{recordId:guid}` | Private Knowledge | Revision-checked correction, archive, or supersession. |
| GET | `/records/{recordId:guid}/revisions` | Private Knowledge | Page immutable revisions. |
| POST | `/records/{recordId:guid}/restore` | Private Knowledge | Append restored revision. |
| POST | `/records/{recordId:guid}/approve` | Private Knowledge | Approve eligible Decision. |
| GET | `/records/export` | Private Knowledge | Export exact-scope bundle. |
| POST | `/records/import` | Private Knowledge | Import bundle, up to 1 MiB. |
| POST | `/proposals/{proposalId:guid}/promote` | Private Knowledge; existing binding and owner effect checks | Promote Proposal. |
| POST | `/proposals/{proposalId:guid}/reject` | Private Knowledge | Reject Proposal. |
| GET | `/context` | Private Knowledge | Compile context using `q`, `maxItems`, and `maxTokens`. |

All writes require one valid `Idempotency-Key` header. Reads do not.

| Method | Native-only full path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/internal/accepted-effects/{receiptId:guid}` | Current `WriteProjects` authority, exact receipt actor/tenant/binding | Read accepted-effect receipt. |
| GET | `/internal/projects/{projectId}/runs/{runId}/accepted-effects/{receiptId:guid}` | Same authority plus exact route scope | Read scoped receipt. |

See [Knowledge and Memory](../architecture/knowledge-memory.md).

## Events & Sessions

Source: `services/events-and-sessions/Agentweaver.EventsAndSessions/`.

### Journal

Native-only prefix: `/internal/sessions`.
All routes require Journal authority and exact project/run claims.

| Method | Suffix | Purpose |
| --- | --- | --- |
| POST | `/{sessionId}` | Create session and persist provider binding. |
| POST | `/{sessionId}/events` | Append typed event; generic writes cannot bypass trusted-writer requirements. |
| POST | `/{sessionId}/fork` | Create explicit-event fork after Orchestrator admission. |
| POST | `/{sessionId}/policy-evaluations` | Fetch, validate, and append immutable policy receipt. |
| GET | `/{sessionId}/events` | Replay bounded event page. |
| GET | `/{sessionId}/events/live` | Stream bounded NDJSON deliveries. |

| Method | Native full path | Auth | Purpose | Gateway full path |
| --- | --- | --- | --- | --- |
| GET | `/internal/projects/{projectId}/runs/{runId}/events` | Journal | Replay committed run journal. | `/api/v1/projects/{projectId}/runs/{runId}/events` |
| GET | `/internal/projects/{projectId}/runs/{runId}/events/live` | Journal | Native NDJSON stream. | No; Gateway SSE uses replay instead |
| GET | `/internal/projects/{projectId}/runs/{runId}/sessions-provider-binding` | Exact bound `ReadRunSelection` authority and current provider | Read immutable Sessions pin. | No |
| GET | No native SSE route | Exact run bearer and current Gateway/owner access checks | SSE over committed replay pages. | `/api/v1/projects/{projectId}/runs/{runId}/events/live` |

### Material, messages, and accepted facts

These routes are native-only. Runtime/material routes recheck current Projects,
Orchestrator, and provider state. Trusted peer checks are additional to bearer
authentication where configured.

| Method | Full path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/internal/runtime/resume/configuration` | Runtime | Resolve private resume configuration. |
| POST | `/internal/sessions/{sessionId}/runtime-configuration` | Runtime; accepted configuration binding | Store private runtime configuration material. |
| POST | `/internal/sessions/{sessionId}/material` | Runtime; typed current material admission | Append typed turn/tool/cache material. |
| GET | `/internal/sessions/{sessionId}/material/{eventId:guid}/{kind}` | Runtime; material-specific current read authority | Read exact typed material. |
| POST | `/internal/sessions/{sessionId}/remote-tool-results` | Runtime; trusted reviewed-result producer/proof | Store private reviewed remote tool result. |
| POST | `/internal/sessions/{sessionId}/remote-tool-results/read` | Runtime; exact reviewed-call proof | Read private reviewed result. |
| POST | `/internal/sessions/{sessionId}/produced-run-captures/{captureId}` | Journal and Source Control proof | Admit produced output package. |
| GET | `/internal/sessions/{sessionId}/produced-run-captures/events/{eventId:guid}` | Journal and current capture authority | Read captured output by event. |
| POST | `/internal/project-facts/accepted-effects` | Bearer and current Knowledge accepted-effect receipt | Admit project accepted-effect fact. |
| POST | `/internal/addressed-messages/admit` | Journal and current Orchestrator route admission | Admit owner-routed message. |
| POST | `/internal/addressed-messages/projects/{projectId}/runs/{runId}/sessions/{sessionId}/claim` | Journal; current recipient fence | Claim next message. |
| POST | `/internal/addressed-messages/projects/{projectId}/runs/{runId}/sessions/{sessionId}/messages/{messageId:guid}/present` | Journal; exact claim and owner admission | Mark presented. |
| POST | `/internal/addressed-messages/projects/{projectId}/runs/{runId}/sessions/{sessionId}/messages/{messageId:guid}/acknowledge` | Journal; exact claim and owner admission | Acknowledge delivery. |

### Native usage

These routes are registered when `EventsAndSessions:RuntimeUsage:Enabled` is
true. They require exact bound `ReadRunSelection` authority; write/admission
routes additionally validate current immutable runtime source evidence.

| Method | Native full path | Purpose | Gateway full path |
| --- | --- | --- | --- |
| POST | `/internal/projects/{projectId}/runs/{runId}/usage/copilot-run-admission` | Admit Copilot run cost binding. | No |
| POST | `/internal/sessions/{sessionId}/usage-receipts` | Admit reference-only source receipt. | No |
| POST | `/internal/runtime/sources/{runtimeInstanceId:guid}/cost-preflight` | Check current pinned cost source. | No |
| GET | `/internal/projects/{projectId}/runs/{runId}/usage` | Read persisted run usage totals. | `/api/v1/projects/{projectId}/runs/{runId}/usage` |
| POST | `/internal/projects/{projectId}/runs/{runId}/usage/copilot-cost-snapshot` | Read current Copilot cost snapshot. | No |

See [events](./events.md) for wire shapes and
[Events & Sessions](../architecture/events-sessions.md) for admission rules.

## Environment

Source: `services/environment/Agentweaver.Environment/`.
These are native owner routes, not Gateway or first-party MCP operations.

### Lifecycle and egress

| Method | Full path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/projects/{projectId}/runs/{runId}/environments` | Environment write and `ReadRunSelection` | Enroll Environment owner state. |
| POST | `/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/release` | Environment write and `ReadRunSelection` | Release fenced owner state. |
| GET | `/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/lifecycle` | Environment read | Inspect lifecycle. |
| POST | `/api/network-egress/apply` | Environment write and `ReadRunSelection` | Apply accepted egress intent. |
| POST | `/api/network-egress/verify` | Environment write and `ReadRunSelection` | Verify owner-pinned policy. |
| POST | `/api/network-egress/revoke` | Environment write | Revoke owned policy. |
| POST | `/api/network-egress/reconcile` | Environment write and `ReadRunSelection` | Reconcile recorded uncertain effect. |
| POST | `/internal/provider-lifecycle/reports` | Bearer | Returns `503 environment_provider_lifecycle_unavailable`; report ingestion is not implemented. |

### Workspace volumes

Prefix:
`/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes`.
GET uses Environment read authority. Writes use Environment write authority and
`ReadRunSelection`; flush also validates the trusted runtime and exact Workspace
generation.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/{volumeId}` | Inspect volume. |
| POST | `/{volumeId}` | Create volume owner record. |
| POST | `/{volumeId}/provision` | Provision selected resource. |
| POST | `/{volumeId}/replace` | Replace exact resource generation. |
| POST | `/{volumeId}/bind` | Bind volume. |
| POST | `/{volumeId}/unbind` | Unbind volume. |
| POST | `/{volumeId}/release` | Release eligible resource. |
| POST | `/{volumeId}/flush` | Verify trusted durable flush. |
| POST | `/cleanup/retry` | Retry recorded cleanup. |
| POST | `/cleanup/{sourceReplaceOperationId:guid}/reconcile` | Reconcile exact old-resource cleanup. |

### Sandbox and BuildTest

Prefix: `/api/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox`.
Except for the two placement projections, every route below requires Environment
write authority and `ReadRunSelection`, including GET inspection routes.
BuildTest also validates accepted checkpoint/command evidence and the current
Sandbox, network, and Workspace binding.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/provision` | Environment write and `ReadRunSelection` | Provision exact accepted Sandbox lease. |
| GET | `/` | Environment write and `ReadRunSelection` | Inspect lease at requested network-policy generation. |
| GET | `/v1/placement` | Environment write | Read actor-safe placement. |
| GET | `/v1/internal/placement` | Exact run-bound `ReadRunSelection` | Read run-bound placement. |
| GET | `/v1/operations/{operationId:guid}/provider-attempts` | Environment write and `ReadRunSelection` | Inspect provider attempt evidence. |
| GET | `/v1/late-resource-cleanups` | Environment write and `ReadRunSelection` | Inspect late-resource cleanup. |
| GET | `/build-test/binding-preparation` | Environment write and `ReadRunSelection` | Read accepted command binding preparation. |
| POST | `/build-test/commands` | Environment write and `ReadRunSelection` | Dispatch accepted BuildTest command. |
| GET | `/build-test/commands/{operationId:guid}` | Environment write and `ReadRunSelection` | Read command operation. |
| POST | `/build-test/commands/{operationId:guid}/reconcile` | Environment write and `ReadRunSelection` | Reconcile command. |
| GET | `/build-test/commands/{operationId:guid}/workspace-capture` | Environment write and `ReadRunSelection` | Read verified Workspace output capture. |
| POST | `/build-test/commands/{operationId:guid}/workspace-capture/acknowledge` | Environment write and `ReadRunSelection` | Acknowledge capture. |
| POST | `/abandon` | Environment write and `ReadRunSelection` | Abandon eligible lease. |
| POST | `/reconcile` | Environment write and `ReadRunSelection` | Reconcile uncertain Sandbox effect. |

### Runtime bootstrap

Prefix:
`/internal/projects/{projectId}/runs/{runId}/environments/{environmentId}/coordination/sessions/{sessionId}/runtime-bootstrap/profiles/{profileId}`.
All routes use Runtime authority and current Environment placement/profile checks.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | Empty | Read bootstrap placement context. |
| GET | `/readiness` | Read measured readiness context. |
| GET | `/workspace` | Read exact Workspace context. |
| POST | `/resolve` | Resolve current registered bootstrap profile. |

Native-only prefix: `/internal/runtime`. All routes use Runtime authority.

| Method | Suffix | Purpose |
| --- | --- | --- |
| POST | `/resume/prepare` | Prepare retained Workspace resume. |
| POST | `/resume/verify` | Verify resumed Workspace. |
| POST | `/resume/bootstrap` | Deliver resume bootstrap or state-loss receipt. |
| POST | `/v1/activate` | Activate registered runtime. |
| POST | `/bootstrap/deliver` | Deliver owner nonce to configured host. |
| POST | `/suspend/resolve` | Resolve trusted suspend and flush state. |
| POST | `/suspend/release-placement` | Release exact suspended placement. |

### Remote MCP connections

Prefix: `/api/projects/{projectId}/remote-mcp/connections`.
Reads require current `ReadProjects`; mutations require current `WriteProjects`.
These use bearer-authenticated current Projects authority and revision checks.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | Empty | List connections. |
| POST | Empty | Create connection configuration. |
| GET | `/{connectionId:guid}` | Read current connection. |
| GET | `/{connectionId:guid}/configurations/{configurationRevision:long}` | Read exact configuration revision. |
| PUT | `/{connectionId:guid}/identity-binding` | Attach revisioned Identity binding. |
| PUT | `/{connectionId:guid}` | Update connection configuration. |
| POST | `/{connectionId:guid}/enable` | Enable connection. |
| POST | `/{connectionId:guid}/disable` | Disable connection. |
| DELETE | `/{connectionId:guid}` | Tombstone connection. |

| Method | Full path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/internal/remote-mcp/current-call/verify` | Runtime; trusted service and reviewed-call binding | Verify current call authority. |
| POST | `/internal/remote-mcp/current-call/dispatch` | Runtime; trusted Orchestrator peer and current-call proof | Validate request, then return `503 remote_mcp_native_transport_unavailable`. |

See [Environment Sandbox](../architecture/environment-sandbox.md),
[Environment egress](../architecture/environment-egress.md), and
[Remote MCP connections](../architecture/remote-mcp-connections.md).

## Identity Broker

Source: `services/identity/Agentweaver.Identity.Broker/`.
The Gateway BFF tables above also list the exact native Copilot and Remote MCP
connection prefixes. Those native endpoints use bearer validation and current
owner-scope authorization, not browser local cookies.

### OAuth and diagnostics

| Method | Full path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/` | Public | Redirect to `/.well-known/openid-configuration` with HTTP 302. |
| GET | `/connect/authorize` | Registered OAuth client request; local session or external challenge | Start PKCE authorization and consent. |
| GET | `/connect/authorize/resume` | Local session and one-use resume handle | Resume saved authorization. |
| POST | `/connect/consent` | Browser; exact pending consent | Approve or decline requested scopes. |
| POST | `/connect/token` | OAuth grant/client/PKCE validation | Exchange code or refresh token; recheck authority. |
| GET | `/diagnostics/whoami` | Bearer | Read authenticated subject and scopes. |
| POST | `/secrets/redeem` | Bearer; exact current grant, purpose, actor, project/run, and SecretRef | Redeem authorized secret version. |

The identity middleware also serves its configured OAuth/OIDC metadata and
external identity-provider callback. These middleware paths are not new product
REST endpoints. See [Identity and secrets](../architecture/identity-secrets.md)
for registration and authentication configuration.

### Direct Repo App browser surface

Prefix: `/auth/github/repo-app`. Registered when the Repo App browser integration
is configured. These routes differ from the Gateway bearer BFF.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/connect` | Browser; antiforgery form | Start user authorization. |
| POST | `/install` | Browser; antiforgery form | Start App installation setup. |
| GET | `/callback` | State and transaction cookie | Complete user authorization. |
| GET | `/csrf` | Browser | Issue antiforgery token. |
| GET | `/status` | Browser | Read connection and local credential readiness. |
| POST | `/disconnect` | Browser; CSRF and expected connection revision | Disconnect local connection. |
| GET | `/repositories` | Browser | Read installation/repository metadata. |
| POST | `/selection` | Browser; CSRF and current connection revision | Create short-lived repository selection. |

Native `POST /internal/source-control/github-app/installations/token` requires
current bearer/grant and accepted Source Control binding. It mints a
repository-scoped installation token inside Identity. It is not a browser route.

### Runtime credentials

Native-only prefix: `/internal/runtime`.
All routes use Runtime authority, exact current actor/tenant, and the
operation's purpose-bound credential or owner grant. Registration is conditional
on the runtime credential composition.

| Method | Suffix | Purpose |
| --- | --- | --- |
| POST | `/model-session/grant` | Issue current runtime model grant. |
| POST | `/model-session/verify` | Revalidate model credential receipt. |
| POST | `/model-session/redeem` | Redeem owner-authorized model credential. |
| POST | `/bootstrap/request` | Request bootstrap delivery. |
| POST | `/bootstrap/verify-pending` | Verify pending Configure proof. |
| POST | `/bootstrap/consume` | Consume Configure nonce. |
| POST | `/bootstrap/exchange` | Exchange current bootstrap proof. |
| POST | `/source/verify` | Verify source credential proof. |
| POST | `/source/rotate` | Rotate source credential. |
| POST | `/source/revoke` | Revoke source credential. |

## AgentHost

The service entry is `services/agenthost/Agentweaver.AgentHost/Program.cs`.
Mappings are in `packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs`.

The first three paths are configuration-derived. Let `configure.AbsolutePath`
be the path of the host's registered `ConfigureEndpoint`; no fixed value is
implied here. Protected calls require a current bearer for that configure
audience and matching request scheme/authority, then exact runtime proof checks.

| Method | Path expression or literal | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `configure.AbsolutePath` | Runtime | Receive bootstrap nonce. |
| POST | `configure.AbsolutePath + "/activate"` | Runtime | Configure native session. |
| POST | `configure.AbsolutePath + "/resume-state"` | Runtime | Read private resume bootstrap state. |
| POST | `/runtime/v1/refresh` | Runtime | Refresh host session proof. |
| POST | `/runtime/v1/turns/fixed-credit-denials/read` | Runtime | Read fixed-credit guard evidence. |
| POST | `/runtime/v1/turns/denials/read` | Runtime | Read native denial evidence. |
| GET | `/runtime/v1/readiness` | Runtime | Read current authenticated readiness. |
| POST | `/runtime/v1/a2a/message:send` | Runtime | Admit native turn/message send. |
| POST | `/runtime/v1/suspend` | Runtime | Suspend with trusted Workspace handling. |
| POST | `/runtime/v1/workspace/resume` | Runtime | Verify retained Workspace resume. |
| GET | `/health/live` | Public | Process liveness. |
| GET | `/health/ready` | Public | Measured host readiness; `503` on startup or owner failure. |

See [AgentHost](../architecture/agenthost.md).

## MCP

`services/mcp/Agentweaver.Mcp/Program.cs` maps authenticated `/mcp` through the
MCP SDK. It also maps public protected-resource metadata at the audience-derived
path and public `GET /health/live` and `GET /health/ready`.
See [MCP tools](./mcp-tools.md) for transport, scopes, tool arguments, and results.

## Shared health routes

Projects & Config, Knowledge, Events & Sessions, Environment, Orchestrator, and
Identity each map anonymous `GET /health/live` and `GET /health/ready`.
Their readiness handlers inspect their configured owner database. This is not
an end-to-end product check. Gateway and MCP report host availability; AgentHost
has separate measured readiness as listed above.

The Web host maps its own health routes and SPA/static paths. See
[web host reference](./web.md#host-health).

## Registration and implementation boundaries

- Optional native routes depend on host composition. A public Gateway mapping
  does not enable an absent owner route.
- Native Source Control capture/file routes are not Gateway-produced-file APIs.
- Environment provider lifecycle ingestion and Remote MCP native dispatch
  explicitly return unavailable responses as listed above.
- No generic filesystem, arbitrary upstream, or unversioned 0.x `/api` proxy is
  added by the Gateway.
- Gate approval, provider selection, process readiness, and accepted requests
  report different facts. None substitutes for completed runtime execution.
