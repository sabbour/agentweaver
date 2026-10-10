# Gateway/BFF

`Agentweaver.Gateway` is the source-owned .NET 10 HTTP entry for browser, CLI, and
first-party MCP clients. It is an unpublished service candidate: this guide describes
the checked-in host and does not claim a deployment, public endpoint, image, or live
environment.

## Discovery and routing

The versioned API is rooted at `/api/v1`. `GET /openapi/v1.json` describes every
finite route, including its JSON request and response schemas, owner, parameters,
bearer security, response semantics, and the Events SSE media type. The Gateway maps
Projects & Config, Orchestrator, Knowledge, and Events routes. It is not an arbitrary
upstream proxy and callers cannot choose owner URLs or owner identity headers.

The Gateway validates the Identity Broker issuer, token signature, lifetime, and
configured audience before forwarding the original bearer token to the owning API.
`X-Agentweaver-Tenant`, when supplied, is forwarded as a selector for owner-side
validation; it is not an identity or role claim. The owner remains authoritative for
current project membership, role, binding, and operation-specific checks.

### Browser CORS

Set the required `Gateway:WebOrigin` to the Web application's exact HTTPS origin,
for example `https://web.example.com`. It must be a service root, not a wildcard,
path, or URI containing credentials, query, or fragment. The Gateway processes
preflight requests before authentication and applies exact-origin, route-specific
method and header allowlists; it does not use a global permissive CORS policy.
Versioned API, OpenAPI, and run-bound GitHub App installation policies do not allow
credentials. The Copilot connection BFF is the sole Gateway exception: its exact
origin policy allows credentials for its callback cookie and only `GET`/`POST`
with `Authorization`, `Accept`, `Content-Type`, and `X-Agentweaver-Tenant`.
The direct Identity Broker Repo App browser endpoints have their own separate,
credentialed CORS boundary and allowlist.

The `createKnowledgeRecord`, `updateKnowledgeRecord`, `restoreKnowledgeRecord`,
`approveKnowledgeDecision`, `importKnowledgeRecords`, `promoteKnowledgeProposal`,
and `rejectKnowledgeProposal` operations require exactly one `Idempotency-Key`
string header. Its value must be 1–128 ASCII letters or digits, or `.`, `_`, `-`,
or `:`. Reuse the same key when retrying the same write. The key is only for
idempotency; it does not establish identity or approve an operation. Knowledge
rejects missing, duplicate, blank, and invalid values. Reads, including
`exportKnowledgeRecords`, and other Gateway operations do not require this header.
Gateway request bodies are limited to 64 KiB by default; the Knowledge import
endpoint alone allows up to 1 MiB. MCP allows an additional 64 KiB for the
JSON-RPC envelope.

Responses preserve the owner's status and body. In particular, `202 Accepted` means
only that the owner accepted work; it is not proof that an asynchronous effect
completed. A missing, invalid, or expired Gateway token returns `401`. Owner status
and structured error bodies pass through; an unavailable or redirected owner, invalid
owner contract, or bounded-response violation returns `502`, and a finite owner
request timeout returns `504`.

### Authorization context and selector forwarding

`GET /api/v1/authorization/context` delegates to the existing Projects & Config
`GET /api/authorization/context` owner route. It accepts no query parameters,
forwards the validated Broker bearer and optional `X-Agentweaver-Tenant` selector,
and requires the owner's `api.read`, non-purpose context contract. The Gateway
validates the response against the exact versioned JSON contract and returns it
with `Cache-Control: no-store`; redirects, malformed or duplicate contract fields,
wrong issuer, actor, signed project/run binding, explicit tenant selector, or
cacheable responses are rejected rather than passed through.
The result reports the current actor, selected tenant and membership revision,
optional project/run bindings, and effective authority. It is advisory context for
the client, not a permission grant or a cacheable authorization decision.

The Web client supplies that selector only on the allow-listed Projects/configuration,
run Coordination, Knowledge, run Selection/Usage, and finite journal replay calls.
The live event SSE route and unrelated APIs do not receive it. Context and selector
forwarding do not replace the owning service's authorization check.

## Identity Broker browser BFF

The Gateway also exposes explicit, non-OpenAPI routes to the configured
`Gateway:Owners:IdentityBrokerAddress`. These routes are not generic proxy
operations and are not included in the MCP tool catalog.
The Repo App source routes were released in #1934; runtime availability still
depends on the optional producer and owner configuration. Copilot routes require
the #1906 producer. A missing owner address or incompatible audience is surfaced
as an explicit owner failure.

