# Worktree Reference

## Team-root awareness

Squad agents may run inside a git worktree. Resolve `.squad/` paths from the
`TEAM_ROOT` supplied by the Coordinator, never from an assumed current directory.
The Coordinator resolves that root through `.squad/config.json` overrides, then the
current worktree root, then the primary checkout reported by `git worktree list --porcelain`.

## Worktree policy

[`CONTRIBUTING.md`](../../CONTRIBUTING.md#ai-agent-contributions) is the canonical
contribution policy. This reference supplies the mechanics; do not create a competing
policy in spawn prompts or skills. Its
[Branch Topology Activation Plan](../../CONTRIBUTING.md#branch-topology)
governs whether a branch tier may be added.

| Contributor | Workspace rule |
|-------------|----------------|
| Locally run Squad agent (including Copilot CLI) | **Required:** one dedicated local git worktree per issue. |
| Hosted agent (for example, GitHub `@copilot`) | Use the platform-created isolated branch/environment; no local worktree applies. |
| Human contributor | Worktrees are optional; a normal branch in the primary checkout is supported. |

## Locally run agent lifecycle

Use one worktree per issue. Agents collaborating on that issue reuse it; locally run
agents must not work issue changes in the shared primary checkout.

1. Fetch current `origin/dev` and check for an existing matching worktree:
   ```bash
   git fetch origin dev
   git worktree list
   ```
2. If none exists, create it under `.worktrees/` using the issue branch:
   ```bash
   git worktree add ".worktrees/{branch-slug}" \
     -b "squad/{issue-number}-{kebab-case-slug}" origin/dev
   ```
3. Make the worktree ready with `npm run deps:ensure`. This keeps a physical
   `node_modules` in the current worktree and shares only npm's fingerprinted download
   cache when safe. Never point, junction, hardlink, or clone a worktree's dependency
   tree from another checkout.
4. Pass `WORKTREE_PATH` and `TEAM_ROOT` in the spawn context.
5. A worktree isolates files and indexes; it does **not** provide integration
   safety. Before merge, the feature branch must be updated to current
   `origin/dev` and required CI must rerun successfully.
6. After the PR squash-merges, remove the worktree:
   ```bash
   git worktree remove ".worktrees/{branch-slug}"
   git worktree prune
   ```

## Hosted and human work

For a hosted agent, pass the issue, branch, and PR target (`dev`) to the hosting
platform but do not create or clean up a local worktree. A human may use the same
worktree commands for convenience, or simply create a short-lived branch in the primary
checkout.

Protected-branch lifecycle: `needsReview` → `readyToMerge` → `needsUpdate`
(when `dev` moved) → `ciFailure`/`readyToMerge` after the rerun → `done`.
Sync from current `origin/dev`, resolve any conflict or failure, test, push,
and squash-merge only when GitHub reports the branch current and green. Never retarget to an unreviewed local integration branch.

## State considerations

Use the Coordinator-supplied `TEAM_ROOT` for shared Squad state. If a worktree has
worktree-local state, it remains branch-local and merges through normal PR flow. Avoid
concurrent writes to shared primary-checkout state.
