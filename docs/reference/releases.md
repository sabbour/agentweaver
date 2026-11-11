# Release reference

Use this page to find the source of release metadata and the workflow for each
release operation. The canonical
[release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
owns the operational steps for package publication, image publication, and
local workload deployment. Do not treat those operations as one release state.

## Release metadata

The tracked [`foundation.json`](https://github.com/sabbour/agentweaver/blob/v1/releases/foundation.json)
manifest owns component IDs, package kinds, versions, and project paths. Its
schema is [`manifest.schema.json`](https://github.com/sabbour/agentweaver/blob/v1/releases/manifest.schema.json).

## Package versions and publication

Use the existing changeset and version-plan tools:

```powershell
npm ci
npm run release:validate
npm run release:plan
npm run release:apply
```

Review the resulting manifest and version changes before merging. A change to
`releases/foundation.json` on `v1` runs
`.github/workflows/v1-release-pack.yml`. The workflow packs manifest projects
whose kind is `contract` or `library` and publishes their NuGet packages to
GitHub Packages. It skips package versions that already exist. It does not
publish container images.

`Agentweaver.Policy.Acs` is a shared library package, not a workload. Its
manifest entry does not activate ACS or change the deployed v4 Policy path,
which continues to use the existing YAML adapter.

The standalone .NET CLI archive workflow is separate. See the
[CLI packaging guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/cli-dotnet/README.md).

## Container image publication

Changes to service code, Docker inputs, or the component manifest on `v1`
trigger `.github/workflows/v1-integration-images.yml`. The workflow builds the
ten image repositories used by `deploy/k8s/kustomization.yaml` and pushes
`sha-<commit>` tags directly to GHCR. Its summary reports each image digest.
Each image carries its source commit, Git tree, and component version in the
`org.opencontainers.image.revision`, `io.agentweaver.source-tree`, and
`org.opencontainers.image.version` labels.

For an ACR candidate, the release runbook provides
`npm run azure:build-acr-candidate`. It builds the same ten images from an
exact `v1` commit, pushes `sha-<commit>` tags to the selected ACR, and verifies
the source labels and image digests. This local build does not change the
automatic GHCR workflow or publish a product release.

The checked-in release and Kustomize composition includes the MCP compatibility
image, workload, and ingress resources. Their presence does not make MCP a
supported product or acceptance surface. Supported client paths are the
Gateway/API, browser, and native .NET CLI. Outbound remote MCP connections are
separate.

## Kubernetes workload deployment

Image publication does not deploy workloads. The operator runs the local
deployment command from an approved Azure CLI and Kubernetes context. The
operator-supplied JSON image manifest selects repositories to update; omitted
repositories keep their live image references and registries. GHCR is the
default registry. The `--registry <acr-login-server>` option selects ACR for
the images in the manifest or for all images in the legacy `--image-tag` form.
The legacy form updates all ten images.

```powershell
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
```

The command resolves selected references in the chosen registry to digests, runs the selected
services' migration Jobs, applies the namespaced Kustomize overlay, and checks
the nine Deployment rollouts. Before any cluster write, it checks that every
required Secret exists with the expected type; it reads only Secret names and
types. It does not provision Azure resources, grant
access, read Secret data, or manage Namespaces. A migration failure stops before
the root workload apply. Earlier migrations or workload updates may have
completed if a later step fails; inspect state before retrying.

For a first install, run certificate-only routing and the separate Broker
ConfigMap-only installer before this command. The Kustomize helper does not
create or repair that map. Follow the
[first-install order](../guide/deploy-to-azure.md#first-install-broker-configmap-order).

The preflight also validates and preserves an active Identity Broker GitHub
provider successor ConfigMap, its environment bindings, the explicit
`SecretKeyRef`, and its fingerprint. It stops before workload writes if
provider state is unknown or conflicts with source. It does not read Secret
data. See [GitHub provider setup](../guide/deploy-to-azure.md#configure-the-optional-github-providers).

The Projects & Config migration Job uses Kubernetes Workload Identity, not a
token Secret. Its one-time identity and PostgreSQL ownership setup are separate
prerequisites. The release runbook and
[database maintenance guide](../guide/database-maintenance.md) describe those
operations.

## Evidence boundary

A merged source revision, published package, published image, deployed
workload, and live user journey are different facts. Use the evidence and
verification steps for the operation you performed. A manifest entry, image
tag, or ready Deployment does not establish a completed user journey.

## Related references

- [Deploy Agentweaver to Azure](../guide/deploy-to-azure.md)
- [Database maintenance](../guide/database-maintenance.md)
- [Kustomize bundle](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
