# Onboarding and authentication experience

Agentweaver uses Microsoft Entra ID for browser sign-in. GitHub Apps provide separate capabilities after sign-in.

Scope: this page covers sign-in, setup readiness, GitHub capabilities, sign-out, and MCP authentication.

See also: [Overview](./00-overview.md), [Projects](./projects.md), [MCP client experience](./mcp-client.md), [Authentication guide](../guide/authentication.md), [MCP OAuth](../mcp-oauth.md), and [Auth & security deep dive](../deep-dive/auth-security.md).

## Mental model

The web UI and MCP server identify each caller.

- The web UI uses Microsoft Entra ID.
- The MCP server accepts Agentweaver broker tokens for its exact MCP resource.
- The GitHub Copilot App provides model-provider access.
- The GitHub Repo App provides repository access.

Each protected action maps to one caller. Agentweaver applies platform roles and project assignments to that caller.

## First-run web UI experience

Agentweaver checks the browser session before it shows the app shell. The loading state names this operation.

If no session exists, the page shows **Sign in with Microsoft Entra ID**. This action opens the configured Entra authorization endpoint.

The browser returns through `/auth/entra/callback`. Agentweaver keeps authorization details on the server.

For normal web sign-in, the callback returns a one-time exchange code to the frontend.
The frontend redeems it through `/api/auth/session/exchange`, which issues the browser
session. Entra's authorization code and the server-held PKCE verifier are a separate
exchange with Microsoft; do not confuse either with an MCP broker token.

## Setup readiness

A ready model provider is the first useful milestone. Agentweaver blocks AI work until this required row is ready.

A Platform Admin can authorize GitHub Copilot or activate a custom-key provider. Other users see **Unavailable to you** with recovery guidance.

- The completed row identifies the provider and its project or platform scope.
- Repository access is optional.
- Local agent work can continue without a GitHub repository.
- Pull-request publishing requires repository access.

During required setup, the Platform Admin can add and manage all supported model providers.
The administrator must choose one active provider.

When the provider is ready, select **Continue to Agentweaver**.
Agentweaver opens the app shell and starts a three-step product tour.

The tour introduces **Projects**, **Sessions**, and **Start task**.
The administrator can skip the tour or press Escape.
Agentweaver stores the completed tour for the signed-in user.

To start the tour again, open the settings menu and select **Take product tour**.

The setup pattern also shows loading, error, permission, and success states with text labels.

## GitHub capabilities

The two GitHub Apps have separate purposes:

- GitHub Copilot supplies AI access.
- The Repo App supplies repository access.

Authorize GitHub Copilot from Platform settings or Project settings. The effective status identifies the provider and scope.

Connect the GitHub Repo App from Account settings, or authorize repository access from a repository action elsewhere in the product. Agentweaver returns to the current task after the browser handoff.

GitHub authorization does not replace Entra identity. It does not grant an Agentweaver role or project membership.

### Signed-in shell

After required setup, Agentweaver shows the normal shell. The project gallery offers local and GitHub-backed project creation.

The blank-project path does not require repository access. The GitHub-backed path requests repository access before it loads repositories.

### Sign-out

The signed-in account menu includes **Sign out**. This action ends the Agentweaver session and returns the browser to `/`.

Sign-out affects future authenticated calls. It does not retroactively cancel server-side runs that are already in progress; those runs continue according to their own run lifecycle and review state.

## MCP client connection experience

An MCP client connects to Agentweaver either locally or over HTTP:

| Client mode | What the user points at | What authenticates the call |
|---|---|---|
| Local STDIO | A command that starts the Agentweaver MCP app with `--stdio` | The process requires `AGENTWEAVER_TOKEN` to contain an Agentweaver broker token for the exact MCP resource and `mcp:invoke`; the API validates it again and enforces project authorization. |
| Hosted HTTP | The Agentweaver MCP URL ending in `/mcp` | Each request sends `Authorization: Bearer <token>` and the MCP server validates it before invoking tools. |

