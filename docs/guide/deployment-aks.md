---
title: Deploy to AKS
---

# Deploy to AKS

This is the operator path for a fresh Agentweaver AKS deployment. From a cloned
checkout, use the root package scripts as the canonical workflow. They select the
appropriate PowerShell or bash AKS implementation for the host OS and enforce the
required provisioning, image, deployment, and verification order.

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
| Node.js with `pnpm` or `npm` | canonical package-script workflow |

Create a GitHub OAuth App, then export:

```bash
export GITHUB_CLIENT_ID=<oauth-app-client-id>
export GITHUB_CLIENT_SECRET=<oauth-app-client-secret>
```

The same values are required before `pnpm run infra:deploy` (or its `npm run`
equivalent), because the identity-provisioning step writes them to Key Vault.

In PowerShell:

```powershell
$env:GITHUB_CLIENT_ID = '<oauth-app-client-id>'
$env:GITHUB_CLIENT_SECRET = '<oauth-app-client-secret>'
```

## Canonical package-script workflow

Run these commands from the repository root. Examples use `pnpm`; replace
`pnpm run` with `npm run` to use npm instead.

### 1. Provision AKS infrastructure

```bash
pnpm run infra:deploy
```

This provisions the resource group, ACR, AKS cluster and node pools, workload
identity and Key Vault secrets, monitoring, the MCP OAuth signing key, and
PostgreSQL. It is the required first-deploy command.

### 2. Build, push, and verify images

```bash
pnpm run release:images
```

For unattended frontend builds, export `AZURE_ARTIFACTS_NPM_PAT` (preferred) or
`AZURE_ARTIFACTS_NPM_PASSWORD_B64` first. Otherwise the build uses an existing
`~/.npmrc` credential and only then falls back to interactive helper flows on
supported hosts.

### 3. Deploy and verify the release

```bash
pnpm run release:deploy
```

This applies the release and runs the post-deploy verification checks. Run the
three commands in order for a first deployment; for a normal redeploy, run the
last two.

## Installer alternative

```bash
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks
```

Windows PowerShell delegates to `install.sh` through WSL2:

```powershell
$env:GITHUB_CLIENT_ID = '<oauth-app-client-id>'
$env:GITHUB_CLIENT_SECRET = '<oauth-app-client-secret>'
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1'))) -Aks
```

The installer remains available for bootstrapping, but use the package-script
workflow above for ongoing infrastructure and release work. Its flags apply only
to the installer:

| Bash | PowerShell | Use when |
|---|---|---|
| `--image-tag <tag>` | `-ImageTag <tag>` | pin/redeploy a specific immutable image tag |
| `--skip-postgres` | `-SkipPostgres` | PostgreSQL already exists and `agentweaver-postgres` secret is valid |
| `--skip-oauth-key` | `-SkipOauthKey` | Key Vault already has `mcp-oauth-signing-key` |

Never use `latest`. By default, `IMAGE_TAG` is `git rev-parse --short HEAD`; `AGENTHOST_IMAGE_TAG` defaults to the same value.

## Advanced: running AKS steps individually

The package scripts run these underlying steps in order:

| Package script | Underlying AKS steps |
| --- | --- |
| `infra:deploy` | `00-variables` → `10-create-cluster` → `15-setup-identity` → `15-provision-monitoring` → `16-provision-oauth-signing-key` → `17-provision-postgres` |
| `release:images` | `20-build-push-images` → `25-verify-image-provenance` |
| `release:deploy` | `30-deploy` → `40-verify` |

Use the canonical commands for normal operations. When recovering a single
failed step, the cross-platform launcher can run it without choosing a shell:

```bash
node scripts/run-os-script.mjs 15-setup-identity
```

The launcher uses the matching `scripts/aks/<step>.ps1` implementation on
Windows and `<step>.sh` on macOS/Linux. To invoke an implementation directly,
use its native form:

```powershell
.\scripts\aks\15-setup-identity.ps1
```

