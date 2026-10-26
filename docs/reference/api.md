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

## Periodic issue sync

These routes do not require a run. They require an authenticated project Owner
with `api.read` and `projects.admin`; run-bound and purpose-bound tokens are
rejected. Gateway exposes them under `/api/v1`. Generated MCP tools prefix the
operation IDs with `agentweaver_`.

| Gateway route | Native owner | Operation ID |
| --- | --- | --- |
| `GET /api/v1/projects/{projectId}/issue-sync` | Orchestrator `GET /api/projects/{projectId}/issue-sync/` | `readIssueSync` |
| `PUT /api/v1/projects/{projectId}/issue-sync` | Orchestrator `PUT /api/projects/{projectId}/issue-sync/` | `configureIssueSync` |
| `POST /api/v1/projects/{projectId}/issue-sync/grants` | Identity same path without `/v1`, with trailing slash | `createIssueSyncGrant` |
| `GET /api/v1/projects/{projectId}/issue-sync/grants/{grantId}` | Identity same path without `/v1` | `readIssueSyncGrant` |
| `POST /api/v1/projects/{projectId}/issue-sync/grants/{grantId}/revoke` | Identity same path without `/v1` | `revokeIssueSyncGrant` |

Create a grant with `{ "selectionCode": "<fresh Identity repository selection code>" }`
after selecting the project's connected GitHub App repository. Identity consumes
the owner-bound selection once and returns
`{ grantId, project: { projectId, tenantId, creatorId, connectionId, repository: { owner, name } }, active }`.
No credential appears in public responses. Secret-authenticated repositories
cannot borrow run-token redemption for background sync.

Configure with `{ expectedRevision, settings: { enabled, grantId,
intervalSeconds, maxItems, maxCycleSeconds } }`. `expectedRevision` is zero on
creation and the returned revision on edits. `intervalSeconds` is 30-86400,
`maxItems` 1-100, and `maxCycleSeconds` 5-120 and no larger than the interval.
A cycle reads one page of at most `maxItems` issues and reconciles at most
`maxItems` tasks, within its deadline. Configuration and status return
`projectId`, `tenantId`, `revision`, `settings`, safe `grantProof`, `nextCycleAt`,
nullable `lastCycleAt`, `status`, nullable `errorCode`, `importPage`, and
`taskCursor`. An unconfigured project returns 404, not fabricated status.
Concurrent configuration/cycles return 409.

Owners may disable sync even after its grant is revoked or the repository is
unselected. Revocation returns `{ "revoked": true }` and prevents the next
cycle; in-flight cycles also revalidate before effects. Only the creator or
a current tenant administrator can revoke a grant. Grant reads validate the original
creator's current authority. A changed repository does not reuse old mappings.

Status is `configured`, `disabled`, `running`, `succeeded`, or `failed`.
Failure codes include `issue_sync_grant_revoked`,
`issue_sync_creator_not_owner`, `issue_sync_repository_not_selected`,
`issue_sync_cycle_timeout`, provider-specific `issue_sync_provider_*`,
and `issue_sync_create_outcome_pending`. Failures preserve confirmed progress
and retry next cycle; uncertain external writes are not reported as success.
See [reconciliation and recovery](../deep-dive/orchestration.md#periodic-repository-issue-sync).

Private routes are not Gateway/MCP tools. Identity
`POST /internal/issue-sync/grants/{grantId}/token` and
`POST /internal/issue-sync/grants/{grantId}/validate` accept only the configured
Orchestrator mTLS peer. Projects `POST /internal/issue-sync/project-authority`
accepts only the Identity mTLS peer and `{ projectId, tenantId, creatorId }`.
Projects' authenticated `GET /api/projects/{projectId}/issue-sync-owner`
and `issue-sync-authority` provide live owner and selected-repository proofs.
The native `GET /api/projects/{projectId}/issue-sync-grant-revocation` supplies
current creator/admin revocation evidence without granting worker authority.
These routes never create run or secret grants. Webhooks and conflict prompts
are out of scope.

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
| Private Knowledge | Current `AccessPrivateKnowledge` permission plus `ReadRunSelection` authority. Run-scoped routes use the accepted run Memory selection; project-wide search requires an unbound token and uses only existing run-provider bindings. The permission comes from Projects, not record content. |
| Runtime | Authenticated bearer plus exact current runtime registration, placement, purpose, proof, or receipt required by that operation. These are not ordinary user write APIs. |
| Environment read | Bearer plus current `ReadProjects` authority and matching tenant/project/run binding. |
| Environment write | Bearer plus current `WriteProjects` authority, matching owner binding, and operation-specific fences. Operations that read accepted selections also require `ReadRunSelection`. Projects does not grant `WriteProjects` to run-bound callers. |
| Browser | Broker local session cookie; mutations require antiforgery protection. |

The optional `X-Agentweaver-Tenant` header is a selector. Owners validate it;
clients cannot use it to grant membership or change identity.

## Gateway

### Project backlog content

The Orchestrator owns project backlog tasks independently of runs. The Gateway
forwards the tenant selector and exposes these versioned operations:

| Method | Gateway path | Authority |
| --- | --- | --- |
| GET | `/api/v1/projects/{projectId}/backlog` | Current `ReadProjects` |
| POST | `/api/v1/projects/{projectId}/backlog/tasks` | Current `WriteProjects` |
| POST | `/api/v1/projects/{projectId}/backlog/ready-all` | Current `WriteProjects` |
| PUT | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}/content` | Current `WriteProjects` |
| PUT | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}/position` | Current `WriteProjects` |
| PUT | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}/state` | Current `WriteProjects` |
| POST | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}/archive` | Current `WriteProjects` |
| DELETE | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}` | Current `WriteProjects` |
| PUT | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}` | Current `WriteProjects` |
| DELETE | `/api/v1/projects/{projectId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}` | Current `WriteProjects` |

