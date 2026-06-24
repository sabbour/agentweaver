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
│  │   HTTPRoute /api/*                 │  HTTPRoute /                   │  │
│  │                 │                  │                                 │  │
│  │                 ▼                  ▼                                 │  │
│  │  ┌──────────────────┐   ┌──────────────────────┐                   │  │
│  │  │ agentweaver-api  │   │ agentweaver-frontend  │                   │  │
│  │  │ Service :8080    │   │ Service :80            │                   │  │
│  │  └────────┬─────────┘   └──────────┬────────────┘                   │  │
│  │           │                        │                                 │  │
│  │           ▼                        ▼                                 │  │
│  │  ┌──────────────────┐   ┌──────────────────────┐                   │  │
│  │  │ API Pod          │   │ Frontend Pods (x2)    │                   │  │
│  │  │ .NET 10 :8080    │   │ nginx :80             │                   │  │
│  │  │ UID 1000         │   │ UID 1000              │                   │  │
│  │  │ replicas: 1      │   │ replicas: 2           │                   │  │
│  │  └────────┬─────────┘   └──────────────────────┘                   │  │
│  │           │                                                         │  │
│  │           ▼                                                         │  │
│  │  ┌──────────────────┐                                               │  │
│  │  │ PVC: agentweaver-│                                               │  │
│  │  │ data (Azure Disk │                                               │  │
│  │  │ RWO) /data       │                                               │  │
│  │  └──────────────────┘                                               │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
│                                                                             │
│  ACR: agentweaverregistry.azurecr.io                                       │
│    agentweaver-api:<tag>                                                    │
│    agentweaver-frontend:<tag>                                               │
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
              └─► HTTPRoute agentweaver-frontend-route  (PathPrefix: /)
                    └─► Service agentweaver-frontend :80
                          └─► Frontend Pod :80 (nginx)
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

HTTPRoute (agentweaver-frontend-route)
  parentRef: agentweaver-gateway
  match: PathPrefix /
  backendRef: agentweaver-frontend :80
```

Route specificity: `/api` (longer prefix) wins over `/` — no conflict.

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
| `allow-gateway-to-frontend` | `app: agentweaver-frontend` | Allows ingress on :80 from gateway pods only |

Gateway pods are identified by the label `istio.io/gateway-name: agentweaver-gateway` (set automatically by the approuting-istio controller on the data plane pods it provisions in the same namespace).

#### Sandbox isolation

Sandbox pods (`k8s/networkpolicy-sandbox.yaml`) have two policies:
- **Ingress deny-all** — the API accesses sandbox pods via `kubectl exec` through the kube-apiserver, not direct networking.
- **Egress allow-list** — DNS (kube-dns) + HTTPS/HTTP to the internet, with the cluster pod CIDR excluded so sandboxes cannot reach the API pod.

The pod CIDR exclusion (`except: 10.244.0.0/16`) is the default for Azure CNI Overlay. Verify with:
```bash
az aks show -g "${RESOURCE_GROUP}" -n "${CLUSTER_NAME}" --query networkProfile.podCidr -o tsv
```

#### FQDN-based egress (optional hardening)

With Cilium + ACNS enabled (`--network-dataplane cilium --enable-acns`), sandbox egress can be further tightened using `CiliumNetworkPolicy` with `toFQDNs` to restrict to specific domains (e.g. `github.com`, `npmjs.org`) rather than the broad `0.0.0.0/0` allow. This is documented as an open item in `k8s/networkpolicy-sandbox.yaml`.

### Non-root containers

Both the API and Frontend containers run as UID 1000 (`runAsNonRoot: true`, `runAsUser: 1000`). Capabilities are dropped (`capabilities.drop: [ALL]`). The API pod additionally sets `allowPrivilegeEscalation: false`.

### Sandbox isolation (Wave 2)

Agent runs will execute in per-run Kata VM isolated sandbox pods (`runtimeClassName: kata-vm-isolation`), providing VM-grade isolation equivalent to the localhost mxc/WSL sandbox. This is implemented in 017-US2.

### Secrets management

**Wave 1**: GitHub Copilot token is injected via a manually created Kubernetes `Secret` (`agentweaver-secrets`). The `secretKeyRef` is marked `optional: true` so the pod starts even when the secret is absent (authentication will fail at runtime, not startup).

**Wave 2 (017-US4)**: The manual secret is replaced by Azure Key Vault CSI driver + workload identity. The API's `ServiceAccount` (`agentweaver-api`) will be federated to a user-assigned managed identity via OIDC, and a `SecretProviderClass` will deliver the token from Key Vault with no static credentials in any manifest.

---

## Storage model

### SQLite on Azure Disk RWO

The API relies on two SQLite databases:
- `agentweaver.db` — main application data (runs, projects, tasks, blueprints)
- `memory.db` — EF Core managed memory/decisions store

Both are stored under the `Database:Path` configuration key (set to `/data/agentweaver.db`). The `MemoryDbContext` derives its path from the same directory, so both databases land on the same PVC.

```
PVC: agentweaver-data
  storageClass: managed-premium (Azure Disk, RWO)
  mountPath: /data
  │
  ├── agentweaver.db      (main SQLite DB, SqliteDb)
  ├── memory.db           (EF Core memory DB, MemoryDbContext)
  ├── worktrees/          (git worktrees per run)
  └── workspaces/         (project workspace directories)
```

### Single-writer guarantee

SQLite does not support concurrent writes from multiple processes. The API Deployment enforces:

- `replicas: 1` — only one pod runs at a time
- `strategy: Recreate` — the old pod is fully terminated and releases the RWO disk before the new pod starts

This prevents the `RWO` disk from being multi-attached (which Azure Disk does not support) and prevents SQLite write corruption.

### EF Core migrations

On startup, `Program.cs` runs `memoryDb.Database.MigrateAsync()` to apply any pending EF Core migrations. This is safe under the single-replica model: only one instance runs migrations at any time.

A transition guard in `Program.cs` handles databases created before migrations were introduced (pre-migration DBs that have `AgentMemory` but no `__EFMigrationsHistory`), seeding the history table before calling `MigrateAsync`.

### PVC lifecycle (Wave 2)

The PVC `agentweaver-data` is created in 017-US5. For Wave 1 testing without the PVC, replace the `persistentVolumeClaim` volume in `api-deployment.yaml` with an `emptyDir`:

```yaml
volumes:
  - name: data
    emptyDir: {}
```

Data will be lost on pod restart, but the stack is fully functional for validation.
