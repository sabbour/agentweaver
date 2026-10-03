# Foundation release composition (schema version 1)

`foundation.json` is a **draft**, not a release or proof of a deployed service.
It pins the independently versioned .NET foundation libraries
(`Agentweaver.Abstractions`, `Agentweaver.Providers`, and
`Agentweaver.Persistence.Postgres`, each currently `0.1.0`).
There is no repository-wide version authority, deployable image, local runtime,
or persona/deployment evidence in this first foundation. The draft cannot be
published as a platform release.

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

Run `npm run release:validate` to check the draft against checked-in projects,
and `npm run test:release` for validator tests. Neither command installs
dependencies or contacts the cloud. CI also restores the .NET solution with
checked-in lock files, builds once, and tests the provider contracts.
