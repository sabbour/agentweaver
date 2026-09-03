# MCP OAuth authorization server

Agentweaver uses OpenIddict as its OAuth authorization server and Microsoft Entra
as the upstream human identity provider. Copilot CLI, GitHub Copilot desktop, and
VS Code may use a configured static public client or the restricted RFC 7591
registration endpoint.

Discovery is served from `/.well-known/oauth-authorization-server`. The canonical
issuer is `Auth:OAuth:PublicOrigin`; the MCP audience is always that exact origin
plus `/mcp`. Authorization and token requests must each carry that one exact
`resource` value; omission, duplication, and normalized variants are rejected.
The canonical values are not derived from request headers.

The server supports authorization code and rotating refresh-token grants only.
PKCE S256 and explicit consent are required. The stable least-privilege scope is
`mcp:invoke`; requesting additional approved scopes re-opens consent. Password,
implicit, client-credentials, and device grants are unavailable.

Access tokens are signed JWTs with a ten-minute lifetime. Authorization codes and
refresh tokens are opaque references persisted by OpenIddict. Code replay is
rejected. Refresh-token replay atomically revokes all tokens in its authorization
family. Refresh-family expiration is fixed at 30 days, and redeemed records are
retained for that lifetime plus a seven-day replay-detection margin.

Production loads active and previous signing and encryption certificate versions
from Azure Key Vault. Startup fails closed without usable durable keys.
Development may generate process-ephemeral keys.

In production the gateway terminates TLS and forwards HTTP to the API. Forwarded
scheme and host processing runs before routing and OpenIddict, accepts exactly one
hop, and trusts only configured private gateway CIDRs. The AKS deploy derives
those CIDRs from the cluster network profile; arbitrary internet
`X-Forwarded-*` headers are ignored.

Anonymous dynamic registrations are limited to literal loopback callbacks or
tightly validated reverse-domain private-use schemes. HTTPS callbacks require an
explicitly administered static registration. Dynamic registrations expire after
30 days by default; maintenance disables the corresponding OpenIddict
application and reclaims active quota.

This layer does not change MCP request validation. Broker-only MCP validation,
protected-resource metadata, and removal of transitional validation belong to the
subsequent resource-server cutover.
