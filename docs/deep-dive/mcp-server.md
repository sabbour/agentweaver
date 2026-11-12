# Retained MCP compatibility adapter

## 1–4. Goals, constraints, context and strategy

Agentweaver does not offer a supported product Model Context Protocol
(MCP) server or MCP acceptance surface. Supported client paths are Gateway/API,
browser and native .NET CLI. This page explains the retained adapter under
`services/mcp/Agentweaver.Mcp/` so contributors can understand its boundaries;
it is not a connection or installation procedure.

The checked-in release/Kustomize composition still includes MCP image,
workload and ingress resources. Their presence does not establish supported
product availability. This documentation does not remove or deploy them.

The adapter translates finite Gateway operations without keeping another
workflow state machine. Discovery does not grant authority. It is separate
from [outbound remote MCP connections](../architecture/remote-mcp-connections.md)
used by agents.

## 5. Building blocks

### How the adapter exposes tools

An MCP client authenticates with Identity Broker. The MCP host validates the
token and tool arguments, then forwards the same bearer to Gateway. Gateway
routes the operation to its owner, which checks current authority and state.

| Source | Responsibility |
| --- | --- |
| `GatewayToolCatalog.cs` | Loads Gateway OpenAPI, admits operations, and builds tool names and schemas |
| `GatewayJsonSchemaValidator.cs` | Validates advertised argument schemas |
| `McpToolHandlers.cs` | Discovers tools, builds requests, and maps results and errors |
| `GatewayApiClient.cs` | Preserves the caller bearer, restricts paths, and forwards supported headers |
| `RuntimeOwnerHttpTransport.CreateTrustedOwnerHandler()` | Validates internal owner TLS using the mounted backend CA |
| `McpServiceOptions.cs` | Configures Broker issuer/audience, Gateway root, metadata path, and timeout |
| `Program.cs` | Composes the stateless HTTP transport, authentication, health routes, and handlers |

The tool catalog's authoritative API definitions come from Gateway's
`GatewayRouteCatalog.cs` and `GatewayOpenApi.cs`.

MCP uses the shared owner transport for its internal Gateway calls. The handler
trusts only the read-only `service-backend-ca` mount and checks the owner
hostname and server-authentication purpose. Broker issuer discovery continues
to use normal system trust.

## 6. Runtime view

### Understand discovery and inputs

`GatewayToolCatalog` fetches `GET /openapi/v1.json`, a machine-readable OpenAPI
description of Gateway's routes, and accepts the supported 3.1.0 shape. It
limits the document to 2 MiB and admits supported
operations under the versioned API root. Tools are named
`agentweaver_` plus the operation ID; for example, `listProjects` becomes
`agentweaver_listProjects`.

The catalog includes each operation's summary, owner description, parameters,
body schema, and accepted-only marker. It skips SSE operations and unsupported
HTTP methods. It rejects duplicate names, unsupported headers, invalid schemas,
and an empty catalog. After a successful load, the catalog instance caches the
operations.

Path and query parameters become tool inputs. The supported header inputs are:

| HTTP header | MCP input |
| --- | --- |
| `X-Agentweaver-Tenant` | `tenantSelector` |
| `Idempotency-Key` | `idempotencyKey` |

A JSON body becomes `body`; required API parameters stay required. The generated
object schema rejects extra top-level properties. It does not accept a
caller-selected owner URL, actor identity, or arbitrary authorization header.

### Retained call path

In the compatibility implementation, a call requires a valid Broker bearer
and access to the configured HTTPS Gateway. This describes the source path,
not a supported product-use instruction.

1. The client connects to authenticated `/mcp` over the stateless HTTP
   transport.
2. `tools/list` loads the Gateway catalog and returns the admitted schemas.
3. `tools/call` resolves the discovered tool name and validates its arguments.
4. The handler escapes path values, serializes query values, and selects the
   declared body and header inputs.
