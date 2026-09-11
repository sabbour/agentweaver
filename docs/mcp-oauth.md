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

## Client session recovery

Dynamic registrations default to `mcp:invoke offline_access`, and the server
rotates the refresh token on every successful refresh. An MCP client must persist
both the access token and the current refresh token in its protected
configuration, then atomically replace the stored pair after each successful
refresh. Keeping only the access token in memory makes a dropped or restarted
session unable to recover silently.

Clients that register explicitly must request `offline_access`, use the
`refresh_token` grant against `/oauth/token`, and preserve the replacement
refresh token returned by the server. A refresh failure caused by an expired,
replayed, revoked, or client-mismatched token requires a new interactive
authorization flow. The Agentweaver server cannot repair a client
configuration that discarded its refresh token.

The consent page explicitly identifies the Agentweaver browser session before an
authorization can be approved. It shows the validated Entra display name and email or
UPN (falling back to the Entra object ID when no email is available). If no
identity-bearing Agentweaver session exists, it instead shows **Not signed in to
Agentweaver** and a same-origin Microsoft Entra sign-in action; it never renders an
anonymous consent decision.

## GitHub capability browser handoffs

MCP authorization and GitHub capabilities are separate. GitHub Repo App and project
Copilot App handoffs start from an already authenticated MCP identity and are pinned to
that initiating Entra subject. Opening an opaque browser URL without an Agentweaver browser
session starts Entra sign-in, then resumes the original handoff only after the browser
session has been issued. A session for a different Entra subject cannot redeem the handoff.

After GitHub returns, Agentweaver renders a no-store completion page that reports a safe
success, already-completed, or error result and instructs the operator to return to MCP
polling. The page, MCP result, and Entra continuation exclude GitHub authorization state, callback
cookies, authorization codes, tokens, and transaction details.

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

Access tokens are signed JWTs with an eight-hour lifetime by default. Clients that
need a longer session use `offline_access` and renew through the rotating refresh
token; the token response reports `expires_in=28800`. Authorization codes and refresh
tokens are opaque references persisted by OpenIddict. Code replay is rejected.
Refresh-token replay atomically revokes all tokens in its authorization family.
Refresh-family expiration is fixed at 30 days, and redeemed records are retained for
that lifetime plus a seven-day replay-detection margin.

Production loads active and previous signing and encryption certificate versions
from Azure Key Vault. Startup fails closed without usable durable keys.
Development may generate process-ephemeral keys.

Provision and deploy carry the certificate family names through
`OAUTH_SIGNING_CERTIFICATE_NAME` and `OAUTH_ENCRYPTION_CERTIFICATE_NAME`. Routine
rotation adds a new version under the same name; the loader selects the newest two
enabled, time-valid versions. Deployment verification checks the canonical origin,
exact `/mcp` resource, runtime certificate names, Key Vault versions, and keyed RS256
JWKS output.

The API setting `Auth:OAuth:AccessTokenLifetimeHours` controls only
Agentweaver-issued OAuth access tokens. AKS deployment supplies it through
`OAUTH_ACCESS_TOKEN_LIFETIME_HOURS`, which defaults to `8` and is included in the
OAuth runtime checksum. Supported values are 1 through 24 hours. It does not change
authorization-code, refresh-family, provider-capability, Entra, or Copilot token
lifetimes.

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
