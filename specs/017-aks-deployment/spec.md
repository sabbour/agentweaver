# Feature Specification: Deploying Agentweaver to Azure Kubernetes Service (AKS)

**Feature Branch**: `017-aks-deployment`

**Created**: 2026-06-24

**Status**: Draft

**Input**: User description: "Deploy Agentweaver to Azure Kubernetes Service (AKS) with Gateway API through the Application Routing add-on, meshless Istio (ambient mode), and Kubernetes-native sandbox execution replacing the WSL+mxc sandbox. Reference hosted-copilot-sandbox patterns."

---

## Overview

Agentweaver today runs entirely on a developer's localhost — a Windows host with WSL2 — and depends on host-specific facilities that do not exist inside a Kubernetes container:

- **Sandbox execution** uses `WslMxcSandboxExecutor` (WSL2 + `mxc`/hyperlight microVM isolation) selected by `SandboxExecutorFactory.Create()`. On Linux the factory falls back to `LinuxBwrapExecutor` (bubblewrap) or `LinuxNativeMxcSandboxExecutor` (lxc), and finally to a deny-by-default `PassthroughExecutor`. **None of these provide acceptable per-run isolation inside an ordinary AKS pod**, because nested namespace/microVM isolation requires privileges and kernel features that standard pods do not have.
- **Persistence** is file-based: two SQLite databases (`agentweaver.db` via `SqliteDb`, `memory.db` via EF Core `MemoryDbContext`) plus git worktrees and project workspaces, all rooted at `AppPaths.DataDirectory` (`%LOCALAPPDATA%/agentweaver`, i.e. `~/.local/share/agentweaver/` on Linux). `AppPaths` is explicitly overridable through configuration (`Database:Path`), so no code change is required to relocate data — only a writable volume.
- **Identity / secrets** are local: the GitHub Copilot token is resolved by `GitHubCopilotClientFactory` from the OS credential store (`OsCredentialStoreGitHubTokenStore`) or a config fallback (`Providers:GitHubCopilot:GitHubToken`/`ApiKey`). Neither exists in a cluster.

This feature delivers a Kubernetes-native deployment of Agentweaver on AKS. The single most important change is replacing the WSL+mxc sandbox with a **Kubernetes-native, per-run isolated execution environment**, modeled on the `agent-sandbox` `SandboxTemplate`/`SandboxWarmPool`/`SandboxClaim` flow proven in the `hosted-copilot-sandbox` reference project. Around that, the deployment uses the AKS **Application Routing add-on with Gateway API** (`HTTPRoute`, not `Ingress`) for HTTPS ingress, **Cilium NetworkPolicy** (default-deny + allow-list, enforced by the `--network-dataplane cilium --enable-acns` data plane) for inter-pod isolation, **Azure Key Vault + workload identity** for secrets, and **persistent volumes** for SQLite and workspace storage. Note: the `approuting-istio` GatewayClass uses Istio **for the gateway only** — there is **no** Istio service mesh (no sidecars and no ambient/ztunnel) on workload pods (see Clarification C4).

### What is fundamentally different from localhost

| Concern | Localhost (today) | AKS (this spec) |
|---------|-------------------|-----------------|
| Sandbox isolation | WSL2 + mxc microVM (`WslMxcSandboxExecutor`) | Per-run isolated pod (Kata VM runtime class + NetworkPolicy + non-root), launched via a Kubernetes-native sandbox claim |
| Ingress | Kestrel on `localhost:5000`/`7120` | Gateway API `Gateway` + `HTTPRoute` via Application Routing add-on, HTTPS terminated at gateway |
| Inter-component trust | In-process / loopback | Cilium NetworkPolicy (default-deny + allow-list) via `--network-dataplane cilium --enable-acns` |
| Secrets | OS credential store / user secrets | Azure Key Vault via CSI driver + workload identity |
| Storage | `~/.local/share/agentweaver/` on host disk | PersistentVolumeClaim (Azure Disk / Azure Files) |
| Images | `dotnet run` / `vite dev` | Container images in ACR, deployed via Deployments |

### Reference patterns adopted from `hosted-copilot-sandbox`

The reference repo (`C:\Users\asabbour\Git\hosted-copilot-sandbox`) demonstrates the target architecture. Concrete patterns reused here:

- **Cluster creation** (`scripts/aks/00-create-cluster.sh`): `az aks create ... --enable-app-routing-istio --enable-gateway-api --enable-default-domain --workload-runtime KataVmIsolation --os-sku AzureLinux --attach-acr <ACR_ID>`.
- **Gateway API** (`k8s/aks/gateway.yaml`): `Gateway` with `gatewayClassName: approuting-istio`, an HTTPS listener on `443` with `tls.mode: Terminate` and a `certificateRefs` entry; `HTTPRoute`s (`httproute-shim.yaml`, `admin-ui-route.yaml`) with `PathPrefix` matches and `backendRefs` to services.
- **Sandbox model** (`k8s/aks/sandbox-template-kata.yaml`, `k8s/sandbox-warmpool.yaml`, `shim/server.js`): the upstream `agent-sandbox` controller with a `SandboxTemplate` (warm-pod blueprint, `runtimeClassName: kata-vm-isolation`, `restartPolicy: Never`, `automountServiceAccountToken: false`, dedicated SA, `emptyDir` workspace), a `SandboxWarmPool` (pre-warmed replicas), and a per-session `SandboxClaim` that late-binds a warm pod to a run.
- **Network isolation** (`k8s/aks/networkpolicy.yaml`): default-deny ingress on sandbox pods; egress restricted to DNS + GitHub/allowed endpoints; ingress to the orchestrator only from the gateway.
- **Image build** (`scripts/aks/10-build-push-images.sh`): `az acr build` per image; managed-identity ACR attach means no `imagePullSecret`. Reference Dockerfiles use `mcr.microsoft.com/dotnet/sdk:10.0` → `mcr.microsoft.com/dotnet/aspnet:10.0`, non-root UID, explicit `EXPOSE`.

> Note on divergence: the reference repo does **not** ship `SecretProviderClass`/workload-identity manifests, and its sandbox pods run as root (`runAsNonRoot: false`) under Kata for defense-in-depth. This spec **adds** Key Vault CSI + workload identity and explicit **Cilium NetworkPolicy** (default-deny + allow-list) as first-class requirements (see Clarifications). The `approuting-istio` GatewayClass is used for gateway routing only; **no** Istio service mesh (sidecar or ambient/ztunnel) is deployed on workload pods.

---

## Clarifications

Key design decisions made for this spec. Each records the chosen option and rationale so an implementing engineer can proceed without re-deciding.

### C1 — Sandbox execution model: agent-sandbox SandboxClaim (warm pool), not raw Jobs

**Decision**: Adopt the **`agent-sandbox` controller** pattern (`SandboxTemplate` + `SandboxWarmPool` + per-run `SandboxClaim`) rather than spawning a fresh Kubernetes `Job` per run.

**Rationale**: Cold-starting a pod (image pull, .NET + toolchain init) per agent run adds tens of seconds of latency; the reference project solved this with a warm pool that is late-bound on claim. The controller also owns pod/PVC/service lifecycle and garbage collection, which is simpler and safer than the API hand-rolling Job creation and cleanup. A new `KubernetesSandboxExecutor` (implementing the existing `ISandboxExecutor` interface) becomes the selected backend when running in-cluster. (Raw `Job`-per-run is documented as a fallback/simpler alternative in FR-026 for environments without the controller.)

