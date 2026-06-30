---
title: Deploy to AKS
---

# Deploy to AKS

This is the operator path for a fresh Agentweaver AKS deployment. The scripts under `scripts/aks/` are the source of truth; this page gives the required prerequisites, the exact order, and the checks to run.

## Prerequisites

Install and log in:

```bash
az login
az account set --subscription <subscription-id>
az extension add --upgrade --name aks-preview
az aks install-cli
```

Required local tools:

| Tool | Why |
|---|---|
| Azure CLI | resource provisioning |
| `kubectl` | cluster apply/verify |
| `envsubst` | manifest rendering |
| `openssl` | OAuth/A2A key material |
| `git` | default image tag = short commit SHA |

Create a GitHub OAuth App, then export:

```bash
export GITHUB_CLIENT_ID=<oauth-app-client-id>
export GITHUB_CLIENT_SECRET=<oauth-app-client-secret>
```

## One-command install

```bash
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks
```

Windows PowerShell delegates to `install.sh` through WSL2:

```powershell
$env:GITHUB_CLIENT_ID = '<oauth-app-client-id>'
$env:GITHUB_CLIENT_SECRET = '<oauth-app-client-secret>'
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1'))) -Aks
```

Useful flags:

| Bash | PowerShell | Use when |
|---|---|---|
| `--image-tag <tag>` | `-ImageTag <tag>` | pin/redeploy a specific immutable image tag |
| `--skip-postgres` | `-SkipPostgres` | PostgreSQL already exists and `agentweaver-postgres` secret is valid |
| `--skip-oauth-key` | `-SkipOauthKey` | Key Vault already has `mcp-oauth-signing-key` |

Never use `latest`. By default, `IMAGE_TAG` is `git rev-parse --short HEAD`; `AGENTHOST_IMAGE_TAG` defaults to the same value.

## Manual order

Run from repo root when resuming or debugging:

```bash
source scripts/aks/00-variables.sh
bash scripts/aks/10-create-cluster.sh
bash scripts/aks/15-setup-identity.sh
bash scripts/aks/16-provision-oauth-signing-key.sh
bash scripts/aks/17-provision-postgres.sh
bash scripts/aks/20-build-push-images.sh
bash scripts/aks/gen-a2a-mtls-certs.sh
bash scripts/aks/30-deploy.sh
bash scripts/aks/40-verify.sh
```

`install.sh --aks` runs the same sequence and also carries `TENANT_ID` and `IDENTITY_CLIENT_ID` between steps.

## What the scripts deploy

- AKS cluster with `apppool` for app workloads and `katapool` for Kata-isolated AgentHost pods.
- Azure Container Registry images:
  - `agentweaver-api:${IMAGE_TAG}`
  - `agentweaver-frontend:${IMAGE_TAG}`
  - `agentweaver-mcp:${IMAGE_TAG}`
  - `agentweaver-agent-host:${AGENTHOST_IMAGE_TAG}`
- Azure Key Vault secrets for GitHub OAuth and MCP OAuth signing.
- Azure Database for PostgreSQL Flexible Server using FQDN `<server>.postgres.database.azure.com`; private connectivity comes from the VNet-linked `privatelink.postgres.database.azure.com` zone.
- Agent-sandbox CRDs/controller plus one AgentHost `SandboxTemplate` and one `SandboxWarmPool`, both named `agentweaver-agent-host`.

`20-build-push-images.sh` is redeploy-efficient: with a known previous/current tag it rebuilds changed images in parallel and retags unchanged images with `az acr import`.

## Deploy-order invariants

`30-deploy.sh` applies manifests in dependency order. The important invariants are:

1. `storageclass-workspace.yaml` before `pvc-workspace.yaml`.
2. `sandbox-template-agenthost.yaml` before `sandbox-warmpool-agenthost.yaml`.
3. Services/gateways/routes before deployments.
4. Deployments include API, frontend, MCP, worker, and worker HPA.

## Verify

```bash
bash scripts/aks/40-verify.sh
```

The verifier checks pods, main and preview gateways, routes, SecretProviderClass sync, API sandbox RBAC, Kata runtime, AgentHost template/warm pool, workspace storage, and HTTP health.

Useful follow-up commands:

```bash
kubectl get pods,gateway,httproute,pvc -n agentweaver
kubectl get sandboxtemplate,sandboxwarmpool -n agentweaver
kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver
```

## Redeploy

```bash
export IMAGE_TAG=$(git rev-parse --short HEAD)
bash scripts/aks/20-build-push-images.sh
bash scripts/aks/30-deploy.sh
bash scripts/aks/40-verify.sh
```

Or in one command:

```bash
bash install.sh --aks --image-tag "$(git rev-parse --short HEAD)"
```

## Common failures

| Symptom | Check |
|---|---|
| Gateway not programmed | `kubectl describe gateway agentweaver-gateway -n agentweaver` |
| ImagePullBackOff | confirm ACR attach and image tag pushed by `20-build-push-images.sh` |
| API/MCP auth failures | confirm Key Vault has `github-client-id`, `github-client-secret`, `mcp-oauth-signing-key` |
| AgentHost pods not ready | `kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` and check `kata-vm-isolation` runtime |
| Postgres connection failure | verify `agentweaver-postgres` secret and private DNS for `<server>.postgres.database.azure.com` |
