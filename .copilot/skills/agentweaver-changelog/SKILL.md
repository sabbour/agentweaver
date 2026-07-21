---
name: "agentweaver-changelog"
description: "Agentweaver Changesets lifecycle for contributor release intent, prepared changelogs, publication, and release notes"
domain: "release-management, documentation"
confidence: "high"
source: "RELEASING.md and scripts/changesets/*.mjs"
triggers: ["generate the changelog", "changelog for this release", "how do I add a changeset", "what changed in this release", "write release notes", "publish a release"]
---

## Purpose

Agentweaver uses [Changesets](https://github.com/changesets/changesets) to turn
reviewed, contributor-authored release intent into release metadata. It is a
single-package adapter: the root private `agentweaver` package is the only selectable
package. `VERSION` is the product-version source of truth; `package.json.version` and
`package-lock.json.packages[""].version` must mirror it. `@changesets/cli` is pinned to
the stable `^2.31.0` line, not the `next` prerelease tag.

## Changeset lifecycle

1. **Add intent:** for a user-facing PR, run `npm run changeset`, select
   `agentweaver`, choose the appropriate bump, and write user-facing prose. At `0.x`,
   use `patch` for compatible fixes and `minor` for features or breaking changes.
   Reserve `major` for the deliberate `release/v1.0.0` transition.
2. **Preview:** run `npm run changeset:status` and `npm run release:plan` to inspect
   pending fragments and the calculated version before cutting the release branch.
3. **Prepare:** only on a clean `release/vX.Y.Z` branch, run
   `npm run release:prepare -- --expected X.Y.Z`. It consumes the fragments, lets
   Changesets update package/lockfile versions and the matching `CHANGELOG.md` section,
   writes `VERSION`, and validates all version mirrors and the generated section.
4. **Promote:** promote the prepared release branch to `main` through a green PR.
5. **Publish:** from a clean checkout at the exact promoted `main` SHA, run
   `npm run release:publish`. It validates the prepared metadata and creates the
   annotated tag plus the GitHub Release from the exact matching changelog section. It
   does not calculate or commit a version, and it does not deploy anything.
6. **Deploy:** deploy that published version with
   `npm run azure:deploy-from-release -- vX.Y.Z`. For the normal first shipment,
   `npm run azure:release` composes steps 5 and 6 in one command.
7. **Forward-port:** before deleting the release branch, create a short-lived branch
   from current `dev` and run `npm run release:sync-dev -- <prepare-sha>`. Open and
   merge its PR to return the prepared metadata and consumed fragments to `dev`.

## Changelog and release-notes rules

`CHANGELOG.md` is durable in-repository history. GitHub Release notes are a
per-release projection in the Releases UI, sourced from that release's exact
`CHANGELOG.md` section by `extractChangelogSection`; they are not independently
generated.

Never hand-edit `CHANGELOG.md`, and never regenerate it after tagging.
`release:prepare` is the only command that writes it. Do not run an old changelog
generator: none should exist in this repository.

## Deploy commands use SHA identity, not release identity

`release:publish` creates repository release identity (tag + GitHub Release) but
performs no Azure operations. `azure:deploy-from-release` consumes an existing
published `vX.Y.Z`. `azure:deploy-from-local`, `azure:deploy-from-commit`, and
`azure:provision-infra` use SHA identity instead and never consume changesets or
create a release — they are for dev/test deploys, not official releases.

## Recovery and guardrails

- If preparation fails, fix the release branch or re-cut it. Never hand-edit generated
  version metadata or changelog output.
- If publication fails after preparation, retain the clean exact-`main` checkout and
  resume with `npm run azure:release -- --resume vX.Y.Z`.
- Do not omit a changeset from a user-facing PR unless the
  `changeset:not-required` exemption has a `Changeset exemption:` rationale.
- Do not use Changesets publish, prerelease, or snapshot workflows: Agentweaver is not
  published to npm.

For the authoritative release procedure, read [RELEASING.md](../../../RELEASING.md).
