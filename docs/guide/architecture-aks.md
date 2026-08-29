---
title: AKS Architecture
---

# AKS Architecture

This document describes the architecture of the Agentweaver AKS deployment: its components, networking topology, security model, and storage design.

For step-by-step deployment instructions see [Deploy to AKS](/guide/deployment-aks).

---

## Component diagram

![AKS component diagram (simplified overview): Client through the Gateway to Frontend, API, MCP; API and Worker to the AgentHost warm pool; shared Workspace PVC and CSI SecretProviderClass; PostgreSQL, Key Vault, ACR, and GitHub](../diagrams/aks-component-simplified.png)

<!--
  Pre-rendered as a static PNG from ../diagrams/src/aks-component-simplified.json
  by docs/diagram-renderer (a Fluent-styled React Flow app) + Playwright, so
  it matches the same card/icon/badge look used live in the product UI
  instead of generic Mermaid/mermaid-cli output. To edit: change the
  graph-spec JSON, run `npm run docs:render-diagrams`, and commit the
  regenerated PNG + .hash.txt. CI fails if the spec's content hash drifts
  from the committed .hash.txt (see scripts/docs/capture-diagrams.mjs).
-->

> This is a simplified overview. See the detailed diagram below for the full request/data flow.

![AKS component diagram (detailed): full request/data flow between the Browser/AI client, AKS Gateway, Frontend, API, Worker, MCP, the Kata VM AgentHost warm pool, Workspace PVC, CSI SecretProviderClass, Azure Key Vault, Azure PostgreSQL, Azure Container Registry, and GitHub](../diagrams/aks-component-detailed.png)

<!--
  Pre-rendered the same way as the simplified diagram above, from
  ../diagrams/src/aks-component-detailed.json. To edit: change the
  graph-spec JSON, run `npm run docs:render-diagrams`, and commit the
  regenerated PNG + .hash.txt.
-->

---


## AgentHost warm-pool lifecycle

The Worker now runs in `pod-per-run`, so coordinator child agents execute in AgentHost pods via this warm pool rather than in-process on the Worker:

- **AgentHost pool** — `agentweaver-agent-host`, `k8s/base/sandbox-warmpool-agenthost.yaml`, `replicas: 2`, keeps two AgentHost pods pre-warmed for live agent turns.

Warm AgentHost pods boot with no `RunId`, enter standby, and accept `POST /configure` even while not ready for A2A turns. The executor claims one warm pod, waits for the claim binding, calls `/configure` with run identity, credentials, purpose, and a shared/local workspace descriptor, then waits for `/healthz` to become ready before sending the first `message:stream` turn. Normal runs use `ExecutionWorkspaceMode.Shared`. Assembly Build/Test uses `LocalReadOnly`, sends an immutable source ref/base SHA/tree contract, and executes from a verified checkout created inside the disk-backed `/local-workspace` emptyDir; its preview maps the API-resolved relative cwd into that same checkout. `LocalWritable` and `ImplementationTurn` define the reuse seam for #253 without enabling implementation write-back in this path.

![AgentHost warm-pool lifecycle: Standby, Configuring, Ready, Serving, Released](../diagrams/guide-architecture-aks-fig1.png)

<!-- Rendered from ../diagrams/src/guide-architecture-aks-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

`/configure` has one-time semantics (`409` after the first successful configuration). It is not protected by the turn bearer token because it delivers that token; the NetworkPolicy limiting AgentHost ingress to API/worker pods is the guard.

The live sandbox path binds claims to the AgentHost warm pool (`AgentHostWarmPoolRef`, default `agentweaver-agent-host`) and delivers per-run context through `/configure`; it does not create per-run templates or per-run warm pools for AgentHost. Source: `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:40`, `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:332`, `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:480`, `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:497`, `k8s/base/sandbox-template-agenthost.yaml:36`, `k8s/base/sandbox-warmpool-agenthost.yaml:19`.

---

## Networking flow

### Inbound request path

![Inbound request path: 🌐 Client, Public LoadBalancer IP, Gateway: agentweaver-gateway, Service: agentweaver-api, Service: agentweaver-mcp, Service: agentweaver-frontend, API Pod :8080, MCP Pod :8080, Frontend Pod :8080](../diagrams/guide-architecture-aks-fig2.png)

<!-- Rendered from ../diagrams/src/guide-architecture-aks-fig2.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

Route specificity: `/api` and `/mcp` (longer prefixes) win over `/` — no conflict.

### Gateway API resource relationships

