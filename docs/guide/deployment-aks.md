---
title: Deploy to AKS
---

# Deploy to AKS

Use the root package scripts from a cloned checkout to provision AKS and release
Agentweaver.

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
| Node.js with `pnpm` or `npm` | run deployment commands |

Create a GitHub OAuth App, then export:

```bash
export GITHUB_CLIENT_ID=<oauth-app-client-id>
export GITHUB_CLIENT_SECRET=<oauth-app-client-secret>
```

Set these values before `pnpm run infra:deploy` or `npm run infra:deploy`.

In PowerShell:

```powershell
$env:GITHUB_CLIENT_ID = '<oauth-app-client-id>'
$env:GITHUB_CLIENT_SECRET = '<oauth-app-client-secret>'
```

## Commands

Run these commands from the repository root. Examples use `pnpm`; use `npm run`
with the same script name if you use npm.

### 1. Provision AKS infrastructure

```bash
pnpm run infra:deploy
```

Provision the cluster, identity, monitoring, OAuth key, and PostgreSQL.

### 2. Build, push, and verify images

```bash
pnpm run release:images
```

For unattended frontend builds, set `AZURE_ARTIFACTS_NPM_PAT` (preferred) or
`AZURE_ARTIFACTS_NPM_PASSWORD_B64`.

### 3. Deploy and verify the release

```bash
pnpm run release:deploy
```

Deploy the release and verify it. Run all three commands for a first deployment;
for a redeploy, run the last two.

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

## Running an individual step

To re-run identity setup:

```bash
node scripts/run-os-script.mjs 15-setup-identity
```

To re-run verification without deploying:

```bash
node scripts/run-os-script.mjs 40-verify
```

To run a step directly:

```powershell
.\scripts\aks\15-setup-identity.ps1
```

```bash
bash scripts/aks/15-setup-identity.sh
```

## Verify

`pnpm run release:deploy` deploys and verifies in one command. To re-run only
verification:

```bash
node scripts/run-os-script.mjs 40-verify
```

The verifier checks cluster resources, routes, and health.

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
