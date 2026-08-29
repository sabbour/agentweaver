# Auth & Security — Conceptual Deep Dive

## Purpose and mental model

Agentweaver has three related but distinct security jobs:

1. **Know who is calling.** A request must carry a bearer credential that can be mapped to a GitHub user or an Agentweaver-issued OAuth identity.
2. **Know whether that user is allowed.** Most non-bootstrap surfaces are restricted to callers who match at least one of a configured list of allow-rules (each rule is either a bare GitHub org, or an org + specific team).
3. **Let MCP clients authenticate without learning GitHub secrets.** Agentweaver acts as an OAuth 2.1 Authorization Server for MCP clients. GitHub remains the human identity provider; Agentweaver mints short-lived tokens for its own MCP resource.

The design deliberately separates **identity proof** from **authorization policy**:

- GitHub proves the user's identity and can prove org/team membership when GitHub's APIs allow it.
- Agentweaver enforces local invariants: the configured allow-rule list, token lifetime, redirect policy, PKCE, refresh-token rotation, revocation, and middleware exemptions.
- MCP clients receive Agentweaver credentials, not GitHub credentials.

The important rebuild principle is: **never trust a client merely because it reached a route**. Each route should be either explicitly public bootstrap/discovery, or it should pass through bearer-token authentication and org authorization.

## Supported authorization modes (`Auth:Mode`)

Agentweaver now treats **Entra-based authorization mode** and **GitHub-based authorization mode** as two supported deployment choices selected by `Auth:Mode`.

| `Auth:Mode` | Platform sign-in | Platform authorization gate | GitHub's role |
|---|---|---|---|
| `Entra` | Single-tenant Microsoft Entra ID | Entra App Roles (`PlatformAdmin`, `ProjectCreator`, `Contributor`, `Viewer`) plus app-native project role assignments (`Owner`, `Contributor`, `Viewer`) | Linked resource-provider identity for repository access, pull requests, and Copilot entitlement |
| `GitHubLegacy` | GitHub OAuth | GitHub org/team allow-rules plus existing resource ownership checks | Both the platform identity provider and the repository/Copilot identity |

Both modes are supported. Choosing `Entra` does **not** imply a forced cutover for deployments that prefer GitHub-based authorization, and choosing `GitHubLegacy` does **not** block normal operation.

### Entra mode: platform identity, GitHub as a linked provider

In `Auth:Mode=Entra`, Agentweaver separates **platform identity** from **GitHub capability**:

1. The user signs in with a **single-tenant** Entra application.
2. The ID token establishes the durable platform subject (`oid`) and the user's Tier 1 App Roles.
3. Agentweaver enforces Tier 1 platform access on every protected request.
4. Agentweaver then evaluates Tier 2 project/data access from its own database using project-level role assignments keyed by that Entra `oid`.
5. GitHub accounts are linked **after** platform sign-in so the user can browse repositories, clone/push, open pull requests, and consume GitHub Copilot through one or more linked GitHub tokens.

This produces two different but complementary authorization layers:

- **Tier 1 — platform admission**: `PlatformAdmin`, `ProjectCreator`, `Contributor`, `Viewer`
- **Tier 2 — project/data authorization**: `Owner`, `Contributor`, `Viewer`

The important invariant is that **GitHub token lookup happens only after the platform user is already authenticated and authorized for the target resource**. A linked GitHub token can expand what repositories or Copilot entitlements the user can reach, but it cannot elevate platform or project authorization by itself.

Just as importantly, **Agentweaver project roles do not grant GitHub rights**. `Owner`, `Contributor`, and `Viewer` govern only Agentweaver-side actions such as viewing project data, triggering runs, or managing project membership. Whether a user can clone, push, open a pull request, or administer a repository depends entirely on the resolved linked GitHub identity's real GitHub permission on that repository.

Switching `Auth:Mode` invalidates existing browser sign-in handshakes immediately: outstanding web session exchange codes are mode-bound and cannot be redeemed after a mode flip, and previously issued bearer tokens from the old mode are rejected by the new mode's request-auth pipeline.

### GitHub-based authorization mode: still fully supported

In `Auth:Mode=GitHubLegacy`, Agentweaver keeps the current GitHub-centered flow:

- GitHub OAuth is the primary sign-in path.
- GitHub org/team rules remain the platform admission gate.
- Existing per-resource ownership checks remain in force.
- GitHub still provides the repository token and GitHub Copilot entitlement.

This mode is not a compatibility shim or a sunset path; it remains a valid deployment choice when GitHub-based authorization is the right operational fit.

### MCP/client implications

The MCP and API surfaces must become **mode-aware**:

- MCP authentication helpers can no longer assume GitHub is always the platform login.
- In Entra mode, GitHub tooling becomes **linked-account management** plus **linked-token-aware repository access**.
- Repository listing/import flows must be able to enumerate repositories across the user's linked GitHub identities, not just one token.
- Project and admin tooling must expose project-role assignment APIs/tooling without relying on GitHub org membership as the only authorization model.

The existing GitHub-focused sections below continue to describe the current GitHub-based path in detail. This section is the conceptual source of truth for the new dual-mode model and for the Entra-mode additions that will be layered onto the current implementation.

## Threat model and guardrail summary

Agentweaver assumes attackers may:

