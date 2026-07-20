# Releasing Agentweaver

This describes how Agentweaver ships changes: versioning, cutting a semver release, and
the difference between a **release** and an **upgrade**. Everything here is driven by the
Node.js toolchain in `scripts/azure/` — there is no separate branch-promotion pipeline;
work reaches protected `main` through up-to-date, CI-gated PRs. Azure dev/test
deployments may come from any branch; official releases come from an exact
protected-`main` commit.

## "Deploying to Azure" is not the same as "cutting a release"

Three `azure:*` commands touch a real Azure cluster, but **only one of them cuts a release.**
Don't conflate "I deployed to Azure" with "I released" — `azure:deploy` and `azure:upgrade`
stand up or update a live environment for **development/testing/staging**, with **no
versioning implications at all**:

| Command | Use it when | Starting point / safety | Version or release artifacts? |
|---|---|---|---|
| `npm run azure:deploy` | The personal/shared dev/test environment does not exist yet, or you need a full idempotent infrastructure reconciliation. | Interactive prompts or explicit params; provisions Azure resources and deploys. Safe to rerun, but it changes the selected Azure environment. | **None.** No bump, tag, changelog, or GitHub Release. |
| `npm run azure:upgrade` | The environment already exists and you want to validate the current code on a real cluster. This is the normal Azure dev/test iteration loop. | Builds an immutable current-`HEAD` short-SHA image and refuses a dirty tree by default. `--allow-dirty` is only for a personal/throwaway environment, never shared validation. | **None.** No bump, tag, changelog, or GitHub Release. |
| `npm run azure:verify` | You only want to rerun read-only post-deploy health/provenance checks. | Existing deployed environment; does not build or deploy. | **None.** |
| `npm run azure:release` | A maintainer is intentionally publishing an official semver release from a protected-`main` SHA. | Target process: CI-gated release PR first, then tag/publish/deploy the exact merged SHA. The current command still performs its own commit/push and needs the migration described under [Cutting a release](#cutting-a-release). | **Yes.** Bumps `VERSION`, creates `vX.Y.Z`, and publishes the GitHub Release. |

So: `azure:deploy`/`azure:upgrade` = "put code on a live cluster for dev/test/staging" (no
bump, no tag, no GitHub Release). `azure:release` = "cut an official, versioned release of the
project." **`azure:release` is the only command that should ever be thought of as cutting a
release**, and it is what the [Versioning](#versioning), [Before you cut a
release](#before-you-cut-a-release), and [Cutting a release](#cutting-a-release) sections below
are about. For the `deploy`/`upgrade` mechanics, see [Upgrading a running
environment](#upgrading-a-running-environment-no-version-bump) and the README's Deploy to Azure
section.

## Versioning

Agentweaver uses [semver](https://semver.org), tracked in the repo-root `VERSION` file
(plain `X.Y.Z`, no `v` prefix). Each release is defined by an **annotated git tag `vX.Y.Z`**
— that tag is the single source of truth for "what is a release". `VERSION`, the
`CHANGELOG.md` entry, and the GitHub Release all derive from (and must agree with) the tag;
nothing downstream should describe a release the tag does not mark.

### Semantic versioning at 0.x (pre-1.0)

Agentweaver is currently **`0.y.z`** (check `VERSION` — at time of writing it is `0.9.70`).
Per the semver spec ([item 4](https://semver.org/#spec-item-4)) and common OSS practice, the
normal major/minor/patch compatibility guarantees do **not** apply while the major version is
`0`. For this repo, at its current `0.x` stage:

| Bump | `0.y.z` → | Use it for |
|---|---|---|
| **patch** | `0.y.(z+1)` | Backwards-compatible bug fixes only — no new features, no breaking changes. |
| **minor** | `0.(y+1).0` | **Everything else: new features AND breaking changes.** At `0.x` a breaking change does *not* force a major bump; it rides a minor bump. |
| **major** | `1.0.0` | Reserved. Do not cut a `major` release until the project is intentionally declaring a stable public API (see below). |

This matches the project's stated maturity: `docs/index.md` labels Agentweaver **Alpha
software** ("expect breaking changes and rough edges … don't use it for production work
yet"). Breaking changes are expected and are shipped as **minor** bumps.

**What changes at 1.0.0.** The first `major` release (`0.y.z` → `1.0.0`) is the point at which
Agentweaver commits to a stable public API. From `1.0.0` onward the *full* semver contract
kicks in: **major** = breaking change, **minor** = backwards-compatible feature, **patch** =
backwards-compatible fix. Until that deliberate 1.0.0 declaration, keep using `minor` for
breaking/feature work and `patch` for compatible fixes, and **do not** run `azure:release --
major`.

### Changelog vs. GitHub Release notes

There are **two separate release artifacts**, populated by **two different commands from two
different sources**. They are *not* redundant and never write to the same place:

| Artifact | Populated by | Source | Scope |
|---|---|---|---|
| **`CHANGELOG.md`** (in-repo, durable history) | `python scripts/gen-changelog.py` | Conventional-commit **subjects** on `main` | Every annotated `vX.Y.Z` tag range, newest first |
| **GitHub Release notes** (Releases UI, per tag) | `scripts/azure/release.mjs` (the `azure:release` step) | Merged **PR titles** since the previous tag (via `gh pr list`) | The one release being cut |

Both anchor on the annotated `vX.Y.Z` tag as the definition of a release, so they describe the
*same* set of releases — just from two angles (commit subjects vs. PR titles). With
**squash-merge** (one commit on `main` per merged PR — the recommended merge mode, see
[CONTRIBUTING.md](CONTRIBUTING.md#making-a-change)) the commit subject and the PR title are the
same string, so the two artifacts stay in agreement automatically.

- `CHANGELOG.md` is **generated, never hand-edited** — regenerate with
  `python scripts/gen-changelog.py` (buckets by prefix: `fix`, `feat`, `refactor`/`chore`,
  `docs`, `test`). It is not folded into the release PR because the new tag
  does not exist yet. Regenerate it after publication (so the tag range is
  included) and land it through a separate protected follow-up PR, e.g.
  `chore(docs): regenerate changelog for vX.Y.Z`.
- The GitHub Release notes are produced automatically by `azure:release`; you do not run a
  separate command for them.

## Before you cut a release

A release starts with a short-lived release branch from current `origin/main`.
The release PR contains the `VERSION` bump and any required release metadata;
it must be current with `main` and pass all required PR checks. Record the
exact squash-merged `main` SHA: that SHA, not a
maintainer's mutable checkout, is the release source.

- All four blocking checks must be green after the release branch is updated
  to current `main`.
- The release PR is the release cutoff; no separate freeze or release branch
  is required.
- The checkout used for publication must be clean and exactly at the recorded SHA.

## Branching model

Agentweaver is **trunk-based**: `main` is the only long-lived branch and must always be in a
releasable state (see the full contributor convention in
[CONTRIBUTING.md → Making a change](CONTRIBUTING.md#making-a-change)). In short:

- **`main` is always releasable.** Every change, including docs and version
  bumps, reaches it through a short-lived, up-to-date PR. There is
  deliberately no `dev`/`staging`/routine `release` promotion branch flow.
- **PRs are mandatory.** Direct pushes to `main` are prohibited. A PR may
  merge only while current with `main` and all blocking checks are green.
- **Squash merge is required** so `main` keeps one commit per logical change,
  and GitHub automatically deletes the source branch. That single
  commit subject feeds both `CHANGELOG.md` (commit subjects) and the GitHub Release notes (PR
  titles) identically — see [Changelog vs. GitHub Release notes](#changelog-vs-github-release-notes).
- **Routine release branches are not used.** A short-lived release-PR branch
  exists only long enough to land the version bump. Create a persistent
  maintenance branch only when Agentweaver must patch an older supported line
  while incompatible development continues, or needs a deliberate multi-day
  stabilization window.

The checkable **Branch Topology Activation Plan** (including when to enable
Merge Queue, create `release/X.Y`, or adopt `dev → release → main`) is maintained
in [CONTRIBUTING.md](CONTRIBUTING.md#branch-topology--room-for-growth). It governs
topology changes; this document remains the release-operation reference.

### No local integration branch

There is no supported long-lived local `integration`, `staging`, or
release-foundation branch. Local development is branch-agnostic:
`npm run dev` tests whatever is checked out in the current worktree and never
touches protected `main`. For integration testing before merge,
deploy that feature branch with `azure:deploy`/`azure:upgrade` to a real Azure
dev/test environment. The Azure cluster is the integration/staging
**environment**; required up-to-date PR checks are the git integration
mechanism.

An audit of the older local-only scratch refs found that all of these have
**zero commits not already reachable from `main`**, are not pushed to
`origin`, and are not referenced by repository tooling. They are safe to
remove with normal (non-force) deletion:

```bash
git branch -d main-staging integration integration-v0.9.71 release-staging \
  release/v0.9.71-foundation-integration main-tip localmain/main \
  merge-docs-landing-main
```

Git may refuse deletion while a ref is checked out by a worktree; remove that
obsolete worktree first, then rerun `git branch -d`. Do not use `-D` merely to
bypass that protection.

### Protected `main`

GitHub Merge Queue is unavailable because this repository is owned by the
personal account `sabbour`; personal-account repositories cannot enable it,
regardless of visibility. The enforceable near-term fallback is standard
branch protection requiring PRs, conversation resolution, linear history,
branches up to date before merge, and these blocking checks: `.NET tests`,
`Node toolchain tests`, `Web tests`, and `Docs build`. `Web lint` remains
advisory.

This fallback creates more update/retest churn under concurrent PRs: after one
PR merges, every competing PR becomes stale and must update and rerun CI.
The exact admin configuration is documented in
[`.github/main-branch-protection.md`](.github/main-branch-protection.md).
Workflow changes are versioned here, but Ahmed must activate the repository
ruleset/settings. No live admin mutation was made during this pass.

If the repository is ever transferred to a GitHub organization, revisit
Merge Queue as a future improvement; it is not a current action item.

### Dev/test and release flow

```text
feature branch/worktree
  ├─ npm run dev ───────────────────────> local-only validation
  ├─ azure:deploy / azure:upgrade ─────> Azure dev/test/staging environment
  │                                      (available before or after PR creation)
  └─ PR CI ─> update to latest main ─> CI rerun ─> protected main
                                                        │
                                                        └─ release PR
                                                             └─ exact merged SHA
                                                                  └─ tag/publish/deploy
```

This is why rejecting a staging **branch** does not remove staging: Azure
dev/test clusters provide the staging **environment**, while protected,
up-to-date PRs test admission to the sole source branch.

## Cutting a release

The protected-trunk release process is:

1. Create `release/vX.Y.Z` as a short-lived branch from current `origin/main`.
2. Change only `VERSION` (plus explicitly required release metadata), commit,
   push, and open a release PR.
3. Update the release branch to current `main` and pass every required check.
4. Record and check out the exact squash-merged `main` SHA.
5. Create annotated tag `vX.Y.Z`, publish the GitHub Release, and deploy that
   exact SHA through the shared build/provenance/deploy/verify engine.
6. Regenerate `CHANGELOG.md` after the tag and land it through a separate
   protected follow-up PR.

**Current automation gap — follow-up required.** `scripts/azure/release.mjs`
currently combines preparation and publication: it edits `VERSION`, commits,
tags, and pushes directly before publishing/deploying. That is incompatible
with mandatory PRs and protected `main`. Do not rush a bypass into the
script. A follow-up should split it into protected-PR-compatible prepare and
publish-existing-SHA phases (while preserving the staged `--resume` recovery
work). Until that lands, do not run the normal bump mode after the ruleset is
activated except under an explicit, audited owner-approved emergency bypass.

`npm run azure:release -- <bump> --dry-run` remains safe for previewing the
current implementation; it performs no git, GitHub, Azure, or Kubernetes
mutation.

### Cutting a patch release from a maintenance branch

1. If it does not already exist, create `release/X.Y` from the last supported tag.
   Apply a maintenance-branch-scoped copy of the `main` ruleset described in
   [`.github/main-branch-protection.md`](.github/main-branch-protection.md).
2. Land the fix through a PR into `release/X.Y`, following the same protection,
   review, and required-check rules as `main`.
3. From that branch's merged fix SHA, cut the patch tag and run
   `npm run azure:release` to publish it. Then forward-port the fix to `main` through
   a normal PR.

## If a release fails partway through

The current release script creates and pushes the version-bump commit and annotated tag, then
creates the GitHub Release **before** building and deploying. If a later image,
provenance, deploy, or verification step fails, do **not** run the normal bump command
again: it would calculate a new version and must never recreate the existing tag or
GitHub Release.

First fix the underlying deployment problem and ensure the checkout is clean and on the
release commit (the root `VERSION` must still be the tag's version). Then resume only the
unfinished shared-engine work:

```bash
npm run azure:release -- --resume vX.Y.Z
# Optional safe preview:
npm run azure:release -- --resume vX.Y.Z --dry-run
```

`--resume` is intentionally conservative. It requires a final `vX.Y.Z` tag whose version
matches `VERSION`, verifies that the tag exists locally, and verifies that the matching
GitHub Release already exists. It then skips every version-file, commit, tag, push, and
GitHub-Release action, and runs only build/retag, provenance verification, deployment, and
post-deploy verification. If any prerequisite is absent or mismatched, it stops with an
error rather than guessing; inspect the repository and GitHub Release state before deciding
whether the original normal release command is safe to run.

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

- **Regenerate the in-repo `CHANGELOG.md` through a separate protected follow-up
  PR.** Once the `vX.Y.Z` tag exists, run `python scripts/gen-changelog.py`
  (which reads annotated tags), commit the result on a short-lived branch,
  update it to current `main`, pass CI, and squash-merge it. See [Changelog vs. GitHub Release
  notes](#changelog-vs-github-release-notes).
- Confirm the new tag/image is actually running in the target environment
  (`npm run azure:verify`, or check via `kubectl`/Application Insights) before
  considering any related GitHub issue closeable. Merging to `main`, tagging, or a
  passing peer review alone are **not** sufficient — only live confirmation that the
  commit has shipped counts.
- If the release includes infra/schema changes, double-check `k8s/` manifests and EF
  Core migrations were included in the tagged commit.