Task deletion is rejected when another task depends on it, when it came from an
accepted work plan, when it has a repository issue sync receipt, or while that
project's issue sync cycle is active. Once queue positions are installed, a
successful deletion compacts the task's state queue in the same transaction.

The native paths replace `/api/v1` with `/api`. Reads require `api.read`.
Writes also require `projects.admin` and project Owner or tenant administrator
authority. Run-bound and purpose-bound tokens cannot use these routes. An
optional project binding must match the route. The bearer must include the
Orchestrator and Projects audiences. The owner rechecks membership and role
revisions after acquiring the graph lock and again before committing every
write.

Capture accepts `taskId`, `expectedGraphRevision`, and `content`. Content
contains `title`, optional `description`, `lifecycle` (`open` or `closed`,
default `open`), and optional `repositoryIssue`. A title has 1-512 characters
and no control characters. A description has at most 65,536 characters and no
NUL character. The owner trims the title. Content replacement also requires
`expectedTaskRevision`. Both operations return the current project graph;
stale revisions and duplicate repository issues return `409` with typed
backlog issues. An unchanged replacement preserves both revisions.

`repositoryIssue` contains a provider catalog ID (`providerId`), a typed
`repository` with `owner` and `name`, and a positive `issueNumber`. Repository
names are normalized to lowercase. A project can link an issue to only one
task, including archived tasks. Issue-sync clients read the graph to reuse
that identity on repeated import; a conflicting insert does not create a
second task. These routes do not call the repository provider or synchronize
issues.

Closing a ready task moves it back to backlog. A closed task cannot become
ready or receive a new claim. Closing a claimed task retains its existing
claim and run ownership; it does not cancel execution. Reopening does not
schedule work. Archive remains separate from open/closed lifecycle.
Historical ID-only tasks retain an empty title until edited.

State writes can move an unclaimed open task between `backlog` and `ready`;
only an atomic claim can set `claimed`. Dependency writes reject self-links,
cross-project links, missing tasks, and cycles. Adding an existing dependency
succeeds as a no-op and does not change the graph revision. Marking a task
Ready queues it but does not prove its prerequisites are complete. A claim
checks current prerequisite execution and output evidence again and returns
typed blockers when work is not eligible. Archive and dependency changes use
the same graph lock and revision checks as task edits.

Position writes use a zero-based insertion index in the complete state queue
after removing the moved task. Hidden archived or closed tasks still occupy
queue slots, and each successful move reindexes affected queues. Insertion is
unavailable until migration 035 installs persisted positions.

Each task snapshot includes a zero-based `position` within its queue. A position
write sends `state`, `position`, `expectedGraphRevision`, and
`expectedTaskRevision`. The owner reorders the queue under the graph lock and
advances revisions for every task whose state or position changes. Batch-ready
sends `expectedGraphRevision` and moves open, unarchived tasks without a
pending automation invocation from Backlog to Ready in one graph change.
Prerequisites are still checked when a run claims a task.

Delete sends `expectedGraphRevision` and `expectedTaskRevision`. The owner
rejects claimed, archived, or provisional tasks, tasks that have dependents,
and tasks promoted from an accepted work plan. It removes the deleted task's
own prerequisite links in the same transaction.

Migration `026_backlog_task_content.sql` stores editable content.
Migration `035_backlog_task_queue_position.sql` stores queue order and adds the
index used to read it. The new owner image accepts the complete schema before
035 is applied, but position and batch-ready writes return `503` until then.
Both migration versions, the backlog delete grant, and the PostgreSQL plan
hash are recorded in the reviewed plan; none is applied to a live database.
MCP discovers these operations from the Gateway catalog. The retained Board
supports queue order, batch-ready, state, archive, and prerequisite actions; it
does not start a run.

### Repository team sync

The Gateway exposes an explicit preview-and-commit operation for the current
project team:

| Method | Gateway path | Native owner path | Operation ID |
| --- | --- | --- | --- |
| GET | `/api/v1/projects/{projectId}/team/sync` | Orchestrator `GET /api/projects/{projectId}/team/sync` | `readProjectTeamSync` |
| POST | `/api/v1/projects/{projectId}/team/sync` | Orchestrator `POST /api/projects/{projectId}/team/sync` | `commitProjectTeamSync` |

Both operations require an unbound bearer with `api.read` and `projects.admin`,
current project Owner authority, an active project with a GitHub App repository
selection, and a fresh repository selection code in
`X-Agentweaver-Team-Sync-Selection`. The Gateway forwards the selection code
only as a request header. Responses are `Cache-Control: no-store`.

GET previews changes derived from the current Projects & Config cast and
charters. The preview includes `configurationRevision`, `changeSetHash`,
`nothingToSync`, and changed `.squad/` paths with an `added`, `modified`, or
`removed` kind. POST requires `expectedConfigurationRevision` and
`expectedChangeSetHash` from that preview. The owner recomputes the exact plan
and rejects stale project configuration, repository head, or file content
with `409` before creating a commit. It writes only `.squad/team.md` and
canonical charter paths for current cast members; it does not load repository
files as a team roster or mirror them automatically.
The preview hash also binds the selected GitHub installation and numeric
repository identity, so a repository rename or replacement requires a new
preview even when its path and contents appear unchanged.
The optional commit message is limited to 200 characters and cannot contain
control characters. These routes are excluded from the MCP tool catalog so
repository sync remains an explicit web action.

Repository sync is not AgentSuite export/import. Repository sync reads and
commits selected `.squad/` files in the configured repository. AgentSuite
export/import transfers a digest-checked portable archive, and import stages a
casting proposal for review and confirmation. Neither AgentSuite operation
reads or writes repository files.