For a hosted MCP client, the user experience is normally discovery-driven. The user adds the Agentweaver MCP server URL to the client. The client tries to call `/mcp`. If it has no bearer token, the server responds with `401` and a `WWW-Authenticate` challenge that points to OAuth Protected Resource metadata. The client fetches that metadata, learns the MCP resource and authorization server issuer, fetches Authorization Server metadata, then runs a PKCE authorization-code flow.

The OAuth-capable client may also dynamically register its redirect URI. Local native clients use
literal loopback redirect URIs such as `http://127.0.0.1:<port>/callback` or
`http://[::1]:<port>/callback`. Agentweaver rejects hostnames, fragments, and embedded user info.
Hosted Claude connectors use the fixed public client ID `agentweaver-claude`
instead of dynamic registration. That client accepts only
`https://claude.ai/api/mcp/auth_callback`, has no secret, and requires S256
PKCE.

For loopback redirect URIs, registered URI matching ignores the port when the scheme, host, and path match. This follows RFC 8252: native clients often bind a fresh local port for each sign-in attempt. Token redemption still binds to the exact redirect URI used in the authorization request, so the authorization code cannot be moved to a different redirect target.

## MCP OAuth and bearer-token flow

The MCP OAuth flow has four visible phases:

1. **Discovery.** The client learns that `/mcp` is protected, discovers the protected-resource metadata, then discovers Agentweaver's Authorization Server metadata and JWKS URI.
2. **Sign-in and consent.** The client opens `/oauth/authorize` in a browser. Without an Entra-backed browser session, the page offers sign-in through `/auth/entra/authorize`; the Entra callback resumes the saved request through `/oauth/resume`. Agentweaver then asks the human to approve the client's requested access. An existing consent can be reused unless the client requests consent again.
3. **Token issuance.** OpenIddict returns an authorization code to the client's validated redirect URI. The client redeems it at `/oauth/token` with its PKCE verifier, the same redirect URI, and the exact MCP resource. An approved `offline_access` request enables refresh tokens. Denial returns an OAuth error, not access.
4. **Tool use.** The client calls `/mcp` with `Authorization: Bearer <Agentweaver JWT>`. The MCP server validates the JWT offline using JWKS, then forwards the same bearer token to the API during tool calls.

The broker JWT is signed with keyed RS256 and bound to the single exact
`<public-origin>/mcp` audience. Its subject comes from the Entra browser identity,
not a GitHub login. MCP uses OpenIddict discovery/JWKS validation, then checks the issuer,
audience, signing key ID, algorithm, subject, and `mcp:invoke` scope. The API independently
validates the forwarded credential and enforces platform and project authorization.

Refresh tokens are opaque reference tokens managed by OpenIddict. Refresh-token family
tracking and replay handling prevent a consumed or revoked grant from remaining a reusable
credential. Clients should use their OAuth library's refresh flow, not copy tokens manually.

## MCP broker-only acceptance

There is one public MCP credential class: an Agentweaver broker token for the exact
resource and scope. There is **no** automation-key, raw-GitHub, or direct-Entra fallback.

- No token: `401` with protected-resource metadata and scope in the challenge, without an error.
- Invalid token: `401` with `invalid_token`.
- Valid broker token without `mcp:invoke`: `403` with `insufficient_scope`.
- Health and protected-resource discovery remain public.

HTTP tool calls forward only the validated broker token. STDIO has no inbound HTTP
context, so it forwards `AGENTWEAVER_TOKEN`, which must itself be a broker token.
It never falls back to an API key.

Authentication is not authorization. Project endpoints apply the required **Viewer**,
**Contributor**, or **Owner** role in addition to platform access. GitHub capability consent
does not create those assignments, and a GitHub username is not an administrator grant.
See `apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:67`,
`apps/Agentweaver.Mcp/AgentweaverApiClient.cs:359`, and
`apps/Agentweaver.Api/Security/ProjectAuthorization.cs:59`.

## GitHub capability tools in MCP

