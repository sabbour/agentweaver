# Agentweaver releases

`foundation.json` is the authoritative list of component IDs, package kinds,
versions, and project paths. Its schema is in `manifest.schema.json`.

## Versioning and packages

Author changes with the repository's existing changeset workflow, then validate
and apply the reviewed version plan:

```powershell
npm ci
npm run release:validate
npm run release:plan
npm run release:apply
```

The plan/apply commands update the tracked component versions and preserve the
existing package-versioning behavior. Review the manifest and generated changes
before merging.

Changes to `releases/foundation.json` on `v1` run
`.github/workflows/v1-release-pack.yml`. It packs manifest components whose
kind is `contract` or `library` and publishes their NuGet packages to GitHub
Packages. Duplicate package versions are skipped. The workflow does not build
or publish container images.

The CLI build, run, and API-acceptance contract is documented in the
[CLI guide](../docs/guide/cli.md). Native acceptance uses GHCR by default; its
input manifest can select an approved ACR login server for exact-source image
checks. This selection does not change the image-publication destination.
This package-publication workflow does not publish standalone CLI archives.

## Container images

Changes to service projects, Docker inputs, or the component manifest on `v1`
automatically run `.github/workflows/v1-integration-images.yml` (push-triggered,
not manual). It pushes ten images directly to GHCR as `sha-<40-character-commit>`
tags; the workflow summary lists each digest. Every image is labeled with the
exact source commit (`org.opencontainers.image.revision`), its Git tree
(`io.agentweaver.source-tree`), and its `releases/foundation.json` component
version (`org.opencontainers.image.version`).

To build all ten images from an existing commit on the current `v1` history,
run the workflow manually with its full, lowercase commit SHA:

```powershell
gh workflow run v1-integration-images.yml --ref v1 -f source_sha=<40-character-commit>
```

The manual run stops if the commit is not an ancestor of `origin/v1`. The
workflow summary lists each pushed image digest. There are no image archives,
publication receipts, or source-claim gates.

To build the same ten images from an exact `v1` commit into Azure Container
Registry (ACR), use the local candidate builder. Run it from the repository
root with Node.js 24, .NET, Docker Buildx, Azure CLI, and an Azure CLI identity
that already has push access to the target registry:

```powershell
az login
az account set --subscription <approved-subscription>
npm run azure:build-acr-candidate -- <full-lowercase-40-character-commit-sha> <acr-login-server>
```

Use the ACR login server in the form `<name>.azurecr.io`. The builder fetches
`origin/v1`, stops unless the selected commit is an ancestor, and uses a
detached worktree at that commit. It uses the same Docker and .NET container
build paths and the same source, tree, and version labels as the v1 image
workflow. It logs in with `az acr login`, pushes `sha-<commit>` tags, and
verifies all ten image identities and digests. The result includes the source
tree, each ACR digest, and AgentHost's `linux/amd64` compressed pull-byte
measurement. All candidate images target `linux/amd64`, as the workflow does
on its Ubuntu builder. The builder does not create role assignments. If the
identity does not have push access, stop and ask an authorized ACR administrator.
The size check accepts OCI and Docker gzip-layer descriptors and reads their
sizes without downloading the image layers.
`az acr login` stores Docker credentials in the configured credential store.
After the ACR work is complete, remove that login with
`docker logout <acr-login-server>`.

## Deploy an exact v1 source commit

Use this command for intermediate acceptance of a commit whose ten images are
already published. Run it from the repository root on an operator machine with
Azure CLI, `kubectl`, `kubelogin`, Docker Buildx, and Node.js 24. Select and
confirm an approved non-production subscription and Kubernetes context first.
The command runs migrations and applies workloads to that cluster, so use only
the exact reviewed and trusted commit. Its `npm ci` runs package lifecycle
scripts from that commit; review and trust the source before execution. The
command requires the same AKS and namespace permissions as the local
deployment command.

Replace the placeholder with the commit's full lowercase 40-character SHA.
The default registry is GHCR. To use ACR, sign in with Azure CLI, select the
approved subscription with `az account set --subscription <approved-subscription>`,
and run `az acr login --name <acr-name>` so Docker Buildx can inspect the images.
Add `--registry <acr-login-server>` to the command. The exact-source helper
uses an ACR pull token for its OCI checks; it does not print or store that
token. `az acr login` stores Docker credentials in the configured credential
store; remove them with `docker logout <acr-login-server>` when finished.

```powershell
npm run azure:deploy-from-commit -- <full-lowercase-40-character-commit-sha> --execute
```

For ACR, use:

```powershell
npm run azure:deploy-from-commit -- <full-lowercase-40-character-commit-sha> --registry <acr-login-server> --execute
```

The command fetches the current `origin/v1` and stops unless the selected
commit is an ancestor. It creates a detached worktree at that exact commit,
runs locked `npm ci` there, resolves all ten `sha-<commit>` tags in the selected
registry to digests, and checks each image's OCI source commit and Git tree.
It passes those immutable digests and the registry to the existing Kustomize
deployment helper. ACR deployment stops if the selected source commit does not
include registry-aware Kustomize support.

