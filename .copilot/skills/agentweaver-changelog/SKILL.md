---
name: "agentweaver-changelog"
description: "Agentweaver Changesets lifecycle for release preparation, publication, and release notes"
domain: "release-management, documentation"
confidence: "high"
source: "RELEASING.md and scripts/changesets/*.mjs"
triggers: ["generate the changelog", "changelog for this release", "add a changeset", "publish a release", "write release notes"]
---

## Changeset lifecycle

1. Contributors run `npm run changeset` for user-facing behavior.
2. Maintainers preview pending intent with `npm run changeset:status` and
   `npm run release:plan`.
3. On a clean `release/vX.Y.Z` branch, run
   `npm run release:prepare -- --expected X.Y.Z`.
4. Promote the prepared branch to `main`.
5. From the exact promoted `main` SHA, run `npm run release:publish` to create
   the annotated tag and GitHub Release without deploying.
6. Deploy that published version with
   `npm run azure:deploy-from-release -- vX.Y.Z`.
7. For the normal first shipment, `npm run azure:release` composes steps 5
   and 6.
8. Forward-port prepared metadata with
   `npm run release:sync-dev -- <preparation-sha>`.

## Identity rules

- `CHANGELOG.md` is durable repository history and is generated only by
  `release:prepare`.
- GitHub Release notes are the exact matching changelog section.
- `release:publish` creates repository release identity but performs no Azure
  operations.
- `azure:deploy-from-release` consumes an existing published `vX.Y.Z`.
- `azure:deploy-from-local`, `azure:deploy-from-commit`, and
  `azure:provision-infra` use SHA identity and
  never consume changesets or create a release.
- Never hand-edit or regenerate the changelog after publication.

For the authoritative procedure and recovery commands, read
[RELEASING.md](../../../RELEASING.md).
