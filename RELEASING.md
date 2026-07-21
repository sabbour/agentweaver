# Releasing Agentweaver

Agentweaver uses a protected `dev → release/vX.Y.Z → main` promotion flow.
Repository release identity and Azure deployment are separate operations.

## Command model

| Command | Identifier | Purpose |
|---|---|---|
| `npm run azure:provision-infra` | Current HEAD short SHA by default | Provision or reconcile Azure infrastructure and perform its initial deployment. |
| `npm run azure:deploy-from-local` | Current HEAD short SHA | Deploy local work to an existing environment. No release identity is created or consumed. |
| `npm run release:publish` | Prepared `vX.Y.Z` | Create the annotated tag and GitHub Release from the exact protected-`main` SHA. No Azure work. |
| `npm run azure:deploy-from-release -- vX.Y.Z` | Existing published semver tag | Build/retag and deploy that exact release to the configured environment. |
| `npm run azure:release` | Prepared `vX.Y.Z` | First-shipment convenience command: publish, then deploy the same release. |
| `npm run azure:verify` | Running environment | Read-only health verification. |

```text
local HEAD SHA
  └─ azure:deploy-from-local
       └─ image:<short-SHA> → running dev/test environment

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

## Publishing and deploying

From a clean checkout at the exact resulting `origin/main` SHA:

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

After any deployment, use `npm run azure:verify` or inspect the cluster directly
before considering the change shipped.