![Gateway API resource relationships: Gateway, HTTPRoute, HTTPRoute, HTTPRoute, agentweaver-api :8080, agentweaver-mcp :8080, agentweaver-frontend :80](../diagrams/guide-architecture-aks-fig3.png)

<!-- Rendered from ../diagrams/src/guide-architecture-aks-fig3.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

---

## Security model

### Network security — Cilium NetworkPolicy

The cluster is provisioned with `--network-dataplane cilium` (Azure CNI Overlay + Cilium). Cilium enforces all `NetworkPolicy` resources and also exposes `CiliumNetworkPolicy` for FQDN-based egress control when needed.

The `approuting-istio` gateway class means the Application Routing add-on uses an Istio-based data plane for the **gateway only** — no Istio service mesh, sidecars, or ambient mode runs on workload pods.

### Security policies

#### Network traffic diagram

![Network traffic diagram: Gateway Pod, API Pod, MCP Pod, Frontend Pods, AgentHost sandbox pod, worker, api.github.com, Azure Key Vault, kube-dns, External Services](../diagrams/guide-architecture-aks-fig4.png)

<!-- Rendered from ../diagrams/src/guide-architecture-aks-fig4.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

#### NetworkPolicy rules

| Policy | Selector | Effect |
|--------|----------|--------|
| `default-deny-ingress` | all `app.kubernetes.io/part-of: agentweaver` pods (gateway excluded) | Denies all inbound by default |
| `allow-gateway-to-api` | `app: agentweaver-api` | Ingress on :8080 from gateway pods or `aks-istio-ingress` namespace |
| `allow-gateway-to-frontend` | `app: agentweaver-frontend` | Ingress on :8080 from gateway pods or `aks-istio-ingress` namespace |
| `allow-gateway-to-mcp` | `app: agentweaver-mcp` | Ingress on :8080 from gateway pods or `aks-istio-ingress` namespace |
| `default-deny-egress-apps` | api, mcp, frontend | Denies all egress by default |
| `allow-app-dns-egress` | api, mcp, frontend | UDP/TCP :53 to `kube-dns` |
| `allow-app-internal-egress` | api, mcp, frontend | TCP :8080 to other `app.kubernetes.io/part-of: agentweaver` pods |
| `allow-app-external-https-egress` | api, mcp only | TCP :443 to any external host |
| `sandbox-deny-ingress` | `app: agentweaver-sandbox` | Denies all ingress by default |
| `allow-worker-to-agenthost-a2a` | `app: agentweaver-sandbox` | Opens TCP :8088 only from worker/API pods for AgentHost A2A turns |
| `sandbox-egress-allowlist` | `app: agentweaver-sandbox` | DNS + TCP :443 to `140.82.112.0/20` (GitHub) |

Gateway pods are identified by `gateway.networking.k8s.io/gateway-name: agentweaver-gateway`, set automatically by the approuting-istio controller.

#### Sandbox isolation

Sandbox pods (`k8s/base/networkpolicy-sandbox.yaml` plus `k8s/base/networkpolicy-agenthost.yaml`) have a deny-by-default posture with one turn-path exception:
- **Ingress deny-all by default** — command execution still uses pod-exec through the kube-apiserver.
- **A2A ingress exception** — `allow-worker-to-agenthost-a2a` opens only TCP `8088` from worker/API pods to AgentHost pods. `POST /configure` is intentionally not protected by the turn bearer token because it delivers that token; NetworkPolicy is the guard. `POST /a2a/agent/v1/message:stream` still requires `Authorization: Bearer {per-run token}`, delivered by `/configure` and unique per run.
- **Egress allow-list** — DNS (`kube-dns`) + public HTTPS on port 443 for package registries and GitHub/Copilot/Azure APIs. Broad cluster-internal (RFC1918) egress stays denied, so ordinary sandbox pods cannot reach arbitrary workload pods. **AgentHost** pods are the one exception: `agenthost-egress-allowlist` (`k8s/base/networkpolicy-agenthost-egress.yaml`) adds narrow, identity-based (`podSelector`) egress to `agentweaver-api` and `agentweaver-mcp` on TCP `8080` so native agent tools and the operator-assistant MCP can reach those two services east-west. These must be `podSelector` rules, not CIDR/`ipBlock` rules: under Cilium an in-cluster ClusterIP resolves to the destination pod's security identity, and a CIDR allow (even `0.0.0.0/0`) matches only the "world" entity, never a cluster-managed pod identity — so a CIDR rule silently black-holes API/MCP traffic (#424).

