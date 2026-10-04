# Foundation release composition (schema version 1)

`foundation.json` is a **draft**, not a release or proof of a deployed service.
It pins eight independently versioned .NET foundation NuGet packages:
`Agentweaver.Abstractions`, `Agentweaver.Providers`,
`Agentweaver.Persistence.Postgres`, `Agentweaver.Secrets.AzureKeyVault`,
`Agentweaver.Identity`, `Agentweaver.Telemetry`,
`Agentweaver.Telemetry.AzureMonitor`, and `Agentweaver.ObjectStore.AzureBlob`.
The current package baseline for all eight is `0.0.0`.
`Agentweaver.Identity.Broker` remains service version `0.2.0`; it is not one
of the NuGet packages.
There is no repository-wide version authority, deployable image, local runtime,
or persona/deployment evidence in this first foundation. The draft cannot be
published as a platform release.

The initial package notes are the archived P0 changesets referenced by the
earlier release receipt. The baseline changeset does not create another version
bump. Later published package changes use patch, minor, or major intent based
on public API impact.

### Unreleased v1.0.0 impact

The Abstractions foundation now exposes a provider-neutral, versioned Secrets
reference and purpose/run-bound trusted redemption contract. Credential results
have explicit expiry and invalidation and avoid default diagnostic/JSON value
disclosure. The independent Azure Key Vault adapter uses validated vault
configuration and an injected Azure SDK client or `TokenCredential`, fetches exact
versions, and limits returned credentials to five minutes or the vault expiry.
Account-free SDK transport tests cover failures, cancellation and redaction.
This adds no authorization gate or deployable service; the draft composition's
initial package baseline is `0.0.0`.

#1766 adds explicit AKS workload-identity composition in the existing Key Vault
adapter: trusted hosts provide tenant ID, client ID, and projected token-file path
to Azure Identity, with no developer-identity fallback. Fake-transport tests cover
OAuth exchange and exact-version vault requests. Authentication, authorization and
rotation in a deployed AKS service remain unverified; no new component or version
is added to the draft manifest.
The component-scoped Changeset records future minor release intent, not an
immediate package version bump or a release of the draft platform.

Each merged foundation change has a record under `.changeset/` with its actual
component ID and unreleased semver intent. Tooling-only records use empty
frontmatter; neither kind is a release, version bump, deployed-service claim, or
substitute for independently verified publication.

`manifest.schema.json` describes the strict wire shape. The dependency-free
validator additionally checks unique component IDs and projects, safe project
paths, each component's explicit `<Version>` in its checked-in `.csproj`, actual
`ProjectReference` edges, and the `compatibility` entries for those edges.
Each compatibility entry names an in-manifest consumer and dependency and lists
the *exact* dependency versions accepted by that consumer. The pinned version
must occur in the list; do not infer compatibility from semver proximity.
Semantic versions have `major.minor.patch` and optional prerelease/build
suffixes; a prerelease numeric identifier cannot have a leading zero.

Draft service entries can omit `imageDigest` until publication supplies an actual digest.
The #1779 candidate registers `Agentweaver.Identity.Broker` as an unpublished service.
Its current manifest/project version is `0.2.0`.
The record is release intent, not a registry artifact or deployment receipt.
Do not use a placeholder digest to represent an unpublished service.

Future `release` compositions must contain an actual `service` component
(with a SHA-256 image digest and its independently versioned project), plus
`evidence` with a lowercase 40-character `sourceSha`, matching AKS
`deploymentSha`, and exactly one API, UI, and MCP persona run at that same
source SHA with an HTTPS evidence URL. This validates evidence *references*,
not their authenticity or success; a deployment and its test results require
external verification before publication. `draft` forbids `evidence` so it
cannot claim unearned exact-SHA validation. The manual workflow publishes artifacts only.
Platform release composition, chart wiring, and deployment gates remain future work.

## Draft release impact

- Added `Agentweaver.Telemetry` 0.0.0 to the draft foundation composition:
  native OpenTelemetry traces, metrics, and logs with per-service resource identity
  and caller-configured in-process/exporter integration. No exporter is configured
  by this lower layer.
- Added `Agentweaver.Telemetry.AzureMonitor` 0.0.0 to the draft composition:
  opt-in Azure Monitor traces, metrics, and logs via the supported exporter SDK,
  with explicit connection string and optional injected `TokenCredential`.
  Other exporters can be composed through the lower-layer callbacks.
