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
| `gh` CLI, authenticated (`gh auth status`) | release publication and published-release validation |

Create a GitHub OAuth App, then either export the credentials as environment
variables or supply them as flags/params-file values (see below):

> **Callback URL nuance:** the OAuth App's **Authorization callback URL**
> must match where Agentweaver is actually reachable — `localhost` for local
> dev, but the AKS Gateway's public host for an Azure deployment
> (`https://<gateway-host>/auth/github/callback`). The gateway host isn't
> known until *after* your first deploy provisions the AKS App Routing
> managed certificate — once it finishes, the deploy's outputs summary
> prints the exact value to use as **GitHub OAuth callback URL**. So: deploy
> first with a placeholder callback URL, then come back and paste in the
> printed one. GitHub OAuth Apps only support one callback URL each, so if
> you also run this locally, use a second OAuth App for `localhost`.

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
npm run azure:provision-infra
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
npm run azure:provision-infra -- --params-file scripts/azure/params.my-env.json
```

or

```bash
npm run azure:provision-infra -- --resource-group agentweaver-rg --cluster-name agentweaver-aks \
  --acr-name agentweaverregistry --location westus2 --node-vm-size Standard_D4s_v6 --keyvault-name agentweaver-kv \
  --github-client-id "$GITHUB_CLIENT_ID" --github-client-secret "$GITHUB_CLIENT_SECRET"
```

Config precedence: flags > env > params file > detected defaults > prompt.
Optional flags: `--skip-postgres`, `--skip-oauth-key`, `--image-tag <tag>`, `--node-vm-size <sku>`.

`NODE_VM_SIZE` (or `--node-vm-size`) controls the AKS system/app/kata pool SKU for new clusters. The default is `Standard_D4s_v6`; existing clusters are unaffected because the installer only uses the value when it needs to run `az aks create` or `az aks nodepool add`.

### GitHub organization allowlist

`GITHUB_ALLOWED_ORG` accepts comma- or semicolon-separated rules: `*` (all
organizations), `org`, `org/*`, or `org/team-slug`. Use `*` only for a trusted
internal deployment where unrestricted GitHub organization membership is the
intended sign-in policy.

### Image-build progress and optional Azure CLI limits

The installer prints elapsed time for frontend preparation, each image
lifecycle, and each ACR build/import/provenance operation. ACR manifest reads
are bounded to 60 seconds because they are read-only and safely retried by the
existing visibility poll. To bound a local Azure CLI process for a mutating
operation, explicitly set one or both environment variables:

```powershell
$env:ACR_BUILD_TIMEOUT_MS = "1800000"  # 30 minutes
$env:ACR_IMPORT_TIMEOUT_MS = "600000"  # 10 minutes
```

These limits are opt-in and do not retry a timed-out build or import: a local
CLI timeout leaves the remote operation's state unknown. Inspect the target
ACR tag/digest before deciding whether a manual retry is safe.

### Deploying local work to an existing environment

```bash
npm run azure:deploy-from-local
```

Mints a new immutable image tag from `HEAD` (refuses a dirty working tree),
builds and pushes images, redeploys, verifies provenance, and cycles the
AgentHost warm-pool sandboxes (reapply-and-wait on the SandboxWarmPool —
never manual pod deletion).

To deploy an arbitrary committed branch, PR ref, or historical commit without
switching the caller's checkout:

```bash
npm run azure:deploy-from-commit -- <sha-or-ref>
```

The ref is fetched and resolved to an exact commit, deployed from a temporary
detached worktree, and identified by its short SHA. Uncommitted state is never
included.

### Publishing and deploying a release

```bash
npm run release:publish
npm run azure:deploy-from-release -- vX.Y.Z

# First shipment convenience orchestration:
npm run azure:release
```

See the [operations guide](./operations.md#release-process) for the full
release preparation, publication, deployment, and recovery mechanics.

## Running an individual step

`scripts/azure/cli.mjs` composes the same step modules under `scripts/azure/steps/`
that infrastructure provisioning, local deployment, and release deployment use.
To re-run just verification:

```bash
npm run azure:verify
```

## Verify

All three deployment paths perform verification as part of their workflow.
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
npm run azure:deploy-from-local
```

`azure:deploy-from-local` mints a new image tag from the current `HEAD` short SHA,
builds/pushes/verifies it, and redeploys — this is the canonical redeploy path
described above.

## Common failures

| Symptom | Check |
|---|---|
| Gateway not programmed | `kubectl describe gateway agentweaver-gateway -n agentweaver` |
| ImagePullBackOff | confirm ACR attach and the selected deployment command pushed the image tag |
| API/MCP auth failures | confirm Key Vault has `github-client-id`, `github-client-secret`, `mcp-oauth-signing-key` |
| AgentHost pods not ready | `kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` and check `kata-vm-isolation` runtime |
| Postgres connection failure | verify `agentweaver-postgres` secret and private DNS for `<server>.postgres.database.azure.com` |
