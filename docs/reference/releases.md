# v1 release reference

The tracked [`foundation.json`](https://github.com/sabbour/agentweaver/blob/v1/releases/foundation.json)
manifest owns the current component IDs, versions, kinds, and project paths.
Its schema is [`manifest.schema.json`](https://github.com/sabbour/agentweaver/blob/v1/releases/manifest.schema.json).
For automatic GHCR image builds and the local Kubernetes deployment command,
use the
[v1 release workflow runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md).

## Versioning and NuGet packages

Use the existing changeset and version-plan tools to update package versions:

```powershell
npm ci
npm run release:validate
npm run release:plan
npm run release:apply
```

Review the resulting manifest and version changes before merging. A change to
`releases/foundation.json` on `v1` runs
`.github/workflows/v1-release-pack.yml`, which packs `contract` and `library`
projects and publishes their NuGet packages to GitHub Packages. It skips
already-published package versions and does not build container images.

The native standalone CLI archives use the separate workflow described in
the [CLI packaging guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/cli-dotnet/README.md).

## Container images

The `.github/workflows/v1-integration-images.yml` workflow builds the ten image
repositories used by `deploy/k8s/kustomization.yaml` when service code, Docker
inputs, or the component manifest changes on `v1`. It builds the AgentHost and
Identity Broker Dockerfiles, Web frontend and host, and seven .NET service
containers. Images are pushed directly to GHCR as `sha-<commit>` tags; the
workflow summary reports the resulting digests.

The image workflow uses standard GitHub Actions Docker and .NET container
publishing steps. It does not transfer image archives between jobs or require
publication receipts, source claims, or provenance checks.

## Kubernetes deployment

The image workflow automatically pushes all ten image repositories to GHCR as
`sha-<commit>` tags. An operator runs the workload deployment locally from an
approved Azure CLI and Kubernetes context:

```powershell
az login
az account set --subscription <approved-subscription>
az aks get-credentials --resource-group agentweaver --name agentweaver-aks `
  --subscription <approved-subscription>
kubelogin convert-kubeconfig --login azurecli
kubectl config current-context
npm run deploy:kustomize -- --image-tag sha-<40-character-commit> --execute
```

Confirm the selected subscription and cluster context before execution. The
operator identity must already have the required AKS and namespace permissions;
the command does not provision Azure resources, grant access, read Secrets, or
manage Namespaces. It resolves all ten GHCR tags to digests, runs the
Broker/P1 and Projects & Config migration Jobs with runtime-grant reconciliation,
applies the namespaced Kustomize overlay, then checks all nine Deployment
rollouts. A migration failure prevents the root workload apply.

Projects & Config migrations use Kubernetes Workload Identity, not a token
Secret. The one-time setup provisions the `projects-config-migration`
ServiceAccount annotation and the
`agentweaver-id-projects-config-migration` identity, federated for audience
`api://AzureADTokenExchange` and subject
`system:serviceaccount:agentweaver:projects-config-migration`. A separate
one-time PostgreSQL setup maps that identity to a principal with `CONNECT` and
ownership only of the `projects_config` schema and its exact planned objects;
it must not own the database, retain database `CREATE`, or access other
schemas. The setup command does not change PostgreSQL ownership. The deploy
command verifies the ServiceAccount client-ID annotation before creating any
migration Job and fails closed if it is missing.

The migration Job applies EF migrations, then reconciles the reviewed
Projects & Config runtime-grant plan transactionally before workload rollout.
Extend and review that plan when new objects need permissions; do not use
blanket/default grants.
Complete the other Azure, database, identity, certificate, and runtime
configuration prerequisites in the [deployment guide](../guide/deploy-to-azure.md).

See the [Kubernetes bundle guide](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md) for overlay
contents and rollout commands.
