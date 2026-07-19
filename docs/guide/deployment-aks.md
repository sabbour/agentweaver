---
title: Deploy to Azure
---

# Deploy to Azure

Use the root `azure:*` package scripts (`scripts/azure/cli.mjs`) from a cloned
checkout to provision Azure resources and deploy Agentweaver.

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
| Azure CLI (`az`) | resource provisioning; images build remotely via `az acr build` — no local Docker daemon is required |
| `kubectl` | cluster apply/verify |
| `git` | default image tag = short commit SHA |
| Node.js 20+ with `npm` or `pnpm` | run the deployment commands |
| `gh` CLI, authenticated (`gh auth status`) | only for `azure:release`'s changelog + GitHub Release creation |

Create a GitHub OAuth App, then either export the credentials as environment
variables or supply them as flags/params-file values (see below):

```bash
export GITHUB_CLIENT_ID=<oauth-app-client-id>
export GITHUB_CLIENT_SECRET=<oauth-app-client-secret>
```

In PowerShell:

```powershell
$env:GITHUB_CLIENT_ID = '<oauth-app-client-id>'
$env:GITHUB_CLIENT_SECRET = '<oauth-app-client-secret>'
```

## Commands

Run these commands from the repository root. Examples use `npm run`; `pnpm run`
with the same script name is equivalent.

### First deployment

```bash
npm run azure:deploy
```

With no arguments (and a TTY), this launches an interactive installer that
prompts for the Azure subscription, resource group (existing or new),
location, cluster/ACR/Key Vault names, and GitHub OAuth client ID/secret, then
provisions the cluster, identity, monitoring, the OAuth signing key,
PostgreSQL, builds and pushes images, verifies provenance, deploys, and
verifies the result — printing an outputs summary at the end (never secrets).

For non-interactive use, pass flags, environment variables, and/or a params
file (see [`scripts/azure/params.example.json`](../../scripts/azure/params.example.json)):

```bash
npm run azure:deploy -- --params-file scripts/azure/params.my-env.json
```

or

```bash
npm run azure:deploy -- --resource-group agentweaver-rg --cluster-name agentweaver-aks-2 \
  --acr-name agentweaverregistry --location westus2 --keyvault-name agentweaver-kv \
  --github-client-id "$GITHUB_CLIENT_ID" --github-client-secret "$GITHUB_CLIENT_SECRET"
```

Config precedence: flags > env > params file > detected defaults > prompt.
Optional flags: `--skip-postgres`, `--skip-oauth-key`, `--image-tag <tag>`.

### Upgrading an existing deployment

```bash
npm run azure:upgrade
```

Mints a new immutable image tag from `HEAD` (refuses a dirty working tree),
builds and pushes images, verifies provenance, redeploys, and cycles the
AgentHost warm-pool sandboxes (reapply-and-wait on the SandboxWarmPool —
never manual pod deletion).

### Cutting a release

```bash
npm run azure:release -- patch   # or: minor | major
npm run azure:release -- patch --dry-run   # preview without making changes
```

See the [operations guide](./operations.md#release-process) for the full
release mechanics (semver bump, changelog, GitHub Release, build/deploy/verify).

## Running an individual step

`scripts/azure/cli.mjs` composes the same step modules under `scripts/azure/steps/`
that `deploy`/`upgrade`/`release` use. To re-run just verification:

```bash
npm run azure:verify
```

## Verify

`npm run azure:deploy` and `npm run azure:upgrade` verify as their final step.
To re-run only verification:

```bash
npm run azure:verify
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
npm run azure:upgrade
```

`azure:upgrade` mints a new image tag from the current `HEAD` short SHA,
builds/pushes/verifies it, and redeploys — this is the canonical redeploy path
described above.

## Common failures

| Symptom | Check |
|---|---|
| Gateway not programmed | `kubectl describe gateway agentweaver-gateway -n agentweaver` |
| ImagePullBackOff | confirm ACR attach and image tag was pushed (`azure:deploy`/`azure:upgrade` build+push this step) |
| API/MCP auth failures | confirm Key Vault has `github-client-id`, `github-client-secret`, `mcp-oauth-signing-key` |
| AgentHost pods not ready | `kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` and check `kata-vm-isolation` runtime |
| Postgres connection failure | verify `agentweaver-postgres` secret and private DNS for `<server>.postgres.database.azure.com` |
