# Use the Gateway

`Agentweaver.Gateway` is the .NET HTTP entry point for the browser, CLI, and
first-party MCP clients. It exposes a finite, versioned API and delegates
requests to Projects & Config, Orchestrator, Knowledge, Events, and the
Identity Broker. It is not an arbitrary upstream proxy: callers cannot choose
owner URLs or supply owner identity headers.

This article describes the checked-in v1 host. Source code and Kubernetes
manifests do not establish that the Gateway, its owners, or a public endpoint
are deployed or available.

## Before you begin

To make a live request, you need a deployed Gateway, its required owner
services, and a Broker-issued access token for a registered client. The client
must use the configured HTTPS issuer and audience. To build or inspect the
source contract, use the .NET 10 SDK selected by `global.json`.

## Understand request authorization

The versioned HTTP API uses the `/api/v1` prefix. The Gateway publishes its
route contract at `GET /openapi/v1.json`. The document describes supported
routes, request and response schemas, owners, bearer security, and the Events
server-sent events (SSE) response.

For a product API request, the Gateway validates the Identity Broker issuer,
token signature, lifetime, and configured audience. It forwards the original
bearer token to the owning API. The owner then checks current membership,
roles, project or run bindings, and operation-specific permissions. A valid
Gateway token does not replace an owner authorization check.

The optional `X-Agentweaver-Tenant` header is a tenant selector, not an
identity, role, or permission claim. The Gateway forwards it only on routes
that allow the selector. The owner validates it and checks current authority.
The authorization-context endpoint returns advisory information for a client;
it is not a permission grant and is not safe to cache as an authorization
decision.

## Call a Gateway API operation

1. Read `GET /openapi/v1.json` to find a supported route, its owner, and its
   request and response contract.
2. Obtain an access token from the Identity Broker for a registered client.
3. Send the request to the exact versioned route with the bearer token. Send
   `X-Agentweaver-Tenant` only when that route allows the selector.
4. Check the owning service's response. A Gateway-accepted request does not
   prove that an asynchronous operation completed.

## Configure the Gateway

Set the following configuration before you start the host:

| Setting | Requirement |
| --- | --- |
| `Identity:Issuer` | HTTPS issuer for the Identity Broker. |
| `Identity:Audience` | HTTPS audience expected by the Gateway. |
| `Gateway:Owners:Projects` | HTTPS service root for Projects & Config. |
| `Gateway:Owners:Orchestrator` | HTTPS service root for Orchestrator. |
| `Gateway:Owners:Knowledge` | HTTPS service root for Knowledge. |
| `Gateway:Owners:Events` | HTTPS service root for Events. |
| `Gateway:Owners:IdentityBrokerAddress` | HTTPS service root for the Identity Broker. |
| `Gateway:Owners:Environment` | Optional HTTPS service root for Environment. Without it, Environment routes return 503 `owner_not_configured`. |
| `Gateway:WebOrigin` | The Web application's exact HTTPS origin, for example `https://portal.<managed-domain>`. |
| `Gateway:OwnerRequestTimeoutSeconds` | Finite timeout from 1 through 120 seconds. The default is 15 seconds. |

Service addresses must be HTTPS roots. The Gateway rejects insecure or
non-root owner addresses and does not follow redirects.

## Configure browser access

Set `Gateway:WebOrigin` to the Web application's exact origin. Do not use a
wildcard, URL path, credentials, query, or fragment. The Gateway handles
preflight requests before authentication and applies route-specific allow lists
for origins, methods, and headers. Most Gateway browser policies do not allow
credentials. The Copilot connection BFF is the exception: it allows
credentials for its callback cookie, only `GET` and `POST`, and only the
`Authorization`, `Accept`, `Content-Type`, and `X-Agentweaver-Tenant` headers.
The versioned API, OpenAPI, and run-bound GitHub App installation policies do
not allow credentials.

The Identity Broker's direct Repo App browser endpoints have a separate,
credentialed CORS policy. Configuring `Gateway:WebOrigin` does not configure
the Broker's policy.

To read the current actor, tenant selection, and effective authority, call
`GET /api/v1/authorization/context`. The endpoint forwards the validated
bearer token and optional tenant selector to Projects & Config, validates the
owner response, and returns it with `Cache-Control: no-store`. Treat the
response as current display context, not a grant. Owners recheck authorization
for each protected operation.

## Read project and Skill data

The Gateway forwards Skills and marketplace operations to Projects & Config.
The owner reads and validates selected Skill content; the Gateway does not
upload source files for marketplace selection.