```bash
bash scripts/aks/15-setup-identity.sh
```

For a complete manual bash sequence when debugging a deployment:

```bash
source scripts/aks/00-variables.sh
bash scripts/aks/10-create-cluster.sh
bash scripts/aks/15-setup-identity.sh
bash scripts/aks/15-provision-monitoring.sh
bash scripts/aks/16-provision-oauth-signing-key.sh
bash scripts/aks/17-provision-postgres.sh
bash scripts/aks/20-build-push-images.sh
bash scripts/aks/25-verify-image-provenance.sh
bash scripts/aks/gen-a2a-mtls-certs.sh
bash scripts/aks/30-deploy.sh
bash scripts/aks/40-verify.sh
```

Each underlying step sources `00-variables` itself, so the package-script
workflow does not need to preserve a shell session between steps. The
`gen-a2a-mtls-certs` step is available for advanced certificate recovery and is
not part of the three canonical package commands.

## What the canonical commands deploy

- AKS cluster with `apppool` for app workloads and `katapool` for Kata-isolated AgentHost pods.
- Azure Container Registry images:
  - `agentweaver-api:${IMAGE_TAG}`
  - `agentweaver-frontend:${IMAGE_TAG}`
  - `agentweaver-mcp:${IMAGE_TAG}`
  - `agentweaver-agent-host:${AGENTHOST_IMAGE_TAG}`
- Azure Key Vault secrets for GitHub OAuth and MCP OAuth signing.
- Azure Database for PostgreSQL Flexible Server using FQDN `<server>.postgres.database.azure.com`; private connectivity comes from the VNet-linked `privatelink.postgres.database.azure.com` zone.
- Agent-sandbox CRDs/controller plus one AgentHost `SandboxTemplate` and one `SandboxWarmPool`, both named `agentweaver-agent-host`.

`release:images` is redeploy-efficient: its underlying image-build step resolves
the prior deployed image tag to its Git tag or the commit that wrote its value to
`VERSION`, rebuilds only components whose relevant paths changed, and retags
unchanged images with `az acr import`. For advanced troubleshooting, preview that
underlying bash step without invoking ACR with `DRY_RUN=true
PREVIOUS_IMAGE_TAG=vX.Y.Z bash scripts/aks/20-build-push-images.sh`.

## Deploy-order invariants

`release:deploy` applies manifests through its underlying deployment step in
dependency order. The important invariants are:

1. `storageclass-workspace.yaml` before `pvc-workspace.yaml`.
2. `sandbox-template-agenthost.yaml` before `sandbox-warmpool-agenthost.yaml`.
3. Services/gateways/routes before deployments.
4. Deployments include API, frontend, MCP, worker, and worker HPA.

## Verify

`pnpm run release:deploy` completes the deployment and its verification as one
operation. To rerun only verification while troubleshooting, use the advanced
individual-step launcher:

```bash
node scripts/run-os-script.mjs 40-verify
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
pnpm run release:images
pnpm run release:deploy
```

In PowerShell, use `$env:IMAGE_TAG = '<tag>'` before the same two commands.
The image build creates `apps/web/dist` locally before `az acr build`, then
uploads only the compiled frontend assets. Its temporary `.npmrc.build` is
deleted on exit so feed secrets never enter Docker layers.

The installer alternative remains:

```bash
bash install.sh --aks --image-tag "$(git rev-parse --short HEAD)"
```

## Common failures

| Symptom | Check |
|---|---|
| Gateway not programmed | `kubectl describe gateway agentweaver-gateway -n agentweaver` |
| ImagePullBackOff | confirm ACR attach and image tag was pushed by `pnpm run release:images` |
| API/MCP auth failures | confirm Key Vault has `github-client-id`, `github-client-secret`, `mcp-oauth-signing-key` |
| AgentHost pods not ready | `kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` and check `kata-vm-isolation` runtime |
| Postgres connection failure | verify `agentweaver-postgres` secret and private DNS for `<server>.postgres.database.azure.com` |
