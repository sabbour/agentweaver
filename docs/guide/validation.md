# Validation workflow

Agentweaver keeps every package root's `node_modules` physical and private to its
worktree. The dependency helper accelerates a new worktree by sharing only npm's
content-addressed download cache:

```bash
npm run deps:ensure
```

The cache lives under Git's common directory. Each namespace is keyed by the package
root, `package.json`, `package-lock.json`, exact Node/npm versions, OS, architecture,
libc, sanitized install-shaping npm configuration, install arguments, and lifecycle
environment. A persisted invalidation generation for each package root is also part of
the key. Branch names are never part of the key.

Every new or changed worktree still runs `npm ci`, so npm deletes and recreates only
that worktree's dependency tree. A worktree-local marker lets an unchanged subsequent
validation skip reinstalling after checking the hidden lockfile, physical package
paths, and representative `require.resolve` results. Web dependencies must resolve
under that worktree's `apps/web/node_modules`; docs dependencies must resolve under
its `docs/node_modules`. Vite/Vitest/VitePress caches, TypeScript build information,
build output, and test scratch remain worktree-local.

npm owns normal concurrent reads and writes to its download cache. Agentweaver adds one
validation mutex per canonical worktree so two commands cannot replace the same local
`node_modules` concurrently. Lock owners record PID and process start time; stale local
locks are recovered only after that identity proves the original process is gone.
Maintenance operations use a separate global lock.

If a shared namespace is malformed or `npm ci` reports corruption, the helper
quarantines that namespace atomically under the maintenance lock and retries once with
a clean cache. A second failure stops validation. It never regenerates a lockfile.
Workspace roots, local lockfile links, authenticated npm configuration, CI, and
`AGENTWEAVER_DISABLE_SHARED_DEPS=1` use isolated `npm ci` instead. Set
`AGENTWEAVER_DEPS_CACHE_DIR` to choose another cache root.

Explicitly rotate a project's persisted generation, quarantine its prior cache
namespace, and delete only its current worktree tree:

```bash
npm run deps:invalidate -- --project apps/web
```

Verify npm's cache integrity under the maintenance lock:

```bash
npm run deps:verify -- --project apps/web
```

No live shared/junctioned `node_modules`, hardlinks, copy-on-write clones, or
cross-worktree writable trees are used.

## Validation profiles

Use the layer profile as an advisory preflight while developing. It detects affected
areas, prepares each required dependency root once, and prints command timing:

```bash
npm run validate:layer
```

For a .NET layer, provide a component-specific VSTest filter:

```bash
npm run validate:layer -- --area dotnet \
  --dotnet-filter "FullyQualifiedName~Agentweaver.Tests.Coordinator"
```

.NET validation uses the shared user-level NuGet package cache, locked restore, one
worktree-local build, then `dotnet test --no-build --no-restore`. `bin/` and `obj/`
are never shared. If an assets file points at another checkout, only that project's
current-worktree outputs are deleted before restore.

## .NET CI shards

CI runs the .NET suite in stable product-namespace shards. PostgreSQL/Testcontainers,
process-global environment, and Kata/bubblewrap tests use dedicated category shards so
container and process state cannot leak across runners. The Kata gate remains required
and requires a usable bubblewrap user namespace.

Each shard uploads a TRX artifact, whose test outcomes and per-test durations are
machine-readable.

## Coverage reports

Coverage uses the test families already owned by the repository: Coverlet across the
authoritative .NET shard matrix, Vitest's V8 provider for the web app, and pinned local
`c8` for the Node-native CI toolchain tests. Run an individual family or every family:

```bash
npm run coverage:dotnet
npm run coverage:web
npm run coverage:node
npm run coverage:all
```

The commands print the tested source scope, revision, generated report paths, and any
failed or unsupported required family. They remove only their own previous report
directory, require fresh regular in-repository reports, and reject missing, stale,
malformed, or symlinked .NET report inputs before combining them.