### C2 — Sandbox isolation boundary: Kata VM runtime class + NetworkPolicy + non-root

**Decision**: Sandbox pods run under `runtimeClassName: kata-vm-isolation` (provided by `--workload-runtime KataVmIsolation` at cluster create), with a per-sandbox `NetworkPolicy` (default-deny ingress, egress limited to DNS + GitHub endpoints), `automountServiceAccountToken: false`, and a non-root `securityContext` with a read-only root filesystem plus a writable workspace volume.

**Rationale**: Kata gives VM-grade isolation equivalent in intent to the localhost mxc microVM, satisfying the requirement that sandboxed agent shell commands cannot escape to the node or reach the orchestrator. We strengthen the reference (which ran sandbox containers as root under Kata) by adding `runAsNonRoot: true` + read-only rootfs as defense-in-depth.

### C3 — Persistence: keep SQLite on a single-replica API with PVC (no managed DB migration in v1)

**Decision**: The Agentweaver.Api `Deployment` runs as a **single replica** (`replicas: 1`, `strategy: Recreate`) with a `PersistentVolumeClaim` mounted at the data directory; `Database:Path` is set so SQLite (`agentweaver.db`, `memory.db`), git worktrees, and workspaces all live on the volume.

**Rationale**: SQLite + EF Core migrations are already wired and work unchanged when `AppPaths`/`Database:Path` points at a writable mount. A single replica avoids SQLite multi-writer corruption and matches today's single-process model. Migrating to Azure SQL/PostgreSQL is explicitly **out of scope** (future work) — but the storage class choice (Azure Disk RWO) keeps that door open. (See Assumptions and Out of Scope.)

### C4 — Inter-pod security: Cilium NetworkPolicy (default-deny + allow-list), no Istio service mesh

**Decision**: The `approuting-istio` GatewayClass provides **Istio-based gateway routing only**. **No** Istio service mesh is deployed on workload pods — there are **no sidecars** and **no ambient/ztunnel** dataplane on application or sandbox pods. Inter-pod security relies entirely on **Cilium NetworkPolicy** (a default-deny baseline plus explicit allow-list rules) enforced by the Cilium data plane enabled at cluster creation with `--network-dataplane cilium --enable-acns` (Advanced Container Networking Services). The allow-list grants: gateway → API and gateway → Frontend; API → MCP; and sandbox egress restricted to DNS plus an FQDN allow-list (GitHub and explicitly approved hosts). All other pod-to-pod traffic is denied by default.

**Rationale**: Istio ambient mode does **not** work with the `approuting-istio` gateway class — `approuting-istio` installs Istio for the gateway data plane only, not as a workload service mesh. Attempting to label namespaces `istio.io/dataplane-mode: ambient` and apply `PeerAuthentication`/`AuthorizationPolicy` resources against ztunnel does not produce a working mesh in this configuration. Cilium NetworkPolicy (already available because the cluster runs the Cilium dataplane with ACNS) provides identity/label-based L3/L4 isolation between components without a service mesh, achieving the same lateral-movement-prevention goal. FQDN-based egress policy (an ACNS/Cilium capability) enforces the sandbox egress allow-list.

### C5 — Frontend hosting: containerized static server behind the same Gateway

**Decision**: `apps/web` (React/Vite SPA) is built to static assets and served by a minimal container (e.g. nginx or a static file server) as its own `Deployment` + `Service`, routed by an `HTTPRoute` for `/`. The API is routed by an `HTTPRoute` for `/api/*` on the same `Gateway`/host.

**Rationale**: The web app is a pure client-side SPA (`vite build`); no SSR. A static container keeps it cache-friendly and lets the gateway path-split `/api` vs `/` cleanly.

### C6 — MCP server exposure: external via Gateway HTTPRoute, bearer-token protected

**Decision**: `Agentweaver.Mcp` is exposed **externally** through an `HTTPRoute` on the same `Gateway` (e.g. path `/mcp` or a dedicated subdomain), protected by **authentication and authorization**. Every MCP request (HTTP/SSE) MUST carry a valid **bearer token**; the token is either a GitHub-OAuth-derived token or a long-lived **API key** issued by Agentweaver for non-interactive CLI use. The token is validated against the stored GitHub auth or a separate API-key store. Cilium NetworkPolicy still restricts which in-cluster pods may reach the MCP service directly.

**Rationale**: A developer's primary workflow is using the **Copilot CLI locally** against Agentweaver tools. Keeping MCP cluster-internal would force them to change their workflow. Exposing MCP via the Gateway (alongside `/api` and `/`) lets a local Copilot CLI connect to the hosted MCP endpoint and authenticate with a token, exactly as VS Code extensions and CLI tools authenticate to hosted services. The bearer-token requirement keeps the externally reachable surface protected. (This supersedes the earlier "in-cluster only, ClusterIP, no HTTPRoute" decision.)

### C7 — Sandbox executor selection: router/factory, not a raw DI override

**Decision**: The `ISandboxExecutor` DI registration MUST be provided by a proper **`ISandboxExecutorRouter`** (or named factory) that inspects the runtime environment and selects the backend: **in-cluster → `KubernetesSandboxExecutor`**; **not in-cluster → the existing `SandboxExecutorFactory` chain** (mxc/WSL/bwrap/lxc/passthrough). Both executors MUST coexist — the local mxc executor continues to work on developer machines. The current `if (SandboxExecutorFactory.IsInCluster)` raw `AddSingleton` override in `Program.cs` MUST be replaced by this routing pattern.

**Rationale**: The existing approach registers a second `AddSingleton<ISandboxExecutor>` that wins for `GetRequiredService<ISandboxExecutor>` only because it is registered last. This is fragile: if registration order changes, the override is silently dropped and the wrong executor is used (a security-relevant failure — falling back to local-only backends in-cluster). A router/factory that makes the selection explicit (in-cluster probe + configuration override) removes the order dependency and makes the decision auditable. The selection decision must also be emitted as a run event (see C8).

### C8 — Executor selection observability

**Decision**: The executor-selection decision (which backend was chosen and why) MUST be emitted as a run event, per the contract established in spec 002. The Kubernetes in-cluster path MUST be verified to wire this event.

**Rationale**: Operators and users need to confirm that runs in-cluster actually use `KubernetesSandboxExecutor` (real isolation) and not a degraded fallback. Surfacing the selection as an event makes silent mis-selection detectable.

### C9 — Authorization: authn exists, authz must be added (GitHub org/team membership, fail closed)

**Decision**: Authentication via GitHub OAuth (`GitHubOAuthRedirectService`) and device flow (`GitHubDeviceFlowAuthService`) is present, but there is **no authorization layer** today (no `[Authorize]` attributes, no token-checking middleware on API routes, no user-identity propagation). For the AKS deployment this is a **blocker**: the API and Web MUST be protected. The authz model is **GitHub Organization (and optionally Team) membership**, configuration-driven and **fail-closed**:

