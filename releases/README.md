# Foundation release composition (schema version 1)

`foundation.json` is a **draft**, not a release or proof of a deployed service.
It pins the independently versioned .NET foundation libraries
(`Agentweaver.Abstractions`, `Agentweaver.Providers`,
`Agentweaver.Persistence.Postgres`, `Agentweaver.Secrets.AzureKeyVault`,
`Agentweaver.Telemetry`, `Agentweaver.Telemetry.AzureMonitor`, and
`Agentweaver.ObjectStore.AzureBlob`; Persistence.Postgres is `0.2.0` and
the other libraries are `0.1.0`).
There is no repository-wide version authority, deployable image, local runtime,
or persona/deployment evidence in this first foundation. The draft cannot be
published as a platform release.

### Unreleased v1.0.0 impact

The Abstractions foundation now exposes a provider-neutral, versioned Secrets
reference and purpose/run-bound trusted redemption contract. Credential results
have explicit expiry and invalidation and avoid default diagnostic/JSON value
disclosure. The independent Azure Key Vault adapter uses validated vault
configuration and an injected Azure SDK client or `TokenCredential`, fetches exact
versions, and limits returned credentials to five minutes or the vault expiry.
Account-free SDK transport tests cover failures, cancellation and redaction.
This adds no authorization gate, deployable service or released version; the
draft composition remains at `0.1.0` without a manual version bump.

`manifest.schema.json` describes the strict wire shape. The dependency-free
validator additionally checks unique component IDs and projects, safe project
paths, each component's explicit `<Version>` in its checked-in `.csproj`, actual
`ProjectReference` edges, and the `compatibility` entries for those edges.
Each compatibility entry names an in-manifest consumer and dependency and lists
the *exact* dependency versions accepted by that consumer. The pinned version
must occur in the list; do not infer compatibility from semver proximity.
Semantic versions have `major.minor.patch` and optional prerelease/build
suffixes; a prerelease numeric identifier cannot have a leading zero.

Future `release` compositions must contain an actual `service` component
(with a SHA-256 image digest and its independently versioned project), plus
`evidence` with a lowercase 40-character `sourceSha`, matching AKS
`deploymentSha`, and exactly one API, UI, and MCP persona run at that same
source SHA with an HTTPS evidence URL. This validates evidence *references*,
not their authenticity or success; a deployment and its test results require
external verification before publication. `draft` forbids `evidence` so it
cannot claim unearned exact-SHA validation. Release publishing, chart wiring,
and deployment gates are future work, not supplied by this validator.

## Draft release impact

- Added `Agentweaver.Telemetry` 0.1.0 to the draft foundation composition:
  native OpenTelemetry traces, metrics, and logs with per-service resource identity
  and caller-configured in-process/exporter integration. No exporter is configured
  by this lower layer.
- Added `Agentweaver.Telemetry.AzureMonitor` 0.1.0 to the draft composition:
  opt-in Azure Monitor traces, metrics, and logs via the supported exporter SDK,
  with explicit connection string and optional injected `TokenCredential`.
  Other exporters can be composed through the lower-layer callbacks.
- No version bump or publication is implied by this draft entry. Documentation,
  tests, and CI wiring are exempt from a separate release entry. v1 uses plain Markdown
  and this validated manifest, not an installed Changesets or publishing pipeline.

Run `npm run release:validate` to check the draft against checked-in projects,
and `npm run test:release` for validator tests. Neither command installs
dependencies or contacts the cloud. CI also restores the .NET solution with
checked-in lock files, builds once, and tests all foundation libraries, including
the Azure Blob transport adapter.

## Unreleased foundation impact

- #1768 adds consumer-scoped transactional inbox receipts to
  `Agentweaver.Persistence.Postgres` 0.2.0. Migration 2 upgrades existing
  service-owned version-1 schemas without changing their outbox records. This
  remains a draft library, not a consumer service or exactly-once delivery.
- #1750 supplies the platform-singleton Object Store contract for opaque platform
  artifacts. #1744 adds the Azure Blob adapter, with streamed reads, create-only
  writes, and missing-object delete semantics. This draft library composition
  is not a platform release or version bump. No Changesets pipeline exists on v1.
