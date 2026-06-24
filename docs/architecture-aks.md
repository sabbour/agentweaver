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
│  │ namespace: agentweaver   [istio.io/dataplane-mode: ambient]          │  │
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

### Istio ambient mode (meshless mTLS)

The `agentweaver` namespace is labeled `istio.io/dataplane-mode: ambient`. This enrolls all pods in the Istio ambient mesh via the node-level `ztunnel` DaemonSet, providing:

- **L4 mTLS** between all pods in the namespace — no sidecars required, zero pod overhead
- **Workload identity** derived from Kubernetes ServiceAccount SPIFFE identities
- Foundation for `AuthorizationPolicy` resources (017-US3)

The Application Routing add-on uses the `approuting-istio` gateway class, which integrates natively with the ambient mesh.

### Security policies (017-US3)

#### PeerAuthentication — `k8s/peer-authentication.yaml`

```yaml
kind: PeerAuthentication
spec:
  mtls:
    mode: STRICT
```

Instructs the `ztunnel` DaemonSet to reject any plaintext (non-mTLS) connection entering or leaving any pod in the `agentweaver` namespace. Even if a caller bypasses `NetworkPolicy`, the ztunnel node proxy will drop the packet before it reaches the workload. This is the namespace-wide mTLS enforcement layer.

#### AuthorizationPolicy — allow gateway → API / frontend

`k8s/authorization-policy-api.yaml` and `k8s/authorization-policy-frontend.yaml` each set `action: ALLOW` with a single `from.source.principals` rule:

```
cluster.local/ns/app-routing-system/sa/approuting-istio-gateway
```

This is the SPIFFE identity (derived from the ServiceAccount) of the AKS App Routing gateway pods. Only traffic whose mTLS client certificate carries that identity is permitted to reach `agentweaver-api` or `agentweaver-frontend`. All other callers are implicitly denied (ALLOW policies deny by default when no rule matches).

> **Namespace assumption:** The App Routing add-on provisions gateway pods in the `app-routing-system` namespace with ServiceAccount `approuting-istio-gateway`. If your cluster uses a different namespace or SA name, update the `principals` field accordingly.

#### AuthorizationPolicy — deny sandbox → API

`k8s/authorization-policy-deny-sandbox-to-api.yaml` sets `action: DENY` for:

```
cluster.local/ns/agentweaver/sa/agentweaver-sandbox
```

Sandbox pods (Wave 2, 017-US2) run with the `agentweaver-sandbox` ServiceAccount and are explicitly prevented from calling the API even if the ambient mesh or network policy were misconfigured. DENY policies take precedence over any ALLOW policy.

#### Verify mTLS is active

```bash
# Confirm all workloads in the namespace are enrolled in ambient (ztunnel-managed)
istioctl ztunnel-config workload -n agentweaver

# Expected output includes each pod with PROTOCOL=HBONE and STATUS=Healthy.
# HBONE (HTTP-Based Overlay Network Encapsulation) indicates ztunnel is wrapping
# all traffic in mTLS tunnels between pods.

# Check that PeerAuthentication is applied
kubectl get peerauthentication -n agentweaver

# Check AuthorizationPolicies
kubectl get authorizationpolicy -n agentweaver
```

### NetworkPolicy

Three policies are applied in `k8s/networkpolicy-default-deny.yaml`:

| Policy | Selector | Effect |
|--------|----------|--------|
| `default-deny-ingress` | all pods | Denies all inbound traffic by default |
| `allow-gateway-to-api` | `app: agentweaver-api` | Allows ingress on :8080 from gateway pods only |
| `allow-gateway-to-frontend` | `app: agentweaver-frontend` | Allows ingress on :80 from gateway pods only |

Gateway pods are identified by the label `istio.io/gateway-name: agentweaver-gateway` (set automatically by the approuting-istio controller on the data plane pods it provisions in the same namespace).

A fallback rule allows ingress from the `aks-istio-ingress` namespace to handle cluster/add-on variants that front traffic from there instead.

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
