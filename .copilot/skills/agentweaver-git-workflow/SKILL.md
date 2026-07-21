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

## Mandatory Worktree Isolation

**Every locally run background agent making code changes MUST work in its own dedicated
Git worktree.** This is unconditional: it applies to issue-linked work and ad-hoc work
alike. Never let a local agent make code changes from the shared primary checkout.

Use these naming conventions:

| Work type | Branch | Worktree |
|---|---|---|
| Issue-linked | `squad/{issue-number}-{short-kebab-slug}` | `.worktrees/{issue-number}-{short-kebab-slug}` |
| Ad-hoc fix | `fix/{short-kebab-slug}` | `.worktrees/{short-kebab-slug}` |
| Ad-hoc maintenance/docs | `chore/{short-kebab-slug}` or `docs/{short-kebab-slug}` | `.worktrees/{short-kebab-slug}` |

### Coordinator responsibility

Whoever spawns a local background agent for **any** code change MUST put explicit
worktree bootstrap commands (`git fetch`, `git worktree add`, and `cd`) in the spawn
prompt. Do not assume the agent will infer this requirement or allow it to default to
the shared primary checkout.

For an issue-linked task, the prompt must include commands equivalent to:

```bash
git fetch origin dev
git worktree add ".worktrees/{issue-number}-{slug}" \
  -b "squad/{issue-number}-{slug}" origin/dev
cd ".worktrees/{issue-number}-{slug}"
```

For an ad-hoc task, the prompt must include commands equivalent to:

```bash
git fetch origin dev
git worktree add ".worktrees/{short-kebab-slug}" \
  -b "fix/{short-kebab-slug}" origin/dev
cd ".worktrees/{short-kebab-slug}"
```

For example, two local agents that both run Git operations in the same directory can
overwrite each other's checked-out branch or staged files, mixing uncommitted changes.
Separate worktrees prevent that collision.

## Issue Work

1. Choose the workspace appropriate to the contributor:
   - **Locally run Squad/Copilot CLI agent:** use the required dedicated worktree
     created with the bootstrap commands above.
   - **Hosted agent** (such as GitHub `@copilot`): use the platform-provided isolated
     branch and environment; do not create a local worktree.
   - **Human contributor:** a worktree is optional; a normal branch in the primary
     checkout is fine:
     ```bash
     git checkout -b "squad/{issue-number}-{slug}" dev
     ```
2. Make focused changes, run the relevant tests, and commit with the issue reference.
   **Before committing, check if a changeset is needed:** if the diff touches
   `apps/`, `packages/`, `scripts/azure/`, or `k8s/` with a real user-facing
   behavior change (not just lint/refactor/test-only), run `npm run changeset`
   (see the [changelog skill](../agentweaver-changelog/SKILL.md)). The
   `Changeset advisory` CI job only emits a warning — it never blocks merge —
   so this is easy to silently skip; don't rely on CI to catch it.
3. Push and open a draft or ready PR against `dev`:
   ```bash
   git push -u origin "squad/{issue-number}-{slug}"
   gh pr create --base dev --title "{description}" --body "Closes #{issue-number}" --draft
   ```
4. **Before merging any PR, always verify its live state directly — never assume from the diff alone:**
   ```bash
   gh pr view {pr-number} --json mergeable,mergeStateStatus,statusCheckRollup \
     --jq '{mergeable, mergeState: .mergeStateStatus, checks: [.statusCheckRollup[] | {name, status, conclusion}]}'
   ```
   Confirm `mergeable` is `MERGEABLE` (no conflicts) and every required check
   (`.NET tests`, `Node toolchain tests`, `Web tests`, `Docs build`) shows
   `conclusion: SUCCESS`. Re-run any failed required check once
   (`gh run rerun {run-id} --failed`) to rule out known CPU-contention flakes
   before treating a real failure as caused by the PR's own changes.
5. After confirming the above, squash-merge.
6. Remove a local agent worktree after merge.

## Ad-hoc Work

Use the same mandatory local-worktree process for work that has no GitHub issue. Select
the appropriate `fix/`, `chore/`, or `docs/` branch prefix, use the matching ad-hoc
worktree naming convention above, run relevant tests, and open a PR against `dev`.

## Anti-Patterns

- ❌ Branching from or opening a normal PR to `main`
- ❌ Merge-committing a PR instead of squash-merging it
- ❌ Letting a locally run background agent make any code change in the shared primary checkout
- ❌ Spawning a local code-changing agent without explicit `fetch`, `worktree add`, and `cd` commands
- ❌ Creating a local worktree for a hosted agent
- ❌ Merging or trusting a PR without checking its live `mergeable`/`mergeStateStatus` and each required check's actual `conclusion`
- ❌ Opening a PR with a real user-facing fix/feature under `apps/`, `packages/`,
  `scripts/azure/`, or `k8s/` without adding a changeset, and assuming the
  non-blocking `Changeset advisory` job would have caught it if one were needed