- steal redirect URLs from browser history, logs, or `Referer` headers;
- replay authorization codes or refresh tokens;
- point OAuth error redirects at attacker-controlled URLs;
- send raw GitHub tokens, configured automation keys, malformed JWTs, or revoked Agentweaver JWTs;
- exploit SAML-enforced GitHub org behavior to create false membership conclusions;
- set unsafe config flags accidentally in production;
- run high-volume probing against public OAuth endpoints.

The main guardrails are:

- **No long-lived token in redirect URLs.** Browser sign-in redirects with a short-lived, single-use code, then returns the GitHub token only from a POST exchange.
- **PKCE S256 for public clients.** MCP clients cannot use `plain` PKCE and cannot redeem a code without the verifier.
- **Redirect validation before redirecting.** Invalid OAuth clients or redirect URIs get local errors, not redirects to untrusted destinations.
- **Short-lived access tokens.** Agentweaver JWTs last about 15 minutes; refresh tokens are rotating and theft-sensitive.
- **Revocation at two layers.** Refresh-token chains can be revoked, and access-token `jti` values can be deny-listed until expiry.
- **Fail closed on uncertain authorization.** If org membership cannot be verified for a live request, the request is blocked rather than allowed.
- **Do not cache uncertainty.** Transient GitHub failures and rate limits are not cached as durable authorization facts.
- **Production startup guards.** Test auth bypasses and missing public OAuth issuer/audience config fail fast in production.

Where this lives:

- `apps/Agentweaver.Api/Program.cs`
- `apps/Agentweaver.Api/Security`
- `apps/Agentweaver.Api/Auth`
- `apps/Agentweaver.Mcp`

## Architecture at a glance

Every request crosses a network-policy boundary into the gateway, then passes through two ordered middlewares: `GitHubTokenAuthMiddleware` resolves identity (Agentweaver JWT validated offline against the `jti` denylist, or a raw GitHub token validated via `GET /user` and cached), and `GitHubOrgAuthorizationMiddleware` enforces the configured allow-rule list before any protected route runs. Browser sign-in and the MCP OAuth flow both terminate at GitHub as the human identity provider.

![Architecture at a glance: Browser web UI, MCP client, Direct API caller, default-deny + allowlist NetworkPolicies, Istio gateway / HTTPRoute, AuthEndpoints GitHub sign-in, OAuth 2.1 Authorization Server, GitHubTokenAuthMiddleware, GitHubOrgAuthorizationMiddleware, Protected /api routes, Bearer valid? JWT jti / GitHub /user, Org + team membership, …](../diagrams/auth-security-fig1.png)

<!-- Rendered from ../diagrams/src/auth-security-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

## Web sign-in: GitHub OAuth on behalf of the user

Web sign-in solves a browser problem: the user needs to authorize Agentweaver with GitHub, but the browser must not receive server secrets and should not receive the GitHub access token through a URL.

Conceptually, Agentweaver behaves as a confidential GitHub OAuth client:

1. The browser asks Agentweaver to start sign-in.
2. Agentweaver creates a random CSRF `state`, remembers it briefly, and redirects the browser to GitHub's authorization page.
3. GitHub authenticates the human and redirects back with an authorization `code` and the same `state`.
4. Agentweaver validates and consumes the `state`, then exchanges the code server-to-server using its GitHub client ID, callback URL, and client secret.
5. Agentweaver calls GitHub `/user` with the returned GitHub access token to learn the login.
6. Agentweaver stores the GitHub token for later server use.
7. Instead of putting that GitHub token in the frontend redirect, Agentweaver creates a one-time web session code, redirects with only that opaque code, and requires the frontend to redeem it by POST.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    autonumber
    participant Browser
    participant API as Agentweaver API
    participant GitHub
    participant Store as GitHub token store

    Browser->>API: Start GitHub sign-in
    API->>API: Generate short-lived CSRF state
    API-->>Browser: Redirect to GitHub authorize URL
    Browser->>GitHub: User signs in / approves scopes
    GitHub-->>API: Callback with code + state
    API->>API: Validate and consume state
    API->>GitHub: Exchange code using server-side client secret
    GitHub-->>API: GitHub access token
    API->>GitHub: Fetch /user for login
    API->>Store: Persist GitHub token and identity
    API->>API: Issue 60-second one-time web code
    API-->>Browser: Redirect to frontend with opaque code only
    Browser->>API: POST code to session exchange endpoint
    API-->>Browser: Return session token + login in response body