5. `GatewayApiClient` validates the current bearer and restricts the request to
   the configured Gateway's `/api/v1/` paths.
6. Gateway routes the request to its owner.
7. The MCP handler reads at most 8 MiB and returns the structured owner result
   with text content.

Call deadlines cover catalog loading, the Gateway request, and body reading.
`McpServiceOptions` sets a ten-second Gateway request timeout. Caller
cancellation propagates; it does not become a successful tool response.

The request builder rejects absolute URLs, traversal segments, escaped path
separators, and fragments. HTTP redirects are disabled. The MCP service never
substitutes a service bearer for the caller.

### Interpret results correctly

An accepted-only operation or HTTP `202` produces a message that acceptance is
not completion. If the owner returns `accepted: false`, the tool reports an
error even when the HTTP status is successful. To learn the final state after
an accepted operation, call the owner's read operation. The catalog does not
expose the SSE stream as a long-running tool.

### Authentication and transport contract

`Identity:Issuer`, `Identity:Audience`, and `Gateway:BaseAddress` are explicit
HTTPS settings. The Gateway address must identify the service root.

The host publishes OAuth protected-resource metadata at the path derived from
its configured audience. An unauthorized `/mcp` response advertises that
metadata through `WWW-Authenticate`.

Gateway TLS validation uses the mounted backend CA, checks the server name, and
requires server-auth certificate usage. Forwarded certificate headers do not
establish trust. `/health/live` and `/health/ready` are anonymous host checks;
they do not fetch the catalog or prove that Gateway or an owner can complete a
call.

For supported settings, see the
[runtime configuration reference](../reference/runtime-configuration.md) and
[Gateway/BFF guide](../guide/gateway.md).

### Failure mapping

| Condition | Tool error |
| --- | --- |
| Name is absent from the admitted catalog | `unknown_tool` |
| Arguments do not match the advertised schema | `invalid_arguments` |
| Current MCP bearer is unavailable | `unauthenticated` |
| Gateway or catalog request reaches its deadline | `gateway_timeout` |
| Gateway or catalog cannot be reached | `gateway_unavailable` |
| Catalog shape is invalid | `gateway_contract_invalid` |
| Owner response exceeds the size bound | `gateway_response_too_large` |

Owner HTTP errors retain their result details; they do not become fabricated
completion.

## 7–9. Deployment, shared concepts and decisions

The source composition includes an MCP compatibility workload. Its health
routes do not load Gateway's catalog or prove owner availability. The
[deployment view](./infra-deployment.md) distinguishes checked-in resources
from observed deployment and supported product surfaces.

Bearer forwarding and fixed roots follow [Gateway](./gateway.md);
current authority follows [Identity](./identity-secrets.md) and owner checks.
The [canonical ADR view](./00-system-overview.md#9-architecture-decisions)
keeps the broader platform proposal distinct from supported surfaces.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| A tool supplies an absolute/traversal path or extra input | Reject rather than make an arbitrary Gateway request | Retained catalog/client tests |
| Owner returns `202` or `accepted: false` | Preserve acceptance/completion distinction and owner error | Controlled tool-handler tests |
| Catalog exceeds 2 MiB or result exceeds 8 MiB | Reject the bounded read | Compatibility contract tests; no deployed product claim |

The main reader risk is mistaking retained code or ingress resources for a
supported product. Use the checked-in normal-command API case for API
acceptance; retained MCP fixture success cannot replace that case.

**Terms:** inbound adapter translates client tools to Gateway; outbound
connection lets an agent address a remote server; neither discovery nor a tool
schema is an execution grant.

## Related content

- [Gateway](./gateway.md) explains routing, forwarding, and OpenAPI discovery.
- [Orchestration](./orchestration.md) explains decisions behind coordination
  tools.
- [Identity and secrets](./identity-secrets.md) explains authentication and grants.
- [Contracts and endpoints](../reference/contracts.md) records the public API.
