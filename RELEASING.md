# Releasing Agentweaver

Agentweaver uses a protected `dev → release/vX.Y.Z → main` promotion flow.
Repository release identity and Azure deployment are separate operations.

## Command model

| Command | Identifier | Purpose |
|---|---|---|
| `npm run azure:provision-infra` | Current HEAD short SHA by default | Provision or reconcile Azure infrastructure and perform its initial deployment. |
| `npm run azure:deploy-from-local` | Current HEAD short SHA | Deploy local work to an existing environment. No release identity is created or consumed. |
| `npm run azure:deploy-from-commit -- <sha-or-ref>` | Resolved exact commit SHA | Deploy any committed ref without switching or modifying the caller's checkout. |
| `npm run release:publish` | Prepared `vX.Y.Z` | Create the annotated tag, wait for GHCR images, then create the GitHub Release. No Azure work. |
| `npm run azure:deploy-from-release -- vX.Y.Z [--image-source acr-build]` | Existing published semver tag | Import/build and deploy the exact release, then verify image provenance, warm-pool, and live health. Optional manifest/bundle diagnostics are reported as `NOT_RUN` when absent. |
| `npm run azure:release` | Prepared `vX.Y.Z` | Publish and deploy the same release through the normal source, version, image, and live-verification checks. Optional manifest/bundle diagnostics are reported as `NOT_RUN` when absent. |
| `npm run azure:verify` | Running environment | Read-only health verification. |

```text
local HEAD SHA
  └─ azure:deploy-from-local
       └─ image:<short-SHA> → running dev/test environment

arbitrary branch / PR tip / commit
  └─ azure:deploy-from-commit -- <sha-or-ref>
       └─ detached exact-commit worktree → image:<short-SHA> → running environment
            └─ representative integration + feature-specific API/UI E2E acceptance
                 └─ only if passing: prepare and promote release

prepared exact main SHA
  └─ release:publish
       └─ annotated vX.Y.Z tag + GHCR images + GitHub Release
            └─ azure:deploy-from-release -- vX.Y.Z
                 └─ image:vX.Y.Z → running versioned environment
                      └─ source, provenance, warm-pool, and live-health checks
                           └─ optional catalog diagnostics: NOT_RUN when not requested
```

## Versioning

`VERSION` remains the product version. The root private `agentweaver` package is
Changesets' single-package adapter; `package.json.version` and
both root lockfile mirrors — `package-lock.json.version` and
`package-lock.json.packages[""].version` — must always equal `VERSION`. The validator
checks each lockfile field independently so a missing or stale mirror cannot be masked
by the other. Run `npm run version:check` to verify this invariant.

At `0.x`, use a `patch` changeset for compatible fixes and a `minor` changeset
for features or breaking changes. `major` is reserved for the deliberate
`release/v1.0.0` transition. Contributors add intent with `npm run changeset`;
only `npm run release:prepare` changes version mirrors or `CHANGELOG.md`.

`CHANGELOG.md` is durable repository history. GitHub Release notes are copied
from its exact matching section; do not run another changelog generator.

## Preparing a release

1. Select a green `dev` SHA and run `npm run changeset:status` plus
   `npm run release:plan`. The planner queries published semver tags from
   `origin` and stops if `dev` does not contain the previous release preparation
   forward-port. Never bypass that guard: create a short-lived branch from
   current `dev`, run `npm run release:sync-dev -- <release-preparation-sha>`,
   merge that PR, and plan again.
   By default, Changesets selects the minimum version implied by the pending
   fragments. When a release is intentionally targeting a later patch in that
   same major/minor series, pass the exact target to the planner:

   ```bash
   npm run release:plan -- --target X.Y.Z
   ```

   `--target` must be a stable `X.Y.Z` version in the native plan's major/minor
   series, at least the native minimum, and newer than both `VERSION` and the
   latest published tag. `--expected` remains an assertion that the prepared
   version and `release/vX.Y.Z` branch match; it does not select or force a
   version. Omitting `--target` preserves the native Changesets patch/minor
   result. Native dependent-package range updates are retained, and the target
   is applied consistently to planned fixed or linked package groups.
2. Create `release/vX.Y.Z` from that SHA and soak it. After stabilization fixes
   are committed, record the exact candidate SHA and deploy it before preparing
   release metadata or creating any release identity:

   ```bash
   npm run azure:deploy-from-commit -- <candidate-sha>
   npm run azure:verify
   ```

   Run intermediate end-to-end tests **against this exact-SHA deployment**:
   representative integration coverage and feature-specific API/UI acceptance
   for everything shipping. Include the staging identity smoke
   (`node scripts/api-harness/run-persona.mjs` against the deployed staging URL
   with the default recorder-session auth provider) when identity or repository
   authorization is affected. Record the candidate SHA, deployment identity,
   selected tests, and passing results as release evidence. Failures block
   preparation, promotion, publication, and release deployment; fix the
   candidate, commit, redeploy its new SHA, and rerun acceptance.

