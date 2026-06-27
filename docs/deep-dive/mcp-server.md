# MCP Server — Deep Dive

## Purpose & Scope (MCP RS vs the API's OAuth AS)

`Agentweaver.Mcp` is the Model Context Protocol (MCP) surface for Agentweaver. It can run in two transports:

- **stdio mode** (`--stdio`) for local MCP hosts.
- **HTTP mode** at `/mcp`, where the process is an OAuth 2.1 **Resource Server (RS)** and protects tool calls with bearer authentication.

The MCP process does **not** issue OAuth tokens. Token issuance lives in `Agentweaver.Api`, which hosts the OAuth 2.1 **Authorization Server (AS)** endpoints: RFC 8414 metadata, `/oauth/authorize`, `/oauth/token`, `/oauth/jwks`, `/oauth/register`, and `/oauth/revoke` (`apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:7-20`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:28-67`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:69-243`, `apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:245-338`).

The boundary is:

- **AS (`Agentweaver.Api`)**: brokers GitHub login, enforces GitHub org membership before issuing codes/tokens, signs short-lived JWT access tokens, publishes JWKS, rotates refresh tokens, and denylists revoked access-token `jti`s (`apps/Agentweaver.Api/Program.cs:123-133`, `apps/Agentweaver.Api/Auth/OAuth/McpOAuthBrokerService.cs:101-117`, `apps/Agentweaver.Api/Auth/OAuth/McpOAuthBrokerService.cs:168-210`, `apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs:7-20`, `apps/Agentweaver.Api/Auth/OAuth/McpRefreshTokenStore.cs:15-24`).
- **RS (`Agentweaver.Mcp`)**: advertises protected-resource metadata, challenges unauthenticated MCP calls with RFC 9728 discovery information, validates Agentweaver-minted JWTs offline using the AS JWKS, and forwards the caller's bearer token to the API for per-user identity (`apps/Agentweaver.Mcp/Program.cs:75-105`, `apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:51-125`, `apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:11-17`, `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:43-59`).

Production pins issuer/audience to the public host on both sides. The MCP RS refuses to start in Production if `Auth:Mcp:Issuer` or `Auth:Mcp:Audience` is missing (`apps/Agentweaver.Mcp/Program.cs:13-32`), and the API AS has the same fail-fast guard for `Auth:OAuth:Issuer`/`Auth:OAuth:Audience` because MCP forwards public-host JWTs to the API over an internal service address (`apps/Agentweaver.Api/Security/OAuthConfigGuard.cs:3-18`, `apps/Agentweaver.Api/Security/OAuthConfigGuard.cs:21-50`).

## Protected Resource Metadata (RFC 9728: paths, document shape, why both bare + /mcp forms)

In HTTP mode, the MCP RS serves RFC 9728 protected-resource metadata without authentication at both:

- `GET /.well-known/oauth-protected-resource`
- `GET /.well-known/oauth-protected-resource/mcp`

Both routes return the same document (`apps/Agentweaver.Mcp/Program.cs:79-100`). The returned shape is:

```json
{
  "resource": "https://HOST/mcp",
  "authorization_servers": ["https://HOST"],
  "bearer_methods_supported": ["header"],
  "scopes_supported": ["mcp:invoke"],
  "resource_documentation": "https://HOST/docs"
}
```

The issuer is `Auth:Mcp:Issuer` when configured; otherwise it is derived from the incoming request scheme and host. The `resource` is always `{issuer}/mcp` (`apps/Agentweaver.Mcp/Program.cs:82-97`).

The server exposes both the bare and `/mcp`-suffixed forms because path-aware clients such as Copilot CLI / VS Code probe the resource-suffixed well-known URI. The code comment calls this out directly (`apps/Agentweaver.Mcp/Program.cs:79-81`), and the Kubernetes `HTTPRoute` also routes both exact paths to the MCP service for client compatibility (`k8s/mcp-httproute.yaml:27-39`).

The bearer middleware explicitly bypasses auth for `/healthz` and any `/.well-known/oauth-protected-resource*` path, so clients can discover metadata before they possess a token (`apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:51-59`).

## Bearer Token Validation (McpBearerTokenMiddleware: JWT validation, JWKS fetch, audience/issuer, jti)