- No version bump or publication is implied by this draft entry. Documentation,
  tests, and CI-only changes do not require a component changeset; product component
  changes require a fresh changeset even when the release prose describes them.

Run `npm run release:validate` to check the draft against checked-in projects
and validate all `.changeset/*.md` records. Run
`npm run release:validate -- --base <full-base-commit-sha>` to additionally require
an added or modified changeset in that diff for every changed product component.
CI supplies the PR base SHA (or the previous push SHA) and checks their common
ancestor against HEAD. An absent or unavailable base is an error, not an
exemption. Run `npm run test:release` for validator tests. Neither command installs
dependencies or contacts the cloud. CI also restores the .NET solution with
checked-in lock files, builds once, and tests all foundation libraries, including
the Azure Blob transport adapter.

## Unreleased foundation impact

- #1776 adds the `Agentweaver.Identity` 0.0.0 authorization library. It authorizes
  exact run grants and compares immutable
  pre/post grant identity, revision, expiry and bindings. Abstractions adds a
  metadata-only, thread-safe `SecretCredential.LimitLifetime` on the original
  credential, preserving backend invalidation without reading or copying a
  value. The archived P0 changesets provide its initial release notes. The
  compatibility record pins Identity's Abstractions dependency to 0.0.0.
  No broker/store/service/Azure
  deployment or new-tree approval from historical review receipts is implied.
- #1768 adds consumer-scoped transactional inbox receipts to
  `Agentweaver.Persistence.Postgres` 0.0.0. Migration 2 upgrades existing
  service-owned version-1 schemas without changing their outbox records. This
  remains a draft library, not a consumer service or exactly-once delivery.
- #1750 supplies the platform-singleton Object Store contract for opaque platform
  artifacts. #1744 adds the Azure Blob adapter, with streamed reads, create-only
  writes, and missing-object delete semantics. This draft library composition
  is not a platform release or version bump. Changeset records track intent;
  source-bound version preparation is described here. The original P0 packages
  were published from an earlier source; #1825 tracks their current 0.0.0 baseline.
  This draft does not claim platform deployment.
- #1767 adds ordered, layered provider composition: permitted Guardrails and
  Telemetry providers resolve and pin as ordered sets, pinning resource
  generations and order. Network Policy resolves its required L3/L4 and
  optional L7 layers as platform-selected, platform-owned providers, and
  pinning that layer additionally records a confirmed *applied* egress intent
  generation. Exclusive and singleton provider cardinalities are preserved
  and other cardinalities are explicitly deferred. This only pins and records
  the above; it adds no runtime enforcement, egress compilation/application,
  deployable service, or version bump, and the draft composition remains at
  `0.0.0`.

## Release planning and package preparation

Dependency-free Node scripts prepare component versions and locked package/image artifacts.
The plan, apply, and pack commands do not publish or deploy artifacts.
The manual workflow has a separate publication choice.

- `npm run release:plan` reads `releases/foundation.json` and every pending
  `.changeset/*.md` record, computes the highest-severity semver bump per
  touched component (major beats minor beats patch when more than one
  changeset touches the same component), and writes a checksummed,
  HEAD-bound plan to `artifacts/release/plan.json` (gitignored). Planning
  never modifies source files. Notes must match their exact committed HEAD bytes.
  Uncommitted notes, missing source notes, and stale source claims fail.
  The single recorded `baseline` changeset provides initial notes for the eight
  NuGet packages at `0.0.0` and does not create a version bump. Other changesets
  continue to use patch, minor, or major intent.