Source: `services/gateway/Agentweaver.Gateway/GatewayEndpoints.cs` and
`GatewayRouteCatalog.cs`.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/openapi/v1.json` | Public | OpenAPI 3.1 route and schema catalog. |
| GET | `/agents/agentweaver.agent.md` | Public | Operator agent definition as `text/markdown`; excluded from OpenAPI and the MCP catalog. |
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

Every response containing a `ProjectSummary` includes `effectiveRoles`. Each
entry identifies a role name and its exact `tenant` or `project` resource
scope. Projects & Config includes only active roles for the current caller on
the project or its selected tenant and rechecks them against current owner
state. Tenant-wide roles appear on each applicable tenant project. These
values are for display only: they are not permissions, a client-side
authorization cache, or a grant.

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
| GET | `/{projectId}/configuration/choices` | Read | Read current project-scoped model and platform provider choices. No query parameters; `no-store`. | Yes |
| PUT | `/{projectId}/configuration` | Admin | Append validated configuration. | Yes |
| GET | `/{projectId}/casting/universes` | Read | List supported universe names for an authorized project. | Yes |
| POST | `/{projectId}/casting/proposals` | Admin | Stage a complete configuration draft for review. | Yes |
| POST | `/{projectId}/casting/proposals/scenario` | Admin | Create a scenario-based proposal. | Yes |
| POST | `/{projectId}/casting/proposals/manual` | Admin | Create a manual proposal. | Yes |
| POST | `/{projectId}/casting/proposals/blueprint` | Admin | Create a proposal from a blueprint. Returns the proposal and the skill bindings it couldn't apply. | No |
| GET | `/{projectId}/casting/proposals` | Read | List proposals with optional `limit` (1–100, default 50). | Yes |
| GET | `/{projectId}/casting/proposals/{proposalId:guid}` | Read | Read a proposal. | Yes |
| PATCH | `/{projectId}/casting/proposals/{proposalId:guid}` | Admin | Revise a proposal. | Yes |
| PATCH | `/{projectId}/casting/proposals/{proposalId:guid}/members/{agentId}/role` | Admin | Change a member's catalog role and optional display title in a pending draft. | Yes |
| POST | `/{projectId}/casting/proposals/{proposalId:guid}/confirm` | Admin | Confirm reviewed casting. | Yes |
| POST | `/{projectId}/casting/proposals/{proposalId:guid}/reject` | Admin | Reject a proposal. | Yes |
| GET | `/{projectId}/casting/export` | Admin | Export current casting or optional positive `revision`. | Yes |
| POST | `/{projectId}/casting/import` | Admin | Stage a digest-checked transfer; request body limit 1 MiB. | Yes |
| GET | `/{projectId}/agentsuite-exports` | Admin | Export the current or requested positive configuration revision as a validator-checked AgentSuite package; archive limit 5 MiB. | Yes |
| POST | `/{projectId}/agentsuite-imports` | Admin | Validate a native AgentSuite archive and stage its agents as a casting proposal; request body limit 24 MiB. | Yes |
| GET | `/{projectId}/agentsuite-imports/{sourceIdentity}` | Read | Read a retained AgentSuite import record. | Yes |
| GET | `/{projectId}/agentsuite-agents/{effectiveAgentId}/policy` | Read | Read the policy bound to an imported agent. | Yes |
| PUT | `/{projectId}/runs/{runId}/selection` | Selection | Accept an immutable run selection. | Yes |
| GET | `/{projectId}/runs/{runId}/selection` | Selection | Read the accepted selection. | Yes |
| PUT | `/{projectId}/runs/{runId}/interactive-selection` | Selection | Accept a new chat run selection from current server configuration. The Orchestrator calls this route during run creation. | No |
| GET | `/{projectId}/runs/{runId}/cancellation-selection` | `api.read`, `projects.orchestrator`, unbound project cancellation authority | Read the saved selection after a fresh Contributor, Owner, or tenant-admin check. Used by Orchestrator cancellation, not run admission. | No |

Run selection requires a configured platform runtime-defaults revision. On a
fresh installation, selection returns `409 platform_runtime_unconfigured`
until an operator bootstraps a PlatformAdmin and writes the reviewed defaults.
See [Deploy to Azure](../guide/deploy-to-azure.md#bootstrap-the-first-platformadmin-and-configure-runtime-defaults).

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
| `400` | `email_principal_not_supported` | Email is profile data, not a member ID. Sign in and use the member's Broker subject ID. |
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
| GET | `/api/blueprints` | Bearer; any signed-in caller | List built-in blueprints with their availability. | No |
| POST | `/api/blueprints/validate` | Bearer; any signed-in caller | Check a blueprint without changing state. | No |
| GET | `/api/platform/runtime-defaults/` | `api.read`, `projects.admin`, unbound PlatformAdmin | Read platform runtime defaults. | `/api/v1/platform/runtime-defaults` |
| PUT | `/api/platform/runtime-defaults/` | Same PlatformAdmin authority | Append runtime defaults. | `/api/v1/platform/runtime-defaults` |
| GET | `/api/platform/components` | `api.read`, unbound, current TenantAdmin for the caller's tenant or PlatformAdmin | Read active platform components and readiness for every area. See [Platform components](../guide/platform-components.md). | `/api/v1/platform/components` |
| POST | `/internal/projects/{projectId}/runs/{runId}/broker-run-binding` | Trusted Broker TLS and current Projects proof checks | Validate a Broker run-binding request. Registered only when enabled. | No |

### Casting through the Gateway

Casting routes use Projects & Config as the only configuration authority. The
owner enforces `ReadProjects` for proposal reads and `WriteProjects` for
mutations **and export**. Writes and export require `api.read`, `projects.admin`,
and current TenantAdmin or project Owner authority. Catalog reads require
authentication. No new roles or idempotency headers are introduced.

| Method | Gateway path | Authorization | Result |
| --- | --- | --- | --- |
| POST | `/api/v1/projects/{projectId}/casting/proposals/formulate` | Run-bound bearer and separate project-writer bearer | Pending proposal with completed Formulate status. |
| POST | `/api/v1/projects/{projectId}/casting/proposals/analyze` | Run-bound bearer and separate project-writer bearer | Pending proposal with completed Analyze status. |

Formulate and Analyze are strict Gateway operations with separate request
shapes: Formulate requires a goal, while Analyze has no goal and uses
read-only repository signals. Both requests include the expected configuration
revision, accepted run and root session, idempotency key, canonical prompt,
team size, intent, preferred role IDs, and optional universe. They return a
pending `ProjectCastingProposal` with its generation mode and completed status.
The generated team remains reviewable; neither operation updates live
configuration.

These operations require two bearer credentials. Send the exact run-bound
bearer in `Authorization` and the unbound project-writer bearer in
`X-Agentweaver-Project-Authorization`. Gateway checks that both resolve to the
same issuer, actor, and tenant, that the run bearer matches the request's
project and run, and that the project bearer has current write authority. Only
the run bearer is sent to Orchestrator. Confirm or reject the returned proposal
with its current `expectedDraftRevision`; confirmation also rechecks the base
configuration revision.

These operations are browser-only and are excluded from MCP discovery. The
separate project-writer bearer is never exposed as an MCP tool argument.

Create requests carry `expectedConfigurationRevision` and either `draft`,
`templateId`, or `roleIds`. Scenario and manual requests may also include
`universe`, one of the names returned by the project-scoped universe route;
`null` selects **Random (any universe)**. Patch carries `expectedDraftRevision` and `draft`.
Confirm and reject carry `expectedDraftRevision`. Confirmation also checks the
proposal's base configuration revision. A stale revision returns `409`; rejection
does not change configuration. A selected universe stays in the proposal draft
and is saved with project configuration only after confirmation. Import accepts `expectedConfigurationRevision`
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
| POST | `/{skillId}/revisions/{revision:long}/revoke` | Admin | Revoke an imported revision. | Yes |

| Method | Native full path | Auth | Purpose | Gateway full path |
| --- | --- | --- | --- | --- |
| POST | `/api/skills/preview` | `api.read`; unbound request | Validate local skill files without saving. | `/api/v1/skills/preview` |
| POST | `/api/skills/package` | `api.read`; unbound request | Group uploaded files, a ZIP archive, or draft fields into skill candidates without saving. | `/api/v1/skills/package` |
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

`GET /api/projects/{projectId}/skill-marketplaces/definitions` uses Read
authority and lists the suggested marketplaces. The Gateway path is
`/api/v1/projects/{projectId}/skill-marketplaces/definitions`.

See [Projects & Config](../architecture/projects-config.md) and
[Skill marketplace sources](../architecture/skill-marketplace.md).

## Orchestrator

### Caller notifications

Native owner prefix: `/api/notifications`. Gateway prefix:
`/api/v1/notifications`. These routes require an unbound token with current
Projects read authority; tokens with `project_id` or `run_id` claims return
`403 caller_binding_mismatch`. See [Notifications](../guide/notifications.md).

| Method | Suffix | Purpose | Gateway |
| --- | --- | --- | --- |
| GET | `?cursor=&pageSize=&projectId=&includeDismissed=` | Read newest-first pending gates, a total count and an opaque cursor. | Yes |
| POST | `/{notificationId}/dismiss` | Dismiss for the caller without resolving the gate. Bodyless and idempotent. | Yes |

Responses are `no-store`. Token bindings do not narrow this caller-wide
endpoint; use `projectId`. The default
excludes caller dismissals; shared Flow reads use `includeDismissed=true`.
Unknown or repeated query parameters return `400 query_invalid`.

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
| POST | `/{runId}/messages` | Bound to the run | Send user text to the coordinator or an explicitly selected active session. Returns an Events admission receipt. | Yes |

Create, cancel, and run messages require exactly one `Idempotency-Key` header.
A retry with the same key and body returns the original result. A retry with
the same key and a different body returns `409 idempotency_conflict`. Start
takes no body and no key; a repeated start returns the same result without
sending the first message again. Cancel on a finished run returns `200` with
`alreadyTerminal: true` and does not change state. Cancellation receipts are
scoped to each caller.
Start has a 30-second deadline covering its request lock and downstream calls;
a timeout returns `504 run_start_timeout`. Start after cancellation returns
`409 run_terminal`.

The create request body has `firstMessage` (1–32,768 UTF-8 bytes),
`expectedProjectConfigRevision`, and optional `title` and `workflowId`. The
Gateway accepts create bodies up to 256 KiB. Responses never include the first
message.

### Run messages

Native path: `POST /api/projects/{projectId}/runs/{runId}/messages`.
Gateway path: `POST /api/v1/projects/{projectId}/runs/{runId}/messages`.

The request body has `text` (1–32,768 UTF-8 bytes) and optional
`targetSessionId`. With no target, the message goes to the run's coordinator
session. An explicit target must be an active session in the same run. The
Gateway accepts bodies up to 256 KiB.

The caller needs `api.read`, `projects.orchestrator`, an owner-audience token
bound to the exact project and run, and fresh Projects authority for the
accepted run selection. The issuer and subject in the receipt come from that
authenticated token; caller-supplied sender identities are rejected. An
unbound token or a token bound to another project or run receives `403`.

The `200` receipt includes the Orchestrator message ID, the Events journal
message and thread IDs, thread sequence, target session, and authenticated
sender identity. A retry with the same key and body returns the same receipt
without adding a second journal message. Reusing a key with different text
returns `409 idempotency_conflict`. The key reserved for first-message
delivery (`{runId}:first`) cannot be used for a follow-up message.

### Run list

Native path: `/api/projects/{projectId}/runs`.
Gateway path: `/api/v1/projects/{projectId}/runs`.

| Method | Query | Auth | Purpose | Gateway |
| --- | --- | --- | --- | --- |
| GET | `state`, `startedBy`, `cursor`, `pageSize` | Read | List accepted runs, newest first. | Yes, `listProjectRuns` |

The caller needs the `api.read` scope and project or tenant read permission.
A run-bound token sees only its own run. `pageSize` defaults to 25 and accepts
1–100. `state` takes a comma-separated list of `idle`, `active`, `blocked`,
`completed`, `failed`, `indeterminate`, and `cancelled`, or the `open` and
`terminal` aliases. `startedBy=me` filters to runs accepted by the caller's exact
issuer and subject. The Sessions page uses this filter. Unknown or repeated query parameters return
`400 query_invalid`. A bad filter, cursor, or page size returns
`400 run_state_filter_invalid`, `run_started_by_filter_invalid`, `cursor_invalid`,
or `page_size_invalid`. Pass `nextCursor` back with the same filters and page size;
the cursor is also bound to the caller identity when `startedBy=me` is set.
Each item includes the selected `workflowId` and `needsInputCount`, the count of
durable pending decision gates. Saved run requests supply `title` and `goal`;
they can be `null` for older or non-interactive runs without a saved request.

### Project run session history

| Method | Native path | Auth | Purpose | Gateway path |
| --- | --- | --- | --- | --- |
| GET | `/api/projects/{projectId}/runs/{runId}/history/sessions` | Read | Read a run's session tree. | `/api/v1/projects/{projectId}/runs/{runId}/history/sessions` |

This read uses fresh project authority, so an authorized project reader does
not need to be the actor who accepted the run. A run-bound token remains limited
to its bound run. The Gateway route returns `Cache-Control: no-store`.

### Project issue and pull request lists

These reads return Agentweaver's stored data. They make no source control
provider call at read time, so they don't need a provider credential.

| Path | Query | Gateway |
| --- | --- | --- |
| `GET /api/projects/{projectId}/source-control/issues` | `state`, `cursor`, `pageSize` | Yes, `listProjectIssues` |
| `GET /api/projects/{projectId}/source-control/pull-requests` | `state`, `cursor`, `pageSize` | Yes, `listProjectPullRequests` |

The Gateway path adds `/v1` after `/api`. Both routes need the `api.read`
scope, return `Cache-Control: no-store`, and recheck Projects read permission
after the read. If permission is lost during the read, the route returns
`403` with no items. `pageSize` defaults to 25 and accepts 1–100. Unknown or
repeated query parameters return `400 query_invalid`. A bad cursor or page
size returns `400 cursor_invalid` or `page_size_invalid`. Pass `nextCursor`
back with the same filter and page size.

**Issue list.** Returns repository issues that
[periodic issue sync](#periodic-issue-sync) linked to backlog tasks, ordered by
issue number, newest first. The caller needs current project or tenant
`readProjects` permission. Run-bound tokens are rejected. `state` is `open`
(default), `closed`, or `all`; another value returns
`400 issue_state_filter_invalid`. Archived tasks and tasks without an issue
link aren't listed, and a project without an issue sync configuration returns
an empty page. Each item has the task ID and backlog state, provider,
repository, issue number, stored title, stored state, and task timestamps.
The backlog doesn't store an issue URL or author, so items don't include them.

**Pull request list.** Returns pull requests that the project's runs created
or reused, as Source Control recorded them, ordered by `recordedAt`, newest
first. The caller needs the same permission as the run list. A run-bound token
sees only its own run's records. `state` is `open` (default), `closed`,
`merged`, or `all`; another value returns
`400 pull_request_state_filter_invalid`. Each item has the provider,
repository, number, title, URL, state, head and base branch and commit, and
its provenance: `runId`, `disposition` (`created` or `reused`), `recordedAt`,
and `updatedAt`. Title and state are the values from the last time a run
recorded the pull request, not live provider values.

The pull request list isn't a complete pull request history. Recording starts
when Orchestrator migration `032_source_control_pull_request_records.sql` is
applied. `coverage.recordedSince` reports that time, and
`coverage.includesEarlierPullRequests` is always `false`. Older pull requests
aren't recorded. Until migration 032 is applied, the route returns
`503 pull_request_records_unavailable`, not an empty list. An empty list would
wrongly tell a client that the project has no pull requests; the 503 says the
records don't exist yet. Migration 032 is
code-only: startup, the default migrator, and the P1 initializer don't apply it.

These routes are the data source for the Priority issue and pull request
pickers. When pull request recording has not started, the picker explains that
records are unavailable rather than treating the response as an empty list.

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

### Chief of Staff

Native prefix: `/api/chief-of-staff`.
Gateway prefix: `/api/v1/chief-of-staff`.

These routes store a caller's private Chief of Staff conversations and
Priorities. They never start, cancel, or change runs, sessions, or gates.
New chats use the [Runs](#runs) routes, and the conversation entry binds the
returned run. For guidance, see [Chief of Staff](../guide/chief-of-staff.md).

| Method | Suffix | Query | Purpose | Gateway |
| --- | --- | --- | --- | --- |
| GET | `/conversations` | `cursor`, `pageSize` | List my conversations, newest first. | `listChiefOfStaffConversations` |
| POST | `/conversations` | — | Bind a run that I created to a conversation entry. Returns `200` with the same entry on repeat. | `bindChiefOfStaffConversation` |
| GET | `/conversations/{conversationId}` | — | Read one of my conversations. | `getChiefOfStaffConversation` |
| GET | `/priorities` | — | List my Priorities in order, with `orderRevision`. | `listChiefOfStaffPriorities` |
| POST | `/priorities` | — | Create a Priority at the end of the list. Requires `Idempotency-Key`. Returns `201`, or `200` on replay. | `createChiefOfStaffPriority` |
| PUT | `/priorities/order` | — | Replace the order at `expectedOrderRevision`. | `reorderChiefOfStaffPriorities` |
| GET | `/priorities/{priorityId}` | — | Read one Priority. | `getChiefOfStaffPriority` |
| PATCH | `/priorities/{priorityId}` | — | Change outcome, notes, state, or checklist at `expectedRevision`. | `updateChiefOfStaffPriority` |
| POST | `/priorities/{priorityId}/links` | — | Add a typed link at `expectedRevision`. | `addChiefOfStaffPriorityLink` |
| DELETE | `/priorities/{priorityId}/links/{linkId}` | `expectedRevision` (required) | Remove a link. | `removeChiefOfStaffPriorityLink` |

Every route requires the `api.read` scope and a token that isn't bound to a
project or run. A bound token returns `403`. The Orchestrator reads fresh
Projects authority on each call and returns only the caller's rows; another
user's conversation or Priority returns `404`.

Access rules:

- Binding a conversation requires read permission on the project and a run
  that the caller created through `POST /api/v1/projects/{projectId}/runs`.
  Another user's run returns `404 conversation_run_not_found`.
- Adding a link requires read permission on the linked project. A
  `conversation` link must name one of the caller's conversations in the same
  project.
- Removing a link doesn't require project access, so users can remove links
  to projects they no longer read.
- When the caller loses read permission on a project, conversations and links
  in that project return `access: unavailable` with only their identifier,
  kind, and creation time. Priority outcome, notes, and checklist are the
  caller's own content and stay visible.

Revisions and conflicts:

- Each Priority has a `revision`. Writes with a stale `expectedRevision`
  return `409 priority_revision_conflict`. Reread the Priority, then retry.
- The list has an `orderRevision`. A stale reorder returns
  `409 priority_order_conflict`. A reorder that doesn't name every Priority
  exactly once returns `400 priority_order_invalid`.
- A create replay with the same key and body returns the original Priority. A
  changed body returns `409 idempotency_conflict`.
- Limits: 100 Priorities (`409 priority_limit_reached`), 50 links per
  Priority (`409 priority_link_limit_reached`), and 50 checklist items. A
  duplicate link returns `409 priority_link_exists`.

Link kinds are `run`, `conversation`, `issue`, `pull_request`, and `artifact`.
Every link names `projectId`. `run` needs `runId`, `conversation` needs
`conversationId`, `issue` and `pull_request` need `number`, `providerId`, and
`repository: { owner, name }`, and `artifact`
needs `runId` and `artifactId`. Identity is returned unchanged in list, read and
write receipts, and removed with other protected target fields after read loss.
New repository links without complete identity return `400 priority_link_invalid`.
Before Core migration 033, a compatible owner returns
`503 database_update_pending` for those saves and leaves the revision unchanged.
Existing number-only rows remain readable; clients must refuse ambiguous targets
rather than infer a repository from the number.

Reads resolve each link:

| Kind | `state` |
| --- | --- |
| `run` | The current run state from the run list, or `access: missing` when the run doesn't exist. |
| `conversation` | The run state, or the request state (`awaiting_binding`, `cancelled`) before the run starts. |
| `issue`, `pull_request`, `artifact` | `unavailable`. The owners have no current-state read for these items. |

Priority `state` is `active`, `achieved`, or `dropped`. It's the user's own
judgment and doesn't depend on run state, so an outcome survives runs ending.
An empty `notes` value clears the notes, and `checklist` replaces the whole
list.

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

For coordinator sessions, the status response also includes `executionRecovery`,
a read-only view of the latest bound MAF checkpoint. It reports checkpoint status
and dependency readiness, pending child dispatches, BuildTest, OpenPullRequest,
Merge, and IndependentReview intent/receipt evidence, failed dependencies, and
the current pending decision blocker. External-effect intents without receipts
are marked `mayRepeat`; unresolved running effects are `indeterminate`. A saved
terminal receipt is reported only
as recorded evidence, not as proof of physical exactly-once execution. Missing or
plan-mismatched checkpoints, or checkpoint progress incompatible with the
accepted plan, are marked unavailable, not safe or complete. The projection
does not dispatch work or reconcile children.

For `IndependentReview`, `platformGateOperations` carries `approvalRequestId`,
`evidenceHash`, and `reviewDecision` (null while pending). Its effect recovery
state is `awaitingApproval` until an answer is checkpointed. When dependency
results are ready, dispatch records the immutable review intent before opening
one coordinator Approval decision. Its prompt identifies the reviewed source,
workspace/build-test receipt, or pull request head. The accountable actor answers
the existing `/decisions/gates/{requestId}/answer` route with `approve` or
`reject` (also used when requesting changes). A matching approval releases the
gate only after its terminal receipt is saved. Rejection saves a failed gate and
leaves dependents blocked. An unrelated decision, changed workspace or provider
pull request head, or revoked run authority cannot complete the gate. A software
workflow must order build/test before review; a workflow
with review must put merge after review. Content-only workflows may omit build/test.

The dispatch body may include `openPullRequestWorkspaces`, an object keyed by
OpenPullRequest step ID. Each value contains `workspaceId`,
`workspaceIncarnationId`, `baseSha`, `branchName`, `headSha`, and `baseBranch`.
The Source Control owner checks the current repository pin and exact workspace
revision before it creates or reuses a pull request. The Orchestrator stores the
request and operation identity before the owner call and reuses both after a
retry or recovery. The owner records that operation before contacting the
provider. Recovery reuses only the matching pull request, including when it has
closed; it never creates another pull request for the same operation. A missing
or ambiguous provider result fails closed. A gate succeeds only after the owner
returns a valid pull request number, URL, and matching head SHA and the
Orchestrator saves its receipt. The owner holds a workspace revision lease while
checking the workspace and writing to the provider. An owner error or a changed
decision, source binding, or workspace leaves the gate incomplete, so dependent
steps do not run. This executor requires the private
`Orchestrator:SourceControlOwner:OwnerBaseAddress` configuration.

For each `Merge` step, dispatch may include a `mergeInputs` entry keyed by step ID.
Each entry contains `method` and may contain `pullRequestNumber` with
`expectedHeadSha`; the number and head must be supplied together. When there is
exactly one completed OpenPullRequest ancestor, the dispatcher can derive that
pair and the caller may omit both. Without such an ancestor, both are required.
The dispatcher stores the operation and expected head before it calls the
authenticated Source Control owner's `POST /merge-intents` route. An approval
pending response leaves the checkpoint running and dependents blocked. After the
exact approval is recorded, a later dispatch recovers that same intent through
`POST /merge-intents/{intentId}/execute`. The Merge gate succeeds only after the
owner returns the matching repository, PR, head, method, approval evidence, and
durable merge SHA, which the Orchestrator stores as a compact receipt.

### Backlog claims

Native owner prefix: `/api/projects/{projectId}/runs/{runId}/backlog`.
Graph reads, task creation, state/archive changes, and dependency edits also
have project-scoped operations described above. The Gateway and MCP expose the
claim operation at
`/api/v1/projects/{projectId}/runs/{runId}/backlog/tasks/{taskId}/claim`.
Claims use the current accepted run selection and Coordination authority.

| Method | Suffix | Purpose |
| --- | --- | --- |
| GET | `/` | Read backlog and readiness. |
| POST | `/tasks` | Add task. |
| PUT | `/tasks/{taskId}/state` | Change task state. |
| POST | `/tasks/{taskId}/archive` | Archive task. |
| PUT | `/tasks/{taskId}/dependencies/{prerequisiteTaskId}` | Add dependency. |
| DELETE | `/tasks/{taskId}/dependencies/{prerequisiteTaskId}` | Remove dependency. |
| POST | `/tasks/{taskId}/claim` | Atomically claim eligible work with current prerequisite evidence. |
| POST | `/promotions` | Promote the run's confirmed work plan into backlog tasks. |

#### Plan promotion and task origin

`POST /promotions` turns the current confirmed work plan of a coordinator run
into backlog tasks. The body contains `rootSessionId`, `workPlanId`,
`expectedDecisionStateVersion`, `expectedExecutionFence`,
`expectedGraphRevision`, and `idempotencyKey` (1-128 characters). The owner
checks that the plan is still the confirmed, dispatchable plan of the root
session, that the fence and decision version match, and that the run has no
registered children and no MAF dispatch records.

Each plan item becomes one open task. The task title is the item title and the
description is the item task text. The task ID is `plan-` followed by 32
lowercase hexadecimal characters of a SHA-256 hash of the length-prefixed project,
run, plan, and item IDs. Each item dependency becomes a task dependency. The tasks, edges,
promotion record, and one origin record per task commit in one transaction.
The decision state does not change. After promotion, MAF dispatch rejects the
promoted plan with `maf_execution_dispatch_stale`.

A retry with the same key and values returns the stored promotion. The
response is a `BacklogPlanPromotion` with `promotionId`, `projectId`, `runId`,
`rootSessionId`, `workPlanId`, `workflowId`, `workflowRevision`,
`decisionStateVersion`, `executionFence`, `graphRevision`, `promotedAt`, and
`tasks` (`taskId` and `workItemId`, ordered by `workItemId`).

| Code | Status | Meaning |
| --- | --- | --- |
| `backlog_promotion_invalid` | 400 | A required value is missing or out of range. |
| `backlog_promotion_stale` | 409 | The plan, decision version, or fence is no longer current. |
| `backlog_promotion_run_started` | 409 | The run has children or dispatch records. |
| `backlog_promotion_conflict` | 409 | The plan was already promoted with another key. |
| `backlog_promotion_idempotency_conflict` | 409 | The key was used with different values. |
| `backlog_promotion_selection_unavailable` | 409 | The pinned selection context is missing. |

A stale graph revision returns `409` with typed backlog issues.

A claim request contains `expectedGraphRevision`, `expectedTaskRevision`, and
an `idempotencyKey` of 1-128 characters. The owner binds that key to the task,
the accepted selection snapshot, actor, and run. Retrying the same request
returns the stored claim and root-session identity; reusing the key with
different inputs returns `409`. The task claim, accepted root, initial
decision, and outbox intent commit in one Orchestrator transaction. The
Gateway does not return an accepted claim if prerequisite evidence, run
admission, or current authority checks fail before commit. The Events session
is registered after the transaction. If that registration fails, the task
claim is still durable; retry the same request with the same key to recover
the receipt and retry registration. This does not promise exactly-once
external effects or start an automatic scheduler.

The claim reserves a coordinator root against the run's accepted workflow and
immutable selection snapshot. Its `preparing` phase is a reservation state,
not proof that work has been dispatched or completed. Subsequent coordination
and dispatch use the existing Orchestrator workflow owner.

Two native project routes read the recorded data with current `ReadProjects`
authority:

| Method | Path | Response |
| --- | --- | --- |
| GET | `/api/projects/{projectId}/backlog/promotions` | `{ promotions }`, oldest first. |
| GET | `/api/projects/{projectId}/backlog/origins` | `{ origins }`, ordered by task ID. |

A `BacklogTaskOrigin` contains `task` (`projectId` and `taskId`),
`promotionId`, `runId`, `rootSessionId`, `workPlanId`, and `workItemId`.
Tasks added by ordinary capture or the run `POST /tasks` route have no origin
record. Migration `030_backlog_task_origin.sql` must be applied before these
routes work. Applying the migration, granting access to the new tables, and
accepting the changed PostgreSQL plan hash each require separate operator
approval.

### Source Control

Native prefix:
`/api/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}`.
Gateway prefix:
`/api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}`.

These routes require Coordination authority and current Source Control
selection/pins. Gateway routes require an explicit `X-Agentweaver-Tenant`.
Merge operations also validate the current merge gate and intent.
Output-capture reads return only admitted captures. The Gateway forwards the
`X-Source-Control-Output-Sha256` file header and allows up to 64 MiB for capture
reads, because a capture can hold a 32 MiB manifest and 16 MiB files.

| Method | Suffix | Purpose | Gateway |
| --- | --- | --- | --- |
| POST | `/pin` | Pin repository; optional App `selectionCode`, or legacy secret mode with no body. | Yes |
| POST | `/issues` | Create repository issue. | Yes |
| POST | `/pull-requests` | Create or reuse pull request. | Yes |
| GET | `/pull-requests/{pullRequestNumber:long}/reviews` | Read reviews. | Yes |
| POST | `/workspaces` | Prepare pinned repository workspace. | Yes |
| POST | `/workspaces/{workspaceId}/diff` | Read repository diff. | Yes |
| POST | `/workspaces/{workspaceId}/output-captures` | Capture verified produced output. | Yes |
| GET | `/output-captures` | Read capture page. | Yes |
| GET | `/output-captures/{captureId}` | Read capture metadata. | Yes |
| GET | `/output-captures/{captureId}/diff` | Read captured diff. | Yes |
| GET | `/output-captures/{captureId}/files` | Read exact captured file. | Yes |
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
| POST | `/actions/authorize` | Authorize action and commit guarded admission. Returns HTTP 428 with `runtime_guardrails_content_required` when ordered Guardrails checks apply to a model turn. |
| POST | `/actions/authorize-content` | Authorize a model turn with its exact UTF-8 text so the ordered Guardrails checks can run. The text is never stored. |
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

Project-wide native prefix: `/api/projects/{projectId}/knowledge`.
Project-wide Gateway prefix: `/api/v1/projects/{projectId}/knowledge`.
Project-wide search requires an unbound Broker token with current project
`AccessPrivateKnowledge` and direct project `ReadRunSelection`. It searches all
agents through existing provider bindings and does not create a new binding.
Run-bound tokens are rejected. Results are globally ordered across provider
resources before paging. Each item includes `accessRunId`, the run binding used
to authorize revision-history reads; `record.sourceRunId` is separate record
provenance.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/records` | Private Knowledge and direct project `ReadRunSelection` | Search all agents across existing run-provider bindings by `kind`, `q`, `includeInactive`, `page`, and `pageSize`. The optional `state=pending` requires `kind=proposal` and counts pending proposals even when `includeInactive=false`; other state filters are not supported. Each result includes the access run for history reads; record provenance is separate. |

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
| GET | `/internal/projects/{projectId}/runs/{runId}/project-history/events` | Read | Read run activity with current project read authority and tenant ownership checks. | `/api/v1/projects/{projectId}/runs/{runId}/history/events` |
| GET | `/internal/projects/{projectId}/runs/{runId}/events/live` | Journal | Native NDJSON stream. | No; Gateway SSE uses replay instead |
| GET | `/internal/projects/{projectId}/runs/{runId}/sessions-provider-binding` | Exact bound `ReadRunSelection` authority and current provider | Read immutable Sessions pin. | No |
| GET | No native SSE route | Exact run bearer and current Gateway/owner access checks | SSE over committed replay pages. | `/api/v1/projects/{projectId}/runs/{runId}/events/live` |

