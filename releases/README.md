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

Changes to service projects, Docker build inputs, or the component manifest on
`v1` run `.github/workflows/v1-integration-images.yml`. Its ten-image matrix
builds the existing AgentHost and Identity Broker Dockerfiles, the Web frontend
and host, and the seven .NET service containers. Each image is pushed directly
to its existing `ghcr.io/sabbour/...` repository with the immutable tag
`sha-<commit>`. The workflow summary lists each image digest.

There is no intermediate image archive, cross-runner artifact transfer,
publication receipt, source-claim gate, or custom publisher. GitHub Actions
uses the repository's `GITHUB_TOKEN` with package-write permission.

## Deploy to Kubernetes

Use `.github/workflows/v1-deploy.yml` on branch `v1` with an immutable
`sha-<commit>` image tag produced by the image workflow. GitHub requires the
manual workflow to exist on the repository's default branch before it appears
in the dispatch list. The workflow uses the existing `v1-db-maintenance`
environment and maintenance identity.

The workflow resolves all ten tags to registry digests and runs
`npm run deploy:kustomize`. The command runs Broker and P1 migration Jobs,
then the Projects & Config migration, renders the existing root overlay,
applies it to the selected context and `agentweaver` namespace, and waits for
all nine Deployments. A migration failure stops before the root Kustomize
apply. No publication receipt or source-claim gate is required.

**Current blocker:** Projects & Config has no approved schema-owner workload
identity configured for automated migration. Its runtime identity is
intentionally DDL-restricted, and the old operator connection/token path is
not used by deployment. The migration runner reports this missing identity
and stops before workload apply. Do not grant the runtime identity DDL access
or supply a manual token. The authorized Infra owner must verify or configure
the existing approved migration identity before the full deployment can
succeed.

Complete the documented Azure, database, identity, certificate, and runtime
configuration prerequisites first. See the [Azure deployment guide](../docs/guide/deploy-to-azure.md),
[Kustomize operator guide](../deploy/k8s/README.md), and
[runtime configuration reference](../docs/reference/runtime-configuration.md).