- `npm run release:apply` consumes that plan and the manifest. It fails
  closed — writing nothing — on a corrupted or hand-edited plan, a plan
  computed against a different commit (`stale plan`), a dirty working tree,
  a version collision, a partially applied plan, or a missing changeset or
  colliding archive destination; every precondition is checked before any
  file is touched. On success it bumps each component's manifest entry and
  checked-in `<Version>` mirror, preserves explicit `compatibility` entries,
  records a changelog entry in `releases/CHANGELOG.md`, and archives the
  consumed changesets under `.changeset/archive/`. Reapplying an
  already-applied plan is a no-op only after exact source-derived receipt verification.
  Unrelated dirty edits and changed preparation bytes fail.
  Apply prepares draft compositions only, without published image or deployment claims.
  A service version bump removes its previous `imageDigest`; the new version is unpublished.

  Every missing dependency version blocks preparation, including patch and minor bumps.
  Before planning, verify consumer compatibility with the target dependency version.
  Record the test evidence in the PR.
  Add that exact version to the source manifest's `compatibility[].versions`.
  Commit that declaration before planning.
  A successful package build alone does not prove compatibility.

  Caught write failures restore prior bytes and remove only newly created empty owned directories.
  Existing empty changelogs and directories survive rollback.
  Cleanup failures include the original error and require manual recovery.
  Process crashes and concurrent external writes are outside this rollback guarantee.
  A byte-preserving checkout is required: Git source bytes are the receipt authority.
  On Windows, use `core.autocrlf=false` before checkout.

  Each successful apply also writes a tracked (not gitignored) receipt to
  `releases/receipts/<sourceSha>.json`, naming the exact components bumped and,
  for every archived changeset, its original path, archived path, and a
  SHA-256 hash of its content. This receipt is what lets the CI changeset
  coverage guard (`release:validate -- --base <sha>`) recognize a real,
  `release:apply`-produced archival as equivalent to a fresh top-level
  changeset note for exact version-mirror edits only.
  The receipt includes the manifest path and the source-derived plan checksum.
  The guard compares archives with genuine Git source notes, then verifies preparation decisions.
  Extra edits in the same component or another component still require fresh top-level notes.
  A self-reported archive hash or an empty component list cannot grant coverage.
  An empty plan is a no-op only when genuine committed source notes contain no
  version intent or contain only the recorded initial package baseline.
  Recomputing a checksum or removing local notes cannot suppress pending source intent.
- `npm run release:pack` restores and builds each component with locked dependencies.
  By default it packs contracts/libraries and prepares service images in a fresh,
  empty `artifacts/release/pack/` directory. Pass `--packages-only` to prepare
  only the manifest's contracts and libraries; service images are not rebuilt.
  It writes an atomic `provenance.json` receipt after all artifacts succeed.
  The receipt includes the source commit SHA, manifest hash, each component's ID/kind/version,
  and a SHA-256 hash of every artifact file it actually produced. It refuses
  to run on a dirty tree, refuses a non-empty output directory (artifacts
  from different runs are never mixed), fails if the build step leaves
  the tree dirty afterward or if HEAD moves mid-run, and verifies the exact
  expected package or image artifact exists for each component rather
  than trusting a nonempty output directory. `release:pack` never pushes a
  package to a feed.
  Every component requires a checked-in `packages.lock.json`.
  Services use native .NET `PublishContainer` with an explicit immutable
  `<ContainerBaseImage>...@sha256:...</ContainerBaseImage>` in their project.
  Exactly one active declaration is required; XML comments do not supply a pin.
  Multiple active declarations, including conditional declarations, block preparation.
  The validated digest is passed explicitly to Release build and container publication.
  A Debug-only project condition cannot select an unpinned SDK default instead.
  The SDK writes a local `<id>.<version>.tar.gz` image archive without a registry push.
  Provenance records lock hashes and image archive hashes, not fabricated registry digests.
  A failed preparation produces no completed provenance.
  Partial output must not mix with a later run.

  For an exact-source NuGet-only release, use
  `npm run release:pack -- --packages-only --out artifacts/release/packages-only`,
  then run `node scripts/release/publish.mjs releases/foundation.json artifacts/release/packages-only <full-source-sha> --packages-only --confirm-publication`.
  The publisher validates that provenance contains exactly the manifest's
  non-service components and requires no container registry credentials.

The manually dispatched `v1 release pack` GitHub Actions workflow
(`.github/workflows/v1-release-pack.yml`) validates and prepares the exact dispatch source.
Dispatch requires the `v1` branch and an `expected_source` equal to the full dispatch SHA.
The default `publish: false` uploads artifacts only.
Pack artifact names bind the source SHA and workflow run ID, not the attempt number.
Rerunning failed publication jobs uses the successful pack job's artifact.
A full rerun replaces that run's pack artifact; a redispatch has a different run ID.
Neither path bypasses the durable publication claim.