Use these routes to browse and select Skills:

- `GET /api/v1/projects/{projectId}/skills` reads paginated inventory metadata.
  It accepts `cursor` and `pageSize`; the owner defaults the page size to 25
  and accepts values from 1 through 50. The opaque cursor uses keyset order,
  by skill ID in C collation, ascending, then revision descending. This is
  not a snapshot across pages. Repeated or unknown query keys, invalid
  cursors, and page sizes outside 1 through 50 return the owner's `400`
  response. The owner requires current `api.read` and `projects.admin`
  authority, active tenant membership, and TenantAdmin or project Owner
  authority. The owner permits archived projects and rechecks authority after
  the read. The Gateway returns the owner's status and body with
  `Cache-Control: no-store`.
- `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/preview`
  accepts `expectedSourceRevision`, `resolvedCommitSha`, and `selectedPath`.
  The response includes validation metadata and a content digest, not the
  pinned manifest or resources.
- `POST /api/v1/projects/{projectId}/skill-marketplaces/sources/{sourceId}/import`
  accepts the same selection plus `idempotencyKey` and
  `expectedContentDigest`. Optional `skillId` and `expectedRevision` select
  an existing revision to replace. The owner reads the pinned files,
  validates them, and records their provenance. Marketplace browse requires
  `expectedSourceRevision`; selected-content preview and import reject query
  parameters.
- Inline Skill preview and import request bodies are limited to 3 MiB.
  The Gateway buffers every explicitly bounded route before contacting its
  owner, including these skill-content routes and requests without
  `Content-Length`. Oversized requests return `413` with
  `request_body_too_large` and are not forwarded.
  Marketplace-selected preview and import use the same request-body limit.

The owner remains authoritative for permission checks, revision validation,
content validation, and persistence. A returned Skill assignment or inventory
record does not prove that a runtime loaded the Skill. The current public API
does not provide actor-safe runtime-loaded status.

## List project runs

Use `GET /api/v1/projects/{projectId}/runs` to list a project's runs without
knowing a run ID. The Gateway forwards the request to the Orchestrator owner
and returns its status and body with `Cache-Control: no-store`.

- `pageSize` defaults to 25 and accepts 1 through 100.
- `state` filters by a comma-separated list of derived states, or by the
  `open` (`idle`, `active`, `blocked`) and `terminal` aliases.
- `cursor` continues from the previous page's `nextCursor`. Send the same
  `state` and `pageSize` values with it.
- Items are ordered by creation time, newest first. Pages are not a snapshot;
  a run created after the first page is not repeated.

The owner checks the caller's current project or tenant read permission with
Projects & Config. A run-bound token sees only its own run. The list reads
persisted run records only. `title` and `goal` are `null` until a run start
request stores them.

## Read project metrics and usage

Send `GET /api/v1/projects/{projectId}/metrics` for run counts and a daily
series. Send `GET /api/v1/projects/{projectId}/usage` for usage grouped by
model and agent. The Gateway forwards these requests to Orchestrator and
Events & Sessions, respectively. It does not aggregate the responses.

Use the same explicit `from` and `to` values for aligned intervals. Bounds
are inclusive and exclusive, respectively, and use UTC. ISO dates and
timestamps with explicit offsets are accepted. If omitted, `to` is now and
`from` is 30 days earlier. The maximum interval is 92 days. Metrics supports
only `bucket=day`; usage does not accept a bucket. Unknown or repeated query
keys return `400`.

Both routes require `api.read`, the owning service and Projects audiences,
and current project or tenant read permission. Run-bound callers see only
their own run. Responses use `Cache-Control: no-store`. Reads do not change
stored data.

