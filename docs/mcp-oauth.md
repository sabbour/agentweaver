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

The MCP resource server publishes RFC 9728 metadata anonymously at both
`/.well-known/oauth-protected-resource` and
`/.well-known/oauth-protected-resource/mcp`. Both documents advertise the exact
`<public-origin>/mcp` resource, the same-origin authorization server, and only
`mcp:invoke`.

The server supports authorization code and rotating refresh-token grants only.
PKCE S256 and explicit consent are required. The stable least-privilege scope is
`mcp:invoke`; requesting additional approved scopes re-opens consent. Password,
implicit, client-credentials, and device grants are unavailable.

The browser consent page keeps a strict Content Security Policy. Its form posts
only to Agentweaver, while the policy also permits the callback source selected
by the validated authorization request. OpenIddict validates that callback
against server-side application metadata before the endpoint runs; Agentweaver
then serializes only the validated request's scheme or authority, never raw query
text. This preserves exact private-use and static HTTPS callbacks while allowing
an RFC 8252 IPv4 loopback registration without a port to use a fresh ephemeral
request port. If the Agentweaver browser session expires before consent is
submitted, the POST renders a same-origin sign-in continuation instead of
redirecting the form submission into the Entra sign-in chain.

Access tokens are signed JWTs with a ten-minute lifetime. Authorization codes and
refresh tokens are opaque references persisted by OpenIddict. Code replay is
rejected. Refresh-token replay atomically revokes all tokens in its authorization
family. Refresh-family expiration is fixed at 30 days, and redeemed records are
retained for that lifetime plus a seven-day replay-detection margin.

Production loads active and previous signing and encryption certificate versions
from Azure Key Vault. Startup fails closed without usable durable keys.
Development may generate process-ephemeral keys.

Provision and deploy carry the certificate family names through
`OAUTH_SIGNING_CERTIFICATE_NAME` and `OAUTH_ENCRYPTION_CERTIFICATE_NAME`. Routine
rotation adds a new version under the same name; the loader selects the newest two
enabled, time-valid versions. Deployment verification checks the canonical origin,
exact `/mcp` resource, runtime certificate names, Key Vault versions, and keyed RS256
JWKS output.

Azure deployments derive the canonical origin from the trusted managed
`DefaultDomainCertificate` status. The deploy renderer rejects the committed
placeholder and applies a shared OAuth runtime checksum to the API and MCP pod
templates, so both processes restart and consume the same origin.

In production the gateway terminates TLS and forwards HTTP to the API. Forwarded
scheme and host processing runs before routing and OpenIddict, accepts exactly one
hop, and trusts only configured private gateway CIDRs. The AKS deploy derives
those CIDRs from the cluster network profile; arbitrary internet
`X-Forwarded-*` headers are ignored.

Anonymous dynamic registrations are limited to IPv4 `127.0.0.1` loopback
callbacks or tightly validated reverse-domain private-use schemes. IPv6
callbacks fail closed because their authorities cannot be represented safely in
the consent CSP. HTTPS callbacks require an explicitly administered static
registration with a CSP-compatible DNS or IPv4 host. Dynamic registrations
expire after 30 days by default; maintenance disables the corresponding
OpenIddict application and reclaims active quota.

The API resource server validates these broker access tokens through the named
`BrokerBearer` scheme only for endpoints classified `PlatformOrMcp`. The same
credential is rejected by self-only, platform-only, internal-service, and
run-capability endpoints. Issuer and audience remain pinned to the configured
canonical origin and `/mcp` resource, so `Host` and forwarded-host input cannot
steer validation.

The MCP process uses ASP.NET/OpenIddict remote discovery and JWKS validation. It
accepts only broker JWTs with the exact issuer and audience, a keyed RS256
signature, valid lifetime, subject, and `mcp:invoke` scope. It forwards only that
validated token to the API. The API accepts broker credentials only on
`PlatformOrMcp` endpoints and continues to enforce project authorization.
