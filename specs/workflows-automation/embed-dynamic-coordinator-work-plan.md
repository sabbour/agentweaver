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
