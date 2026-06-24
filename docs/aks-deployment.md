# AKS Deployment Guide

This document covers how to deploy Agentweaver to Azure Kubernetes Service (AKS) using the scripts and manifests in `scripts/aks/` and `k8s/`.

---

## Prerequisites

| Tool | Minimum version | Install |
|------|----------------|---------|
| Azure CLI | 2.80.0+ | [docs.microsoft.com/cli/azure/install](https://docs.microsoft.com/cli/azure/install-azure-cli) |
| aks-preview extension | latest | `az extension add --upgrade --name aks-preview` |
| kubectl | 1.29+ | `az aks install-cli` |
| envsubst | any | `apt install gettext` / `brew install gettext` |
| curl | any | bundled on most systems |

Log in to Azure before running any script:

```bash
az login
az account set --subscription <YOUR_SUBSCRIPTION_ID>
```

---

## Step-by-step deployment

All scripts are run from the **repo root** unless noted otherwise.

### 1. Set variables

```bash
# Optional — override any default
export RESOURCE_GROUP=agentweaver-rg
export CLUSTER_NAME=agentweaver-aks
export ACR_NAME=agentweaverregistry   # must be globally unique, alphanumeric only
export LOCATION=eastus
export IMAGE_TAG=v1

source scripts/aks/00-variables.sh
```

### 2. Create cluster and ACR

```bash
bash scripts/aks/10-create-cluster.sh
```

This creates:
- A resource group in `$LOCATION`
- An Azure Container Registry
- An AKS cluster with:
  - Application Routing add-on (Istio variant, `approuting-istio` gateway class)
  - Gateway API
  - Azure CNI Overlay networking with **Cilium dataplane** (`--network-dataplane cilium`) — required for NetworkPolicy enforcement
  - **ACNS** (`--enable-acns`) — Advanced Container Networking Services; required for `CiliumNetworkPolicy` FQDN-based egress filtering and network observability
  - Kata VM isolation workload runtime (`kata-vm-isolation` RuntimeClass)
  - AzureLinux node OS
  - ACR attachment (no `imagePullSecret` required)

> **Feature registration**: Some flags require preview features. If `az aks create` fails with "feature not registered":
> ```bash
> az feature register --namespace Microsoft.ContainerService --name AKSAppRoutingGatewayAPI
> az feature register --namespace Microsoft.ContainerService --name AKS-KataVMIsolation
> az provider register --namespace Microsoft.ContainerService
> # Wait until State=Registered:
> az feature show --namespace Microsoft.ContainerService --name AKSAppRoutingGatewayAPI --query properties.state -o tsv
> ```

### 3. Build and push container images

```bash
bash scripts/aks/20-build-push-images.sh
```

Builds two images via `az acr build` (no local Docker daemon required):
- `agentweaver-api:<IMAGE_TAG>` — .NET 10 API
- `agentweaver-frontend:<IMAGE_TAG>` — React SPA served by nginx

### 4. Set up secrets (Key Vault + workload identity)

```bash
# Set your tenant ID first
export TENANT_ID=$(az account show --query tenantId -o tsv)

# Optionally set a custom Key Vault name (must be globally unique)
export KEYVAULT_NAME=agentweaver-kv

# Set your GitHub Copilot token
export GITHUB_COPILOT_TOKEN=ghp_...

bash scripts/aks/15-setup-identity.sh
```

This script:
1. Creates a **user-assigned managed identity** (`agentweaver-api-identity`)
2. Creates an **Azure Key Vault** with RBAC authorization enabled
3. Stores the GitHub Copilot token as secret `github-copilot-token` in Key Vault
4. Grants the managed identity the `Key Vault Secrets User` role on the vault
5. Enables **OIDC issuer** and **workload identity** on the AKS cluster
6. Creates a **federated credential** linking the AKS service account to the managed identity
7. Enables the **Azure Key Vault Secrets Provider** add-on (CSI driver)

At completion, the script prints the `IDENTITY_CLIENT_ID` to export before deploying:

```bash
export IDENTITY_CLIENT_ID=<printed by script>
```

#### How the token flows from Key Vault to the pod

```
Key Vault secret: github-copilot-token
        │
        │  SecretProviderClass (k8s/secret-provider-class.yaml)
        │  – CSI driver fetches the secret using the pod's workload identity token
        ▼
Kubernetes Secret: agentweaver-secrets / github-copilot-token
        │
        │  secretKeyRef in api-deployment.yaml
        ▼
Pod env var: Providers__GitHubCopilot__GitHubToken
        │
        │  ASP.NET Core configuration binding
        ▼
appsettings.json key: Providers:GitHubCopilot:GitHubToken
```

The CSI volume mount on `/mnt/secrets` is required to trigger the `SecretProviderClass` sync — without it, the Kubernetes Secret is never populated and the `secretKeyRef` will fail.

### 5. Deploy to AKS

```bash
bash scripts/aks/30-deploy.sh
```

This script:
1. Applies `k8s/namespace.yaml` (creates the `agentweaver` namespace)
2. Creates the `DefaultDomainCertificate` resource and waits for it to become Available
3. Derives the managed domain hostname from the certificate status
4. Creates the `agentweaver-api` service account
5. Renders all `k8s/*.yaml` manifests (substitutes `${HOST}`, `${ACR_LOGIN_SERVER}`, `${IMAGE_TAG}`)
6. Applies all rendered manifests
7. Waits for the Gateway to become Programmed
8. Waits for all Deployment rollouts to complete

At completion, the script prints the public URL.

### 6. Verify the deployment

```bash
bash scripts/aks/40-verify.sh
```

---

## Persistent storage

Agentweaver uses two PersistentVolumeClaims:

| PVC | Storage class | Access mode | Size | Purpose |
|-----|--------------|-------------|------|---------|
| `agentweaver-data` | `managed-csi-premium` | ReadWriteOnce | 10 Gi | SQLite databases (`agentweaver.db`, `memory.db`) |
| `agentweaver-workspace` | `azurefile-csi-premium` | ReadWriteMany | 50 Gi | Agent workspaces and git worktrees |

### Why Azure Disk for data (`managed-csi-premium`)

SQLite's WAL (write-ahead log) mode requires consistent, low-latency disk I/O and exclusive write access. Azure Disk Premium SSDs provide predictable IOPS and `ReadWriteOnce` semantics that match the single-replica deployment. A `Recreate` rollout strategy ensures the old pod fully detaches the disk before the new pod attaches it, avoiding multi-attach errors.

### Why Azure Files for workspace (`azurefile-csi-premium`)

Agent workspaces and git worktrees may be accessed by multiple sandbox pods simultaneously. Azure Files Premium supports `ReadWriteMany`, allowing concurrent read/write access across pods without coordination overhead. The premium tier provides the throughput needed for git operations on large repositories.

### Checking PVC binding

After deploying, verify both PVCs are bound before the API pod starts:

```bash
kubectl get pvc -n agentweaver
```

Expected output:
```
NAME                     STATUS   VOLUME   CAPACITY   ACCESS MODES   STORAGECLASS             AGE
agentweaver-data         Bound    ...      10Gi       RWO            managed-csi-premium      ...
agentweaver-workspace    Bound    ...      50Gi       RWX            azurefile-csi-premium    ...
```

If a PVC stays in `Pending`, check events:

```bash
kubectl describe pvc agentweaver-data -n agentweaver
kubectl describe pvc agentweaver-workspace -n agentweaver
```

### Expanding storage

Azure Disk and Azure Files both support online volume expansion. To expand:

1. Edit the PVC manifest to increase `storage`:
   ```bash
   kubectl edit pvc agentweaver-data -n agentweaver
   # Change storage: 10Gi -> 20Gi
   ```
2. The CSI driver will resize the underlying disk automatically; no pod restart is required for Azure Files. For Azure Disk, the filesystem is expanded online on the next I/O operation.

---



### Check pod status

```bash
kubectl get pods -n agentweaver
```

All pods should be in `Running` state. If not:

```bash
kubectl describe pod <pod-name> -n agentweaver
kubectl logs <pod-name> -n agentweaver
```

### Check Gateway and HTTPRoutes

```bash
kubectl get gateway,httproute -n agentweaver -o wide
```

Expected:
- Gateway `agentweaver-gateway`: `PROGRAMMED=True`, address assigned
- HTTPRoute `agentweaver-api-route`: `ACCEPTED=True`, `RESOLVEDREFS=True`
- HTTPRoute `agentweaver-frontend-route`: `ACCEPTED=True`, `RESOLVEDREFS=True`

### Check the managed TLS certificate

```bash
kubectl get defaultdomaincertificate cert -n agentweaver -o yaml
```

The `.status.conditions` should include `Available=True`. The `.status.domain` field contains the wildcard domain used as the gateway hostname.

### Test the endpoints

```bash
HOST=$(kubectl get defaultdomaincertificate cert -n agentweaver \
  -o jsonpath='{.status.domain}' | sed 's/^\*\.//')
HOST="agentweaver.${HOST}"

# Frontend
curl -I "https://${HOST}/"

# API health
curl "https://${HOST}/api/diagnostics/health"
```

---

## Sandbox setup

Agentweaver uses a Kubernetes-native sandbox (`KubernetesSandboxExecutor`) when deployed
to AKS. Each agent run claims a pre-warmed pod from a `SandboxWarmPool` via a
`SandboxClaim` CRD, executes the shell command via pod-exec, then releases the claim.

### Prerequisites

The `agent-sandbox` extensions controller must be installed on the cluster:
```bash
kubectl apply -f https://github.com/Azure/agent-sandbox/releases/latest/download/install.yaml
```

### Apply sandbox CRDs and NetworkPolicy

```bash
# Apply the sandbox template (Kata VM, non-root, read-only rootfs)
envsubst < k8s/sandbox-template.yaml | kubectl apply -f -

# Apply the warm pool (3 pre-warmed pods)
kubectl apply -f k8s/sandbox-warmpool.yaml

# Apply the NetworkPolicy (default-deny ingress + egress allow-list)
kubectl apply -f k8s/networkpolicy-sandbox.yaml
```

> **Note**: `sandbox-template.yaml` requires `$ACR_LOGIN_SERVER` and `$IMAGE_TAG`
> to be set before applying (the same variables used in `scripts/aks/30-deploy.sh`).

### Verify warm pods are running

```bash
# List warm pods — should show 3 pods in Running state
kubectl get pods -n agentweaver -l app=agentweaver-sandbox

# Check warm pool status
kubectl get sandboxwarmpool agentweaver-sandbox -n agentweaver -o yaml

# Expected: status.readyReplicas == 3
```

### Verify sandbox executor selection

When the API pod starts inside the cluster, the log should contain:
```
KubernetesSandboxExecutor selected (KUBERNETES_SERVICE_HOST detected)
```

Check with:
```bash
kubectl logs -n agentweaver -l app=agentweaver-api | grep -i sandbox
```

### Claim lifecycle

1. `KubernetesSandboxExecutor` creates a `SandboxClaim` named `run-<16hex>`.
2. The controller binds the claim to a warm pod (sub-second when pool is warm).
3. The executor runs the command via Kubernetes pod-exec WebSocket API.
4. On completion, the claim is deleted; the controller terminates the pod and refills
   the pool.

### Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Sandbox:Kubernetes:Namespace` | `agentweaver` | Namespace for claims and pods |
| `Sandbox:Kubernetes:TemplateRef` | `agentweaver-sandbox` | SandboxTemplate to claim from |
| `Sandbox:Kubernetes:TimeoutSeconds` | `600` | Per-sandbox session timeout |

Override in `appsettings.json` or via environment variables (e.g.,
`Sandbox__Kubernetes__TimeoutSeconds=300`).


### Gateway not Programmed

```bash
kubectl describe gateway agentweaver-gateway -n agentweaver
```

Common causes:
- DefaultDomainCertificate not yet Available — wait a few minutes after first apply
- aks-preview extension not installed or Gateway API feature not registered (see Step 2 note)
- `gatewayClassName: approuting-istio` not available — confirm `--enable-app-routing-addon-istio` was set at cluster creation

### Pods in ImagePullBackOff

```bash
kubectl describe pod <pod-name> -n agentweaver | grep -A5 Events
```

Common causes:
- ACR not attached: verify `az aks show --name $CLUSTER_NAME --resource-group $RESOURCE_GROUP --query addonProfiles`
- Wrong image tag: confirm `IMAGE_TAG` matches the tag pushed in step 3
- Image not pushed: re-run `scripts/aks/20-build-push-images.sh`

### API pod CrashLoopBackOff

```bash
kubectl logs -n agentweaver -l app=agentweaver-api --previous
```

Common causes:
- PVC `agentweaver-data` not yet bound — run `kubectl get pvc -n agentweaver` and wait for `STATUS=Bound`
- GitHub Copilot token not synced — confirm `15-setup-identity.sh` completed and `IDENTITY_CLIENT_ID` / `KEYVAULT_NAME` / `TENANT_ID` were set when deploying; check CSI driver pod logs: `kubectl logs -n kube-system -l app=secrets-store-csi-driver`

### NetworkPolicy blocking traffic

If the frontend or API returns 502/503, the NetworkPolicy may be blocking gateway traffic. Verify the gateway data plane pods carry the expected label:

```bash
kubectl get pods -n agentweaver -l istio.io/gateway-name=agentweaver-gateway --show-labels
```

The `networkpolicy-default-deny.yaml` allows ingress from pods with `istio.io/gateway-name: agentweaver-gateway`. If the gateway pods carry a different label, update policy `allow-gateway-to-api` and `allow-gateway-to-frontend` accordingly.

### Istio ambient mesh not enrolling pods

```bash
kubectl get pods -n agentweaver -o jsonpath='{range .items[*]}{.metadata.name}: {.metadata.annotations.ambient\.istio\.io/redirection}{"\n"}{end}'
```

If pods are not enrolled, confirm the namespace carries the label:

```bash
kubectl get namespace agentweaver --show-labels
# Expected: istio.io/dataplane-mode=ambient
```

If missing, re-apply `k8s/namespace.yaml` and restart pods:

```bash
kubectl apply -f k8s/namespace.yaml
kubectl rollout restart deployment -n agentweaver
```
