# Releasing Agentweaver

Agentweaver uses a protected `dev → release/vX.Y.Z → main` promotion flow.
Repository release identity and Azure deployment are separate operations.

## Command model

| Command | Identifier | Purpose |
|---|---|---|
| `npm run azure:provision-infra` | Current HEAD short SHA by default | Provision or reconcile Azure infrastructure and perform its initial deployment. |
| `npm run azure:deploy-from-local` | Current HEAD short SHA | Deploy local work to an existing environment. No release identity is created or consumed. |
| `npm run azure:deploy-from-commit -- <sha-or-ref>` | Resolved exact commit SHA | Deploy any committed ref without switching or modifying the caller's checkout. |
| `npm run release:publish` | Prepared `vX.Y.Z` | Create the annotated tag and GitHub Release from the exact protected-`main` SHA. No Azure work. |
| `npm run azure:deploy-from-release -- vX.Y.Z` | Existing published semver tag | Build/retag and deploy that exact release to the configured environment. |
| `npm run azure:release` | Prepared `vX.Y.Z` | First-shipment convenience command: publish, then deploy the same release. |
| `npm run azure:verify` | Running environment | Read-only health verification. |

```text
local HEAD SHA
  └─ azure:deploy-from-local
       └─ image:<short-SHA> → running dev/test environment

arbitrary branch / PR tip / commit
  └─ azure:deploy-from-commit -- <sha-or-ref>
       └─ detached exact-commit worktree → image:<short-SHA> → running environment

prepared exact main SHA
  └─ release:publish
       └─ annotated vX.Y.Z tag + GitHub Release
            └─ azure:deploy-from-release -- vX.Y.Z
                 └─ image:vX.Y.Z → running versioned environment
```

## Versioning

`VERSION` remains the product version. The root private `agentweaver` package is
Changesets' single-package adapter; `package.json.version` and
`package-lock.json.packages[""].version` must always equal `VERSION`. Run
`npm run version:check` to verify this invariant.

At `0.x`, use a `patch` changeset for compatible fixes and a `minor` changeset
for features or breaking changes. `major` is reserved for the deliberate
`release/v1.0.0` transition. Contributors add intent with `npm run changeset`;
only `npm run release:prepare` changes version mirrors or `CHANGELOG.md`.

`CHANGELOG.md` is durable repository history. GitHub Release notes are copied
from its exact matching section; do not run another changelog generator.

## Preparing a release

1. Select a green `dev` SHA and run `npm run changeset:status` plus
   `npm run release:plan`.
2. Create `release/vX.Y.Z` from that SHA and soak it.
3. On the clean release branch run:

   ```bash
   npm run release:prepare -- --expected X.Y.Z
   ```

4. Review and commit `VERSION`, package mirrors, `CHANGELOG.md`, and consumed
   fragments as `chore(release): prepare vX.Y.Z`.
5. Promote the prepared branch to `main` through a green PR.

> `release:prepare` runs from a normal dev checkout — you do **not** need to
> delete `node_modules/` or build output first (the script itself invokes the
> Changesets CLI from `node_modules/`). Its clean-tree guard only rejects
> ignored files **outside** recognized dependency/build/output locations
> (`node_modules/`, `dist/`, `bin/`, `obj/`, test output, and the harness
> run-artifact dirs are all fine). Keep the tree free of *stray* ignored files
> — an ignored file at the repo root or inside a tracked source tree still
> blocks the release so a human can investigate it.

## Publishing and deploying

From a clean checkout at the exact resulting `origin/main` SHA (including no
untracked or unexpected git-ignored files):

```bash
# Repository identity only: tag + GitHub Release, no Azure deployment
npm run release:publish

# Deploy that already-published release now or later
npm run azure:deploy-from-release -- vX.Y.Z
```

For the normal first shipment to the default environment, the composite command
performs both operations:

```bash
npm run azure:release
```

The composite is resumable orchestration, not a transaction. If deployment
fails after publication, the tag and GitHub Release remain durable:

```bash
npm run azure:release -- --resume vX.Y.Z
```

To deploy the same release to another configured environment, check out the
exact tag commit and run `azure:deploy-from-release` with that tag. The command
requires a clean checkout whose `HEAD` equals the annotated tag, verifies that
the GitHub Release and prepared metadata exist, and then builds/deploys/verifies
the release without publishing anything new.

Before deleting the release branch, create a short-lived branch from current
`dev` and forward-port the preparation commit:

```bash
npm run release:sync-dev -- <release-preparation-sha>
```

## Local and infrastructure deployment

Use `azure:provision-infra` for first/full idempotent infrastructure setup. Its
default image identifier is the current HEAD short SHA, never the repository
`VERSION`.

Use `azure:deploy-from-local` for day-to-day deployment to an existing
environment:

```bash
npm run azure:deploy-from-local
npm run azure:deploy-from-local -- --allow-dirty
```

It mints a short-SHA image tag, builds, deploys, performs post-deploy provenance
verification, reapplies and waits for the AgentHost warm pool, and never creates
or consumes a semver release. `--allow-dirty` is only for personal/throwaway
testing; the tag identifies the base HEAD commit, not uncommitted content.

Use `azure:deploy-from-commit` to deploy an already-committed branch, PR tip, or
older commit without switching the current checkout:

```bash
npm run azure:deploy-from-commit -- origin/teammate-branch
npm run azure:deploy-from-commit -- pull/123/head
npm run azure:deploy-from-commit -- abc1234
```

It fetches and resolves the argument to an exact commit, creates a temporary
detached worktree, runs the same SHA deployment pipeline as
`azure:deploy-from-local`, and removes the worktree afterward. It never includes
uncommitted changes and has no dirty-tree override.

After any deployment, use `npm run azure:verify` or inspect the cluster directly
before considering the change shipped.
