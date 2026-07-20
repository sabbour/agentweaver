# ADR 0001: Isolate concurrent agent work with Git worktrees

- **Status:** Accepted

## Context

Agentweaver commonly has multiple agents working concurrently. Sharing a single checkout
would let independent agents overwrite files, contend for the Git index, and accidentally
mix unrelated changes.

## Decision

Each agent issue uses a dedicated Git worktree under `.worktrees/`, checked out on its own
short-lived branch. The main checkout remains separate; agents working the same issue reuse
that issue's worktree.

## Consequences

Concurrent issue work has filesystem and index isolation while retaining one local Git
object database. Worktree creation, dependency-linking, reuse, and cleanup are required
parts of the agent lifecycle. Human contributors may still use a normal feature branch in
the main checkout.