The HTTP middleware protects all non-health, non-protected-resource-metadata MCP requests (`apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:51-60`). Missing bearer credentials return `401` with a `WWW-Authenticate` challenge that includes `resource_metadata="{issuer}/.well-known/oauth-protected-resource"`; invalid credentials add `error="invalid_token"` (`apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:61-69`, `apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:162-177`).

Validation order:

1. **Static Agentweaver API key fast path** for automation/CI. Keys are read from `Auth:ApiKey`/`Auth:User` or `Auth:Keys:*` (`apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:73-81`, `apps/Agentweaver.Mcp/McpApiKeyRegistry.cs:5-13`, `apps/Agentweaver.Mcp/McpApiKeyRegistry.cs:18-37`).
2. **Agentweaver OAuth access token**. The middleware calls `McpAccessTokenValidator`, which first rejects non-JWT-shaped tokens, resolves issuer/audience, fetches signing keys, validates RS256 signature, `iss`, `aud`, lifetime, and extracts `sub`, `gh_login`, `jti`, and `org` (`apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:83-94`, `apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:45-94`).
3. **Transitional raw GitHub token path**, enabled unless `Auth:Mcp:AllowGitHubPassthrough=false`, validates with `GET https://api.github.com/user` and caches results (`apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:96-131`, `apps/Agentweaver.Mcp/McpBearerTokenMiddleware.cs:134-160`).

JWT details:

- The RS resolves issuer from `Auth:Mcp:Issuer`, falling back to the request host; audience from `Auth:Mcp:Audience`, falling back to `{issuer}/mcp` (`apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:52-54`, `apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:123-139`).
- JWKS comes from `Auth:Mcp:JwksUri` when set, otherwise `{issuer}/oauth/jwks`, and is cached for 10 minutes (`apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:96-114`).
- The validator permits only RS256 with 30 seconds of clock skew (`apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:59-70`).
- `jti` is extracted by the MCP RS, but the authoritative denylist check happens in the API after the MCP server forwards the same bearer token downstream. The API validates the Agentweaver JWT, rejects denied `jti`s, and sets caller context for per-user authorization (`apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:83-87`, `apps/Agentweaver.Mcp/AgentweaverApiClient.cs:43-59`, `apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:168-190`, `apps/Agentweaver.Api/Auth/OAuth/McpRefreshTokenStore.cs:139-160`).

The AS mints these JWTs with `sub`, `scope=mcp:invoke`, `gh_login`, `jti`, optional `org`, `iss`, `aud`, `iat`, `nbf`, and `exp`; default lifetime is 15 minutes (`apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs:23-27`, `apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs:77-107`). `/oauth/jwks` publishes the public RSA key as `{ kty, use, alg, kid, n, e }` (`apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:56-67`, `apps/Agentweaver.Api/Auth/OAuth/McpTokenService.cs:127-140`).

## Client Discovery Chain (mermaid sequenceDiagram: client -> /mcp 401 -> protected-resource metadata -> AS metadata -> authorize/token -> /mcp with bearer)

```mermaid
sequenceDiagram
    autonumber
    participant C as MCP client
    participant RS as Agentweaver.Mcp (Resource Server)
    participant AS as Agentweaver.Api (Authorization Server)
    participant GH as GitHub

    C->>RS: POST /mcp (no bearer)
    RS-->>C: 401 WWW-Authenticate: Bearer resource_metadata="https://HOST/.well-known/oauth-protected-resource"
    C->>RS: GET /.well-known/oauth-protected-resource[/mcp]
    RS-->>C: resource=https://HOST/mcp<br/>authorization_servers=[https://HOST]
    C->>AS: GET /.well-known/oauth-authorization-server[/mcp]
    AS-->>C: authorization_endpoint, token_endpoint, jwks_uri, scopes, PKCE S256
    C->>AS: GET /oauth/authorize?response_type=code&code_challenge_method=S256&resource=https://HOST/mcp
    AS->>GH: Redirect user to GitHub OAuth
    GH-->>AS: GitHub callback code
    AS->>AS: Enforce org membership; issue single-use auth code
    AS-->>C: Redirect to client redirect_uri?code=...
    C->>AS: POST /oauth/token (code + code_verifier)
    AS-->>C: Bearer JWT access_token (+ refresh_token)
    C->>RS: POST /mcp Authorization: Bearer access_token
    RS->>AS: GET /oauth/jwks (or configured internal JWKS URI), cached
    RS->>RS: Validate RS256 + iss + aud + exp
    RS->>AS: Forward bearer to Agentweaver API tool endpoint
    AS->>AS: Validate JWT and jti denylist
    AS-->>RS: API response
    RS-->>C: MCP tool result
```

