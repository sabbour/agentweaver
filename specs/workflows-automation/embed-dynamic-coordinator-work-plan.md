# Embed a dynamic coordinator work plan in a workflow

**Issue:** [#1544](https://github.com/sabbour/agentweaver/issues/1544)

**Area:** Workflows & automation

## User story

As a workflow author, I want one stage to derive and execute a dependent work plan at runtime,
then return its assembled output to my workflow without opening a second review or merge gate.

## Context / problem

Static workflow edges and `fan_out` branches require their node count and dependencies to be
known before execution. The coordinator can decompose a goal into a dynamic DAG, but its
current entry point creates its own work plan and its assembly path includes coordinator-owned
review, merge, and Scribe. Neither can safely be used as an embedded workflow stage yet.

## Scope

### In

- At most one non-nested `coordinator_composed` stage with a non-empty prompt and exactly one
  unconditional continuation.
- Persist one child coordinator run and work plan per parent run and node before decomposition.
- Reuse coordinator subtasks, dependencies, dispatch, deterministic integration assembly,
  pending-delivery fencing, cancellation, and topology/artifact stores.
- Return child run and work-plan identity, integration branch, tree hash, aggregate diff, and
  included child runs to the parent; parent workflow owns review, merge, and Scribe.
- Fail closed on selected/generated recursive child workflows.

### Out

- Multiple or nested composed stages, nested outcome-spec confirmation, a parallel task store,
  coordinator-owned review/merge/Scribe inside the embedded stage.

## Acceptance criteria

- [x] The grammar recognizes the node but does not advertise it as authorable or executable
  before the durable runtime is implemented; structurally invalid definitions fail validation.
- [ ] One correlated pre-dispatch child run and work plan survive retries and restart.
- [ ] Runtime-derived dependent subtasks execute and assemble exactly once without nested policy gates.
- [ ] The typed assembled result resumes the pinned parent exactly once; failure and cancellation
  propagate with durable diagnostics.
- [ ] API, MCP, events, topology, and artifacts expose parent-to-child correlation.
- [ ] An acceptance workflow demonstrates a runtime-dependent DAG.

## Notable edge cases

- A pre-created but empty WorkPlan must be decomposed rather than mistaken for a finished plan.
- A crash after integration but before delivering the parent continuation must not reassemble.
- An edited workflow or generated child workflow containing composition must not recurse.

## Current implementation boundary

The reserved node still fails binding. The internal child-work path reserves and reattaches one
correlated run/plan, populates an empty composed plan through the normal decomposition path,
uses the existing dependency dispatcher, and can persist an assembly-only result in the same
work-plan row before using the established parent-delivery receipt. These contracts have
focused in-process tests; they are not an executable workflow or a completed acceptance DAG.

**Decision needed before enabling the node:** collective assembly builds a *separate* integration
branch, while a pinned parent workflow continues in its own run worktree and its existing review
and merge executors read that worktree branch. Returning only a branch name, diff, or task text
does not make the assembled files visible to those executors. The ordinary coordinator merge
would update the user's branch (prohibited for the nested coordinator). Choose a crash-recoverable,
idempotent transfer of the verified assembled tree into the **parent's isolated worktree branch**
before parent resume, guarded by the parent's captured pre-composition tree hash; alternatively
define and test a new parent-worktree identity handoff across every downstream executor and
cleanup path. Until one of these contracts is implemented, the binder must continue to reject
`coordinator_composed`, and issue #1544 remains open.
