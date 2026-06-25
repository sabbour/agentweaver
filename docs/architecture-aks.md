# AKS Architecture

This document describes the architecture of the Agentweaver AKS deployment: its components, networking topology, security model, and storage design.

---

## Component diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Internet                                                                    │
│                                                                             │
│   Browser / API client                                                      │
│         │  HTTPS :443                                                       │
└─────────┼───────────────────────────────────────────────────────────────────┘
          │
          ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ AKS Cluster (eastus)                                                        │
│                                                                             │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │ namespace: agentweaver                                               │  │
│  │                                                                      │  │
│  │  ┌─────────────────────────────────────────────────────────┐        │  │
│  │  │ Gateway: agentweaver-gateway (approuting-istio)         │        │  │
│  │  │  - HTTPS listener :443, TLS Terminate                   │        │  │
│  │  │  - cert: DefaultDomainCertificate (managed TLS)         │        │  │
│  │  │                                                         │        │  │
│  │  │  Gateway data plane pods                                │        │  │
│  │  │  (agentweaver-gateway-approuting-istio Deployment)      │        │  │
│  │  └──────────────┬──────────────────┬───────────────────────┘        │  │
│  │                 │                  │                                 │  │
│  │   HTTPRoute /api/*      HTTPRoute /mcp/*      HTTPRoute /            │  │
│  │                 │              │                  │                  │  │
│  │                 ▼              ▼                  ▼                  │  │
│  │  ┌──────────────────┐ ┌──────────────┐ ┌──────────────────────┐    │  │
│  │  │ agentweaver-api  │ │agentweaver-  │ │ agentweaver-frontend  │    │  │
│  │  │ Service :8080    │ │mcp Svc :8080 │ │ Service :80            │    │  │
│  │  └────────┬─────────┘ └──────┬───────┘ └──────────┬────────────┘    │  │
│  │           │                  │                    │                  │  │
│  │           ▼                  ▼                    ▼                  │  │
│  │  ┌──────────────────┐ ┌──────────────┐ ┌──────────────────────┐    │  │
│  │  │ API Pod          │ │ MCP Pod      │ │ Frontend Pods (x2)    │    │  │
│  │  │ .NET 10 :8080    │ │ .NET 10 :8080│ │ ASP.NET Core :8080    │    │  │
│  │  │ UID 1000         │ │ UID 1000     │ │ UID 1000              │    │  │
│  │  │ replicas: 1      │ │ replicas: 1  │ │ replicas: 2           │    │  │
│  │  └────────┬─────────┘ └──────────────┘ └──────────────────────┘    │  │
│  │           │                                                         │  │
│  │           ├───────────────────────────┐                            │  │
│  │           ▼                           ▼                            │  │
│  │  ┌──────────────────┐  ┌──────────────────────┐                   │  │
│  │  │ PVC: agentweaver-│  │ PVC: agentweaver-    │                   │  │
│  │  │ data (Azure Disk │  │ workspace (Azure     │                   │  │
│  │  │ RWO) /data       │  │ Files RWX) /workspace│                   │  │
│  │  └──────────────────┘  └──────────────────────┘                   │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
│  ACR: agentweaverregistry.azurecr.io                                       │
│    agentweaver-api:<tag>                                                    │
│    agentweaver-frontend:<tag>                                               │
│    agentweaver-mcp:<tag>                                                    │
│    agentweaver-sandbox:<tag>                                                │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Networking flow

### Inbound request path

```
Client (HTTPS :443)
  └─► Public LoadBalancer IP (provisioned by approuting-istio for agentweaver-gateway)
        └─► Gateway agentweaver-gateway
              TLS terminated with DefaultDomainCertificate (*.azureaksapps.io)
              │
              ├─► HTTPRoute agentweaver-api-route   (PathPrefix: /api)
              │     └─► Service agentweaver-api :8080
              │           └─► API Pod :8080
              │
              ├─► HTTPRoute agentweaver-mcp-route   (PathPrefix: /mcp)
              │     └─► Service agentweaver-mcp :8080
              │           └─► MCP Pod :8080
              │
              └─► HTTPRoute agentweaver-frontend-route  (PathPrefix: /)
                    └─► Service agentweaver-frontend :80
                          └─► Frontend Pod :8080 (ASP.NET Core)
```

### Gateway API resource relationships

```
Gateway (agentweaver-gateway)
  gatewayClassName: approuting-istio          ← managed by AKS App Routing add-on
  listener https :443
    hostname: agentweaver.<managed-domain>
    tls.mode: Terminate
    certificateRefs: [Secret/cert]
    allowedRoutes.from: Same                  ← only same-namespace routes attach

HTTPRoute (agentweaver-api-route)
  parentRef: agentweaver-gateway
  match: PathPrefix /api
  backendRef: agentweaver-api :8080

HTTPRoute (agentweaver-mcp-route)
  parentRef: agentweaver-gateway
  match: PathPrefix /mcp
  backendRef: agentweaver-mcp :8080

HTTPRoute (agentweaver-frontend-route)
  parentRef: agentweaver-gateway
  match: PathPrefix /
  backendRef: agentweaver-frontend :80
```

Route specificity: `/api` and `/mcp` (longer prefixes) win over `/` — no conflict.

---

## Security model

### Network security — Cilium NetworkPolicy

The cluster is provisioned with `--network-dataplane cilium` (Azure CNI Overlay + Cilium). Cilium enforces all `NetworkPolicy` resources and also exposes `CiliumNetworkPolicy` for FQDN-based egress control when needed.

The `approuting-istio` gateway class means the Application Routing add-on uses an Istio-based data plane for the **gateway only** — no Istio service mesh, sidecars, or ambient mode runs on workload pods.

### Security policies

#### NetworkPolicy

Three policies are applied in `k8s/networkpolicy-default-deny.yaml`:

| Policy | Selector | Effect |
|--------|----------|--------|
| `default-deny-ingress` | all pods | Denies all inbound traffic by default |
| `allow-gateway-to-api` | `app: agentweaver-api` | Allows ingress on :8080 from gateway pods only |
| `allow-gateway-to-frontend` | `app: agentweaver-frontend` | Allows ingress on :8080 (the frontend pod port) from gateway pods only |

Both allow rules also include a fallback `namespaceSelector` for the managed
`aks-istio-ingress` namespace, covering add-on variants where the gateway data plane
runs outside the `agentweaver` namespace.

Gateway pods are identified by the label `istio.io/gateway-name: agentweaver-gateway` (set automatically by the approuting-istio controller on the data plane pods it provisions in the same namespace).

#### Sandbox isolation

Sandbox pods (`k8s/networkpolicy-sandbox.yaml`) have two policies:
- **Ingress deny-all** — the API accesses sandbox pods via pod-exec through the kube-apiserver, not direct networking.
- **Egress allow-list** — DNS (kube-dns) + HTTPS (port 443) to the internet, with the cluster pod CIDR excluded so sandboxes cannot reach the API pod. Plain HTTP (port 80) is not allowed.

The pod CIDR exclusion (`except: 10.244.0.0/16`) is the default for Azure CNI Overlay. Verify with:
```bash
az aks show -g "${RESOURCE_GROUP}" -n "${CLUSTER_NAME}" --query networkProfile.podCidr -o tsv
```

#### FQDN-based egress

External (internet) egress from sandbox pods is restricted to an FQDN allow-list by the companion `CiliumNetworkPolicy` in `k8s/cilium-network-policy-sandbox.yaml`, which uses `toFQDNs` rules. This policy requires the cluster to be provisioned with `--network-dataplane cilium --enable-acns`. Apply it alongside `networkpolicy-sandbox.yaml` so the broad `0.0.0.0/0` HTTPS allow is narrowed to the permitted domains (e.g. `github.com`, `npmjs.org`).

### Non-root containers

Both the API and Frontend containers run as UID 1000 (`runAsNonRoot: true`, `runAsUser: 1000`). Capabilities are dropped (`capabilities.drop: [ALL]`). The API pod additionally sets `allowPrivilegeEscalation: false`.

### Sandbox isolation

Agent runs execute shell commands in per-run Kata VM isolated sandbox pods
(`runtimeClassName: kata-vm-isolation`), claimed from a pre-warmed `SandboxWarmPool`
via a `SandboxClaim` (`extensions.agents.x-k8s.io/v1alpha1`). This provides VM-grade
isolation equivalent to the localhost mxc/WSL sandbox. The API selects the
`KubernetesSandboxExecutor` automatically when it detects the in-cluster environment.
See [aks-deployment.md](./aks-deployment.md#sandbox-setup) for setup.

### Secrets management

The GitHub Copilot token is delivered from **Azure Key Vault** via the **Secrets Store
CSI driver** and **workload identity** — there are no static credentials in any manifest.
The API's `ServiceAccount` (`agentweaver-api`) is federated to a user-assigned managed
identity through the cluster's OIDC issuer, and a `SecretProviderClass`
(`k8s/secret-provider-class.yaml`) syncs the Key Vault secret into the
`agentweaver-secrets` Kubernetes Secret. The deployment reads it via a `secretKeyRef`
into the `Providers__GitHubCopilot__GitHubToken` environment variable. The CSI volume
mount on `/mnt/secrets` is what triggers the sync. See
[aks-deployment.md](./aks-deployment.md#how-the-token-flows-from-key-vault-to-the-pod)
for the full token flow.

The MCP deployment consumes a separate, manually created `agentweaver-mcp-secrets`
Secret (created by `scripts/aks/30-deploy.sh` from the `MCP_API_KEY`, `MCP_AUTH_API_KEY`,
and `MCP_AUTH_USER` environment variables).

---

## Storage model

### SQLite on Azure Disk RWO

The API uses two PersistentVolumeClaims:
- `agentweaver-data` — Azure Disk (`managed-csi-premium`, RWO), mounted at `/data`, for the SQLite databases.
- `agentweaver-workspace` — Azure Files (`azurefile-csi-premium`, RWX), mounted at `/workspace`, for agent workspaces and per-run git worktrees.

The two SQLite databases on the data PVC are:
- `agentweaver.db` — main application data (runs, projects, tasks, blueprints)
- `memory.db` — EF Core managed memory/decisions store

Both are stored under the `Database:Path` configuration key (set to `/data/agentweaver.db`). The `MemoryDbContext` derives its path from the same directory, so both databases land on the same PVC.

```
PVC: agentweaver-data (Azure Disk, RWO)
  storageClass: managed-csi-premium
  mountPath: /data
  │
  ├── agentweaver.db      (main SQLite DB, SqliteDb)
  └── memory.db           (EF Core memory DB, MemoryDbContext)

PVC: agentweaver-workspace (Azure Files, RWX)
  storageClass: azurefile-csi-premium
  mountPath: /workspace
  │
  ├── worktrees/          (git worktrees per run)
  └── <project workspaces> (project working directories)
```

### Single-writer guarantee

SQLite does not support concurrent writes from multiple processes. The API Deployment enforces:

- `replicas: 1` — only one pod runs at a time
- `strategy: Recreate` — the old pod is fully terminated and releases the RWO disk before the new pod starts

This prevents the `RWO` disk from being multi-attached (which Azure Disk does not support) and prevents SQLite write corruption.

### EF Core migrations

On startup, `Program.cs` runs `memoryDb.Database.MigrateAsync()` to apply any pending EF Core migrations. This is safe under the single-replica model: only one instance runs migrations at any time.

A transition guard in `Program.cs` handles databases created before migrations were introduced (pre-migration DBs that have `AgentMemory` but no `__EFMigrationsHistory`), seeding the history table before calling `MigrateAsync`.

### Ephemeral storage for testing

Both PVCs (`agentweaver-data` and `agentweaver-workspace`) are applied by
`scripts/aks/30-deploy.sh` before the deployments roll out. For throwaway testing
without persistent volumes, replace the `persistentVolumeClaim` volumes in
`api-deployment.yaml` with `emptyDir`:

```yaml
volumes:
  - name: data
    emptyDir: {}
  - name: workspace
    emptyDir: {}
```

Data will be lost on pod restart, but the stack is fully functional for validation.
