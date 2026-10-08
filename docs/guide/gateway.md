# Gateway/BFF

`Agentweaver.Gateway` is the source-owned .NET 10 HTTP entry for browser, CLI, and
first-party MCP clients. It is an unpublished service candidate: this guide describes
the checked-in host and does not claim a deployment, public endpoint, image, or live
environment.

## Discovery and routing

The versioned API is rooted at `/api/v1`. `GET /openapi/v1.json` is the live route
catalog; it identifies each owner, route parameters, bearer security, response
semantics, and the Events SSE media type. The Gateway maps a finite set of Projects &
Config, Orchestrator, Knowledge, and Events routes. It is not an arbitrary upstream
proxy and callers cannot choose owner URLs or owner identity headers.

The Gateway validates the Identity Broker issuer, token signature, lifetime, and
configured audience before forwarding the original bearer token to the owning API.
`X-Agentweaver-Tenant`, when supplied, is forwarded as a selector for owner-side
validation; it is not an identity or role claim. The owner remains authoritative for
current project membership, role, binding, and operation-specific checks.

Responses preserve the owner's status and body. In particular, `202 Accepted` means
only that the owner accepted work; it is not proof that an asynchronous effect
completed. A missing, invalid, or expired Gateway token returns `401`. Owner status
and structured error bodies pass through; an unavailable or redirected owner, invalid
owner contract, or bounded-response violation returns `502`, and a finite owner
request timeout returns `504`.

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