The MCP server exposes two explicit GitHub App capabilities for assistant-driven sessions. The Repo App is caller-scoped; the Copilot App is project-scoped and can only be connected by a Project Owner:

| Tool | User-facing purpose | What the user sees |
|---|---|---|
| `github_repo_app_connect` | Start a Repo App browser handoff for the current caller. | An opaque transaction ID, browser URL, and expiry. Open the URL in a browser to continue GitHub authorization. |
| `github_repo_app_authorization_status` | Poll the caller's Repo App authorization. | A redacted lifecycle state and expiry; no token, installation, repository, or permission data. |
| `github_repo_app_disconnect` | Remove the caller's Repo App connection. | A de-privileging confirmation. |
| `project_copilot_app_connect` | Start a project-pinned Copilot App browser handoff. | An opaque transaction ID, browser URL, and expiry for an authorized Project Owner. |
| `project_copilot_app_authorization_status` | Poll the project's Copilot App authorization. | A redacted lifecycle state and expiry, scoped to the initiating caller and project. |
| `project_copilot_app_disconnect` | Remove a project's Copilot App connection. | A de-privileging confirmation for an authorized Project Owner. |
| `project_github_capability_status` | Inspect unattended GitHub readiness for a project. | Server-derived, redacted capability readiness only. |

Before GitHub-backed work, an agent calls `github_repo_app_connect`, asks the user to open the returned browser URL, then polls `github_repo_app_authorization_status`. The API transfers the callback cookie directly to the browser through a one-time opaque handoff; OAuth state, callback cookies, tokens, installation details, repository data, and permissions never enter MCP output.

For unattended project work, a Project Owner repeats that browser flow with `project_copilot_app_connect`, polls its authorization status, and checks `project_github_capability_status`. Disconnect tools intentionally remove authority rather than exposing or transferring it.

## Troubleshooting and edge cases

### The web UI shows the sign-in page again

The startup gate shows the sign-in page when no valid Entra session exists. Select **Sign in with Microsoft Entra ID**.

### The sign-in page shows an error

The page shows Entra callback and session errors near the sign-in action. Start a new sign-in attempt.

### The GitHub project picker requires repository access

In **Create project from GitHub**, select **Authorize repository access**. If authorization fails to start, try the action again.

### MCP client gets `Bearer token required`

The MCP client called hosted `/mcp` without a bearer token. OAuth-aware clients should follow the
`WWW-Authenticate` challenge to protected-resource metadata, discover the Authorization Server,
and run the OAuth flow. Stdio clients must be configured with an Agentweaver broker token.

### MCP client gets `invalid_token`

The bearer token is not a valid Agentweaver broker token. Check the exact issuer and
`<public-origin>/mcp` audience, keyed RS256 signature, lifetime, subject, and `mcp:invoke` scope.
Raw Entra, GitHub, and API-key credentials are not accepted.

### Local MCP redirect fails on loopback

Use a literal loopback HTTP redirect URI such as `http://127.0.0.1:<port>/callback` or
`http://[::1]:<port>/callback`. The client must redeem the authorization code with the exact
redirect URI from the authorization request.

### Platform or project access is denied

Check the signed-in Entra account and its platform role or project assignment.
Ask an authorized administrator or project owner to review access. Reconnecting GitHub
cannot repair a missing Agentweaver role. A separate Repo App repository-access failure
must be resolved through the GitHub capability connection and authorized repository selection.

### Token expires during an MCP session

Agentweaver JWT access tokens last eight hours by default. OAuth-capable clients should use the refresh token grant to rotate the refresh token and receive a new access token. If refresh fails because the refresh token expired, was reused, was revoked, or no longer matches the client, reconnect the MCP client and repeat the OAuth consent flow.

### GitHub capability handoffs expire

If the browser handoff expires before the user completes GitHub authorization, start a fresh Repo App or project Copilot App connection. The old opaque transaction cannot be reused.

## Experience guardrails

