---
title: Deploy to AKS
---

# Deploy to AKS

This guide covers deploying Agentweaver to Azure Kubernetes Service (AKS).

## One-liner deploy (recommended)

Run the full AKS provisioning — ACR, cluster, identity, Postgres, image builds, mTLS certs, and deployment — with a single command:

```bash
# macOS / Linux / WSL2
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks
```
```powershell
# Windows PowerShell (delegates to install.sh via WSL2)
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1'))) -Aks
```

**Optional flags:**

| Flag (bash) | Flag (PowerShell) | Effect |
|---|---|---|
| `--skip-postgres` | `-SkipPostgres` | Skip Postgres provisioning (step 17) if it already exists |
| `--skip-oauth-key` | `-SkipOauthKey` | Skip OAuth signing key provisioning (step 16) if it already exists |
| `--image-tag <tag>` | `-ImageTag <tag>` | Use this image tag instead of the short git SHA (see [Redeploy](#redeploy--update)) |

The installer will clone the repo to `~/agentweaver` if you don't already have a local checkout, then run all provisioning steps in order.

> **Prerequisites before running:** `az login`, `kubectl`, `envsubst`, `openssl`, and the `aks-preview` Azure CLI extension. See [Prerequisites](#prerequisites) below for install links.

### Redeploy / update

Re-running the installer with `--image-tag` builds new images, pushes them, and redeploys — this is the standard update path:

```bash
# From a cloned checkout
bash install.sh --aks --image-tag <new-git-sha>
```
```powershell
.\install.ps1 -Aks -ImageTag <new-git-sha>
```

Or via one-liner (no local checkout required):

```bash
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks --image-tag <new-git-sha>
```

> **Never use `:latest`.** Image tags are immutable per build. The default is `git rev-parse --short HEAD`. Always pin to a specific SHA for reproducible, rollback-safe deployments.

---

<details>
<summary><strong>Advanced: manual step-by-step installation</strong></summary>

The one-liner above calls these 11 scripts internally, in order. Run them manually if you need to customise or resume a partial install:

1. `scripts/aks/00-variables.sh` — shared environment variables (source, don't run directly)
2. `scripts/aks/10-create-cluster.sh` — ACR + AKS cluster
3. `scripts/aks/15-setup-identity.sh` — managed identity + Key Vault secrets
4. `scripts/aks/16-provision-oauth-signing-key.sh` — OAuth signing key
5. `scripts/aks/17-provision-postgres.sh` — Azure Database for PostgreSQL
6. `scripts/aks/20-build-push-images.sh` — build and push container images (no local Docker required)
7. `scripts/aks/gen-a2a-mtls-certs.sh` — A2A mTLS certificates *(must run before step 8)*
8. `scripts/aks/30-deploy.sh` — apply all Kubernetes manifests
9. `scripts/aks/40-verify.sh` — verify deployment health
10. `scripts/aks/c3-scale-zero.sh` — scale to zero (optional cost-saving)
11. `scripts/aks/c4-flip-postgres.sh` — flip to external Postgres (optional)

The detailed walkthrough for each step follows below.

</details>

---

## Prerequisites

### Tools

| Tool | Minimum version | Install |
|------|----------------|---------|
| Azure CLI | 2.80.0+ | [Install guide](https://docs.microsoft.com/cli/azure/install-azure-cli) |
| `aks-preview` extension | latest | `az extension add --upgrade --name aks-preview` |
| kubectl | 1.29+ | `az aks install-cli` |
| `envsubst` | any | `apt install gettext` / `brew install gettext` |

Log in before running any script:

```bash
az login
az account set --subscription <YOUR_SUBSCRIPTION_ID>
```

### GitHub OAuth App

Agentweaver uses GitHub OAuth for user authentication. Create a GitHub OAuth App before provisioning:

1. Go to **GitHub → Settings → Developer settings → OAuth Apps → New OAuth App**
2. Set **Authorization callback URL** to `https://<your-host>/auth/github/callback`  
   (you can update this later once the managed domain is assigned)
3. Note the **Client ID** and generate a **Client secret**

### Required secret values

Gather these before running `scripts/aks/15-setup-identity.sh`:

| Variable | Description |
|----------|-------------|
| `MCP_API_KEY` | Internal API loopback key for the API's Scribe/coordinator self-calls (`Auth__ApiKey`) |
| `GITHUB_CLIENT_ID` | GitHub OAuth App client ID |
| `GITHUB_CLIENT_SECRET` | GitHub OAuth App client secret |

---

## Step 1 — Set variables

All scripts source `scripts/aks/00-variables.sh`, which exports shared environment variables. Override defaults before sourcing:

```bash
export RESOURCE_GROUP=agentweaver-rg
export CLUSTER_NAME=agentweaver-aks
export ACR_NAME=agentweaverregistry   # globally unique, alphanumeric only
export LOCATION=westus2
export KEYVAULT_NAME=agentweaver-kv   # globally unique

# IMAGE_TAG defaults to the short git SHA (recommended)
# export IMAGE_TAG=$(git rev-parse --short HEAD)

source scripts/aks/00-variables.sh
```

> **Image tagging**: `IMAGE_TAG` defaults to `git rev-parse --short HEAD` (the short commit SHA). Always use a commit SHA — never `:latest`. Every deploy script reads this variable to tag and reference the exact images in use.

---

## Step 2 — Create the cluster and ACR

```bash
bash scripts/aks/10-create-cluster.sh
```

This script provisions:

- **Resource group** in `$LOCATION`
- **Azure Container Registry** (`$ACR_NAME`) with admin auth disabled
- **AKS cluster** (`$CLUSTER_NAME`) with:

| Feature | Flag | Purpose |
|---------|------|---------|
| App Routing (Istio variant) | `--enable-app-routing-istio` | `approuting-istio` GatewayClass + managed LoadBalancer |
| Gateway API | `--enable-gateway-api` | Required for `HTTPRoute` resources |
| Managed default domain | `--enable-default-domain` | Provisions a `*.azureaksapps.io` domain + TLS cert |
| Azure CNI Overlay | `--network-plugin azure --network-plugin-mode overlay` | Pod networking |
| Cilium dataplane | `--network-dataplane cilium` | `NetworkPolicy` enforcement |
| ACNS | `--enable-acns` | `CiliumNetworkPolicy` FQDN-based egress + network observability |
| AzureLinux nodes | `--os-sku AzureLinux` | Hardened node OS (all pools) |
| System pool taint | `--node-taints CriticalAddonsOnly=true:NoSchedule` | Restricts nodepool1 to critical-addon pods; app workloads use `apppool` |
| Cluster Autoscaler (system pool) | `--enable-cluster-autoscaler --min-count 1 --max-count 3` | Scales system node pool automatically (1–3 nodes) |
| Key Vault CSI driver | `--enable-addons azure-keyvault-secrets-provider` | Mounts Key Vault secrets as volumes |
| Workload Identity | `--enable-oidc-issuer --enable-workload-identity` | Federated credentials for pods |
| ACR pull-through | `--attach-acr $ACR_ID` | No `imagePullSecret` required |

After cluster creation the script adds two **user node pools** via `az aks nodepool add`:

| Pool | Mode | workloadRuntime | Autoscaler | Taint | Label | Receives |
|------|------|-----------------|------------|-------|-------|---------|
| `nodepool1` | System | *(standard)* | 1–3 nodes | `CriticalAddonsOnly=true:NoSchedule` | — | kube-system / critical addons only |
| `apppool` | User | *(standard)* | 1–5 nodes | *(none)* | — | api, worker, mcp, frontend, jobs |
| `katapool` | User | `KataVmIsolation` | 1–5 nodes | `sandbox=kata:NoSchedule` | `agentweaver.io/kata=true` | Sandbox / AgentHost pods |

> **Why a dedicated app pool?** `CriticalAddonsOnly=true:NoSchedule` on the system pool is the AKS-recommended way to reserve it for cluster-critical components. No tolerations are needed in any application deployment YAML — app workloads schedule onto `apppool` by default, which has no taint. Sandbox and AgentHost pods land on `katapool` via their existing `SandboxTemplate` toleration (`sandbox=kata:NoSchedule`) and preferred `nodeAffinity` (`agentweaver.io/kata=true`).

> **NAP vs cluster-autoscaler**: `--node-provisioning-mode Auto` (Node Auto Provisioning) is **not** used because NAP and cluster-autoscaler are mutually exclusive. Kata VM isolation requires `--workload-runtime KataVmIsolation` on a fixed user pool with `--enable-cluster-autoscaler`.

After cluster creation the script installs the **agent-sandbox** CRD controller:

```bash
kubectl apply -f https://github.com/kubernetes-sigs/agent-sandbox/releases/download/v0.4.6/release.yaml
```

This provides the `SandboxClaim`, `SandboxTemplate`, and `SandboxWarmPool` CRDs used by the sandbox executor.

> **Feature registration**: Some flags are in preview. If `az aks create` fails with "feature not registered":
> ```bash
> az feature register --namespace Microsoft.ContainerService --name AKSAppRoutingGatewayAPI
> az feature register --namespace Microsoft.ContainerService --name AKS-KataVMIsolation
> az provider register --namespace Microsoft.ContainerService
> # Wait until Registered:
> az feature show --namespace Microsoft.ContainerService --name AKSAppRoutingGatewayAPI \
>   --query properties.state -o tsv
> ```

---

## Step 3 — Set up identity and secrets

```bash
export TENANT_ID=$(az account show --query tenantId -o tsv)
export MCP_API_KEY=<generated-key>
export GITHUB_CLIENT_ID=<from-github-oauth-app>
export GITHUB_CLIENT_SECRET=<from-github-oauth-app>

bash scripts/aks/15-setup-identity.sh
```

The script:

1. Creates a **user-assigned managed identity** (`agentweaver-api-identity`)
2. Creates an **Azure Key Vault** (`$KEYVAULT_NAME`) with RBAC authorization enabled
3. Stores three secrets in Key Vault:
   - `mcp-api-key` — internal API loopback key for Scribe/coordinator self-calls
   - `github-client-id` — GitHub OAuth App client ID
   - `github-client-secret` — GitHub OAuth App client secret
4. Grants the managed identity **Key Vault Secrets User** on the vault
5. Enables OIDC issuer + workload identity on the cluster (if not already enabled)
6. Creates a **federated credential** linking `serviceaccount/agentweaver-api` in namespace `agentweaver` to the managed identity

At completion, export the identity client ID for use in Step 5:

```bash
export IDENTITY_CLIENT_ID=$(az identity show \
  --name agentweaver-api-identity \
  --resource-group "${RESOURCE_GROUP}" \
  --query clientId -o tsv)
```

### How secrets flow from Key Vault to pods

```
Azure Key Vault
  └── github-client-id        ─┐
  └── github-client-secret     ├─ SecretProviderClass: agentweaver-secrets
  └── mcp-api-key             ─┘   (k8s/secret-provider-class.yaml)
                                       │
                                       │  CSI driver fetches via pod workload identity
                                       ▼
                               Pod volume: /mnt/secrets-store/
                                 github-client-id       (file)
                                 github-client-secret   (file)
                                 mcp-api-key            (file)
                                       │
                                       │  API startup script reads files:
                                       ▼
                               env vars injected at runtime (not in YAML):
                                 GitHub__ClientId
                                 GitHub__ClientSecret
                                 Auth__ApiKey

The MCP pod mounts no secrets; its auth relies only on the OAuth paths.
```

The CSI volume mount is what triggers `SecretProviderClass` synchronization — without the volume attached, secrets are never fetched.

---

## Step 4 — Build and push container images

```bash
bash scripts/aks/20-build-push-images.sh
```

Builds four images via `az acr build` (no local Docker daemon required). The build runs remotely in ACR:

| Image | Dockerfile | Build context |
|-------|-----------|---------------|
| `agentweaver-api:${IMAGE_TAG}` | `apps/Agentweaver.Api/Dockerfile` | repo root |
| `agentweaver-frontend:${IMAGE_TAG}` | `apps/web/Dockerfile` | repo root |
| `agentweaver-mcp:${IMAGE_TAG}` | `apps/Agentweaver.Mcp/Dockerfile` | repo root |
| `agentweaver-sandbox:${IMAGE_TAG}` | `apps/agentweaver-sandbox/Dockerfile` | `apps/agentweaver-sandbox/` |

The API, frontend, and MCP images use the repo root as the build context because their Dockerfiles reference multiple project subdirectories. The sandbox image is self-contained.

Example output after a successful build:
```
agentweaverregistry.azurecr.io/agentweaver-api:abc1234
agentweaverregistry.azurecr.io/agentweaver-frontend:abc1234
agentweaverregistry.azurecr.io/agentweaver-mcp:abc1234
agentweaverregistry.azurecr.io/agentweaver-sandbox:abc1234
```

---

## Step 5 — Deploy

```bash
IDENTITY_CLIENT_ID=<from-step-3> \
KEYVAULT_NAME=agentweaver-kv \
TENANT_ID=$(az account show --query tenantId -o tsv) \
bash scripts/aks/30-deploy.sh
```

The script aborts if `IDENTITY_CLIENT_ID`, `KEYVAULT_NAME`, or `TENANT_ID` are unset.

### What `30-deploy.sh` does

1. Applies `k8s/namespace.yaml` (creates the `agentweaver` namespace)
2. Creates a `DefaultDomainCertificate` resource named `cert` and waits until it becomes `Available`
3. Derives the managed hostname: `agentweaver.<wildcard-domain>` (e.g. `agentweaver.abc123.westus2.aksapp.io`)
4. Renders all `k8s/*.yaml` manifests using `envsubst`, substituting:
   - `${HOST}` — managed hostname
   - `${ACR_LOGIN_SERVER}` — ACR login server
   - `${IMAGE_TAG}` — commit SHA
   - `${IDENTITY_CLIENT_ID}` — managed identity client ID
   - `${KEYVAULT_NAME}` — Key Vault name
   - `${TENANT_ID}` — Azure tenant ID
5. Applies resources in this order:
   - `serviceaccount-api.yaml` → `secret-provider-class.yaml` → `rbac-api.yaml` → `quota.yaml` → `pvc-data.yaml` → `pvc-workspace.yaml`
   - Network policies and Cilium egress policies
   - Services, Gateway, HTTPRoutes, backup CronJob
   - Sandbox template and warm pool (skipped if CRDs not installed)
6. Waits for Gateway `Programmed=True`
7. Applies deployments (api, frontend, mcp)
8. Waits for all three rollouts to complete

At completion:
```
Frontend URL: https://agentweaver.<domain>/
API URL:      https://agentweaver.<domain>/api/
MCP URL:      https://agentweaver.<domain>/mcp/
```

---

## Kubernetes manifests overview

All manifests live in `k8s/`. The deploy script applies them in dependency order.

### Core infrastructure

| File | Kind | Purpose |
|------|------|---------|
| `namespace.yaml` | Namespace | `agentweaver` namespace with `app.kubernetes.io/part-of: agentweaver` label |
| `serviceaccount-api.yaml` | ServiceAccount | `agentweaver-api` SA, annotated with managed identity client ID for workload identity |
| `rbac-api.yaml` | Role + RoleBinding | Grants `agentweaver-api` SA permission to create/delete `SandboxClaim`, get/create pods, create `pods/exec` |
| `quota.yaml` | ResourceQuota + LimitRange + PodDisruptionBudget | Namespace quota (25 pods, 8 CPU req, 16Gi mem req, 20 sandbox claims); default container limits; PDBs for api/mcp/frontend |

### Secrets

| File | Kind | Purpose |
|------|------|---------|
| `secret-provider-class.yaml` | SecretProviderClass | Fetches `mcp-api-key`, `github-client-id`, `github-client-secret` from Key Vault into the API pod volume |

### Storage

| File | Kind | Purpose |
|------|------|---------|
| `pvc-data.yaml` | PersistentVolumeClaim | `agentweaver-data` — 10 Gi Azure Disk Premium (RWO), mounted at `/data` for SQLite databases |
| `pvc-workspace.yaml` | PersistentVolumeClaim | `agentweaver-workspace` — 50 Gi Azure Files Premium (RWX), mounted at `/workspace` for agent worktrees |

### Network policies

| File | Kind | Purpose |
|------|------|---------|
| `networkpolicy-default-deny.yaml` | NetworkPolicy (×6) | Default-deny ingress/egress; allows gateway→api, gateway→frontend, DNS, internal pod traffic, external HTTPS for api+mcp |
| `networkpolicy-mcp.yaml` | NetworkPolicy | Allows gateway ingress to MCP pod on port 8080 |
| `networkpolicy-sandbox.yaml` | NetworkPolicy (×2) | Deny-all ingress to sandbox pods; egress allow-list: DNS + HTTPS to GitHub IP range |
| `cilium-network-policy-sandbox.yaml` | CiliumNetworkPolicy | FQDN-based egress for sandbox pods: `api.github.com`, `registry.npmjs.org`, Azure AI services |
| `serviceentry-telemetry.yaml` | CiliumNetworkPolicy | FQDN-based egress for app pods: GitHub, Azure AI services, OpenTelemetry collector |

### Networking (Gateway API)

| File | Kind | Purpose |
|------|------|---------|
| `gateway.yaml` | Gateway | `agentweaver-gateway` — HTTPS listener on port 443, TLS terminate with managed cert, `gatewayClassName: approuting-istio` |
| `httproute-api.yaml` | HTTPRoute | Routes `PathPrefix: /api` and `/auth` → `agentweaver-api:8080` |
| `httproute-frontend.yaml` | HTTPRoute | Routes `PathPrefix: /` (catch-all) → `agentweaver-frontend:80` |
| `mcp-httproute.yaml` | HTTPRoute | Routes `PathPrefix: /mcp` → `agentweaver-mcp:8080`; rewrites `/mcp/health` → `/healthz` |

### Workloads

| File | Kind | Purpose |
|------|------|---------|
| `api-deployment.yaml` | Deployment | API pod — 1 replica, `Recreate` strategy (SQLite single-writer), init container runs EF migrations |
| `api-service.yaml` | Service | `agentweaver-api` ClusterIP :8080 |
| `frontend-deployment.yaml` | Deployment | Frontend pods — 2 replicas, serves React SPA |
| `frontend-service.yaml` | Service | `agentweaver-frontend` ClusterIP :80 → :8080 |
| `mcp-deployment.yaml` | Deployment | MCP server — 1 replica, forwards caller Bearer token to API |
| `mcp-service.yaml` | Service | `agentweaver-mcp` ClusterIP :8080 |

### Sandbox

| File | Kind | Purpose |
|------|------|---------|
| `sandbox-template.yaml` | SandboxTemplate | Template for isolated pods — `kata-vm-isolation` runtime, non-root, read-only rootfs, workspace PVC |
| `sandbox-template-agenthost.yaml` | SandboxTemplate | AgentHost (pod-per-run) template — allows per-run env injection; image `${AGENTHOST_IMAGE_TAG}` |
| `sandbox-warmpool.yaml` | SandboxWarmPool | Keeps 3 pre-warmed generic sandbox pods ready (`agentweaver-sandbox`) |
| `sandbox-warmpool-agenthost.yaml` | SandboxWarmPool | `agentweaver-agent-host` pool (`replicas: 0`) — template reference for pod-per-run AgentHost claims (env injection forces a cold start, so no warm spare is kept) |
| `sandbox-claim-template.yaml` | (template) | Reference v1beta1 SandboxClaim shape — `spec.warmPoolRef.name` + `spec.lifecycle` |

### Maintenance

| File | Kind | Purpose |
|------|------|---------|
| `backup-cronjob.yaml` | CronJob | Daily SQLite backup at 03:17 UTC — runs `sqlite3 .backup`, retains 14 days |

---

## TLS and HTTPS

TLS is handled by the AKS **App Routing add-on** with a managed **DefaultDomainCertificate**.

### How it works

1. The deploy script creates a `DefaultDomainCertificate` resource named `cert` in the `agentweaver` namespace:

   ```yaml
   apiVersion: approuting.kubernetes.azure.com/v1alpha1
   kind: DefaultDomainCertificate
   metadata:
     name: cert
     namespace: agentweaver
   spec:
     target:
       secret: agentweaver-tls
   ```

2. The App Routing controller provisions a wildcard certificate for the cluster's managed domain (e.g. `*.abc123.westus2.aksapp.io`) and stores it in `Secret/agentweaver-tls`.

3. `k8s/gateway.yaml` references the secret in its TLS listener:

   ```yaml
   tls:
     mode: Terminate
     certificateRefs:
       - kind: Secret
         name: agentweaver-tls
   ```

4. The gateway terminates TLS and forwards plain HTTP to backend services inside the cluster.

### Check certificate status

```bash
kubectl get defaultdomaincertificate cert -n agentweaver -o yaml
# status.conditions should include Available=True
# status.domain contains the wildcard domain
```

### Update the GitHub OAuth callback URL

Once the managed domain is assigned, update the GitHub OAuth App's callback URL:

```bash
HOST=$(kubectl get defaultdomaincertificate cert -n agentweaver \
  -o jsonpath='{.status.domain}' | sed 's/^\*\.//')
echo "Callback URL: https://agentweaver.${HOST}/auth/github/callback"
```

---

## Network policies

The `agentweaver` namespace enforces **default-deny** with explicit allow rules. All policies are in `k8s/networkpolicy-default-deny.yaml` (plus sandbox-specific files).

### Ingress rules

| Policy | Selector | Allows |
|--------|----------|--------|
| `default-deny-ingress` | all `app.kubernetes.io/part-of: agentweaver` pods (gateway excluded) | Denies all ingress by default |
| `allow-gateway-to-api` (unnamed in YAML) | `app: agentweaver-api` | Ingress on :8080 from gateway pods or `aks-istio-ingress` namespace |
| `allow-gateway-to-frontend` | `app: agentweaver-frontend` | Ingress on :8080 from gateway pods or `aks-istio-ingress` namespace |
| `allow-gateway-to-mcp` | `app: agentweaver-mcp` | Ingress on :8080 from gateway pods or `aks-istio-ingress` namespace |
| `sandbox-deny-ingress` | `app: agentweaver-sandbox` | Denies all ingress (API accesses sandboxes via `pods/exec`, not network) |

Gateway pods are identified by `gateway.networking.k8s.io/gateway-name: agentweaver-gateway`, which the `approuting-istio` controller sets automatically.

### Egress rules

| Policy | Selector | Allows |
|--------|----------|--------|
| `default-deny-egress-apps` | api, mcp, frontend pods | Denies all egress by default |
| `allow-app-dns-egress` | api, mcp, frontend pods | UDP/TCP :53 to `kube-dns` in `kube-system` |
| `allow-app-internal-egress` | api, mcp, frontend pods | TCP :8080 to other `app.kubernetes.io/part-of: agentweaver` pods |
| `allow-app-external-https-egress` | api, mcp pods only | TCP :443 to any external host |
| `sandbox-egress-allowlist` | `app: agentweaver-sandbox` | DNS + TCP :443 to GitHub IP range `140.82.112.0/20` |

### Cilium FQDN egress (sandbox)

`k8s/cilium-network-policy-sandbox.yaml` narrows sandbox internet egress to specific hostnames:

- `api.github.com` — GitHub REST API
- `registry.npmjs.org`, `*.npmjs.org` — npm packages
- `*.services.ai.azure.com`, `*.openai.azure.com`, `*.cognitiveservices.azure.com`, `*.models.ai.azure.com` — Azure AI services

### Cilium FQDN egress (app pods)

`k8s/serviceentry-telemetry.yaml` controls app pod (api, mcp, frontend) external egress:

- `api.github.com`, `github.com`, `*.github.com` — GitHub API (auth token validation, org membership)
- Azure AI service domains
- `otel-collector.observability.svc.cluster.local:4317` — OpenTelemetry collector

---

## Sandbox setup

The API uses a Kubernetes-native sandbox executor when running in-cluster. Each agent run claims a pre-warmed pod from the `SandboxWarmPool`, executes commands via `pods/exec`, then releases the claim.

### Prerequisites

The agent-sandbox controller is installed by `scripts/aks/10-create-cluster.sh`. Verify:

```bash
kubectl api-resources --api-group=extensions.agents.x-k8s.io
# Should list: sandboxclaims, sandboxtemplates, sandboxwarmpools
```

### Sandbox pod characteristics

From `k8s/sandbox-template.yaml`:
- **Runtime**: `kata-vm-isolation` — hardware VM-grade isolation (not just container isolation)
- **Security**: non-root (UID 1000), read-only rootfs, no capabilities, seccomp RuntimeDefault
- **Volumes**: workspace PVC at `/workspace`, `emptyDir` at `/tmp`
- **Resources**: 256Mi–4Gi RAM, 250m–1000m CPU
- **Env injection**: Disallowed (sandbox cannot inherit API pod env vars)

### Verify warm pool

```bash
# Should show 3 pods in Running state
kubectl get pods -n agentweaver -l app=agentweaver-sandbox

# Check warm pool status
kubectl get sandboxwarmpool agentweaver-sandbox -n agentweaver \
  -o jsonpath='{.status}' | jq
# status.readyReplicas should equal 3
```

### Claim lifecycle

1. API creates a `SandboxClaim` (e.g. `run-a1b2c3d4e5f6g7h8`)
2. Controller binds it to a warm pod (sub-second when pool is warm)
3. API runs commands via Kubernetes `pods/exec` WebSocket
4. On completion, claim is deleted; controller terminates the pod and refills the pool

---

## Step 6 — Verify

```bash
bash scripts/aks/40-verify.sh
```

Checks performed:
- Pod running counts (api ≥1, frontend ≥1, mcp ≥1, sandbox warm pods ≥1)
- Gateway `Programmed=True` with an assigned address
- All three HTTPRoutes `Accepted=True` and `ResolvedRefs=True`
- `SecretProviderClass` objects exist and have synced
- API RBAC Role and RoleBinding present; SA can create SandboxClaims and `pods/exec`
- `kata-vm-isolation` RuntimeClass present
- `SandboxTemplate` and `SandboxWarmPool` exist
- HTTP smoke tests: `GET /`, `GET /api/health`, `GET /mcp/health` all return 200

---

## Updating a deployment

To deploy a new version:

```bash
# Rebuild images with the new commit SHA
export IMAGE_TAG=$(git rev-parse --short HEAD)
bash scripts/aks/20-build-push-images.sh

# Re-deploy (re-renders manifests with new IMAGE_TAG)
IDENTITY_CLIENT_ID=<value> \
KEYVAULT_NAME=agentweaver-kv \
TENANT_ID=$(az account show --query tenantId -o tsv) \
bash scripts/aks/30-deploy.sh
```

The API Deployment uses `strategy: Recreate` — the old pod terminates before the new one starts, ensuring the RWO Azure Disk is not multi-attached.

---

## Troubleshooting

### Gateway not Programmed

```bash
kubectl describe gateway agentweaver-gateway -n agentweaver
```

Common causes:
- `DefaultDomainCertificate` not yet `Available` — wait a few minutes; check with `kubectl get defaultdomaincertificate cert -n agentweaver`
- Preview feature not registered — see Step 2 feature registration note
- `gatewayClassName: approuting-istio` not available — confirm the cluster was created with `--enable-app-routing-istio`

### Pods in ImagePullBackOff

```bash
kubectl describe pod <pod-name> -n agentweaver | grep -A10 Events
```

Common causes:
- ACR not attached: `az aks show --name $CLUSTER_NAME --resource-group $RESOURCE_GROUP --query addonProfiles.acrProfile`
- Wrong image tag: `$IMAGE_TAG` must match what was pushed in Step 4
- Image not pushed: re-run `scripts/aks/20-build-push-images.sh`

### API pod in CrashLoopBackOff

```bash
kubectl logs -n agentweaver -l app=agentweaver-api --previous
```

Common causes:
- PVC not bound: `kubectl get pvc -n agentweaver` — wait for `STATUS=Bound`
- Secrets not synced: check CSI driver logs:  
  `kubectl logs -n kube-system -l app=secrets-store-csi-driver`
- Missing `IDENTITY_CLIENT_ID` / `KEYVAULT_NAME` / `TENANT_ID` at deploy time — the `SecretProviderClass` will have wrong values and the CSI driver will fail to authenticate
- EF migration failed: check init container logs:  
  `kubectl logs -n agentweaver <api-pod-name> -c migrate-memory-db`

### 502 / 503 from frontend or API

The NetworkPolicy may be blocking gateway traffic. Verify the gateway pods carry the expected label:

```bash
kubectl get pods -n agentweaver \
  -l gateway.networking.k8s.io/gateway-name=agentweaver-gateway \
  --show-labels
```

The `networkpolicy-default-deny.yaml` allows ingress from pods with label `gateway.networking.k8s.io/gateway-name: agentweaver-gateway`. If those pods are missing or carry a different label, the policy will silently drop traffic.

### Secrets not mounted in pod

```bash
# Check SecretProviderClassPodStatus objects
kubectl get secretproviderclasspodstatus -n agentweaver

# Check CSI driver on the node
kubectl logs -n kube-system -l app=secrets-store-csi-driver | tail -50
```

If the managed identity federation is misconfigured, the CSI driver will log 403 errors from Key Vault.

### Sandbox pods not appearing in warm pool

```bash
kubectl describe sandboxwarmpool agentweaver-sandbox -n agentweaver
kubectl get events -n agentweaver --sort-by='.lastTimestamp' | tail -20
```

Common causes:
- `kata-vm-isolation` RuntimeClass missing — re-run `scripts/aks/10-create-cluster.sh` or check `kubectl get runtimeclass`
- Agent-sandbox controller not running: `kubectl get pods -n agent-sandbox-system`
- ACR pull failure for sandbox image — check pod events on a sandbox pod

### Istio / `approuting-istio` clarification

The `approuting-istio` GatewayClass is used **for gateway routing only** — provisioning the public LoadBalancer and TLS termination. It does **not** enroll workload pods in an Istio service mesh. No sidecars, no ambient mode, no ztunnel runs on `agentweaver` workload pods.

Inter-pod security is enforced exclusively by **Cilium NetworkPolicy**. If you see unexpected traffic drops between pods, inspect Cilium resources — not Istio:

```bash
kubectl get networkpolicies -n agentweaver
kubectl get ciliumnetworkpolicies -n agentweaver
```