- .NET reports are written to `TestResults/coverage/<shard>/coverage.cobertura.xml`,
  then locally combined by pinned ReportGenerator in
  `TestResults/coverage/combined/{Cobertura.xml,Summary.json,Summary.txt,index.html}`.
  `TestResults/coverage/status.json` lists verified test counts and missing families;
  when a shard fails, the combined report contains **only** successful shards and
  `complete` is false. If merging itself fails, `status.json` records that error
  and leaves `combined` null. Partial numbers are never a complete .NET result,
  and the command exits nonzero.
  Coverlet explicitly includes the five named app assemblies, the supporting
  `Agentweaver.Api.Data` app assembly, and seven `packages/Agentweaver.*`
  assemblies, excluding tests, `obj/` and migration designer files. Only
  loaded/instrumented assemblies appear in the denominator: compare
  report assembly names with the printed inclusion list before interpreting totals.
  Each shard obeys the timeout in the authoritative CI shard matrix (15 minutes by
  default, 20 for orchestration); a timed-out shard is a failed required family.
- Web includes untouched `apps/web/src/**/*.{ts,tsx}` product sources while excluding
  tests, fixtures, setup, declarations, and generated files. Reports
  `coverage-final.json`, `coverage-summary.json`, `lcov.info`, text and HTML live in
  `apps/web/coverage/`.
- Node includes untouched `.mjs` sources under `scripts/azure`, `scripts/changesets`,
  `scripts/ci`, and `scripts/demo-recording`, excluding tests, fixtures, generated
  outputs and vendored files. `c8 --all` instruments the canonical tests in those
  four areas and writes `coverage-final.json`, `coverage-summary.json`, `lcov.info`,
  text and HTML to `coverage/node/`.

The Kata runtime shard requires Linux bubblewrap user namespaces. A host that cannot
run it is reported as a partial .NET coverage run and exits nonzero. PostgreSQL
Testcontainers is attempted, but an unavailable Docker runtime or database is also
reported as a failed required shard, not silently omitted. Browser E2E, API/MCP persona
harnesses, deployment checks, and other integration families are outside these
instrumentation commands and must be run through their own contracts. Line, branch,
and function coverage describe executed instrumentation, not behavioral completeness;
use the JSON/Cobertura machine reports for uncovered paths and counters, not an
estimated threshold or test-adequacy score.

### Scheduled cross-surface coverage reports