```mermaid
sequenceDiagram
    actor User
    participant Web as Retained v1 web
    participant Gateway
    participant Identity as Identity Broker
    participant Orchestrator as Source Control owner
    participant GitHub
    User->>Web: Connect Repo App
    Web->>Gateway: POST authorization (current bearer)
    Gateway->>Identity: Same bearer; no tenant selector
    Identity-->>Gateway: Authorization URL + host-only callback cookie
    Gateway-->>Web: URL + unchanged Set-Cookie
    Web->>GitHub: Provider consent in popup
    GitHub-->>Gateway: Browser callback + exact host-only cookie
    Gateway->>Identity: Allow-listed query + named cookie
    Identity-->>Gateway: 302 + unchanged Location and clearing Set-Cookie
    Gateway-->>Web: Allow-listed callback outcome
    Web->>Gateway: GET status / repository metadata (current bearer)
    Gateway->>Identity: Same bearer; no tenant selector
    Identity-->>Web: Opaque connection ID + safe metadata
    Web->>Identity: POST /auth/github/repo-app/install (CSRF + session)
    Identity-->>Web: 302 + host-only installation callback cookie
    Web->>GitHub: Install App
    GitHub-->>Gateway: Installation callback + exact host-only cookie
    Gateway->>Identity: Allow-listed query + named cookie
    Identity-->>Web: 302 to configured Repo App callback page
    Web->>Identity: Refresh status / repository metadata (current bearer)
    Identity-->>Web: Broker-confirmed connection and repositories
    Web->>Gateway: POST existing run-bound pin {selectionCode}
    Gateway->>Orchestrator: Same bearer + tenant
```

User-level GitHub Repo App authorization and repository discovery require the
current validated Broker bearer and no tenant selector:

- `POST /api/auth/github/repo-app/authorizations` begins OAuth and returns an
  authorization URL, transaction ID, and expiry.
- `GET /api/auth/github/repo-app/authorization/status` returns connection
  status, GitHub login, and the stable opaque Identity `connectionId`.
- `GET /api/auth/github/repo-app/authorizations/{transactionId}` reads the
  transaction status; `POST /api/auth/github/repo-app/authorization/refresh`
  refreshes owner-held credentials; `DELETE /api/auth/github/repo-app/authorization`
  revokes the connection.
- `GET` and `POST /api/github/repository-selections` list redacted repository
  and installation metadata and issue a short-lived opaque selection code for
  a caller-selected full repository name.

The browser never receives provider tokens, numeric installation/repository IDs,
or permission grants. OAuth callbacks are bearerless `GET` requests to
`/auth/github/repo-app/callback` with only `code`, `state`, and `error`; the
installation callback is `/auth/github/repo-app/installation/callback` with
only `installation_id`, `setup_action`, and `state`. Each callback forwards only
its exact `__Host-agentweaver-repo-app-auth` or
`__Host-agentweaver-repo-app-install-auth` cookie, preserves the owner's
`Location` and every `Set-Cookie` value, and trusts the owner to consume its
persisted single-use state and redirect only to a configured allow-listed route.

Installation setup is a cookie-and-CSRF-protected browser POST to the
Identity Broker's `/auth/github/repo-app/install` endpoint. The Broker owns
the installation transaction and callback cookie. The callback alone does not
confirm access; the browser refreshes Broker status and repository metadata
before enabling run-bound repository operations. User-level
authorization/discovery and callbacks are not MCP tools. Run-bound repository operations remain ordinary Gateway/MCP operations;
`pinSourceControlRepository` accepts an optional `{ "selectionCode": "..." }`
body for GitHub App mode and keeps its existing response and status behavior.
Omitting the body preserves legacy secret-mode pin requests. Source Control
operations require the exact tenant selector; the owner checks the accepted
run configuration and binds the selected repository server-side.

Copilot browser consent follows the same original-bearer rule at
`/api/connections/copilot-user/v1/{begin,complete,refresh,revoke}` and
`GET /api/connections/copilot-user/v1/{connectionId}`. Begin sets only the
`__Host-agentweaver-copilot-link` callback cookie; complete forwards only that
cookie with the unchanged validated bearer and optional tenant selector.
Unlike the selector-free Repo App browser BFF, each Copilot lifecycle route
forwards an explicitly supplied tenant selector unchanged. Gateway and
Identity Broker audiences must already match the admitted client resource; the
Gateway does not exchange or mint a different identity token. Where Projects &
Core uses a separate resource, that same client token must also carry its
existing Projects API resource. These lifecycle routes are excluded from MCP
tools.

## First-party MCP client

