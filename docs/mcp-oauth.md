# MCP OAuth authorization server

Agentweaver uses OpenIddict as its OAuth authorization server and Microsoft Entra
as the upstream human identity provider. Copilot CLI, GitHub Copilot desktop, and
VS Code may use a configured static public client or the restricted RFC 7591
registration endpoint.

Discovery is served from `/.well-known/oauth-authorization-server`. The canonical
issuer is `Auth:OAuth:PublicOrigin`; the MCP audience is always that exact origin
plus `/mcp`. Neither value is derived from `Host` or forwarded headers.

The server supports authorization code and rotating refresh-token grants only.
PKCE S256 and explicit consent are required. The stable least-privilege scope is
`mcp:invoke`; requesting additional approved scopes re-opens consent. Password,
implicit, client-credentials, and device grants are unavailable.

Access tokens are signed JWTs with a ten-minute lifetime. Authorization codes and
refresh tokens are opaque references persisted by OpenIddict. Code replay is
rejected. Refresh-token replay atomically revokes all tokens in its authorization
family.

Production loads active and previous signing and encryption certificate versions
from Azure Key Vault. Startup fails closed without usable durable keys.
Development may generate process-ephemeral keys.

This layer does not change MCP request validation. Broker-only MCP validation,
protected-resource metadata, and removal of transitional validation belong to the
subsequent resource-server cutover.