The FQDN-based `CiliumNetworkPolicy` in `k8s/base/cilium-network-policy-sandbox.yaml` further narrows sandbox internet egress to specific hostnames: `api.github.com`, `registry.npmjs.org` (and `*.npmjs.org`), and Azure AI service domains. This policy requires `--network-dataplane cilium --enable-acns` at cluster creation and must be applied alongside `networkpolicy-sandbox.yaml`.

### Non-root containers

Both the API and Frontend containers run as UID 1000 (`runAsNonRoot: true`, `runAsUser: 1000`). Capabilities are dropped (`capabilities.drop: [ALL]`). The API pod additionally sets `allowPrivilegeEscalation: false`.

### Sandbox isolation

Agent runs execute shell commands in per-run Kata VM isolated sandbox pods
(`runtimeClassName: kata-vm-isolation`), claimed from a pre-warmed `SandboxWarmPool`
via a `SandboxClaim` (`extensions.agents.x-k8s.io/v1beta1`). This provides VM-grade
isolation. The API selects the `KubernetesSandboxExecutor` automatically when it detects
the in-cluster environment (`KUBERNETES_SERVICE_HOST` is set).
See [Deploy to AKS](/guide/deployment-aks#sandbox-setup) for setup details.

### Secrets management

Secrets are delivered from **Azure Key Vault** with **Azure Workload Identity**. API app secrets still use the Secrets Store CSI driver; AgentHost user GitHub tokens are resolved on the API side and brokered to the sandbox pod in the one-time `/configure` call (`gitHubAccessToken`), because the sandbox identity has no Key Vault access (issue #471). There are no static credentials in any manifest.

![Secrets management: Managed Identity, ServiceAccount, ServiceAccount, AKS OIDC Issuer, AKS OIDC Issuer, Azure Key Vault, SecretProviderClass, Per-user GitHub token secret, API Pod, Warm AgentHost Pod, MCP Pod](../diagrams/guide-architecture-aks-fig5.png)

<!-- Rendered from ../diagrams/src/guide-architecture-aks-fig5.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

The API's and worker's `ServiceAccount`s (`agentweaver-api`, `agentweaver-worker`) are federated to the shared, Key-Vault-privileged user-assigned `agentweaver-api-identity` through the cluster's OIDC issuer (`agentweaver-api-fedcred` and `agentweaver-worker-fedcred` respectively). The worker has its own Kubernetes RBAC identity and receives only sandbox lifecycle and legacy exec permissions; it does not inherit the API's preview-management permissions. The `agentweaver-agent-host` ServiceAccount is federated to a **separate, dedicated managed identity (`agentweaver-agenthost-identity`) that has no Key Vault role assignments** (issue #471) via its own federated credential (`agentweaver-agenthost-fedcred`). Because the sandbox runs untrusted shell/tool code, it must not be able to read Key Vault; the run owner's GitHub token is instead brokered per-run by the API in the `/configure` call.

One static `SecretProviderClass` object syncs app secrets from Key Vault into the API pod volume:

**`agentweaver-secrets`** (used by API pod, `k8s/base/secret-provider-class.yaml`):

| Key Vault secret | File in `/mnt/secrets-store/` | Used for |
|-----------------|------------------------------|----------|
| `mcp-api-key` | `mcp-api-key` | API authentication and worker loopback calls → `Auth__ApiKey` |

The MCP pod mounts no secrets; MCP auth relies only on OAuth (Agentweaver-minted JWT + transitional GitHub passthrough).

Secrets are read at pod startup via a shell wrapper in the container `command` — they are sourced from files, not injected as Kubernetes Secret refs. The CSI volume mount on `/mnt/secrets-store` is required to trigger synchronization; without it the files are never written.

Secret rotation polling is set to 2 minutes (`secrets-store.csi.k8s.io/rotation-poll-interval: "2m"`) for CSI-mounted API app secrets. GitHub capability credentials are brokered per run after platform authorization; AgentHost pods do not receive an OAuth client secret mount.

---

## Authentication

Agentweaver uses **Microsoft Entra ID** for browser authentication. There are no API keys issued to end users.

### Login flow

1. User visits the frontend and clicks **Sign in with Microsoft Entra ID**.
2. The API redirects the browser to the configured Entra application.
3. Entra returns to `https://<host>/auth/entra/callback`; the API establishes the platform session from the Entra identity and app roles.
4. Repository discovery and project creation use a distinct Repo App browser handoff with an opaque, short-lived selection code.
5. Copilot-backed work uses a distinct project-scoped Copilot App browser handoff.

For a production deployment, the deploy renderer derives both
`Auth__Entra__RedirectUri` (`https://<host>/auth/entra/callback`) and
`Auth__Entra__FrontendUrl` (`https://<host>`) from the public deployment host. Both
values are required: the API does not fall back to localhost when either is absent.
Register the public callback URI under the app's `publicClient` platform before enabling
Entra sign-in; use `npm run azure:setup-entra-app -- --redirect-uri
https://<host>/auth/entra/callback` to prepare a registration. Do not change an existing
production app registration without the identity owner's approval.

### MCP authentication

The MCP server (`agentweaver-mcp`) forwards the authenticated caller context to the API (`AGENTWEAVER_API_URL: http://agentweaver-api:8080`). Platform authorization is based on Entra identity; repository and Copilot capabilities continue through the Repo App and Copilot App handoffs. There is no static MCP bearer key.

### External dependencies

| Service | Purpose | Allowed by |
|---------|---------|-----------|
| `api.github.com` | GitHub App capability operations | `CiliumNetworkPolicy` FQDN allowlist |
| `github.com` | Repo App and Copilot App browser handoffs | `CiliumNetworkPolicy` FQDN allowlist |
| Azure Key Vault (`*.vault.azure.net`) | Secret fetch via CSI driver | HTTPS egress + workload identity |
| Azure Container Registry (`agentweaverregistry.azurecr.io`) | Image pull (kubelet, not pod) | ACR attachment on cluster |
| OpenTelemetry collector (`otel-collector.observability.svc.cluster.local:4317`) | Telemetry export (gRPC) | `CiliumNetworkPolicy` FQDN allowlist |

---
## Storage model

### PostgreSQL (primary data store)

The API uses **Azure Database for PostgreSQL Flexible Server** for all application state. The connection string is provisioned by `scripts/azure/steps/17-provision-postgres.mjs`, stored in the `agentweaver-postgres` Kubernetes Secret, and injected as environment variables at pod startup.

Both the `SqliteDb` (projects, runs, backlog, revisions) and the `MemoryDbContext` (decisions, agent memory, OAuth state, checkpoints) are wired to the same Postgres instance in production via `Database__Provider=Postgres`:

| Connection string key | Used by | Contents |
|-----------------------|---------|----------|
| `ConnectionStrings__Postgres` | `SqliteDb` (Dapper) | Projects, runs, backlog tasks, revisions, run events |
| `ConnectionStrings__MemoryDb` | `MemoryDbContext` (EF Core) | Decisions, agent memory, steering, OAuth state, checkpoints |

With Postgres as the data store, the API can run two replicas with `RollingUpdate` — no single-writer constraint.

### Workspace volume

One PersistentVolumeClaim handles all filesystem-backed state:

- `agentweaver-workspace` — Azure Files (`azurefile-csi-premium`, RWX), mounted at `/workspace`. Shared across all replicas and the worker pod.

```
PVC: agentweaver-workspace (Azure Files, RWX)
  storageClass: azurefile-csi-premium
  mountPath: /workspace
  │
  ├── .home/                   (shared HOME dir — app/runtime state; no GitHub token mirror)
  ├── worktrees/               (git worktrees per run)
  └── <project workspaces>     (project working directories)
```

### EF Core migrations

On startup, the API and worker run schema migrations via their **init containers** (`migrate-memory-db`). They execute the EF bundle as `/app/efbundle --verbose -- --postgres-migrations`, which selects the Postgres migrations assembly and reads the production connection string from the injected configuration. This runs before the main container starts, ensuring the schema is always current before the application accepts traffic.

The init container uses the same image as the API (`agentweaver-api:${IMAGE_TAG}`) and reads `ConnectionStrings__MemoryDb` + `ConnectionStrings__Postgres` from the `agentweaver-postgres` Secret. No connection string is embedded in the image or manifest. Local design-time commands remain SQLite by default; use `--postgres-migrations` only when a Postgres connection string is supplied through configuration, environment variables, or Development user secrets.

### Ephemeral storage for testing

For throwaway testing without Postgres, set `Database__Provider=Sqlite` in the API environment and replace the workspace `persistentVolumeClaim` volume with `emptyDir`:

```yaml
volumes:
  - name: workspace
    emptyDir: {}
```

Data will be lost on pod restart, but the stack is fully functional for validation. SQLite mode enforces `replicas: 1` + `strategy: Recreate` to prevent write contention.
