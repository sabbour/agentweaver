---
name: "git-workflow"
description: "Agentweaver Git workflow for protected dev integration, worktrees, and PRs"
domain: "version-control"
confidence: "high"
source: "CONTRIBUTING.md"
---

## Integration Workflow

`dev` is the default protected integration branch. Use a short-lived issue branch and a
PR targeting `dev`; PRs are squash-merged so `dev` has one commit per logical change.
`main` is stable/published-only and accepts only soaked release promotions or audited
emergency hotfixes.

Issue branches use:

```text
squad/{issue-number}-{kebab-case-slug}
```

## Issue Work

1. Start from current `dev`:
   ```bash
   git checkout dev
   git pull origin dev
   ```
2. Choose the workspace appropriate to the contributor:
   - **Locally run Squad/Copilot CLI agent:** create or reuse the required dedicated
     worktree at `.worktrees/{branch-slug}`:
     ```bash
     git worktree add ".worktrees/{branch-slug}" \
       -b "squad/{issue-number}-{slug}" dev
     cd ".worktrees/{branch-slug}"
     ```
   - **Hosted agent** (such as GitHub `@copilot`): use the platform-provided isolated
     branch and environment; do not create a local worktree.
   - **Human contributor:** a worktree is optional; a normal branch in the primary
     checkout is fine:
     ```bash
     git checkout -b "squad/{issue-number}-{slug}" dev
     ```
3. Make focused changes, run the relevant tests, and commit with the issue reference.
4. Push and open a draft or ready PR against `dev`:
   ```bash
   git push -u origin "squad/{issue-number}-{slug}"
   gh pr create --base dev --title "{description}" --body "Closes #{issue-number}" --draft
   ```
5. **Before merging any PR, always verify its live state directly — never assume from the diff alone:**
   ```bash
   gh pr view {pr-number} --json mergeable,mergeStateStatus,statusCheckRollup \
     --jq '{mergeable, mergeState: .mergeStateStatus, checks: [.statusCheckRollup[] | {name, status, conclusion}]}'
   ```
   Confirm `mergeable` is `MERGEABLE` (no conflicts) and every required check
   (`.NET tests`, `Node toolchain tests`, `Web tests`, `Docs build`) shows
   `conclusion: SUCCESS`. Re-run any failed required check once
   (`gh run rerun {run-id} --failed`) to rule out known CPU-contention flakes
   before treating a real failure as caused by the PR's own changes.
6. After confirming the above, squash-merge.
7. Remove a local agent worktree after merge.

## Release and Deployment Identity

- `release:prepare` generates version metadata on `release/vX.Y.Z`.
- `release:publish` runs only from the exact promoted `main` SHA and creates
  the annotated tag plus GitHub Release; it performs no Azure deployment.
- `azure:deploy-from-release -- vX.Y.Z` requires an existing published tag
  and a clean checkout at that tag commit.
- `azure:deploy-from-local` ships the current local HEAD under a short-SHA
  image identifier and never creates release identity.
- `azure:deploy-from-commit -- <sha-or-ref>` deploys any exact committed ref
  through a temporary detached worktree without switching the caller's checkout.
- `azure:provision-infra` is the full Azure infrastructure installer and
  reconciler, not the command for deploying a published release.
- `azure:release` composes publication and the first release deployment.

## Anti-Patterns

- ❌ Branching from or opening a normal PR to `main`
- ❌ Merge-committing a PR instead of squash-merging it
- ❌ Letting a locally run agent work an issue in the shared primary checkout
- ❌ Creating a local worktree for a hosted agent
- ❌ Merging or trusting a PR without checking its live `mergeable`/`mergeStateStatus` and each required check's actual `conclusion`
