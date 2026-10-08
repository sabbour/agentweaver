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

The `createKnowledgeRecord`, `updateKnowledgeRecord`, `restoreKnowledgeRecord`,
`approveKnowledgeDecision`, `importKnowledgeRecords`, `promoteKnowledgeProposal`,
and `rejectKnowledgeProposal` operations require exactly one `Idempotency-Key`
string header. Its value must be 1–128 ASCII letters or digits, or `.`, `_`, `-`,
or `:`. Reuse the same key when retrying the same write. The key is only for
idempotency; it does not establish identity or approve an operation. Knowledge
rejects missing, duplicate, blank, and invalid values. Reads, including
`exportKnowledgeRecords`, and other Gateway operations do not require this header.
Knowledge import bundles are limited to 1 MiB; MCP allows an additional 64 KiB for
the JSON-RPC envelope.

Responses preserve the owner's status and body. In particular, `202 Accepted` means
only that the owner accepted work; it is not proof that an asynchronous effect
completed. A missing, invalid, or expired Gateway token returns `401`. Owner status
and structured error bodies pass through; an unavailable or redirected owner, invalid
owner contract, or bounded-response violation returns `502`, and a finite owner
request timeout returns `504`.

## First-party MCP client

`Agentweaver.Mcp` exposes native Streamable HTTP at `POST /mcp`. Its `tools/list`
catalog is built from the live Gateway `GET /openapi/v1.json` document; `tools/call`
only invokes those finite routes and their declared schemas. SSE routes and
unsupported request shapes are not exposed as tools.

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
`Gateway:Owners:Knowledge`, and `Gateway:Owners:Events`. Set
`Gateway:OwnerRequestTimeoutSeconds` to a finite value from 1 to 120; the default is
15 seconds. Redirects and insecure or non-root owner addresses are rejected.

From the repository root, build the host with
`dotnet build services\gateway\Agentweaver.Gateway\Agentweaver.Gateway.csproj
--no-restore --configuration Release`. The Broker integration suite exercises the
real Broker-issued token and live Projects/PostgreSQL authorization boundary with
controlled owner transports:

```powershell
dotnet test tests\Agentweaver.Identity.Broker.Tests\Agentweaver.Identity.Broker.Tests.csproj --no-build --no-restore --configuration Release --filter FullyQualifiedName~GatewayDelegatesOwnerStatusesReplaysCursorsAndReauthorizesBeforeSseWrite
```