- **Organization membership** — only members of a configured GitHub organization (`Auth:GitHub:AllowedOrg`, e.g. `myorg`) are permitted. The API verifies org membership via the GitHub API after OAuth completion.
- **Optional Team membership** — access can be further restricted to a specific team within the org (`Auth:GitHub:AllowedTeam`, e.g. `myorg/agentweaver-users`).
- **Configuration-driven** — the org/team restriction is set in configuration (Key Vault secret or appsettings).
- **Fail closed** — if `Auth:GitHub:AllowedOrg` is **not** configured, the API MUST deny all access (return `403` with a clear misconfiguration message), **not** allow all. An unconfigured authz policy is a deployment error, never a permissive default. "Any authenticated GitHub user" is explicitly **not** an acceptable model.

**Rationale**: Hosting Agentweaver on a public Gateway endpoint without authorization would expose all functionality to anyone who can reach the URL. Authn proves identity but does not gate access. Allowing "any authenticated GitHub user" is far too broad — any GitHub account on the internet would qualify. Restricting to members of a specific org/team scopes access to the intended user population, and failing closed when the policy is unconfigured prevents an accidental open deployment.

### C10 — GitHub App redirect URLs for AKS hosting

**Decision**: When deploying to AKS, the GitHub OAuth App (or GitHub App) registration MUST have its **Authorization callback URL** updated to the hosted callback (`https://<managed-domain>/auth/callback`), and the `Auth:GitHub:CallbackUrl` config value (delivered via K8s Secret/Key Vault) MUST match it. If the managed domain changes (e.g. the cluster is recreated and the default domain changes), the GitHub App registration MUST be updated again. This is a **manual operator step** that the runbook MUST document.

**Rationale**: `GitHubOAuthRedirectService` builds the OAuth redirect from `Auth:GitHub:CallbackUrl`. On localhost this points at `localhost`; in AKS it must point at the managed Gateway domain, and GitHub will reject callbacks whose URL is not registered on the app. Because the AKS default domain is assigned at cluster creation (and changes if recreated), this cannot be fully automated and must be a documented runbook step.

### C11 — SQLite on AKS: reliability assessment, POC vs. production, configurable backend

**Decision**: Keep SQLite on a single-replica API with a PVC (Azure Disk RWO, `Recreate` strategy) for this spec, documented as a **known limitation with a production upgrade path**. The data layer MUST support **configurable database backends** so swapping to a managed DB is a config-only change.

**Reliability assessment**:

- **What works**: Single-replica + Azure Disk RWO + `Recreate` is reliable for a POC. Azure Premium SSD provides durable storage; data survives pod restarts and rescheduling. The only risk is a brief **downtime gap** during pod replacement (`Recreate` detaches the disk from the old pod before attaching to the new one).
- **What doesn't scale**: No multi-replica (SQLite is single-writer by design); no automatic failover; recovery from disk corruption requires manual intervention; no point-in-time restore without an external backup.
- **Alternatives for production**: (a) **Azure SQL** — fully managed, multi-AZ, automated backups, EF Core connection pooling; (b) **Azure Database for PostgreSQL** — open source, EF Core via Npgsql; (c) keep SQLite but add **Azure Backup** for the PVC disk plus a read-replica pattern for `memory.db` (EF Core).
- **Recommendation**: SQLite on PVC is acceptable for a POC / single-operator deployment. The connection string and EF Core provider MUST be configurable (already partially true via `Database:Path`) so swapping to Azure SQL/PostgreSQL is a config-only change with no business-logic code changes beyond the EF provider registration.

**Rationale**: This makes the durability trade-off explicit instead of implicitly accepting SQLite as production-ready. Keeping the provider configurable preserves the migration path without committing to a managed DB in this spec.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Deploy Agentweaver to AKS with Gateway API ingress (Priority: P0)

As a platform operator, I can deploy the full Agentweaver stack (API, Frontend, MCP) to an AKS cluster and reach it over HTTPS through a Gateway API endpoint provisioned by the Application Routing add-on, so that users can use Agentweaver without any localhost setup.

**Why this priority**: This is the foundational MVP — without a reachable, running stack on AKS, nothing else matters. It proves the cluster, images, deployments, services, and Gateway/HTTPRoute wiring end-to-end.

**Independent Test**: Apply the cluster bootstrap and `k8s/` manifests to a fresh AKS cluster; browse to `https://<host>/` and load the Frontend; call `https://<host>/api/diagnostics` (or equivalent health endpoint) and receive a 200. No sandbox run is required for this story.

**Acceptance Scenarios**:

1. **Given** an AKS cluster created with `--enable-app-routing-istio --enable-gateway-api`, **When** the operator applies the API/Frontend/MCP Deployments, Services, the `Gateway`, and the two `HTTPRoute`s, **Then** a public HTTPS endpoint serves the Frontend at `/` and proxies the API at `/api/*`.
2. **Given** the stack is deployed, **When** the operator runs `kubectl get gateway,httproute -n agentweaver`, **Then** the `Gateway` reports a `PROGRAMMED=True` condition and an assigned address, and both `HTTPRoute`s report `Accepted=True`/`ResolvedRefs=True`.
3. **Given** TLS is configured on the HTTPS listener, **When** a user connects, **Then** the connection terminates TLS at the gateway with a valid certificate (default-domain or supplied cert).
4. **Given** the API Deployment has liveness/readiness probes, **When** the API container is starting, **Then** it is not added to the Service endpoints until the readiness probe passes.

---

### User Story 2 - Sandbox execution via Kubernetes-native isolation (Priority: P0)

As an Agentweaver user, when I start an agent run, the agent's shell commands execute inside a per-run isolated Kubernetes sandbox (replacing the WSL+mxc executor that cannot run in a container), so that runs are isolated from the orchestrator, from each other, and from the node, with restricted network egress.

**Why this priority**: Sandbox execution is the core security boundary of Agentweaver and the component that fundamentally cannot work as-is on AKS. The product is unsafe to run multi-tenant without it. This is co-P0 with US1 because a deployment that can't run agents safely is not viable.

**Independent Test**: Trigger an agent run through the API; observe that a sandbox pod is claimed/created for the run, the agent's shell commands execute inside it, run/output events stream back to the API and into the run event log, the sandbox is torn down at run completion, and an attempt by the sandbox to reach the API or a non-allowlisted host is blocked.

**Acceptance Scenarios**:

1. **Given** the API is running in-cluster, **When** an agent run requires shell execution, **Then** a `KubernetesSandboxExecutor` (selected by the in-cluster sandbox factory path) obtains an isolated sandbox pod for that run (via `SandboxClaim` against a warm pool, or a Job fallback) rather than attempting WSL/mxc/bwrap.
2. **Given** a sandbox pod is bound to a run, **When** the agent executes a command, **Then** stdout/stderr stream back as ordered `SandboxOutputChunk`s and a terminal exit-code chunk, preserving the existing `ISandboxExecutor` contract.
3. **Given** a sandbox pod is running, **When** it attempts a network connection to the Agentweaver API, the MCP service, or any host outside the egress allowlist, **Then** the connection is denied by Cilium NetworkPolicy (default-deny + FQDN egress allow-list).
4. **Given** a run completes, fails, or times out, **When** the executor finishes, **Then** the sandbox pod (and its ephemeral workspace) is released/garbage-collected and does not linger.
5. **Given** the sandbox enforces resource and time limits, **When** a command exceeds its CPU/memory limit or `TimeoutMs`, **Then** it is terminated and the result reports `TimedOut`/non-zero exit without affecting other runs.
6. **Given** the sandbox pod runs under the Kata runtime class as non-root with a read-only root filesystem, **When** the agent writes files, **Then** writes succeed only under the mounted writable workspace volume.

