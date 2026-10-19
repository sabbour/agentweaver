# MCP server

Use this page to understand how Agentweaver's first-party Model Context
Protocol (MCP) server exposes Gateway operations as product tools. The server
does not maintain a second workflow state machine, and discovering a tool does
not grant authority.

This inbound product server is separate from remote MCP connections used by
agents. See the [remote MCP architecture](../architecture/remote-mcp-connections.md)
for the outbound path.

## How the server exposes tools

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

## Understand discovery and inputs

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

## Call a discovered tool

Before calling a product tool, configure the MCP client with a valid Broker
token and access to the configured HTTPS Gateway.

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

## Interpret results correctly

An accepted-only operation or HTTP `202` produces a message that acceptance is
not completion. If the owner returns `accepted: false`, the tool reports an
error even when the HTTP status is successful. To learn the final state after
an accepted operation, call the owner's read operation. The catalog does not
expose the SSE stream as a long-running tool.

## Configure authentication and transport

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

## Diagnose failures

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

## Related content

- [Gateway](./gateway.md) explains routing, forwarding, and OpenAPI discovery.
- [Orchestration](./orchestration.md) explains decisions behind coordination
  tools.
- [Identity and secrets](./identity-secrets.md) explains authentication and grants.
- [Contracts and endpoints](../reference/contracts.md) records the public API.
