# Releasing Agentweaver

This describes how Agentweaver ships changes: the branch promotion flow, the semver
release process, and the difference between a **release** and an **upgrade**.

## Branch flow

```
feat/fix/docs/chore branch → dev → preview → main
```

- **`dev`** — integration branch. PRs from feature/fix branches land here first.
- **`preview`** — staging branch. `dev` is promoted to `preview` (squashing/stripping
  internal-only paths like `.squad/`, `team-docs/`, `docs/proposals/`) via the
  `squad-promote` GitHub Actions workflow.
- **`main`** — release branch. `preview` promotes to `main` once its version has a
  matching `CHANGELOG.md` entry and contains no forbidden internal paths. Pushing to
  `main` triggers `squad-release.yml`.

Promotion is manual (`workflow_dispatch` on `squad-promote.yml`), supports a dry run
(`dry_run: true`), and validates before merging:
- `preview → main` fails if `VERSION`'s value doesn't have a corresponding
  `## [vX.Y.Z]` entry in `CHANGELOG.md`.
- `preview → main` fails if any `.squad/`, `.ai-team/`, `.ai-team-templates/`,
  `team-docs/`, or `docs/proposals/` files are present.

## Versioning

Agentweaver uses semver, tracked in the repo-root `VERSION` file (currently plain
`X.Y.Z`, no `v` prefix). `CHANGELOG.md` is **generated**, not hand-maintained — run
`python scripts/gen-changelog.py` to regenerate it from git tag/commit history if needed
(grouped by release tag, bucketed by commit-message prefix: `fix`, `feat`,
`refactor`/`chore`, `docs`, `test`).

## Cutting a release

Releases are cut with the Node.js toolchain (`scripts/azure/release.mjs`), not by hand:

```bash
npm run azure:release -- major   # or: minor | patch
npm run azure:release -- patch --dry-run   # preview without mutating anything
```

This performs the full release mechanics in one step:

1. Validates the working tree is clean (fails if there are staged or unstaged changes).
2. Reads and bumps `VERSION` (major/minor/patch).
3. Commits `chore(release): bump version to vX.Y.Z` and creates an annotated tag `vX.Y.Z`.
4. Pushes the release commit and tag to `origin`.
5. Generates a changelog from merged PRs since the previous tag (via `gh`) and creates
   the GitHub Release.
6. Delegates to the **same shared step engine** every other command uses — there is
   exactly one build/deploy code path in the whole toolchain:
   - `steps/20-build-push-images.mjs` — build/retag + provenance-stamp container images
   - `steps/25-verify-image-provenance.mjs` — verify image provenance
   - `steps/30-deploy.mjs` — deploy to AKS
   - `steps/40-verify.mjs` — post-deploy verification

`--dry-run` (or `DRY_RUN=true`) skips every git/`gh` mutation and every `az`/`kubectl`
mutation in the delegated steps — safe to run to preview what a release would do.

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

## Other useful commands

```bash
npm run azure:deploy    # first-time / idempotent deploy to a fresh or existing Azure environment
npm run azure:verify    # post-deploy health/provenance verification only
npm run azure:dev       # local dev orchestration (see npm run dev)
```

Run any command with `--help` for its full flag reference, and see the module header
comment at the top of the corresponding `scripts/azure/*.mjs` file for the detailed
design rationale and binding semantics behind its behavior — several non-obvious
ordering/timing decisions (documented inline) exist specifically to avoid past
regressions (e.g. the stale-image and warm-pool-teardown issues referenced above).

## After a release ships

- Confirm the new tag/image is actually running in the target environment
  (`npm run azure:verify`, or check via `kubectl`/Application Insights) before
  considering any related GitHub issue closeable — merging to `main` or tagging alone
  is not sufficient confirmation that a fix has shipped.
- If the release includes infra/schema changes, double-check `k8s/` manifests and EF
  Core migrations were included in the tagged commit.