---

### User Story 3 - Cilium NetworkPolicy enforcement between components (Priority: P1)

As a security-conscious operator, I want all in-cluster traffic between the Frontend, API, MCP, and sandbox components governed by **Cilium NetworkPolicy** (a default-deny baseline plus an explicit allow-list), so that a compromised component cannot freely move laterally, and sandboxes cannot reach the API/MCP directly. No Istio service mesh (sidecar or ambient) is used for workload pods.

**Why this priority**: Strong defense-in-depth, but the stack is functional and demonstrable without it (US1/US2). It hardens, rather than enables, the deployment.

**Independent Test**: Apply the default-deny ingress policy and the allow-list policies on the workload namespace (enforced by the Cilium dataplane, `--network-dataplane cilium --enable-acns`); verify with in-cluster probes that an unlisted pod-to-pod connection (e.g. sandbox → API) is denied, that the gateway→API and gateway→Frontend paths work, that API→MCP works, and that sandbox egress is limited to the DNS + FQDN allow-list.

**Acceptance Scenarios**:

1. **Given** a default-deny ingress `CiliumNetworkPolicy`/`NetworkPolicy` on the workload namespace, **When** workloads are deployed, **Then** no pod accepts ingress except where an explicit allow rule exists (deny-by-default verified).
2. **Given** the gateway-to-API and gateway-to-Frontend allow rules, **When** the Gateway forwards a request to the API or Frontend, **Then** it is allowed; **When** a sandbox pod attempts to connect to the API or MCP, **Then** it is denied.
3. **Given** the API-to-MCP allow rule, **When** the API calls the MCP service, **Then** it is allowed; all other sources to MCP are denied.
4. **Given** the sandbox egress FQDN allow-list (DNS + GitHub + approved hosts), **When** a sandbox pod attempts an egress connection, **Then** only allow-listed FQDNs succeed and all other egress is denied.

---

### User Story 4 - Secrets via Azure Key Vault + workload identity (Priority: P1)

As an operator, I want the GitHub Copilot token and other secrets (e.g. API keys, admin token) sourced from Azure Key Vault and delivered to the API pod via the Key Vault CSI driver using workload identity (no static credentials in manifests), so that secrets are centrally managed, rotated, and never committed.

**Why this priority**: Important for production security and operability, but for an initial demo the token can be injected via a Kubernetes `Secret`. P1 because it replaces, rather than enables, the basic path.

**Independent Test**: Configure a Key Vault with the Copilot token, a user-assigned managed identity federated to the API's Kubernetes service account, a `SecretProviderClass`, and a CSI volume/`secretObjects`; deploy the API; verify it reads the token from the mounted secret/env and authenticates to GitHub Copilot, with no secret value present in any manifest.

**Acceptance Scenarios**:

1. **Given** a user-assigned managed identity federated to the API's service account via OIDC, **When** the API pod starts, **Then** it acquires an Entra token for Key Vault **without** any client secret in the pod.
2. **Given** a `SecretProviderClass` referencing the Copilot token in Key Vault, **When** the API pod mounts the CSI secrets volume, **Then** the token is available to the app (as a mounted file and/or synced env var) and `GitHubCopilotClientFactory` resolves it (via `Providers:GitHubCopilot:GitHubToken` config binding or token-store wiring).
3. **Given** the secret is rotated in Key Vault, **When** the CSI driver refreshes (or the pod restarts), **Then** the new value is used with no manifest change.
4. **Given** the manifests are committed to git, **When** they are reviewed, **Then** no plaintext secret values are present (only Key Vault references and identity client IDs).

---

### User Story 5 - Persistent storage for SQLite + workspaces (Priority: P1)

As an operator, I want the API's SQLite databases, git worktrees, and project workspaces stored on a persistent volume, so that data survives pod restarts and rescheduling.

**Why this priority**: Without persistence the system loses run history, memory, and workspaces on every restart. P1 (not P0) because US1 can be demonstrated ephemerally, but no real usage is durable without it.

**Independent Test**: Deploy the API with a PVC mounted at the data directory and `Database:Path` pointed at it; create a run and a project, delete the API pod, let it reschedule, and confirm the run history, `memory.db` content, and workspace files are intact.

**Acceptance Scenarios**:

1. **Given** a PVC (Azure Disk RWO) mounted at the data directory and `Database:Path` set accordingly, **When** the API starts, **Then** `agentweaver.db` and `memory.db` are created/migrated on the volume and EF Core migrations apply cleanly.
2. **Given** state exists on the volume, **When** the API pod is deleted and rescheduled (Recreate strategy), **Then** the new pod re-attaches the same volume and all prior data is present.
3. **Given** a single-replica Deployment, **When** rescheduling occurs, **Then** the Recreate strategy ensures the old pod releases the RWO disk before the new pod attaches it (no multi-attach/SQLite multi-writer).
4. **Given** workspaces and worktrees are written during runs, **When** the pod restarts, **Then** those directories persist under the same mount.

---

### User Story 6 - Local Copilot CLI connects to the hosted MCP endpoint (Priority: P1)

As a developer using the Copilot CLI locally, I can configure my CLI to connect to the hosted Agentweaver MCP endpoint, authenticate with a token, and use Agentweaver tools without changing my local workflow.

**Why this priority**: Preserves the developer's existing local workflow against the hosted deployment. P1 because the core stack (US1/US2) is usable via the Web UI without it, but external MCP access is a primary product affordance.

**Independent Test**: Configure a local Copilot CLI with the hosted MCP URL (e.g. `https://<host>/mcp`) and a bearer token (GitHub OAuth token or an Agentweaver-issued API key); confirm the CLI completes the MCP handshake (HTTP/SSE), lists Agentweaver tools, and invokes one; confirm that a request with a missing/invalid token is rejected with 401.

**Acceptance Scenarios**:

1. **Given** the MCP endpoint is exposed via an `HTTPRoute` on the Gateway, **When** a local Copilot CLI connects to `https://<host>/mcp` with a valid bearer token, **Then** the MCP session is established and Agentweaver tools are usable.
2. **Given** an MCP request with no bearer token or an invalid token, **When** it reaches the MCP endpoint, **Then** it is rejected with `401 Unauthorized`.
3. **Given** a bearer token that is a GitHub-OAuth-derived token OR an Agentweaver-issued API key, **When** the token is validated, **Then** both forms are accepted against their respective stores (GitHub auth / API-key store).
4. **Given** the developer's existing local CLI configuration, **When** they point it at the hosted endpoint, **Then** no other local workflow change is required.

---

### User Story 7 - Authorization gating the API and Web (Priority: P0)

As an operator, I can configure Agentweaver so that only members of a configured GitHub organization (optionally narrowed to a specific team) can access the API and Web, with unauthenticated requests redirected to GitHub OAuth or rejected with 401, non-members rejected with 403, and an unconfigured policy failing closed (deny all).

**Why this priority**: P0 because exposing the stack on a public Gateway without an authorization layer would make all functionality reachable by anyone with the URL. Authn exists today but authz does not, making this a deployment blocker.