`Agentweaver.Mcp` exposes native Streamable HTTP at `POST /mcp`. Its `tools/list`
catalog is built from the live Gateway `GET /openapi/v1.json` document; `tools/call`
only invokes those finite routes and their declared schemas. SSE routes and
unsupported request shapes are not exposed as tools.
User-level consent, repository discovery/selection, and browser callbacks are
not in that OpenAPI document and cannot be called through MCP.

The MCP host validates the Identity Broker issuer, signature, lifetime, and its
configured audience using OpenIddict. It publishes RFC 9728 protected-resource
metadata with `resource` set to `Identity:Audience` and
`authorization_servers` set to `Identity:Issuer`. For an audience such as
`https://api.example/mcp`, the metadata URL is
`https://api.example/.well-known/oauth-protected-resource/mcp`; unauthenticated
requests to `/mcp` receive a Bearer challenge pointing to that URL. The metadata
endpoint is public so clients can discover the authorization server.

After validation, the MCP host forwards the original bearer token to the Gateway.
Gateway validates it again for its own configured audience and the owner remains
responsible for current membership, roles, purpose bindings, and operation
authorization. MCP arguments cannot supply an actor or grant permission. Owner
statuses and response bodies remain visible in tool results; an owner rejection,
HTTP error, or unavailable Gateway/owner is an MCP tool error, and acceptance does
not claim asynchronous completion.

Before dispatch, MCP validates arguments against the resolved OpenAPI schema
advertised by `tools/list`, including required properties, types, enums, unions,
array items, and declared string constraints. Catalog retrieval and each tool call
have a 10-second deadline that covers response-body reads after HTTP headers arrive.
Caller cancellation is propagated; deadline expiry and Gateway unavailability are
reported as explicit MCP errors.

The MCP host requires the same HTTPS `Identity:Issuer` and `Identity:Audience`
settings as other resource APIs plus `Gateway:BaseAddress` as an HTTPS service
root. The Gateway URL is not selected by MCP tool arguments.

## Run event stream

`GET /api/v1/projects/{projectId}/runs/{runId}/events/live` reads one event at a time
from the Events owner's committed run journal. It checks current Projects & Config
read authority for the exact signed project/run before loading the first page and
again after each page has loaded but before writing an event. It repeats that
check before every later event. Immediately before each write, it also checks that
the validated bearer token has not expired while an owner request was in progress.
A revoked or expired caller therefore receives no subsequent event; if an SSE
connection has already started, the Gateway terminates it rather than sending a
misleading JSON error as an event.

Each SSE `id` is the journal's `nextCursor`, unchanged. Reconnect with
`Last-Event-ID`; the `cursor` query parameter is also supported, but duplicate or
disagreeing cursor sources are rejected. `GET
/api/v1/projects/{projectId}/runs/{runId}/events?cursor={cursor}&limit={limit}`
exposes bounded journal replay through the same Events owner.

The current run-bound Projects probe is
`GET /api/projects/{projectId}?runId={runId}`. It requires the original Broker
token's exact project and run bindings, no token purpose, and fresh current
`ReadProjects` authority. Only one `runId` query parameter is accepted, and the
response is `Cache-Control: no-store`. This adds no role or write permission.

The OpenAPI document uses concrete component schemas for the delegated owner
contracts. It preserves owner-defined response status codes and bodies; it does not
replace owner validation or error handling. An Events transport failure before the
SSE response starts returns a structured `502` problem. If the response has started,
the Gateway aborts the stream instead of appending a JSON problem to event data.

## Configuration and local checks

The host requires HTTPS `Identity:Issuer`, `Identity:Audience`, and service-root
addresses at `Gateway:Owners:Projects`, `Gateway:Owners:Orchestrator`,
`Gateway:Owners:Knowledge`, `Gateway:Owners:Events`, and
`Gateway:Owners:IdentityBrokerAddress`. Set
`Gateway:OwnerRequestTimeoutSeconds` to a finite value from 1 to 120; the default is
15 seconds. Redirects and insecure or non-root owner addresses are rejected.

Run produced-file list, diff, and content reads are not yet mapped in v1. P2 #1917
owns the durable run-bound manifest and object-version references; until that
producer is admitted, the Gateway and retained UI do not proxy host files or claim
produced-file browsing parity.

From the repository root, build the host with
`dotnet build services\gateway\Agentweaver.Gateway\Agentweaver.Gateway.csproj
--no-restore --configuration Release`. The Broker integration suite exercises the
real Broker-issued token and live Projects/PostgreSQL authorization boundary with
controlled owner transports:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite
```
