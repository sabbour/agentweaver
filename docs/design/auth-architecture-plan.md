# Auth architecture plan: retiring the hand-rolled auth middleware

- **Status:** Proposal (design/research only — no code changes in this task)
- **Author:** Tank (Squad)
- **Date:** 2026-08-02
- **Scope:** `apps/Agentweaver.Api` request authentication, the AKS ingress/mesh boundary,
  and an evaluation of [oauth2-proxy](https://github.com/oauth2-proxy/oauth2-proxy).

> **Read this first — a premise correction.** The brief for this work assumed Agentweaver
> runs on the **AKS Istio service mesh add-on** with sidecars and `istiod`. It does not.
> `scripts/azure/steps/10-create-cluster.mjs:307` provisions the cluster with
> `--enable-app-routing-istio`, i.e. the **application routing Gateway API implementation**
> (`GatewayClass: approuting-istio`, see `k8s/base/gateway.yaml`). Microsoft documents these
> as **mutually exclusive** offerings, and app routing explicitly does **not** support Istio
> CRDs or sidecar injection. Nothing in `k8s/` labels the `agentweaver` namespace for
> injection (`k8s/base/namespace.yaml` sets only Pod Security). **Every mesh-level option in
> Phase 2 below is therefore blocked today** until a deliberate infrastructure migration
> happens. This is the single most important finding in this document, and it changes the
> recommended order of work: fix the app-level design first (Phase 1), and treat the mesh as
> a *later, optional* hardening step with a real infrastructure prerequisite.

---

## 1. Root cause: why the current design keeps producing the same bug

### 1.1 What exists today

Three hand-rolled `RequestDelegate` middlewares run in sequence
(`apps/Agentweaver.Api/Program.cs:1039-1048`):

```
UseExceptionHandler → UseCors → UseRateLimiter
  → GitHubTokenAuthMiddleware                    (authentication)
  → PlatformRoleAuthorizationMiddleware  (Entra)  |  GitHubOrgAuthorizationMiddleware (GitHubLegacy)
  → <endpoints>
```

None of them is an `IAuthenticationHandler` / `IAuthorizationHandler` participating in the
ASP.NET Core authentication and authorization pipeline. Each instead carries its **own**
hardcoded allow-list of public paths:

| Component | Allow-list | Location |
|---|---|---|
| `GitHubTokenAuthMiddleware` | inline `if` chain: `/api/ping`, `/api/health`, `/api/version`, `/api/auth/session/exchange`, `/api/auth/config`, `…/webhooks/github` | `Security/ApiKeyAuthMiddleware.cs` `InvokeAsync` |
| `PlatformRoleAuthorizationMiddleware` | `ExemptPrefixes[]` — 12 entries | `Auth/PlatformRoleAuthorizationMiddleware.cs:14-30` |
| `GitHubOrgAuthorizationMiddleware` | `ExemptPrefixes[]` — 11 entries | `Auth/GitHubOrgAuthorizationMiddleware.cs:26-45` |
| OpenAPI security transformer | *fourth* copy of the same knowledge | `OpenApi/OpenApiSecurityTransformers.cs:71` |

So the answer to "is this endpoint public?" is encoded in **four** places, in three
different shapes (exact match, `StartsWithSegments` prefix, suffix match), and in **none**
of them next to the route definition.

### 1.2 The failure mode, in git history

The repository has a recurring, self-similar bug class:

- `582e161c` — `fix: exempt /api/version from auth middleware`
- `79164db9` — `fix: exempt /api/version from GitHubOrgAuthorizationMiddleware` *(the same
  endpoint, a second time, because the first fix only patched one of the lists)*
- `1eb628cd` — `fix(auth): exempt /api/auth/session/exchange from GitHub token middleware`
- `2087c6c8` — `fix: StartsWithSegments exempt prefix must not have trailing slash` *(a bug
  in the matching logic of the allow-list itself)*
- **Today:** `GET /api/server/info` is declared public at the route
  (`Endpoints/ProjectEndpoints.cs:46` — "public server metadata (no auth required)") and,
  per the motivating report, carries `.AllowAnonymous()`. The middleware 401s it anyway,
  because `/api/server/info` is not in the inline `if` chain. *(A separate PR by another
  agent is fixing this specific endpoint right now; this plan does not touch it.)*

That is five incidents of one defect. The pattern is not "someone forgot" — it is that the
design **makes forgetting the default outcome**: adding a public endpoint requires editing
between one and four unrelated files that the endpoint author has no reason to open.

### 1.3 The principle

> An authorization decision must be **declared where the route is declared**, or enforced at
> a boundary that **derives** it from that declaration. It must never be re-stated in a
> parallel, hand-maintained list.

ASP.NET Core already implements this: `.AllowAnonymous()` / `.RequireAuthorization()` attach
`IAllowAnonymous` / `IAuthorizeData` **metadata to the endpoint**, and the routing layer
makes that metadata available to any middleware that runs after route matching. Agentweaver
writes that metadata (`.AllowAnonymous()` appears on the MCP OAuth AS routes in
`Endpoints/OAuthServerEndpoints.cs:50-54`, on `/api/server/info`, and elsewhere) and then
**ignores it**. The fix is to stop ignoring it, not to maintain the list better.

A secondary, equally important consequence: because the metadata is ignored, the OpenAPI
document's security annotations are produced from yet another copy of the list, so the
published contract can disagree with runtime behaviour — which is exactly what the
api-harness consumes.

---

## 2. Phase 1 — make the middleware read endpoint metadata (near-term, in-process)

**Effort: S. Risk: low. Recommended first, independently of everything else.**

### 2.1 The change

Replace the hardcoded path checks in all three middlewares with a single shared helper:

```csharp
// Conceptual — the actual implementation belongs in the Phase 1 PR, not here.
static bool IsAnonymous(HttpContext ctx) =>
    ctx.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
```

Notes on the exact API, verified against this repo:

- `apps/Agentweaver.Api/Agentweaver.Api.csproj:54` targets **`net10.0`**. Both
  `IAllowAnonymous` (the long-standing marker interface) and `IAllowAnonymousData` are
  available; `Metadata.GetMetadata<IAllowAnonymous>()` is the form that matches what
  `.AllowAnonymous()` actually attaches on minimal APIs and is what
  `AuthorizationMiddleware` itself checks. The Phase 1 PR should confirm against the pinned
  SDK rather than trusting this paragraph.
- The complement — "does this endpoint want authentication?" — should be expressed as
  **default-deny**: any endpoint under `/api` (and `/mcp`, `/oauth`, `/.well-known`) that
  does *not* carry `IAllowAnonymous` requires a principal. That inverts today's
  fail-open-if-you-forget-the-list into fail-closed-unless-you-opt-out, which is the
  correct default for an authn boundary.

### 2.2 Middleware ordering — does this need `UseRouting()` moved?

`Program.cs` never calls `app.UseRouting()` explicitly. On `WebApplication`, routing is
**auto-inserted at the very start of the pipeline** when endpoints are mapped (with
`UseEndpoints` auto-terminating it), so `context.GetEndpoint()` is *already* populated by
the time `GitHubTokenAuthMiddleware` runs. **No reordering should be required.**

However, this is implicit behaviour that a future edit could silently break (calling
`app.UseRouting()` explicitly anywhere later would relocate the marker and quietly null out
`GetEndpoint()` for the auth middleware — a fail-*open* outcome under a default-deny design,
which is the dangerous direction). Therefore the Phase 1 PR should:

1. Add an **explicit** `app.UseRouting()` immediately before `app.UseCors()` so the ordering
   is stated, not inferred.
2. Add a **startup guard or test** asserting that `GetEndpoint()` is non-null inside the auth
   middleware for a known mapped route, so a future reorder fails loudly in CI instead of
   silently disabling authentication.
3. Treat a `null` endpoint as **not anonymous** (deny) rather than anonymous.

Side-effect review of an explicit `UseRouting()` at that position:

- **`UseExceptionHandler`** stays first — unaffected; it must remain outermost.
- **`UseCors`** — CORS *should* run after routing so endpoint-specific CORS metadata is
  honoured; moving `UseRouting` explicitly ahead of it is the documented, recommended order
  and is what already happens implicitly today. No behaviour change expected.
- **`UseRateLimiter`** — already relies on endpoint metadata for named policies; it also
  benefits from an explicit routing marker. No change.
- Static file serving / SPA fallback is handled by the separate frontend deployment
  (`k8s/base/frontend-deployment.yaml`), so no static-file ordering concern in the API.

### 2.3 Also fold in

- Collapse the three (four, counting OpenAPI) allow-lists into **one** source of truth. The
  handful of genuinely path-shaped exemptions that cannot be endpoint metadata (e.g. the
  `/mcp` proxy prefix, `/openapi`) should live in a single `PublicPaths` type consumed by
  all consumers including `OpenApiSecurityTransformers`.
- The GitHub webhook receiver deserves a named marker rather than a suffix match: it is not
  "anonymous", it is "authenticated by HMAC inside the endpoint". A dedicated metadata marker
  (e.g. `[WebhookAuthenticated]`) documents that distinction and prevents someone treating it
  as a public read endpoint.
- Regression test: enumerate `EndpointDataSource` at test time and assert that **every**
  mapped endpoint is either `AllowAnonymous` or requires a principal — a test that would have
  caught all five historical incidents.

### 2.4 Issue tracking

I searched `gh issue list` / `gh search issues` for an existing issue filed today covering
this (queries: `AllowAnonymous`, `auth middleware`) — **none is visible in the repo's issue
list yet** (newest open issues are #687/#688, both Azure provisioning fixes). If the other
agent's issue lands after this document, link it here rather than opening a duplicate; if it
does not, Phase 1 needs its own issue titled roughly *"auth middleware must honour endpoint
`AllowAnonymous` metadata instead of a hardcoded path list"*.

### 2.5 Phase 1.5 — become a real `AuthenticationHandler`

**Effort: M.** The natural end state of Phase 1 is to stop being middleware at all: implement
authentication *schemes* (Entra JWT via `JwtBearerHandler` + `EntraAccessTokenValidator`,
GitHub PAT/session, MCP OAuth JWT via `McpTokenService`, internal service key), register them,
and use `app.UseAuthentication()` / `app.UseAuthorization()` with named policies. Tier-1
platform roles and the org allow-list become **authorization policies**. This deletes
`GitHubTokenAuthMiddleware`, `PlatformRoleAuthorizationMiddleware` and
`GitHubOrgAuthorizationMiddleware` outright and gets correct `WWW-Authenticate` challenges
and 401-vs-403 semantics. It is a bigger blast radius than Phase 1 and it, not the mesh, is
the highest-value structural cleanup available.

**→ The full execution plan for this phase is [Appendix A](#appendix-a-phase-15-execution-plan)
at the end of this document.**

---

## 3. Phase 2 — mesh-level enforcement (Entra mode): blocked by a prerequisite

**Effort: L (M for the manifests, L including the cluster migration). Recommended: defer.**

### 3.1 What it would look like

With the **Istio service mesh add-on** (not what is deployed today), Entra-mode deployments
could add:

```yaml
apiVersion: security.istio.io/v1
kind: RequestAuthentication
metadata: { name: agentweaver-entra, namespace: agentweaver }
spec:
  selector: { matchLabels: { app.kubernetes.io/name: agentweaver-api } }
  jwtRules:
    - issuer: "https://login.microsoftonline.com/<TENANT_ID>/v2.0"
      jwksUri: "https://login.microsoftonline.com/<TENANT_ID>/discovery/v2.0/keys"
      audiences: ["api://<CLIENT_ID>"]
      forwardOriginalToken: true
---
apiVersion: security.istio.io/v1
kind: AuthorizationPolicy
metadata: { name: agentweaver-require-jwt, namespace: agentweaver }
spec:
  selector: { matchLabels: { app.kubernetes.io/name: agentweaver-api } }
  action: ALLOW
  rules:
    - to: [{ operation: { paths: ["/api/health","/api/version","/api/ping","/api/server/info",
                                  "/api/auth/config","/api/auth/session/exchange",
                                  "/.well-known/*","/oauth/*","/openapi/*"] } }]
    - from: [{ source: { requestPrincipals: ["*"] } }]
```

### 3.2 What this DOES replace

- Cryptographic validation of Entra bearer JWTs — signature, `iss`, `aud`, `exp`, `nbf` —
  moved to Envoy, off the request path in managed code. `EntraAccessTokenValidator`'s JWKS
  fetch/cache would become redundant for edge validation.
- Coarse "is this path public, or does it need *any* valid principal" enforcement, applied
  uniformly to every pod in the namespace — including the MCP deployment — regardless of app
  code correctness. This is genuine defense-in-depth: a future `Program.cs` regression could
  no longer expose an endpoint anonymously.

### 3.3 What this does NOT replace

- **GitHubLegacy mode.** Its credentials are GitHub PATs/OAuth tokens validated by calling
  `https://api.github.com/user` (`ApiKeyAuthMiddleware.cs` `ValidateGitHubTokenAsync`) plus a
  session-exchange flow. These are opaque tokens, not JWKS-verifiable JWTs;
  `RequestAuthentication` cannot validate them. GitHubLegacy keeps app-level auth entirely,
  or migrates behind oauth2-proxy (Phase 3).
- **Tier-2 per-project RBAC** (`Security/ProjectAuthorization.cs`) — requires project/role
  rows from Agentweaver's own database. Stays in app code. Permanently.
- **The MCP OAuth 2.1 Authorization Server** (`Endpoints/OAuthServerEndpoints.cs`: RFC 8414
  metadata, JWKS, dynamic client registration, PKCE authorize/token/revoke). Agentweaver is
  the **issuer** here, not a relying party. Mesh policy cannot take over an AS role; at most
  it would need a *second* `jwtRules` entry pointing at Agentweaver's own issuer/JWKS so
  MCP-issued tokens are also accepted at the edge.
- **The internal service key** path (`Auth:ApiKey`) and the `agentweaver-internal` caller.
- **Account linking** (Entra primary identity + linked GitHub account for repo/Copilot
  operations) — bespoke app logic.
- **The auth-mode epoch check** (`AuthModeEpochService`) that rejects requests served by a
  pod running a stale auth mode. Mesh policy has no notion of this.

Net: the app-level middleware **shrinks but does not disappear**, and the two systems must
be kept in agreement — which reintroduces, at the manifest layer, exactly the
duplicated-allow-list problem Phase 1 exists to eliminate, unless the Istio path list is
**generated from** the endpoint metadata rather than hand-written. If Phase 2 is ever done,
generating the `AuthorizationPolicy` path list from the OpenAPI document at build time is a
hard requirement, not a nice-to-have.

### 3.4 Platform feasibility — the blocker

Research against Microsoft Learn (Aug 2026) found:

| Question | Finding |
|---|---|
| `RequestAuthentication` on the **Istio add-on** | Usable. Not in the blocked-CRD list (`ProxyConfig, WorkloadEntry, WorkloadGroup, IstioOperator, WasmPlugin`). |
| `AuthorizationPolicy` ALLOW/DENY on the **Istio add-on** | Usable; MS migration guidance actively recommends it. `DENY` has no MS example but is not blocked. |
| `AuthorizationPolicy` `action: CUSTOM` (ext_authz) | Not documented by MS at all. Its prerequisite, `meshConfig.extensionProviders`, **is** on the add-on MeshConfig allow-list (classified **Allowed** = permitted but *outside Azure support*), set via `istio-shared-configmap-asm-1-XX` in `aks-istio-system`. Mechanically enabled, unvalidated — would need empirical proof on a cluster. |
| Gateway API on the Istio add-on | GA from `asm-1-26`+ (`gatewayClassName: istio`). |
| **`approuting-istio` (what Agentweaver actually uses)** | **Istio CRD support: "Not supported."** It only manages infrastructure for Gateway API resources. It **cannot coexist** with the Istio add-on — enabling one requires disabling the other *and deleting all `*.istio.io` CRDs*. |
| Entra-at-the-gateway on `approuting-istio` | An **open, unanswered feature request**: [Azure/AKS#5852](https://github.com/Azure/AKS/issues/5852) (opened 2026-07-08) asks for exactly this — allow an Envoy `extensionProvider` in the `istio-gateway-class-defaults` allow-list, or a managed ext_authz/oauth2-proxy integration. No Microsoft response yet. |
| MS guidance for Entra JWT validation via `RequestAuthentication` | **None found.** The only MS-authored `RequestAuthentication` sample uses the AKS cluster OIDC issuer, and comes from the *Application Network (preview, ambient mode)* product, not the sidecar add-on. Pitfall to remember: v1 tokens carry `iss: https://sts.windows.net/{tenant}/`, v2 carry `.../v2.0`. |

**Conclusion:** Phase 2 requires migrating the cluster off `approuting-istio` onto the Istio
service-mesh add-on with sidecar injection for the `agentweaver` namespace. That is a
disruptive, cross-cutting infrastructure change (new GatewayClass, CRD deletion/re-install,
canary-upgrade revision management pinned to AKS versions, sidecar resource overhead on every
pod including AgentHost sandboxes, and re-validation of every `NetworkPolicy`/
`CiliumNetworkPolicy` in `k8s/base/` since sidecars change the traffic path). **It is not
justified by the auth problem alone.** Do it if and when the mesh is wanted for its own
reasons (mTLS, L7 telemetry, traffic shifting) — and take auth offload as a bonus.

### 3.5 If it is ever done: follow the existing mode-conditional pattern

Do not invent a new mechanism. `k8s/` manifests are built through `kustomize build` and
then applied file-by-file from an explicit list in `scripts/azure/steps/30-deploy.mjs:60-83`,
with per-file resource identity declared in `FILE_RESOURCES` in
`scripts/azure/lib/kustomize.mjs` and values injected via Kustomize `replacements` sourced
from the generated `agentweaver-runtime-config` ConfigMap (`AUTH_MODE` already flows through
there — `kustomize.mjs:170-176`). PR #686 (*"use FQDN-based Cilium egress for public-mode
Postgres"*) is the current precedent for branching a manifest on a deployment mode. Any
Istio auth resources should be new files with `FILE_RESOURCES` entries, conditionally
included in the apply list when `AUTH_MODE === "Entra"`, with `TENANT_ID`/`CLIENT_ID`
injected by replacement — never templated ad hoc.

---

## 4. Phase 3 — oauth2-proxy evaluation

**Recommendation: do not adopt. Effort if adopted: L. Value delivered: mostly duplicative of
Phase 1/1.5.**

### 4.1 The honest case *for* it

- It ships first-class providers for **both** of Agentweaver's IdPs — GitHub (with org/team
  restriction via `--github-org` / `--github-team`, which maps almost exactly onto
  `Auth__GitHub__AllowedOrg` and `GitHubOrgAuthorizationMiddleware`) and **Microsoft Entra
  ID / Azure AD OIDC**. That is a genuinely strong signal: the two modes Agentweaver
  hand-built are both off-the-shelf features there.
- It is mature, widely deployed, and would move the OAuth redirect dance, token exchange,
  refresh, and session cookie management out of Agentweaver's code.
- Deployment options are well-trodden: a sidecar/Deployment in front of the app, or an
  Istio `AuthorizationPolicy` with `action: CUSTOM` → `envoyExtAuthzHttp` extension provider.
  It injects `X-Auth-Request-User` / `-Email` / `-Groups` / `-Access-Token` downstream.

### 4.2 The case *against* it, concretely

1. **The Istio integration path is unavailable today** — same blocker as Phase 2 (§3.4), and
   Azure/AKS#5852 is the open, unanswered request to make it available on `approuting-istio`.
   The fallback is running oauth2-proxy as a plain reverse proxy in front of the API/frontend
   Services, which means it becomes a new hop that must be threaded through
   `k8s/base/httproute-api.yaml`, `networkpolicy-default-deny.yaml` (`allow-gateway-to-api`,
   `allow-gateway-to-frontend`), the PDBs and quota in `k8s/base/quota.yaml`, plus Key Vault
   handling for its cookie secret and client secret via `secret-provider-class.yaml`. Real,
   permanent operational surface.
2. **It solves the browser-login problem, which Agentweaver has already solved.** PRs #653
   and #658 landed Entra browser sign-in *including PKCE-only operation when no client secret
   is available* — a scenario oauth2-proxy handles less naturally, since it is designed as a
   confidential client. Ripping out working, recently-hardened sign-in code to adopt a
   component that is *less* flexible on exactly the constraint this deployment hit is a poor
   trade.
3. **It does not address the actual bug.** The five historical incidents were *"a public
   endpoint got 401'd by an out-of-date allow-list"*. oauth2-proxy has the same design
   shape — `--skip-auth-routes` / `--skip-auth-regex`, a hand-maintained regex list — so it
   would **reproduce the root cause at a different layer**, and this time with no access to
   ASP.NET Core endpoint metadata to derive it from. This is the decisive argument.
4. **It cannot do three things Agentweaver needs**, all of which stay custom regardless:
   - **GitHub account linking as a secondary identity.** oauth2-proxy establishes one session
     with one IdP. The "sign in with Entra, then separately link a GitHub account for
     repo/Copilot operations" two-tier model — including `IGitHubTokenStore` /
     `IGitHubTokenScopeProvider`, per-project GitHub identities
     (`MapProjectGitHubIdentityEndpoints`), and Key Vault-backed user token storage —
     is entirely bespoke. oauth2-proxy would front the *primary* login only.
   - **The MCP OAuth 2.1 Authorization Server.** oauth2-proxy is a relying party / client-side
     gate. Agentweaver is an **AS** for third-party MCP clients (dynamic client registration
     RFC 7591, PKCE, revocation RFC 7009, its own JWKS). Different protocol role entirely;
     zero overlap. Worse, an oauth2-proxy in front of `/oauth/*` and `/.well-known/*` would
     actively *break* MCP client discovery unless carefully skipped — more allow-list.
   - **Tier-2 per-project RBAC.** Needs the app's own DB. Unchanged.
5. **Migration requires running both stacks concurrently.** The SPA
   (`apps/web/src/api/client.ts`) sends bearer tokens; oauth2-proxy is cookie-session-first.
   Bridging those means keeping the existing token path alive throughout, i.e. *more* auth
   code in flight, not less, for the whole migration window — and the MCP/CLI/agent clients
   (AgentHost, harnesses, `agentweaver-internal` service key) are non-browser callers that
   can never use a cookie session and would need a permanent bypass anyway.

### 4.3 Verdict

**Not recommended — not even partial adoption.** A partial adoption (oauth2-proxy fronting
only the web frontend and browser session establishment, leaving MCP, the AS, service-key
callers and account linking untouched) is the only variant that is coherent, but it buys
little: it replaces recently-shipped, working, PKCE-capable sign-in code with a new
deployment unit, adds a skip-route list that recreates the original bug class, and leaves
every hard part of Agentweaver's auth model exactly where it is.

**Revisit if and only if** (a) the cluster moves to the Istio service-mesh add-on for
independent reasons *and* ext_authz is empirically validated on the add-on, or (b)
Azure/AKS#5852 ships managed ext_authz support for `approuting-istio`, or (c) Agentweaver
needs to support several additional IdPs, where oauth2-proxy's provider catalogue would start
to pay for itself.

---

## 5. Recommended rollout order

| # | Work | Effort | Prereq | Recommendation |
|---|---|---|---|---|
| 1 | **Honour `AllowAnonymous` endpoint metadata**; default-deny; explicit `UseRouting()`; single `PublicPaths` source shared with the OpenAPI transformer; enumerate-all-endpoints regression test | **S** | none | **Do now.** Eliminates the bug class outright. |
| 2 | **Convert to real authentication schemes + authorization policies** (`AuthenticationHandler` per scheme; `PlatformAccess` / org checks become policies; delete all three middlewares) — **full execution plan in [Appendix A](#appendix-a-phase-15-execution-plan)** | **M–L** (3 PRs, flag-gated cutover) | Phase 1 | **Do next.** Highest structural value; keeps everything in-process and testable. |
| 3 | Istio `RequestAuthentication` + `AuthorizationPolicy` for `AUTH_MODE=Entra`, generated from OpenAPI, applied conditionally via the `30-deploy.mjs` + `FILE_RESOURCES` pattern | **L** | **Cluster migration off `approuting-istio` onto the Istio service-mesh add-on** | **Defer.** Only if the mesh is adopted for its own reasons. |
| 4 | oauth2-proxy | **L** | Phase 3 infra + ext_authz validation | **Do not adopt.** Revisit only under §4.3's conditions. |

### Explicitly out of scope — do not change, at any phase

- **Tier-2 per-project RBAC** (`Security/ProjectAuthorization.cs`). Requires app-owned data;
  no infrastructure layer can take it over.
- **The MCP OAuth 2.1 Authorization Server** (`Endpoints/OAuthServerEndpoints.cs`,
  `McpTokenService`, `McpRefreshTokenStore`). Agentweaver is the issuer; this is a protocol
  role no proxy or mesh can assume.
- **GitHub account-linking flows** and per-project GitHub identities.
- **The GitHub webhook HMAC verification path** — authenticated by signature, not by bearer
  token. It needs a distinct marker, never a plain "public" classification.
- **`AuthModeEpochService`** stale-epoch rejection.
- **The `Auth:ApiKey` internal service-key path** — non-browser, non-JWT, must keep working
  for AgentHost/worker calls.

---

## 6. Open questions for follow-up

1. Is there an independent appetite for the Istio service-mesh add-on (mTLS, L7 telemetry)?
   That, not auth, is what would justify Phase 3.
2. Should the Istio path allow-list — if ever built — be generated from `/openapi/v1.json` in
   CI? (Answer should be yes; otherwise Phase 3 re-creates the very problem Phase 1 fixes.)
3. Should Phase 1.5 also replace the OpenAPI security transformer's path heuristics with the
   same metadata source, so the published contract and runtime behaviour cannot diverge?

---

# Appendix A: Phase 1.5 execution plan

*(the "M" phase — replacing `GitHubTokenAuthMiddleware`, `PlatformRoleAuthorizationMiddleware`
and `GitHubOrgAuthorizationMiddleware` with real ASP.NET Core authentication schemes and
authorization policies)*

**Prerequisite:** Phase 1 has landed (endpoint metadata is the source of truth for "is this
public"). Phase 1.5 assumes every endpoint already carries correct `AllowAnonymous` metadata;
without that, this phase inherits the original bug.

## A.1 Sizing facts (measured, not estimated)

| Fact | Value | Why it matters |
|---|---|---|
| `GetCaller(` call sites | **73**, across **17** files | Every one reads `HttpContext.Items` today. This is the migration's real surface area, not the 3 middleware files. |
| Auth-relevant test files | 21 (see A.5) | Parity baseline. |
| Existing `WebApplicationFactory` harnesses | 15 under `tests/Agentweaver.Tests/Helpers/` incl. `EntraWebApplicationFactory`, `OAuthWebApplicationFactory`, `ProjectsWebApplicationFactory` | The decision-matrix test should extend these, not invent a new harness. |
| Target framework | `net10.0` | Full modern auth stack available. |
| `AUTH_MODE` | single deployment-time switch (`GitHubLegacy` \| `Entra`), flows from `kustomize.mjs:176` | Modes are **never simultaneous** — decisive for scheme composition (A.2.2). |

## A.2 Target end-state architecture

### A.2.1 The schemes

Four production authentication schemes plus a Development-only bypass scheme, registered by
name. Constants belong in one static class
(e.g. `Auth/AgentweaverAuthSchemes.cs`) so nothing is stringly-typed at call sites.

| Scheme name | Implementation | Reuses | Notes |
|---|---|---|---|
| `Entra` | `AddJwtBearer("Entra", ...)` | **`EntraAccessTokenValidator`** — do **not** re-implement | See A.2.3. Only registered when `AUTH_MODE=Entra`. |
| `McpOAuth` | `AddJwtBearer("McpOAuth", ...)` | `McpTokenService.CreateValidationParameters(issuer, audience)` (already returns a `TokenValidationParameters`) + `McpRefreshTokenStore.IsJtiDeniedAsync` as an `OnTokenValidated` event that calls `context.Fail()` | Issuer/audience are **per-request** (`OAuthServerConfig.ResolveIssuer(context, config)`), so this must be resolved in `OnMessageReceived`/`OnTokenValidated` rather than pinned at startup — a real constraint on a naive `AddJwtBearer` registration; see A.2.4. |
| `GitHubToken` | custom `AuthenticationHandler<GitHubTokenSchemeOptions>` | the existing `IMemoryCache` + `ValidateGitHubTokenAsync` logic lifted verbatim out of `ApiKeyAuthMiddleware.cs` | Opaque PAT validated by calling `https://api.github.com/user`; not a JWT, so it must be a hand-written handler. Only registered when `AUTH_MODE=GitHubLegacy`. |
| `InternalServiceKey` | custom `AuthenticationHandler<...>` | the `Auth:ApiKey` comparison block | Must use a **fixed-time comparison** (`CryptographicOperations.FixedTimeEquals`) — the current `token == internalKey` is a timing-comparison weakness worth fixing while the code is being moved. |
| `TestBypass` | custom handler, **Development-only** | `_bypassForTests` / `_testApiKeyMap` block | Keep the existing `LogCritical` guard rails and the `environment.IsDevelopment()` gate exactly as-is. Registered only when the bypass is active. |

### A.2.2 How the schemes compose

`AUTH_MODE` is a deployment-time switch, not a per-request one, so **do not** build a
per-endpoint `[Authorize(AuthenticationSchemes = "...")]` matrix — that would push mode
knowledge into 200+ route declarations and recreate the distributed-knowledge problem.

**Use a policy scheme as the default.** Register one `AddPolicyScheme("Agentweaver", ...)`
whose `ForwardDefaultSelector` picks a concrete scheme from the request:

```
Agentweaver (policy scheme, DefaultScheme)
 ├─ Authorization: Bearer <token> matches Auth:ApiKey shape → InternalServiceKey
 ├─ token is a well-formed JWT issued by our own AS (iss == OAuthServerConfig.ResolveIssuer)
 │                                                        → McpOAuth
 ├─ AUTH_MODE=Entra     → Entra
 └─ AUTH_MODE=GitHubLegacy → GitHubToken
```

Properties this gives:

- **One** place encodes the mode branch, mirroring today's single `if (_authMode == …)`.
- The internal service key and MCP OAuth JWT paths stay available in **both** modes, which
  matches today's behaviour (the current middleware checks the internal key before the mode
  branch, and the MCP OAuth path is reachable in GitHubLegacy mode).
- Selector fallbacks must **not** throw on a malformed token — return the mode-default scheme
  and let that scheme produce a clean `AuthenticateResult.Fail`, preserving the current
  "any unparseable token ⇒ 401" outcome.
- `AddAuthentication(AgentweaverAuthSchemes.Policy)` sets both `DefaultAuthenticateScheme`
  and `DefaultChallengeScheme`, so `[Authorize]` with no scheme argument does the right thing
  everywhere and endpoint authors never name a scheme.

The **only** places that should ever name a scheme explicitly are endpoints that must accept
*exclusively* one credential type (e.g. an MCP-only route pinned to `McpOAuth`). Keep that
list near-empty and justify each entry in a comment.

### A.2.3 Reusing `EntraAccessTokenValidator` — the specific mechanism

`EntraAccessTokenValidator.ValidateAsync(token, ct)` today does OIDC-metadata discovery
(`{authority}/.well-known/openid-configuration`), signature/issuer/audience validation, and
then **maps app-role claims into `RecognizedRoles` / `PrimaryRole` / `DisplayName`**
(`EntraAccessTokenClaims`, `EntraAccessTokenValidator.cs:186`). Two options:

- **Preferred:** register `AddJwtBearer("Entra")` with `Authority`/`Audience`/
  `MetadataAddress` sourced from the validator's existing `Authority` / `Issuer` / `ClientId`
  properties (so configuration reading stays in one class), and move the **claims-shaping**
  logic into an `OnTokenValidated` event that rewrites the principal into Agentweaver's claim
  vocabulary. This hands JWKS caching, key rollover, clock skew, and `WWW-Authenticate`
  challenge generation to the framework — all things the hand-rolled validator currently owns.
- **Fallback if the mapping proves lossy:** keep `ValidateAsync` and wrap it in a thin custom
  `AuthenticationHandler` that calls it and builds the principal. Less framework benefit, but
  a zero-behaviour-change option. **Decide this by test, not by argument** — run the A.5
  matrix against both.

Either way `EntraAccessTokenValidator` is *reused*, and `IsConfigured` remains the guard that
prevents starting Entra mode without `Auth:Entra:ClientId`/`TenantId`.

### A.2.4 The `McpOAuth` per-request issuer problem

`OAuthServerConfig.ResolveIssuer(context, configuration)` derives the issuer from the incoming
request (host-aware), because the AS metadata must match the URL the client actually used.
`JwtBearerOptions.TokenValidationParameters` is resolved once at startup. Resolution:

- Set `TokenValidationParameters.ValidateIssuer = false` / `ValidateAudience = false` at
  registration, and perform the issuer/audience check inside `OnTokenValidated` using the
  live `HttpContext` and `McpTokenService.CreateValidationParameters(issuer, audience)` —
  failing the result if they do not match. This is a real deviation from the textbook
  registration and **must be called out in review**, because getting it wrong silently
  disables issuer validation. Guard it with a dedicated test that a token minted for issuer A
  is rejected when presented to host B.
- The `IsJtiDeniedAsync` revocation check moves into the same event. It is currently the only
  DB call on the hot auth path; keep it scoped to the MCP scheme so Entra requests do not pay
  for it.

### A.2.5 `CallerContext` → claims

Today `SetCaller` stashes a `CallerContext` in `HttpContext.Items[CallerItemKey]` **and**
builds a parallel `ClaimsPrincipal` with overlapping data. That duplication is the thing to
remove. Target: **the `ClaimsPrincipal` is the single source of truth**; `CallerContext`
becomes a thin, cached projection over it.

Claim vocabulary (mostly already emitted by `BuildClaimsPrincipal`, so this is largely
formalisation, not invention):

| `CallerContext` property | Claim | Notes |
|---|---|---|
| `User` | `ClaimTypes.NameIdentifier` | already emitted |
| `EntraObjectId` | `oid` | already emitted |
| `EntraTenantId` | `tid` | already emitted |
| `PlatformRoles` | repeated `ClaimTypes.Role` | already emitted; enables `IsInRole` and role-based policies for free |
| `PrimaryPlatformRole` | `agentweaver_primary_role` | new; currently only in `CallerContext` |
| `GitHubLogin` | `gh_login` | already emitted |
| `Org` | `agentweaver_org` | new; currently only in `CallerContext` |
| `IsOAuthJwt` | derived from `principal.Identity.AuthenticationType == "McpOAuth"` | stop storing it as data; it *is* the scheme |
| (internal caller) | `agentweaver_internal` | already emitted; better still, derive from the scheme name |

**Do not touch the 73 `GetCaller(...)` call sites in this phase.** Keep
`ApiKeyAuthMiddleware.GetCaller(HttpContext)` as a public shim that now *reads from
`context.User`* instead of `Items`. That single change makes all 73 call sites correct with
zero diff, and lets the risky part (scheme plumbing) be reviewed in isolation. Migrating call
sites to inject `ClaimsPrincipal`/`HttpContext.User` directly is worthwhile later cleanup but
is explicitly **out of scope** for Phase 1.5 — bundling it would make the diff unreviewable.

One behavioural subtlety to preserve: `CallerContext.Owns(ownerUser)` matches on **either**
`User` **or** `GitHubLogin`. The projection must keep that exact semantics; a regression here
is a broken-access-control bug, not a cosmetic one.

## A.3 Authorization policies

### A.3.1 Tier-1: `PlatformAccess` (replaces `PlatformRoleAuthorizationMiddleware`)

A `PlatformAccess` policy already exists (the middleware calls
`_authorizationService.AuthorizeAsync(context.User, null, "PlatformAccess")` at
`PlatformRoleAuthorizationMiddleware.cs:50-52`) — so the requirement/handler machinery is
**already built**. The middleware is a hand-rolled invocation of a policy that
`UseAuthorization()` would apply natively. Removing it is therefore mostly deletion:

- Register `PlatformAccess` as the **fallback policy**
  (`options.FallbackPolicy`) when `AUTH_MODE=Entra`. A fallback policy applies to every
  endpoint that has **no** authorization metadata and is **not** `AllowAnonymous` — exactly
  the middleware's current semantics, but derived from endpoint metadata instead of a
  12-entry `ExemptPrefixes` array.
- The `ExemptPrefixes` entries that are not `/api`-prefixed routes (`/oauth`, `/.well-known`,
  `/openapi`, `/mcp`) become `AllowAnonymous` on their route groups — most already are
  (`OAuthServerEndpoints.cs:50-54`).
- Preserve the **403 body shape**: the middleware returns
  `{ error = "Access denied. A recognized Agentweaver platform role is required." }`. The
  framework's default `Forbid()` writes an empty body. Register a custom
  `IAuthorizationMiddlewareResultHandler` to keep the JSON contract byte-identical (see risk
  R3).

### A.3.2 Tier-1: GitHub org allow-list (replaces `GitHubOrgAuthorizationMiddleware`)

Becomes a `GitHubOrgAccess` policy with an `IAuthorizationRequirement` +
`AuthorizationHandler` that calls the existing `IGitHubOrgAuthorizationService`. Two
properties must survive verbatim:

- **Fail-closed:** "if `Auth:GitHub:AllowedOrg` is not set at all, every non-exempt request is
  blocked with 403." The handler must fail (not succeed-by-default) on missing config. There
  is an existing test (`GitHubOrgAuthorizationMiddlewareTests.cs`) — it must keep passing,
  adapted only at the seam.
- **Caller-token reuse:** the org check uses the caller's own GitHub token from the
  `Authorization` header. An `AuthorizationHandler` has `HttpContext` via
  `IHttpContextAccessor` or `AuthorizationHandlerContext.Resource`; prefer passing the token
  through as a **claim** minted by the `GitHubToken` scheme rather than re-reading the header
  in the handler, so the authorization layer never re-parses credentials.
- Registered as the fallback policy when `AUTH_MODE=GitHubLegacy`, symmetric with A.3.1.

Also add `RequirePlatformAdmin` (and any other role-specific policies currently expressed as
ad-hoc role checks inside endpoints) so `.RequireAuthorization("RequirePlatformAdmin")` is
available as declarative route metadata.

### A.3.3 `AuthModeEpochService` — stays, but moves to the right layer

Today the stale-epoch check sits inside `GitHubTokenAuthMiddleware.InvokeAsync` **after** the
`Authorization`-header presence check and returns 401. It is not an identity check and not a
per-endpoint authorization decision — it is a **pod liveness/tenancy guard**: "this replica is
running the wrong auth mode, do not trust anything it authenticates."

Recommendation: keep it as **its own small middleware**, placed *before*
`UseAuthentication()`, that short-circuits with 401 when the instance is on a stale epoch and
the request carries an `Authorization` header. Rationale:

- It must apply regardless of which scheme would have been selected.
- Modelling it as an authorization policy would make it silently skipped for `AllowAnonymous`
  endpoints — which is arguably fine but changes behaviour; keeping it as middleware is the
  zero-delta choice.
- Do **not** put it inside a scheme handler: it would then run once per scheme and its DB call
  would multiply.

Its current placement means it only fires for requests that *have* a bearer token; preserve
that condition exactly, or health probes on a stale pod will start failing and take the pod
out of service in a way it currently is not.

### A.3.4 Tier-2 per-project RBAC — stays as in-handler checks

`ProjectAuthorization.RequireAccessAsync(httpContext, project, config, minimumRole, ct)`
needs (a) the **route's** project id, (b) a **loaded `Project` entity** (the endpoint has
already fetched it), and (c) an async DB lookup of the caller's project role. That is
resource-based authorization over a runtime-resolved resource — it cannot be a static policy
attached at route-registration time.

- **Keep the explicit in-endpoint call.** This is the correct pattern, not a wart.
- Optional, low-value-but-tidy follow-up (**not** part of Phase 1.5): re-express it as
  `IAuthorizationService.AuthorizeAsync(user, project, new ProjectRoleRequirement(minRole))`
  — ASP.NET Core's resource-based authorization API — which would let it share
  requirement/handler infrastructure with Tier-1. Behaviourally identical; defer until
  Tier-1 is stable.
- The internal-service-caller exemption (`ProjectAuthorization.InternalServiceUser`) currently
  string-compares `caller.User` against `"agentweaver-internal"` and `Auth:User`, with a
  comment saying it is "kept in sync with `GitHubTokenAuthMiddleware`'s internal-key path" —
  another instance of duplicated knowledge. After Phase 1.5 it should test the **scheme**
  (`InternalServiceKey`) or the `agentweaver_internal` claim instead of the username string.
  This is a genuine correctness improvement: today anyone who can cause `caller.User` to equal
  the configured `Auth:User` value gets the exemption regardless of how they authenticated.

## A.4 Migration strategy — flag-gated cutover, 3 PRs

Big-bang is not acceptable here: this code path runs on 100% of requests, and the failure mode
of a mistake is either a total outage (everything 401s) or a silent security hole (everything
authenticates). But the risk is not evenly spread across the work — it is concentrated almost
entirely in *"does the new stack build the same principal, and reach the same allow/deny
decision, as the old one?"*. Answer that question in staging, with a switch that can put the
old behaviour back in seconds, and the rest becomes ordinary refactoring.

**The safety net is a config flag, not a parallel evaluation.** Both pipelines live in the
codebase for one release cycle; exactly one of them is active at a time, chosen by
configuration. That gives a rollback measured in *a config change and a pod restart* rather
than a revert-build-redeploy cycle, and it avoids the cost and complexity of running two auth
stacks on every request.

### A.4.0 The three PRs (start here)

| PR | Stage | What it is | Risk | Gate to proceed |
|---|---|---|---|---|
| **1** | **Prepare** | `ClaimsPrincipal` becomes the source of truth, and the golden decision-matrix test is written **against today's middleware** so current behaviour is captured as the parity baseline. No enforcement change whatsoever. | **None** — nothing about who is allowed in changes. | Matrix test green and checked in; existing suite passes unchanged. |
| **2** | **Cut over behind a flag** | The full new stack (schemes + policy scheme + authorization policies) ships **off by default**, behind `Auth:UseSchemeBasedPipeline`. Turn it **on in staging**, prove parity, then turn it on in production. Old middleware stays in the tree but inert when the flag is on. | Medium to *enable*, **near-zero to undo** — flip the flag back off. | Decision-matrix test green with the flag **on**; all three harness suites green against staging with the flag **on**. |
| **3** | **Clean up** | After a stated production bake, delete the old middlewares, delete the flag, and finish hardening + docs. | Low — deleting a path that has been provably unused in production. | Bake period elapsed with no auth incidents. |

Read this as: **one cheap PR, one flag flip you can undo instantly, one deletion PR.** The
schedule lives in the *bake time* between PR 2's production enablement and PR 3, not in the
engineering.

### A.4.1 The flag

- **Config key:** `Auth:UseSchemeBasedPipeline` (bool, default `false`). This matches the
  existing `Auth:*` namespace — `Auth:Mode`, `Auth:ApiKey`, `Auth:GitHub:AllowedOrg`,
  `Auth:Entra:ClientId` — and the repo's `AuthModeResolver`-style single-switch pattern.
- **Deployment wiring:** env `Auth__UseSchemeBasedPipeline`, sourced from an
  `AUTH_USE_SCHEME_BASED_PIPELINE` key on the `agentweaver-runtime-config` ConfigMap, exactly
  as `Auth__Mode` is sourced from `AUTH_MODE` today (`k8s/base/api-deployment.yaml:186-190`,
  `scripts/azure/lib/kustomize.mjs:176`). No new mechanism.
- **Resolution is startup-time, not per-request.** Read it once in `Program.cs` and branch the
  pipeline construction; do not evaluate it inside a middleware. Log the resolved value at
  startup next to the existing `"Running in {AuthMode} auth mode."` line so every pod's logs
  state which pipeline it is running.
- **Interaction with `Auth:Mode`:** orthogonal. The flag chooses *how* auth is implemented;
  `Auth:Mode` chooses *which IdP*. Both `Entra` and `GitHubLegacy` must work under both
  pipelines, so the matrix test runs 2 × 2.
- **The flag is temporary by construction.** PR 3 deletes it. It must never acquire a second
  meaning, and it must not be exposed as a user-facing setting in `docs/guide/`.

### A.4.2 PR detail

*(Full rigor retained — the sub-items below are the same units of work as before, now scoped
as tasks within three PRs.)*

---

#### PR 1 — Prepare: claims consolidation + parity baseline. (S)

**Risk: none.** No enforcement change; verifiable entirely with the existing test suite.

1. **Claims consolidation.** Make `ClaimsPrincipal` the source of truth: add the two missing
   claims (`agentweaver_primary_role`, `agentweaver_org`), and rewrite
   `GetCaller(HttpContext)` to project from `context.User` instead of `HttpContext.Items`.
   `SetCaller` keeps writing both for now. **This is the change that de-risks the other 72
   `GetCaller` call sites** without touching them.
   - Preserve `CallerContext.Owns` semantics exactly (matches `User` **or** `GitHubLogin`) —
     see risk R7.
2. **Golden decision-matrix test** (A.5.2 #1), written against the **current** middleware.
   That ordering is what makes it a parity test rather than a description of the new code.
   Check the resulting matrix in as the baseline; PR 2 re-runs the identical matrix with the
   flag on and must produce identical results.

*Rollback: trivial revert.*

---

#### PR 2 — Cut over behind `Auth:UseSchemeBasedPipeline`. (M — the big one)

Ships **off by default**. Merging this PR changes nothing in production until the flag is
turned on, so the merge itself is low-risk; the *enablement* is the event to manage.

1. **Build the new pipeline** behind the flag: register the policy scheme + all concrete
   handlers (`Entra`, `GitHubToken`, `McpOAuth`, `InternalServiceKey`, plus the
   Development-only bypass — A.2), `UseAuthentication()`, and `UseAuthorization()` with the
   mode-appropriate **fallback policy** (`PlatformAccess` for Entra, `GitHubOrgAccess` for
   GitHubLegacy) plus the custom `IAuthorizationMiddlewareResultHandler` that preserves the
   existing 401/403 response bodies (risks R1, R3).
2. **Keep the old pipeline intact** on the `false` branch: `GitHubTokenAuthMiddleware`,
   `PlatformRoleAuthorizationMiddleware`, `GitHubOrgAuthorizationMiddleware` unchanged. This
   is the safety net. Both branches must be exercised in CI (see risk R11).
3. **Convert `ExemptPrefixes` entries to `AllowAnonymous` route-group metadata** where they are
   not already (most exist from Phase 1). This is shared by both branches and is therefore
   safe to land here.
4. **Tests:** per-scheme unit tests, policy-scheme selector tests, fallback-policy coverage,
   and response-shape golden tests (A.5.2 #2–#5). **Run the full decision matrix twice — flag
   off and flag on — and assert the two produce identical outcomes.** That assertion is the
   parity control for the whole migration, and it runs on every CI
   build rather than only in staging.
5. **Staging enablement:** flip `AUTH_USE_SCHEME_BASED_PIPELINE=true` in staging and run all
   three harness suites (`agentweaver-api-harness`, `agentweaver-mcp-harness`,
   `agentweaver-ui-harness`) against it (A.5.3). The MCP harness matters most — it is the only
   automated coverage of a real third-party client doing discovery + challenge + token
   (risk R2). Exercise both `Auth:Mode` values if staging permits.
6. **Production enablement:** a separate, deliberate config change — not part of the merge.
   Announce it, watch auth error rates, and treat "flip the flag back off" as the standing
   rollback for the first days.

*Rollback: **set `Auth:UseSchemeBasedPipeline=false` and restart.** No revert, no rebuild, no
redeploy of application code. If the problem is structural rather than operational, reverting
the PR is still clean because the default is off.*

---

#### PR 3 — Clean up: delete the old pipeline and the flag. (S–M)

**Do not open this PR until the flag has been on in production for a stated bake period with
no auth incidents.** State the period explicitly in the PR body (a full release cycle is a
reasonable default) — an unbounded "when it feels safe" is how flags become permanent.

1. Delete `GitHubTokenAuthMiddleware`, `PlatformRoleAuthorizationMiddleware` and
   `GitHubOrgAuthorizationMiddleware`. Delete `Auth:UseSchemeBasedPipeline` and its
   ConfigMap/env wiring; the scheme-based pipeline becomes the only path.
2. **Cleanup and hardening.** Delete `SetCaller`'s `Items` write and the `CallerItemKey`
   constant; retire `OpenApiSecurityTransformers`' path heuristics in favour of the same
   metadata; switch `ProjectAuthorization.IsInternalServiceCaller` to a scheme/claim test
   (A.3.4, risk R10); fixed-time comparison for the internal API key. Add the CI guard test
   that fails if any endpoint is neither `AllowAnonymous` nor covered by a policy.
3. **Simplify the tests** that ran the matrix against both branches down to the single
   remaining pipeline.
4. **Docs.** Update `docs/guide/` auth pages and `docs/mcp-oauth.md` for the new scheme names,
   `WWW-Authenticate` behaviour, and any changed status codes, per `CONTRIBUTING.md`.

*Rollback: revert. Note this is the one PR whose revert restores a flag that is expected to be
set to `true` in the environment — so if it is reverted, verify the config value is still
consistent with the restored code (risk R11).*

---

**Realistic total: 3 PRs.** The engineering is PR 2; the calendar time is the production bake
between PR 2's enablement and PR 3. Do not shorten the bake to close the flag out faster — a
flag that lives one release cycle too long costs nothing, while deleting the fallback one week
too early costs an incident.

## A.5 Test plan

### A.5.1 Existing tests that must keep passing **unchanged**

These are the parity baseline. If any of them needs its *assertions* edited (as opposed to a
mechanical seam/constructor fix), that is a **behaviour change** and must be justified in the
PR description, not quietly absorbed:

| File | Guards |
|---|---|
| `tests/Agentweaver.Tests/Auth/AuthModeSwitchTests.cs` | mode switching, cross-mode token rejection, **auth-mode epoch** invalidation |
| `tests/Agentweaver.Tests/Auth/EntraAuthModeTests.cs` | Entra-mode request outcomes |
| `tests/Agentweaver.Tests/Projects/GitHubOrgAuthorizationMiddlewareTests.cs` | org allow-list incl. **fail-closed on missing config** |
| `tests/Agentweaver.Tests/Projects/GitHubOrgAuthorizationServiceTests.cs` | org/team membership resolution |
| `tests/Agentweaver.Tests/Projects/McpOAuthServerTests.cs`, `OAuth/OAuthTokenLifecycleTests.cs`, `OAuth/OAuthMetadataTests.cs`, `OAuth/OAuthBackwardCompatTests.cs`, `OAuth/OAuthStateBindingTests.cs`, `OAuth/OAuthOrgEnforcementTests.cs`, `OAuth/OAuthRefreshOrgRevalidationTests.cs`, `OAuth/McpOAuthBrokerStoreTests.cs`, `OAuth/GitHubOAuthStateStoreTests.cs`, `OAuth/OAuthConfigGuardTests.cs` | the MCP OAuth AS end-to-end — **the surface most likely to break silently** |
| `tests/Agentweaver.Tests/Memory/ProjectOwnershipAuthorizationTests.cs` | Tier-2 RBAC |
| `tests/Agentweaver.Tests/Blueprints/HttpContextAuthenticatedOwnerContextTests.cs` | `CallerContext` projection — **directly exercises the PR 1 change** |
| `tests/Agentweaver.Tests/Coordinator/CollectiveAssemblyScribeApiAuthTests.cs`, `AgentHostUserAuthTests.cs`, `Preview/PreviewRunnerAuthAndScrubTests.cs`, `Mcp/GitHubAuthToolsTests.cs` | internal-service-key and agent loopback paths |

Frontend suites that assert on status codes (`apps/web/src/__tests__/ProjectGalleryGitHub.test.tsx`,
`ProjectSwitcher.test.tsx`, `SteerPanel.test.tsx`, `WorkflowsPage.test.tsx`) must also stay
green — they encode the 401-vs-403 contract described in risk R1.

### A.5.2 New tests required

**1. The decision matrix (the centrepiece).** A single integration test that enumerates
`EndpointDataSource` and, for **every** mapped endpoint, asserts the expected status for each
credential:

| Credential | Expected |
|---|---|
| no `Authorization` header | 200 if `AllowAnonymous`, else **401** |
| malformed / non-Bearer header | 401 |
| expired Entra token | 401 |
| wrong-audience Entra token | 401 |
| wrong-issuer Entra token (v1 `sts.windows.net` vs v2) | 401 |
| valid Entra token, **no** platform role | 403 (not 401) |
| valid Entra token **with** required role | 200/2xx |
| valid GitHub PAT, org member | 200/2xx (GitHubLegacy factory) |
| valid GitHub PAT, **non-member** | 403 |
| valid MCP OAuth JWT | 200/2xx on MCP-reachable routes |
| MCP OAuth JWT with **revoked jti** | 401 |
| MCP OAuth JWT minted for a **different issuer/host** | 401 |
| internal service key | 200/2xx |
| **wrong** internal service key | 401 |

Implementation notes: build it as a data-driven xUnit `[Theory]` over
`(endpoint × credential)`, run against `EntraWebApplicationFactory` and the GitHubLegacy
factory. **Write this test in PR 1, against the *current* middleware, and check in the
resulting matrix as the golden baseline** — that is what makes it a parity test rather than a
description of the new code. New endpoints joining the codebase later must extend the matrix,
which also gives the Phase 1 bug class a permanent CI backstop.

**2. Per-scheme unit tests** — one focused test class per handler: token accepted/rejected,
claims produced (exact claim types and values), `AuthenticateResult.NoResult` vs `Fail`
(they differ: `NoResult` lets the next scheme try, `Fail` stops — getting this wrong in the
policy scheme is a plausible silent-bypass bug).

**3. Policy-scheme selector tests** — for each `(AUTH_MODE, token shape)` combination, assert
which concrete scheme is selected, including the malformed-token fallback path.

**4. Fallback-policy coverage test** — assert `FallbackPolicy` is non-null in both modes and
that an endpoint with **no** metadata at all is denied (guards against a future
`AddAuthorization()` refactor silently dropping the fallback — a fail-open regression).

**5. Response-shape golden tests** — 401 body is `{"error":"unauthorized"}`, 403 body is the
platform-role JSON, `WWW-Authenticate` header value is asserted verbatim (see R2).

**6. Both-branches parity test** — in PR 2, run the full decision matrix twice against the same
`WebApplicationFactory` (once with `Auth:UseSchemeBasedPipeline=false`, once with `true`) and
assert the two outcome sets are **identical**. This runs on every CI build and is the primary
parity control for the migration.

**7. `AllowAnonymous`-count snapshot** — assert the *number and set* of anonymous endpoints
matches a checked-in list. Any PR that makes an endpoint public then requires an explicit,
reviewable one-line change to that list. Cheap, and it converts "someone accidentally made
`/api/projects` public" from an incident into a failing CI job.

### A.5.3 Live verification (`CONTRIBUTING.md` requires it for runtime-impacting work)

Run all three harnesses against staging with `Auth:UseSchemeBasedPipeline=true` before enabling
the flag in production, and again after PR 3 removes the old pipeline:
`agentweaver-api-harness`, `agentweaver-mcp-harness`, `agentweaver-ui-harness`. The MCP
harness matters most — it is the only automated coverage of a **real** third-party client
performing the discovery + challenge + token flow, which is precisely what R2 threatens.

## A.6 Risk register

| # | Risk | Why it can break **silently** | Mitigation |
|---|---|---|---|
| **R1** | **401 → 403 semantic shift.** Today an Entra caller with a valid token but no platform role gets **403** from `PlatformRoleAuthorizationMiddleware`; an unauthenticated caller gets **401** from the auth middleware. The framework's `AuthorizationMiddleware` issues **401 when the principal is unauthenticated** and 403 only when authenticated-but-denied. A misconfigured fallback policy flips these. | The SPA pattern-matches: `ProjectGalleryGitHub.test.tsx` / `ProjectSwitcher.test.tsx` show 401 ⇒ "show sign-in affordance", while `SteerPanel.test.tsx` shows 403 ⇒ "permission error". A flip makes the UI offer sign-in to users who *are* signed in (an infinite re-login loop) — no exception, no log. | Matrix test row "valid token, no role ⇒ **403**" is mandatory. Keep the frontend suites green. |
| **R2** | **`WWW-Authenticate` challenge changes.** Current code writes a bare 401 with **no** `WWW-Authenticate` header and body `{"error":"unauthorized"}`. `JwtBearerHandler` emits `WWW-Authenticate: Bearer error="invalid_token", error_description="..."` and an **empty body**. | MCP clients (Copilot CLI, VS Code) do RFC-driven challenge-response and RFC 9728 protected-resource discovery; a *new* header could change client behaviour (possibly for the better — or into a redirect loop), and an emptied body breaks anything parsing `error`. Neither shows up as a server-side error. | Golden response-shape tests (A.5.2 #5). Decide deliberately whether to *start* emitting `WWW-Authenticate` (arguably a spec-compliance improvement for MCP) and, if so, validate it with the MCP harness against a real client **before** PR 3, not after. Preserve the JSON body via a custom result handler regardless. |
| **R3** | **403 body shape loss.** `Forbid()` writes an empty body; the middleware writes a specific JSON message. | Clients showing the message get a blank error dialog. | Custom `IAuthorizationMiddlewareResultHandler`; golden test. |
| **R4** | **Middleware ordering.** `UseAuthentication`/`UseAuthorization` must sit after `UseRouting` and after `UseCors`; `UseExceptionHandler` must stay outermost; the rate limiter's position relative to auth determines whether unauthenticated floods consume the limiter budget. | Wrong order can make `[Authorize]` silently not run (endpoint metadata not yet resolved) — a **fail-open** outcome that no test notices unless a test specifically asserts a protected endpoint 401s. | Phase 1's explicit `UseRouting()` + the fallback-policy coverage test (A.5.2 #4). Assert ordering in a startup test, not by reading `Program.cs`. |
| **R5** | **`AuthenticateResult.NoResult` vs `Fail` confusion** in the policy scheme / multi-scheme setup. | `NoResult` on every scheme yields an unauthenticated principal, which under a *missing* fallback policy means the request **succeeds anonymously**. | A.5.2 #2 and #4; default-deny fallback policy in both modes. |
| **R6** | **MCP OAuth issuer validation disabled at registration** (A.2.4) and then not re-checked in `OnTokenValidated`. | Complete bypass: any RS256 token from any issuer is accepted. Nothing logs. | Dedicated test: token minted for host A rejected on host B. Flag this file for mandatory Seraph security review. |
| **R7** | **`CallerContext.Owns` semantics drift** during the claims projection (it matches `User` **or** `GitHubLogin`). | Silent broken-access-control in either direction — too permissive is a security bug, too strict looks like a random 403. | PR 1 is deliberately isolated so this is reviewable alone; `HttpContextAuthenticatedOwnerContextTests` + `ProjectOwnershipAuthorizationTests` are the guards. |
| **R8** | **Epoch check relocation** (A.3.3) changes which requests it applies to. | A stale-epoch pod could start serving authenticated traffic (security) or start failing health probes and get evicted (availability). | Preserve the "only when an `Authorization` header is present" condition verbatim; `AuthModeSwitchTests` covers it. |
| **R9** | **Test bypass leaking out of Development.** The bypass becomes a registered *scheme* rather than an `if` inside one middleware — a larger, more permanent-looking surface. | A configuration slip in a non-Development environment would authenticate arbitrary tokens. | Register the scheme **only** when `environment.IsDevelopment() && Testing:BypassGitHubTokenAuth`; keep both `LogCritical` messages; add a test asserting the scheme is absent in Production. |
| **R10** | **Internal-service-key exemption widening** — `ProjectAuthorization.IsInternalServiceCaller` matches on a *username string*. | Anyone whose resolved principal name equals `Auth:User` inherits the Tier-2 bypass, regardless of scheme. Pre-existing, but the refactor is the moment to fix it — and the moment it could be made worse. | A.3.4: switch to a scheme/claim test in PR 3's cleanup step, with a test that a non-`InternalServiceKey` caller with a colliding name is **denied**. |
| **R11** | **Flag drift / rotting fallback.** `Auth:UseSchemeBasedPipeline` leaves two auth pipelines in the tree for a release cycle. The inert branch stops being exercised, environments drift apart (staging on, prod off), and the flag outlives its purpose. | The "safe rollback" silently stops being safe: a later change touches only the live branch, so flipping the flag back off restores a path that no longer matches current endpoint metadata, claims, or route registrations — and *nothing fails* until the moment you need the fallback. Environment drift also means a staging-green result can stop meaning anything about prod. Both are invisible without a deliberate check. | (a) **CI runs the full decision matrix against BOTH branches** on every build (A.5.2 #6) — the inert path is never unexercised; (b) log the resolved flag value at startup on every pod, so which pipeline is running is always visible; (c) state the **bake period explicitly** in PR 2's body and open PR 3 as a tracked follow-up issue at the same time, so deletion is scheduled, not hoped for; (d) any PR touching auth during the flag's lifetime must update both branches or explicitly state why not; (e) after PR 3, verify no environment still sets the removed key. |

## A.7 Effort summary

| PR | Stage | Scope | Size |
|---|---|---|---|
| **1** | Prepare | Claims consolidation + `GetCaller` projection + golden decision-matrix test against current behaviour | S |
| **2** | Cut over behind a flag | Schemes + policy scheme + authorization policies behind `Auth:UseSchemeBasedPipeline`; both branches parity-tested in CI; staging then production enablement | **L** (the bulk of the phase) |
| **3** | Clean up | Delete old middlewares + the flag, hardening, CI guards, docs — after the production bake | S–M |

Overall the phase remains **M–L** — larger than the original §2.5 estimate once the 73
`GetCaller` call sites and the MCP OAuth issuer constraint are accounted for. It is still the
best available investment: it deletes three bespoke middlewares, removes the last of the
duplicated allow-lists, and fixes two latent security weaknesses (R6-adjacent issuer handling
and R10's username-based service exemption) as a side effect.
