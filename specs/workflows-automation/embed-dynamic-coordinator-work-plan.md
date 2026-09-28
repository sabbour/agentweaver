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

- [x] The grammar advertises the node as authorable and executable only with the supported
  single-stage topology; structurally invalid definitions fail validation.
- [x] One correlated pre-dispatch child run and work plan survive retries and restart.
- [x] Runtime-derived dependent subtasks execute and assemble exactly once without nested policy gates.
- [x] The typed assembled result resumes the pinned parent exactly once; failure and cancellation
  propagate with durable diagnostics.
- [x] API, MCP, events, topology, and artifacts expose parent-to-child correlation.
- [x] Acceptance coverage demonstrates a runtime-dependent DAG and typed parent continuation.

## Notable edge cases

- A pre-created but empty WorkPlan must be decomposed rather than mistaken for a finished plan.
- A crash after integration but before delivering the parent continuation must not reassemble.
- An edited workflow or generated child workflow containing composition must not recurse.

## Current implementation

The node binds to the durable child-work request port. The child-work path reserves and reattaches one
correlated run and plan, populates its dynamic DAG through normal decomposition and dependency dispatch,
and checkpoints assembly in the existing work-plan row. The assembly path stages the verified
integration tree before an idempotent fast-forward into the parent's **isolated** branch; it checks the
captured pre-composition tree, refuses dirty or diverged worktrees, and never updates the user's branch.
Recovery reattaches the parent's durable branch and reconciles a crash between the Git transfer and its
run-row tree-hash update. The parent resumes only after the installed tree matches the staged result,
and receives a typed completion carrying the correlated child and assembly identities.