The AS metadata advertises the authorize, token, registration, JWKS, and revocation endpoints, supports only `response_types_supported=["code"]`, `grant_types_supported=["authorization_code","refresh_token"]`, `code_challenge_methods_supported=["S256"]`, and public clients with `token_endpoint_auth_methods_supported=["none"]` (`apps/Agentweaver.Api/Endpoints/OAuthServerEndpoints.cs:28-55`). The OAuth overview in `docs/mcp-oauth.md` documents the same AS/RS split and token claim intent (`docs/mcp-oauth.md:5-15`, `docs/mcp-oauth.md:27-60`, `docs/mcp-oauth.md:114-127`).

## Exposed MCP Tools / Capabilities

Tool classes are discovered from the MCP assembly (`WithToolsFromAssembly`) and exposed over the selected transport (`apps/Agentweaver.Mcp/Program.cs:59-71`). Most tools are thin proxies over the Agentweaver API; the client wrapper forwards the validated inbound bearer token when present, falling back to the configured shared key only in contexts without an inbound HTTP request, such as stdio (`apps/Agentweaver.Mcp/AgentweaverApiClient.cs:43-59`).

| Capability area | Tools | Source |
|---|---|---|
| Projects | `project_list`, `project_get`, `project_create`, `project_rename`, `project_relink`, `project_delete`, `project_configure`, `project_list_runs` | `apps/Agentweaver.Mcp/Tools/ProjectTools.cs:13-132` |
| Runs | `run_submit`, `run_status`, `run_watch`, `run_review`, `run_show_artifacts`, `run_get_file`, `run_retry`, `run_archive` | `apps/Agentweaver.Mcp/Tools/RunTools.cs:19-160` |
| Coordinator orchestration | `coordinator_start`, outcome-spec get/confirm/revise, work-plan/children reads, `coordinator_steer`, `orchestration_topology` | `apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:12-119` |
| Backlog / board | capture/edit/delete/move/reorder/archive board tasks, settings, workflow stages, `send_all_backlog_to_ready`, `backlog_decompose_spec` | `apps/Agentweaver.Mcp/Tools/BacklogTools.cs:27-240` |
| Memory / decisions / sessions | decision inbox submit/list/merge/reject, direct decisions, `squad_decide`, memory record/list/get/search/export/import, session start/current/update | `apps/Agentweaver.Mcp/Tools/MemoryTools.cs:23-246` |
| Team casting | `team_get`, `team_cast`, `team_member_add`, `team_member_retire`, `team_member_get_charter` | `apps/Agentweaver.Mcp/Tools/TeamTools.cs:12-111` |
| GitHub auth helpers | `github_status`, `github_signout`, `github_signin` device flow | `apps/Agentweaver.Mcp/Tools/GitHubAuthTools.cs:13-39` |
| Workspace browsing | `list_project_workspace_refs`, `list_project_workspace`, `get_project_workspace_file` | `apps/Agentweaver.Mcp/Tools/WorkspaceTools.cs:13-68` |
| Workflows | `workflows_list`, `workflow_get`, `workflows_sync`, `workflow_generate`, `workflow_save` | `apps/Agentweaver.Mcp/Tools/WorkflowTools.cs:19-101` |
| Blueprints / catalog | `list_blueprints`, `validate_blueprint`, `blueprint_generate`, `catalog_list_roles`, `catalog_list_scenarios` | `apps/Agentweaver.Mcp/Tools/BlueprintTools.cs:14-52`, `apps/Agentweaver.Mcp/Tools/CatalogTools.cs:12-24` |
| Diagnostics / sandbox | `diagnostics_get`, `heartbeat_status`, `sandbox_policy_get`, `sandbox_policy_set` | `apps/Agentweaver.Mcp/Tools/DiagnosticsTools.cs:12-24`, `apps/Agentweaver.Mcp/Tools/SandboxPolicyTools.cs:12-29` |