Ordinary `ci.yml` PR runs do **not** run these instrumented commands — they run the
same test suites uninstrumented, split across the seven required .NET shards plus the
web/Node/docs jobs, so a PR never pays the extra instrumentation+merge cost on top of
already-expensive required checks. There is no separate coverage workflow file:
coverage is collected by the exact same `dotnet-test-shards`, `web-tests`, and
`node-toolchain-tests` jobs in [`.github/workflows/ci.yml`](https://github.com/sabbour/agentweaver/blob/dev/.github/workflows/ci.yml),
instrumented in place. A weekly schedule (Monday 05:00 UTC) and an opt-in
`collect_coverage` `workflow_dispatch` boolean input both set a `collect_coverage`
output on the `changes` job; when true, it (a) forces those three jobs to run
regardless of path filters or draft state, and (b) threads a `--collect-coverage` flag
into the *same single* `dotnet test` / `vitest` / `node --test` command each job
already runs — never a second test invocation, and byte-identical command lines to an
ordinary PR when the flag is unset.

Each coverage-collecting job uploads its own report as a GitHub Actions artifact —
`dotnet-coverage-<shard-id>` per .NET shard, `web-coverage`, `node-coverage` — with a
**14-day retention** (not the repository default), and does so with `if: always()` so
a partial or failed coverage run still leaves its reports and `status.json`/
`coverage-summary.json` inspectable. The coverage step itself is **not**
`continue-on-error`: a partial or unsupported family (for example a timed-out .NET
shard, or Postgres/Kata being unavailable) fails that job, so GitHub's own job status
is never a false green for a partial run.

Two jobs run only when `collect_coverage` is true, after the test jobs above:

- `dotnet-coverage-combine` downloads every `dotnet-coverage-*` shard artifact produced
  in the *same run* (never re-running tests), merges them with pinned
  ReportGenerator via `scripts/ci/coverage-combine.mjs`, and uploads the result as
  `dotnet-coverage-combined`.
- `coverage-summary` downloads `dotnet-coverage-combined`, `web-coverage`, and
  `node-coverage`, then runs `node scripts/ci/coverage-summary.mjs` to append a
  per-area markdown report (covered/total lines, branches, methods/functions;
  completed vs. missing .NET shards; absent instrumented assemblies) to the run's job
  summary. This summary job only reports — it never fails the workflow on another
  job's behalf, and it never computes or displays a pass/fail coverage threshold; read
  the linked artifacts for the authoritative numbers and uncovered paths.

`workflow_dispatch` inputs only appear in the Actions "Run workflow" UI/API once the
workflow file on the repository's default branch (`dev`) declares them; dispatching
`ci.yml` with `collect_coverage: true` from a feature branch before that input lands on
`dev` will not offer the input. The scheduled trigger has the same requirement, but
since `ci.yml` already runs on every pull request, there is no bootstrap problem to
work around with a narrowly-scoped `pull_request` trigger the way a brand-new
standalone workflow would need — the feature branch introducing this already gets the
same jobs exercised, uninstrumented, on its own PR.

Each stacked PR gets its path-targeted preflight. Run the full profile against the
exact integrated tree at the stack top:

```bash
npm run validate:full
```

PR 1 must also pass the full suite against its current `dev` merge candidate before
merge. A green stack top does not replace that check. Restack and rerun after a lower
layer merges. Validation logs an identity derived from the exact commit/tree, dirty
digest, profile version, environment, and toolchain; test results are never reused
across Git SHAs.

## Persona harnesses

Use the executable harness contract for the surface you are testing. Copilot users can
invoke the matching repository skill.

| Scope | Executable contract | Copilot skill |
| --- | --- | --- |
| API | [`scripts/api-harness/SKILL.md`](https://github.com/sabbour/agentweaver/blob/dev/scripts/api-harness/SKILL.md) | [`agentweaver-api-harness`](https://github.com/sabbour/agentweaver/blob/dev/.github/skills/agentweaver-api-harness/SKILL.md) |
| UI | [`scripts/ui-harness/SKILL.md`](https://github.com/sabbour/agentweaver/blob/dev/scripts/ui-harness/SKILL.md) | [`agentweaver-ui-harness`](https://github.com/sabbour/agentweaver/blob/dev/.github/skills/agentweaver-ui-harness/SKILL.md) |
| MCP | [`scripts/mcp-harness/SKILL.md`](https://github.com/sabbour/agentweaver/blob/dev/scripts/mcp-harness/SKILL.md) | [`agentweaver-mcp-harness`](https://github.com/sabbour/agentweaver/blob/dev/.github/skills/agentweaver-mcp-harness/SKILL.md) |
| All surfaces | [`scripts/combined-harness/README.md`](https://github.com/sabbour/agentweaver/blob/dev/scripts/combined-harness/README.md) | [`agentweaver-harness`](https://github.com/sabbour/agentweaver/blob/dev/.github/skills/agentweaver-harness/SKILL.md) |

Use the
[`agentweaver-harness-scenarios`](https://github.com/sabbour/agentweaver/blob/dev/.github/skills/agentweaver-harness-scenarios/SKILL.md)
skill to list or create persona scenarios.

### Reusable challenge catalog

Reviewed personas remain indexed in `scripts/persona-briefs/catalog.json`. Versioned
acceptance and stress contracts live in
`scripts/persona-briefs/challenges.v1.json`:

```bash
node scripts/persona-briefs/challenge-catalog.mjs validate
node scripts/persona-briefs/challenge-catalog.mjs list
node scripts/persona-briefs/challenge-catalog.mjs get product-management-full-lifecycle-v1
node scripts/persona-briefs/challenge-catalog.mjs select-release --manifest <release-feature-manifest.json>
npm run azure:deploy-from-release -- vX.Y.Z --resume \
  --feature-manifest <release-feature-manifest.json> \
  --acceptance-bundle <canonical-harness-judge-bundle.json>
```

Catalog prose is untrusted scenario intent. It cannot configure targets, credentials,
commands, approvals, GitHub actions, or deployments. Actual scenarios are driven by a
human persona through the dynamic Harness, which discovers the live surface and reacts
to real responses. Structural checks, narration, prerecorded requests, and arbitrary
URLs cannot replace revision-, project-, and run-bound execution evidence.
Every passing execution claim and required surface needs non-empty typed evidence bound
to the exact deployed revision, project, challenge execution, run, and surface.

The full catalog does not run for every release. Release acceptance combines the
bounded `release-lumenpath-launch-integration-v1` representative project with focused
API and/or UI challenges for every newly shipped behavior, selected from its affected
surfaces. Missing direct claim or surface coverage fails closed. Scheduled deep stress
and manual destructive scenarios remain separate.

Release planning selects and documents the required scenarios but does not execute them.
Before release deployment, the deployment boundary validates the declared scenario
selection and binds it to the target tag commit and deployment identity. After that exact
revision is deployed, run the selected Harness scenarios and resume
`azure:deploy-from-release` with their result manifests. The boundary fails closed unless
the representative challenge and every feature-specific affected surface have passing
exact-revision evidence, cleanup succeeded, and no abnormal anomaly remains unresolved.
Evidence also binds to the catalog version. The standalone manifest validator is
diagnostic only and cannot close acceptance.

Authoritative closure requires a canonical Harness/Judge bundle. The verifier resolves
every result and evidence path beneath that bundle's directory, rejects traversal or
missing files, recomputes SHA-256, compares media/path metadata, and binds bundle, batch,
result, scenario, and execution IDs to the exact deployment. This protects the
trusted release operator from accidental or simply fabricated JSON closure; it does not
claim cryptographic protection from a malicious operator who controls the local files.

Abnormal release results use the structured
`agentweaver.release-acceptance-result/v1` contract. Harness and Judge only emit
revision-bound result and anomaly evidence; they cannot mutate GitHub. The coordinator
resolves the release-derived patch milestone, files the repair issue, and closes it
only after the repair is deployed and focused retests pass every required surface, or
after an explicit reviewed no-product-repair disposition.

API and MCP runners use the common lifecycle helpers in
`scripts/harness-shared/persona-lifecycle.mjs` for argument parsing, normalized
verdict persistence, judge invocation, result-line formatting, and deterministic
exit handling. Their surface adapters retain their own transport validation,
retries, evidence collection, output paths, and CLI option aliases.

# Oracle release acceptance

The API harness has a dedicated deterministic Oracle assembly/revision gate:
`node scripts/api-harness/run-oracle-acceptance.mjs --help`. It accepts a
disposable project and a running coordinator ID (or a goal to start one),
original and corrected visible application evidence, grounded feedback and
target files. It obtains recorder-session authentication in memory, preflights
version/OpenAPI/session once, polls incremental parent/child events, and enforces
configurable per-phase deadlines. The product currently performs Build & Test
before the human assembly review, so the first browser preview runs when that
gate is reached. It checks preview registration, HTTP 200, browser render,
visible content, and fatal errors; after one request_changes it verifies changed
assembly revision content identity and a distinct corrected browser render
before approval. Both decisions pin the current parent `output_revision_id`
and prepare a fresh orchestration execution key; artifact readiness and
browser verification share one corrected-preview deadline.
Only previews created by the driver are deleted and their absence confirmed.
Use a dedicated run: the current preview start API lacks an atomic
`created`/ownership indicator for sessions concurrently started by another caller.
The redacted JSONL transcript and result JSON contain timeout diagnostics and
phase IDs. Shell approvals require explicit `--approve-shell` on a disposable
project. This structured release gate complements, rather than replaces,
free-form PersonaActor scenario exploration (see
[`scripts/api-harness/SKILL.md`](../../scripts/api-harness/SKILL.md)).
