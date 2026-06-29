# MCP OAuth 2.1 Authorization Server (Option C)

::: warning Experimental
The Agentweaver MCP server is **experimental**. Tool names, parameters, and behavior may change without notice. Pin to a known revision if you depend on the current surface.
:::

> Feature: `mcp-oauth` · Tasks T1-T3 (metadata + JWKS, token service, authorize/token)

Agentweaver hosts a thin OAuth 2.1 Authorization Server (AS) inside `Agentweaver.Api`. It lets MCP
clients (GitHub Copilot CLI, Claude Desktop, etc.) discover the AS, run a PKCE authorization-code
flow themselves, and obtain short-lived JWT access tokens bound to the MCP resource. The AS brokers
the actual login to GitHub using the existing confidential GitHub OAuth app, enforces `microsoft`
organization membership, and mints its own tokens. The GitHub `client_secret` and the user's GitHub
token never leave the server — the client only ever receives Agentweaver-minted artifacts.

The MCP server itself stays a pure OAuth Resource Server (it validates these tokens; it never issues
them). Resource-Server changes — the `WWW-Authenticate` 401 challenge, the
`oauth-protected-resource` metadata, and the JWT validation middleware — are **T6** and are not part
of this scope.

## What is implemented here (T1-T3)

| Task | Surface |
|---|---|
| T1 | `GET /.well-known/oauth-authorization-server` (RFC 8414) and `GET /oauth/jwks` |
| T2 | `McpTokenService` — RS256 signing of short-lived, audience-bound JWT access tokens + JWKS |
| T3 | `GET /oauth/authorize` and `POST /oauth/token` — authorization-code flow with mandatory PKCE (S256) |

## Endpoints

### `GET /.well-known/oauth-authorization-server`

Unauthenticated. Returns RFC 8414 metadata. The `issuer` is bound to the host the request arrived on
(`{scheme}://{host}`) unless `Auth:OAuth:Issuer` is configured, so the document is correct on local,
staging, and production hosts without per-environment code.

```json
{
  "issuer": "https://HOST",
  "authorization_endpoint": "https://HOST/oauth/authorize",
  "token_endpoint": "https://HOST/oauth/token",
  "registration_endpoint": "https://HOST/oauth/register",
  "jwks_uri": "https://HOST/oauth/jwks",
  "revocation_endpoint": "https://HOST/oauth/revoke",
  "scopes_supported": ["mcp:invoke", "offline_access"],
  "response_types_supported": ["code"],
  "grant_types_supported": ["authorization_code", "refresh_token"],
  "code_challenge_methods_supported": ["S256"],
  "token_endpoint_auth_methods_supported": ["none"]
}
```

`code_challenge_methods_supported` is `["S256"]` only — `plain` is rejected. The
`registration_endpoint` (T5) and `revocation_endpoint` (T4) are advertised for forward compatibility
but are not yet implemented.

### `GET /oauth/jwks`

Unauthenticated. Publishes the public half of the current signing key so a Resource Server can
validate access tokens offline.

```json
{ "keys": [ { "kty": "RSA", "use": "sig", "alg": "RS256", "kid": "<kid>", "n": "<base64url>", "e": "<base64url>" } ] }
```

The `kid` is derived deterministically from the public key material, so the same key always advertises
the same `kid` (enabling future `kid`-based rotation).

### `GET /oauth/authorize`

Unauthenticated. Begins the authorization-code flow. Query parameters:

| Parameter | Required | Notes |
|---|---|---|
| `response_type` | yes | Must be `code`. |
| `client_id` | yes | Public client identifier. |
| `redirect_uri` | yes | Loopback (`http://127.0.0.1:*`, `http://localhost:*`, `http://[::1]:*`) or HTTPS, exact-match, no fragment. |
| `code_challenge` | yes | PKCE challenge (mandatory). |
| `code_challenge_method` | yes | Must be `S256`. `plain` and a missing method are rejected. |
| `scope` | no | Defaults to `mcp:invoke`. |
| `state` | no | Opaque client value, echoed back on the redirect. |
| `resource` | no | RFC 8707 resource indicator. |

