---
name: "git-concurrency-preflight"
description: "Run a quick git safety pre-flight before starting large uncommitted work in a shared checkout, when another session is concurrently active, or before destructive git operations in a repo with concurrent sessions/worktrees."
domain: "git"
confidence: "high"
source: "verified interactively 2026-07-29"
allowed-tools: Bash(git:*)
---

# Git concurrency pre-flight

Use this skill when git state may be shared across multiple active sessions or agents.
Typical triggers:

- before starting large or multi-file uncommitted work in a shared checkout
- when told another session/agent is doing significant concurrent work
- before destructive git operations such as `checkout`, `reset`, `rebase`, or
  `pull --rebase` in a repo known to use concurrent sessions or many worktrees

This is repo-agnostic guidance. Agentweaver is the motivating example because it often
has many `.worktrees/` plus parallel Squad/Copilot sessions, but the same risk pattern
applies anywhere multiple processes may touch the same clone.

## Goal

Confirm whether the current working directory is isolated and whether local branch state
still matches expectations **before** you add more uncommitted changes or run a
destructive git command.

## Run this exact pre-flight

```bash
git status --short --branch
git worktree list
git log <branch> -N --oneline
git log origin/<branch> -N --oneline
git stash list
```

Replace:

- `<branch>` with the current branch from `git status --short --branch`
- `N` with a small comparison window such as `5` or `10`

## How to interpret each result

### 1) `git status --short --branch`

Use this to confirm:

- which branch you are actually on
- whether the branch tracks a remote
- whether git reports ahead/behind/diverged state
- whether uncommitted changes are already present

Safe-ish result:

- expected branch
- no surprising ahead/behind/diverged marker
- the uncommitted diff is understood and intentionally yours

Escalate/pause if:

- the branch is not the one you expected
- git reports unexpected divergence from the tracking branch
- there is a large uncommitted diff you did not create or cannot confidently attribute

### 2) `git worktree list`

Use this to understand whether concurrent work is likely isolated in separate worktrees
or happening in the exact same checkout.

Safe-ish result:

- your current directory is a dedicated worktree for this session/task
- other active work appears to be happening in other worktree paths

Escalate/pause if:

- you are in the shared main checkout rather than an isolated worktree
- another session is likely using this same directory
- the worktree layout is unclear enough that you cannot tell whether state is shared

If the current directory is **not clearly isolated**, do not assume safety just because
other worktrees exist.

### 3) `git log <branch> -N --oneline` vs `git log origin/<branch> -N --oneline`

Compare the recent local and remote commit windows.

Safe-ish result:

- local `HEAD` matches the expected remote tip
- any local-only commits are intentional and understood

Escalate/pause if:

- local and remote tips differ unexpectedly
- the remote has moved in a way you were not expecting
- the history suggests a rewrite, force-push, or other concurrent mutation you did not
  initiate

This check complements `git status` by making recent branch history differences obvious.

### 4) `git stash list`

Use this as a sanity check that another process did not hide relevant work in the stash.

Safe-ish result:

- stash contents are empty or fully expected

Escalate/pause if:

- new or unfamiliar stash entries appear
- stash messages suggest another session stashed work from this checkout

## Decision rule

If any pre-flight result is surprising, ambiguous, or suggests the checkout is shared,
**stop and ask the user before proceeding**. Do not guess.

## What not to do while risk is present

Until the checkout is confirmed safe:

- do **not** run destructive git operations:
  - `git checkout`
  - `git reset`
  - `git rebase`
  - `git pull --rebase`
- do **not** try to “clean things up” by stashing, discarding, or moving files on your
  own
- do **not** assume another session's work is isolated unless `git worktree list`
  clearly proves it

## Preferred mitigation

If the state is understood and the user wants work to continue, reduce the exposure
window:

- prefer an isolated dedicated worktree/branch for each substantial concurrent task
- prefer committing and pushing accumulated work to a dedicated branch promptly rather
  than leaving a large uncommitted diff sitting indefinitely in a shared checkout

## Suggested one-line rationale

“I’m running a git concurrency pre-flight first because another session may be active
and I don’t want to risk destructive operations or pile more uncommitted work into a
shared checkout until branch/worktree state is confirmed safe.”
