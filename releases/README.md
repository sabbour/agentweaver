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

## Deploy workloads

`.github/workflows/v1-deploy.yml` requires only `image_tag` and uses the
`v1-db-maintenance` GitHub environment. The workflow must exist on the default
branch (`dev`) to be dispatchable; dispatch the run on `v1`.

Select a successful image run, confirm its `headSha` and the ten digests in
its summary, then dispatch `sha-<headSha>`. The deploy workflow accepts a tag,
not a digest list, and resolves the image digests at run time:

```powershell
gh run list --repo sabbour/agentweaver --workflow v1-integration-images.yml --branch v1 --limit 20
gh run view <image-run-id> --repo sabbour/agentweaver --json status,conclusion,headBranch,headSha,url
gh workflow run v1-deploy.yml --repo sabbour/agentweaver --ref v1 --raw-field image_tag=sha-<commit>
```

The `v1-db-maintenance` environment needs `AZURE_DB_MAINTENANCE_CLIENT_ID`.
The one-time `npm run db:maintenance:setup -- --v1-deploy-only --execute`
prepares deploy identity and namespace permissions; it does not change
PostgreSQL ownership. The workflow checks its target and scoped permissions;
it does not broaden them. Before first deploy, an authorized one-time handover
must give the Projects & Config migration identity `CONNECT` and ownership
only of `projects_config` and its planned objects. Its migration Job uses
Workload Identity, not a per-release token. Normal deployment runs migrations
and reviewed runtime-grant reconciliation before applying workloads.

If the one-time handover guard rejects unexpected role attributes or
memberships, stop for an authorized operator to diagnose and correct the
confirmed mismatch within the approved role policy. Retry only after it is
verified; do not loosen the guard or broaden the role.

The deploy job resolves all ten images to digests, runs Broker, P1, and
Projects & Config migration Jobs, applies the namespaced Kustomize overlay,
then waits for all nine Deployments. It excludes Namespace objects (no
Pod Security policy changes). There are no publication receipts, archives,
claim bundles, or deployment evidence gates. Monitor the actual run:

```powershell
gh run list --repo sabbour/agentweaver --workflow v1-deploy.yml --branch v1 --limit 10
gh run watch <deploy-run-id> --repo sabbour/agentweaver --exit-status
gh run view <deploy-run-id> --repo sabbour/agentweaver --log-failed
```

Earlier migrations or workload apply may have completed when a later step
fails; inspect partial state and do not assume rollback. For deployment-only
failures, rerun with the same published `image_tag`; rebuild only if build
inputs changed. Before retrying a failed migration, have an authorized
operator diagnose the cause and state. Verify schema compatibility before
choosing an older image.

See the [Azure deployment guide](../docs/guide/deploy-to-azure.md),
[Kustomize guide](../deploy/k8s/README.md), and
[runtime configuration reference](../docs/reference/runtime-configuration.md).