- The web UI uses Microsoft Entra ID for human sign-in.
- GitHub Apps provide model-provider and repository capabilities only.
- The web UI never asks users to paste a GitHub token.
- GitHub client secrets and GitHub access-token exchanges happen server-side.
- Browser redirects carry one-time codes, not long-lived GitHub tokens.
- OAuth bootstrap and discovery routes are public because clients need them before they have a token.
- Web API calls use the authenticated browser session or supported Entra credentials;
  external MCP calls use broker tokens.
- MCP validates Agentweaver JWTs offline via JWKS, then forwards the caller's bearer token to the API.
- Platform roles and project assignments remain authoritative after broker authentication.
- Raw Entra tokens, GitHub tokens, and API keys are rejected at the public MCP boundary.

Humans sign in with Entra. They authorize each GitHub capability only when the current task requires it.

<details id="diagram-context-experience-onboarding-auth-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Browser sign-in and readiness</td></tr>
<tr><td>takeaway</td><td>Entra establishes identity; AI readiness and optional GitHub capabilities remain separate.</td></tr>
<tr><td>group-title-0</td><td>BROWSER SIGN-IN</td></tr>
<tr><td>group-title-1</td><td>SESSION AND SETUP</td></tr>
<tr><td>Browser</td><td>Browser</td></tr>
<tr><td>Browser</td><td>Start Entra sign-in</td></tr>
<tr><td>Browser</td><td>/auth/entra/authorize</td></tr>
<tr><td>Browser</td><td>The API binds this request to expiring browser state.</td></tr>
<tr><td>Auth API</td><td>Auth API</td></tr>
<tr><td>Auth API</td><td>Save state and PKCE</td></tr>
<tr><td>Auth API</td><td>verifier + nonce</td></tr>
<tr><td>Auth API</td><td>The verifier stays server-side. Redirect carries a challenge.</td></tr>
<tr><td>Microsoft Entra</td><td>Microsoft Entra</td></tr>
<tr><td>Microsoft Entra</td><td>Authenticate identity</td></tr>
<tr><td>Microsoft Entra</td><td>code + state callback</td></tr>
<tr><td>Microsoft Entra</td><td>Not GitHub login; not repository authorization.</td></tr>
<tr><td>Ready app shell</td><td>Ready app shell</td></tr>
<tr><td>Ready app shell</td><td>Continue when ready</td></tr>
<tr><td>Ready app shell</td><td>platform access + AI</td></tr>
<tr><td>Ready app shell</td><td>GitHub Repo App access is optional for GitHub work.</td></tr>
<tr><td>Browser session</td><td>Browser session</td></tr>
<tr><td>Browser session</td><td>One-time code exchange</td></tr>
<tr><td>Browser session</td><td>session credential</td></tr>
<tr><td>Browser session</td><td>Frontend exchanges a code; no raw token in callback URL.</td></tr>
<tr><td>Callback checks</td><td>Callback checks</td></tr>
<tr><td>Callback checks</td><td>Consume state once</td></tr>
<tr><td>Callback checks</td><td>redeem code + verifier</td></tr>
<tr><td>Callback checks</td><td>Validate Entra response and bound browser callback.</td></tr>
<tr><td>e0</td><td>authorize</td></tr>
<tr><td>e1</td><td>redirect</td></tr>
<tr><td>e2</td><td>callback</td></tr>
<tr><td>e3</td><td>exchange</td></tr>
<tr><td>e4</td><td>setup check</td></tr>
<tr><td>note</td><td>Session identity does not grant repository access, provider readiness, or project membership.</td></tr>
<tr><td>n0</td><td>The API binds this request
to expiring browser state.</td></tr>
<tr><td>n1</td><td>The verifier stays server-side.
Redirect carries a challenge.</td></tr>
<tr><td>n2</td><td>Not GitHub login;
not repository authorization.</td></tr>
<tr><td>n3</td><td>GitHub Repo App access is
optional for GitHub work.</td></tr>
<tr><td>n4</td><td>Frontend exchanges a code;
no raw token in callback URL.</td></tr>
<tr><td>n5</td><td>Validate Entra response and
bound browser callback.</td></tr>
<tr><td>groups</td><td>BROWSER SIGN-IN; SESSION AND SETUP</td></tr>
</tbody></table>
</details>