**Independent Test**: Deploy with `Auth:GitHub:AllowedOrg` set; from an unauthenticated browser, request a protected Web route and confirm a redirect to GitHub OAuth; from an unauthenticated API client, request a protected endpoint and confirm `401`; authenticate as an org member and confirm access succeeds; authenticate as a non-member and confirm `403`; with `Auth:GitHub:AllowedTeam` set, confirm a member of the org but not the team is denied; finally, deploy with **no** `AllowedOrg` configured and confirm the API denies all access (`403` misconfiguration), never allowing open access.

**Acceptance Scenarios**:

1. **Given** the authz middleware is enabled, **When** an unauthenticated request hits any API endpoint except `/health`, `/auth/*`, or `/mcp` (which has its own token check), **Then** it is rejected with `401`.
2. **Given** an unauthenticated user opens the Web app, **When** the app loads, **Then** the user is redirected into the GitHub OAuth flow.
3. **Given** `Auth:GitHub:AllowedOrg` is configured and a user authenticates, **When** the API verifies GitHub org membership, **Then** a member is authorized (identity available to business logic) and a non-member is rejected with `403`.
4. **Given** `Auth:GitHub:AllowedTeam` is also configured, **When** an authenticated org member who is not in the team requests access, **Then** access is denied with `403`.
5. **Given** `Auth:GitHub:AllowedOrg` is **not** configured, **When** any request reaches a protected endpoint, **Then** the API fails closed and denies access with a clear misconfiguration error (`403`), never allowing open access.

---

### Edge Cases

- **Sandbox warm pool exhausted**: When all warm pods are claimed and a new run arrives, the system MUST either queue the claim until a pod is available or scale the pool, and surface a clear "sandbox capacity" signal rather than silently failing the run.
- **Sandbox controller unavailable**: If the `agent-sandbox` controller/CRDs are not installed, the in-cluster executor MUST fail closed with an actionable error (and MAY use the Job fallback when explicitly enabled), never falling back to `PassthroughExecutor` semantics that would run commands unsandboxed.
- **Gateway certificate not yet provisioned**: When the default-domain certificate is still issuing, `HTTPRoute`s should report not-yet-programmed; the operator runbook MUST document how to confirm certificate readiness.
- **PVC multi-attach**: If a second API pod is accidentally scheduled (e.g. manual scale-up), the RWO disk MUST prevent dual-attach; the spec mandates `replicas: 1` + `Recreate` to avoid this.
- **Copilot token missing/expired in cluster**: `GitHubCopilotClientFactory` throws `GitHubCopilotUnauthorizedException`; the deployment MUST surface this as a clear unauthenticated error and not crash-loop the pod.
- **Sandbox egress allowlist too narrow**: If a run needs an endpoint not in the allowlist (e.g. a package registry), the run fails network calls; the allowlist MUST be a reviewable ConfigMap/NetworkPolicy so it can be extended deliberately.
- **Long-running run vs node drain**: When a node is drained/upgraded, in-flight sandbox pods may be evicted; the API MUST detect sandbox pod loss (watch) and fail the affected run cleanly with a retriable signal.
- **Executor mis-selection in-cluster**: If the executor router fails to detect the in-cluster environment (e.g. probe error), it MUST fail closed (refuse to run shell commands) rather than silently falling back to a local-only backend (mxc/WSL/bwrap) or passthrough; the selection decision MUST be observable via a run event.
- **MCP request without/with invalid token**: An external MCP request lacking a valid bearer token (GitHub OAuth token or Agentweaver API key) MUST be rejected with `401`, never served; token validation failures MUST NOT leak whether the token format was OAuth vs API key.
- **Unauthenticated access to API/Web**: Requests to protected routes without an authenticated session MUST be rejected (`401`) or redirected to GitHub OAuth (Web); `/health`, `/auth/*`, and `/mcp` (own token check) are the only unauthenticated exceptions. Authenticated users who are not members of the configured GitHub org/team MUST be rejected with `403`.
- **Authz policy unconfigured (fail closed)**: If `Auth:GitHub:AllowedOrg` is not configured, the API MUST deny all access (`403` with a clear misconfiguration message) rather than defaulting to open or "any authenticated user"; this is a deployment error the runbook MUST call out.
- **GitHub callback URL mismatch after redeploy**: If the AKS managed domain changes (cluster recreated) but the GitHub App's Authorization callback URL / `Auth:GitHub:CallbackUrl` is not updated, OAuth login fails; the runbook MUST flag this manual step and the failure MUST surface a clear configuration error, not a crash loop.
- **SQLite downtime gap on pod replacement**: Because the API uses `Recreate`, there is a brief unavailability window while the RWO disk detaches from the old pod and attaches to the new one; this is an accepted POC limitation and MUST be documented (production upgrade path: managed DB per C11).

---

## Requirements *(mandatory)*

### Functional Requirements

#### Cluster & platform

- **FR-001**: The deployment MUST target an AKS cluster created with the Application Routing add-on (Istio variant) and Gateway API enabled, i.e. `az aks create ... --enable-app-routing-istio --enable-gateway-api --enable-default-domain`. The cluster MUST also use the **Cilium network dataplane with Advanced Container Networking Services**, i.e. `--network-dataplane cilium --enable-acns`, so that label-based and FQDN-based NetworkPolicy can be enforced.
- **FR-002**: The cluster MUST enable a VM-isolation workload runtime for sandbox pods via `--workload-runtime KataVmIsolation`, exposing a `kata-vm-isolation` runtime class.
- **FR-003**: The cluster MUST be attached to an Azure Container Registry (`--attach-acr <ACR_ID>`) so images are pulled via managed identity with no `imagePullSecret`.
- **FR-004**: Node pools MUST be designed as: a **system** pool (cluster services), a **workload** pool (API, Frontend, MCP), and a **sandbox** pool (sized/tainted for sandbox + Kata workloads). Sandbox pods MUST schedule onto the sandbox pool via nodeSelector/taints+tolerations; orchestrator pods MUST NOT schedule onto the sandbox pool.
- **FR-005**: The cluster MUST enable OIDC issuer and workload identity (`--enable-oidc-issuer --enable-workload-identity`) and the Key Vault Secrets Provider add-on (CSI) for secret delivery.

#### Application deployment

- **FR-006**: Agentweaver.Api MUST deploy as a `Deployment` (single replica, `strategy: Recreate`) + `ClusterIP` `Service`, listening on its HTTP port (container port aligned to `ASPNETCORE_URLS`, e.g. `8080`).
- **FR-007**: The API Deployment MUST define liveness and readiness probes against a lightweight HTTP endpoint, and resource `requests`/`limits` for CPU and memory.
- **FR-008**: The API MUST receive configuration via env/ConfigMap including `Database:Path` (pointing at the persistent volume), `Cors:AllowedOrigins` (the public host), and `Providers:GitHubCopilot` token wiring; secret values MUST come from Key Vault/Secret, never inline.
- **FR-009**: Agentweaver.Frontend (`apps/web`) MUST be built to static assets (`vite build`) and served by a static-file container `Deployment` + `Service`; it MUST be configured to call the API at the same public host under `/api`.
- **FR-010**: Agentweaver.Mcp MUST deploy as a `Deployment` + `Service`, exposed **externally** via an `HTTPRoute` on the same `Gateway` (e.g. `PathPrefix: /mcp` or a dedicated subdomain) so a local Copilot CLI can reach it; every MCP request MUST be authenticated by a bearer token (see FR-039..FR-042).

#### Gateway API (Application Routing add-on)

