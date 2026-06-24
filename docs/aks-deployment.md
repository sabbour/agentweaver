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
  - Azure CNI Overlay networking
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
1. Applies `k8s/namespace.yaml` (creates the `agentweaver` namespace with Istio ambient mesh label)
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

## Verifying the deployment

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

## Troubleshooting

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
- PVC `agentweaver-data` does not exist (Wave 2 / 017-US5 creates it) — for initial testing, edit `api-deployment.yaml` to use an `emptyDir` volume instead
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