The command does not switch branches or change tracked files in your current
worktree. It runs the existing migration-first deployment, permission checks,
selected-context apply, and nine Deployment rollouts. This command can change
the selected cluster. A failure after a migration or apply can leave partial
deployment state; follow the [local deployment recovery guidance](#deploy-workloads-locally).
If Git cannot remove the temporary worktree, the command reports the path and
leaves it in place.

## Deploy workloads locally

For a local deployment that updates only selected repositories, use the JSON
image manifest described in the
[Kustomize guide](../deploy/k8s/README.md#select-images-with-a-manifest).

Run deployment from a machine with Azure CLI, `kubectl`, `kubelogin`,
Docker Buildx, and Node.js 24. The helper uses Buildx to resolve image
references and reads AgentHost manifests and blob descriptors from the
selected registry to measure the selected platform image; it does not download
the image layers. The default registry is GHCR. For ACR, sign in with Azure
CLI and run `az acr login --name <acr-name>` before deployment. The ACR reader
uses a pull token for its OCI checks without printing or storing the token.
The Docker login uses the configured credential store; remove it with
`docker logout <acr-login-server>` when finished.
Select the published references you want to update in the operator-supplied
manifest; the helper keeps live references for omitted images. The legacy
`--image-tag` option still updates all ten images.

```powershell
az login
az account set --subscription <approved-subscription>
az account show --query "{subscriptionId:id,tenantId:tenantId}" --output table
az aks get-credentials --resource-group agentweaver --name agentweaver-aks `
  --subscription <approved-subscription>
kubelogin convert-kubeconfig --login azurecli
kubectl config current-context
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
```

Confirm the Azure subscription, tenant, and Kubernetes context are the
approved target before executing. The local Azure CLI identity must already
have the required AKS and namespace permissions; the deployment does not
grant access or provision Azure resources.

The command resolves selected manifest references to digests in the selected
registry and keeps live references, including their registries, for omitted
repositories. Use `--registry <acr-login-server>` to select ACR for the images
in the manifest; without that option, the command uses GHCR. For AgentHost, it verifies the
`linux/amd64` image manifest and measures the compressed image bytes. It
stamps the same digest, platform, and byte count into the AgentHost runtime
profile and Environment Sandbox options. An omitted AgentHost image keeps the
validated live tuple; missing or conflicting live references fail before any
Kubernetes write.
The live Environment image must already use the digest in that tuple. If it
uses a tag or a different digest, select AgentHost explicitly to update it.

Before writes, the command checks that `agenthost-tls` exists as a
`kubernetes.io/tls` Secret and checks the effective Agent Sandbox permissions
of the Environment ServiceAccount. The Secret check reads server-side table
metadata only; it does not validate certificate contents. The checks do not
create the Secret or grant access. The proposed RBAC manifest in
`deploy/k8s/proposed/` is separate from the active Kustomize set and is not
applied.

The command runs only migrations for selected services, renders the Kustomize
overlay, applies namespaced resources, and waits for all nine Deployments. It
excludes Namespace objects. A migration failure stops before the root
workload apply. Earlier migrations or workload apply may have
completed when a later step fails; inspect partial state and do not assume
rollback. Before retrying a failed migration, have an authorized operator
diagnose the cause and state. Verify schema compatibility before choosing an
older image.

Before any migration or Kubernetes write, the helper requires the live
`identity-broker-runtime-config-v3` ConfigMap to match the rendered Broker map,
including its identity, `immutable` flag, `data`, and `binaryData`. It also
checks that the Broker Deployment references this map. A missing, malformed,
or different map stops the command before writes. The helper reports changed
field or key names without values. After a successful check, it omits the
unchanged immutable map from the final apply. It does not create, replace, or
repair the map; use a separately reviewed design for those cases.

When `environment-runtime-config` changes, the helper checks permissions
before writes, stops the Environment Deployment, waits for its Pods and the
immutable ConfigMap cache window, then replaces only the observed map with
UID and resource-version delete preconditions. It restores the prior replica
count and verifies rollout. If replacement or rollout fails, the Environment
recovery annotation remains; rerun the same deployment command to resume.
A change to only `agenthost-runtime-v1` does not restart Environment.
See the [Kustomize guide](../deploy/k8s/README.md) for replacement details.

The Projects & Config migration identity must already have `CONNECT` and
ownership only of `projects_config` and its planned objects. Its migration
Job uses Kubernetes Workload Identity, not a per-release token. The command
checks the ServiceAccount annotation before creating a migration Job and
fails closed if it is missing. See the [database maintenance guide](../docs/guide/database-maintenance.md)
for the separate maintenance workflow and setup.

See the [Azure deployment guide](../docs/guide/deploy-to-azure.md),
[Kustomize guide](../deploy/k8s/README.md), and
[runtime configuration reference](../docs/reference/runtime-configuration.md).