- **FR-011**: A `Gateway` resource with `gatewayClassName: approuting-istio` MUST define an HTTPS listener on port `443` with `tls.mode: Terminate` and a `certificateRefs` entry (default-domain certificate or operator-supplied secret).
- **FR-012**: An `HTTPRoute` MUST route `PathPrefix: /api` to the API Service, a second `HTTPRoute` MUST route `PathPrefix: /` to the Frontend Service, and a third `HTTPRoute` MUST route `PathPrefix: /mcp` (or a dedicated subdomain) to the MCP Service, all bound to the `Gateway` via `parentRefs`.
- **FR-013**: The spec MUST document what the Application Routing add-on manages (the gateway data-plane Deployment/Service, certificate wiring, DNS for default domain) versus what the operator authors (the `Gateway`, `HTTPRoute`s, `certificateRefs`, hostnames).
- **FR-014**: If routes reference Services/secrets across namespaces, the required `ReferenceGrant` resources MUST be included.

#### Inter-pod security (Cilium NetworkPolicy)

- **FR-015**: Inter-pod security MUST be enforced by **Cilium NetworkPolicy** (default-deny ingress baseline + explicit allow-list) on the Agentweaver workload namespace(s). **No** Istio service mesh (no sidecar injection, no `istio.io/dataplane-mode: ambient` label, no ztunnel) is applied to workload pods; the `approuting-istio` GatewayClass is used for gateway routing only.
- **FR-016**: A default-deny ingress policy MUST apply to the workload namespace so that pods accept ingress only where an explicit allow rule exists; the meaning (gateway-only entry, no lateral pod-to-pod traffic by default) MUST be documented.
- **FR-017**: Allow-list `CiliumNetworkPolicy`/`NetworkPolicy` rules MUST permit: Gateway → API, Gateway → Frontend, Gateway → MCP, and API → MCP; the sandbox service account MUST be denied any path to the API and MCP. Sandbox egress MUST be limited to DNS plus an **FQDN allow-list** (GitHub and approved hosts) using the Cilium/ACNS FQDN policy capability.
- **FR-018**: Each workload MUST run under a distinct Kubernetes `ServiceAccount` and carry distinct pod labels so that label/identity-based NetworkPolicy can distinguish API, Frontend, MCP, gateway, and sandbox.

#### Sandbox execution

- **FR-019**: A new `KubernetesSandboxExecutor` MUST implement the existing `ISandboxExecutor` interface (`ExecuteAsync`, `StreamAsync`, `IsRealIsolation`, `BackendName`, network-warning surface) so the agent runtime (`CopilotAIAgent`, consumed via `SandboxExecutorFactory`) is unchanged above the executor boundary.
- **FR-020**: The `ISandboxExecutor` DI registration MUST be provided by a proper **`ISandboxExecutorRouter`** (or named factory) that inspects the runtime environment (in-cluster probe via service-account token / downward API, plus a configuration override) and selects `KubernetesSandboxExecutor` when in-cluster or the existing `SandboxExecutorFactory` chain (mxc/WSL/bwrap/lxc/passthrough) otherwise. Both executors MUST coexist (local mxc continues to work). A raw `AddSingleton<ISandboxExecutor>` override that wins only by registration order (the current `if (IsInCluster)` pattern in `Program.cs`) MUST NOT be used, since it is silently replaced if registration order changes.
- **FR-020a**: The executor-selection decision (chosen backend and reason) MUST be emitted as a run event per the spec 002 contract, and the Kubernetes in-cluster path MUST be verified to wire this event.
- **FR-021**: The sandbox MUST run each agent run in an isolated pod claimed from a warm pool via the `agent-sandbox` `SandboxClaim` flow, backed by a `SandboxTemplate` and `SandboxWarmPool`.
- **FR-022**: Sandbox pods MUST use `runtimeClassName: kata-vm-isolation`, `restartPolicy: Never`, `automountServiceAccountToken: false`, a non-root `securityContext` (`runAsNonRoot: true`, `seccompProfile: RuntimeDefault`), a read-only root filesystem, and a writable workspace volume (ephemeral `emptyDir` by default).
- **FR-023**: Sandbox pods MUST be governed by a `NetworkPolicy` that default-denies ingress and restricts egress to DNS plus an allowlisted set of endpoints (GitHub API and other explicitly approved hosts), with the allowlist authored as a reviewable resource (ConfigMap/NetworkPolicy).
- **FR-024**: The executor MUST enforce per-run CPU/memory `limits` and the command `TimeoutMs`, terminating overruns and reporting `TimedOut`/exit code via the existing result/chunk contract.
- **FR-025**: The API MUST launch and monitor sandbox pods using the Kubernetes client API — watch pod/claim status, stream logs/output back into run events, and detect premature pod loss (eviction) to fail the run cleanly.
- **FR-026**: The system MUST release/garbage-collect the sandbox (claim + pod + ephemeral workspace) on run completion, failure, or timeout. A simpler **Kubernetes `Job`-per-run** path MAY be provided as a documented fallback for clusters without the `agent-sandbox` controller; it MUST preserve the same isolation guarantees (Kata, NetworkPolicy, non-root, limits) and MUST NOT degrade to unsandboxed passthrough.
- **FR-027**: When no Kubernetes sandbox backend is available in-cluster, the executor MUST fail closed (refuse to run shell commands) rather than executing them unsandboxed.

#### Persistence

- **FR-028**: The API MUST mount a `PersistentVolumeClaim` (Azure Disk, `ReadWriteOnce`) at the data directory, and `Database:Path` MUST point at that mount so `agentweaver.db`, `memory.db`, worktrees, and workspaces persist there.
- **FR-029**: EF Core migrations (`MemoryDbContext.MigrateAsync`) and `SqliteDb.EnsureCreatedAsync()` MUST run against the volume-backed database on startup without manual steps.
- **FR-030**: If multi-pod read-write file sharing is ever required for workspaces, the spec MUST note Azure Files (`ReadWriteMany`) as the alternative storage class; the v1 default is single-replica Azure Disk RWO.

#### Secrets & identity

- **FR-031**: A user-assigned managed identity MUST be federated (OIDC) to the API's Kubernetes `ServiceAccount`, annotated for workload identity, granting the API pod tokens for Key Vault with no static credentials.
- **FR-032**: A `SecretProviderClass` MUST map Key Vault secrets (at minimum the GitHub Copilot token; optionally an admin/API token) into the API pod via the Key Vault CSI driver, optionally synced to a Kubernetes `Secret`/env (`secretObjects`).
- **FR-033**: No secret values may appear in committed manifests — only Key Vault references and identity client IDs. A non-secret `Secret`/ConfigMap fallback path (for local/demo clusters) MUST be clearly separated and documented as non-production.

#### Images & CI/CD

- **FR-034**: A `Dockerfile` for Agentweaver.Api MUST build with `mcr.microsoft.com/dotnet/sdk:10.0` and run on `mcr.microsoft.com/dotnet/aspnet:10.0`, run as a non-root user, `EXPOSE` the HTTP port, and set `ASPNETCORE_URLS` accordingly.
- **FR-035**: A `Dockerfile` for Agentweaver.Frontend MUST build static assets and serve them from a minimal non-root static-server image exposing an HTTP port.
- **FR-036**: A build/push script (`az acr build`-based) MUST build and push API, Frontend, and MCP images to ACR with a tag, mirroring `scripts/aks/10-build-push-images.sh`.
- **FR-037**: The API/Frontend/MCP Deployments MUST use `RollingUpdate` for stateless components; the API (stateful, single-replica) MUST use `Recreate` to avoid PVC multi-attach.
- **FR-038**: Manifests MUST be organized so that the sequence of operations (create cluster → install agent-sandbox controller → build/push images → deploy → configure secrets/identity) is reproducible from scripts, mirroring the reference `scripts/aks/00..90` numbering.

