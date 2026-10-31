# Agentweaver v1 releases

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

The standalone .NET CLI archives remain a separate product release asset. Their
build, smoke-test, checksum, and candidate artifact flow is documented in
[`scripts/cli-dotnet/README.md`](../scripts/cli-dotnet/README.md).

## Container images

Changes to service projects, Docker inputs, or the component manifest on `v1`
automatically run `.github/workflows/v1-integration-images.yml` (push-triggered,
not manual). It pushes ten images directly to GHCR as `sha-<40-character-commit>`
tags; the workflow summary lists each digest. There are no image archives,
publication receipts, or source-claim gates.

## Deploy workloads locally

For a local deployment that updates only selected repositories, use the JSON
image manifest described in the
[Kustomize guide](../deploy/k8s/README.md#select-images-with-a-manifest).

Run deployment from a machine with Azure CLI, `kubectl`, `kubelogin`, Docker,
and Node.js 24. Select the published references you want to update in the
operator-supplied manifest; the helper keeps live references for omitted
images. The legacy `--image-tag` option still updates all ten images.

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

The command resolves selected manifest references to GHCR digests and keeps
live references for omitted repositories. It runs only migrations for
selected services, renders the Kustomize overlay, applies namespaced
resources, and waits for all nine Deployments. It excludes Namespace objects.
A migration failure stops before the root workload apply. Earlier migrations
or workload apply may have
completed when a later step fails; inspect partial state and do not assume
rollback. Before retrying a failed migration, have an authorized operator
diagnose the cause and state. Verify schema compatibility before choosing an
older image.

The Projects & Config migration identity must already have `CONNECT` and
ownership only of `projects_config` and its planned objects. Its migration
Job uses Kubernetes Workload Identity, not a per-release token. The command
checks the ServiceAccount annotation before creating a migration Job and
fails closed if it is missing. See the [database maintenance guide](../docs/guide/database-maintenance.md)
for the separate maintenance workflow and setup.

See the [Azure deployment guide](../docs/guide/deploy-to-azure.md),
[Kustomize guide](../deploy/k8s/README.md), and
[runtime configuration reference](../docs/reference/runtime-configuration.md).
