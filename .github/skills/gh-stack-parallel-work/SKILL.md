---
name: "gh-stack-parallel-work"
description: "Agentweaver override for coordinated gh stack work across dependent PR layers"
domain: "version-control"
confidence: "high"
source: "team-decision and GitHub gh-stack overview"
---

## When to use this skill

Use this skill whenever work may use a stacked PR, `gh stack`, two or more dependent
layers, or a large feature that spans auth, backend, frontend, and tests. It overrides
generic stack and worktree guidance for that work.

For independent issues with no branch-to-branch dependency, use the normal issue and
worktree workflow instead.

## Ownership boundary

**The coordinator owns stack design and all `gh stack` operations.**

The coordinator, and not individual agents:

- analyzes the feature dependency graph and chooses atomic review layers;
- decides the ordered branches and each branch's base;
- creates or assigns an isolated worktree and branch for every agent;
- runs `gh stack init` exactly once for the feature;
- runs `gh stack add` while adding each subsequent layer;
- supplies agents their assigned layer, base, lower-layer dependency, and complete stack
  context;
- runs `gh stack submit`, `gh stack sync`, and stack-level merge actions; and
- coordinates readiness, handoffs, and dependencies between layer owners.

An assigned agent works only in the coordinator-provided worktree and branch. Agents
must not independently run `gh stack init`, `gh stack add`, `gh stack submit`,
`gh stack sync`, `gh stack rebase`, merge a stack layer, or perform direct rebase/merge
operations that change stack topology. An agent reports its readiness, validation,
blockers, and dependencies to the coordinator instead.

Agents may make their assigned changes, commit them, and provide the resulting commit
SHA to the coordinator. The coordinator decides when and how those commits enter the
managed stack.

## Stack model and review behavior

A stack is a dependency chain of atomic, independently reviewable PRs:

```text
feature/frontend-and-tests  -> feature/backend-contracts
feature/backend-contracts   -> feature/auth-foundation
feature/auth-foundation     -> dev (the trunk for this feature)
```

- The bottom layer targets trunk. For ordinary Agentweaver work, trunk is `dev`.
- Every upper layer targets the branch immediately below it, never `dev` directly.
- The GitHub stack UI shows the complete chain, layer status, and review context, so a
  reviewer can see how one focused PR fits the feature.
- Protection rules and CI requirements for every layer derive from the bottom layer's
  base. A mid-stack PR does not evade the quality bar merely because its direct base is
  another feature branch.
- Selecting a layer to merge also lands every unmerged lower layer, in bottom-up order.
  A layer cannot merge while leaving an unmerged dependency below it.
- After a partial merge, GitHub automatically rebases the remaining stack so its next
  layer targets trunk and remains ready for review.

`gh stack` manages the dependency-sensitive mechanics: branch creation and bases,
cascading rebase, push, PR creation, PR linking, and stack synchronization. The
coordinator uses it as the one authority for those operations.

This prevents three common failures:

1. A one-branch “stack” gives reviewers no dependency chain or stack UI context.
2. Independently created branches often target the wrong base, producing duplicate diffs
   and broken merge order.
3. Multiple agents changing stack branches or rebasing at once creates avoidable branch,
   worktree, and force-push contention.

## Coordinator procedure

1. **Analyze dependencies.** Map shared types/schema/auth first; then backend contracts;
   then frontend consumers, integration tests, and documentation. Keep a dependency in
   its layer or a lower layer.
2. **Choose atomic review layers.** Each layer must tell one coherent review story and
   remain meaningful on its own. Split by dependency direction and review audience, not
   by arbitrary file count.
3. **Establish the stack centrally.** From an isolated coordinator worktree, initialize
   the bottom branch with `gh stack init` once. Create each ordered upper branch with
   `gh stack add`, ensuring its base is the previous layer.
4. **Assign isolated worktrees.** Create or assign one worktree and one named branch per
   layer. Do not give two agents the same worktree or branch.
5. **Distribute complete context.** Tell every agent its exact worktree path, assigned
   branch, immediate lower dependency, scope, validation command, and the prohibition on
   direct stack commands.
6. **Collect layer readiness.** Confirm commits, validation results, and remaining
   dependencies in bottom-up order. Resolve cross-layer needs before changing stack
   structure.
7. **Operate the stack centrally.** The coordinator adds remaining layers, then uses
   `gh stack submit` and `gh stack sync` to push, create/update, and link PRs. Coordinate
   review and merge at stack level; do not ask layer agents to rebase or merge.

## Agent assignment prompt

Provide this block, completed for the assigned layer:

```text
You own one assigned layer of a coordinator-managed gh stack.

WORKTREE_PATH: <absolute isolated worktree path>
ASSIGNED_STACK_BRANCH: <exact branch name>
BASE_BRANCH: <immediate lower stack branch, or dev for the bottom layer>
LOWER_LAYER_DEPENDENCY: <branch/PR and what it provides, or none>
STACK_CONTEXT: <ordered bottom-to-top branch list and this layer's scope>

Work only in WORKTREE_PATH and on ASSIGNED_STACK_BRANCH. Do not run gh stack commands,
add/submit/sync/rebase/merge the stack, change PR bases, or create competing branches.
Commit only your assigned layer. Report the commit SHA, validation performed, readiness,
and any dependency/blocker to the coordinator.
```

## Reference

This policy follows the GitHub `gh stack` overview:
<https://github.github.com/gh-stack/introduction/overview/>.