Read the [metrics definitions](../reference/api.md#project-metrics-and-usage)
before comparing v1 with the 0.x dashboard. Run success uses completed runs,
not agent dependencies. Usage retains unknown values and separate cost units.
These routes do not price usage or change budgets. Cross-project overview
is out of scope.

## Review project casting

Casting routes use the Projects & Config owner. The Gateway forwards the
validated bearer and optional `X-Agentweaver-Tenant` selector without granting
authority. Proposal reads require current `ReadProjects`; mutations and export
require current `WriteProjects`, including `api.read` and `projects.admin`.
Catalog reads require an authenticated bearer.

1. Read `GET /api/v1/casting/templates` or `GET /api/v1/catalog/roles`.
2. Read the current project configuration revision.
3. Send `POST /api/v1/projects/{projectId}/casting/proposals/scenario` with
   `expectedConfigurationRevision` and `templateId`. For manual selection, use
   `/proposals/manual` with `roleIds`. To supply a complete draft, use
   `/proposals` with `draft`.
4. Review the returned pending proposal. List proposals with `GET /proposals`
   and optional `limit` (1 through 100, default 50). Read or patch one at
   `/proposals/{proposalId}`; patch requires `expectedDraftRevision` and `draft`.
5. Send `POST /proposals/{proposalId}/confirm` or `/reject` with
   `expectedDraftRevision`. Confirmation appends a configuration revision;
   rejection leaves configuration unchanged.

The proposal paths in steps 4 and 5 are relative to
`/api/v1/projects/{projectId}/casting`. A stale draft or base configuration
revision returns `409`. Read the latest state and review it again; do not
automatically confirm a replacement draft. Casting uses revision checks in
request bodies, not an `Idempotency-Key` header.
The owner returns `403` when the caller lacks current `WriteProjects`
authority for a casting write, including confirm, reject, and import.

To transfer a team, read `/casting/export` under the project path, optionally
with a positive `revision`. Send the returned transfer to `/casting/import`
with the target `expectedConfigurationRevision`. Import checks the format and
digest and stages a proposal for review. It does not apply the team. Both the
Gateway and owner limit import request bodies to 1 MiB. The Gateway checks
the complete body before forwarding, including requests without
`Content-Length`. Larger bodies return `413` without contacting the owner.
Transfer version or
team incompatibility returns `422`. These source routes do not establish
deployment or a browser wizard.

## List Remote MCP connections

`GET /api/v1/projects/{projectId}/remote-mcp/connections` lists the project's
Remote MCP connections. The Gateway forwards the request to the Environment
`/api/projects/{projectId}/remote-mcp/directory` route with the bearer token
and tenant selector. It accepts `pageSize` (1 through 100, default 50) and
`after`, the last connection ID of the previous page. Each entry includes the
configuration revision, the configuration SHA-256, the state, the Identity
binding flag, and the current discovery revision and catalog SHA-256. Entries
never include credentials, tokens, or tool schemas. Environment requires
current `ReadProjects` authority. For the full contract, see
[Remote MCP connections](../architecture/remote-mcp-connections.md).

## Send Knowledge writes

The following Knowledge operations require exactly one `Idempotency-Key`
header: `createKnowledgeRecord`, `updateKnowledgeRecord`,
`restoreKnowledgeRecord`, `approveKnowledgeDecision`,
`importKnowledgeRecords`, `promoteKnowledgeProposal`, and
`rejectKnowledgeProposal`. Use a value of 1–128 ASCII letters or digits, or
`.`, `_`, `-`, or `:`. Reuse the same key when retrying the same write.

The key provides idempotency only. It does not establish identity or authorize
an operation. Knowledge rejects missing, duplicate, blank, and invalid keys.
Reads, including `exportKnowledgeRecords`, do not require this header.

## Use the Identity Broker browser routes

The Gateway has explicit browser routes that delegate selected account
operations to `Gateway:Owners:IdentityBrokerAddress`. They are not generic
proxy routes and are not included in the OpenAPI or MCP tool catalog. The
Repo App routes require the compatible optional producer and owner
configuration. The Copilot routes require the Copilot producer. A missing
owner address or incompatible audience returns an owner failure.

GitHub Repo App account authorization and repository discovery use the
validated Broker bearer without a tenant selector:

- `POST /api/auth/github/repo-app/authorizations` starts authorization and
  returns an authorization URL, transaction ID, and expiry.
- `GET /api/auth/github/repo-app/authorization/status` returns safe connection
  status and an opaque connection ID.
- `GET /api/auth/github/repo-app/authorizations/{transactionId}`,
  `POST /api/auth/github/repo-app/authorization/refresh`, and
  `DELETE /api/auth/github/repo-app/authorization` read, refresh, or revoke
  the connection.
- `GET` and `POST /api/github/repository-selections` return redacted
  repository metadata and issue a short-lived selection code.

The browser does not receive provider tokens, numeric installation or
repository IDs, or permission grants. OAuth and installation callbacks are
bearerless browser requests; the Gateway forwards only allow-listed callback
fields and the exact host-only transaction cookie to the owner. It preserves
the owner's `Location` and `Set-Cookie` values. The owner validates and
consumes its persisted, single-use state.

Repo App installation uses the Broker's cookie- and antiforgery-protected
browser endpoint. A callback alone does not prove that the installation
succeeded; refresh Broker status and repository metadata before enabling a
run-bound operation. Run-bound repository pinning remains an ordinary
Gateway/MCP operation. In GitHub App mode, `pinSourceControlRepository`
accepts an optional `selectionCode`; bodyless legacy secret-mode pin requests
retain their existing behavior. Source Control owners validate the accepted
run configuration and exact tenant selector.

The Gateway also delegates the Copilot connection lifecycle at
`/api/connections/copilot-user/v1/{begin,complete,refresh,revoke}` and
`GET /api/connections/copilot-user/v1/{connectionId}`. The begin route sets
the callback cookie. The complete route forwards that exact cookie, the
validated bearer, and any explicitly supplied tenant selector. Gateway and
Identity Broker audiences must already match the admitted client resource;
the Gateway does not exchange or mint another token. These routes are not MCP
tools.

## Connect an MCP client

The separate `Agentweaver.Mcp` host exposes Streamable HTTP at `POST /mcp`.
Its `tools/list` catalog comes from the Gateway's OpenAPI document; `tools/call`
can invoke only the finite routes and schemas in that document. SSE routes and
unsupported request shapes are not MCP tools. Browser consent, repository
discovery, and OAuth callbacks are not in the catalog.

The MCP host validates the Identity Broker issuer, signature, token lifetime,
and configured audience. It then forwards the original bearer token to the
Gateway, which validates the token for its own audience before it forwards the
request to an owner. The owner remains responsible for current authorization.
MCP arguments cannot supply an actor or grant permission.

The MCP host requires the same HTTPS `Identity:Issuer` and
`Identity:Audience` settings, plus `Gateway:BaseAddress` as an HTTPS service
root. Tool arguments cannot select a different Gateway URL. The host publishes
RFC 9728 protected-resource metadata at
`/.well-known/oauth-protected-resource/mcp`; unauthenticated `/mcp` requests
identify that metadata endpoint so clients can discover the authorization
server.

Before dispatch, MCP validates arguments against the resolved OpenAPI schema.
Catalog retrieval and each tool call have a 10-second deadline that includes
reading the response body. Caller cancellation is propagated. The MCP request
envelope can add up to 64 KiB beyond the Gateway request-body limit.

## Read and resume run events

Call `GET /api/v1/projects/{projectId}/runs/{runId}/events/live` to receive
events from the Events owner's committed journal. The Gateway checks current
read authority for the exact signed project and run before it starts, after
each page read, and before each subsequent event. It also checks token expiry
immediately before writing an event. If the caller's authority is revoked or
the token expires after the SSE response starts, the Gateway terminates the
stream instead of writing a JSON error as event data.

Each SSE event ID is the journal's `nextCursor`. Reconnect by sending
`Last-Event-ID`; the `cursor` query parameter is also supported. The Gateway
rejects duplicate or conflicting cursor values. Use
`GET /api/v1/projects/{projectId}/runs/{runId}/events?cursor={cursor}&limit={limit}`
for bounded journal replay.

The Gateway preserves the owner's status and response body. A `202 Accepted`
response means that the owner accepted the request; it does not confirm that an
asynchronous effect completed.

## Understand response limits

The Gateway returns `401` when its bearer token is missing, invalid, or
expired. It forwards owner statuses and structured error bodies. An
unavailable or redirected owner, invalid owner contract, or bounded-response
violation returns `502`; an owner request timeout returns `504`.

Gateway request bodies are limited to 64 KiB by default. The Knowledge import
endpoint accepts up to 1 MiB. Inline Skill preview and import each accept up to
3 MiB. The MCP JSON-RPC envelope adds a separate 64 KiB allowance.

The Gateway does not expose journal object-content reads or run-produced-file
browsing in v1. The durable run-bound artifact manifest and object-version
producer has not been admitted, so neither the Gateway nor the retained Web
client claims host-file access or parity with older monolithic file routes.

## Build and test the host

From the repository root, build the Gateway:

```powershell
dotnet build services\gateway\Agentweaver.Gateway\Agentweaver.Gateway.csproj --no-restore --configuration Release
```

The build compiles the checked-in host; it does not publish or deploy it.
After a Release build, run the focused Broker integration test:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite
```

The test uses a Broker-issued token and the live Projects/PostgreSQL
authorization boundary with controlled owner transports. It checks owner
status forwarding, cursor replay, and authorization before SSE writes. It does
not prove that Gateway or owner services are deployed.

## Expected result

The Gateway validates the configured issuer, signature, lifetime, and
audience, then forwards only the supported route to its fixed owner. The owner
checks current authority and returns its contract response. A `202 Accepted`
response means accepted, not completed.