3. Only after exact-SHA candidate acceptance passes, on the clean release branch run:

   ```bash
   npm run release:prepare -- --expected X.Y.Z
   ```

   If the plan used an explicit target, pass it again during preparation:

   ```bash
   npm run release:prepare -- --expected X.Y.Z --target X.Y.Z
   ```

4. Review and commit `VERSION`, package mirrors, `CHANGELOG.md`, and consumed
   fragments as `chore(release): prepare vX.Y.Z`.
5. Push the release branch.

   `release:prepare` fetches `origin/main` before it changes release files. If
   `origin/main` is not an ancestor, it runs:

   ```bash
   git merge -X ours origin/main --no-ff -m "merge: resolve main into release/vX.Y.Z"
   ```

   The merge runs on a clean tree, before Changesets changes release files.
   This keeps the release metadata commit separate from the ancestry merge. If
   Git reports conflicts, the command stops and leaves the merge for manual
   repair. To inspect without the merge, run
   `npm run release:prepare -- --expected X.Y.Z --no-ancestry-merge`. The command
   fails and prints the same `git merge` command.
   If `release:prepare` creates the ancestry merge, that invocation stops before
   applying release metadata. Re-run it on the clean merged tree so Changesets
   recalculates the plan against any newly merged package manifests, workspace
   configuration, or changesets; update the target and expected release branch
   if the native minimum changed.

   CI enforces this rule on `release/*` pull requests into `main`.
6. Promote the prepared branch to `main` through a green PR using a **merge
   commit**, not rebase or squash:

   ```bash
   gh pr merge <release-pr-number> --merge
   ```

   After promotion, confirm the resulting `main` source content matches the
   accepted candidate apart from prepared release metadata and reviewed
   promotion changes. Any substantive change requires another exact-SHA
   deployment and acceptance before publication.

> **Promotion history is not release identity.** The merge commit preserves the
> release branch's ancestry on `main` but does not guarantee that later promotions
> are conflict-free. The `release:prepare` ancestry merge incorporates `origin/main`
> into the release branch before release metadata changes. Inspect the actual merge
> base and review every conflict resolution; do not treat `-X ours` as proof that
> conflicts are cosmetic. `release:publish` requires the exact fetched `origin/main`
> SHA, not the release branch tip.

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
untracked or unexpected git-ignored files). Publication uses the same ignored-file
policy as preparation: normal dependency, build, test, and harness outputs are
allowed, while stray ignored files outside those recognized locations still block
the release. **Do not publish until the exact-SHA candidate deployment and its
representative integration and feature-specific API/UI acceptance have passed**
as described above:

```bash
# Repository identity only: tag + GHCR images + GitHub Release
npm run release:publish

# Deploy that already-published release now or later
npm run azure:deploy-from-release -- vX.Y.Z
```

For ordinary verified shipping to the default environment, the composite command
publishes and deploys using the existing source, tag, version, image-digest,
provenance, Entra, warm-pool, and live-health checks:

```bash
npm run azure:release
```

Without diagnostic inputs, release acceptance is reported as `NOT_RUN`; this is
not a passing catalog or Harness result.

To opt into catalog-backed diagnostics, supply a feature manifest. It is validated
against the exact tag commit and target deployment identity before publication or
deployment. A manifest without a bundle preserves the existing two-phase flow and
leaves acceptance pending after live verification:

```bash
npm run azure:deploy-from-release -- vX.Y.Z \
  --feature-manifest <release-feature-manifest.json>
```

Run the selected Harness scenarios against that verified deployment. Close the
opt-in acceptance only by resuming the release deployment boundary with exact
result manifests:

```bash
npm run azure:deploy-from-release -- vX.Y.Z --resume \
  --feature-manifest <release-feature-manifest.json> \
  --acceptance-bundle <canonical-harness-judge-bundle.json>
```

For the composite workflow, resume with the same inputs:

```bash
npm run azure:release -- --resume vX.Y.Z \
  --feature-manifest <release-feature-manifest.json> \
  --acceptance-bundle <canonical-harness-judge-bundle.json>
```

The gate validates declared manifests; it does not execute Harnesses. It requires the
selected representative challenge, direct API/UI/MCP coverage for every shipped
behavior and affected surface, non-empty typed evidence bound to the exact deployment,
project, challenge execution, run, catalog version, and surface, successful cleanup,
and no unresolved abnormal anomalies. A no-evidence result cannot complete acceptance.
The bundle manifest binds immutable bundle, batch, result, scenario, and execution IDs.
Every referenced result and evidence artifact must resolve beneath the bundle directory;
the deployment boundary recomputes SHA-256 and compares path/media metadata before
acceptance can close. Missing, outside-root, or hash-mismatched artifacts fail closed.

This is an integrity boundary inside the repository's trusted-operator model, not a
cryptographic defense against a malicious release operator. The operator controls the
local files and is trusted, while the verifier prevents accidental omissions and simple
fabricated result JSON from becoming authoritative. When diagnostics are opted in, only
a verified canonical Harness/Judge bundle passed through `azure:deploy-from-release`
can close acceptance.
Direct helper execution is diagnostic only.

The composite is resumable orchestration, not a transaction. If deployment
or opt-in acceptance fails after publication, the tag and GitHub Release remain durable:

