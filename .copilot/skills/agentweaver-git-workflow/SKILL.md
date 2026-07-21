---
name: "agentweaver-git-workflow"
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

## Anti-Patterns

- ❌ Branching from or opening a normal PR to `main`
- ❌ Merge-committing a PR instead of squash-merging it
- ❌ Letting a locally run agent work an issue in the shared primary checkout
- ❌ Creating a local worktree for a hosted agent
- ❌ Merging or trusting a PR without checking its live `mergeable`/`mergeStateStatus` and each required check's actual `conclusion`
