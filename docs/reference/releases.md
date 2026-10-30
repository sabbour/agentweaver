# v1 release reference

The tracked [`foundation.json`](https://github.com/sabbour/agentweaver/blob/v1/releases/foundation.json)
manifest owns the current component IDs, versions, kinds, and project paths.
Its schema is [`manifest.schema.json`](https://github.com/sabbour/agentweaver/blob/v1/releases/manifest.schema.json).
For the exact GitHub Actions image-selection, deployment-dispatch, monitoring,
and recovery commands, use the
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

Dispatch `.github/workflows/v1-deploy.yml` on `v1` with a `sha-<commit>` tag
from the image workflow. The deployment workflow must be present on the
repository's default branch to appear in GitHub's manual dispatch UI.

The workflow resolves all ten tags to digests, invokes
`npm run deploy:kustomize`, runs Broker/P1 and Projects & Config migration
Jobs, renders the existing Kustomize overlay, applies it to the selected
context, and checks all nine Deployment rollouts. A migration failure prevents
the root workload apply. Publication receipts and source-claim gates are not
part of this path.

The workflow requires `AZURE_DB_MAINTENANCE_CLIENT_ID` in the
`v1-db-maintenance` GitHub environment and uses GitHub OIDC. The existing
`agentweaver-id-db-maintenance` identity needs AKS Cluster User access and the
namespace-scoped Kubernetes permissions applied by
`npm run db:maintenance:setup -- --v1-deploy-only --execute`. The Role limits
Deployment read/update/watch permissions to the nine v1 workload names; it
does not grant Secret or RBAC access. The deploy command does not read Secrets
or manage Namespaces.

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
