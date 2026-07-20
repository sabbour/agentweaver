---
name: "git-workflow"
description: "Agentweaver trunk-based Git workflow for issue branches, worktrees, and PRs"
domain: "version-control"
confidence: "high"
source: "CONTRIBUTING.md"
---

## Trunk-Based Workflow

`main` is the only long-lived branch and must remain releasable. This repository
does not use `dev`, preview, or insiders integration branches. Reviewed changes use
a short-lived branch and a PR targeting `main`; PRs are squash-merged so `main`
has one commit per logical change.

Issue branches use:

```text
squad/{issue-number}-{kebab-case-slug}
```

## Issue Work

1. Start from current `main`:
   ```bash
   git checkout main
   git pull origin main
   ```
2. Choose the workspace appropriate to the contributor:
   - **Locally run Squad/Copilot CLI agent:** create or reuse the required dedicated
     worktree at `.worktrees/{branch-slug}`:
     ```bash
     git worktree add ".worktrees/{branch-slug}" \
       -b "squad/{issue-number}-{slug}" main
     cd ".worktrees/{branch-slug}"
     ```
   - **Hosted agent** (such as GitHub `@copilot`): use the platform-provided isolated
     branch and environment; do not create a local worktree.
   - **Human contributor:** a worktree is optional; a normal branch in the main
     checkout is fine:
     ```bash
     git checkout -b "squad/{issue-number}-{slug}" main
     ```
3. Make focused changes, run the relevant tests, and commit with the issue reference.
   Stage only intended files.
4. Push and open a draft or ready PR against `main`:
   ```bash
   git push -u origin "squad/{issue-number}-{slug}"
   gh pr create --base main --title "{description}" --body "Closes #{issue-number}" --draft
   ```
5. After approval and green blocking CI, squash-merge:
   ```bash
   gh pr merge {pr-number} --squash --delete-branch
   ```
6. Remove a local agent worktree after merge:
   ```bash
   git worktree remove ".worktrees/{branch-slug}"
   git worktree prune
   ```

Use the real issue labels (`squad`, `squad:{member}`, `go:*`, `priority:*`, and
`release:*`) as appropriate. There is no `status:in-progress` label.

## Anti-Patterns

- ❌ Branching from or opening a PR to `dev`
- ❌ Merge-committing a PR instead of squash-merging it
- ❌ Letting a locally run agent work an issue in the shared main checkout
- ❌ Creating a local worktree for a hosted agent
- ❌ Assuming human contributors must use worktrees