<details id="diagram-context-experience-onboarding-auth-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>MCP uses broker credentials</td></tr>
<tr><td>takeaway</td><td>An Entra-backed consent flow issues the exact-resource credential accepted by MCP.</td></tr>
<tr><td>group-title-0</td><td>DISCOVERY AND HUMAN CONSENT</td></tr>
<tr><td>group-title-1</td><td>TOKEN AND RESOURCE ENFORCEMENT</td></tr>
<tr><td>MCP client</td><td>MCP client</td></tr>
<tr><td>MCP client</td><td>Discover resource/issuer</td></tr>
<tr><td>MCP client</td><td>401 challenge + metadata</td></tr>
<tr><td>MCP client</td><td>Use the advertised resource and authorization server.</td></tr>
<tr><td>Browser consent</td><td>Browser consent</td></tr>
<tr><td>Browser consent</td><td>Entra-backed session</td></tr>
<tr><td>Browser consent</td><td>/oauth/authorize + PKCE</td></tr>
<tr><td>Browser consent</td><td>Show client and requested access; Allow or Deny.</td></tr>
<tr><td>OpenIddict</td><td>OpenIddict</td></tr>
<tr><td>OpenIddict</td><td>Bind grant and code</td></tr>
<tr><td>OpenIddict</td><td>client / redirect / resource</td></tr>
<tr><td>OpenIddict</td><td>Existing consent may skip a prompt; denial is not success.</td></tr>
<tr><td>Authorized API</td><td>Authorized API</td></tr>
<tr><td>Authorized API</td><td>Enforce resource access</td></tr>
<tr><td>Authorized API</td><td>project role / membership</td></tr>
<tr><td>Authorized API</td><td>MCP forwards the validated bearer; API checks again.</td></tr>
<tr><td>MCP boundary</td><td>MCP boundary</td></tr>
<tr><td>MCP boundary</td><td>Validate broker token</td></tr>
<tr><td>MCP boundary</td><td>issuer + RS256 + lifetime</td></tr>
<tr><td>MCP boundary</td><td>Exact single audience, subject and mcp:invoke.</td></tr>
<tr><td>Token exchange</td><td>Token exchange</td></tr>
<tr><td>Token exchange</td><td>Code + verifier</td></tr>
<tr><td>Token exchange</td><td>/oauth/token</td></tr>
<tr><td>Token exchange</td><td>Returns Agentweaver token; not raw Entra or GitHub.</td></tr>
<tr><td>e0</td><td>open</td></tr>
<tr><td>e1</td><td>allow</td></tr>
<tr><td>e2</td><td>code grant</td></tr>
<tr><td>e3</td><td>tool bearer</td></tr>
<tr><td>e4</td><td>forward</td></tr>
<tr><td>note</td><td>Invalid/missing token: 401. Missing scope: 403. No API-key or raw Entra/GitHub fallback.</td></tr>
<tr><td>n0</td><td>Use the advertised resource
and authorization server.</td></tr>
<tr><td>n1</td><td>Show client and requested
access; Allow or Deny.</td></tr>
<tr><td>n2</td><td>Existing consent may skip a
prompt; denial is not success.</td></tr>
<tr><td>n3</td><td>MCP forwards the validated
bearer; API checks again.</td></tr>
<tr><td>n4</td><td>Exact single audience,
subject and mcp:invoke.</td></tr>
<tr><td>n5</td><td>Returns Agentweaver token;
not raw Entra or GitHub.</td></tr>
<tr><td>groups</td><td>DISCOVERY AND HUMAN CONSENT; TOKEN AND RESOURCE ENFORCEMENT</td></tr>
</tbody></table>
</details>
