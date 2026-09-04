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

Configure a Microsoft Entra application with the deployed
`https://<gateway-host>/auth/entra/callback` redirect URI. Provide its application
and tenant IDs through the params file or the `--entra-client-id` and
`--entra-tenant-id` flags.

## Commands

Run these commands from the repository root. Examples use `npm run`; `pnpm run`
with the same script name is equivalent.

### First deployment

```bash
npm run azure:provision-infra
```

With no arguments (and a TTY), this launches an interactive installer that
prompts for the Azure subscription, resource group (existing or new),
location, cluster/ACR/Key Vault names, Entra application/tenant IDs, and the
optional GitHub Repo App private-key PEM file, then provisions the cluster,
identity, monitoring, durable OAuth signing and encryption certificates,
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
  --entra-client-id "$ENTRA_CLIENT_ID" --entra-tenant-id "$ENTRA_TENANT_ID"
```

Config precedence: flags > env > params file > detected defaults > prompt.
Optional flags include `--skip-postgres`, `--image-tag <tag>`,
`--node-vm-size <sku>`, `--oauth-signing-certificate-name <name>`, and
`--oauth-encryption-certificate-name <name>`. Use
`--repo-app-private-key-file <path>` or `REPO_APP_PRIVATE_KEY_FILE` to import
the GitHub Repo App PEM without placing its contents in a command argument or
params-file value. The runtime loads the newest two usable
versions under each certificate name; create a new version under the same name for
rotation overlap.

`NODE_VM_SIZE` (or `--node-vm-size`) controls the AKS system/app/kata pool SKU for new clusters. The default is `Standard_D4s_v6`; existing clusters are unaffected because the installer only uses the value when it needs to run `az aks create` or `az aks nodepool add`.

The API receives logical secret name `repo-app-private-key`; its production
secret store maps that name to physical Key Vault secret
`ghtok-repo-app-private-key`. When the canonical secret is absent, deployment
migrates a readable legacy physical `repo-app-private-key` and preserves the
legacy secret. It stops before applying manifests when neither secret exists
or Key Vault access cannot be verified.

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
Each path reads the trusted AKS `DefaultDomainCertificate` status, derives the
canonical `https://agentweaver.<managed-domain>` origin, and injects it into one
runtime ConfigMap. A checksum on both the API and MCP pod templates forces them
to restart whenever that origin changes; request `Host` and forwarded headers
are never used to select the canonical origin.
To re-run only verification:

```bash
npm run azure:verify
```

The verifier checks cluster resources, routes, health, the canonical OAuth public
origin and `/mcp` resource, runtime certificate-family configuration, Key Vault
certificate versions, the canonical Repo App private-key secret, and JWKS.

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
| API/MCP auth failures | confirm Entra client/tenant IDs, canonical OAuth public origin, both configured Key Vault certificate families/versions, and readable `ghtok-repo-app-private-key` |
| AgentHost pods not ready | `kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` and check `kata-vm-isolation` runtime |
| Postgres connection failure | verify `agentweaver-postgres` secret and private DNS for `<server>.postgres.database.azure.com` |