On success the endpoint redirects the user agent to GitHub to log in. After GitHub returns to the
server callback and org membership is confirmed, the server redirects an authorization `code` (and the
client's `state`) back to `redirect_uri`.

Validation failures return `400` with an OAuth error body
(`{"error": "...", "error_description": "..."}`) and **never** redirect, to avoid open redirects.
`invalid_request` is used for missing `client_id`/`redirect_uri` and PKCE problems;
`unsupported_response_type` for a non-`code` `response_type`.

### `POST /oauth/token`

Unauthenticated (public client; `token_endpoint_auth_methods_supported: ["none"]`).
`application/x-www-form-urlencoded` body. Responses include `Cache-Control: no-store`.

**`grant_type=authorization_code`** — parameters `code`, `code_verifier`, `redirect_uri`, `client_id`.
The server enforces single-use codes (≤ 60 s TTL), exact `redirect_uri` and `client_id` match, and
PKCE verification (`code_challenge == BASE64URL(SHA256(code_verifier))`). On success:

```json
{
  "access_token": "<JWT>",
  "token_type": "Bearer",
  "expires_in": 900,
  "scope": "mcp:invoke",
  "refresh_token": "<opaque>"
}
```

`refresh_token` is a structural placeholder in this scope — rotation, hashing, reuse-detection, and the
`refresh_token` grant are **T4**. The `refresh_token` grant currently returns `400 invalid_request`.

Failures return `400` with an OAuth error body: `invalid_grant` (bad/expired/used code, redirect or
client mismatch, PKCE failure) or `unsupported_grant_type`.

## Access token

Signed JWT (RS256). Claims:

| Claim | Value |
|---|---|
| `iss` | `https://HOST` (the resolved issuer) |
| `aud` | `https://HOST/mcp` (the MCP resource, RFC 8707 binding) |
| `sub` | GitHub login of the authenticated user |
| `gh_login` | GitHub login |
| `scope` | `mcp:invoke` |
| `org` | `microsoft` (informational; authoritative check is at issuance) |
| `iat`, `nbf`, `exp` | issued-at, not-before, expiry (15 minutes) |
| `jti` | unique token id (used by the T4 denylist) |

## Configuration

| Key | Purpose |
|---|---|
| `Auth:OAuth:SigningKey` | RSA private key (PEM or bare base64 PKCS#8). Bound to the Key Vault secret **`mcp-oauth-signing-key`**. |
| `Auth:OAuth:Issuer` | Optional issuer override. When empty, the issuer is derived from the request host. |
| `Auth:OAuth:Audience` | Optional audience override. Defaults to `{issuer}/mcp`. |
| `Auth:GitHub:ClientId` / `ClientSecret` / `CallbackUrl` / `Scopes` | Existing confidential GitHub OAuth app (reused for the broker leg). |
| `Auth:GitHub:AllowedOrg` | Organization enforced at token issuance (`microsoft`). |

If `Auth:OAuth:SigningKey` is not set, the service generates an **ephemeral** RSA key at startup for
local development only (a warning is logged). Ephemeral keys do not survive a restart and must not be
used in any shared or hosted deployment.

## Security properties

- **Mandatory PKCE, S256 only.** Missing or `plain` challenge is rejected; codes are bound to the
  `code_challenge`, `client_id`, and `redirect_uri`, are single-use, and expire in ≤ 60 s.
- **Exact redirect-URI policy.** Loopback HTTP or HTTPS only, no fragment; the exact string captured at
  `/authorize` is re-checked at token redemption. (Per-client registered URIs arrive with T5 DCR.)
- **Audience-bound tokens.** `aud` is the MCP resource, so a stolen token is useless elsewhere and
  expires within 15 minutes.
- **Org membership enforced at issuance.** No authorization code (and therefore no token) is issued to
  a non-member; the GitHub broker callback runs `GitHubOrgAuthorizationService` before issuing a code.
- **Secrets stay server-side.** The GitHub `client_secret` and the user's GitHub token never reach the
  client. Tokens, codes, and verifiers are never logged.

## Backward compatibility

There is no static Agentweaver API-key path for MCP — auth relies solely on Agentweaver-minted JWTs
(via DCR) and the transitional raw-GitHub passthrough. The `/oauth/*` and `/.well-known/*` routes are
exempt from the GitHub token and org-authorization middleware so the flow that obtains a token is
itself reachable without a token.

## Out of scope (post-checkpoint)

- **T4** — rotating refresh-token store (hashed) + reuse detection, `/oauth/revoke`, `jti` denylist.
- **T5** — `/oauth/register` Dynamic Client Registration (RFC 7591).
- **T6** — MCP Resource-Server changes: `WWW-Authenticate` 401, `/.well-known/oauth-protected-resource`, JWT validation middleware.
- **T7** — per-user downstream identity for MCP→API calls.