#### External MCP access & authentication

- **FR-039**: The MCP endpoint MUST be accessible externally via the Gateway (`HTTPRoute` at `/mcp` or a dedicated subdomain), alongside the `/api` and `/` routes, so a local Copilot CLI can connect to the hosted MCP server.
- **FR-040**: Every MCP request (HTTP/SSE) MUST require a valid **bearer token**; requests with a missing or invalid token MUST be rejected with `401`.
- **FR-041**: The accepted bearer token MUST be either a GitHub-OAuth-derived token OR a long-lived **API key** issued by Agentweaver for non-interactive CLI use; the token MUST be validated against the stored GitHub auth or a separate API-key store, respectively.
- **FR-042**: The Copilot CLI configuration to reach the hosted MCP endpoint MUST require only the endpoint URL and a token — no other local workflow change.

#### Authorization (authn already present, authz added)

- **FR-043**: All API endpoints except `/health`, `/auth/*`, and `/mcp` (which has its own bearer-token check per FR-040) MUST require an authenticated session; unauthenticated requests MUST be rejected with `401`.
- **FR-044**: Authentication state MUST be carried in a signed session cookie or JWT, and the authenticated user identity MUST be propagated to business logic.
- **FR-045**: The Web app MUST redirect unauthenticated users into the GitHub OAuth flow.
- **FR-046**: Authorization MUST be based on **GitHub Organization membership**: only members of the configured `Auth:GitHub:AllowedOrg` are permitted; the API MUST verify org membership via the GitHub API after OAuth completion, and reject non-members with `403`. "Any authenticated GitHub user" MUST NOT be accepted as an authorization model.
- **FR-046a**: Authorization MAY be further narrowed to a specific team via `Auth:GitHub:AllowedTeam` (`org/team`); when set, the API MUST verify team membership and reject org members who are not in the team with `403`.
- **FR-046b**: The org/team policy MUST be configuration-driven (Key Vault secret or appsettings) and **fail closed**: if `Auth:GitHub:AllowedOrg` is not configured, the API MUST deny all access (return `403` with a clear misconfiguration message) rather than allowing open access. An unconfigured authz policy is a deployment error, never a permissive default.

#### GitHub App configuration for AKS

- **FR-047**: The operator runbook MUST document that, when deploying to AKS, the GitHub OAuth App (or GitHub App) Authorization callback URL MUST be set to `https://<managed-domain>/auth/callback` and the `Auth:GitHub:CallbackUrl` config value (delivered via K8s Secret/Key Vault) MUST match it; if the managed domain changes (e.g. cluster recreated), both MUST be updated again. This is a manual operator step.

#### Configurable data backend

- **FR-048**: The data layer MUST support **configurable database backends** (SQLite for POC; Azure SQL or PostgreSQL for production) selected via configuration, such that the EF Core provider is swappable without changes to business-logic code (only the connection string and EF provider registration change). SQLite on a PVC is the documented v1 default and a known limitation with a production upgrade path (see C11).

### Key Entities *(include if feature involves data)*

- **AKS Cluster**: The managed Kubernetes cluster with Application Routing (Istio gateway), Gateway API, Cilium network dataplane + ACNS, Kata workload runtime, OIDC issuer, workload identity, and Key Vault CSI add-ons; organized into system/workload/sandbox node pools.
- **Gateway**: Gateway API `Gateway` (class `approuting-istio`) owning the HTTPS listener and TLS certificate; the public entry point.
- **HTTPRoute**: Path-based routing rules — `/api` → API Service, `/` → Frontend Service — bound to the `Gateway`.
- **API Deployment / Service / PVC**: Single-replica stateful orchestrator with persistent data volume and Copilot identity.
- **Frontend Deployment / Service**: Stateless static SPA server.
- **MCP Deployment / Service**: Tool backend exposed externally via the Gateway `HTTPRoute` (`/mcp`), protected by a bearer-token check.
- **SandboxTemplate**: Warm-pod blueprint (Kata runtime class, non-root, ephemeral workspace, restricted SA) for sandbox pods.
- **SandboxWarmPool**: Pre-warmed set of sandbox pods (replicas) ready for fast late-binding.
- **SandboxClaim**: Per-run binding of a warm sandbox pod to a specific agent run; created and released by the API/executor.
- **SandboxPod (Job fallback)**: The isolated per-run execution unit (warm-pool pod or Job-spawned pod) where agent shell commands run.
- **NetworkPolicy**: Cilium-enforced ingress/egress isolation for sandbox and workload pods (default-deny ingress, label-based allow-list, FQDN egress allow-list for sandboxes).
- **SecretProviderClass + Managed Identity (workload identity)**: Key Vault → pod secret delivery without static credentials.
- **PersistentVolumeClaim**: Azure Disk RWO volume holding SQLite DBs, worktrees, and workspaces.
- **SandboxExecutorRouter**: Router/named-factory that selects the `ISandboxExecutor` backend (in-cluster → `KubernetesSandboxExecutor`; otherwise the local `SandboxExecutorFactory` chain) and emits the selection as a run event.
- **API key store**: Store of long-lived Agentweaver-issued API keys used (alongside GitHub OAuth tokens) to authenticate external MCP requests.
- **Authenticated session**: Signed session cookie / JWT carrying the authenticated GitHub user identity that gates access to the API and Web, authorized by GitHub org (and optional team) membership; fails closed when no `AllowedOrg` is configured.

### Infrastructure / Data Model

Proposed repository layout (under a new top-level `deploy/` or `k8s/` directory, mirroring the reference `hosted-copilot-sandbox/k8s/aks/` + `scripts/aks/`):

```
deploy/
  aks/
    scripts/
      00-create-cluster.sh        # az aks create with app-routing-istio, gateway-api, kata, oidc, workload-identity, attach-acr
      05-install-agent-sandbox.sh # install agent-sandbox CRDs + controller
      10-build-push-images.sh     # az acr build for api, web, mcp
      20-deploy.sh                 # kubectl apply manifests in order; recreate warm pool
      30-configure-identity.sh    # create UAMI, federated credential, Key Vault access policy
      90-teardown.sh
    manifests/
      namespace.yaml               # workload namespace (no Istio ambient label)
      serviceaccounts.yaml         # api-sa, web-sa, mcp-sa, sandbox-sa (+ workload-identity annotations)
      api-deployment.yaml          # single replica, Recreate, probes, resources, env, PVC mount
      api-service.yaml
      api-pvc.yaml                 # Azure Disk RWO
      web-deployment.yaml
      web-service.yaml
      mcp-deployment.yaml
      mcp-service.yaml
      gateway.yaml                 # approuting-istio, HTTPS:443 Terminate, certificateRefs
      httproute-api.yaml           # PathPrefix /api -> api-service
      httproute-web.yaml           # PathPrefix /  -> web-service
      httproute-mcp.yaml           # PathPrefix /mcp -> mcp-service (bearer-token protected)
      referencegrant.yaml          # if cross-namespace refs
      sandbox-template.yaml        # SandboxTemplate (Kata, non-root, emptyDir workspace)
      sandbox-warmpool.yaml        # SandboxWarmPool replicas
      networkpolicy-sandbox.yaml   # Cilium: default-deny ingress; egress DNS + FQDN allow-list
      networkpolicy-workload.yaml  # Cilium: default-deny + allow-list (gw->api/web/mcp, api->mcp)
      sandbox-egress-allowlist.configmap.yaml
      secretproviderclass.yaml     # Key Vault CSI for Copilot token + API keys
  Dockerfile.api                   # sdk:10.0 -> aspnet:10.0, non-root, EXPOSE 8080
  Dockerfile.web                   # vite build -> static server, non-root
```

