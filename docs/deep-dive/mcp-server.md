# MCP server

## The mental model

The first-party Model Context Protocol (MCP) server turns Gateway's finite API
into product tools. It does not maintain another workflow state machine or grant
authority because a tool name exists.

An MCP client authenticates with Identity Broker. The MCP host validates that
token, validates the tool arguments, and forwards the same bearer to Gateway.
Gateway routes the operation to its owner, which checks current authority and
state.

This inbound product server is separate from remote MCP connections used by
agents. The [remote MCP architecture](../architecture/remote-mcp-connections.md)
describes that outbound path.

## Where this lives

- `services/mcp/Agentweaver.Mcp/Program.cs` — Stateless HTTP transport,
  authentication, health routes, metadata, and tool handlers.
- `services/mcp/Agentweaver.Mcp/McpServiceOptions.cs` — Broker issuer/audience,
  Gateway root, protected-resource metadata path, and request timeout.
- `services/mcp/Agentweaver.Mcp/GatewayToolCatalog.cs` — OpenAPI retrieval,
  operation admission, schema resolution, and tool naming.
- `services/mcp/Agentweaver.Mcp/GatewayJsonSchemaValidator.cs` — Advertised
  argument-schema validation.
- `services/mcp/Agentweaver.Mcp/McpToolHandlers.cs` — Tool discovery, request
  construction, and structured result/error mapping.
- `services/mcp/Agentweaver.Mcp/GatewayApiClient.cs` — Exact caller bearer,
  relative-path restrictions, and supported headers.
- `services/mcp/Agentweaver.Mcp/McpGatewayCertificateValidation.cs` — Gateway
  certificate validation using the mounted backend CA.
- `services/gateway/Agentweaver.Gateway/GatewayRouteCatalog.cs` and
  `GatewayOpenApi.cs` — The tool catalog's authoritative API definitions.

## Core concepts and invariants

### Discovery comes from Gateway

`GatewayToolCatalog` fetches `GET /openapi/v1.json` and accepts the supported
OpenAPI 3.1.0 shape. It bounds the document at 2 MiB and admits supported
operations under the versioned API root.

Tools use `agentweaver_` followed by the operation ID. For example, Gateway's
`listProjects` operation becomes `agentweaver_listProjects`. The catalog retains
the operation's summary, owner description, parameters, body schema, and
accepted-only marker.

The catalog skips SSE operations and unsupported HTTP methods. It rejects
duplicate operation names, unsupported headers, invalid schemas, and an empty
admitted catalog. Operations are cached on the catalog instance after a
successful load.

### Inputs describe operations, not authority

Path and query parameters become tool inputs. Two supported headers have explicit
input names:

| HTTP header | Tool input |
| --- | --- |
| `X-Agentweaver-Tenant` | `tenantSelector` |
| `Idempotency-Key` | `idempotencyKey` |

A JSON request body becomes `body`. Required API parameters remain required
tool inputs. The generated object schema rejects additional top-level
properties.

There is no input for a caller-selected owner URL, actor identity, or arbitrary
authorization header.

### A tool result preserves owner semantics

The MCP wrapper keeps acceptance separate from completion. An accepted-only
operation or HTTP `202` produces the message that acceptance is not completion.
An owner body with `accepted: false` is a tool error even if the HTTP status is
successful.

## Discovery and call lifecycle

1. The client reaches authenticated `/mcp` over the stateless HTTP transport.
2. `tools/list` loads the Gateway catalog and returns the admitted tool schemas.
3. `tools/call` resolves the exact discovered name and validates its arguments
   against the advertised schema.
4. The handler escapes path values, serializes query values, and selects the
   declared JSON body and header inputs.
5. `GatewayApiClient` verifies the current bearer and restricts the request to
   the configured HTTPS Gateway's `/api/v1/` paths.
6. Gateway forwards to the owner and returns its status and body.
7. The MCP handler reads at most 8 MiB and returns the structured owner result
   with text content.

Call deadlines cover catalog loading, the Gateway request, and body reading.
`McpServiceOptions` sets a ten-second Gateway request timeout. Caller cancellation
propagates rather than becoming a successful tool response.

The request builder rejects absolute URLs, traversal segments, escaped path
separators, and fragments. HTTP redirects are disabled. The MCP service never
substitutes a service bearer for the caller.

## Authentication and transport

`Identity:Issuer`, `Identity:Audience`, and `Gateway:BaseAddress` are explicit
HTTPS settings. The Gateway address must identify the service root.

The host publishes OAuth protected-resource metadata at the path derived from
its configured audience. An unauthorized `/mcp` response advertises that
metadata through `WWW-Authenticate`.

Gateway TLS uses the mounted backend CA, checks the server name, and requires
server-auth certificate usage. Forwarded certificate headers do not supply that
trust.

`/health/live` and `/health/ready` are anonymous host checks. They do not fetch the
tool catalog or prove that Gateway or a domain owner can complete a call.

## Failure behavior

| Condition during a tool call | Tool error code |
| --- | --- |
| Name is absent from the admitted catalog | `unknown_tool` |
| Arguments do not match the advertised schema | `invalid_arguments` |
| Current MCP bearer is unavailable | `unauthenticated` |
| Gateway or catalog request reaches its deadline | `gateway_timeout` |
| Gateway or catalog cannot be reached | `gateway_unavailable` |
| Catalog shape is invalid | `gateway_contract_invalid` |
| Owner response exceeds the bound | `gateway_response_too_large` |

Owner HTTP errors retain their result details rather than becoming fabricated
completion. To find the final state after an accepted operation, use its owner
read operation. The MCP catalog does not expose the SSE stream as a long-running
tool.

## Continue reading

- [Gateway](./gateway.md) explains routing, forwarding, and OpenAPI discovery.
- [Orchestration](./orchestration.md) explains decisions behind coordination
  tools.
- [Identity and secrets](./identity-secrets.md) explains authentication and grants.
- [Gateway/BFF guide](../guide/gateway.md) and
  [contracts and endpoints](../reference/contracts.md) record the public API.
