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
file (see [`scripts/azure/params.example.json`](https://github.com/sabbour/agentweaver/blob/dev/scripts/azure/params.example.json)):

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
params-file value. The file must contain exactly one unencrypted PKCS#1
`RSA PRIVATE KEY` or PKCS#8 `PRIVATE KEY` PEM block, the two formats accepted
by the API's .NET `RSA.ImportFromPem` consumer. Provisioning validates the
file immediately after argument and params parsing, before variable discovery
or Azure work. It rejects symlink, junction, and reparse-path ambiguity, copies
the validated bytes to an exclusively created access-restricted temporary
file, gives only that file to Azure, and always removes it. Treat
`REPO_APP_PRIVATE_KEY_FILE` as a one-shot input: after the canonical import
succeeds, unset it in the environment. Remove it from every params file used
for the import. Then delete the PEM. Otherwise a future deploy reloads the
stale path before checking the valid canonical secret. The
runtime loads the newest two usable
versions under each certificate name; create a new version under the same name for
rotation overlap.

`NODE_VM_SIZE` (or `--node-vm-size`) controls the AKS system/app/kata pool SKU for new clusters. The default is `Standard_D4s_v6`; existing clusters are unaffected because the installer only uses the value when it needs to run `az aks create` or `az aks nodepool add`.

The API receives logical secret name `repo-app-private-key`; its production
secret store maps that name to physical Key Vault secret
`ghtok-repo-app-private-key`. Deployment never writes the readable legacy physical
`repo-app-private-key` automatically because Key Vault secret set cannot protect a
create-only write across deployment runners. If only the legacy secret exists,
deployment stops with an explicit download-and-file-import command. The file import
intentionally replaces the canonical value and must run from one serialized operator
or CI step. Any environment or params-file `REPO_APP_PRIVATE_KEY_FILE` setting must be
removed after that import succeeds. Remove the setting before you delete the PEM. See
[Configuration](./configuration#repo-app-installation-and-webhook) for the migration
commands. Deployment also stops before applying manifests when neither secret exists or
Key Vault access cannot be verified. A soft-deleted canonical secret is never
recovered by a normal provision or deploy. Recovery requires the explicit
`--recover-repo-app-private-key` operator flag after workload access is suspended
or the old GitHub App credential is revoked.

### Image-build progress and optional Azure CLI limits

The installer prints elapsed time for frontend preparation, each image
lifecycle, each ACR build/import/provenance operation, and staging-tag cleanup.
ACR manifest and repository digest reads default to a 10-minute per-attempt
budget because ACR's read path can take several minutes while concurrent
imports are still settling. To bound a local Azure CLI process for a mutating
operation, explicitly set one or more environment variables:

```powershell
$env:ACR_BUILD_TIMEOUT_MS = "1800000"    # 30 minutes
$env:ACR_IMPORT_TIMEOUT_MS = "600000"    # 10 minutes
$env:ACR_UNTAG_TIMEOUT_MS = "60000"      # 1 minute per staging-tag cleanup
$env:ACR_QUERY_TIMEOUT_MS = "600000"     # 10 minutes per ACR digest-verification attempt
$env:ACR_IMPORT_CONCURRENCY = "1"        # external image preflight imports at a time
```

`ACR_QUERY_TIMEOUT_MS` bounds a *single* attempt, not the whole wait. ACR digest
lookups are read-only, so timed-out or transient failed attempts are retried
before the deployment decides whether a tag is present, absent, or unknown.
Operators can still lower this for local experiments, but retries widen a
shorter configured budget back toward the 10-minute default so one slow
`az acr repository show` under import load does not make a resume look like a
missing tag.

The build and import limits behave differently from each other. A timed-out
`az acr build` is **not** retried: a local CLI timeout leaves the remote build's
state unknown. Inspect the target ACR tag/digest before deciding whether a
manual retry is safe.

ACR *import* and retag operations are retried automatically (three attempts,
exponential backoff with jitter) on transient transport or service failures,
including connection resets, throttling, and timeouts. This is safe because
those operations are idempotent. Retries pass `--force`, so importing the same
source into the same tag converges on the same digest even if an earlier attempt
actually landed before the connection dropped. Deterministic errors, such as a
missing source image or an authentication failure, still fail immediately
rather than burning retries. Staging-tag cleanup uses its own short
`ACR_UNTAG_TIMEOUT_MS` budget and never fails a deployment. A leaked preflight
tag is harmless. An aborted deployment is not.

External image preflight imports default to one image at a time. This avoids
ACR throttling seen during four-way GHCR import. Set `ACR_IMPORT_CONCURRENCY`
to a higher integer only when the target registry has enough capacity.

When `--image-source ghcr` or `--image-source custom` promotes a staged image
into a final release tag, a failed final-tag digest read is never treated as
"tag absent". Without `--force`, the promotion first attempts a non-forced
import; if ACR reports that the tag already exists, the deploy re-reads the tag
and retries with `--force` only when the existing digest already matches the
staged source digest. Differing tags still fail closed unless the operator
explicitly requested `--force`, and that operator intent is honored even if the
pre-promotion digest read remained unknown after retries.

### Resuming a failed deployment

A release deployment records each completed stage, so a failure part-way
through does not force you to repeat the expensive work:

```bash
npm run azure:deploy-from-release -- v1.2.3 --resume
```

`--resume` skips the build/promotion and deploy stages if they already completed
for this exact release **and** the same target (subscription, resource group,
registry, cluster, namespace, and image source), reusing the image digests they
resolved. Anything else starts clean. Use `--restart` to discard the recorded
state and re-run every stage; `--resume` and `--restart` cannot be combined.

Verification stages are never skipped. Provenance, warm-pool, and health checks
re-run on every attempt, including a resumed one — they are the evidence that
the deployment is correct, so a resumed run still has to prove it. A fully
verified deployment clears its own checkpoint, so the next run for that tag is
complete by default.

Checkpoints are stored under `~/.agentweaver/deploy-state/`, deliberately
outside the repository: a release deployment refuses to run against a dirty
working tree, and that check inspects untracked and ignored paths, so in-repo
state would block the very command that wrote it. The files record only stage
completion timestamps and resolved image digests — never credentials — and are
safe to delete at any time.

### Deploying local work to an existing environment

```bash
npm run azure:deploy-from-local
```

Mints a new immutable image tag from `HEAD` (refuses a dirty working tree by default),
builds and pushes images, redeploys, verifies provenance, and cycles the
AgentHost warm-pool sandboxes (reapply-and-wait on the SandboxWarmPool —
never manual pod deletion).

For an intentional personal development test, `--allow-dirty` is the explicit escape
hatch. It is not release-candidate evidence; use an exact committed candidate for release validation.

| Goal | Command |
| --- | --- |
| First provisioning | `npm run azure:provision-infra` |
| Deploy this checkout | `npm run azure:deploy-from-local` |
| Deploy an exact commit/ref | `npm run azure:deploy-from-commit -- <sha-or-ref>` |
| Deploy a published version | `npm run azure:deploy-from-release -- vX.Y.Z` |
| Verify without deployment | `npm run azure:verify` |

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

### Pruning the container registry

Every deployment adds manifests to the registry: release images, per-commit
images, provenance-stamped copies, and temporary preflight staging tags. Left
alone this grows without bound and makes registry queries slower. To remove
what nothing references:

```bash
# Show what would be removed -- changes nothing.
npm run azure:prune-registry

# Apply it.
npm run azure:prune-registry -- --execute
```

**The prune is a dry run by default** and prints a full plan first. `--execute`
additionally asks for confirmation; pass `--yes` to skip the prompt in
automation.

A manifest is **kept** when it is any of:

- one of the newest `--keep` releases (default 3) in its repository,
- tagged `latest-release`, `latest`, or `stable`,
- running in the cluster right now, by tag **or** by pinned digest,
- a child of any retained multi-arch index, or
- in a protected repository (`moby/*`, which holds the BuildKit images that
  `az acr build` itself runs on).

Everything else is unreferenced and is deleted.

Two safety properties are worth understanding before you trust it:

- **It protects by digest, never by tag name.** A multi-arch OCI index
  references its per-architecture children by digest, and those children carry
  no tags of their own. The intuitive shortcut — "delete everything untagged" —
  therefore destroys the architectures of images you meant to keep. Retained
  indexes are expanded into their children, and a manifest that cannot be
  expanded is kept rather than risked.
- **It fails closed.** If the set of running images cannot be read from the
  cluster, the prune refuses to delete anything instead of guessing, because
  that set is exactly what protects in-use digests. Check your `kubectl`
  context before running it.

Release provenance tags are deliberately write-locked (`writeEnabled=false`),
so deleting one returns `405 REGISTRY_DISALLOWED_OPERATION`. The prune lifts
that lock only for a manifest it has already decided to retire, then retries
the delete once.

Useful flags:

| Flag | Purpose |
| --- | --- |
| `--registry <name>` | Target registry; defaults to `ACR_NAME` from your params file. |
| `--keep <n>` | Releases to retain per repository (default 3). |
| `--json` | Emit the plan as JSON for scripting or review. |
| `--concurrency <n>` | Parallel deletes (default 8). |

Like the rest of the toolchain this talks to the registry REST API rather than
`az acr repository`, which intermittently hangs for minutes at a time (see
[Image-build progress and optional Azure CLI
limits](#image-build-progress-and-optional-azure-cli-limits)). A prune walks
every manifest in every repository, so it is precisely the workload where those
hangs are worst.

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