Application-side change (new code, separate from manifests):

```
packages/Agentweaver.SandboxExec/
  KubernetesSandboxExecutor.cs     # implements ISandboxExecutor via SandboxClaim/Job + pod exec/log streaming
  SandboxExecutorRouter.cs         # router/named-factory: in-cluster -> Kubernetes, else SandboxExecutorFactory chain; emits selection event
  (SandboxExecutorFactory.cs)      # local mxc/WSL/bwrap/lxc/passthrough chain (unchanged, used by router off-cluster)
packages/Agentweaver.Api/ (auth & data)
  AuthorizationMiddleware          # require authenticated session + GitHub org/team membership; 401/403/redirect; fail closed if AllowedOrg unset
  McpBearerTokenAuth               # validate GitHub OAuth token or Agentweaver API key on /mcp
  ApiKeyStore                      # long-lived API keys for non-interactive CLI MCP access
  (EF Core provider registration)  # configurable backend: SQLite (POC) | Azure SQL/PostgreSQL (prod)
```

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A fresh AKS cluster can be brought to a fully running Agentweaver stack (API + Frontend + MCP reachable, Gateway programmed) by running the ordered bootstrap scripts, with zero manual manifest edits beyond environment variables (host, ACR, Key Vault names).
- **SC-002**: Users reach the Frontend over **HTTPS** at the public Gateway host and the Frontend successfully calls the API under `/api`, with TLS terminated at the gateway by a valid certificate.
- **SC-003**: Every agent run executes its shell commands inside a **per-run isolated sandbox pod** (Kata runtime class, non-root, restricted egress) — verified by observing one sandbox pod per active run and its teardown on completion — with **no** use of WSL/mxc/bwrap/passthrough in-cluster.
- **SC-004**: A sandbox pod's attempt to reach the Agentweaver API, the MCP service, or a non-allowlisted external host is **blocked** (Cilium NetworkPolicy: default-deny + FQDN egress allow-list), demonstrably failing such connections while allowing GitHub API egress.
- **SC-005**: In-cluster pod-to-pod traffic is governed by Cilium NetworkPolicy (default-deny + allow-list) with **no Istio sidecars and no ambient/ztunnel** on workload pods (pod container count equals the application container count); an unlisted path (e.g. sandbox → API) is demonstrably denied while gateway→API/Frontend/MCP and API→MCP succeed.
- **SC-006**: The GitHub Copilot token is delivered from Azure Key Vault via workload identity with **no secret value present in any committed manifest**, and agent turns authenticate to GitHub Copilot successfully.
- **SC-007**: Deleting and rescheduling the API pod preserves all run history, `memory.db` content, worktrees, and workspaces (data survives on the PVC), with EF Core migrations applying cleanly on startup.
- **SC-008**: Sandbox warm-pool late-binding makes an isolated environment available for a new run in a small fraction of the time a cold pod start would take (warm claim vs. full image pull + toolchain init), keeping per-run startup latency low.
- **SC-009**: A local Copilot CLI connects to the hosted MCP endpoint over HTTPS with a valid bearer token (GitHub OAuth token or Agentweaver API key), completes the MCP handshake, and invokes Agentweaver tools; a request with a missing/invalid token is rejected with `401`.
- **SC-010**: Unauthenticated access to the API (outside `/health`, `/auth/*`, `/mcp`) is rejected with `401`, and unauthenticated Web access redirects to GitHub OAuth; after authentication, only members of the configured `Auth:GitHub:AllowedOrg` (and `AllowedTeam`, if set) are authorized while non-members get `403`; with `AllowedOrg` unconfigured, the API fails closed (`403`) rather than allowing open access.
- **SC-011**: The executor-selection decision is observable as a run event, and in-cluster runs are confirmed to use `KubernetesSandboxExecutor` (never a local-only or passthrough fallback).
- **SC-012**: Switching the database backend from SQLite to Azure SQL/PostgreSQL is achievable as a configuration change (connection string + EF provider registration) with no business-logic code changes.

## Assumptions

- The implementer has an Azure subscription with permission to create an AKS cluster, an ACR, a user-assigned managed identity, and an Azure Key Vault, and to enable preview/GA add-ons (Application Routing Istio, Gateway API, Kata workload runtime, workload identity).
- An ACR is available (or created by the bootstrap script) and attached to the cluster for managed-identity image pulls.
- The `agent-sandbox` controller and its CRDs (`SandboxTemplate`, `SandboxWarmPool`, `SandboxClaim`) can be installed into the cluster (as in the reference project's `05-install-agent-sandbox.sh`).
- The API runs as a **single replica** because SQLite is a single-writer store; horizontal scaling of the API is out of scope for v1.
- `AppPaths.DataDirectory` / `Database:Path` is the only data-location surface needed; no application code change is required to relocate persistence beyond configuration and (for sandbox) the new executor.
- The GitHub Copilot token has the scopes Agentweaver already requires; token acquisition/refresh logic (`GitHubCopilotClientFactory`, token store/refresh) is reused unchanged, sourcing the token from Key Vault/config in-cluster.
- The default-domain certificate from the Application Routing add-on is acceptable for v1; a custom domain/cert can be substituted via `certificateRefs`.
- The reference `hosted-copilot-sandbox` repo remains the canonical source for concrete manifest shapes (gateway class, HTTPRoute structure, SandboxTemplate fields, NetworkPolicy rules, ACR build flow).

## Out of Scope

- **Multi-region / HA**: single-region, single-replica API only; no geo-redundancy or multi-cluster.
- **Autoscaling the sandbox pool**: dynamic scaling of the warm pool / cluster autoscaler tuning for sandbox capacity is future work (the spec only requires a fixed warm pool and a clear capacity signal).
- **Migrating from SQLite to a managed database** (Azure SQL / PostgreSQL): the actual migration/operation of a managed DB is future work; this spec requires only that the EF Core provider/connection be **configurable** (FR-048) so the switch is a config-only change, and keeps SQLite-on-PVC as the documented v1 default (see C11).
- **Horizontal scaling of the API** (multi-writer persistence): out of scope; depends on the managed-DB migration above.
- **CI/CD pipeline automation** (GitHub Actions/Azure DevOps wiring) beyond the `az acr build` build/push scripts: the spec defines the build/deploy scripts but not a hosted pipeline.
- **Observability stack** (Prometheus/Grafana/Container Insights dashboards, tracing): assumed handled by standard AKS monitoring; not specified here.
- **Custom domain / DNS management** beyond using the add-on default domain or supplying an existing certificate secret.
- **Cost optimization / spot node pools** for the sandbox pool: noted as a future tuning concern, not specified.