```

### Why this shape

- **CSRF state** binds the callback to a sign-in Agentweaver initiated.
- **Server-side code exchange** keeps the GitHub client secret out of browsers and MCP clients.
- **GitHub `/user` lookup** turns an opaque GitHub access token into the accountable login Agentweaver uses for ownership checks.
- **One-time POST exchange** avoids leaking the GitHub access token through URL logs, history, analytics, reverse proxies, or referrers.
- **Short lifetimes and single use** make stolen intermediate codes less valuable.

### Invariants to preserve when rebuilding

- The GitHub `state` must be random, short-lived, and consumed exactly once.
- The callback must reject missing code/state and GitHub errors.
- The GitHub token must not be placed in a query string or fragment.
- The one-time frontend exchange code must be random, short-lived, and atomically removed on redemption.
- Public sign-in endpoints are bootstrap routes; do not require a bearer token before the user has one.

Web auth uses a GitHub App user-to-server flow. The live production GitHub client ID is supplied by configuration rather than stored in source, and the server-side authorization-code exchange uses a client secret that is also kept out of source.

Where this lives:

- `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs`
- `apps/Agentweaver.Api/Auth/GitHubOAuthRedirectService.cs`
- `apps/Agentweaver.Api/Auth/WebSessionExchangeService.cs`
- `.squad/decisions.md`

## API bearer authentication: accepting tokens safely

After bootstrap, protected API calls use `Authorization: Bearer ...`. The API tries to resolve the caller in this order:

1. **Agentweaver OAuth JWT.** If the bearer looks like a JWT and validates as an Agentweaver-issued access token, use its subject, GitHub login, org claim, and `jti`.
2. **Raw GitHub bearer token.** Otherwise, ask GitHub `/user` whether the token is valid and which login owns it.
3. **Development-only bypass.** A configured bypass can map tokens to users only in Development; production refuses to start if bypass flags are enabled.

The API does not accept static automation keys. Hosted MCP forwards each caller's accepted bearer token to the API, so the end-to-end identity is always a raw GitHub token or an Agentweaver JWT that the backend can validate.

![API bearer authentication: accepting tokens safely: Bearer token arrives on protected API route, Looks like valid Agentweaver JWT?, jti deny-listed?, 401, Caller = JWT subject + GitHub login + org, Call GitHub /user, Caller = GitHub login, Org authorization middleware](../diagrams/auth-security-fig2.png)

<!-- Rendered from ../diagrams/src/auth-security-fig2.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

### Why this shape

- **JWT first** lets MCP callers use Agentweaver-issued tokens without calling GitHub on every request.
- **Raw GitHub fallback** preserves direct API use by users who already have a GitHub OAuth token.
- **Short validation caches** reduce GitHub API load but avoid long-lived stale identity decisions.
- **Deny-list check** gives access-token revocation meaning even before the 15-minute JWT lifetime expires.
- **Production bypass guard** prevents a test convenience from becoming a production backdoor.

### Invariants to preserve when rebuilding

- Never treat an arbitrary bearer token as a user without validating it.
- Cache token validation by a token hash, not by raw token value in logs or cache keys intended for inspection.
- Negative validation results should have a much shorter cache lifetime than positive results.
- A revoked Agentweaver JWT must fail before caller context is created.
- Middleware that needs caller identity must run after bearer-token authentication.

Where this lives:

- `apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs`
- `apps/Agentweaver.Api/Security/ApiKeyRegistry.cs`
- `apps/Agentweaver.Api/Security/TestingBypassGuard.cs`

## GitHub org authorization and the SAML nuance

Identity answers "who are you?" Authorization answers "are you allowed to use this Agentweaver deployment?" For hosted Agentweaver, the primary policy is matching at least one entry in a configured allow-rule list. Each rule is one of:

- **`org`** — a bare GitHub org name. Satisfied by org membership (any tier: public or private).
- **`org/*`** — an explicit "any team in this org" wildcard. Semantically identical to a bare `org` rule (org membership is sufficient); accepted for readability.
- **`org/team-slug`** — a specific team within an org. Satisfied only if the caller is a member of that exact team. Team-slug values are defensively normalized (lowercased and space-to-hyphen) so display-name-ish literals like `Azure/AKS PM` still probe the correct GitHub team endpoint (`azure/aks-pm`); a warning is logged whenever normalization changes the input.

A caller is admitted if they satisfy **any one** rule in the list (pure OR across rules). This replaces an earlier two-tier model (single allowed org plus an AND-restricted team); the legacy `Auth:GitHub:AllowedTeam` key is still honored as a deprecated compat shim that is folded into the rule list as an additional OR'd entry (its semantics have therefore changed from AND-restriction to OR-rule — do not rely on it in new deployments).

The authorization middleware runs after bearer authentication. It handles two caller classes differently:

- **Agentweaver OAuth JWT callers:** the signed `org` claim carries the canonical **rule string** that was satisfied at token-issuance time (for example `azure/aks-pm` or `microsoft`). The middleware trusts the JWT only if that exact rule is still present in the current allow-rule list (case-insensitive). This prevents grandfathering: a JWT minted while `microsoft` was allowed cannot satisfy a later configuration that only allows `azure/aks-pm`, and vice versa. Org membership was itself checked when the Authorization Server issued the token and is re-checked on refresh via the same rule-resolution path.
- **Raw GitHub token callers:** use the caller's GitHub token to ask GitHub whether the login satisfies any rule. Rules are evaluated in order; the first `Allowed` short-circuits.

![GitHub org authorization and the SAML nuance: Non-exempt request, Caller context exists?, 401 unauthenticated, Agentweaver OAuth JWT?, JWT rule claim still in allow-list?, Allow, 403, For each configured rule (org or org/team) using caller GitHub token, Authenticated private org membership check, Rule has team slug?, Team membership check, Unauthenticated public-membership check, 403 retry later; do not cache, …](../diagrams/auth-security-fig3.png)

<!-- Rendered from ../diagrams/src/auth-security-fig3.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

For SAML-enforced GitHub organizations, an authenticated API request can fail even for a real member if the token has not been SAML-authorized for that org. This matters because the normal private membership endpoint may return a SAML-related failure instead of a simple "member" or "not member" answer.

Agentweaver uses a two-probe strategy:

1. **Authenticated private membership check.** This can prove private org membership when the token has sufficient org/SAML access.
2. **Unauthenticated public-membership fallback.** This can prove membership only for users who have publicized their org membership.

The fallback must be unauthenticated. If Agentweaver sends the same SAML-blocked token to the public-members endpoint, GitHub can still apply SAML enforcement and return a misleading failure. Without an auth header, GitHub returns the public-membership truth: public members are visible; private members are not.

The unavoidable trade-off is that private members whose token cannot prove membership and who have not publicized membership may be denied or asked to retry. Agentweaver chooses this over allowing unverifiable callers into a protected deployment.

### Result semantics

Each rule in the allow-list is resolved independently and produces a per-rule signal (`Allowed`, `Denied`, `OrgAccessNotGranted` / SAML-enforced, or `Inconclusive`). Signals are then aggregated across the list with precedence **Allowed > SAML-enforced > Inconclusive > Denied**:

- **Allowed:** at least one rule resolved to `Allowed` — GitHub proved org membership and, for team-scoped rules, team membership as well.
- **Denied:** every rule resolved to `Denied` — GitHub gave a definitive non-member answer for each.
- **Org access not granted:** no rule was `Allowed`, and at least one rule hit a token that could not access the org/team private API (commonly because SAML SSO was not authorized).
- **Inconclusive:** no rule was `Allowed` or SAML-enforced, and at least one rule could not be answered reliably because of rate limiting, token failure, network failure, or server error.

Only stable per-rule answers contribute to a cacheable overall result. Inconclusive answers are never cached, because caching them would turn a transient GitHub problem into a durable denial. Note that team-scoped rules have no unauthenticated public-membership fallback (GitHub does not expose team membership publicly); a SAML-blocked token evaluating a team-scoped rule can only produce `OrgAccessNotGranted` or `Inconclusive`, never a spurious `Allowed`.

### Invariants to preserve when rebuilding

- Fail closed if the allow-rule list is missing or empty on non-exempt routes.
- Parse rules using the shared tokenizer (split, trim, dedupe case-insensitively) and then extract an optional `/team-slug` (or `/*`) suffix; treat unparseable entries as configuration errors, not as silent org-only fallbacks.
- Defensively normalize team-slug values (lowercase + space-to-hyphen) so display-name-shaped literals still probe the right GitHub endpoint, and log a warning whenever normalization actually changed the input.
- Aggregate across rules with precedence **Allowed > SAML-enforced > Inconclusive > Denied**; do not let a single `Denied` rule mask a SAML-enforced signal from another rule.
- Do not let HTTP redirects from GitHub turn into accidental success; membership probes should not auto-follow GitHub redirects.
- Detect rate limits before classifying `403` as SAML/org denial.
- Never cache inconclusive authorization decisions (per-rule or aggregate).
- Team-scoped rules have no unauthenticated public-membership fallback — do not invent one.
- If using JWT org claims, ensure the Authorization Server stamps the canonical **rule string** that was satisfied at issuance (not just an org name), and that the middleware re-checks that rule string against the current allow-list on every request (case-insensitive) so config demotions or promotions cannot grandfather in stale JWTs.
- Treat the legacy `Auth:GitHub:AllowedTeam` key as a deprecated OR'd-in compat shim only; new deployments should express team scoping directly in the rule list.

Where this lives:

- `apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationMiddleware.cs`
- `apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationService.cs`
- `apps/Agentweaver.Api/appsettings.json`
- `k8s/base/api-deployment.yaml`

## Resource ownership authorization

Org authorization answers only whether a caller may use this Agentweaver deployment (satisfies at least one configured allow-rule). It does not grant access to every resource in the deployment. Project, team, run, backlog, workspace, workflow, and memory endpoints still enforce resource ownership in the handler or service layer, typically by loading the resource and checking `caller.Owns(...)` before returning or mutating it.

The ownership model is intentionally username-neutral: there is no built-in superuser role derived from a GitHub login, and no GitHub username such as `admin` receives special cross-user access. A caller can act on a resource only when the resource owner matches the authenticated caller identity (or when a feature explicitly creates a resource on behalf of that caller). Non-owners receive `403 Forbidden` or, for existence-hiding reads, `404 Not Found`.

### Invariants to preserve when rebuilding

- Treat allow-rule matching as deployment admission, not resource authorization.
- Use `caller.Owns(...)` or the equivalent owner comparison for every user-owned project, run, team, backlog, and memory resource.
- Do not introduce username-based superuser bypasses; elevated operational paths should be explicit roles or separate administrative capabilities, not magic GitHub logins.

Where this lives:

- `apps/Agentweaver.Api/Endpoints/ProjectEndpoints.cs`
- `apps/Agentweaver.Api/Endpoints/TeamEndpoints.cs`
- `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs`
- `apps/Agentweaver.Api/Endpoints/BacklogEndpoints.cs`

## OAuth 2.1 Authorization Server for MCP

MCP clients are public clients: they cannot safely hold a GitHub client secret, and Agentweaver should not hand them the user's GitHub token. The solution is to make `Agentweaver.Api` an OAuth 2.1 Authorization Server for the MCP resource.

In this model:

- GitHub remains the upstream human identity provider.
- Agentweaver is the OAuth Authorization Server seen by MCP clients.
- The MCP server is the OAuth Resource Server.
- The client receives Agentweaver authorization codes, access tokens, and refresh tokens — never GitHub tokens or GitHub client secrets.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    autonumber
    participant Client as MCP Client
    participant MCP as Agentweaver MCP Resource Server
    participant AS as Agentweaver API / Authorization Server
    participant GitHub
    participant Store as OAuth stores

    Client->>MCP: Request /mcp without token
    MCP-->>Client: 401 with protected-resource metadata link
    Client->>MCP: Fetch protected-resource metadata
    MCP-->>Client: Authorization server issuer
    Client->>AS: Fetch authorization-server metadata + JWKS URI
    Client->>AS: Register client redirect URIs (optional DCR)
    Client->>AS: /oauth/authorize with client_id, redirect_uri, S256 challenge
    AS->>AS: Validate client, redirect URI, response_type, PKCE
    AS-->>Client: Browser redirect to GitHub
    Client->>GitHub: User signs in
    GitHub-->>AS: Callback with code + state
    AS->>GitHub: Exchange GitHub code server-side
    AS->>AS: Enforce org membership
    AS-->>Client: Redirect to client with Agentweaver authorization code
    Client->>AS: /oauth/token with code_verifier
    AS->>AS: Consume code and verify PKCE/client/redirect binding
    AS->>Store: Store hashed rotating refresh token
    AS-->>Client: JWT access token + opaque refresh token
    Client->>MCP: /mcp with Bearer JWT
    MCP->>AS: Fetch/cache JWKS as needed
    MCP->>MCP: Validate JWT offline
```

### Public discovery endpoints

OAuth-capable MCP clients need unauthenticated discovery before they have a token. Agentweaver therefore publishes:

- Authorization Server metadata (`/.well-known/oauth-authorization-server` and MCP-suffixed alias).
- OIDC-compatible discovery aliases for clients that probe those paths.
- JWKS for the public signing key.
- MCP Protected Resource metadata from the MCP server, advertising the authorization server and resource.

Discovery should not require auth; otherwise clients could not learn how to authenticate. The trade-off is that discovery reveals public configuration, so it should expose only non-secret metadata.

### Authorization endpoint logic

`/oauth/authorize` is intentionally strict before redirecting anywhere:

1. Require `client_id`.
2. Validate `redirect_uri` against the redirect policy.
3. If the client registered dynamically, require the requested redirect URI to match that registered set. Native loopback registrations may ignore port for usability, but token redemption still binds to the exact redirect URI used in the authorization request.
4. Require `response_type=code`.
5. Require PKCE with `code_challenge_method=S256`.
6. Record the client's request, keyed by the GitHub CSRF state used for the brokered login.
7. Redirect the user to GitHub.

Validation failures return local OAuth error responses instead of redirects. This avoids open-redirect vulnerabilities where an attacker supplies a malicious redirect URI and receives error details or codes.

### Brokered GitHub login

The broker joins two flows:

- the MCP client's OAuth authorization-code flow with Agentweaver; and
- Agentweaver's confidential OAuth flow with GitHub.

The GitHub callback is shared with web sign-in. Agentweaver decides which path to take by checking whether the callback `state` belongs to a pending MCP authorization. If yes, it finishes the brokered MCP flow: exchange GitHub code, verify org membership, then issue an Agentweaver authorization code for the MCP client redirect.

The MCP authorization code is not a GitHub code. It is a short-lived, single-use Agentweaver artifact bound to:

- client ID;
- redirect URI;
- PKCE challenge;
- GitHub login / subject;
- requested scope.

### Token endpoint logic

`/oauth/token` supports two grants:

- **authorization_code:** consume the Agentweaver authorization code, verify client ID, exact redirect URI, and PKCE verifier, then mint a JWT access token and issue a refresh token.
- **refresh_token:** rotate the refresh token, optionally recheck org membership, and mint a new JWT access token.

Token responses are marked `no-store` because they contain bearer credentials.

Refresh-time org recheck is best effort. If Agentweaver has a brokered GitHub token for the user, it asks GitHub again. A definitive non-member result revokes and denies. An inconclusive result falls back to the org claim captured at issuance so transient GitHub or SAML-token problems do not lock out valid private-org users every time a GitHub token expires.

### Dynamic Client Registration

Dynamic Client Registration lets public MCP clients register redirect URIs and receive a non-secret `client_id`. The registered redirect set becomes the per-client redirect allowlist. Agentweaver rejects confidential client auth methods because this design assumes public clients plus PKCE, not client secrets.

### Revocation

`/oauth/revoke` is idempotent. Unknown tokens still produce success, matching OAuth revocation semantics and avoiding token existence oracles. If the token is a refresh token, Agentweaver revokes the whole chain. If the token is a valid Agentweaver access token, Agentweaver deny-lists its `jti` until natural expiry.

### Invariants to preserve when rebuilding

- OAuth discovery and authorization bootstrap endpoints must be public, but rate-limit expensive flow endpoints.
- Redirect URI validation must happen before any redirect.
- PKCE must be mandatory and S256-only.
- Authorization codes must be short-lived, single-use, and bound to client, redirect URI, and PKCE.
- MCP clients must never receive GitHub access tokens or the GitHub client secret.
- Refresh tokens must rotate; reuse should revoke the chain.

Where this lives:

- `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs`
- `apps/Agentweaver.Api/Auth/OAuth/McpOAuthBrokerService.cs`
- `docs/mcp-oauth.md`

## MCP bearer JWTs: issuance, validation, and forwarding

Agentweaver access tokens are JWTs signed with RS256. They are designed to be validated offline by the MCP Resource Server and by the API.

A token represents:

- **issuer (`iss`)**: the public Agentweaver Authorization Server issuer;
- **audience (`aud`)**: the MCP resource, usually `{issuer}/mcp`;
- **subject (`sub`)**: the authenticated GitHub login;
- **GitHub login (`gh_login`)**: explicit login claim for downstream identity;
- **scope**: currently `mcp:invoke`;
- **org**: the allowed org captured at issuance;
- **lifetime claims**: issued-at, not-before, expiry;
- **JWT ID (`jti`)**: revocation handle.

![MCP bearer JWTs: issuance, validation, and forwarding: Agentweaver API signs JWT with RSA private key, Publishes public key as JWKS, MCP client presents JWT, MCP validates signature, iss, aud, exp, alg, Forward same bearer token to API, API validates JWT and checks jti denylist, Org middleware trusts matching org claim](../diagrams/auth-security-fig4.png)

<!-- Rendered from ../diagrams/src/auth-security-fig4.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

### Signing keys

Production should load a stable RSA private key from configuration/secret storage. Local development can generate an ephemeral key so the flow works on a developer machine, but ephemeral keys invalidate tokens on restart and are unsuitable for multi-instance or hosted deployments.

JWKS exposes only the public key. The `kid` is deterministic from public key material so the same key advertises the same identity across restarts.

### Validation responsibilities

The MCP server validates JWTs using cached JWKS from the Authorization Server. It checks signature, issuer, audience, lifetime, and RS256 algorithm. That lets MCP reject invalid tokens without calling GitHub or the Authorization Server on every request.

The API also validates Agentweaver JWTs when MCP forwards calls downstream. The API additionally checks the `jti` denylist, because revocation state lives in the API database. This split keeps MCP stateless for normal validation while preserving authoritative revocation at the backend.

### Issuer and audience pinning

In production, issuer and audience must be public, stable values. Internal service-to-service hosts such as `http://agentweaver-api:8080` are not the OAuth issuer the client discovered and not the audience embedded in tokens. If production derived issuer/audience from internal request hosts, valid forwarded JWTs would fail validation. Agentweaver therefore requires pinned issuer/audience config in production for both API and HTTP-mode MCP.

Where this lives:

- `apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs`
- `apps/Agentweaver.Mcp/McpAccessTokenValidator.cs`
- `apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs`
- `apps/Agentweaver.Mcp/AgentweaverApiClient.cs`
- `apps/Agentweaver.Api/Security/OAuthConfigGuard.cs`
- `apps/Agentweaver.Mcp/Program.cs`

## Token stores and lifetimes

Agentweaver uses different storage strategies for different credential types because each type has a different replay risk and lifecycle.

### GitHub user tokens

GitHub tokens are the credentials Agentweaver uses to act on behalf of a signed-in user or to recheck org membership. They may include access token, optional refresh token, expiry, login, avatar URL, and scopes.

Token scopes separate storage domains:

- **per-user scope** for the default web sign-in, hosted/multi-user flows, and brokered MCP refresh-time org checks;
- **per-user scope only**; missing caller identity fails closed instead of falling back to a shared installation token.

In AKS, each authenticated user's GitHub token is stored in Azure Key Vault under a per-user key (`ghtok-user--{base32(userId)}`) and is never written to shared storage. The Key Vault token store no longer mirrors tokens to the workspace PVC. Local development may use Windows Credential Manager or per-scope JSON files under the developer data directory, depending on platform. A signed-out tombstone is stored to distinguish "user explicitly signed out" from "never signed in".

A refresh helper centralizes token retrieval. It returns still-valid tokens directly, serializes refreshes per scope to avoid refresh races, signs out when refresh is impossible, and avoids logging raw token values. In Key Vault-backed deployments, the token store also provides a short-lived per-scope refresh lease so only one API replica rotates an expiring GitHub token while other callers re-read the stored winner. Local stores fall back to an in-process per-scope gate.

### OAuth authorization state and web exchange codes

OAuth bootstrap artifacts are persisted in `MemoryDbContext` (Postgres in production, SQLite in development) so that any API replica can validate or redeem them — the load balancer can send the follow-up request to a different pod than the one that issued the code:

- **GitHub CSRF states** (`OAuthState` table): armed at `/auth/github/authorize`, redeemed atomically at `/auth/github/callback` via conditional `ExecuteDeleteAsync` on the state token.
- **MCP pending authorizations** and **Agentweaver authorization codes** (`McpPendingAuthorization`, `McpAuthorizationCode` tables): broker state for the MCP OAuth 2.1 AS.
- **Browser one-time session exchange codes** (`WebSessionExchangeCode` table): issued at the callback and redeemed at `POST /api/auth/session/exchange`. At-most-once redemption is enforced across replicas via a conditional `ExecuteDeleteAsync` on `Code`.

All entries are short-lived (60 s – 10 min TTL). Expired rows are purged opportunistically on Postgres; SQLite/dev relies on read-time expiry checks. A pod restart or scaling event cannot orphan an in-flight login — any replica in the pool will handle the follow-up request.

### OAuth refresh tokens

MCP refresh tokens are opaque to clients and stored only as SHA-256 hashes. A database disclosure should not let an attacker replay plaintext refresh tokens.

Refresh tokens have two lifetimes:

- a **sliding lifetime** extended on successful rotation;
- an **absolute lifetime** that caps the whole chain.

Every refresh consumes the presented token and creates a successor in the same chain. Presenting a consumed or revoked token is treated as possible theft and revokes the entire chain.

### Dynamic clients and revoked JTIs

Dynamic client registrations persist non-secret client IDs and registered redirect URIs. Access-token revocation persists JWT IDs (`jti`) with expiry timestamps so the API can reject revoked JWTs until they would naturally expire.

Where this lives:

- `packages/Agentweaver.Domain/IGitHubTokenStore.cs`
- `apps/Agentweaver.Api/Auth/OsCredentialStoreGitHubTokenStore.cs`
- `apps/Agentweaver.Api/Auth/FileSystemGitHubTokenStore.cs`
- `apps/Agentweaver.Api/Auth/GitHubTokenRefreshService.cs`
- `apps/Agentweaver.Api/Auth/OAuth/McpRefreshTokenStore.cs`
- `apps/Agentweaver.Api/Auth/OAuth/McpClientStore.cs`
- `apps/Agentweaver.Api/Memory/MemoryDbContext.cs`

## Middleware exemptions and bootstrap surfaces

Security middleware cannot simply protect every route, because some routes exist specifically to let unauthenticated clients discover or obtain credentials. Agentweaver therefore uses explicit exemptions.

### API bearer-token middleware

The API bearer-token middleware applies to `/api/*` except:

- ping/health routes;
- the web session exchange route that redeems the one-time browser sign-in code.

That exchange route is exempt because the one-time code is the credential being exchanged for a bearer token. Requiring a bearer token there would create a sign-in loop.

### API org-authorization middleware

Org authorization exempts:

- health routes;
- web and API auth bootstrap routes;
- MCP routes;
- OAuth Authorization Server routes;
- well-known discovery routes.

The rule is not "these routes are unimportant." The rule is "these routes either must be public bootstrap/discovery or are protected by a different layer." OAuth endpoints have their own validation and rate limiting; MCP has its own bearer middleware in HTTP mode.

### MCP bearer middleware

HTTP-mode MCP exempts:

- healthz;
- OAuth Protected Resource metadata.

All MCP tool calls require a bearer token. MCP first accepts configured automation keys (pre-shared keys for machine-to-machine callers), then Agentweaver JWTs, then — while enabled — raw GitHub tokens as a transitional path. When a caller token is accepted, MCP forwards that same bearer token to the API so the backend sees the real caller identity rather than a shared service identity.

### Invariants to preserve when rebuilding

- Exemptions should be path-specific, documented, and intentionally small.
- Public bootstrap routes must have their own input validation and, where expensive, rate limits.
- Do not exempt a route merely because it is inconvenient to authenticate.
- Middleware ordering matters: authentication before authorization.

Where this lives:

- `apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs`
- `apps/Agentweaver.Api/Auth/GitHubOrgAuthorizationMiddleware.cs`
- `apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs`
- `apps/Agentweaver.Mcp/Program.cs`

## Agent-host warm-pool token delivery and `/configure`

AgentHost pods need a valid GitHub token for the signed-in user so they can call GitHub APIs, clone repositories, and act on behalf of that user. The AKS model is now: per-user Key Vault storage plus runtime fetch from a warm pod after one-time `/configure`.

- **Per-user source of truth.** OAuth callbacks store only the authenticated caller's token in `GitHubTokenScope.ForUser(login)`, backed by a Key Vault secret named `ghtok-user--{base32(userId)}`.
- **No shared storage mirror.** GitHub tokens are not written to the workspace PVC or any shared filesystem.
- **No user-token CSI mount.** AgentHost no longer creates per-run `SecretProviderClass` objects or mounts `/mnt/user-tokens`.
- **Brokered token, no sandbox vault access (issue #471).** The API resolves the run owner's token on the API side (which legitimately holds Key Vault access) and delivers it in-memory in the one-time `POST /configure` body. The AgentHost sandbox runs as a **dedicated managed identity (`agentweaver-agenthost-identity`) with no Key Vault role assignments**, so untrusted shell/tool execution inside the pod cannot exchange its workload-identity token for a vault token and read other users' secrets. The in-pod `KeyVaultUserTokenProvider` prefers the brokered token and only falls back to a direct vault fetch, which now fails closed because the identity has no KV roles.

### End-to-end flow

```mermaid
sequenceDiagram
    participant User as User (browser)
    participant API as Agentweaver API / Executor
    participant Claim as SandboxClaim
    participant Pod as Warm AgentHost pod
    participant KV as Azure Key Vault
    participant Agent as CopilotAIAgent

    User->>API: sign in (GitHub OAuth)
    API->>KV: store token as secret ghtok-user--{base32(userId)}
    API->>Claim: bind shared agentweaver-agent-host warm pool
    Claim-->>API: pod IP
    API->>KV: resolve run owner's token (API identity)
    API->>Pod: POST /configure(runId, turnBearerToken, copilotCredential, workingDirectory)
    Pod->>Pod: AgentHostRuntimeState.TryConfigure once
    Pod->>Agent: SetupAsync after configure
    Note over API,Pod: Broker fences the run snapshot before and after credential redemption
    API->>Pod: POST /a2a/agent/v1/message:stream
    Note over API,Pod: Authorization: Bearer {turnBearerToken}
```

`AgentHostRuntimeState` is the mutable singleton that bridges warm-pool startup and per-run configuration. If a pod starts without `RunId`, `AgentHostStartupService` enters standby and logs that it is waiting for `/configure`. If a pod is launched with env vars for backward compatibility, `InitializeFromOptions` seeds the same runtime state and SetupAsync runs immediately.

`POST /configure` accepts `{ runId, turnBearerToken, copilotCredential, workingDirectory? }`. `copilotCredential` contains an opaque snapshot reference plus the bounded credential redeemed by `GitHubCapabilityBroker` for the run's unattended Copilot purpose. AgentHost rejects absent, expired, and run-mismatched credentials and has no Key Vault, mounted-file, shared-store, or ambient-token fallback. `workingDirectory` is the run's `WorktreePath`; when present, AgentHost passes it to `SetupAsync` so file tools use the same shared worktree named by the run's system prompt. The endpoint returns `400` when `runId` or the credential is missing and `409` when the pod was already configured. It is excluded from the readiness gate so standby pods can receive configuration before they are A2A-ready.

### Security model

- `/configure` is **one-time**: `Interlocked.CompareExchange` prevents retargeting an already configured warm pod.
- `/configure` is **not protected by `TurnBearerToken`** because it delivers that token. The guard is Kubernetes NetworkPolicy: AgentHost ingress on port `8088` is restricted to API/worker pods.
- `POST /a2a/agent/v1/message:stream` is unchanged: it still requires `Authorization: Bearer {per-run token}` and rejects mismatches.
- `TurnBearerToken` is no longer written into `SandboxClaim.spec.env` in etcd; it travels over in-cluster HTTP from executor to claimed pod.
- User GitHub token isolation moved from infrastructure-layer CSI projection (pod filesystem contained only one user's file) to application-layer brokering: the API resolves the run owner's token and delivers it in the one-time `/configure` body, and the AgentHost sandbox runs as a dedicated managed identity (`agentweaver-agenthost-identity`) with **no Key Vault role assignments** (issue #471), so the pod cannot read any vault secret directly. This is an explicit trade-off for warm-pool prewarming.

### Configuration

`GitHubCapabilityBroker` redeems only immutable, purpose-bound run snapshots. AgentHost receives the bounded Copilot credential in its one-time `/configure` request and rejects missing, expired, or run-mismatched credentials. It has no Key Vault, CSI, shared-filesystem, configuration-token, or ambient-token compatibility path.

> **Full deep-dive:** [Agent-host token delivery](./agent-token-delivery.md) covers `AgentHostRuntimeState`, `KeyVaultUserTokenProvider`, the configure endpoint, and the security trade-off.

Where this lives:

- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`
- `apps/Agentweaver.Api/Sandbox/IRunSubmittingUserResolver.cs`
- `apps/Agentweaver.AgentHost/Program.cs`
- `apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs`
- `apps/Agentweaver.AgentHost/AgentHostStartupService.cs`
- `apps/Agentweaver.AgentHost/KeyVaultUserTokenProvider.cs`
- `k8s/base/serviceaccount-agenthost.yaml`
- `k8s/base/sandbox-warmpool-agenthost.yaml`
- `k8s/base/sandbox-template-agenthost.yaml`

## Rebuild checklist

If rebuilding Agentweaver auth from scratch, implement the system in this order:

1. **Token abstraction:** define caller context with subject, GitHub login, optional org, and whether the credential is an Agentweaver JWT.
2. **GitHub web sign-in:** confidential code exchange, CSRF state, GitHub `/user` lookup, secure token storage, one-time browser exchange code.
3. **Bearer middleware:** validate Agentweaver JWTs, check revocation, fall back to raw GitHub `/user`, then attach caller context.
4. **Org authorization:** enforce configured org/team, account for SAML, separate denied from inconclusive, cache only stable answers.
5. **OAuth Authorization Server:** discovery, JWKS, authorize, token, DCR, revoke, PKCE S256, redirect policy, brokered GitHub login.
6. **MCP Resource Server:** protected-resource metadata, bearer challenge, JWKS-based JWT validation, downstream token forwarding.
7. **Stores:** OS/file GitHub token store, hashed rotating refresh-token store, dynamic client registration store, `jti` denylist.
8. **Production guards:** fail fast for auth bypass flags and missing public issuer/audience config.

The central design rule is: **GitHub proves the human, Agentweaver narrows that proof to its own resource, and every shortcut must either be short-lived, single-use, explicitly public, or development-only.**

## Copilot model-turn token scope guard

Copilot model turns must run with the submitting user's Copilot-entitled GitHub token. `CopilotAIAgent.ResolveTokenScope` rejects missing user identity and rejects the GitHub App installation scope for model turns (`packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:398`). The platform no longer treats installation scope as a normal operational fallback; long-running or unattended work must preserve the originating user identity or use a future explicit system identity.
