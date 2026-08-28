---
name: "gh-stack-parallel-work"
description: "Agentweaver coordinator policy for parallel implementation in gh-stack PRs"
domain: "version-control"
confidence: "high"
source: "team-decision"
---

## Scope

Use this overlay whenever work may use `gh stack`, dependent PR layers, or a large
feature spanning auth, backend, frontend, and tests. It overrides generic agent
ownership guidance, not the official `gh stack` operational guidance.

An actual stacked PR has **two or more logical PR layers**. A local one-branch tracking
setup or an ordinary one-branch PR is not a stack. Use the normal Agentweaver PR workflow
for ordinary PRs.

## Authoritative operational reference

Before deciding whether work belongs in a stack, or choosing its layers' count, order,
or content, coordinators must read [the official stack-design reference](../../../.agents/skills/gh-stack/references/stack-design.md).
Read [the official gh-stack skill](../../../.agents/skills/gh-stack/SKILL.md) before
running any stack command. It is authoritative for command syntax and flags, command
behavior and side effects, errors and recovery, non-interactive forms, remote selection,
and stack-design references.

In particular, coordinators must use the official safe non-interactive forms: JSON view
output, automatic PR submission, and explicit branch names for initialization and added
layers. The official skill and command-specific `gh stack <command> --help` define
supported flags and remote selection. Its multi-remote `--remote <name>` rule applies
only to supported `push`, `submit`, `sync`, `rebase`, and `link` operations; `init`
does not accept `--remote`. Do not invent flags or rely on interactive prompts.

## Coordinator-owned stack lifecycle

**Only the coordinator owns feature stack topology and lifecycle.** The coordinator:

- analyzes dependencies and chooses atomic, ordered review layers;
- centrally initializes the stack once, with exact ordered branch names, and adds later
  layers centrally;
- gives every agent an isolated worktree, assigned branch, lower-layer dependency, and
  full stack context;
- serializes stack commands. A stack state can be locked, so agents and coordinators do
  not run competing operations against the same stack;
- submits, synchronizes, rebases, pushes, and links the stack; and
- coordinates review, merge readiness, and recovery.

Arrange the initial stack before distributing per-layer worktrees. Branches checked out
in other worktrees can prevent local stack adoption or initialization. Worktree isolation
remains mandatory for concurrent implementation after that initial setup.

When upstream or a lower layer changes, the coordinator follows the official sync and
rebase workflow. A fix belongs in the lowest layer that owns it, never in an upper PR for
convenience. The coordinator uses `gh stack push`, not a raw push, after stack rebases.

`gh stack modify` is TUI-only; it is not a non-interactive recovery path. When
restructuring is necessary in an automated or agent workflow, the coordinator follows the
official recovery guidance, including coordinator-directed unstack/re-initialize when
appropriate, then re-submits or syncs.

## Agent boundaries

Assigned agents work only in their coordinator-provided worktree and branch. They do not
initialize, add, submit, sync, rebase, push, modify, or merge a stack; change a stack PR
base; or create competing stack branches.

Agents use pathspec-scoped `git add <files>` only. They must not use all-files staging
shortcuts, including `gh stack add -Am`, in shared or agent worktrees. They commit only
their assigned layer and report the commit SHA, validation, readiness, dependencies, and
blockers to the coordinator.

## Merge policy

For a real stack, only the coordinator uses `gh stack merge`; never use raw `gh pr merge`
for it. Ordinary one-branch PRs use the repository's normal PR merge policy.

## Agent assignment prompt

```text
You own one assigned layer of a coordinator-managed gh stack.

WORKTREE_PATH: <absolute isolated worktree path>
ASSIGNED_STACK_BRANCH: <exact branch name>
BASE_BRANCH: <immediate lower stack branch, or dev for the bottom layer>
LOWER_LAYER_DEPENDENCY: <branch/PR and what it provides, or none>
STACK_CONTEXT: <ordered bottom-to-top branch list and this layer's scope>

Work only in WORKTREE_PATH and on ASSIGNED_STACK_BRANCH. Do not run gh stack commands,
change PR bases, rebase/merge/push the stack, or create competing branches. Use
pathspec-only `git add <files>`. Commit only your layer and report commit SHA, validation,
readiness, and any dependency/blocker to the coordinator.
```