Unverified: the generated protocol-level tool list may include schema details from the MCP SDK beyond the static C# attributes above; this document only records the source-declared tool names and descriptions.

## Hosting & Routing (mcp-service, mcp-httproute, /mcp/health rewrite, network policy)

The MCP Kubernetes `Service` is `agentweaver-mcp` in namespace `agentweaver`, selects pods labeled `app: agentweaver-mcp`, and exposes TCP port `8080` as a ClusterIP (`k8s/mcp-service.yaml:1-18`).

`HTTPRoute` exposes three public routing shapes on `${HOST}`:

1. `Exact /mcp/health` rewrites to internal `/healthz` before sending to `agentweaver-mcp:8080` (`k8s/mcp-httproute.yaml:14-26`). The app maps `/healthz` to `{ "status": "healthy" }` in HTTP mode (`apps/Agentweaver.Mcp/Program.cs:75-78`).
2. Exact `/.well-known/oauth-protected-resource` and `/.well-known/oauth-protected-resource/mcp` route to the MCP RS unauthenticated (`k8s/mcp-httproute.yaml:27-39`).
3. `PathPrefix /mcp` routes MCP protocol traffic to `agentweaver-mcp:8080` (`k8s/mcp-httproute.yaml:40-46`).

The deployment pins MCP RS OAuth validation to the public host with `Auth__Mcp__Issuer=https://${HOST}` and `Auth__Mcp__Audience=https://${HOST}/mcp`, but fetches JWKS from the internal API service (`Auth__Mcp__JwksUri=http://agentweaver-api:8080/oauth/jwks`) to avoid public gateway hairpinning (`k8s/mcp-deployment.yaml:48-64`). Its probes hit `/healthz` on port `8080` (`k8s/mcp-deployment.yaml:76-89`).

`secretprovider-mcp.yaml` mounts MCP-related secrets from Azure Key Vault (`mcp-api-key`, `mcp-auth-api-key`, `mcp-auth-user`) into the Kubernetes secret `agentweaver-mcp-secrets` (`k8s/secretprovider-mcp.yaml:16-37`). The network policy selects `app: agentweaver-mcp` pods and allows ingress on TCP `8080` only from the Gateway pod selector and the `aks-istio-ingress` namespace selector (`k8s/networkpolicy-mcp.yaml:8-24`).

## Gotchas (the well-known path-aware discovery routing issue and its fix)

- **Well-known paths are not under `/mcp`.** A route that only matches `PathPrefix /mcp` will never send `/.well-known/oauth-protected-resource` to the MCP service. The fix is the exact-match `HTTPRoute` rule for both protected-resource metadata paths before the `/mcp` prefix rule (`k8s/mcp-httproute.yaml:27-46`).
- **Path-aware clients probe the suffixed form.** Copilot CLI / VS Code compatibility requires `/.well-known/oauth-protected-resource/mcp`; serving only the bare path breaks discovery for those clients (`apps/Agentweaver.Mcp/Program.cs:79-100`, `k8s/mcp-httproute.yaml:27-36`).
- **Pin public issuer/audience in hosted deployments.** If MCP or API derives issuer/audience from internal gateway/service hosts, AS-minted tokens with `aud=https://HOST/mcp` fail validation on internal calls. Both processes have Production guards for this (`apps/Agentweaver.Mcp/Program.cs:13-32`, `apps/Agentweaver.Api/Security/OAuthConfigGuard.cs:3-18`).
- **MCP validates JWTs, API enforces revoked `jti`s.** The MCP RS extracts `jti` but does not own the denylist. It forwards the bearer token to the API, where `McpRefreshTokenStore.IsJtiDeniedAsync` rejects revoked access tokens (`apps/Agentweaver.Mcp/McpAccessTokenValidator.cs:83-87`, `apps/Agentweaver.Api/Security/ApiKeyAuthMiddleware.cs:168-190`, `apps/Agentweaver.Api/Auth/OAuth/McpRefreshTokenStore.cs:153-160`).