```bash
npm run azure:release -- --resume vX.Y.Z \
  --feature-manifest <release-feature-manifest.json> \
  --acceptance-bundle <canonical-harness-judge-bundle.json>
```

If the image build fails, `release:publish` stops before it creates the
GitHub Release. Fix the image build. Then rerun the tag image workflow and run:

```bash
npm run release:publish -- --resume vX.Y.Z
```

To deploy the same release to another configured environment, check out the
exact tag commit and run `azure:deploy-from-release` with that tag. The command
requires a clean checkout whose `HEAD` equals the annotated tag, verifies that
the GitHub Release and prepared metadata exist, then builds/deploys/verifies the
release without publishing anything new. Supply a feature manifest to opt into
that target environment's catalog declaration and post-deployment acceptance
checks; without one, those diagnostics remain `NOT_RUN`.

By default, `azure:deploy-from-release` imports the release images that
`.github/workflows/publish-images.yml` already published for this exact tag
(`--image-source ghcr`) instead of rebuilding them. To rebuild the release
images from source into ACR instead, add `--image-source acr-build`:

```bash
npm run azure:deploy-from-release -- vX.Y.Z \
  --image-source acr-build
```

The GHCR ref is always the release tag itself, and the GHCR owner/repository
is derived automatically from the repo's GitHub origin remote — there is no
separate `--ghcr-ref` flag here (unlike `azure:provision-infra`) because a
release deployment only ever pulls the tag it is deploying. Pass
`--ghcr-token <token>` (or set `GHCR_TOKEN`) if the package is private. This
is the fastest way to redeploy an already-published release to an existing
environment: it skips rebuilding four container images and only imports,
retags, and redeploys them. It never touches cluster, ACR, Postgres,
identity, or monitoring infrastructure — use `azure:provision-infra` if any
of that needs to change.

Before deleting the release branch, create a short-lived branch from current
`dev` and forward-port the preparation commit:

```bash
npm run release:sync-dev -- <release-preparation-sha>
```

After publication, reconcile the milestones against what the release actually
consumed. Merge order decides the real contents, so a milestone set before the cut
can name the wrong release. `release:prepare` consumes the changeset fragments it
shipped. Map each consumed fragment back to the pull request that added it:

```bash
git log origin/dev --oneline --diff-filter=A -- ".changeset/<fragment>.md"
```

Put every pull request that appears in that list on this release's milestone.
Move every pull request that does not appear to the next milestone. Create the
next milestone (`vX.Y.Z+1`) and close the milestone for the release just
published; move any unshipped work to the new milestone:

```bash
gh api repos/<owner>/<repo>/milestones -f title="vX.Y.Z+1" -f state=open
gh api repos/<owner>/<repo>/milestones/<number> -X PATCH -f state=closed
```

See [CONTRIBUTING.md → Target release
milestone](CONTRIBUTING.md#target-release-milestone) for the contributor side.

## Published container images

Alongside the Azure/ACR deployment path, eligible branch and release-flow triggers
publish container images to GitHub's container/artifact registry via the
[`Publish images` workflow](.github/workflows/publish-images.yml):

| Trigger | Tags applied to each `ghcr.io/<owner>/agentweaver-*` image |
|---|---|
| Push to `dev` | `sha-<short>`, `dev` |
| Push to `release/vX.Y.Z` | `sha-<short>`, `rc-X.Y.Z` |
| Push to `main` | `sha-<short>`, `main` |
| Push tag `vX.Y.Z` | `sha-<short>`, `X.Y.Z`, `vX.Y.Z`, `latest` |
| Manual run on `dev`, `main`, or `release/vX.Y.Z` | `sha-<short>` plus that ref's `dev`, `main`, or `rc-X.Y.Z` channel tag |
| Manual run on another ref | `sha-<short>` |

This table follows `scripts/ci/ghcr-plan.mjs`: `workflow_dispatch` is classified
by its selected ref, not forced into a commit-only channel. The checked-in workflow
skips docs/specs/Markdown-only **pushes**. Manual runs have their own trigger.
These are repository facts, not evidence that a specific image or release exists.

Routine pull requests do not trigger image builds; the `CI` workflow performs PR
validation. Release images are built and published through the `vX.Y.Z` tag flow.

Release images are published from the `vX.Y.Z` tag push. The `release:publish`
command waits for that image workflow before it creates the GitHub Release.
As a result, the tag, the GitHub Release, and the `vX.Y.Z` images all describe
the same exact `main` SHA. The tag push builds with `IMAGE_TAG=vX.Y.Z` even
when a `sha-<short>` image for that commit already exists; copying the SHA
image manifest would preserve its non-release runtime identity. Image
publication is independent of deployment.
`azure:deploy-from-release` imports these images by default. Add
`--image-source acr-build` to build and ship them into the configured Azure
environment from source instead.

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

After the release is created and its identity deployed, run the separate
post-deployment acceptance boundary above against the released build (including
Preview harness and targeted API smoke where applicable). These results do not
replace the passing pre-publication exact-SHA candidate E2E evidence. Do not
target production before the release exists; failures block further promotion
until corrected and revalidated.
