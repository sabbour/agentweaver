# Gateway

Use this page to understand how the Gateway exposes Agentweaver's owner services
to the browser and first-party MCP server. The Gateway is a backend-for-frontend
(BFF), not a workflow engine: the owner still decides whether a caller has
current permission and whether a requested transition is valid.

## How Gateway routes requests

Gateway authenticates the caller, selects a configured owner, forwards the
request, and returns the owner's result. A finite route catalog maps public
`/api/v1` operations to Projects & Config, Orchestrator, Knowledge, Events, or
Identity Broker.

| Source | Responsibility |
| --- | --- |
| `GatewayRouteCatalog.cs` | Maps public routes to owner routes and forwarding rules |
| `GatewayOptions.cs` | Configures fixed HTTPS owner roots, web origin, audience, issuer, and timeout |
| `GatewayOwnerClient.cs` | Forwards the caller request, bounds responses, and handles server-sent events (SSE) |
| `GatewayEndpoints.cs` | Implements health, discovery, BFF connection flows, and fallback behavior |
| `GatewayOpenApi.cs` | Builds OpenAPI from the route catalog |
| `GatewayRunEventProjection.cs` | Validates public event pages and envelopes |
| `GatewayWebCors.cs` | Applies exact-origin, route-specific browser policies |
| `Program.cs` | Composes authentication, clients, CORS, telemetry, and routes |

The browser callers and Broker session handling live in
`apps/web/src/v1/api.ts` and `AuthContext.tsx`.

## Understand the route boundary

The route catalog declares each operation's method, owner path, operation ID,
body behavior, and forwarding flags. Callers cannot choose an upstream URL.
Unknown versioned paths reach the authenticated `route_not_found` fallback
instead of an arbitrary proxy.

`GET /openapi/v1.json` publishes this finite API contract. The MCP service uses
it to build its tool catalog. Health and OpenAPI discovery are anonymous;
product operations require authentication.

OpenIddict validates the configured Identity Broker issuer and audience. Before
forwarding, the proxy requires one current validated bearer and forwards it
unchanged. `X-Agentweaver-Tenant` selects a tenant for owner validation; it is
not a role or identity assertion. Supported idempotency and revision headers
are forwarded, not interpreted as approval.

Gateway preserves the owner's response status and body. In particular,
`202 Accepted` means an owner accepted an operation; it does not prove that a
resource, model turn, or workflow finished. Gateway has no domain database and
does not reconstruct current state from a cached response.

## Follow a catalog request

Before calling a product route, obtain a valid Broker bearer and use a catalog
operation with its declared method, path, and body.

1. Routing selects the finite endpoint and browser CORS policy.
2. Authentication and authorization run before the owner call.
3. `GatewayOwnerClient` expands and escapes route values against the saved owner
   path.
4. The client forwards the original bearer, allowed selectors and headers,
   query string, and declared JSON body.
5. A per-request deadline bounds the owner call and response read.
6. The proxy returns the owner's status, supported headers, and bounded body
   with `Cache-Control: no-store`.

The default request-body limit is 64 KiB. Catalog entries override it for
specific operations, including skill content and Knowledge import. Ordinary
owner responses are limited to 8 MiB, and redirects are rejected. See the
[Gateway/BFF guide](../guide/gateway.md) for operation-specific limits.

`GET /api/v1/authorization/context` uses a dedicated path. Gateway validates
the Projects response's no-store header, exact contract shape, caller scope,
and confirmed tenant context. It does not create membership or derive
permissions from a selector.

## Understand sign-in and connection flows

Identity Broker owns browser sign-in. Gateway's explicit BFF routes support
GitHub Repo App, Hosted Copilot, and remote MCP connection management. These
fixed routes are not general callback proxies. Their forwarders enforce the
relevant query, cookie, tenant-selector, and response contracts.

A connection reference identifies a connection; it is not a provider
credential. For browser requests, `Gateway:WebOrigin` identifies one exact
HTTPS origin. The Copilot connection BFF uses a credentialed cookie policy;
ordinary versioned API policies do not enable ambient credentials.

## Follow a journal-backed event stream

The public SSE path reads committed Events pages rather than forwarding an
unverified upstream stream. You need current run-bound project read access and
an opaque journal cursor if you are reconnecting.

1. Gateway reads the cursor from `cursor` or `Last-Event-ID`. If both are
   present, their values must match.
2. It checks current run-bound project read access through Projects.
3. It reads and validates a bounded Events page.
4. It checks project access again before each event write.
5. It emits `event: session-event`, the opaque cursor as `id`, and the
   projected JSON envelope as `data`.
6. When no event is available, it flushes a keep-alive and polls again.

The projection checks event schema, ordering, references, and cursor progress.
It excludes private runtime configuration and remote-tool result material.
Reconnect resumes journal history; it does not trigger model execution. The
configured owner timeout applies to each owner request, not to the entire
long-lived SSE connection.

## Diagnose failures

| Condition | Gateway behavior |
| --- | --- |
| Missing, invalid, or expired bearer | Returns `401` |
| Owner rejects permission or a state transition | Preserves the owner's status and body |
| Owner redirects or is unavailable | Returns `502` |
| Owner response violates its validated contract or size bound | Returns `502`; aborts if the response already started |
| Owner request reaches its deadline | Returns `504` |
| Invalid reconnect cursor | Returns `400` before streaming starts |
| Authority or owner access fails during SSE | Aborts the stream to prevent further disclosure |

Retry or reconnect according to the owner contract. Gateway does not retry a
mutation as a new operation or infer success after a broken connection.

## Related content

- [System overview](./00-system-overview.md) places Gateway in the request flow.
- [MCP server](./mcp-server.md) explains its OpenAPI-based client.
- [Events and sessions](./events-sessions.md) explains replay and private material.
- [Gateway/BFF guide](../guide/gateway.md), [web client guide](../guide/web-client.md),
  [runtime configuration reference](../reference/runtime-configuration.md), and
  [contracts and endpoints](../reference/contracts.md) provide configuration and
  API details.
