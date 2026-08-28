---
name: "gh-stack-parallel-work"
description: "Agentweaver override for coordinated gh stack work across dependent PR layers"
domain: "version-control"
confidence: "high"
source: "team-decision and GitHub gh-stack overview/workflows"
---

## When to use this skill

Use this skill whenever work may use a stacked PR, `gh stack`, two or more dependent
layers, or a large feature that spans auth, backend, frontend, and tests. It overrides
generic stack and worktree guidance for that work. A stack has **two or more logical PR
layers**. A local one-branch tracking setup, or an ordinary one-branch PR, is not a
stacked PR.

For independent issues with no branch-to-branch dependency, use the normal issue and
worktree workflow instead.

## Ownership boundary

**The coordinator owns stack design and all `gh stack` operations.**

The coordinator, and not individual agents:

- analyzes the feature dependency graph and chooses atomic review layers;
- decides the ordered branches and each branch's base;
- centrally establishes the initial ordered stack with `gh stack init` exactly once;
- runs `gh stack add` while adding each subsequent layer;
- arranges isolated worktrees after the initial stack is established;
- supplies agents their assigned layer, base, lower-layer dependency, and complete stack
  context;
- runs `gh stack submit`, `gh stack sync`, `gh stack rebase`, `gh stack push`,
  `gh stack modify`, and `gh stack merge`; and
- coordinates readiness, handoffs, and dependencies between layer owners.

An assigned agent works only in the coordinator-provided worktree and branch. Agents
must not independently run `gh stack init`, `gh stack add`, `gh stack submit`,
`gh stack sync`, `gh stack rebase`, `gh stack push`, `gh stack modify`, or
`gh stack merge`; agents also do not perform direct rebase/merge operations that change
stack topology. An agent reports its readiness, validation, blockers, and dependencies
to the coordinator instead.

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

1. Calling a one-branch PR a “stack” gives reviewers no dependency chain or stack UI
   context.
2. Independently created branches often target the wrong base, producing duplicate diffs
   and broken merge order.
3. Multiple agents changing stack branches or rebasing at once creates avoidable branch,
   worktree, and force-push contention.

## Reconciliation, fixes, and recovery

- **Merged lower layers or upstream changes:** The coordinator uses `gh stack sync` as
  the normal reconciliation path. It fetches, reconciles remote and local composition,
  fast-forwards trunk, cascades a rebase, safely pushes, syncs PR state and links, and
  can prune merged local branches. If remote and local composition diverge, resolve that
  centrally before retrying; do not let agents improvise stack changes.
- **Fixes found above their layer:** Put the fix in the lowest layer that owns it, never
  in an upper PR merely because that branch is checked out. The coordinator coordinates
  `gh stack rebase` and then `gh stack push` for dependent layers above it.
- **Pushing rebased layers:** `gh stack push` uses safe `--force-with-lease` behavior.
  Its multi-branch update is not atomic: report every rejected branch to the coordinator
  and investigate it before retrying. Never use a raw force push for stack branches.
- **Changing composition:** Only the coordinator uses `gh stack modify` to add, remove,
  fold, rename, or reorder layers, then follows with `gh stack submit` or
  `gh stack sync` as appropriate.
- **Merging:** For an actual stack, only `gh stack merge` is used. Without a merge queue
  it atomically lands the selected layer and all lower unmerged layers. With a merge
  queue, it adds the chosen PRs together and the queue may land them in ordered groups.
  Ordinary PRs continue to use the repository's normal PR merge policy.
- **Staging:** Do not use all-files shortcuts such as `gh stack add -Am` in shared or
  agent worktrees. Preserve Agentweaver's pathspec-only `git add <files>` rule.

## Coordinator procedure

1. **Analyze dependencies.** Map shared types/schema/auth first; then backend contracts;
   then frontend consumers, integration tests, and documentation. Keep a dependency in
   its layer or a lower layer.
2. **Choose atomic review layers.** Each layer must tell one coherent review story and
   remain meaningful on its own. Split by dependency direction and review audience, not
   by arbitrary file count.
3. **Establish the stack centrally.** Before distributing layer worktrees, initialize
   the bottom branch with `gh stack init` once and create each ordered upper branch with
   `gh stack add`, ensuring its base is the previous layer. Already checked-out branches
   can prevent local adoption or initialization, so this order is required.
4. **Assign isolated worktrees.** Create or assign one worktree and one named branch per
   layer after setup. Worktree isolation remains mandatory for concurrent implementation;
   do not give two agents the same worktree or branch.
5. **Distribute complete context.** Tell every agent its exact worktree path, assigned
   branch, immediate lower dependency, scope, validation command, and the prohibition on
   direct stack commands.
6. **Collect layer readiness.** Confirm commits, validation results, and remaining
   dependencies in bottom-up order. Resolve cross-layer needs before changing stack
   structure.
7. **Operate the stack centrally.** The coordinator uses `gh stack submit` and
   `gh stack sync` to push, create/update, and link PRs; it uses `gh stack merge` for
   stack merges. Coordinate review and merge at stack level; do not ask layer agents to
   rebase or merge.

Use only commands offered by the installed `gh stack` version. This skill intentionally
does not prescribe command flags beyond the safe behavior described above.

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
change PR bases, rebase/merge the stack, or create competing branches. Use pathspec-only
`git add <files>`; never use all-files staging shortcuts. Commit only your assigned
layer. Report the commit SHA, validation performed, readiness, and any dependency/blocker
to the coordinator.
```

## Reference

This policy follows the GitHub `gh stack` overview and workflows guide:
<https://github.github.com/gh-stack/introduction/overview/>
and <https://github.github.com/gh-stack/guides/workflows/>.