### Manual artifact publication

Publication is a separate manual operation, not authorization to deploy or release the platform.
The workflow's `publish: true` choice runs the `v1-publication` environment job.
Repository owners can configure approval on that environment.
This change does not modify environment policy or branch protection.

Configure `RELEASE_NUGET_SOURCE` and `RELEASE_REGISTRY` as environment variables.
Configure `RELEASE_NUGET_API_KEY`, `RELEASE_REGISTRY_USER`, and
`RELEASE_REGISTRY_PASSWORD` as environment secrets.
`RELEASE_REGISTRY` is a lowercase registry host, optionally followed by a
lowercase repository namespace/path. The host is used for Docker login; the full
value prefixes each component image path. For example, `ghcr.io/sabbour`
publishes `ghcr.io/sabbour/<component-id>:<version>`. A host-only value keeps
the existing `registry.example/<component-id>:<version>` behavior. Schemes,
credentials, query/fragment suffixes, empty path elements, and traversal
segments are rejected.
Feed URLs must use HTTPS without embedded credentials or query tokens.
Feed fragments are also forbidden.
The script suppresses subprocess output and passes the registry password through stdin.
Credentials do not enter source files, provenance, or receipts.
The runner uses an isolated Docker configuration under ignored `artifacts/`.

Before publication, the script verifies source HEAD, source manifest, and every artifact hash.
Only the publication job receives `contents: write` and a scoped `GH_TOKEN`.
The repository is selected by `GITHUB_REPOSITORY`.
The pack job and top-level workflow permissions remain read-only.
After validation, the script atomically creates a GitHub annotated-tag claim at
`refs/tags/agentweaver-publication/<sourceSha>/claim`.
The claim binds the source commit, provenance hash, planned component versions,
artifact hashes, and credential-free publication destinations.
Only the successful ref creator proceeds to external operations.
Existing claim or result refs, ambiguous responses, and API errors block publication.
The script never overwrites or deletes these refs.
It pushes packages with native `dotnet nuget push`.
It loads prepared service images with Docker, then pushes their exact version tags.
`publication.json` records package hashes and actual immutable registry digests at the source SHA.
The workflow uploads that receipt separately.
The script does not modify the draft composition or fabricate deployment evidence.

External publication cannot roll back atomically.
An error leaves `status: "partial"` and the confirmed publication results in the receipt.
An immutable annotated-tag result at
`refs/tags/agentweaver-publication/<sourceSha>/result` records the terminal receipt
and the exact claim object ID.
It retains source, provenance, destination, and published-artifact bindings.
Local `publication.json` and uploaded Actions receipts supplement this durable record.
An existing claim blocks repeated publication even when no result or local receipt exists.
Expired Actions artifacts do not permit another publication attempt.
Concurrent publishers race on atomic ref creation; the loser performs no external operations.
Result or local receipt persistence failures remain explicit.
If publication also fails, the error retains both failures.
The permanent claim remains in place and blocks automatic retry.
Before any reconciliation, inspect the feed, registry, claim, and result independently.
This tooling provides no automatic claim deletion, retry, or duplicate-skipping path.
Do not treat partial receipts as completed publication.

This candidate supplies the manual path but publishes no artifacts.
Released composition acceptance still requires actual digests and exact-source API/UI/MCP deployment proof.
See the [release design](../docs/architecture/design/services-and-release.md#fresh-release-tooling)
for the remaining platform release and deployment gates.

## Changeset authoring

For each PR that changes a versioned library or service, add a **new or modified**
`.changeset/<descriptive-name>.md` covering every affected component ID from
`foundation.json` (or the current composition manifest). Use quoted IDs and
`patch`, `minor`, or `major`, followed by a blank line and a human summary:

```markdown
---
"Agentweaver.Providers": minor
---

Describe the change and its unreleased impact.
```

Additive capabilities use `minor`; select `patch` or `major` based on actual
compatibility. Tooling-only work may use a version-neutral record with empty
frontmatter (`---`, then `---`, then a blank line and a summary), without
inventing a product package. Documentation, tests, and CI-only edits are exempt;
changing product code, project files, schema, or package configuration is not.
Existing historical records do not satisfy a new diff. The validator checks
records against the actual manifest and changed component paths, not npm
workspaces or a global `agentweaver` package.