### Historical turn text

`GET /api/v1/projects/{projectId}/runs/{runId}/sessions/{sessionId}/turns/{eventId}`
returns `{ eventId, identity, text }` from the existing Events material owner.
Use the event and session identities from a committed `turn` event. The native
route is `GET /internal/sessions/{sessionId}/turns/{eventId:guid}`.

The caller needs an exact run-bound bearer and current project read authority.
The owner rechecks authority after reading stored bytes. Responses use
`Cache-Control: no-store`. Missing history returns 404; lost access returns 403.
The route reads only turn text. It does not accept a material kind or expose
object keys, runtime configuration, SDK cache, or remote-tool results.

### Material, messages, and accepted facts

The following routes are native-only. Runtime/material routes recheck current Projects,
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
| POST | `/api/network-egress/verify` | `WriteProjects` and `ReadRunSelection` | Read-only verification of the exact Environment fence, policy generation, and authorized selection revision. |
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
The browser lists repositories directly from Identity Broker and creates a
selection using the discovered `installationId` and `repositoryId`. The POST
uses the local session cookie and an antiforgery token from `/csrf`, sent in
`X-CSRF-TOKEN`; it returns a short-lived selection code for the Gateway request.

| Method | Suffix | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/connect` | Browser; antiforgery form | Start user authorization. |
| POST | `/install` | Browser; antiforgery form | Start App installation setup. |
| GET | `/callback` | State and transaction cookie | Complete user authorization. |
| GET | `/csrf` | Browser | Issue antiforgery token. |
| GET | `/status` | Browser | Read connection and local credential readiness. |
| POST | `/disconnect` | Browser; CSRF and expected connection revision | Disconnect local connection. |
| GET | `/repositories` | Browser | Read installation/repository metadata. |
| POST | `/selection` | Browser; session cookie, CSRF, and current connection revision | Exchange installation and repository IDs for a short-lived selection code. |

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
| GET | `/runtime/v1/projects/{projectId}/hosted-model-reasoning-catalog` | Authenticated project caller | Read live hosted levels by model reference with project and binding revisions; `no-store`. |
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

## Platform component owner routes

Events & Sessions, Environment, Knowledge, Orchestrator, and Identity Broker
each map `GET /internal/platform/components` only when
`PlatformComponents:ServiceAuthentication` is configured. The route accepts only
a service token for the private audience whose `sub` claim equals the configured
Projects caller subject. It returns the owner's areas with `Cache-Control:
no-store`, and it's not in the Gateway OpenAPI document. Projects calls these
routes for `GET /api/platform/components`. See
[Platform components](../guide/platform-components.md).

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
