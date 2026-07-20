# Releasing Agentweaver

Agentweaver uses a protected `dev → release/vX.Y.Z → main` promotion flow. Azure deploys from arbitrary SHAs for validation; only an exact protected-`main` SHA becomes a stable tagged release.

## Deploying is not releasing

| Command | Purpose | Release artifacts |
|---|---|---|
| `npm run azure:deploy` / `npm run azure:upgrade` | Dev/test Azure deployment of a SHA | None; changesets are never consumed. |
| `npm run azure:verify` | Read-only deployment verification | None. |
| `npm run azure:release` | Publish a prepared release from exact `origin/main` | Annotated tag, GitHub Release, build/deploy/verify. |

## Versioning

`VERSION` remains the product version. The root private `agentweaver` package is Changesets' single package adapter; `package.json.version` and `package-lock.json.packages[""].version` must always equal `VERSION`. Run `npm run version:check` to verify this invariant. Do not add workspaces or change `apps/web`'s decorative version.

At `0.x`, use a `patch` changeset only for compatible fixes and a `minor` changeset for features or breaking changes. `major` is reserved for the deliberate `release/v1.0.0` transition. Contributors add intent with `npm run changeset`; only `npm run release:prepare` changes version mirrors or `CHANGELOG.md`.

## Changelog and GitHub Release notes

| Artifact | Role | Source |
|---|---|---|
| `CHANGELOG.md` | Durable in-repository history | Changesets fragments consumed by `release:prepare` |
| GitHub Release notes | Per-release Releases UI projection | Exact matching `CHANGELOG.md` section |

Changesets replaces the former commit/PR-title reconstruction process. Do not run another changelog generator after tagging.

## Before you cut a release

Select a green `dev` SHA, then run `npm run changeset:status` and `npm run release:plan`. The planned version determines the provisional `release/vX.Y.Z` branch name. Keep the publication checkout clean.

## Cutting a release

1. Create and push `release/vX.Y.Z` from the selected green `origin/dev` SHA.
2. Soak it; stabilization PRs include changesets when user-facing and are immediately forward-ported to `dev`.
3. On the clean release branch run `npm run release:prepare -- --expected X.Y.Z`, review the generated metadata, commit it as `chore(release): prepare vX.Y.Z`, and record that preparation SHA.
4. Promote the prepared branch to `main` through a green PR and squash-merge; record the exact resulting `main` SHA.
5. From a clean checkout at that exact SHA run `npm run azure:release` (or its `release:stable` alias). It validates the prepared mirrors/changelog, tags, creates the GitHub Release, then builds, deploys, and verifies. It accepts no bump argument and never commits version files.
6. Before deleting the release branch, create a short-lived branch from current `dev` and run `npm run release:sync-dev -- <prepare-sha>`; merge its PR so the prepared version/changelog and consumed fragments return to `dev` without deleting newer fragments.

## If a release fails partway through

Preparation and publication are separate. If preparation fails, fix the release branch or re-cut it; do not hand-edit generated metadata. If tag/release/deploy work fails after preparation, retain the same clean exact-`main` checkout and run `npm run azure:release -- --resume vX.Y.Z` (use `--dry-run` to preview). Resume validates the prepared version and tag and never versions again; it can create a missing GitHub Release before completing build/deploy/verify.
## Upgrading a running environment (no version bump)

`upgrade` is for shipping a code change to a live environment (e.g. staging) **without**
cutting a semver release — it's the day-to-day "ship what's on `HEAD`" command:

```bash
npm run azure:upgrade
npm run azure:upgrade -- --allow-dirty   # dev/test escape hatch only -- never for real upgrades
```

Key differences from `release`:
- Mints a **new immutable image tag from the current HEAD short git SHA** — it never
  reuses `VERSION`'s semver tag (that belongs to `release` only). Reusing it can cause
  the image build step to no-op the build-vs-retag decision and ship a stale image.
- **Refuses a dirty working tree by default** — fails fast with an actionable error.
  `--allow-dirty` is an explicit opt-in for local dev/testing, never for a real upgrade.
- Runs deploy **before** provenance verification (`steps/25` is a post-deploy safety net
  that checks the digest actually running live in the cluster — running it before
  deploy always compares old live pods against the new target and falsely reports a
  stale-image failure).
- Cycles the AgentHost warm pool by **reapplying `SandboxTemplate`/`SandboxWarmPool` and
  waiting** for `status.readyReplicas == spec.replicas` (timeout ~180s) — it never
  manually deletes pods.

The same "green CI on the commit you are shipping" convention applies to an `upgrade` that
targets a shared or production environment; for a throwaway dev/test environment it is your
call.

## Other useful commands

```bash
npm run azure:deploy    # first-time / idempotent deploy to a fresh or existing Azure environment
npm run azure:verify    # post-deploy health/provenance verification only
npm run azure:dev       # local dev orchestration (see npm run dev)
```

Run any command with `--help` for its full flag reference, and read the module header
comment at the top of the corresponding `scripts/azure/*.mjs` file for the detailed
design rationale and binding semantics behind its behavior — several non-obvious
ordering/timing decisions (documented inline) exist specifically to avoid past
regressions (e.g. the stale-image and warm-pool-teardown issues referenced above).

## After a release or upgrade ships

- `release:prepare` has already generated the matching changelog section. Do not regenerate or hand-edit it after tagging.
- Confirm the new tag/image is actually running in the target environment
  (`npm run azure:verify`, or check via `kubectl`/Application Insights) before
  considering any related GitHub issue closeable. Merging to `main`, tagging, or a
  passing peer review alone are **not** sufficient — only live confirmation that the
  commit has shipped counts.
- If the release includes infra/schema changes, double-check `k8s/` manifests and EF
  Core migrations were included in the tagged commit.
