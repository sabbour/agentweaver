# Gateway

## The mental model

Gateway is the backend-for-frontend (BFF), not another workflow engine. It gives
the browser and first-party MCP service a finite public API over service-owned
operations.

Gateway authenticates the caller, selects the configured owner, forwards the
request, and returns the owner's result. The owner still decides whether the
caller has current permission and whether the requested transition is valid.

## Where this lives

- `services/gateway/Agentweaver.Gateway/Program.cs` — Authentication, HTTP clients,
  CORS, telemetry, and route composition.
- `services/gateway/Agentweaver.Gateway/GatewayOptions.cs` — Fixed HTTPS owner
  roots, web origin, audience, issuer, and request timeout.
- `services/gateway/Agentweaver.Gateway/GatewayRouteCatalog.cs` — Public route to
  owner-route mapping and forwarding flags.
- `services/gateway/Agentweaver.Gateway/GatewayEndpoints.cs` — Health, discovery,
  BFF connection flows, catalog mapping, and API fallback.
- `services/gateway/Agentweaver.Gateway/GatewayOwnerClient.cs` — Caller-bound
  proxying, bounded responses, authorization context, and SSE.
- `services/gateway/Agentweaver.Gateway/GatewayOpenApi.cs` — OpenAPI contracts
  derived from the route catalog.
- `services/gateway/Agentweaver.Gateway/GatewayRunEventProjection.cs` — Public
  event-page and envelope validation.
- `services/gateway/Agentweaver.Gateway/GatewayWebCors.cs` — Exact-origin,
  route-specific browser policies.
- `apps/web/src/v1/api.ts` and `AuthContext.tsx` — Browser callers and Broker
  session handling.

## Core concepts and invariants

### Routes are an allowlist

`GatewayRouteCatalog` maps `/api/v1` operations to Projects & Config,
Orchestrator, Knowledge, Events, or Identity Broker. Each entry declares its
method, owner path, operation ID, body behavior, and forwarding flags.

`GatewayOptions` supplies the owner addresses. Callers cannot select an upstream
URL. Unknown versioned paths reach the authenticated `route_not_found` fallback
instead of an arbitrary proxy.

`GET /openapi/v1.json` publishes the finite contract. The MCP service uses this
document to build its tool catalog. Health and OpenAPI discovery are anonymous;
product operations require authentication.

### Forwarding a bearer does not replace owner authorization

OpenIddict validates the configured Broker issuer and audience. The proxy also
requires one current validated bearer before forwarding it unchanged.

`X-Agentweaver-Tenant` selects a tenant for owner validation on routes that
forward it. It is not a role or identity assertion. The proxy forwards the
supported idempotency and revision headers without interpreting them as approval.

### Acceptance and completion stay separate

Gateway preserves owner response status and body. An owner returning
`202 Accepted` has accepted an operation, not established that its resource,
model turn, or workflow finished.

Gateway has no domain database. Current state stays with each owner rather than
being reconstructed from a cached BFF result.

## Request lifecycle

For a normal catalog route:

1. Routing selects the finite endpoint and browser CORS policy.
2. Authentication and authorization run before the owner call.
3. `GatewayOwnerClient` expands and escapes route values against the saved
   owner path.
4. It forwards the original bearer, allowed selectors and headers, query string,
   and declared JSON body.
5. A per-request deadline bounds the owner call and response read.
6. The proxy returns the owner's status, supported headers, and bounded body
   with `Cache-Control: no-store`.

Requests use a 64-KiB default body limit. Catalog entries override that bound for
specific operations, including skill content and Knowledge import. The proxy
limits ordinary owner responses to 8 MiB and rejects redirects. Use
[Gateway/BFF](../guide/gateway.md) for the operation-specific bounds.

`GET /api/v1/authorization/context` has a dedicated path. Gateway validates the
Projects owner's no-store response, exact contract shape, caller scope, and
confirmed tenant context before returning it. It does not create membership or
derive permissions from a selector.

## Authentication and connection flows

The browser's sign-in protocol belongs to Identity Broker. Gateway's explicit
BFF routes support the GitHub Repo App, Hosted Copilot connection, and remote MCP
connection management flows.

These are fixed routes to Identity, not general callback proxying. Their
forwarders apply the relevant query, cookie, tenant-selector, and response
contracts. A connection reference remains a reference; Gateway does not turn it
into a provider credential.

For browser calls, `Gateway:WebOrigin` identifies one exact HTTPS origin.
`GatewayWebCors` applies route-specific methods and headers before
authentication. The Copilot connection BFF has a credentialed cookie policy;
ordinary versioned API policies do not enable ambient credentials.

## Journal-backed event streaming

The public SSE implementation reads committed Events pages instead of forwarding
an unverified upstream stream:

1. Read the reconnect cursor from `cursor` or `Last-Event-ID`. If both are
   supplied, require identical values.
2. Check current run-bound project read access through Projects.
3. Read and validate a bounded Events page.
4. Check current project access again before each event write.
5. Emit `event: session-event`, the opaque cursor as `id`, and the projected
   JSON envelope as `data`.
6. When no event is available, flush a keep-alive and poll again.

The projection validates the event schema, ordering, references, and cursor
progress. It excludes private runtime configuration and remote-tool result
material. Reconnect resumes journal history; it does not trigger model execution.

The configured owner timeout applies to each owner request, not the entire
long-lived SSE connection.

## Failure behavior

| Condition | Gateway behavior |
| --- | --- |
| Missing, invalid, or expired bearer | Returns `401`. |
| Owner rejects permission or a state transition | Preserves the owner's error status and body. |
| Owner redirects or is unavailable | Returns `502`. |
| Owner response violates the validated contract or response bound | Returns `502`; aborts if the response already started. |
| Owner request reaches its deadline | Returns `504`. |
| Invalid reconnect cursor | Returns `400` before streaming starts. |
| Authority or owner access fails during SSE | Aborts the stream rather than disclosing more events. |

The caller reconnects or retries against the owner contract. Gateway does not
retry a mutation as a new operation or infer success after a broken connection.

## Continue reading

- [System overview](./00-system-overview.md) places Gateway in the request flow.
- [MCP server](./mcp-server.md) explains its OpenAPI-based client.
- [Events and sessions](./events-sessions.md) explains replay and private material.
- [Gateway/BFF guide](../guide/gateway.md), [web client guide](../guide/web-client.md),
  and [contracts and endpoints](../reference/contracts.md) provide configuration
  and API details.
